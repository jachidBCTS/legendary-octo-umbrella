using Microsoft.Extensions.Caching.Memory;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Freshservice;

namespace SchoolFlow.Web.Services;

/// <summary>
/// The live help desk ticket shown on a request page (owner, Nov 23, 2026): its status and the public
/// replies. Read when the page opens, cached for 2 minutes per ticket; never includes private notes.
/// Shown to everyone who can open the request.
/// </summary>
public sealed record TicketView(long Id, string Status, string Css, IReadOnlyList<TicketReply> Replies, bool Unavailable)
{
    public static async Task<TicketView> LoadAsync(IFreshserviceClient freshservice, IMemoryCache cache, long ticketId, CancellationToken ct)
    {
        var key = "ticket-view:" + ticketId;
        if (cache.TryGetValue(key, out TicketView? cached) && cached is not null) return cached;
        TicketView view;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(6));
            var ticket = await freshservice.GetTicketAsync(ticketId, limit.Token);
            if (ticket is null) view = new TicketView(ticketId, "Not found", "muted", Array.Empty<TicketReply>(), false);
            else
            {
                var number = Values.TryToNumber(ticket.GetValueOrDefault("status"), out var n) ? (int)n : 0;
                var label = TicketFields.StatusLabel(number);
                var css = number switch { 4 or 5 => "ok", 3 => "warn", _ => "info" };
                var replies = await freshservice.PublicRepliesAsync(ticketId, limit.Token);
                view = new TicketView(ticketId, label, css, replies, false);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The help desk being slow or down must not break the request page; try again in a minute.
            view = new TicketView(ticketId, "", "muted", Array.Empty<TicketReply>(), true);
            cache.Set(key, view, TimeSpan.FromMinutes(1));
            return view;
        }
        cache.Set(key, view, TimeSpan.FromMinutes(2));
        return view;
    }
}
