using Microsoft.Data.SqlClient;
using WebSSMS.Models;
using System.Data;
using System.Diagnostics;

namespace WebSSMS.Services;

public class QueryExecutionService
{
    private readonly ConnectionManager _connectionManager;

    public QueryExecutionService(ConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public async Task<QueryResult> ExecuteAsync(string sql, bool includeExecutionPlan = false,
        CancellationToken cancellationToken = default)
    {
        var result = new QueryResult();
        var sw = Stopwatch.StartNew();

        var connection = _connectionManager.ActiveConnection;
        if (connection == null)
        {
            result.HasErrors = true;
            result.Messages.Add(new QueryMessage
            {
                Text = "No active connection.",
                Severity = MessageSeverity.Error
            });
            return result;
        }

        try
        {
            if (!await _connectionManager.EnsureConnectedAsync())
            {
                result.HasErrors = true;
                result.Messages.Add(new QueryMessage
                {
                    Text = "Connection lost. Please reconnect.",
                    Severity = MessageSeverity.Error
                });
                return result;
            }

            // Enable execution plan if requested
            if (includeExecutionPlan)
            {
                using var planCmd = connection.CreateCommand();
                planCmd.CommandText = "SET STATISTICS XML ON";
                await planCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 300; // 5 minutes

            // Capture info messages
            connection.InfoMessage += (sender, e) =>
            {
                result.Messages.Add(new QueryMessage
                {
                    Text = e.Message,
                    Severity = MessageSeverity.Info
                });
            };

            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            do
            {
                var resultSet = new ResultSet();

                // Get column metadata
                var schemaTable = reader.GetColumnSchema();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    // Check if this is an XML execution plan result
                    var colName = reader.GetName(i);
                    if (colName == "Microsoft SQL Server 2005 XML Showplan" ||
                        colName.Contains("XML Showplan"))
                    {
                        // Read execution plan XML
                        if (await reader.ReadAsync(cancellationToken))
                        {
                            result.ExecutionPlanXml = reader.GetString(i);
                        }
                        break;
                    }

                    resultSet.Columns.Add(new ColumnInfo
                    {
                        Name = colName,
                        DataType = reader.GetFieldType(i)?.Name ?? "unknown",
                        ClrType = reader.GetFieldType(i) ?? typeof(string),
                        IsNullable = schemaTable.Count > i && (schemaTable[i].AllowDBNull ?? true),
                        Ordinal = i
                    });
                }

                if (resultSet.Columns.Count == 0)
                {
                    resultSet.RowsAffected = reader.RecordsAffected;
                    if (reader.RecordsAffected >= 0)
                    {
                        result.Messages.Add(new QueryMessage
                        {
                            Text = $"({reader.RecordsAffected} row(s) affected)",
                            Severity = MessageSeverity.Info
                        });
                    }
                }
                else
                {
                    // Read rows
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var row = new object?[reader.FieldCount];
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        }
                        resultSet.Rows.Add(row);
                    }

                    resultSet.RowsAffected = resultSet.Rows.Count;
                    result.Messages.Add(new QueryMessage
                    {
                        Text = $"({resultSet.Rows.Count} row(s) returned)",
                        Severity = MessageSeverity.Info
                    });

                    result.ResultSets.Add(resultSet);
                }
            } while (await reader.NextResultAsync(cancellationToken));

            // Disable execution plan
            if (includeExecutionPlan)
            {
                try
                {
                    using var planOffCmd = connection.CreateCommand();
                    planOffCmd.CommandText = "SET STATISTICS XML OFF";
                    await planOffCmd.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch { }
            }
        }
        catch (OperationCanceledException)
        {
            result.WasCancelled = true;
            result.Messages.Add(new QueryMessage
            {
                Text = "Query execution was cancelled by user.",
                Severity = MessageSeverity.Warning
            });
        }
        catch (SqlException ex)
        {
            result.HasErrors = true;
            foreach (SqlError error in ex.Errors)
            {
                result.Messages.Add(new QueryMessage
                {
                    Text = $"Msg {error.Number}, Level {error.Class}, State {error.State}, Line {error.LineNumber}\n{error.Message}",
                    Severity = error.Class > 10 ? MessageSeverity.Error : MessageSeverity.Warning,
                    LineNumber = error.LineNumber
                });
            }
        }
        catch (Exception ex)
        {
            result.HasErrors = true;
            result.Messages.Add(new QueryMessage
            {
                Text = ex.Message,
                Severity = MessageSeverity.Error
            });
        }

        sw.Stop();
        result.ExecutionTime = sw.Elapsed;
        result.Messages.Add(new QueryMessage
        {
            Text = $"Total execution time: {sw.Elapsed:hh\\:mm\\:ss\\.fff}",
            Severity = MessageSeverity.Info
        });

        return result;
    }

    public async Task<QueryResult> ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken = default)
    {
        var result = new QueryResult();
        var sw = Stopwatch.StartNew();

        var connection = _connectionManager.ActiveConnection;
        if (connection == null)
        {
            result.HasErrors = true;
            result.Messages.Add(new QueryMessage { Text = "No active connection.", Severity = MessageSeverity.Error });
            return result;
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 300;

            var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

            result.Messages.Add(new QueryMessage
            {
                Text = $"({rowsAffected} row(s) affected)",
                Severity = MessageSeverity.Info
            });
        }
        catch (SqlException ex)
        {
            result.HasErrors = true;
            foreach (SqlError error in ex.Errors)
            {
                result.Messages.Add(new QueryMessage
                {
                    Text = $"Msg {error.Number}, Level {error.Class}, State {error.State}, Line {error.LineNumber}\n{error.Message}",
                    Severity = error.Class > 10 ? MessageSeverity.Error : MessageSeverity.Warning,
                    LineNumber = error.LineNumber
                });
            }
        }

        sw.Stop();
        result.ExecutionTime = sw.Elapsed;
        return result;
    }

    public async Task<object?> ExecuteScalarAsync(string sql, CancellationToken cancellationToken = default)
    {
        var connection = _connectionManager.ActiveConnection;
        if (connection == null) return null;

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;

        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
