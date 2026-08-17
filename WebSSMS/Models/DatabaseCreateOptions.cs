namespace WebSSMS.Models;

public enum DatabaseFileType
{
    Rows,
    Log
}

public enum FileGrowthMode
{
    Megabytes,
    Percent
}

public enum DatabaseRecoveryModel
{
    Full,
    BulkLogged,
    Simple
}

public enum DatabaseContainmentType
{
    None,
    Partial
}

public class DatabaseFileSpec
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string LogicalName { get; set; } = string.Empty;
    public DatabaseFileType FileType { get; set; } = DatabaseFileType.Rows;
    public string FileGroup { get; set; } = "PRIMARY";
    public int InitialSizeMB { get; set; } = 8;
    public bool AutoGrowthEnabled { get; set; } = true;
    public FileGrowthMode GrowthMode { get; set; } = FileGrowthMode.Megabytes;
    public int GrowthValue { get; set; } = 64;
    public bool MaxSizeLimited { get; set; }
    public int MaxSizeMB { get; set; } = 100;
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    /// <summary>Matches the "Autogrowth / Maxsize" column SSMS renders in its file grid.</summary>
    public string AutoGrowthDescription
    {
        get
        {
            if (!AutoGrowthEnabled) return "None";
            var by = GrowthMode == FileGrowthMode.Percent ? $"By {GrowthValue} percent" : $"By {GrowthValue} MB";
            var max = MaxSizeLimited ? $"Limited to {MaxSizeMB} MB" : "Unlimited";
            return $"{by}, {max}";
        }
    }

    public string TypeDescription => FileType == DatabaseFileType.Log ? "LOG" : "ROWS Data";
}

public class DatabaseFileGroupSpec
{
    public string Name { get; set; } = string.Empty;
    public bool IsReadOnly { get; set; }
    public bool IsDefault { get; set; }
    public bool IsPrimary { get; set; }
}

public class DatabaseCreateOptions
{
    // General
    public string DatabaseName { get; set; } = string.Empty;
    public string Owner { get; set; } = DefaultOwner;
    public List<DatabaseFileSpec> Files { get; set; } = new();
    public List<DatabaseFileGroupSpec> FileGroups { get; set; } = new();

    // Options
    public string Collation { get; set; } = ServerDefaultCollation;
    public DatabaseRecoveryModel RecoveryModel { get; set; } = DatabaseRecoveryModel.Full;
    public string CompatibilityLevel { get; set; } = string.Empty;
    public DatabaseContainmentType Containment { get; set; } = DatabaseContainmentType.None;
    public bool AutoClose { get; set; }
    public bool AutoShrink { get; set; }
    public bool AutoCreateStatistics { get; set; } = true;
    public bool AutoUpdateStatistics { get; set; } = true;
    public bool IsReadOnly { get; set; }
    public bool Trustworthy { get; set; }
    public string PageVerify { get; set; } = "CHECKSUM";

    public const string DefaultOwner = "<default>";
    public const string ServerDefaultCollation = "<server default>";

    public bool HasExplicitOwner => !string.IsNullOrWhiteSpace(Owner) && Owner != DefaultOwner;
    public bool HasExplicitCollation => !string.IsNullOrWhiteSpace(Collation) && Collation != ServerDefaultCollation;
}
