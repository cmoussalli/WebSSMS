namespace WebSSMS.Models;

public class QueryResult
{
    public List<ResultSet> ResultSets { get; set; } = new();
    public List<QueryMessage> Messages { get; set; } = new();
    public string? ExecutionPlanXml { get; set; }
    public TimeSpan ExecutionTime { get; set; }
    public bool HasErrors { get; set; }
    public bool WasCancelled { get; set; }
}

public class ResultSet
{
    public List<ColumnInfo> Columns { get; set; } = new();
    public List<object?[]> Rows { get; set; } = new();
    public int RowsAffected { get; set; }
}

public class ColumnInfo
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public Type ClrType { get; set; } = typeof(string);
    public bool IsNullable { get; set; }
    public int MaxLength { get; set; }
    public int Ordinal { get; set; }
}

public class QueryMessage
{
    public string Text { get; set; } = string.Empty;
    public MessageSeverity Severity { get; set; }
    public int? LineNumber { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public enum MessageSeverity
{
    Info,
    Warning,
    Error
}

public class QueryTab
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "SQLQuery1.sql";
    public string Content { get; set; } = string.Empty;
    public string? SelectedDatabase { get; set; }
    public QueryResult? LastResult { get; set; }
    public bool IsExecuting { get; set; }
    public bool IsDirty { get; set; }
    public CancellationTokenSource? CancellationSource { get; set; }
}
