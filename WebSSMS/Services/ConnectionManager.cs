using Microsoft.Data.SqlClient;
using WebSSMS.Models;

namespace WebSSMS.Services;

public class ConnectionManager : IDisposable
{
    private readonly Dictionary<string, SqlConnection> _connections = new();
    private readonly Dictionary<string, Models.ConnectionInfo> _connectionInfos = new();
    private string? _activeConnectionId;

    public event Action? OnConnectionChanged;
    public event Action<string>? OnError;

    public string? ActiveConnectionId => _activeConnectionId;
    public bool IsConnected => _activeConnectionId != null && _connections.ContainsKey(_activeConnectionId);

    public SqlConnection? ActiveConnection =>
        _activeConnectionId != null && _connections.TryGetValue(_activeConnectionId, out var conn) ? conn : null;

    public IReadOnlyDictionary<string, SqlConnection> Connections => _connections;

    /// <summary>
    /// The credentials behind the active connection. Kept because background work
    /// (streaming a backup file out through SQL Server, for instance) needs its own
    /// connection rather than sharing the circuit's -- SqlConnection is not
    /// thread-safe, and a long transfer would block every other query in the tab.
    /// </summary>
    public Models.ConnectionInfo? ActiveConnectionInfo =>
        _activeConnectionId != null && _connectionInfos.TryGetValue(_activeConnectionId, out var info) ? info : null;

    public async Task<(bool Success, string? Error)> ConnectAsync(Models.ConnectionInfo info)
    {
        try
        {
            var connection = new SqlConnection(info.ConnectionString);
            await connection.OpenAsync();

            _connections[info.Id] = connection;
            _connectionInfos[info.Id] = info;
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
            _connectionInfos.Remove(connectionId);

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
        _connectionInfos.Clear();
    }
}
