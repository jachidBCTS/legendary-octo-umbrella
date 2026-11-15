using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

public sealed record LocationSyncResult(int Added, int Renamed, int SwitchedOff, int Total, string? Skipped = null)
{
    public string Describe() => Skipped ?? (Added + Renamed + SwitchedOff == 0
        ? $"Already up to date ({Total} location{(Total == 1 ? "" : "s")} in Freshservice)."
        : $"Synced {Total} location{(Total == 1 ? "" : "s")} from Freshservice: {Added} added, {Renamed} renamed, {SwitchedOff} switched off.");
}

/// <summary>Copies Freshservice's locations into the Locations table.</summary>
public static class LocationSync
{
    /// <summary>
    /// Adds locations that are new in Freshservice, renames ones whose name changed, and switches off
    /// ones that no longer exist there. Locations added by hand are never touched, and a location an
    /// admin switched off stays off.
    /// </summary>
    public static async Task<LocationSyncResult> RunAsync(AppDbContext db, IFreshserviceClient freshservice, CancellationToken ct)
    {
        var remote = new Dictionary<long, string>();
        foreach (var item in await freshservice.LookupAsync(LookupSources.Locations, null, null, ct))
        {
            if (!long.TryParse(item.Id, out var id) || id <= 0) continue;
            var name = item.Name.Trim();
            if (name.Length == 0) continue;
            remote.TryAdd(id, name.Length > 200 ? name[..200] : name);
        }
        // An empty answer almost always means a permissions problem, not that the school has no buildings.
        if (remote.Count == 0)
            return new LocationSyncResult(0, 0, 0, 0, "Freshservice returned no locations, so nothing was changed. Check that the API key's account can view locations.");

        var local = await db.Locations.Where(l => l.FreshserviceId != null).ToListAsync(ct);
        var byRemoteId = local.GroupBy(l => l.FreshserviceId!.Value).ToDictionary(g => g.Key, g => g.First());
        int added = 0, renamed = 0, switchedOff = 0;
        var now = DateTime.UtcNow;

        foreach (var (id, name) in remote)
        {
            if (byRemoteId.TryGetValue(id, out var existing))
            {
                if (existing.Name == name) continue;
                existing.Name = name;
                existing.UpdatedAt = now;
                renamed++;
            }
            else
            {
                db.Locations.Add(new Location { Name = name, FreshserviceId = id, IsActive = true, UpdatedAt = now });
                added++;
            }
        }
        foreach (var gone in local.Where(l => l.IsActive && !remote.ContainsKey(l.FreshserviceId!.Value)))
        {
            gone.IsActive = false;
            gone.UpdatedAt = now;
            switchedOff++;
        }
        await db.SaveChangesAsync(ct);
        return new LocationSyncResult(added, renamed, switchedOff, remote.Count);
    }
}
