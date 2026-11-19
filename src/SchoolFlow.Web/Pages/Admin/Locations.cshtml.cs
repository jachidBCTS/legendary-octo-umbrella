using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Admin;

public class LocationsModel : PageModel
{
    /// <summary>After any change here, forms see the new list straight away (it is otherwise kept for 30 seconds).</summary>
    public override void OnPageHandlerExecuted(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutedContext context) =>
        context.HttpContext.RequestServices.GetRequiredService<SchoolFlow.Web.Services.SchoolCalendar>().InvalidateLists();

    private readonly AppDbContext _db;
    private readonly IFreshserviceClient _freshservice;
    private readonly IConfiguration _config;

    public LocationsModel(AppDbContext db, IFreshserviceClient freshservice, IConfiguration config)
    {
        _db = db;
        _freshservice = freshservice;
        _config = config;
    }

    /// <summary>The district's locations named in the settings file's location matches.</summary>
    public IReadOnlyList<string> SettingsLocations => StartingLocations.From(_config);

    /// <summary>Adds the district's locations from the settings file (Students:Match:Locations) that are not here yet.</summary>
    public async Task<IActionResult> OnPostFromSettingsAsync(CancellationToken ct)
    {
        var names = SettingsLocations;
        if (names.Count == 0) return Flash("The settings file has no location list (Students:Match:Locations).", false);
        var existing = new HashSet<string>(await _db.Locations.Select(l => l.Name).ToListAsync(ct), StringComparer.OrdinalIgnoreCase);
        var added = names.Where(existing.Add).ToList();
        foreach (var name in added) _db.Locations.Add(new Location { Name = name, UpdatedAt = DateTime.UtcNow });
        if (added.Count > 0) await _db.SaveChangesAsync(ct);
        return Flash(added.Count == 0 ? "Every location in the settings file is already here." : $"Added {added.Count} location{(added.Count == 1 ? "" : "s")} from the settings file.", true);
    }

    public List<Location> Locations { get; private set; } = new();
    public bool IsLive => _freshservice.IsLive;

    public async Task OnGetAsync() =>
        Locations = await _db.Locations.AsNoTracking().OrderByDescending(l => l.IsActive).ThenBy(l => l.Name).ToListAsync();

    public async Task<IActionResult> OnPostSyncAsync(CancellationToken ct)
    {
        try
        {
            var result = await LocationSync.RunAsync(_db, _freshservice, ct);
            return Flash(result.Describe(), result.Skipped is null);
        }
        catch (Exception ex) when (ex is FreshserviceException or HttpRequestException or TaskCanceledException)
        {
            return Flash("Could not sync: " + (ex is FreshserviceException ? ex.Message : "Freshservice could not be reached."), false);
        }
    }

    public async Task<IActionResult> OnPostAddAsync(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 200) return Flash("Give the location a name (up to 200 characters).", false);
        if (await _db.Locations.AnyAsync(l => l.Name == name)) return Flash($"There is already a location called “{name}”.", false);
        _db.Locations.Add(new Location { Name = name, UpdatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        return Flash($"Added {name}.", true);
    }

    public async Task<IActionResult> OnPostRenameAsync(int id, string? name)
    {
        name = (name ?? "").Trim();
        var location = await _db.Locations.FirstOrDefaultAsync(l => l.Id == id);
        if (location is null) return NotFound();
        if (location.FreshserviceId is not null) return Flash("This location comes from Freshservice. Rename it there and sync.", false);
        if (name.Length == 0 || name.Length > 200) return Flash("Give the location a name (up to 200 characters).", false);
        if (await _db.Locations.AnyAsync(l => l.Name == name && l.Id != id)) return Flash($"There is already a location called “{name}”.", false);
        location.Name = name;
        location.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Flash($"Renamed to {name}.", true);
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var location = await _db.Locations.FirstOrDefaultAsync(l => l.Id == id);
        if (location is null) return NotFound();
        location.IsActive = !location.IsActive;
        location.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Flash(location.IsActive ? $"{location.Name} is offered on forms again." : $"{location.Name} is switched off.", true);
    }

    private IActionResult Flash(string message, bool ok)
    {
        TempData["Flash"] = message;
        TempData["FlashKind"] = ok ? "ok" : "bad";
        return RedirectToPage();
    }
}
