using Microsoft.Data.SqlClient;
using WebSSMS.Models;

namespace WebSSMS.Services;

public class MaintenanceService
{
    private readonly ConnectionManager _connectionManager;

    public MaintenanceService(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    private SqlConnection? Connection => _connectionManager.ActiveConnection;

    public async Task<(bool Success, string? Error)> RebuildIndexesAsync(string database, string schema, string tableName)
    {
        if (Connection == null) return (false, "No active connection");

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = $"USE [{database}]; ALTER INDEX ALL ON [{schema}].[{tableName}] REBUILD";
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync();
            return (true, null);
        }
        catch (SqlException ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> ReorganizeIndexesAsync(string database, string schema, string tableName)
    {
        if (Connection == null) return (false, "No active connection");

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = $"USE [{database}]; ALTER INDEX ALL ON [{schema}].[{tableName}] REORGANIZE";
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync();
            return (true, null);
        }
        catch (SqlException ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> UpdateStatisticsAsync(string database, string? schema = null, string? tableName = null)
    {
        if (Connection == null) return (false, "No active connection");

        try
        {
            using var cmd = Connection.CreateCommand();
            if (schema != null && tableName != null)
                cmd.CommandText = $"USE [{database}]; UPDATE STATISTICS [{schema}].[{tableName}]";
            else
                cmd.CommandText = $"USE [{database}]; EXEC sp_updatestats";

            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync();
            return (true, null);
        }
        catch (SqlException ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> ShrinkDatabaseAsync(string database, int targetPercent = 10)
    {
        if (Connection == null) return (false, "No active connection");

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = $"DBCC SHRINKDATABASE ([{database}], {targetPercent})";
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync();
            return (true, null);
        }
        catch (SqlException ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> CheckDatabaseIntegrityAsync(string database)
    {
        if (Connection == null) return (false, "No active connection");

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = $"DBCC CHECKDB ([{database}]) WITH NO_INFOMSGS";
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync();
            return (true, null);
        }
        catch (SqlException ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<List<(string IndexName, double FragPercent, int Pages)>> GetIndexFragmentationAsync(
        string database, string schema, string tableName)
    {
        var results = new List<(string, double, int)>();
        if (Connection == null) return results;

        var sql = $@"
            USE [{database}];
            SELECT i.name, ips.avg_fragmentation_in_percent, ips.page_count
            FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('{schema}.{tableName}'), NULL, NULL, 'LIMITED') ips
            INNER JOIN sys.indexes i ON ips.object_id = i.object_id AND ips.index_id = i.index_id
            WHERE i.name IS NOT NULL
            ORDER BY ips.avg_fragmentation_in_percent DESC";

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add((reader.GetString(0), reader.GetDouble(1), Convert.ToInt32(reader.GetInt64(2))));
            }
        }
        catch { }

        return results;
    }
}
