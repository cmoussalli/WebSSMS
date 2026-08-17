namespace WebSSMS.Models;

public class BackupOptions
{
    public string DatabaseName { get; set; } = string.Empty;
    public BackupType Type { get; set; } = BackupType.Full;
    public string FilePath { get; set; } = string.Empty;
    public string? BackupName { get; set; }
    public string? Description { get; set; }
    public bool CompressBackup { get; set; } = true;
    public bool CopyOnly { get; set; }
    public bool Checksum { get; set; }
    public bool ContinueAfterError { get; set; }
    public DateTime? ExpirationDate { get; set; }
}

public enum BackupType
{
    Full,
    Differential,
    TransactionLog
}

public class RestoreOptions
{
    public string DatabaseName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public bool WithReplace { get; set; }
    public bool WithRecovery { get; set; } = true;
    public bool WithNoRecovery { get; set; }
    public string? RelocateDataFile { get; set; }
    public string? RelocateLogFile { get; set; }
    public List<BackupFileInfo> BackupFiles { get; set; } = new();
}

public class BackupFileInfo
{
    public string LogicalName { get; set; } = string.Empty;
    public string PhysicalName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long Size { get; set; }
}
