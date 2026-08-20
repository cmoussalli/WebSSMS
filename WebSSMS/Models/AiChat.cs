namespace WebSSMS.Models;

public enum AiChatRole
{
    User,
    Assistant,
    System
}

/// <summary>One bubble in the assistant panel.</summary>
public class AiChatEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public AiChatRole Role { get; set; }

    /// <summary>Prose: an answer, a clarifying question, or an error.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The generated script, when the turn produced one.</summary>
    public string? Sql { get; set; }

    /// <summary>The request this answers, kept so the script can be re-inserted with its header.</summary>
    public string? Request { get; set; }

    /// <summary>The lookups the model made before answering, in order.</summary>
    public List<AiToolTrace> Steps { get; set; } = new();

    public bool IsError { get; set; }

    /// <summary>Set once the script has been appended to a query tab.</summary>
    public string? InsertedInto { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.Now;
}

/// <summary>A single read-only lookup the model performed, for the "what it looked at" trail.</summary>
public class AiToolTrace
{
    public string Tool { get; set; } = string.Empty;

    /// <summary>Human-readable form of the arguments, e.g. "AdventureWorks.Sales.Orders".</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>What came back, condensed to a line.</summary>
    public string Result { get; set; } = string.Empty;

    public bool Failed { get; set; }
}

/// <summary>What the model is told about the tab it is writing into.</summary>
public class AiRequestContext
{
    public string? ServerName { get; set; }
    public string? CurrentDatabase { get; set; }
    public List<string> Databases { get; set; } = new();

    /// <summary>Text already in the tab; the generated script is appended after it.</summary>
    public string? CurrentScript { get; set; }
}

/// <summary>The outcome of one question to the model.</summary>
public class AiTurnResult
{
    public string Text { get; set; } = string.Empty;
    public string? Sql { get; set; }
    public List<AiToolTrace> Steps { get; set; } = new();
    public bool IsError { get; set; }

    /// <summary>True when the model asked for clarification instead of producing a script.</summary>
    public bool NeedsClarification => !IsError && string.IsNullOrWhiteSpace(Sql);
}
