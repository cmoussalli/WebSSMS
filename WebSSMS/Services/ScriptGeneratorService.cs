using Microsoft.Data.SqlClient;
using WebSSMS.Models;
using System.Text;

namespace WebSSMS.Services;

public class ScriptGeneratorService
{
    private readonly ConnectionManager _connectionManager;

    public ScriptGeneratorService(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    private SqlConnection? Connection => _connectionManager.ActiveConnection;

    public async Task<string> ScriptTableAsCreateAsync(string database, string schema, string tableName)
    {
        if (Connection == null) return "-- No active connection";

        var sb = new StringBuilder();
        sb.AppendLine($"USE [{database}]");
        sb.AppendLine("GO");
        sb.AppendLine();
        sb.AppendLine($"CREATE TABLE [{schema}].[{tableName}]");
        sb.AppendLine("(");

        var sql = $@"
            USE [{database}];
            SELECT c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH,
                   c.NUMERIC_PRECISION, c.NUMERIC_SCALE, c.IS_NULLABLE,
                   c.COLUMN_DEFAULT,
                   COLUMNPROPERTY(OBJECT_ID('{schema}.{tableName}'), c.COLUMN_NAME, 'IsIdentity') as IsIdentity,
                   IDENT_SEED('{schema}.{tableName}') as IdentSeed,
                   IDENT_INCR('{schema}.{tableName}') as IdentIncr
            FROM INFORMATION_SCHEMA.COLUMNS c
            WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @tableName
            ORDER BY c.ORDINAL_POSITION";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@tableName", tableName);

        var columns = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var colDef = new StringBuilder();
            colDef.Append($"    [{reader.GetString(0)}] ");

            var dataType = reader.GetString(1);
            var maxLen = reader.IsDBNull(2) ? (int?)null : Convert.ToInt32(reader.GetValue(2));
            var precision = reader.IsDBNull(3) ? (int?)null : Convert.ToInt32(reader.GetValue(3));
            var scale = reader.IsDBNull(4) ? (int?)null : Convert.ToInt32(reader.GetValue(4));

            if (maxLen.HasValue && dataType is "nvarchar" or "varchar" or "nchar" or "char" or "binary" or "varbinary")
                colDef.Append(maxLen == -1 ? $"[{dataType}](MAX)" : $"[{dataType}]({maxLen})");
            else if (precision.HasValue && dataType is "decimal" or "numeric")
                colDef.Append($"[{dataType}]({precision}, {scale})");
            else
                colDef.Append($"[{dataType}]");

            var isIdentity = !reader.IsDBNull(7) && Convert.ToInt32(reader.GetValue(7)) == 1;
            if (isIdentity)
            {
                var seed = reader.IsDBNull(8) ? 1 : Convert.ToInt32(reader.GetValue(8));
                var incr = reader.IsDBNull(9) ? 1 : Convert.ToInt32(reader.GetValue(9));
                colDef.Append($" IDENTITY({seed},{incr})");
            }

            colDef.Append(reader.GetString(5) == "YES" ? " NULL" : " NOT NULL");

            if (!reader.IsDBNull(6))
                colDef.Append($" DEFAULT {reader.GetString(6)}");

            columns.Add(colDef.ToString());
        }

        // Get primary key
        var pkSql = $@"
            USE [{database}];
            SELECT kc.name, col.name as column_name
            FROM sys.key_constraints kc
            INNER JOIN sys.index_columns ic ON kc.parent_object_id = ic.object_id AND kc.unique_index_id = ic.index_id
            INNER JOIN sys.columns col ON ic.object_id = col.object_id AND ic.column_id = col.column_id
            WHERE kc.type = 'PK'
            AND OBJECT_SCHEMA_NAME(kc.parent_object_id) = @schema
            AND OBJECT_NAME(kc.parent_object_id) = @tableName
            ORDER BY ic.key_ordinal";

        using var pkCmd = Connection.CreateCommand();
        pkCmd.CommandText = pkSql;
        pkCmd.Parameters.AddWithValue("@schema", schema);
        pkCmd.Parameters.AddWithValue("@tableName", tableName);

        string? pkName = null;
        var pkColumns = new List<string>();

        using var pkReader = await pkCmd.ExecuteReaderAsync();
        while (await pkReader.ReadAsync())
        {
            pkName ??= pkReader.GetString(0);
            pkColumns.Add($"[{pkReader.GetString(1)}]");
        }

        if (pkName != null)
        {
            columns.Add($"    CONSTRAINT [{pkName}] PRIMARY KEY CLUSTERED ({string.Join(", ", pkColumns)})");
        }

        sb.AppendLine(string.Join(",\n", columns));
        sb.AppendLine(")");
        sb.AppendLine("GO");

        return sb.ToString();
    }

    public async Task<string> ScriptTableAsDropAsync(string database, string schema, string tableName)
    {
        await Task.CompletedTask;
        var sb = new StringBuilder();
        sb.AppendLine($"USE [{database}]");
        sb.AppendLine("GO");
        sb.AppendLine();
        sb.AppendLine($"IF OBJECT_ID(N'[{schema}].[{tableName}]', N'U') IS NOT NULL");
        sb.AppendLine($"    DROP TABLE [{schema}].[{tableName}]");
        sb.AppendLine("GO");
        return sb.ToString();
    }

    public async Task<string> ScriptSelectTopAsync(string database, string schema, string tableName, int topN = 1000)
    {
        if (Connection == null) return $"SELECT TOP {topN} * FROM [{schema}].[{tableName}]";

        var sql = $@"
            USE [{database}];
            SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @tableName
            ORDER BY ORDINAL_POSITION";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@tableName", tableName);

        var columns = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add($"[{reader.GetString(0)}]");
        }

        var columnList = columns.Count > 0 ? string.Join(",\n       ", columns) : "*";
        return $"SELECT TOP {topN}\n       {columnList}\nFROM [{database}].[{schema}].[{tableName}]";
    }

    public async Task<string> ScriptProcedureAsync(string database, string schema, string procName)
    {
        if (Connection == null) return "-- No active connection";

        var sql = $@"
            USE [{database}];
            SELECT m.definition
            FROM sys.sql_modules m
            INNER JOIN sys.objects o ON m.object_id = o.object_id
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE s.name = @schema AND o.name = @procName";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@procName", procName);

        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString() ?? $"-- Unable to script [{schema}].[{procName}]";
    }

    public async Task<string> ScriptViewAsync(string database, string schema, string viewName)
    {
        if (Connection == null) return "-- No active connection";

        var sql = $@"
            USE [{database}];
            SELECT m.definition
            FROM sys.sql_modules m
            INNER JOIN sys.objects o ON m.object_id = o.object_id
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE s.name = @schema AND o.name = @viewName";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@viewName", viewName);

        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString() ?? $"-- Unable to script [{schema}].[{viewName}]";
    }

    public async Task<string> ScriptFunctionAsync(string database, string schema, string funcName)
    {
        return await ScriptProcedureAsync(database, schema, funcName); // Same approach via sys.sql_modules
    }

    public string ScriptCreateDatabase(string databaseName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE DATABASE [{databaseName}]");
        sb.AppendLine("GO");
        return sb.ToString();
    }

    public string ScriptDropDatabase(string databaseName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"USE [master]");
        sb.AppendLine("GO");
        sb.AppendLine($"IF DB_ID(N'{databaseName}') IS NOT NULL");
        sb.AppendLine("BEGIN");
        sb.AppendLine($"    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        sb.AppendLine($"    DROP DATABASE [{databaseName}]");
        sb.AppendLine("END");
        sb.AppendLine("GO");
        return sb.ToString();
    }
}
