using System.Collections.Concurrent;
using WebSSMS.Models;

namespace WebSSMS.Services;

/// <summary>
/// Process-wide handoff between a Blazor circuit and the plain HTTP transfer
/// endpoints. The circuit builds a ticket (it has the connection and the user's
/// intent); the endpoint redeems it (it has the response stream).
///
/// Singleton on purpose: the HTTP request that redeems a ticket runs in a
/// different DI scope than the circuit that issued it.
/// </summary>
public sealed class BackupTransferTicketStore
{
    private readonly ConcurrentDictionary<string, BackupTransferTicket> _tickets = new();

    public void Add(BackupTransferTicket ticket)
    {
        Sweep();
        _tickets[ticket.Id] = ticket;
    }

    public BackupTransferTicket? Take(string id, BackupTransferKind kind)
    {
        Sweep();

        if (!_tickets.TryGetValue(id, out var ticket)) return null;
        if (ticket.Kind != kind) return null;
        if (ticket.IsExpired(DateTimeOffset.UtcNow))
        {
            _tickets.TryRemove(id, out _);
            return null;
        }

        return ticket;
    }

    /// <summary>
    /// Uploads are one-shot -- the destination file is written, so a replay would
    /// overwrite it. Downloads stay redeemable until they expire so the browser
    /// can retry a dropped connection.
    /// </summary>
    public void Remove(string id) => _tickets.TryRemove(id, out _);

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _tickets)
        {
            if (pair.Value.IsExpired(now))
                _tickets.TryRemove(pair.Key, out _);
        }
    }
}
