using Microsoft.Data.SqlClient;
using WebSSMS.Models;
using System.Text;

namespace WebSSMS.Services;

public class ImportExportService
{
    private readonly ConnectionManager _connectionManager;
    private readonly QueryExecutionService _queryService;

    public ImportExportService(ConnectionManager connectionManager, QueryExecutionService queryService)
    {
        _connectionManager = connectionManager;
        _queryService = queryService;
    }

    private SqlConnection? Connection => _connectionManager.ActiveConnection;

    public async Task<(int RowsImported, List<string> Errors)> ImportCsvAsync(
        string database, string schema, string tableName,
        string csvContent, bool hasHeaders = true, string delimiter = ",")
    {
        var errors = new List<string>();
        int rowsImported = 0;

        if (Connection == null)
        {
            errors.Add("No active connection.");
            return (0, errors);
        }

        var lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            errors.Add("CSV content is empty.");
            return (0, errors);
        }

        var startLine = hasHeaders ? 1 : 0;
        var headerLine = hasHeaders ? lines[0] : null;
        var columns = headerLine?.Split(delimiter).Select(c => c.Trim().Trim('"')).ToArray();

        for (int i = startLine; i < lines.Length; i++)
        {
            var values = ParseCsvLine(lines[i], delimiter);
            if (values.Length == 0) continue;

            try
            {
                var sb = new StringBuilder();
                sb.Append($"INSERT INTO [{database}].[{schema}].[{tableName}]");

                if (columns != null)
                    sb.Append($" ({string.Join(", ", columns.Select(c => $"[{c}]"))})");

                sb.Append(" VALUES (");
                sb.Append(string.Join(", ", values.Select(v =>
                    string.IsNullOrEmpty(v) ? "NULL" : $"N'{v.Replace("'", "''")}'")
                ));
                sb.Append(")");

                using var cmd = Connection.CreateCommand();
                cmd.CommandText = sb.ToString();
                await cmd.ExecuteNonQueryAsync();
                rowsImported++;
            }
            catch (Exception ex)
            {
                errors.Add($"Row {i + 1}: {ex.Message}");
            }
        }

        return (rowsImported, errors);
    }

    public async Task<string> ExportToCsvAsync(string database, string schema, string tableName,
        bool includeHeaders = true, string delimiter = ",")
    {
        if (Connection == null) return string.Empty;

        var sb = new StringBuilder();
        var sql = $"SELECT * FROM [{database}].[{schema}].[{tableName}]";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();

        // Write headers
        if (includeHeaders)
        {
            var headers = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                headers.Add(EscapeCsvField(reader.GetName(i), delimiter));
            }
            sb.AppendLine(string.Join(delimiter, headers));
        }

        // Write data
        while (await reader.ReadAsync())
        {
            var fields = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "";
                fields.Add(EscapeCsvField(value, delimiter));
            }
            sb.AppendLine(string.Join(delimiter, fields));
        }

        return sb.ToString();
    }

    public async Task<string> ExportToInsertStatementsAsync(string database, string schema, string tableName)
    {
        if (Connection == null) return string.Empty;

        var sb = new StringBuilder();
        var sql = $"SELECT * FROM [{database}].[{schema}].[{tableName}]";

        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync();

        var columnNames = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columnNames.Add($"[{reader.GetName(i)}]");
        }

        var columnsStr = string.Join(", ", columnNames);

        while (await reader.ReadAsync())
        {
            var values = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (reader.IsDBNull(i))
                {
                    values.Add("NULL");
                }
                else
                {
                    var type = reader.GetFieldType(i);
                    if (type == typeof(string) || type == typeof(DateTime) || type == typeof(Guid))
                        values.Add($"N'{reader.GetValue(i).ToString()!.Replace("'", "''")}'");
                    else if (type == typeof(bool))
                        values.Add(reader.GetBoolean(i) ? "1" : "0");
                    else if (type == typeof(byte[]))
                        values.Add($"0x{BitConverter.ToString((byte[])reader.GetValue(i)).Replace("-", "")}");
                    else
                        values.Add(reader.GetValue(i).ToString()!);
                }
            }

            sb.AppendLine($"INSERT INTO [{schema}].[{tableName}] ({columnsStr}) VALUES ({string.Join(", ", values)})");
        }

        return sb.ToString();
    }

    private string[] ParseCsvLine(string line, string delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (line[i].ToString() == delimiter && !inQuotes)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(line[i]);
            }
        }
        fields.Add(current.ToString().Trim());

        return fields.ToArray();
    }

    private string EscapeCsvField(string field, string delimiter)
    {
        if (field.Contains(delimiter) || field.Contains('"') || field.Contains('\n'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }
}
