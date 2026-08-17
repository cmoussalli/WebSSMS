using Microsoft.Data.SqlClient;
using WebSSMS.Models;

namespace WebSSMS.Services;

public class MonitoringService
{
    private readonly ConnectionManager _connectionManager;

    public MonitoringService(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    private SqlConnection? Connection => _connectionManager.ActiveConnection;

    public async Task<List<ActiveProcess>> GetActiveProcessesAsync()
    {
        var processes = new List<ActiveProcess>();
        if (Connection == null) return processes;

        var sql = @"
            SELECT
                s.session_id,
                s.status,
                s.login_name,
                s.host_name,
                ISNULL(DB_NAME(s.database_id), '') as database_name,
                ISNULL(r.command, '') as command,
                s.cpu_time,
                s.reads + s.writes as disk_io,
                s.last_request_start_time,
                s.program_name,
                ISNULL(r.blocking_session_id, 0) as blocked_by,
                ISNULL(r.wait_time, 0) as wait_time,
                r.wait_type,
                (SELECT TOP 1 text FROM sys.dm_exec_sql_text(r.sql_handle)) as sql_text
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
            WHERE s.is_user_process = 1
            ORDER BY s.session_id";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        try
        {
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                processes.Add(new ActiveProcess
                {
                    Spid = Convert.ToInt32(reader.GetValue(0)),
                    Status = reader.GetString(1),
                    LoginName = reader.GetString(2),
                    HostName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    DatabaseName = reader.GetString(4),
                    Command = reader.GetString(5),
                    CpuTime = Convert.ToInt32(reader.GetValue(6)),
                    DiskIO = Convert.ToInt64(reader.GetValue(7)),
                    LastBatch = reader.IsDBNull(8) ? null : reader.GetDateTime(8).ToString("yyyy-MM-dd HH:mm:ss"),
                    ProgramName = reader.IsDBNull(9) ? "" : reader.GetString(9),
                    BlockedBy = Convert.ToInt32(reader.GetValue(10)),
                    WaitTime = Convert.ToInt32(reader.GetValue(11)),
                    WaitType = reader.IsDBNull(12) ? null : reader.GetString(12),
                    SqlText = reader.IsDBNull(13) ? null : reader.GetString(13)
                });
            }
        }
        catch { /* DMV access may require elevated permissions */ }

        return processes;
    }

    public async Task<Dictionary<string, object>> GetServerStatsAsync()
    {
        var stats = new Dictionary<string, object>();
        if (Connection == null) return stats;

        try
        {
            // CPU usage via performance counters (most reliable across all SQL Server versions)
            var cpuSql = @"
                DECLARE @cpu_pct INT = 0;

                -- Method 1: Resource Pool Stats performance counter
                SELECT @cpu_pct = cntr_value
                FROM sys.dm_os_performance_counters WITH (NOLOCK)
                WHERE counter_name = 'CPU usage %'
                AND object_name LIKE '%Resource Pool Stats%'
                AND instance_name = 'default';

                -- Method 2: Fall back to ring buffers if counter returned 0
                IF @cpu_pct = 0
                BEGIN
                    SELECT TOP 1 @cpu_pct = record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int')
                    FROM (
                        SELECT CONVERT(XML, record) as record, [timestamp]
                        FROM sys.dm_os_ring_buffers WITH (NOLOCK)
                        WHERE ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
                        AND record LIKE N'%<SystemHealth>%'
                    ) as x
                    ORDER BY x.[timestamp] DESC;
                END

                SELECT ISNULL(@cpu_pct, 0) as sql_cpu;";

            using var cpuCmd = Connection.CreateCommand();
            cpuCmd.CommandText = cpuSql;

            var result = await cpuCmd.ExecuteScalarAsync();
            stats["SqlCpuPercent"] = result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
            stats["SystemIdlePercent"] = 0;
        }
        catch
        {
            stats["SqlCpuPercent"] = 0;
            stats["SystemIdlePercent"] = 0;
        }

        try
        {
            // Memory usage
            var memSql = @"
                SELECT
                    total_physical_memory_kb / 1024 as total_memory_mb,
                    available_physical_memory_kb / 1024 as available_memory_mb
                FROM sys.dm_os_sys_memory";

            using var memCmd = Connection.CreateCommand();
            memCmd.CommandText = memSql;

            using var memReader = await memCmd.ExecuteReaderAsync();
            if (await memReader.ReadAsync())
            {
                stats["TotalMemoryMB"] = memReader.GetInt64(0);
                stats["AvailableMemoryMB"] = memReader.GetInt64(1);
            }
        }
        catch
        {
            stats["TotalMemoryMB"] = 0L;
            stats["AvailableMemoryMB"] = 0L;
        }

        try
        {
            // Connection count
            var connSql = "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1";
            using var connCmd = Connection.CreateCommand();
            connCmd.CommandText = connSql;
            stats["ActiveConnections"] = (int)(await connCmd.ExecuteScalarAsync() ?? 0);
        }
        catch
        {
            stats["ActiveConnections"] = 0;
        }

        try
        {
            // Database sizes
            var dbSql = @"
                SELECT
                    SUM(CAST(size AS BIGINT) * 8 / 1024) as total_size_mb
                FROM sys.master_files";

            using var dbCmd = Connection.CreateCommand();
            dbCmd.CommandText = dbSql;
            stats["TotalDatabaseSizeMB"] = Convert.ToInt64(await dbCmd.ExecuteScalarAsync() ?? 0);
        }
        catch
        {
            stats["TotalDatabaseSizeMB"] = 0L;
        }

        return stats;
    }

    public async Task<(bool Success, string? Error)> KillProcessAsync(int spid)
    {
        if (Connection == null) return (false, "No active connection");

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = $"KILL {spid}";
            await cmd.ExecuteNonQueryAsync();
            return (true, null);
        }
        catch (SqlException ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<List<(string WaitType, long WaitTimeMs, long WaitingTasks)>> GetTopWaitsAsync()
    {
        var waits = new List<(string, long, long)>();
        if (Connection == null) return waits;

        var sql = @"
            SELECT TOP 10
                wait_type,
                wait_time_ms,
                waiting_tasks_count
            FROM sys.dm_os_wait_stats
            WHERE wait_type NOT LIKE '%SLEEP%'
                AND wait_type NOT LIKE '%IDLE%'
                AND wait_type NOT LIKE '%QUEUE%'
                AND wait_type NOT IN ('CLR_AUTO_EVENT', 'REQUEST_FOR_DEADLOCK_SEARCH',
                    'SQLTRACE_INCREMENTAL_FLUSH_SLEEP', 'XE_TIMER_EVENT', 'FT_IFTS_SCHEDULER_IDLE_WAIT',
                    'LOGMGR_QUEUE', 'CHECKPOINT_QUEUE', 'BROKER_TO_FLUSH', 'BROKER_TASK_STOP',
                    'HADR_FILESTREAM_IOMGR_IOCOMPLETION', 'DIRTY_PAGE_POLL', 'LAZYWRITER_SLEEP')
            ORDER BY wait_time_ms DESC";

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                waits.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }
        catch { }

        return waits;
    }
}
