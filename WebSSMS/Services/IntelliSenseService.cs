using Microsoft.Data.SqlClient;
using WebSSMS.Models;

namespace WebSSMS.Services;

public class IntelliSenseService
{
    private readonly ConnectionManager _connectionManager;
    private readonly Dictionary<string, List<CompletionItem>> _cache = new();

    public IntelliSenseService(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    private SqlConnection? Connection => _connectionManager.ActiveConnection;

    public async Task<List<CompletionItem>> GetCompletionsAsync(string? database = null)
    {
        var cacheKey = database ?? "server";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var completions = new List<CompletionItem>();

        if (Connection == null) return completions;

        try
        {
            // Get database names
            if (database == null)
            {
                var dbSql = "SELECT name FROM sys.databases ORDER BY name";
                using var dbCmd = Connection.CreateCommand();
                dbCmd.CommandText = dbSql;

                using var dbReader = await dbCmd.ExecuteReaderAsync();
                while (await dbReader.ReadAsync())
                {
                    completions.Add(new CompletionItem
                    {
                        Label = dbReader.GetString(0),
                        Kind = "Module",
                        Detail = "Database",
                        InsertText = $"[{dbReader.GetString(0)}]"
                    });
                }

                _cache[cacheKey] = completions;
                return completions;
            }

            // Get tables
            var tableSql = $@"
                USE [{database}];
                SELECT s.name, t.name
                FROM sys.tables t
                INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                ORDER BY s.name, t.name";

            using var tableCmd = Connection.CreateCommand();
            tableCmd.CommandText = tableSql;

            using var tableReader = await tableCmd.ExecuteReaderAsync();
            while (await tableReader.ReadAsync())
            {
                var schema = tableReader.GetString(0);
                var table = tableReader.GetString(1);
                completions.Add(new CompletionItem
                {
                    Label = $"{schema}.{table}",
                    Kind = "Class",
                    Detail = "Table",
                    InsertText = $"[{schema}].[{table}]"
                });
            }

            // Get views
            var viewSql = $@"
                USE [{database}];
                SELECT s.name, v.name
                FROM sys.views v
                INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
                ORDER BY s.name, v.name";

            using var viewCmd = Connection.CreateCommand();
            viewCmd.CommandText = viewSql;

            using var viewReader = await viewCmd.ExecuteReaderAsync();
            while (await viewReader.ReadAsync())
            {
                completions.Add(new CompletionItem
                {
                    Label = $"{viewReader.GetString(0)}.{viewReader.GetString(1)}",
                    Kind = "Interface",
                    Detail = "View",
                    InsertText = $"[{viewReader.GetString(0)}].[{viewReader.GetString(1)}]"
                });
            }

            // Get columns
            var colSql = $@"
                USE [{database}];
                SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE
                FROM INFORMATION_SCHEMA.COLUMNS c
                ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION";

            using var colCmd = Connection.CreateCommand();
            colCmd.CommandText = colSql;

            using var colReader = await colCmd.ExecuteReaderAsync();
            while (await colReader.ReadAsync())
            {
                completions.Add(new CompletionItem
                {
                    Label = colReader.GetString(2),
                    Kind = "Field",
                    Detail = $"{colReader.GetString(0)}.{colReader.GetString(1)} ({colReader.GetString(3)})",
                    InsertText = $"[{colReader.GetString(2)}]"
                });
            }

            // Get stored procedures
            var procSql = $@"
                USE [{database}];
                SELECT s.name, p.name
                FROM sys.procedures p
                INNER JOIN sys.schemas s ON p.schema_id = s.schema_id
                ORDER BY s.name, p.name";

            using var procCmd = Connection.CreateCommand();
            procCmd.CommandText = procSql;

            using var procReader = await procCmd.ExecuteReaderAsync();
            while (await procReader.ReadAsync())
            {
                completions.Add(new CompletionItem
                {
                    Label = $"{procReader.GetString(0)}.{procReader.GetString(1)}",
                    Kind = "Method",
                    Detail = "Stored Procedure",
                    InsertText = $"[{procReader.GetString(0)}].[{procReader.GetString(1)}]"
                });
            }

            // Get functions
            var funcSql = $@"
                USE [{database}];
                SELECT ROUTINE_SCHEMA, ROUTINE_NAME, DATA_TYPE
                FROM INFORMATION_SCHEMA.ROUTINES
                WHERE ROUTINE_TYPE = 'FUNCTION'
                ORDER BY ROUTINE_SCHEMA, ROUTINE_NAME";

            using var funcCmd = Connection.CreateCommand();
            funcCmd.CommandText = funcSql;

            using var funcReader = await funcCmd.ExecuteReaderAsync();
            while (await funcReader.ReadAsync())
            {
                completions.Add(new CompletionItem
                {
                    Label = $"{funcReader.GetString(0)}.{funcReader.GetString(1)}",
                    Kind = "Function",
                    Detail = "Function",
                    InsertText = $"[{funcReader.GetString(0)}].[{funcReader.GetString(1)}]"
                });
            }
        }
        catch { }

        _cache[cacheKey] = completions;
        return completions;
    }

    public void ClearCache()
    {
        _cache.Clear();
    }

    public void ClearCache(string database)
    {
        _cache.Remove(database);
    }
}

public class CompletionItem
{
    public string Label { get; set; } = string.Empty;
    public string Kind { get; set; } = "Text";
    public string Detail { get; set; } = string.Empty;
    public string InsertText { get; set; } = string.Empty;
    public string? Documentation { get; set; }
}
