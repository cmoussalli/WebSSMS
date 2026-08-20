using System.Text;
using System.Text.RegularExpressions;

namespace WebSSMS.Services;

/// <summary>
/// Keeps the AI assistant's own queries to reading.
///
/// The assistant is allowed to look at the database before it writes a script --
/// that is the whole point -- but nothing it sends may change anything. Only a
/// single SELECT (or a CTE that ends in one) gets through, and
/// <see cref="SqlAiToolbox"/> still runs it inside a transaction it always rolls
/// back. Scripts the assistant *generates* are not run here at all; they land in
/// the user's query tab for the user to read and execute.
/// </summary>
public static class ReadOnlySqlGuard
{
    /// <summary>
    /// Anything that writes, executes, or reaches outside the current session.
    /// Matched against the statement with comments and literals stripped, so a
    /// row whose value happens to be 'drop' does not trip it.
    /// </summary>
    private static readonly Regex Forbidden = new(
        @"\b(INSERT|UPDATE|DELETE|MERGE|TRUNCATE|DROP|CREATE|ALTER|RENAME|GRANT|REVOKE|DENY|EXEC|EXECUTE|SP_EXECUTESQL|BACKUP|RESTORE|SHUTDOWN|RECONFIGURE|CHECKPOINT|KILL|DBCC|WAITFOR|OPENROWSET|OPENDATASOURCE|OPENQUERY|OPENXML|BULK|INTO|SET|USE|DECLARE|BEGIN|COMMIT|ROLLBACK|SAVE|GO)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns null when <paramref name="sql"/> is a single read-only statement, or the reason it is not.</summary>
    public static string? Validate(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "No query was supplied.";

        var stripped = StripCommentsAndLiterals(sql).Trim();

        // A trailing terminator is fine; one in the middle means a second statement.
        stripped = stripped.TrimEnd(';', ' ', '\t', '\r', '\n');
        if (stripped.Contains(';'))
            return "Only a single SELECT statement is allowed -- no statement batches.";

        if (stripped.Length == 0)
            return "The query is empty once comments are removed.";

        var firstWord = FirstWord(stripped);
        if (!firstWord.Equals("SELECT", StringComparison.OrdinalIgnoreCase) &&
            !firstWord.Equals("WITH", StringComparison.OrdinalIgnoreCase))
        {
            return $"Only SELECT queries are allowed here; this one starts with '{firstWord}'.";
        }

        var forbidden = Forbidden.Match(stripped);
        if (forbidden.Success)
            return $"'{forbidden.Value.ToUpperInvariant()}' is not allowed -- the assistant may only read.";

        return null;
    }

    private static string FirstWord(string sql)
    {
        var match = Regex.Match(sql, @"[A-Za-z_][A-Za-z0-9_]*");
        return match.Success ? match.Value : sql[..Math.Min(sql.Length, 12)];
    }

    /// <summary>
    /// Blanks out line and block comments, string literals and quoted identifiers,
    /// leaving the statement's structure intact for the keyword checks above.
    /// </summary>
    private static string StripCommentsAndLiterals(string sql)
    {
        var result = new StringBuilder(sql.Length);

        for (int i = 0; i < sql.Length; i++)
        {
            var c = sql[i];

            // -- line comment
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                result.Append('\n');
                continue;
            }

            // /* block comment */, which nests in T-SQL
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var depth = 1;
                i += 2;
                while (i < sql.Length && depth > 0)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { depth++; i += 2; continue; }
                    if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { depth--; i += 2; continue; }
                    i++;
                }
                i--;
                result.Append(' ');
                continue;
            }

            // 'literal', with '' as the escape
            if (c == '\'')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'') { i += 2; continue; }
                        break;
                    }
                    i++;
                }
                result.Append("''");
                continue;
            }

            // [quoted identifier] -- a column may legitimately be called [Delete]
            if (c == '[')
            {
                i++;
                while (i < sql.Length && sql[i] != ']') i++;
                result.Append("id");
                continue;
            }

            // "quoted identifier" under QUOTED_IDENTIFIER ON
            if (c == '"')
            {
                i++;
                while (i < sql.Length && sql[i] != '"') i++;
                result.Append("id");
                continue;
            }

            result.Append(c);
        }

        return result.ToString();
    }
}
