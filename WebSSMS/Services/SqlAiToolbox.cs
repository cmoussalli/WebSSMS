using Microsoft.Data.SqlClient;
using System.Data;
using System.Text;
using System.Text.Json;
using WebSSMS.Models;

namespace WebSSMS.Services;

/// <summary>
/// The only way the AI assistant is allowed to touch SQL Server: a fixed set of
/// read-only lookups over structure and data. The model calls them by name
/// (OpenAI function calling) to ground itself before it writes a script -- it can
/// see which tables and columns really exist, and sample rows to learn what the
/// data looks like -- but it can never write.
///
/// Three things keep it read-only:
///   * only these six functions exist, and none of them takes a free-form batch;
///   * ad-hoc queries go through <see cref="ReadOnlySqlGuard"/> first;
///   * they run inside a transaction that is always rolled back.
///
/// It works on its own connection rather than the circuit's, so the assistant
/// hopping between databases never disturbs the database the user's query tab is
/// pointed at, and a lookup cannot collide with a query the user is running.
/// </summary>
public sealed class SqlAiToolbox : IAsyncDisposable
{
    public const string ListDatabasesTool = "list_databases";
    public const string ListTablesTool = "list_tables";
    public const string DescribeTableTool = "describe_table";
    public const string ListRoutinesTool = "list_routines";
    public const string GetObjectDefinitionTool = "get_object_definition";
    public const string RunSelectTool = "run_select";

    private const int MaxListedObjects = 400;
    private const int MaxDefinitionChars = 8000;
    private const int MaxCellChars = 200;
    private const int LookupTimeoutSeconds = 30;

    /// <summary>Renders a column's type the way it would be written in DDL.</summary>
    private const string TypeExpression = @"
        ty.name + CASE
            WHEN ty.name IN ('varchar','char','varbinary','binary')
                THEN '(' + IIF(c.max_length = -1, 'max', CAST(c.max_length AS varchar(11))) + ')'
            WHEN ty.name IN ('nvarchar','nchar')
                THEN '(' + IIF(c.max_length = -1, 'max', CAST(c.max_length / 2 AS varchar(11))) + ')'
            WHEN ty.name IN ('decimal','numeric')
                THEN '(' + CAST(c.precision AS varchar(11)) + ',' + CAST(c.scale AS varchar(11)) + ')'
            WHEN ty.name IN ('datetime2','time','datetimeoffset')
                THEN '(' + CAST(c.scale AS varchar(11)) + ')'
            ELSE '' END";

    private const string PrimaryKeyJoin = @"
        LEFT JOIN (
            SELECT ic.object_id, ic.column_id
            FROM sys.index_columns ic
            INNER JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            WHERE i.is_primary_key = 1
        ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id";

    private readonly ConnectionManager _connectionManager;

    private SqlConnection? _connection;
    private string? _connectionId;

    public SqlAiToolbox(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    // ----------------------------------------------------------------- tool specs

    /// <summary>The function definitions handed to the model.</summary>
    public IReadOnlyList<LlmTool> BuildTools(AiSettings settings)
    {
        var tools = new List<LlmTool>
        {
            new()
            {
                Name = ListDatabasesTool,
                Description = "List the databases on the connected server.",
                ParametersJson = """{"type":"object","properties":{}}"""
            },
            new()
            {
                Name = ListTablesTool,
                Description = "List tables and views, with their row counts. Use this to find out what actually exists before writing a script.",
                ParametersJson = """
                {
                  "type": "object",
                  "properties": {
                    "database": { "type": "string", "description": "Defaults to the database of the current query tab." },
                    "schema": { "type": "string", "description": "Optional schema filter, e.g. dbo." },
                    "name_like": { "type": "string", "description": "Optional substring filter on the object name." }
                  }
                }
                """
            },
            new()
            {
                Name = DescribeTableTool,
                Description = "Get the full structure of one table or view: columns with data types, nullability, identity, defaults, computed definitions, plus primary key, foreign keys, indexes and row count.",
                ParametersJson = """
                {
                  "type": "object",
                  "properties": {
                    "database": { "type": "string", "description": "Defaults to the database of the current query tab." },
                    "schema": { "type": "string", "description": "Optional; resolved automatically when the name is unique." },
                    "table": { "type": "string", "description": "Table or view name." }
                  },
                  "required": ["table"]
                }
                """
            },
            new()
            {
                Name = ListRoutinesTool,
                Description = "List stored procedures, functions and triggers.",
                ParametersJson = """
                {
                  "type": "object",
                  "properties": {
                    "database": { "type": "string", "description": "Defaults to the database of the current query tab." },
                    "name_like": { "type": "string", "description": "Optional substring filter on the name." }
                  }
                }
                """
            },
            new()
            {
                Name = GetObjectDefinitionTool,
                Description = "Get the T-SQL definition of a view, stored procedure, function or trigger.",
                ParametersJson = """
                {
                  "type": "object",
                  "properties": {
                    "database": { "type": "string", "description": "Defaults to the database of the current query tab." },
                    "schema": { "type": "string" },
                    "name": { "type": "string", "description": "Object name." }
                  },
                  "required": ["name"]
                }
                """
            }
        };

        if (settings.AllowDataQueries)
        {
            tools.Add(new LlmTool
            {
                Name = RunSelectTool,
                Description =
                    $"Run one read-only SELECT to look at the data -- sample rows, check distinct values, count matches. " +
                    $"A single statement only; anything that writes is rejected. At most {settings.MaxRows} rows come back.",
                ParametersJson = """
                {
                  "type": "object",
                  "properties": {
                    "database": { "type": "string", "description": "Defaults to the database of the current query tab." },
                    "sql": { "type": "string", "description": "One SELECT statement. Use TOP to keep it small." },
                    "purpose": { "type": "string", "description": "One short line on why you need this, shown to the user." }
                  },
                  "required": ["sql"]
                }
                """
            });
        }

        return tools;
    }

    // ------------------------------------------------------------------ dispatch

    /// <summary>
    /// Runs one call from the model. Tool-level failures come back as text for the
    /// model to read and retry -- only a lost connection is thrown.
    /// </summary>
    public async Task<AiToolOutcome> InvokeAsync(
        LlmToolCall call, AiSettings settings, string? defaultDatabase, CancellationToken cancellationToken)
    {
        var trace = new AiToolTrace { Tool = call.Name };

        JsonDocument arguments;
        try
        {
            arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
        }
        catch (JsonException ex)
        {
            trace.Failed = true;
            trace.Result = "unreadable arguments";
            return new AiToolOutcome($"ERROR: the arguments were not valid JSON ({ex.Message}). Send them again as a JSON object.", trace);
        }

        using (arguments)
        {
            return await InvokeAsync(call, arguments.RootElement, settings, defaultDatabase, trace, cancellationToken);
        }
    }

    private async Task<AiToolOutcome> InvokeAsync(
        LlmToolCall call, JsonElement args, AiSettings settings, string? defaultDatabase,
        AiToolTrace trace, CancellationToken cancellationToken)
    {
        try
        {
            var connection = await GetConnectionAsync(cancellationToken);
            var database = await ResolveDatabaseAsync(
                connection, Arg(args, "database") ?? defaultDatabase, cancellationToken);

            return call.Name switch
            {
                ListDatabasesTool => await ListDatabasesAsync(connection, trace, cancellationToken),
                ListTablesTool => await ListTablesAsync(connection, database, args, trace, cancellationToken),
                DescribeTableTool => await DescribeTableAsync(connection, database, args, trace, cancellationToken),
                ListRoutinesTool => await ListRoutinesAsync(connection, database, args, trace, cancellationToken),
                GetObjectDefinitionTool => await GetObjectDefinitionAsync(connection, database, args, trace, cancellationToken),
                RunSelectTool => await RunSelectAsync(connection, database, args, settings, trace, cancellationToken),
                _ => Failure(trace, $"There is no tool called '{call.Name}'.")
            };
        }
        catch (AiToolException ex)
        {
            return Failure(trace, ex.Message);
        }
        catch (SqlException ex)
        {
            return Failure(trace, $"SQL Server returned: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure(trace, ex.Message);
        }
    }

    private static AiToolOutcome Failure(AiToolTrace trace, string message)
    {
        trace.Failed = true;
        trace.Result = Condense(message);
        return new AiToolOutcome($"ERROR: {message}", trace);
    }

    // --------------------------------------------------------------------- tools

    private static async Task<AiToolOutcome> ListDatabasesAsync(
        SqlConnection connection, AiToolTrace trace, CancellationToken cancellationToken)
    {
        var databases = new List<string>();

        using var command = CreateCommand(connection,
            "SELECT name FROM sys.databases WHERE state = 0 ORDER BY name");

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            databases.Add(reader.GetString(0));

        trace.Target = "server";
        trace.Result = $"{databases.Count} databases";
        return new AiToolOutcome(Json(new { databases }), trace);
    }

    private static async Task<AiToolOutcome> ListTablesAsync(
        SqlConnection connection, string database, JsonElement args, AiToolTrace trace, CancellationToken cancellationToken)
    {
        var schema = Arg(args, "schema");
        var nameLike = Arg(args, "name_like");

        const string sql = @"
            SELECT TOP (@top) s.name AS SchemaName, o.name AS ObjectName, o.type_desc AS TypeDesc,
                   ISNULL(p.Rows, 0) AS ApproximateRows
            FROM sys.objects o
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            LEFT JOIN (
                SELECT object_id, SUM(rows) AS Rows
                FROM sys.partitions
                WHERE index_id IN (0, 1)
                GROUP BY object_id
            ) p ON p.object_id = o.object_id
            WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0
              AND (@schema IS NULL OR s.name = @schema)
              AND (@nameLike IS NULL OR o.name LIKE '%' + @nameLike + '%')
            ORDER BY s.name, o.name";

        using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@top", MaxListedObjects + 1);
        command.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
        command.Parameters.AddWithValue("@nameLike", (object?)nameLike ?? DBNull.Value);

        var objects = new List<object>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                objects.Add(new
                {
                    schema = reader.GetString(0),
                    name = reader.GetString(1),
                    type = reader.GetString(2) == "VIEW" ? "VIEW" : "TABLE",
                    approximate_rows = reader.GetInt64(3)
                });
            }
        }

        var truncated = objects.Count > MaxListedObjects;
        if (truncated) objects.RemoveRange(MaxListedObjects, objects.Count - MaxListedObjects);

        trace.Target = database + (schema == null ? "" : $".{schema}") + (nameLike == null ? "" : $" ~ '{nameLike}'");
        trace.Result = $"{objects.Count} tables/views";

        return new AiToolOutcome(
            Json(new { database, objects, truncated, note = truncated ? "Too many objects; narrow the search with schema or name_like." : null }),
            trace);
    }

    private static async Task<AiToolOutcome> DescribeTableAsync(
        SqlConnection connection, string database, JsonElement args, AiToolTrace trace, CancellationToken cancellationToken)
    {
        var (schema, table) = SplitObjectName(Arg(args, "schema"), Arg(args, "table"));
        if (table == null) throw new AiToolException("describe_table needs a 'table' argument.");

        var resolved = await ResolveObjectAsync(connection, schema, table, new[] { "U", "V" }, cancellationToken);
        trace.Target = $"{database}.{resolved.Schema}.{resolved.Name}";

        var columns = new List<object>();
        var primaryKey = new List<string>();

        var columnSql = $@"
            SELECT c.name, {TypeExpression} AS TypeName, c.is_nullable, c.is_identity,
                   dc.definition AS DefaultDefinition, cc.definition AS ComputedDefinition,
                   CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS IsPrimaryKey
            FROM sys.columns c
            INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            {PrimaryKeyJoin}
            WHERE c.object_id = @objectId
            ORDER BY c.column_id";

        using (var command = CreateCommand(connection, columnSql))
        {
            command.Parameters.AddWithValue("@objectId", resolved.ObjectId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                var isPrimaryKey = reader.GetInt32(6) == 1;
                if (isPrimaryKey) primaryKey.Add(name);

                columns.Add(new
                {
                    name,
                    type = reader.GetString(1),
                    nullable = reader.GetBoolean(2),
                    identity = reader.GetBoolean(3),
                    @default = reader.IsDBNull(4) ? null : reader.GetString(4),
                    computed = reader.IsDBNull(5) ? null : reader.GetString(5),
                    primary_key = isPrimaryKey
                });
            }
        }

        if (columns.Count == 0)
            throw new AiToolException($"{resolved.Schema}.{resolved.Name} has no columns visible to this login.");

        // Both directions: what this table points at, and what points back at it.
        var foreignKeys = new List<object>();
        const string foreignKeySql = @"
            SELECT fk.name,
                   OBJECT_SCHEMA_NAME(fk.parent_object_id) + '.' + OBJECT_NAME(fk.parent_object_id) AS ParentTable,
                   pc.name AS ParentColumn,
                   OBJECT_SCHEMA_NAME(fk.referenced_object_id) + '.' + OBJECT_NAME(fk.referenced_object_id) AS ReferencedTable,
                   rc.name AS ReferencedColumn,
                   fk.delete_referential_action_desc, fk.update_referential_action_desc
            FROM sys.foreign_keys fk
            INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            INNER JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fk.parent_object_id = @objectId OR fk.referenced_object_id = @objectId
            ORDER BY fk.name, fkc.constraint_column_id";

        using (var command = CreateCommand(connection, foreignKeySql))
        {
            command.Parameters.AddWithValue("@objectId", resolved.ObjectId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                foreignKeys.Add(new
                {
                    name = reader.GetString(0),
                    from = $"{reader.GetString(1)}.{reader.GetString(2)}",
                    to = $"{reader.GetString(3)}.{reader.GetString(4)}",
                    on_delete = reader.GetString(5),
                    on_update = reader.GetString(6)
                });
            }
        }

        var indexes = new List<object>();
        const string indexSql = @"
            SELECT i.name, i.type_desc, i.is_unique, i.is_primary_key,
                   STUFF((
                       SELECT ', ' + c2.name + CASE WHEN ic2.is_descending_key = 1 THEN ' DESC' ELSE '' END
                       FROM sys.index_columns ic2
                       INNER JOIN sys.columns c2 ON c2.object_id = ic2.object_id AND c2.column_id = ic2.column_id
                       WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 0
                       ORDER BY ic2.key_ordinal
                       FOR XML PATH('')), 1, 2, '') AS KeyColumns
            FROM sys.indexes i
            WHERE i.object_id = @objectId AND i.name IS NOT NULL
            ORDER BY i.is_primary_key DESC, i.name";

        using (var command = CreateCommand(connection, indexSql))
        {
            command.Parameters.AddWithValue("@objectId", resolved.ObjectId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                indexes.Add(new
                {
                    name = reader.GetString(0),
                    type = reader.GetString(1),
                    unique = reader.GetBoolean(2),
                    primary_key = reader.GetBoolean(3),
                    columns = reader.IsDBNull(4) ? "" : reader.GetString(4)
                });
            }
        }

        long approximateRows = 0;
        using (var command = CreateCommand(connection,
            "SELECT ISNULL(SUM(rows), 0) FROM sys.partitions WHERE object_id = @objectId AND index_id IN (0, 1)"))
        {
            command.Parameters.AddWithValue("@objectId", resolved.ObjectId);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            approximateRows = value is long rows ? rows : Convert.ToInt64(value ?? 0L);
        }

        trace.Result = $"{columns.Count} columns, {foreignKeys.Count} FK refs";

        return new AiToolOutcome(
            Json(new
            {
                database,
                schema = resolved.Schema,
                name = resolved.Name,
                type = resolved.TypeDescription,
                approximate_rows = approximateRows,
                columns,
                primary_key = primaryKey,
                foreign_keys = foreignKeys,
                indexes
            }),
            trace);
    }

    private static async Task<AiToolOutcome> ListRoutinesAsync(
        SqlConnection connection, string database, JsonElement args, AiToolTrace trace, CancellationToken cancellationToken)
    {
        var nameLike = Arg(args, "name_like");

        const string sql = @"
            SELECT TOP (@top) s.name AS SchemaName, o.name AS ObjectName, o.type_desc AS TypeDesc
            FROM sys.objects o
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.type IN ('P', 'FN', 'IF', 'TF', 'TR') AND o.is_ms_shipped = 0
              AND (@nameLike IS NULL OR o.name LIKE '%' + @nameLike + '%')
            ORDER BY s.name, o.name";

        using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@top", MaxListedObjects);
        command.Parameters.AddWithValue("@nameLike", (object?)nameLike ?? DBNull.Value);

        var routines = new List<object>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            routines.Add(new
            {
                schema = reader.GetString(0),
                name = reader.GetString(1),
                type = reader.GetString(2)
            });
        }

        trace.Target = database + (nameLike == null ? "" : $" ~ '{nameLike}'");
        trace.Result = $"{routines.Count} routines";
        return new AiToolOutcome(Json(new { database, routines }), trace);
    }

    private static async Task<AiToolOutcome> GetObjectDefinitionAsync(
        SqlConnection connection, string database, JsonElement args, AiToolTrace trace, CancellationToken cancellationToken)
    {
        var (schema, name) = SplitObjectName(Arg(args, "schema"), Arg(args, "name"));
        if (name == null) throw new AiToolException("get_object_definition needs a 'name' argument.");

        var resolved = await ResolveObjectAsync(
            connection, schema, name, new[] { "P", "FN", "IF", "TF", "TR", "V" }, cancellationToken);

        trace.Target = $"{database}.{resolved.Schema}.{resolved.Name}";

        using var command = CreateCommand(connection, "SELECT OBJECT_DEFINITION(@objectId)");
        command.Parameters.AddWithValue("@objectId", resolved.ObjectId);
        var definition = await command.ExecuteScalarAsync(cancellationToken) as string;

        if (string.IsNullOrEmpty(definition))
        {
            trace.Result = "no definition (encrypted?)";
            return new AiToolOutcome(
                Json(new { database, schema = resolved.Schema, name = resolved.Name, definition = (string?)null, note = "No definition is available -- the object may be encrypted." }),
                trace);
        }

        var truncated = definition.Length > MaxDefinitionChars;
        if (truncated) definition = definition[..MaxDefinitionChars];

        trace.Result = $"{definition.Length} chars";
        return new AiToolOutcome(
            Json(new { database, schema = resolved.Schema, name = resolved.Name, type = resolved.TypeDescription, definition, truncated }),
            trace);
    }

    private static async Task<AiToolOutcome> RunSelectAsync(
        SqlConnection connection, string database, JsonElement args, AiSettings settings,
        AiToolTrace trace, CancellationToken cancellationToken)
    {
        var sql = Arg(args, "sql");
        var purpose = Arg(args, "purpose");

        trace.Target = purpose ?? Condense(sql ?? "");

        if (!settings.AllowDataQueries)
            throw new AiToolException("Reading data is switched off in the AI settings; work from the structure instead.");

        var rejection = ReadOnlySqlGuard.Validate(sql);
        if (rejection != null) throw new AiToolException(rejection);

        var maxRows = Math.Clamp(settings.MaxRows, 1, 200);

        // Belt and braces over the guard above: whatever runs, nothing it did survives.
        using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            using var command = CreateCommand(connection, sql!);
            command.Transaction = transaction;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var columns = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                columns.Add(string.IsNullOrEmpty(name) ? $"column{i + 1}" : name);
            }

            var rows = new List<string?[]>();
            var truncated = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count >= maxRows) { truncated = true; break; }

                var row = new string?[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i) ? null : FormatCell(reader.GetValue(i));

                rows.Add(row);
            }

            trace.Result = $"{rows.Count}{(truncated ? "+" : "")} rows";

            return new AiToolOutcome(
                Json(new
                {
                    database,
                    columns,
                    rows,
                    row_count = rows.Count,
                    truncated,
                    note = truncated ? $"Only the first {maxRows} rows are shown." : null
                }),
                trace);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    // ---------------------------------------------------------- schema digest

    /// <summary>
    /// A compact table-and-column listing, for models that cannot call functions.
    /// They get one shot at the schema up front instead of looking things up.
    /// </summary>
    public async Task<string> BuildSchemaDigestAsync(
        string? database, int maxTables, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        var resolved = await ResolveDatabaseAsync(connection, database, cancellationToken);

        var sql = $@"
            SELECT s.name AS SchemaName, t.name AS TableName, c.name AS ColumnName,
                   {TypeExpression} AS TypeName, c.is_nullable, c.is_identity,
                   CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS IsPrimaryKey
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.columns c ON c.object_id = t.object_id
            INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            {PrimaryKeyJoin}
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id";

        using var command = CreateCommand(connection, sql);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var digest = new StringBuilder();
        var tables = 0;
        var currentTable = string.Empty;
        var truncated = false;

        while (await reader.ReadAsync(cancellationToken))
        {
            var table = $"{reader.GetString(0)}.{reader.GetString(1)}";
            if (table != currentTable)
            {
                if (tables >= maxTables) { truncated = true; break; }
                if (currentTable.Length > 0) digest.AppendLine(")");
                digest.Append(table).Append('(');
                currentTable = table;
                tables++;
            }
            else
            {
                digest.Append(", ");
            }

            digest.Append(reader.GetString(2)).Append(' ').Append(reader.GetString(3));
            if (reader.GetBoolean(5)) digest.Append(" IDENTITY");
            if (reader.GetInt32(6) == 1) digest.Append(" PK");
            if (!reader.GetBoolean(4)) digest.Append(" NOT NULL");
        }

        if (currentTable.Length > 0) digest.AppendLine(")");

        if (digest.Length == 0)
            return $"Database [{resolved}] has no user tables.";

        var header = $"Tables in [{resolved}] (column type, then IDENTITY / PK / NOT NULL where they apply):\n";
        var footer = truncated
            ? $"\n(Only the first {maxTables} tables are listed. Ask the user for the exact object names if what you need is missing.)"
            : string.Empty;

        return header + digest + footer;
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>
    /// The assistant's own connection. Cloned from the circuit's credentials with a
    /// distinct application name, so it shows up separately in Activity Monitor.
    /// </summary>
    private async Task<SqlConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        var info = _connectionManager.ActiveConnectionInfo
            ?? throw new AiToolException("Not connected to a SQL Server instance. Connect first, then ask again.");

        if (_connection != null && _connectionId == info.Id && _connection.State == ConnectionState.Open)
            return _connection;

        await DisposeConnectionAsync();

        var builder = new SqlConnectionStringBuilder(info.ConnectionString)
        {
            ApplicationName = "WebSSMS AI"
        };

        var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (SqlException ex)
        {
            await connection.DisposeAsync();
            throw new AiToolException($"Could not open a connection for the assistant: {ex.Message}");
        }

        _connection = connection;
        _connectionId = info.Id;
        return connection;
    }

    /// <summary>
    /// Switches the assistant's connection to <paramref name="database"/> and returns
    /// its real name. Checking it against sys.databases is what makes it safe to put
    /// a model-supplied name in a USE.
    /// </summary>
    private static async Task<string> ResolveDatabaseAsync(
        SqlConnection connection, string? database, CancellationToken cancellationToken)
    {
        var requested = database?.Trim().Trim('[', ']');

        if (string.IsNullOrEmpty(requested))
            return connection.Database;

        if (string.Equals(requested, connection.Database, StringComparison.OrdinalIgnoreCase))
            return connection.Database;

        using var command = CreateCommand(connection, "SELECT name FROM sys.databases WHERE name = @name AND state = 0");
        command.Parameters.AddWithValue("@name", requested);

        if (await command.ExecuteScalarAsync(cancellationToken) is not string actual)
            throw new AiToolException($"There is no online database called '{requested}' on this server. Call {ListDatabasesTool} to see what there is.");

        await connection.ChangeDatabaseAsync(actual, cancellationToken);
        return actual;
    }

    private static async Task<ResolvedObject> ResolveObjectAsync(
        SqlConnection connection, string? schema, string name, string[] types, CancellationToken cancellationToken)
    {
        var typeList = string.Join(",", types.Select(t => $"'{t}'"));

        var sql = $@"
            SELECT o.object_id, s.name, o.name, o.type_desc
            FROM sys.objects o
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.name = @name AND o.type IN ({typeList})
              AND (@schema IS NULL OR s.name = @schema)
            ORDER BY s.name";

        var matches = new List<ResolvedObject>();
        using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                matches.Add(new ResolvedObject(
                    reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        if (matches.Count == 1) return matches[0];

        if (matches.Count > 1)
        {
            var candidates = string.Join(", ", matches.Select(m => $"{m.Schema}.{m.Name}"));
            throw new AiToolException($"'{name}' exists in more than one schema ({candidates}). Pass the 'schema' argument.");
        }

        // Nothing matched -- offer near misses so the model corrects itself instead of inventing.
        var suggestions = new List<string>();
        using (var command = CreateCommand(connection, $@"
            SELECT TOP 5 s.name + '.' + o.name
            FROM sys.objects o
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.type IN ({typeList}) AND o.is_ms_shipped = 0 AND o.name LIKE '%' + @name + '%'
            ORDER BY LEN(o.name), o.name"))
        {
            command.Parameters.AddWithValue("@name", name);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                suggestions.Add(reader.GetString(0));
        }

        var qualified = schema == null ? name : $"{schema}.{name}";
        var hint = suggestions.Count > 0
            ? $" Did you mean {string.Join(", ", suggestions)}?"
            : $" Call {ListTablesTool} to see what exists.";

        throw new AiToolException($"'{qualified}' does not exist in [{connection.Database}].{hint}");
    }

    private static SqlCommand CreateCommand(SqlConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = LookupTimeoutSeconds;
        return command;
    }

    /// <summary>Accepts "dbo.Orders" in the name argument, brackets and all.</summary>
    private static (string? Schema, string? Name) SplitObjectName(string? schema, string? name)
    {
        schema = schema?.Trim().Trim('[', ']');
        name = name?.Trim();

        if (string.IsNullOrEmpty(name)) return (string.IsNullOrEmpty(schema) ? null : schema, null);

        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Trim('[', ']'))
            .Where(part => part.Length > 0)
            .ToArray();

        return parts.Length switch
        {
            // database.schema.object -- the database is already selected by then.
            >= 3 => (parts[^2], parts[^1]),
            2 => (parts[0], parts[1]),
            1 => (string.IsNullOrEmpty(schema) ? null : schema, parts[0]),
            _ => (string.IsNullOrEmpty(schema) ? null : schema, null)
        };
    }

    private static string? Arg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        if (!args.TryGetProperty(name, out var value)) return null;

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString()
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string FormatCell(object value)
    {
        var text = value switch
        {
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss"),
            DateTimeOffset offset => offset.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            byte[] bytes => $"0x{Convert.ToHexString(bytes[..Math.Min(bytes.Length, 16)])}{(bytes.Length > 16 ? "..." : "")}",
            bool flag => flag ? "1" : "0",
            _ => value.ToString() ?? string.Empty
        };

        return text.Length > MaxCellChars ? text[..MaxCellChars] + "..." : text;
    }

    private static string Condense(string text)
    {
        var single = string.Join(' ', text.Split(
            new[] { '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return single.Length > 90 ? single[..90] + "..." : single;
    }

    private static readonly JsonSerializerOptions ResultOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string Json(object payload) => JsonSerializer.Serialize(payload, ResultOptions);

    private async Task DisposeConnectionAsync()
    {
        if (_connection == null) return;

        try { await _connection.DisposeAsync(); } catch { }
        _connection = null;
        _connectionId = null;
    }

    public async ValueTask DisposeAsync() => await DisposeConnectionAsync();

    private sealed record ResolvedObject(int ObjectId, string Schema, string Name, string TypeDescription);
}

/// <summary>What one tool call produced: the JSON the model reads, and a line for the user.</summary>
public sealed record AiToolOutcome(string Content, AiToolTrace Trace);

/// <summary>A tool failure the model is expected to read and work around.</summary>
public sealed class AiToolException : Exception
{
    public AiToolException(string message) : base(message) { }
}
