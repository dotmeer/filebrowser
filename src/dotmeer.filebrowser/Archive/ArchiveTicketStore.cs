using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace dotmeer.filebrowser.Archive;

/// <summary>
/// Реестр тикетов выбора для скачивания архивом (SPEC §12.2). Тикет —
/// только адресация: он живёт в памяти, теряется при перезапуске и не
/// является разрешением, поэтому эндпоинт повторно проверяет корень,
/// пути и типы объектов. Ограничение размера — защита от накопления
/// выбора в памяти.
/// </summary>
public sealed class ArchiveTicketStore
{
    private readonly ConcurrentDictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    public ArchiveTicketStore(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public int Count => _tickets.Count;

    public string Create(ArchiveSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            SweepExpired();
            while (_tickets.Count >= ArchiveLimits.MaxLiveTickets)
            {
                var oldest = _tickets
                    .OrderBy(pair => pair.Value.CreatedAtUtc)
                    .Select(pair => pair.Key)
                    .FirstOrDefault();
                if (oldest is null || !_tickets.TryRemove(oldest, out _))
                {
                    break;
                }
            }

            _tickets[token] = new Ticket(selection, _timeProvider.GetUtcNow());
        }

        return token;
    }

    /// <summary>
    /// Забрать выбор по тикету. Тикет допускает повторное использование
    /// в пределах TTL: прерванное скачивание не поддерживает докачку,
    /// но повторная попытка должна работать без возврата в UI.
    /// </summary>
    public bool TryRedeem(string token, out ArchiveSelection selection)
    {
        selection = null!;
        if (string.IsNullOrEmpty(token) || !_tickets.TryGetValue(token, out var ticket))
        {
            return false;
        }

        if (_timeProvider.GetUtcNow() - ticket.CreatedAtUtc > ArchiveLimits.TicketLifetime)
        {
            _tickets.TryRemove(token, out _);
            return false;
        }

        selection = ticket.Selection;
        return true;
    }

    private void SweepExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _tickets)
        {
            if (now - pair.Value.CreatedAtUtc > ArchiveLimits.TicketLifetime)
            {
                _tickets.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record Ticket(ArchiveSelection Selection, DateTimeOffset CreatedAtUtc);
}
