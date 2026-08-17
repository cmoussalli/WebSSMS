using Microsoft.Data.SqlClient;
using WebSSMS.Models;

namespace WebSSMS.Services;

public class ConnectionManager : IDisposable
{
    private readonly Dictionary<string, SqlConnection> _connections = new();
    private string? _activeConnectionId;

    public event Action? OnConnectionChanged;
    public event Action<string>? OnError;

    public string? ActiveConnectionId => _activeConnectionId;
    public bool IsConnected => _activeConnectionId != null && _connections.ContainsKey(_activeConnectionId);

    public SqlConnection? ActiveConnection =>
        _activeConnectionId != null && _connections.TryGetValue(_activeConnectionId, out var conn) ? conn : null;

    public IReadOnlyDictionary<string, SqlConnection> Connections => _connections;

    public async Task<(bool Success, string? Error)> ConnectAsync(Models.ConnectionInfo info)
    {
        try
        {
            var connection = new SqlConnection(info.ConnectionString);
            await connection.OpenAsync();

            _connections[info.Id] = connection;
            _activeConnectionId = info.Id;
            OnConnectionChanged?.Invoke();

            return (true, null);
        }
        catch (SqlException ex)
        {
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    public async Task<(bool Success, string? Error)> TestConnectionAsync(Models.ConnectionInfo info)
    {
        try
        {
            using var connection = new SqlConnection(info.ConnectionString);
            await connection.OpenAsync();
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task DisconnectAsync(string connectionId)
    {
        if (_connections.TryGetValue(connectionId, out var connection))
        {
            try
            {
                if (connection.State != System.Data.ConnectionState.Closed)
                    await connection.CloseAsync();
                connection.Dispose();
            }
            catch { }

            _connections.Remove(connectionId);

            if (_activeConnectionId == connectionId)
            {
                _activeConnectionId = _connections.Keys.FirstOrDefault();
            }

            OnConnectionChanged?.Invoke();
        }
    }

    public void SetActiveConnection(string connectionId)
    {
        if (_connections.ContainsKey(connectionId))
        {
            _activeConnectionId = connectionId;
            OnConnectionChanged?.Invoke();
        }
    }

    public async Task<bool> ChangeDatabaseAsync(string databaseName)
    {
        if (ActiveConnection == null) return false;

        try
        {
            await ActiveConnection.ChangeDatabaseAsync(databaseName);
            OnConnectionChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex.Message);
            return false;
        }
    }

    public string? GetCurrentDatabase()
    {
        return ActiveConnection?.Database;
    }

    public async Task<bool> EnsureConnectedAsync()
    {
        if (ActiveConnection == null) return false;

        if (ActiveConnection.State == System.Data.ConnectionState.Broken ||
            ActiveConnection.State == System.Data.ConnectionState.Closed)
        {
            try
            {
                await ActiveConnection.OpenAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        foreach (var conn in _connections.Values)
        {
            try { conn.Dispose(); } catch { }
        }
        _connections.Clear();
    }
}
