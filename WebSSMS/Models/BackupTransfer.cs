namespace WebSSMS.Models;

/// <summary>
/// How the bytes of a backup file travel between the browser and the machine
/// that actually holds the file.
/// </summary>
public enum BackupTransferMode
{
    /// <summary>The web app can open the path itself (same host, UNC share, mounted volume).</summary>
    FileSystem,

    /// <summary>Only SQL Server can see the path, so the bytes are pulled through the SQL connection.</summary>
    SqlServer
}

public enum BackupTransferKind
{
    Download,
    Upload
}

/// <summary>
/// A single-purpose, short-lived pass to the transfer endpoints.
///
/// The browser never sends a file path to the server -- it sends a ticket id, and
/// the ticket carries the already-validated path plus a delegate that does the
/// actual copying. That keeps <c>/api/backup/download</c> from becoming an
/// arbitrary-file-read hole, and keeps SQL credentials inside the process.
/// </summary>
public sealed class BackupTransferTicket
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public BackupTransferKind Kind { get; init; }
    public BackupTransferMode Mode { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;

    /// <summary>Known or estimated length in bytes, or null when neither is available.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>
    /// True only when <see cref="SizeBytes"/> is the exact byte count of what will be
    /// sent. The SQL transport can only estimate from backup history, and an
    /// estimate must never become a Content-Length -- the browser would stop reading
    /// early and write a truncated, unrestorable .bak.
    /// </summary>
    public bool SizeIsExact { get; init; }

    /// <summary>Upper bound enforced on uploads; 0 means unlimited.</summary>
    public long MaxBytes { get; init; }

    public DateTimeOffset ExpiresUtc { get; init; }

    /// <summary>Download: writes the file into the response body.</summary>
    public Func<Stream, CancellationToken, Task>? WriteToAsync { get; init; }

    /// <summary>Upload: drains the request body into the destination file.</summary>
    public Func<Stream, CancellationToken, Task<long>>? ReadFromAsync { get; init; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresUtc;
}

/// <summary>One backup file discovered on the server, ready to be downloaded.</summary>
public sealed class BackupFileEntry
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;

    /// <summary>Null when the size is not knowable without reading the whole file.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>False when the size came from backup history rather than the file itself.</summary>
    public bool SizeIsExact { get; set; } = true;

    public DateTime? ModifiedUtc { get; set; }

    public string SizeDisplay => SizeBytes is null
        ? "--"
        : SizeIsExact ? FormatSize(SizeBytes.Value) : "~" + FormatSize(SizeBytes.Value);

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}

/// <summary>Result of listing a directory, including how the listing was obtained.</summary>
public sealed class BackupDirectoryListing
{
    public string Directory { get; set; } = string.Empty;
    public List<BackupFileEntry> Files { get; set; } = new();
    public BackupTransferMode Mode { get; set; } = BackupTransferMode.FileSystem;
    public bool CanUpload { get; set; }
    public string? Error { get; set; }
    public string? Note { get; set; }
}

/// <summary>Bound from the "BackupStorage" configuration section.</summary>
public sealed class BackupStorageOptions
{
    public const string SectionName = "BackupStorage";

    /// <summary>
    /// Directories the transfer endpoints may touch. Empty means "any directory,
    /// as long as the extension is allowed" -- convenient out of the box, but set
    /// this in any shared deployment to pin transfers to a known folder.
    /// </summary>
    public List<string> AllowedDirectories { get; set; } = new();

    /// <summary>The set used when <see cref="AllowedExtensions"/> is left empty.</summary>
    public static readonly IReadOnlyList<string> DefaultExtensions =
        new[] { ".bak", ".trn", ".dif", ".bkp" };

    /// <summary>
    /// Empty means "use <see cref="DefaultExtensions"/>". Deliberately not seeded
    /// with those defaults: the configuration binder *appends* to a List that
    /// already has entries, so seeding here would both duplicate every value and
    /// make it impossible to narrow the list from configuration.
    /// </summary>
    public List<string> AllowedExtensions { get; set; } = new();

    /// <summary>Allow pulling a backup through the SQL connection when the app cannot see the path.</summary>
    public bool AllowSqlServerTransfer { get; set; } = true;

    public bool AllowUpload { get; set; } = true;

    /// <summary>0 means unlimited.</summary>
    public long MaxUploadBytes { get; set; }

    public int TicketLifetimeMinutes { get; set; } = 30;

    /// <summary>Pre-filled in the UI; falls back to the instance default backup path.</summary>
    public string? DefaultDirectory { get; set; }
}
