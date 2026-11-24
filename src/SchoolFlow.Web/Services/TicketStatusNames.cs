using Microsoft.Extensions.Caching.Memory;
using SchoolFlow.Core.Freshservice;

namespace SchoolFlow.Web.Services;

/// <summary>
/// The help desk's ticket statuses by number, custom ones included (owner, Nov 24, 2026, queue #21), so a
/// ticket set to "Pending client info" shows that name. Read from Freshservice at most once a day; when the
/// help desk can't be reached the standard four are used and it is tried again after 10 minutes.
/// </summary>
public sealed class TicketStatusNames
{
    private const string Key = "ticket-statuses";
    private readonly IFreshserviceClient _freshservice;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TicketStatusNames> _log;

    public TicketStatusNames(IFreshserviceClient freshservice, IMemoryCache cache, ILogger<TicketStatusNames> log)
    {
        _freshservice = freshservice;
        _cache = cache;
        _log = log;
    }

    public async Task<IReadOnlyList<(int Value, string Label)>> AllAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(Key, out IReadOnlyList<(int Value, string Label)>? hit) && hit is not null) return hit;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(6));
            var list = TicketFields.WithStandard(await _freshservice.TicketStatusesAsync(limit.Token));
            _cache.Set(Key, list, TimeSpan.FromDays(1));
            return list;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Could not read the ticket statuses from the help desk: {Message}", ex.Message);
            _cache.Set(Key, (IReadOnlyList<(int Value, string Label)>)TicketFields.Statuses, TimeSpan.FromMinutes(10));
            return TicketFields.Statuses;
        }
    }

    /// <summary>Forget the list (after "Show ticket fields" on the Settings page) so the next read is fresh.</summary>
    public void Forget() => _cache.Remove(Key);
}
