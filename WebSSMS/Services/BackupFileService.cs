using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Data;
using WebSSMS.Models;

namespace WebSSMS.Services;

/// <summary>
/// Moves backup files between the browser and wherever SQL Server keeps them.
///
/// Two transports, picked per file:
///
///   FileSystem -- the app process can open the path (SQL Server on the same host,
///     a UNC share both machines can reach, or a volume mounted into both
///     containers). Streams at full speed, no size ceiling, and it is the only
///     transport that can accept an upload.
///
///   SqlServer -- the app cannot see the path, so bytes are pulled through the SQL
///     connection with OPENROWSET(BULK ..., SINGLE_BLOB). Needs ADMINISTER BULK
///     OPERATIONS, and caps out at 2 GB because that is the varbinary(max) limit.
///     T-SQL has no matching write path, so uploads still need a reachable folder.
///
/// Every path here belongs to SQL Server's machine, which may run a different OS
/// than this app -- see <see cref="ServerPath"/>.
/// </summary>
public sealed class BackupFileService
{
    private const int CopyBufferSize = 1024 * 1024;

    private readonly ConnectionManager _connectionManager;
    private readonly BackupTransferTicketStore _tickets;
    private readonly BackupStorageOptions _options;
    private readonly HashSet<string> _allowedExtensions;

    public BackupFileService(
        ConnectionManager connectionManager,
        BackupTransferTicketStore tickets,
        IOptions<BackupStorageOptions> options)
    {
        _connectionManager = connectionManager;
        _tickets = tickets;
        _options = options.Value;

        // Cast the first branch so both arms share IEnumerable<string> outright,
        // rather than leaning on target-typed conditionals.
        _allowedExtensions = new HashSet<string>(
            _options.AllowedExtensions.Count > 0
                ? (IEnumerable<string>)_options.AllowedExtensions
                : BackupStorageOptions.DefaultExtensions,
            StringComparer.OrdinalIgnoreCase);
    }

    public BackupStorageOptions Options => _options;

    /// <summary>The resolved extension allow-list, for populating file pickers.</summary>
    public IReadOnlyCollection<string> AllowedExtensions => _allowedExtensions;

    private bool IsAllowedExtension(string extension) => _allowedExtensions.Contains(extension);

    private SqlConnection? Connection => _connectionManager.ActiveConnection;

    /// <summary>Escapes a value for embedding in an N'...' literal.</summary>
    public static string QuoteLiteral(string value) => value.Replace("'", "''");

    // ---------------------------------------------------------------- validation

    /// <summary>
    /// Guards the transfer endpoints against being turned into a general-purpose
    /// file reader for the host. A path has to normalise cleanly, live under an
    /// allowed root (when roots are configured), and carry a backup extension.
    /// </summary>
    public string? ValidateFilePath(string? path, out string canonical)
    {
        canonical = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
            return "No file path was supplied.";

        var style = ServerPath.DetectStyle(path);

        var normalized = ServerPath.Normalize(path, style, out var error);
        if (normalized == null)
            return error;

        var extension = ServerPath.GetExtension(normalized);
        if (string.IsNullOrEmpty(extension) || !IsAllowedExtension(extension))
        {
            return $"'{extension}' is not an allowed backup file type. Allowed: {string.Join(", ", _allowedExtensions)}.";
        }

        var directory = ServerPath.GetDirectoryName(normalized);
        if (string.IsNullOrEmpty(directory))
            return "The file path does not include a directory.";

        var directoryError = ValidateDirectory(directory, out _);
        if (directoryError != null) return directoryError;

        canonical = normalized;
        return null;
    }

    public string? ValidateDirectory(string? directory, out string canonical)
    {
        canonical = string.Empty;

        if (string.IsNullOrWhiteSpace(directory))
            return "No directory was supplied.";

        var style = ServerPath.DetectStyle(directory);

        var normalized = ServerPath.Normalize(directory, style, out var error);
        if (normalized == null) return error;

        normalized = ServerPath.TrimTrailingSeparator(normalized);
        canonical = normalized;

        // No configured roots means "anywhere, but only backup files" -- the
        // extension check is what keeps appsettings.json out of reach.
        if (_options.AllowedDirectories.Count == 0)
            return null;

        foreach (var root in _options.AllowedDirectories)
        {
            var rootStyle = ServerPath.DetectStyle(root);
            if (rootStyle != style) continue;

            var normalizedRoot = ServerPath.Normalize(root, rootStyle, out _);
            if (normalizedRoot == null) continue;

            if (ServerPath.IsWithin(normalized, normalizedRoot, style))
                return null;
        }

        canonical = string.Empty;
        return $"'{normalized}' is outside the directories this server allows backup transfers for.";
    }

    // ------------------------------------------------------------------- listing

    /// <summary>
    /// The instance's default backup folder, used to pre-fill the UI. Configuration
    /// wins; otherwise ask SQL Server (SERVERPROPERTY on 2019+, registry before that).
    /// </summary>
    public async Task<string?> GetDefaultBackupDirectoryAsync()
    {
        if (!string.IsNullOrWhiteSpace(_options.DefaultDirectory))
            return _options.DefaultDirectory;

        if (Connection == null) return null;

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultBackupPath'))";
            cmd.CommandTimeout = 15;
            var value = await cmd.ExecuteScalarAsync();
            if (value is string path && !string.IsNullOrWhiteSpace(path))
                return path;
        }
        catch { /* older instance -- fall through */ }

        try
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = @"
DECLARE @path nvarchar(4000);
EXEC master.dbo.xp_instance_regread
     N'HKEY_LOCAL_MACHINE', N'Software\Microsoft\MSSQLServer\MSSQLServer',
     N'BackupDirectory', @path OUTPUT;
SELECT @path;";
            cmd.CommandTimeout = 15;
            var value = await cmd.ExecuteScalarAsync();
            if (value is string path && !string.IsNullOrWhiteSpace(path))
                return path;
        }
        catch { /* no permission -- caller just gets no default */ }

        return null;
    }

    public async Task<BackupDirectoryListing> ListDirectoryAsync(string directory)
    {
        var listing = new BackupDirectoryListing { Directory = directory };

        var error = ValidateDirectory(directory, out var canonical);
        if (error != null)
        {
            listing.Error = error;
            return listing;
        }

        listing.Directory = canonical;

        // Preferred path: the app can see the folder, so we get sizes and
        // timestamps for free -- and uploads become possible.
        if (ServerPath.IsLocalToThisHost(canonical) && Directory.Exists(canonical))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(canonical))
                {
                    var extension = Path.GetExtension(file);
                    if (!IsAllowedExtension(extension)) continue;

                    var info = new FileInfo(file);
                    listing.Files.Add(new BackupFileEntry
                    {
                        Name = info.Name,
                        FullPath = info.FullName,
                        SizeBytes = info.Length,
                        ModifiedUtc = info.LastWriteTimeUtc
                    });
                }

                listing.Mode = BackupTransferMode.FileSystem;
                listing.CanUpload = _options.AllowUpload && IsDirectoryWritable(canonical);
                if (!listing.CanUpload && _options.AllowUpload)
                    listing.Note = "This folder is readable but not writable by the web app, so uploads are disabled.";

                listing.Files = listing.Files.OrderByDescending(f => f.ModifiedUtc).ToList();
                return listing;
            }
            catch (Exception ex)
            {
                listing.Error = $"Could not read the directory: {ex.Message}";
                return listing;
            }
        }

        // Fallback: ask SQL Server what is in the folder on its own machine.
        if (!_options.AllowSqlServerTransfer)
        {
            listing.Error = $"'{canonical}' is not reachable from the web app, and SQL Server transfer is disabled.";
            return listing;
        }

        if (Connection == null)
        {
            listing.Error = "No active connection.";
            return listing;
        }

        try
        {
            listing.Files = await ListDirectoryViaSqlAsync(canonical);
            listing.Mode = BackupTransferMode.SqlServer;
            listing.CanUpload = false;
            listing.Note = "Listed through SQL Server -- the web app cannot see this path directly. " +
                           "Downloads stream over the SQL connection (2 GB limit); uploads need a shared folder.";
            return listing;
        }
        catch (SqlException ex)
        {
            listing.Error = $"SQL Server could not list '{canonical}': {ex.Message}";
            return listing;
        }
    }

    private async Task<List<BackupFileEntry>> ListDirectoryViaSqlAsync(string directory)
    {
        var connection = Connection!;
        var style = ServerPath.DetectStyle(directory);
        var names = new List<string>();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "EXEC master.sys.xp_dirtree @path, 1, 1";
            cmd.CommandTimeout = 60;
            cmd.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 4000) { Value = directory });

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // xp_dirtree returns subdirectory / depth / file, where file = 1
                // marks a leaf. Directories come back with file = 0.
                if (Convert.ToInt32(reader["file"]) != 1) continue;

                var name = reader["subdirectory"].ToString()!;
                var extension = ServerPath.GetExtension(name);
                if (!IsAllowedExtension(extension)) continue;

                names.Add(name);
            }
        }

        // xp_dirtree has no size column, so borrow what msdb remembers about
        // backups written to this folder. Anything it has never seen shows "--".
        var history = await GetSizesFromHistoryAsync(directory);
        var files = new List<BackupFileEntry>();

        foreach (var name in names)
        {
            var fullPath = ServerPath.Combine(directory, name, style);
            history.TryGetValue(fullPath, out var known);

            files.Add(new BackupFileEntry
            {
                Name = name,
                FullPath = fullPath,
                SizeBytes = known.Size,
                SizeIsExact = false,
                ModifiedUtc = known.Finished
            });
        }

        return files
            .OrderByDescending(f => f.ModifiedUtc ?? DateTime.MinValue)
            .ThenBy(f => f.Name)
            .ToList();
    }

    /// <summary>
    /// Backup history as a display hint only. Note that compressed_backup_size is
    /// the size of the backup *data*, which runs a little short of the size of the
    /// file on disk -- close enough to show a user, never close enough to use as a
    /// Content-Length.
    /// </summary>
    private async Task<Dictionary<string, (long? Size, DateTime? Finished)>> GetSizesFromHistoryAsync(string directory)
    {
        var style = ServerPath.DetectStyle(directory);
        var map = new Dictionary<string, (long?, DateTime?)>(
            style == ServerPathStyle.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var connection = Connection;
        if (connection == null) return map;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT  mf.physical_device_name,
        MAX(COALESCE(bs.compressed_backup_size, bs.backup_size)) AS size_bytes,
        MAX(bs.backup_finish_date)                               AS finished
FROM    msdb.dbo.backupmediafamily AS mf
JOIN    msdb.dbo.backupset         AS bs ON bs.media_set_id = mf.media_set_id
WHERE   mf.physical_device_name LIKE @prefix
GROUP BY mf.physical_device_name";
            cmd.CommandTimeout = 60;

            var escaped = ServerPath.TrimTrailingSeparator(directory)
                .Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
            var prefix = escaped + ServerPath.SeparatorFor(style) + "%";
            cmd.Parameters.Add(new SqlParameter("@prefix", SqlDbType.NVarChar, 4000) { Value = prefix });

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var path = reader.GetString(0);
                long? size = reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1));
                DateTime? finished = reader.IsDBNull(2)
                    ? null
                    : DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
                map[path] = (size, finished);
            }
        }
        catch { /* msdb history is a nicety, not a requirement */ }

        return map;
    }

    private static bool IsDirectoryWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".webssms-write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ download

    public async Task<(BackupTransferTicket? Ticket, string? Error)> CreateDownloadTicketAsync(string filePath)
    {
        var error = ValidateFilePath(filePath, out var canonical);
        if (error != null) return (null, error);

        var fileName = ServerPath.GetFileName(canonical);

        if (ServerPath.IsLocalToThisHost(canonical) && File.Exists(canonical))
        {
            var ticket = new BackupTransferTicket
            {
                Kind = BackupTransferKind.Download,
                Mode = BackupTransferMode.FileSystem,
                FilePath = canonical,
                FileName = fileName,
                SizeBytes = new FileInfo(canonical).Length,
                SizeIsExact = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(_options.TicketLifetimeMinutes),
                WriteToAsync = (output, ct) => CopyFileToAsync(canonical, output, ct)
            };
            _tickets.Add(ticket);
            return (ticket, null);
        }

        if (!_options.AllowSqlServerTransfer)
            return (null, $"'{canonical}' is not reachable from the web app, and SQL Server transfer is disabled.");

        var connectionInfo = _connectionManager.ActiveConnectionInfo;
        if (Connection == null || connectionInfo == null)
            return (null, "No active connection.");

        if (!await FileExistsOnServerAsync(canonical))
            return (null, $"SQL Server cannot see a file at '{canonical}'.");

        // The delegate closes over the connection string only, so it stays valid
        // after the circuit that issued the ticket has gone away, and it never
        // borrows the circuit's SqlConnection mid-query.
        var connectionString = connectionInfo.ConnectionString;
        var sqlTicket = new BackupTransferTicket
        {
            Kind = BackupTransferKind.Download,
            Mode = BackupTransferMode.SqlServer,
            FilePath = canonical,
            FileName = fileName,

            // Only ever a hint from msdb: the true file is a little larger than the
            // backup data it records. The endpoint must not turn this into a
            // Content-Length or the browser would truncate the download.
            SizeBytes = await GetSizeFromHistoryAsync(canonical),
            SizeIsExact = false,

            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(_options.TicketLifetimeMinutes),
            WriteToAsync = (output, ct) => StreamFileThroughSqlAsync(connectionString, canonical, output, ct)
        };
        _tickets.Add(sqlTicket);
        return (sqlTicket, null);
    }

    private async Task<long?> GetSizeFromHistoryAsync(string filePath)
    {
        var directory = ServerPath.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory)) return null;

        var history = await GetSizesFromHistoryAsync(directory);
        return history.TryGetValue(filePath, out var known) ? known.Size : null;
    }

    private async Task<bool> FileExistsOnServerAsync(string path)
    {
        try
        {
            using var cmd = Connection!.CreateCommand();
            cmd.CommandText = @"
DECLARE @result table (file_exists int, is_directory int, parent_exists int);
INSERT INTO @result EXEC master.dbo.xp_fileexist @path;
SELECT file_exists FROM @result;";
            cmd.CommandTimeout = 30;
            cmd.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 4000) { Value = path });

            var value = await cmd.ExecuteScalarAsync();
            return value != null && Convert.ToInt32(value) == 1;
        }
        catch
        {
            // xp_fileexist may be blocked; let the actual read produce the error.
            return true;
        }
    }

    private static async Task CopyFileToAsync(string path, Stream output, CancellationToken ct)
    {
        await using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await input.CopyToAsync(output, CopyBufferSize, ct);
    }

    /// <summary>
    /// Pulls the file over the SQL connection. SequentialAccess plus GetStream keeps
    /// this a real stream on the client side -- the whole backup never lands in the
    /// web app's memory. SQL Server still materialises it as a varbinary(max), which
    /// is where the 2 GB ceiling comes from.
    /// </summary>
    private static async Task StreamFileThroughSqlAsync(
        string connectionString, string path, Stream output, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"SELECT BulkColumn FROM OPENROWSET(BULK N'{QuoteLiteral(path)}', SINGLE_BLOB) AS backup_file";
        cmd.CommandTimeout = 0;

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        if (!await reader.ReadAsync(ct)) return;
        if (await reader.IsDBNullAsync(0, ct)) return;

        await using var blob = reader.GetStream(0);
        await blob.CopyToAsync(output, CopyBufferSize, ct);
    }

    // -------------------------------------------------------------------- upload

    /// <summary>
    /// Uploads only work over the filesystem transport: T-SQL can read a file off
    /// the server's disk but has no supported way to write an arbitrary one back.
    /// </summary>
    public (BackupTransferTicket? Ticket, string? Error) CreateUploadTicket(
        string directory, string fileName, bool overwrite)
    {
        if (!_options.AllowUpload)
            return (null, "Uploading backup files is disabled on this server.");

        if (string.IsNullOrWhiteSpace(fileName))
            return (null, "No file name was supplied.");

        var safeName = fileName.Trim();
        if (safeName.IndexOfAny(new[] { '\\', '/' }) >= 0)
            return (null, "The file name must not contain a path.");

        if (safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || safeName is "." or "..")
            return (null, "The file name contains invalid characters.");

        var directoryError = ValidateDirectory(directory, out var canonicalDirectory);
        if (directoryError != null) return (null, directoryError);

        var style = ServerPath.DetectStyle(canonicalDirectory);
        var target = ServerPath.Combine(canonicalDirectory, safeName, style);

        var fileError = ValidateFilePath(target, out var canonicalTarget);
        if (fileError != null) return (null, fileError);

        if (!ServerPath.IsLocalToThisHost(canonicalTarget) || !Directory.Exists(canonicalDirectory))
        {
            return (null,
                $"The web app cannot write to '{canonicalDirectory}'. Uploads need a folder both the app and " +
                "SQL Server can reach -- a shared UNC path, or a volume mounted into both containers.");
        }

        if (!IsDirectoryWritable(canonicalDirectory))
            return (null, $"The web app has no write permission on '{canonicalDirectory}'.");

        if (File.Exists(canonicalTarget) && !overwrite)
            return (null, $"'{safeName}' already exists in that folder. Tick 'Overwrite' to replace it.");

        var maxBytes = _options.MaxUploadBytes;
        var ticket = new BackupTransferTicket
        {
            Kind = BackupTransferKind.Upload,
            Mode = BackupTransferMode.FileSystem,
            FilePath = canonicalTarget,
            FileName = safeName,
            MaxBytes = maxBytes,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(_options.TicketLifetimeMinutes),
            ReadFromAsync = (input, ct) => SaveUploadAsync(canonicalTarget, maxBytes, input, ct)
        };
        _tickets.Add(ticket);
        return (ticket, null);
    }

    /// <summary>
    /// Writes to a sibling .uploading file and moves it into place at the end, so a
    /// dropped connection cannot leave a half-written .bak that looks restorable.
    /// </summary>
    private static async Task<long> SaveUploadAsync(string target, long maxBytes, Stream input, CancellationToken ct)
    {
        var staging = target + ".uploading";
        long written = 0;

        try
        {
            await using (var output = new FileStream(
                staging, FileMode.Create, FileAccess.Write, FileShare.None,
                CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[CopyBufferSize];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    written += read;
                    if (maxBytes > 0 && written > maxBytes)
                    {
                        throw new InvalidOperationException(
                            $"Upload exceeds the {BackupFileEntry.FormatSize(maxBytes)} limit configured for this server.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                await output.FlushAsync(ct);
            }

            File.Move(staging, target, overwrite: true);
            return written;
        }
        catch
        {
            try { if (File.Exists(staging)) File.Delete(staging); } catch { }
            throw;
        }
    }
}
