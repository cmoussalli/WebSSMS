using Microsoft.Data.SqlClient;
using System.Text;
using WebSSMS.Models;

namespace WebSSMS.Services;

public class DatabaseAdminService
{
    private readonly ConnectionManager _connectionManager;

    public DatabaseAdminService(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    private SqlConnection? Connection => _connectionManager.ActiveConnection;

    /// <summary>Default data/log directories the instance uses for new databases.</summary>
    public async Task<(string DataPath, string LogPath)> GetDefaultFilePathsAsync()
    {
        if (Connection == null) return ("", "");

        var sql = @"
            SELECT
                CONVERT(nvarchar(512), SERVERPROPERTY('InstanceDefaultDataPath')) AS DataPath,
                CONVERT(nvarchar(512), SERVERPROPERTY('InstanceDefaultLogPath')) AS LogPath,
                (SELECT TOP 1 physical_name FROM sys.master_files WHERE database_id = 1 AND type = 0) AS MasterData,
                (SELECT TOP 1 physical_name FROM sys.master_files WHERE database_id = 1 AND type = 1) AS MasterLog";

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = sql;

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var dataPath = reader.IsDBNull(0) ? null : reader.GetString(0);
                var logPath = reader.IsDBNull(1) ? null : reader.GetString(1);

                // Older instances return NULL for the InstanceDefault* properties; fall back to
                // wherever master lives, which is the convention SSMS uses too.
                if (string.IsNullOrWhiteSpace(dataPath) && !reader.IsDBNull(2))
                    dataPath = GetDirectoryName(reader.GetString(2));
                if (string.IsNullOrWhiteSpace(logPath) && !reader.IsDBNull(3))
                    logPath = GetDirectoryName(reader.GetString(3));

                return (EnsureTrailingSeparator(dataPath ?? ""), EnsureTrailingSeparator(logPath ?? ""));
            }
        }
        catch { }

        return ("", "");
    }

    public async Task<List<string>> GetCollationsAsync()
    {
        var collations = new List<string>();
        if (Connection == null) return collations;

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sys.fn_helpcollations() ORDER BY name";

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                collations.Add(reader.GetString(0));
            }
        }
        catch { }

        return collations;
    }

    /// <summary>
    /// Compatibility levels the instance accepts, newest first, along with the level the server
    /// gives a new database by default.
    /// </summary>
    public async Task<(List<int> Levels, int ServerDefault)> GetCompatibilityLevelsAsync()
    {
        var serverDefault = 0;
        if (Connection != null)
        {
            try
            {
                using var cmd = Connection.CreateCommand();
                cmd.CommandText = "SELECT compatibility_level FROM sys.databases WHERE database_id = 1";
                var value = await cmd.ExecuteScalarAsync();
                if (value != null && value != DBNull.Value)
                    serverDefault = Convert.ToInt32(value);
            }
            catch { }
        }

        var known = new[] { 170, 160, 150, 140, 130, 120, 110, 100, 90, 80 };
        var levels = serverDefault > 0
            ? known.Where(l => l <= serverDefault).ToList()
            : known.ToList();

        if (serverDefault > 0 && !levels.Contains(serverDefault))
            levels.Insert(0, serverDefault);

        return (levels, serverDefault);
    }

    public async Task<bool> DatabaseExistsAsync(string databaseName)
    {
        if (Connection == null || string.IsNullOrWhiteSpace(databaseName)) return false;

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sys.databases WHERE name = @name";
            cmd.Parameters.AddWithValue("@name", databaseName);
            return await cmd.ExecuteScalarAsync() != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Server principals offered in the Owner picker.</summary>
    public async Task<List<string>> GetLoginNamesAsync()
    {
        var logins = new List<string>();
        if (Connection == null) return logins;

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = @"
                SELECT name FROM sys.server_principals
                WHERE type IN ('S', 'U', 'G') AND name NOT LIKE '##%'
                ORDER BY name";

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                logins.Add(reader.GetString(0));
            }
        }
        catch { }

        return logins;
    }

    /// <summary>A starting point for the dialog that mirrors SSMS's defaults.</summary>
    public DatabaseCreateOptions BuildDefaultOptions(string dataPath, string logPath)
    {
        var options = new DatabaseCreateOptions();
        options.FileGroups.Add(new DatabaseFileGroupSpec { Name = "PRIMARY", IsDefault = true, IsPrimary = true });
        options.Files.Add(new DatabaseFileSpec
        {
            FileType = DatabaseFileType.Rows,
            FileGroup = "PRIMARY",
            Path = dataPath
        });
        options.Files.Add(new DatabaseFileSpec
        {
            FileType = DatabaseFileType.Log,
            FileGroup = string.Empty,
            Path = logPath
        });
        return options;
    }

    /// <summary>
    /// Renames the default files to track the database name, the way SSMS does while you type.
    /// Only files the user has not renamed themselves are touched.
    /// </summary>
    public void ApplyDatabaseNameToFiles(DatabaseCreateOptions options, string previousName)
    {
        var newName = options.DatabaseName;

        foreach (var file in options.Files)
        {
            var suffix = file.FileType == DatabaseFileType.Log ? "_log" : "";
            var extension = file.FileType == DatabaseFileType.Log ? ".ldf" : ".mdf";

            var expectedLogical = string.IsNullOrEmpty(previousName) ? "" : previousName + suffix;
            if (file.LogicalName == expectedLogical)
                file.LogicalName = string.IsNullOrEmpty(newName) ? "" : newName + suffix;

            var expectedFileName = string.IsNullOrEmpty(previousName) ? "" : previousName + suffix + extension;
            if (file.FileName == expectedFileName)
                file.FileName = string.IsNullOrEmpty(newName) ? "" : newName + suffix + extension;
        }
    }

    public List<string> Validate(DatabaseCreateOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.DatabaseName))
        {
            errors.Add("Enter a name for the database.");
        }
        else if (options.DatabaseName.IndexOfAny(new[] { '\'', '"', '[', ']' }) >= 0)
        {
            errors.Add("The database name contains characters that are not allowed.");
        }

        var rowsFiles = options.Files.Where(f => f.FileType == DatabaseFileType.Rows).ToList();
        if (rowsFiles.Count == 0)
            errors.Add("The database must have at least one data file.");

        if (!options.Files.Any(f => f.FileType == DatabaseFileType.Log))
            errors.Add("The database must have at least one log file.");

        if (rowsFiles.Count > 0 && !rowsFiles.Any(f => IsPrimary(f.FileGroup)))
            errors.Add("At least one data file must belong to the PRIMARY filegroup.");

        foreach (var file in options.Files)
        {
            var label = string.IsNullOrWhiteSpace(file.LogicalName) ? "(unnamed file)" : file.LogicalName;

            if (string.IsNullOrWhiteSpace(file.LogicalName))
                errors.Add("Every file needs a logical name.");

            if (string.IsNullOrWhiteSpace(file.FileName))
                errors.Add($"Enter a file name for '{label}'.");

            if (string.IsNullOrWhiteSpace(file.Path))
                errors.Add($"Enter a path for '{label}'.");

            if (file.InitialSizeMB <= 0)
                errors.Add($"The initial size for '{label}' must be greater than zero.");

            if (file.AutoGrowthEnabled && file.GrowthValue <= 0)
                errors.Add($"The autogrowth increment for '{label}' must be greater than zero.");

            if (file.MaxSizeLimited && file.MaxSizeMB <= file.InitialSizeMB)
                errors.Add($"The maximum size for '{label}' must be larger than its initial size.");

            if (file.FileType == DatabaseFileType.Rows && string.IsNullOrWhiteSpace(file.FileGroup))
                errors.Add($"Choose a filegroup for '{label}'.");
        }

        foreach (var name in Duplicates(options.Files.Where(f => !string.IsNullOrWhiteSpace(f.LogicalName)).Select(f => f.LogicalName)))
            errors.Add($"The logical file name '{name}' is used more than once.");

        foreach (var name in Duplicates(options.FileGroups.Select(g => g.Name)))
            errors.Add($"The filegroup name '{name}' is used more than once.");

        return errors.Distinct().ToList();
    }

    /// <summary>
    /// The statements needed to create the database, one per batch. They are joined with GO for
    /// the Script button and executed one at a time by <see cref="CreateDatabaseAsync"/>, because
    /// CREATE DATABASE has to be the only statement in its batch.
    /// </summary>
    public List<string> BuildCreateDatabaseBatches(DatabaseCreateOptions options)
    {
        var nl = Environment.NewLine;
        var db = Quote(options.DatabaseName);
        var batches = new List<string>();

        var create = new StringBuilder();
        create.Append($"CREATE DATABASE {db}");

        if (options.Containment == DatabaseContainmentType.Partial)
            create.Append($"{nl} CONTAINMENT = PARTIAL");

        var rowsFiles = options.Files.Where(f => f.FileType == DatabaseFileType.Rows).ToList();
        var logFiles = options.Files.Where(f => f.FileType == DatabaseFileType.Log).ToList();

        if (rowsFiles.Count > 0)
        {
            // Data files are grouped by filegroup, PRIMARY first, the way SSMS emits them.
            var groups = rowsFiles
                .GroupBy(f => IsPrimary(f.FileGroup) ? "PRIMARY" : f.FileGroup, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => IsPrimary(g.Key) ? 0 : 1)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => (IsPrimary(g.Key) ? " ON  PRIMARY " : $" FILEGROUP {Quote(g.Key)} ")
                             + nl + string.Join($",{nl}", g.Select(FileClause)));

            create.Append(nl + string.Join($",{nl}", groups));
        }

        if (logFiles.Count > 0)
        {
            create.Append($"{nl} LOG ON {nl}");
            create.Append(string.Join($",{nl}", logFiles.Select(FileClause)));
        }

        if (options.HasExplicitCollation)
            create.Append($"{nl} COLLATE {options.Collation}");

        batches.Add(create.ToString());

        if (!string.IsNullOrWhiteSpace(options.CompatibilityLevel))
            batches.Add($"ALTER DATABASE {db} SET COMPATIBILITY_LEVEL = {options.CompatibilityLevel}");

        batches.Add($"ALTER DATABASE {db} SET RECOVERY {RecoveryClause(options.RecoveryModel)}");
        batches.Add($"ALTER DATABASE {db} SET AUTO_CLOSE {OnOff(options.AutoClose)}");
        batches.Add($"ALTER DATABASE {db} SET AUTO_SHRINK {OnOff(options.AutoShrink)}");
        batches.Add($"ALTER DATABASE {db} SET AUTO_CREATE_STATISTICS {OnOff(options.AutoCreateStatistics)}");
        batches.Add($"ALTER DATABASE {db} SET AUTO_UPDATE_STATISTICS {OnOff(options.AutoUpdateStatistics)}");

        if (!string.IsNullOrWhiteSpace(options.PageVerify))
            batches.Add($"ALTER DATABASE {db} SET PAGE_VERIFY {options.PageVerify}");

        if (options.Trustworthy)
            batches.Add($"ALTER DATABASE {db} SET TRUSTWORTHY ON");

        // Filegroup flags can only be applied once the filegroups exist and hold files.
        foreach (var fileGroup in options.FileGroups.Where(g => !g.IsPrimary && g.IsReadOnly))
            batches.Add($"ALTER DATABASE {db} MODIFY FILEGROUP {Quote(fileGroup.Name)} READ_ONLY");

        var defaultFileGroup = options.FileGroups.FirstOrDefault(g => g.IsDefault && !g.IsPrimary);
        if (defaultFileGroup != null)
            batches.Add($"ALTER DATABASE {db} MODIFY FILEGROUP {Quote(defaultFileGroup.Name)} DEFAULT");

        if (options.HasExplicitOwner)
            batches.Add($"ALTER AUTHORIZATION ON DATABASE::{db} TO {Quote(options.Owner)}");

        // READ_ONLY goes last: once it is on, no further metadata changes are allowed.
        if (options.IsReadOnly)
            batches.Add($"ALTER DATABASE {db} SET READ_ONLY");

        return batches;
    }

    public string GenerateCreateDatabaseScript(DatabaseCreateOptions options)
    {
        var sb = new StringBuilder();
        foreach (var batch in BuildCreateDatabaseBatches(options))
        {
            sb.AppendLine(batch);
            sb.AppendLine("GO");
        }
        return sb.ToString();
    }

    public async Task<QueryResult> CreateDatabaseAsync(DatabaseCreateOptions options, Action<string>? onProgress = null)
    {
        var result = new QueryResult();

        var connection = Connection;
        if (connection == null)
        {
            result.HasErrors = true;
            result.Messages.Add(new QueryMessage { Text = "No active connection.", Severity = MessageSeverity.Error });
            return result;
        }

        void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
        {
            onProgress?.Invoke(e.Message);
            result.Messages.Add(new QueryMessage { Text = e.Message, Severity = MessageSeverity.Info });
        }

        connection.InfoMessage += OnInfoMessage;

        try
        {
            foreach (var batch in BuildCreateDatabaseBatches(options))
            {
                onProgress?.Invoke(batch);

                using var cmd = connection.CreateCommand();
                cmd.CommandText = batch;
                cmd.CommandTimeout = 0;
                await cmd.ExecuteNonQueryAsync();
            }

            result.Messages.Add(new QueryMessage
            {
                Text = $"Database '{options.DatabaseName}' created successfully.",
                Severity = MessageSeverity.Info
            });
        }
        catch (SqlException ex)
        {
            result.HasErrors = true;
            result.Messages.Add(new QueryMessage { Text = ex.Message, Severity = MessageSeverity.Error });
        }
        catch (Exception ex)
        {
            result.HasErrors = true;
            result.Messages.Add(new QueryMessage { Text = ex.Message, Severity = MessageSeverity.Error });
        }
        finally
        {
            connection.InfoMessage -= OnInfoMessage;
        }

        return result;
    }

    private static string FileClause(DatabaseFileSpec file)
    {
        var parts = new List<string>
        {
            $"NAME = N'{Literal(file.LogicalName)}'",
            $"FILENAME = N'{Literal(CombinePath(file.Path, file.FileName))}'",
            $"SIZE = {ToKb(file.InitialSizeMB)}KB"
        };

        if (file.MaxSizeLimited)
            parts.Add($"MAXSIZE = {ToKb(file.MaxSizeMB)}KB");
        else if (file.AutoGrowthEnabled)
            parts.Add("MAXSIZE = UNLIMITED");

        if (file.AutoGrowthEnabled)
        {
            parts.Add(file.GrowthMode == FileGrowthMode.Percent
                ? $"FILEGROWTH = {file.GrowthValue}%"
                : $"FILEGROWTH = {ToKb(file.GrowthValue)}KB");
        }
        else
        {
            parts.Add("FILEGROWTH = 0");
        }

        return $"( {string.Join(" , ", parts)} )";
    }

    private static string RecoveryClause(DatabaseRecoveryModel model) => model switch
    {
        DatabaseRecoveryModel.Simple => "SIMPLE",
        DatabaseRecoveryModel.BulkLogged => "BULK_LOGGED",
        _ => "FULL"
    };

    private static IEnumerable<string> Duplicates(IEnumerable<string> values) =>
        values.GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
              .Where(g => g.Count() > 1)
              .Select(g => g.Key);

    private static bool IsPrimary(string? fileGroup) =>
        string.IsNullOrWhiteSpace(fileGroup) || fileGroup.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase);

    private static long ToKb(int megabytes) => (long)megabytes * 1024;

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    private static string Literal(string value) => value.Replace("'", "''");

    private static string CombinePath(string directory, string fileName) =>
        string.IsNullOrWhiteSpace(directory) ? fileName : EnsureTrailingSeparator(directory) + fileName;

    private static string EnsureTrailingSeparator(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (path.EndsWith('\\') || path.EndsWith('/')) return path;
        return path + (path.Contains('\\') ? '\\' : '/');
    }

    private static string GetDirectoryName(string physicalPath)
    {
        var index = physicalPath.LastIndexOfAny(new[] { '\\', '/' });
        return index < 0 ? physicalPath : physicalPath.Substring(0, index + 1);
    }
}
