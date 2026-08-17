namespace WebSSMS.Models;

public class ServerInfo
{
    public string ServerName { get; set; } = string.Empty;
    public string ProductVersion { get; set; } = string.Empty;
    public string ProductLevel { get; set; } = string.Empty;
    public string Edition { get; set; } = string.Empty;
    public string EngineEdition { get; set; } = string.Empty;
    public string Collation { get; set; } = string.Empty;
    public bool IsClustered { get; set; }
    public bool IsHadrEnabled { get; set; }
    public int ProcessorCount { get; set; }
    public long PhysicalMemoryMB { get; set; }
    public string OsVersion { get; set; } = string.Empty;
    public DateTime ServerStartTime { get; set; }
}

public class DatabaseInfo
{
    public string Name { get; set; } = string.Empty;
    public int DatabaseId { get; set; }
    public string State { get; set; } = string.Empty;
    public string RecoveryModel { get; set; } = string.Empty;
    public string CompatibilityLevel { get; set; } = string.Empty;
    public string Collation { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public DateTime CreateDate { get; set; }
    public double SizeMB { get; set; }
    public double SpaceAvailableMB { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsAutoShrink { get; set; }
    public bool IsAutoClose { get; set; }
    public string? DefaultFileGroup { get; set; }
    public List<DatabaseFile> Files { get; set; } = new();
}

public class DatabaseFile
{
    public string Name { get; set; } = string.Empty;
    public string PhysicalName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public double SizeMB { get; set; }
    public double MaxSizeMB { get; set; }
    public string Growth { get; set; } = string.Empty;
    public string FileGroup { get; set; } = string.Empty;
}

public class ActiveProcess
{
    public int Spid { get; set; }
    public string Status { get; set; } = string.Empty;
    public string LoginName { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public int CpuTime { get; set; }
    public long DiskIO { get; set; }
    public string? LastBatch { get; set; }
    public string ProgramName { get; set; } = string.Empty;
    public string? SqlText { get; set; }
    public int BlockedBy { get; set; }
    public int WaitTime { get; set; }
    public string? WaitType { get; set; }
}

public class AgentJobInfo
{
    public Guid JobId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
    public string? CategoryName { get; set; }
    public string? OwnerName { get; set; }
    public DateTime? LastRunDate { get; set; }
    public string? LastRunStatus { get; set; }
    public DateTime? NextRunDate { get; set; }
    public DateTime CreateDate { get; set; }
    public DateTime ModifyDate { get; set; }
    public List<AgentJobStep> Steps { get; set; } = new();
    public List<AgentJobSchedule> Schedules { get; set; } = new();
}

public class AgentJobStep
{
    public int StepId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Subsystem { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string? DatabaseName { get; set; }
    public string OnSuccessAction { get; set; } = string.Empty;
    public string OnFailAction { get; set; } = string.Empty;
}

public class AgentJobSchedule
{
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string FrequencyType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime? NextRunDate { get; set; }
}

public class ExecutionPlanNode
{
    public string NodeId { get; set; } = string.Empty;
    public string PhysicalOp { get; set; } = string.Empty;
    public string LogicalOp { get; set; } = string.Empty;
    public double EstimateRows { get; set; }
    public double EstimateCPU { get; set; }
    public double EstimateIO { get; set; }
    public double SubtreeCost { get; set; }
    public int EstimatedRowSize { get; set; }
    public double TotalCostPercentage { get; set; }
    public string? ObjectName { get; set; }
    public string? OutputList { get; set; }
    public string? Warnings { get; set; }
    public List<ExecutionPlanNode> Children { get; set; } = new();
}
