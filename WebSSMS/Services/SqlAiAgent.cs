using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebSSMS.Models;

namespace WebSSMS.Services;

/// <summary>
/// Turns a request in English into a T-SQL script for the query tab.
///
/// The model is not asked to write from imagination: it gets read-only tools over
/// the connected server (<see cref="SqlAiToolbox"/>) and is told to look up every
/// object it names, and to come back with questions rather than guess when the
/// request is still ambiguous after looking. A turn therefore ends in one of two
/// ways -- a script, or questions.
/// </summary>
public sealed class SqlAiAgent
{
    /// <summary>User/assistant turns kept for follow-ups. Tool traffic is per-turn and not retained.</summary>
    private const int MaxHistoryEntries = 20;

    private const int MaxScriptContextChars = 4000;
    private const int MaxDigestTables = 60;

    private static readonly Regex SqlBlock = new(
        @"```[ \t]*(?:sql|tsql|t-sql)?[ \t]*\r?\n(?<body>.*?)```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly LlmClient _client;
    private readonly SqlAiToolbox _toolbox;
    private readonly AiSettingsProvider _settings;

    private readonly List<LlmMessage> _history = new();

    public SqlAiAgent(LlmClient client, SqlAiToolbox toolbox, AiSettingsProvider settings)
    {
        _client = client;
        _toolbox = toolbox;
        _settings = settings;
    }

    /// <summary>Forgets the conversation. The next question starts clean.</summary>
    public void Reset() => _history.Clear();

    public async Task<AiTurnResult> AskAsync(
        string question,
        AiRequestContext context,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;

        var invalid = settings.Validate();
        if (invalid != null)
            return new AiTurnResult { IsError = true, Text = invalid };

        var steps = new List<AiToolTrace>();

        try
        {
            var messages = new List<LlmMessage>
            {
                LlmMessage.System(await BuildSystemPromptAsync(settings, context, cancellationToken))
            };
            messages.AddRange(_history);
            messages.Add(LlmMessage.User(question));

            var tools = settings.UseTools ? _toolbox.BuildTools(settings) : null;
            var budget = Math.Clamp(settings.MaxToolCalls, 0, 40);

            // One pass per round of lookups, plus the round that answers.
            for (var round = 0; round <= budget; round++)
            {
                progress?.Report(steps.Count == 0 ? "Thinking..." : "Working out the script...");

                var reply = await _client.CompleteAsync(settings, messages, tools, cancellationToken);

                if (reply.ToolCalls.Count == 0)
                {
                    var result = BuildResult(reply.Content ?? string.Empty, steps);
                    Remember(question, reply.Content ?? string.Empty);
                    return result;
                }

                messages.Add(reply);

                foreach (var call in reply.ToolCalls)
                {
                    if (steps.Count >= budget)
                    {
                        messages.Add(LlmMessage.Tool(call.Id,
                            "ERROR: no lookups left for this question. Answer with what you already know, or ask the user."));
                        continue;
                    }

                    progress?.Report(DescribeCall(call));

                    var outcome = await _toolbox.InvokeAsync(call, settings, context.CurrentDatabase, cancellationToken);
                    steps.Add(outcome.Trace);
                    messages.Add(LlmMessage.Tool(call.Id, outcome.Content));

                    progress?.Report($"{Friendly(call.Name)} -- {outcome.Trace.Result}");
                }
            }

            return new AiTurnResult
            {
                IsError = true,
                Steps = steps,
                Text = $"The model kept looking things up without answering (stopped after {budget} lookups). " +
                       "Try a narrower request, or raise the lookup limit in AI settings."
            };
        }
        catch (OperationCanceledException)
        {
            return new AiTurnResult { IsError = true, Steps = steps, Text = "Cancelled." };
        }
        catch (LlmException ex)
        {
            return new AiTurnResult { IsError = true, Steps = steps, Text = ex.Message };
        }
        catch (AiToolException ex)
        {
            return new AiTurnResult { IsError = true, Steps = steps, Text = ex.Message };
        }
        catch (Exception ex)
        {
            return new AiTurnResult { IsError = true, Steps = steps, Text = $"The assistant failed: {ex.Message}" };
        }
    }

    /// <summary>Checks the endpoint answers and reports what it serves.</summary>
    public async Task<(bool Success, string Message)> TestAsync(AiSettings settings, CancellationToken cancellationToken = default)
    {
        var invalid = settings.Validate();
        if (invalid != null) return (false, invalid);

        try
        {
            var models = await _client.ListModelsAsync(settings, cancellationToken);

            if (models.Count == 0)
                return (true, $"Connected to {settings.ResolvedBaseUrl}, but it listed no models.");

            var wanted = settings.ResolvedModel;
            if (models.Any(model => string.Equals(model, wanted, StringComparison.OrdinalIgnoreCase)))
                return (true, $"Connected. '{wanted}' is available ({models.Count} models served).");

            var sample = string.Join(", ", models.Take(8));
            return (false, $"Connected, but '{wanted}' is not in the list. Available: {sample}{(models.Count > 8 ? ", ..." : "")}");
        }
        catch (LlmException ex)
        {
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // --------------------------------------------------------------------- prompt

    private async Task<string> BuildSystemPromptAsync(
        AiSettings settings, AiRequestContext context, CancellationToken cancellationToken)
    {
        var prompt = new StringBuilder();

        prompt.AppendLine("""
            You are the SQL assistant built into WebSSMS, a web-based management studio for Microsoft SQL Server.
            The user describes what they want; you produce one T-SQL script, which WebSSMS appends to the bottom of
            the query tab they currently have open. They read it and run it themselves -- you never execute it.

            ## Ground yourself before you write

            Everything you name must exist. Never invent a table, column, view, procedure, parameter or data type,
            and never assume a naming convention: look it up. A normal sequence is list_tables, then describe_table
            for each object the request touches, then a small run_select if the shape of the data matters (what a
            status column actually contains, whether a join is one-to-many, how dates are stored). Prefer looking
            something up over asking the user about it -- questions are for intent, not for facts you can check.

            ## Ask when the request is genuinely ambiguous

            If, after looking, the request could still reasonably produce more than one script, do NOT guess and do
            NOT write SQL. Reply with plain text only: one line naming what you found, then at most four numbered
            questions. Typical reasons to ask: which of several similar tables is meant; what a vague word like
            "active", "recent" or "top" should mean in this schema; whether a removal is a DELETE or a soft-delete
            flag; which columns the result should have; the grain of an aggregate; how NULLs and duplicates should
            be treated; whether an existing object should be ALTERed or dropped and recreated; which database the
            script targets when it is not the current one.

            Ask once and ask everything at once. When the user answers, write the script -- do not open a second
            round of questions unless the answer itself was ambiguous. If the request is already clear, do not ask
            at all.

            ## The script

            When you are ready, reply with one or two sentences saying what the script does and anything worth
            checking, then exactly one fenced code block, opened with ```sql, containing the whole script and
            nothing else. Never send a fenced block in the same reply as questions: WebSSMS treats any script you
            send as final and appends it to the user's editor immediately.

            Rules for the script itself:
            - T-SQL for Microsoft SQL Server. Schema-qualify every object and bracket identifiers: [dbo].[Orders].
            - Make it runnable as-is at the end of the current tab. Start with USE [database]; and GO when it
              targets a database other than the current one.
            - Put GO between batches wherever T-SQL requires it -- CREATE/ALTER PROCEDURE, VIEW, FUNCTION and
              TRIGGER must each start a batch.
            - Never open a code block for anything but the final script, and do not repeat the script in prose.
            - Do not add your own header comment; WebSSMS writes one. Comment inside the script where the logic
              is not obvious.
            - Give UPDATE and DELETE a WHERE clause. For anything destructive or schema-changing, add a short
              comment saying what it will affect and leave it to the user to run.
            - Do not wrap the script in a transaction unless the user asks for one.
            """);

        prompt.AppendLine();
        prompt.AppendLine("## The connection you are writing for");
        prompt.AppendLine();
        prompt.AppendLine($"- Server: {context.ServerName ?? "unknown"}");
        prompt.AppendLine($"- Database of the current tab: {context.CurrentDatabase ?? "unknown"} (tools default to it)");

        if (context.Databases.Count > 0)
            prompt.AppendLine($"- Databases on this server: {string.Join(", ", context.Databases.Take(40))}");

        if (settings.UseTools)
        {
            prompt.AppendLine();
            prompt.AppendLine(settings.AllowDataQueries
                ? "Your tools are read-only: they list and describe objects, and run a single SELECT to sample data. Nothing you call can change anything."
                : "Your tools are read-only and cover structure only -- reading rows is switched off, so do not ask to run SELECTs.");
        }
        else
        {
            // No function calling: one shot at the schema, up front.
            prompt.AppendLine();
            prompt.AppendLine("You have no lookup tools in this mode. Work only from the schema below, and ask the user about anything it does not cover.");
            prompt.AppendLine();

            try
            {
                prompt.AppendLine(await _toolbox.BuildSchemaDigestAsync(
                    context.CurrentDatabase, MaxDigestTables, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                prompt.AppendLine($"(The schema could not be read: {ex.Message})");
            }
        }

        if (!string.IsNullOrWhiteSpace(context.CurrentScript))
        {
            var script = context.CurrentScript.Trim();
            var truncated = script.Length > MaxScriptContextChars;
            if (truncated) script = script[^MaxScriptContextChars..];

            prompt.AppendLine();
            prompt.AppendLine("## What is already in the tab");
            prompt.AppendLine();
            prompt.AppendLine("Your script is appended after this. Match its style, and treat it as context when the");
            prompt.AppendLine("user says \"the query above\" or \"that table\".");
            prompt.AppendLine();
            if (truncated) prompt.AppendLine("(earlier lines omitted)");
            prompt.AppendLine("```sql");
            prompt.AppendLine(script);
            prompt.AppendLine("```");
        }

        return prompt.ToString();
    }

    // --------------------------------------------------------------------- result

    private static AiTurnResult BuildResult(string content, List<AiToolTrace> steps)
    {
        var result = new AiTurnResult { Steps = steps };

        var match = SqlBlock.Match(content);
        if (match.Success)
        {
            result.Sql = match.Groups["body"].Value.Trim('\r', '\n').TrimEnd();
            result.Text = SqlBlock.Replace(content, string.Empty).Trim();
        }
        else
        {
            result.Text = content.Trim();
        }

        if (string.IsNullOrWhiteSpace(result.Text) && result.Sql == null)
        {
            result.IsError = true;
            result.Text = "The model returned an empty reply. Try again, or check the model and endpoint in AI settings.";
        }
        else if (string.IsNullOrWhiteSpace(result.Text))
        {
            result.Text = "Here is the script.";
        }

        return result;
    }

    private void Remember(string question, string answer)
    {
        _history.Add(LlmMessage.User(question));
        _history.Add(new LlmMessage { Role = "assistant", Content = answer });

        // Only whole exchanges are kept, so a tool message can never be orphaned
        // from the assistant turn that asked for it -- which some APIs reject.
        while (_history.Count > MaxHistoryEntries)
            _history.RemoveRange(0, 2);
    }

    // ------------------------------------------------------------------- progress

    private static string Friendly(string tool) => tool switch
    {
        SqlAiToolbox.ListDatabasesTool => "Listed databases",
        SqlAiToolbox.ListTablesTool => "Listed tables",
        SqlAiToolbox.DescribeTableTool => "Read structure",
        SqlAiToolbox.ListRoutinesTool => "Listed routines",
        SqlAiToolbox.GetObjectDefinitionTool => "Read definition",
        SqlAiToolbox.RunSelectTool => "Sampled data",
        _ => tool
    };

    private static string DescribeCall(LlmToolCall call)
    {
        var target = TargetOf(call);

        return call.Name switch
        {
            SqlAiToolbox.ListDatabasesTool => "Listing databases...",
            SqlAiToolbox.ListTablesTool => "Looking for tables...",
            SqlAiToolbox.DescribeTableTool => $"Reading the structure of {target ?? "a table"}...",
            SqlAiToolbox.ListRoutinesTool => "Listing procedures and functions...",
            SqlAiToolbox.GetObjectDefinitionTool => $"Reading the definition of {target ?? "an object"}...",
            SqlAiToolbox.RunSelectTool => target == null ? "Looking at the data..." : $"Looking at the data: {target}",
            _ => $"Calling {call.Name}..."
        };
    }

    /// <summary>Pulls the interesting argument out of a call for the progress line.</summary>
    private static string? TargetOf(LlmToolCall call)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var name in new[] { "table", "name", "purpose" })
            {
                if (document.RootElement.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // A malformed argument blob is the tool's problem, not the progress line's.
        }

        return null;
    }
}

/// <summary>
/// Puts the required header on a generated script. Every block WebSSMS drops into
/// a query tab says where it came from, what was asked for, and that nobody has
/// run it yet.
/// </summary>
public static class AiScriptFormatter
{
    private const string Rule = "-- ============================================================================";
    private const int WrapAt = 90;

    public static string Wrap(string sql, string request, AiSettings settings)
    {
        var header = new StringBuilder();

        header.AppendLine(Rule);
        header.AppendLine("-- Generated by the WebSSMS AI assistant -- review before running.");
        header.AppendLine($"-- Model:   {settings.ResolvedModel} ({AiSettings.DisplayName(settings.Provider)})");
        header.AppendLine($"-- Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var label = "-- Request: ";
        foreach (var line in WrapRequest(request))
        {
            header.AppendLine(label + line);
            label = "--          ";
        }

        header.AppendLine(Rule);

        return header + sql.Trim('\r', '\n').TrimEnd() + Environment.NewLine;
    }

    /// <summary>Folds the request into comment-width lines, keeping the user's own line breaks.</summary>
    private static IEnumerable<string> WrapRequest(string request)
    {
        var normalized = request.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (normalized.Length == 0) yield return "(none)";

        foreach (var paragraph in normalized.Split('\n'))
        {
            var remaining = paragraph.Trim();
            if (remaining.Length == 0) continue;

            while (remaining.Length > WrapAt)
            {
                var cut = remaining.LastIndexOf(' ', Math.Min(WrapAt, remaining.Length - 1));
                if (cut <= 0) cut = WrapAt;

                yield return remaining[..cut].TrimEnd();
                remaining = remaining[cut..].TrimStart();
            }

            if (remaining.Length > 0) yield return remaining;
        }
    }
}
