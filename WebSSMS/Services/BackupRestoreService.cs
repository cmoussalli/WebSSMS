using Microsoft.Data.SqlClient;
using WebSSMS.Models;
using System.Text;
using static WebSSMS.Services.BackupFileService;

namespace WebSSMS.Services;

public class BackupRestoreService
{
    private readonly ConnectionManager _connectionManager;

    public BackupRestoreService(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    private SqlConnection? Connection => _connectionManager.ActiveConnection;

    public async Task<QueryResult> BackupDatabaseAsync(BackupOptions options, Action<string>? onProgress = null)
    {
        var result = new QueryResult();
        if (Connection == null)
        {
            result.HasErrors = true;
            result.Messages.Add(new QueryMessage { Text = "No active connection.", Severity = MessageSeverity.Error });
            return result;
        }

        var sb = new StringBuilder();
        sb.Append($"BACKUP {(options.Type == BackupType.TransactionLog ? "LOG" : "DATABASE")} [{options.DatabaseName}]");
        sb.AppendLine($" TO DISK = N'{QuoteLiteral(options.FilePath)}'");

        var withClauses = new List<string>();
        if (options.Type == BackupType.Differential) withClauses.Add("DIFFERENTIAL");
        if (options.CopyOnly) withClauses.Add("COPY_ONLY");
        if (options.CompressBackup) withClauses.Add("COMPRESSION");
        if (options.Checksum) withClauses.Add("CHECKSUM");
        if (options.ContinueAfterError) withClauses.Add("CONTINUE_AFTER_ERROR");
        if (!string.IsNullOrEmpty(options.BackupName)) withClauses.Add($"NAME = N'{QuoteLiteral(options.BackupName)}'");
        if (!string.IsNullOrEmpty(options.Description)) withClauses.Add($"DESCRIPTION = N'{QuoteLiteral(options.Description)}'");
        withClauses.Add("STATS = 10");

        if (withClauses.Count > 0)
            sb.AppendLine($"WITH {string.Join(", ", withClauses)}");

        try
        {
            Connection.InfoMessage += (_, e) =>
            {
                onProgress?.Invoke(e.Message);
                result.Messages.Add(new QueryMessage { Text = e.Message, Severity = MessageSeverity.Info });
            };

            using var cmd = Connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync();

            result.Messages.Add(new QueryMessage
            {
                Text = $"Backup of '{options.DatabaseName}' completed successfully.",
                Severity = MessageSeverity.Info
            });
        }
        catch (SqlException ex)
        {
            result.HasErrors = true;
            result.Messages.Add(new QueryMessage { Text = ex.Message, Severity = MessageSeverity.Error });
        }

        return result;
    }

    public async Task<QueryResult> RestoreDatabaseAsync(RestoreOptions options, Action<string>? onProgress = null)
    {
        var result = new QueryResult();
        if (Connection == null)
        {
            result.HasErrors = true;
            result.Messages.Add(new QueryMessage { Text = "No active connection.", Severity = MessageSeverity.Error });
            return result;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"RESTORE DATABASE [{options.DatabaseName}]");
        sb.AppendLine($"FROM DISK = N'{QuoteLiteral(options.FilePath)}'");

        var withClauses = new List<string>();
        if (options.WithReplace) withClauses.Add("REPLACE");
        if (options.WithRecovery) withClauses.Add("RECOVERY");
        if (options.WithNoRecovery) withClauses.Add("NORECOVERY");
        if (!string.IsNullOrEmpty(options.RelocateDataFile))
            withClauses.Add($"MOVE N'{QuoteLiteral(options.BackupFiles.FirstOrDefault(f => f.Type == "D")?.LogicalName ?? string.Empty)}' TO N'{QuoteLiteral(options.RelocateDataFile)}'");
        if (!string.IsNullOrEmpty(options.RelocateLogFile))
            withClauses.Add($"MOVE N'{QuoteLiteral(options.BackupFiles.FirstOrDefault(f => f.Type == "L")?.LogicalName ?? string.Empty)}' TO N'{QuoteLiteral(options.RelocateLogFile)}'");
        withClauses.Add("STATS = 10");

        if (withClauses.Count > 0)
            sb.AppendLine($"WITH {string.Join(", ", withClauses)}");

        try
        {
            // Set database to single user first
            try
            {
                using var singleUserCmd = Connection.CreateCommand();
                singleUserCmd.CommandText = $"ALTER DATABASE [{options.DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE";
                singleUserCmd.CommandTimeout = 30;
                await singleUserCmd.ExecuteNonQueryAsync();
            }
            catch { /* Database may not exist yet */ }

            Connection.InfoMessage += (_, e) =>
            {
                onProgress?.Invoke(e.Message);
                result.Messages.Add(new QueryMessage { Text = e.Message, Severity = MessageSeverity.Info });
            };

            using var cmd = Connection.CreateCommand();
            cmd.CommandText = sb.ToString();
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync();

            // Set back to multi user
            try
            {
                using var multiUserCmd = Connection.CreateCommand();
                multiUserCmd.CommandText = $"ALTER DATABASE [{options.DatabaseName}] SET MULTI_USER";
                await multiUserCmd.ExecuteNonQueryAsync();
            }
            catch { }

            result.Messages.Add(new QueryMessage
            {
                Text = $"Restore of '{options.DatabaseName}' completed successfully.",
                Severity = MessageSeverity.Info
            });
        }
        catch (SqlException ex)
        {
            result.HasErrors = true;
            result.Messages.Add(new QueryMessage { Text = ex.Message, Severity = MessageSeverity.Error });
        }

        return result;
    }

    public async Task<List<BackupFileInfo>> GetBackupFileListAsync(string filePath)
    {
        var files = new List<BackupFileInfo>();
        if (Connection == null) return files;

        var sql = $"RESTORE FILELISTONLY FROM DISK = N'{QuoteLiteral(filePath)}'";
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            files.Add(new BackupFileInfo
            {
                LogicalName = reader["LogicalName"].ToString()!,
                PhysicalName = reader["PhysicalName"].ToString()!,
                Type = reader["Type"].ToString()!,
                Size = Convert.ToInt64(reader["Size"])
            });
        }
        return files;
    }

    public string GenerateBackupScript(BackupOptions options)
    {
        var sb = new StringBuilder();
        sb.Append($"BACKUP {(options.Type == BackupType.TransactionLog ? "LOG" : "DATABASE")} [{options.DatabaseName}]");
        sb.AppendLine($" TO DISK = N'{QuoteLiteral(options.FilePath)}'");

        var withClauses = new List<string>();
        if (options.Type == BackupType.Differential) withClauses.Add("DIFFERENTIAL");
        if (options.CopyOnly) withClauses.Add("COPY_ONLY");
        if (options.CompressBackup) withClauses.Add("COMPRESSION");
        if (!string.IsNullOrEmpty(options.BackupName)) withClauses.Add($"NAME = N'{QuoteLiteral(options.BackupName)}'");
        withClauses.Add("STATS = 10");

        if (withClauses.Count > 0)
            sb.AppendLine($"WITH {string.Join(", ", withClauses)}");

        sb.AppendLine("GO");
        return sb.ToString();
    }
}
