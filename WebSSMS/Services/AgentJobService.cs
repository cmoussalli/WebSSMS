using Microsoft.Data.SqlClient;
using WebSSMS.Models;

namespace WebSSMS.Services;

public class AgentJobService
{
    private readonly ConnectionManager _connectionManager;

    public AgentJobService(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    private SqlConnection? Connection => _connectionManager.ActiveConnection;

    public async Task<List<AgentJobInfo>> GetJobsAsync()
    {
        var jobs = new List<AgentJobInfo>();
        if (Connection == null) return jobs;

        var sql = @"
            USE msdb;
            SELECT
                j.job_id, j.name, j.description, j.enabled,
                c.name as category_name, SUSER_SNAME(j.owner_sid) as owner,
                j.date_created, j.date_modified,
                (SELECT TOP 1 run_date FROM msdb.dbo.sysjobhistory h
                 WHERE h.job_id = j.job_id AND h.step_id = 0
                 ORDER BY h.run_date DESC, h.run_time DESC) as last_run_date,
                (SELECT TOP 1 run_status FROM msdb.dbo.sysjobhistory h
                 WHERE h.job_id = j.job_id AND h.step_id = 0
                 ORDER BY h.run_date DESC, h.run_time DESC) as last_run_status
            FROM msdb.dbo.sysjobs j
            LEFT JOIN msdb.dbo.syscategories c ON j.category_id = c.category_id
            ORDER BY j.name";

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var job = new AgentJobInfo
                {
                    JobId = reader.GetGuid(0),
                    Name = reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                    IsEnabled = Convert.ToInt32(reader[3]) == 1,
                    CategoryName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    OwnerName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CreateDate = reader.GetDateTime(6),
                    ModifyDate = reader.GetDateTime(7),
                    LastRunStatus = reader.IsDBNull(9) ? null : Convert.ToInt32(reader[9]) switch
                    {
                        0 => "Failed",
                        1 => "Succeeded",
                        2 => "Retry",
                        3 => "Canceled",
                        _ => "Unknown"
                    }
                };

                jobs.Add(job);
            }
        }
        catch { /* SQL Agent may not be available */ }

        return jobs;
    }

    public async Task<List<AgentJobStep>> GetJobStepsAsync(Guid jobId)
    {
        var steps = new List<AgentJobStep>();
        if (Connection == null) return steps;

        var sql = @"
            USE msdb;
            SELECT step_id, step_name, subsystem, command, database_name,
                   on_success_action, on_fail_action
            FROM msdb.dbo.sysjobsteps
            WHERE job_id = @jobId
            ORDER BY step_id";

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@jobId", jobId);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                steps.Add(new AgentJobStep
                {
                    StepId = Convert.ToInt32(reader[0]),
                    Name = reader.GetString(1),
                    Subsystem = reader.GetString(2),
                    Command = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    DatabaseName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    OnSuccessAction = Convert.ToInt32(reader[5]) switch
                    {
                        1 => "Quit with success",
                        2 => "Quit with failure",
                        3 => "Go to next step",
                        4 => "Go to step",
                        _ => "Unknown"
                    },
                    OnFailAction = Convert.ToInt32(reader[6]) switch
                    {
                        1 => "Quit with success",
                        2 => "Quit with failure",
                        3 => "Go to next step",
                        4 => "Go to step",
                        _ => "Unknown"
                    }
                });
            }
        }
        catch { }

        return steps;
    }

    public async Task<(bool Success, string? Error)> StartJobAsync(Guid jobId)
    {
        if (Connection == null) return (false, "No active connection");

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "USE msdb; EXEC sp_start_job @job_id = @jobId";
            cmd.Parameters.AddWithValue("@jobId", jobId);
            await cmd.ExecuteNonQueryAsync();
            return (true, null);
        }
        catch (SqlException ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> StopJobAsync(Guid jobId)
    {
        if (Connection == null) return (false, "No active connection");

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "USE msdb; EXEC sp_stop_job @job_id = @jobId";
            cmd.Parameters.AddWithValue("@jobId", jobId);
            await cmd.ExecuteNonQueryAsync();
            return (true, null);
        }
        catch (SqlException ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> EnableJobAsync(Guid jobId, bool enable)
    {
        if (Connection == null) return (false, "No active connection");

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = $"USE msdb; EXEC sp_update_job @job_id = @jobId, @enabled = {(enable ? 1 : 0)}";
            cmd.Parameters.AddWithValue("@jobId", jobId);
            await cmd.ExecuteNonQueryAsync();
            return (true, null);
        }
        catch (SqlException ex)
        {
            return (false, ex.Message);
        }
    }
}
