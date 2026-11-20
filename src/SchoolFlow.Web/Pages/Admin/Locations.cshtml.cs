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

    /// <summary>The district codes an admin can pick: those in the settings file, and any already in use.</summary>
    public List<string> DistrictChoices { get; private set; } = new();

    /// <summary>One row of the list as the admin left it (names of Freshservice locations are ignored).</summary>
    public sealed class LocationEdit
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? District { get; set; }
        public bool Active { get; set; }
    }

    [BindProperty] public List<LocationEdit> Rows { get; set; } = new();

    /// <summary>
    /// Saves every change made in the list at once (names of hand-added locations, districts, on/off).
    /// All or nothing: if one name is not allowed, nothing is saved.
    /// </summary>
    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var all = await _db.Locations.ToListAsync(ct);
        var byId = all.ToDictionary(l => l.Id);
        var now = DateTime.UtcNow;
        var changed = 0;
        var renamed = new List<Location>();
        foreach (var edit in Rows)
        {
            if (!byId.TryGetValue(edit.Id, out var location)) continue;
            var touched = false;
            var name = (edit.Name ?? "").Trim();
            if (location.FreshserviceId is null && !location.TicketChoice && edit.Name is not null && name != location.Name)
            {
                if (name.Length == 0 || name.Length > 200) return Flash($"“{location.Name}” needs a name (up to 200 characters). Nothing was saved.", false);
                location.Name = name;
                renamed.Add(location);
                touched = true;
            }
            var district = (edit.District ?? "").Trim();
            if (district.Length > 40) return Flash($"“{location.Name}”: a district code is at most 40 characters. Nothing was saved.", false);
            var newDistrict = district.Length == 0 ? null : district;
            if (!string.Equals(newDistrict, location.District, StringComparison.Ordinal)) { location.District = newDistrict; touched = true; }
            if (edit.Active != location.IsActive) { location.IsActive = edit.Active; touched = true; }
            if (touched) { location.UpdatedAt = now; changed++; }
        }
        // Only a new name is checked: the list may already hold the same name twice (a hand-added copy of a Freshservice location).
        var clash = renamed.FirstOrDefault(r => all.Any(l => l != r && string.Equals(l.Name, r.Name, StringComparison.OrdinalIgnoreCase)));
        if (clash is not null) return Flash($"There is already a location called “{clash.Name}”. Nothing was saved.", false);
        if (changed == 0) return Flash("Nothing had changed.", true);
        await _db.SaveChangesAsync(ct);
        return Flash($"Saved {changed} location{(changed == 1 ? "" : "s")}.", true);
    }

    public List<Location> Locations { get; private set; } = new();
    public bool IsLive => _freshservice.IsLive;

    public async Task OnGetAsync()
    {
        Locations = await _db.Locations.AsNoTracking().OrderByDescending(l => l.IsActive).ThenBy(l => l.Name).ToListAsync();
        DistrictChoices = SchoolCalendar.DistrictAliases(_config).Values
            .Concat(Locations.Select(l => l.District ?? "")).Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d).ToList();
    }

    public async Task<IActionResult> OnPostSyncAsync(CancellationToken ct)
    {
        try
        {
            var result = await LocationSync.RunAsync(_db, _freshservice, ct, _config["Freshservice:LocationField"]);
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

    private IActionResult Flash(string message, bool ok)
    {
        TempData["Flash"] = message;
        TempData["FlashKind"] = ok ? "ok" : "bad";
        return RedirectToPage();
    }
}
