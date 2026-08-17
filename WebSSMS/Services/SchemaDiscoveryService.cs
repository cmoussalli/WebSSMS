using Microsoft.Data.SqlClient;
using WebSSMS.Models;

namespace WebSSMS.Services;

public class SchemaDiscoveryService
{
    private readonly ConnectionManager _connectionManager;

    public SchemaDiscoveryService(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    private SqlConnection? Connection => _connectionManager.ActiveConnection;

    public async Task<List<string>> GetDatabasesAsync()
    {
        var databases = new List<string>();
        if (Connection == null) return databases;

        const string sql = "SELECT name FROM sys.databases ORDER BY name";
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            databases.Add(reader.GetString(0));
        }
        return databases;
    }

    public async Task<List<DatabaseObjectNode>> GetTablesAsync(string database)
    {
        var nodes = new List<DatabaseObjectNode>();
        if (Connection == null) return nodes;

        var sql = $@"
            USE [{database}];
            SELECT t.TABLE_SCHEMA, t.TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES t
            WHERE t.TABLE_TYPE = 'BASE TABLE'
            ORDER BY t.TABLE_SCHEMA, t.TABLE_NAME";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            nodes.Add(new DatabaseObjectNode
            {
                Name = reader.GetString(1),
                SchemaName = reader.GetString(0),
                DatabaseName = database,
                ObjectType = DatabaseObjectType.Table
            });
        }
        return nodes;
    }

    public async Task<List<DatabaseObjectNode>> GetViewsAsync(string database)
    {
        var nodes = new List<DatabaseObjectNode>();
        if (Connection == null) return nodes;

        var sql = $@"
            USE [{database}];
            SELECT TABLE_SCHEMA, TABLE_NAME
            FROM INFORMATION_SCHEMA.VIEWS
            ORDER BY TABLE_SCHEMA, TABLE_NAME";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            nodes.Add(new DatabaseObjectNode
            {
                Name = reader.GetString(1),
                SchemaName = reader.GetString(0),
                DatabaseName = database,
                ObjectType = DatabaseObjectType.View
            });
        }
        return nodes;
    }

    public async Task<List<DatabaseObjectNode>> GetProceduresAsync(string database)
    {
        var nodes = new List<DatabaseObjectNode>();
        if (Connection == null) return nodes;

        var sql = $@"
            USE [{database}];
            SELECT ROUTINE_SCHEMA, ROUTINE_NAME
            FROM INFORMATION_SCHEMA.ROUTINES
            WHERE ROUTINE_TYPE = 'PROCEDURE'
            ORDER BY ROUTINE_SCHEMA, ROUTINE_NAME";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            nodes.Add(new DatabaseObjectNode
            {
                Name = reader.GetString(1),
                SchemaName = reader.GetString(0),
                DatabaseName = database,
                ObjectType = DatabaseObjectType.Procedure
            });
        }
        return nodes;
    }

    public async Task<List<DatabaseObjectNode>> GetFunctionsAsync(string database)
    {
        var nodes = new List<DatabaseObjectNode>();
        if (Connection == null) return nodes;

        var sql = $@"
            USE [{database}];
            SELECT r.ROUTINE_SCHEMA, r.ROUTINE_NAME, r.DATA_TYPE
            FROM INFORMATION_SCHEMA.ROUTINES r
            WHERE r.ROUTINE_TYPE = 'FUNCTION'
            ORDER BY r.ROUTINE_SCHEMA, r.ROUTINE_NAME";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var dataType = reader.IsDBNull(2) ? null : reader.GetString(2);
            nodes.Add(new DatabaseObjectNode
            {
                Name = reader.GetString(1),
                SchemaName = reader.GetString(0),
                DatabaseName = database,
                ObjectType = dataType == "TABLE" ? DatabaseObjectType.TableFunction : DatabaseObjectType.ScalarFunction
            });
        }
        return nodes;
    }

    public async Task<List<DatabaseObjectNode>> GetColumnsAsync(string database, string schema, string tableName)
    {
        var nodes = new List<DatabaseObjectNode>();
        if (Connection == null) return nodes;

        var sql = $@"
            USE [{database}];
            SELECT c.COLUMN_NAME, c.DATA_TYPE,
                   c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE,
                   c.IS_NULLABLE,
                   COLUMNPROPERTY(OBJECT_ID('{schema}.{tableName}'), c.COLUMN_NAME, 'IsIdentity') as IsIdentity
            FROM INFORMATION_SCHEMA.COLUMNS c
            WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @tableName
            ORDER BY c.ORDINAL_POSITION";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@tableName", tableName);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var dataType = reader.GetString(1);
            var maxLen = reader.IsDBNull(2) ? (int?)null : Convert.ToInt32(reader.GetValue(2));
            var precision = reader.IsDBNull(3) ? (int?)null : Convert.ToInt32(reader.GetValue(3));
            var scale = reader.IsDBNull(4) ? (int?)null : Convert.ToInt32(reader.GetValue(4));

            var typeDisplay = dataType;
            if (maxLen.HasValue && dataType is "nvarchar" or "varchar" or "nchar" or "char" or "binary" or "varbinary")
                typeDisplay = maxLen == -1 ? $"{dataType}(MAX)" : $"{dataType}({maxLen})";
            else if (precision.HasValue && dataType is "decimal" or "numeric")
                typeDisplay = $"{dataType}({precision},{scale})";

            nodes.Add(new DatabaseObjectNode
            {
                Name = reader.GetString(0),
                DatabaseName = database,
                ObjectType = DatabaseObjectType.Column,
                DataType = typeDisplay,
                IsNullable = reader.GetString(5) == "YES",
                IsIdentity = !reader.IsDBNull(6) && Convert.ToInt32(reader.GetValue(6)) == 1
            });
        }
        return nodes;
    }

    public async Task<List<DatabaseObjectNode>> GetIndexesAsync(string database, string schema, string tableName)
    {
        var nodes = new List<DatabaseObjectNode>();
        if (Connection == null) return nodes;

        var sql = $@"
            USE [{database}];
            SELECT i.name, i.type_desc, i.is_unique, i.is_primary_key
            FROM sys.indexes i
            INNER JOIN sys.objects o ON i.object_id = o.object_id
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE s.name = @schema AND o.name = @tableName AND i.name IS NOT NULL
            ORDER BY i.name";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@tableName", tableName);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var isPK = reader.GetBoolean(3);
            nodes.Add(new DatabaseObjectNode
            {
                Name = reader.GetString(0),
                DatabaseName = database,
                ObjectType = isPK ? DatabaseObjectType.PrimaryKey : DatabaseObjectType.Index,
                DataType = reader.GetString(1) + (reader.GetBoolean(2) ? " (Unique)" : "")
            });
        }
        return nodes;
    }

    public async Task<List<DatabaseObjectNode>> GetForeignKeysAsync(string database, string schema, string tableName)
    {
        var nodes = new List<DatabaseObjectNode>();
        if (Connection == null) return nodes;

        var sql = $@"
            USE [{database}];
            SELECT fk.name,
                   OBJECT_SCHEMA_NAME(fk.referenced_object_id) + '.' + OBJECT_NAME(fk.referenced_object_id) as ReferencedTable
            FROM sys.foreign_keys fk
            INNER JOIN sys.objects o ON fk.parent_object_id = o.object_id
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE s.name = @schema AND o.name = @tableName
            ORDER BY fk.name";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@tableName", tableName);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            nodes.Add(new DatabaseObjectNode
            {
                Name = reader.GetString(0),
                DatabaseName = database,
                ObjectType = DatabaseObjectType.ForeignKey,
                DataType = $"→ {reader.GetString(1)}"
            });
        }
        return nodes;
    }

    public async Task<List<DatabaseObjectNode>> GetTriggersAsync(string database, string schema, string tableName)
    {
        var nodes = new List<DatabaseObjectNode>();
        if (Connection == null) return nodes;

        var sql = $@"
            USE [{database}];
            SELECT tr.name, tr.is_disabled
            FROM sys.triggers tr
            INNER JOIN sys.objects o ON tr.parent_id = o.object_id
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE s.name = @schema AND o.name = @tableName
            ORDER BY tr.name";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@tableName", tableName);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            nodes.Add(new DatabaseObjectNode
            {
                Name = reader.GetString(0),
                DatabaseName = database,
                ObjectType = DatabaseObjectType.Trigger,
                DataType = reader.GetBoolean(1) ? "Disabled" : "Enabled"
            });
        }
        return nodes;
    }

    public async Task<List<DatabaseObjectNode>> GetCheckConstraintsAsync(string database, string schema, string tableName)
    {
        var nodes = new List<DatabaseObjectNode>();
        if (Connection == null) return nodes;

        var sql = $@"
            USE [{database}];
            SELECT cc.name, cc.definition
            FROM sys.check_constraints cc
            INNER JOIN sys.objects o ON cc.parent_object_id = o.object_id
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE s.name = @schema AND o.name = @tableName
            ORDER BY cc.name";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@tableName", tableName);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            nodes.Add(new DatabaseObjectNode
            {
                Name = reader.GetString(0),
                DatabaseName = database,
                ObjectType = DatabaseObjectType.CheckConstraint,
                DataType = reader.GetString(1)
            });
        }
        return nodes;
    }

    public async Task<TableDefinition> GetTableDefinitionAsync(string database, string schema, string tableName)
    {
        var table = new TableDefinition
        {
            DatabaseName = database,
            SchemaName = schema,
            TableName = tableName,
            IsExisting = true
        };

        if (Connection == null) return table;

        // Load columns
        var colSql = $@"
            USE [{database}];
            SELECT c.COLUMN_NAME, c.DATA_TYPE,
                   c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE,
                   c.IS_NULLABLE, c.COLUMN_DEFAULT, c.ORDINAL_POSITION,
                   COLUMNPROPERTY(OBJECT_ID(@fullName), c.COLUMN_NAME, 'IsIdentity') as IsIdentity,
                   IDENT_SEED(@fullName) as IdentSeed,
                   IDENT_INCR(@fullName) as IdentIncr
            FROM INFORMATION_SCHEMA.COLUMNS c
            WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @tableName
            ORDER BY c.ORDINAL_POSITION";

        using var colCmd = Connection.CreateCommand();
        colCmd.CommandText = colSql;
        colCmd.Parameters.AddWithValue("@schema", schema);
        colCmd.Parameters.AddWithValue("@tableName", tableName);
        colCmd.Parameters.AddWithValue("@fullName", $"{schema}.{tableName}");

        using var colReader = await colCmd.ExecuteReaderAsync();
        while (await colReader.ReadAsync())
        {
            var col = new ColumnDefinition
            {
                Name = colReader.GetString(0),
                DataType = colReader.GetString(1),
                MaxLength = colReader.IsDBNull(2) ? null : Convert.ToInt32(colReader.GetValue(2)),
                Precision = colReader.IsDBNull(3) ? null : Convert.ToInt32(colReader.GetValue(3)),
                Scale = colReader.IsDBNull(4) ? null : Convert.ToInt32(colReader.GetValue(4)),
                IsNullable = colReader.GetString(5) == "YES",
                DefaultValue = colReader.IsDBNull(6) ? null : colReader.GetString(6),
                OrdinalPosition = Convert.ToInt32(colReader.GetValue(7)),
                IsIdentity = !colReader.IsDBNull(8) && Convert.ToInt32(colReader.GetValue(8)) == 1,
                IdentitySeed = !colReader.IsDBNull(9) ? Convert.ToInt32(colReader.GetValue(9)) : 1,
                IdentityIncrement = !colReader.IsDBNull(10) ? Convert.ToInt32(colReader.GetValue(10)) : 1
            };
            table.Columns.Add(col);
        }

        // Load primary key columns
        var pkSql = $@"
            USE [{database}];
            SELECT c.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE c
                ON tc.CONSTRAINT_NAME = c.CONSTRAINT_NAME AND tc.TABLE_SCHEMA = c.TABLE_SCHEMA
            WHERE tc.TABLE_SCHEMA = @schema AND tc.TABLE_NAME = @tableName
                AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'";

        using var pkCmd = Connection.CreateCommand();
        pkCmd.CommandText = pkSql;
        pkCmd.Parameters.AddWithValue("@schema", schema);
        pkCmd.Parameters.AddWithValue("@tableName", tableName);

        using var pkReader = await pkCmd.ExecuteReaderAsync();
        var pkColumns = new HashSet<string>();
        while (await pkReader.ReadAsync())
        {
            pkColumns.Add(pkReader.GetString(0));
        }

        foreach (var col in table.Columns)
        {
            col.IsPrimaryKey = pkColumns.Contains(col.Name);
        }

        return table;
    }

    public async Task<ServerInfo> GetServerInfoAsync()
    {
        if (Connection == null) return new ServerInfo();

        var sql = @"
            SELECT
                SERVERPROPERTY('ServerName') as ServerName,
                SERVERPROPERTY('ProductVersion') as ProductVersion,
                SERVERPROPERTY('ProductLevel') as ProductLevel,
                SERVERPROPERTY('Edition') as Edition,
                SERVERPROPERTY('EngineEdition') as EngineEdition,
                SERVERPROPERTY('Collation') as Collation,
                SERVERPROPERTY('IsClustered') as IsClustered,
                SERVERPROPERTY('IsHadrEnabled') as IsHadrEnabled";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new ServerInfo
            {
                ServerName = reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString()!,
                ProductVersion = reader.IsDBNull(1) ? "" : reader.GetValue(1).ToString()!,
                ProductLevel = reader.IsDBNull(2) ? "" : reader.GetValue(2).ToString()!,
                Edition = reader.IsDBNull(3) ? "" : reader.GetValue(3).ToString()!,
                EngineEdition = reader.IsDBNull(4) ? "" : reader.GetValue(4).ToString()!,
                Collation = reader.IsDBNull(5) ? "" : reader.GetValue(5).ToString()!,
                IsClustered = !reader.IsDBNull(6) && Convert.ToInt32(reader.GetValue(6)) == 1,
                IsHadrEnabled = !reader.IsDBNull(7) && Convert.ToInt32(reader.GetValue(7)) == 1
            };
        }
        return new ServerInfo();
    }

    public async Task<List<DatabaseInfo>> GetDatabaseInfosAsync()
    {
        var infos = new List<DatabaseInfo>();
        if (Connection == null) return infos;

        var sql = @"
            SELECT d.name, d.database_id, d.state_desc, d.recovery_model_desc,
                   d.compatibility_level, d.collation_name, SUSER_SNAME(d.owner_sid),
                   d.create_date, d.is_read_only, d.is_auto_shrink_on, d.is_auto_close_on,
                   ISNULL((SELECT SUM(CAST(mf.size AS BIGINT) * 8.0 / 1024) FROM sys.master_files mf WHERE mf.database_id = d.database_id), 0),
                   ISNULL((SELECT SUM(CAST(mf.size AS BIGINT) * 8.0 / 1024) FROM sys.master_files mf WHERE mf.database_id = d.database_id AND mf.type = 0), 0)
            FROM sys.databases d
            ORDER BY d.name";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            infos.Add(new DatabaseInfo
            {
                Name = reader.GetString(0),
                DatabaseId = reader.GetInt32(1),
                State = reader.GetString(2),
                RecoveryModel = reader.GetString(3),
                CompatibilityLevel = reader.GetValue(4).ToString()!,
                Collation = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Owner = reader.IsDBNull(6) ? "" : reader.GetString(6),
                CreateDate = reader.GetDateTime(7),
                IsReadOnly = reader.GetBoolean(8),
                IsAutoShrink = reader.GetBoolean(9),
                IsAutoClose = reader.GetBoolean(10),
                SizeMB = Convert.ToDouble(reader.GetValue(11)),
                SpaceAvailableMB = Convert.ToDouble(reader.GetValue(12))
            });
        }

        // Load file details for each database
        var fileSql = @"
            SELECT mf.database_id, mf.name, mf.physical_name, mf.type_desc,
                   CAST(mf.size AS BIGINT) * 8.0 / 1024 AS SizeMB,
                   CASE WHEN mf.max_size = -1 THEN -1 ELSE CAST(mf.max_size AS BIGINT) * 8.0 / 1024 END AS MaxSizeMB,
                   CASE WHEN mf.is_percent_growth = 1 THEN CAST(mf.growth AS VARCHAR) + '%'
                        ELSE CAST(CAST(mf.growth * 8.0 / 1024 AS INT) AS VARCHAR) + ' MB' END AS Growth,
                   ISNULL(fg.name, '') AS FileGroup
            FROM sys.master_files mf
            LEFT JOIN sys.filegroups fg ON mf.data_space_id = fg.data_space_id AND mf.database_id = DB_ID()
            ORDER BY mf.database_id, mf.file_id";

        using var fileCmd = Connection.CreateCommand();
        fileCmd.CommandText = fileSql;

        var dbLookup = infos.ToDictionary(d => d.DatabaseId);

        using var fileReader = await fileCmd.ExecuteReaderAsync();
        while (await fileReader.ReadAsync())
        {
            var dbId = fileReader.GetInt32(0);
            if (dbLookup.TryGetValue(dbId, out var dbInfo))
            {
                dbInfo.Files.Add(new DatabaseFile
                {
                    Name = fileReader.GetString(1),
                    PhysicalName = fileReader.GetString(2),
                    Type = fileReader.GetString(3),
                    SizeMB = Convert.ToDouble(fileReader.GetValue(4)),
                    MaxSizeMB = Convert.ToDouble(fileReader.GetValue(5)),
                    Growth = fileReader.GetString(6),
                    FileGroup = fileReader.GetString(7)
                });
            }
        }

        return infos;
    }

    public async Task<List<string>> GetSchemasAsync(string database)
    {
        var schemas = new List<string>();
        if (Connection == null) return schemas;

        var sql = $"USE [{database}]; SELECT name FROM sys.schemas WHERE schema_id < 16384 ORDER BY name";
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            schemas.Add(reader.GetString(0));
        }
        return schemas;
    }

    public async Task<List<DatabaseObjectNode>> GetSynonymsAsync(string database)
    {
        var nodes = new List<DatabaseObjectNode>();
        if (Connection == null) return nodes;

        var sql = $@"
            USE [{database}];
            SELECT s.name, syn.name, syn.base_object_name
            FROM sys.synonyms syn
            INNER JOIN sys.schemas s ON syn.schema_id = s.schema_id
            ORDER BY s.name, syn.name";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            nodes.Add(new DatabaseObjectNode
            {
                Name = reader.GetString(1),
                SchemaName = reader.GetString(0),
                DatabaseName = database,
                ObjectType = DatabaseObjectType.Synonym,
                DataType = $"→ {reader.GetString(2)}"
            });
        }
        return nodes;
    }

    public async Task<List<DatabaseObjectNode>> GetUserDefinedTypesAsync(string database)
    {
        var nodes = new List<DatabaseObjectNode>();
        if (Connection == null) return nodes;

        var sql = $@"
            USE [{database}];
            SELECT s.name, t.name, bt.name as base_type, t.max_length
            FROM sys.types t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            LEFT JOIN sys.types bt ON t.system_type_id = bt.user_type_id
            WHERE t.is_user_defined = 1
            ORDER BY s.name, t.name";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            nodes.Add(new DatabaseObjectNode
            {
                Name = reader.GetString(1),
                SchemaName = reader.GetString(0),
                DatabaseName = database,
                ObjectType = DatabaseObjectType.UserDefinedType,
                DataType = reader.IsDBNull(2) ? "" : reader.GetString(2)
            });
        }
        return nodes;
    }
}
