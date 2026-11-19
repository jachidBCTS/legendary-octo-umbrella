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
    private readonly IStaffDirectory _staff;

    public LocationsModel(AppDbContext db, IFreshserviceClient freshservice, IConfiguration config, IStaffDirectory staff)
    {
        _db = db;
        _freshservice = freshservice;
        _config = config;
        _staff = staff;
    }

    /// <summary>The district codes an admin can pick: those in the settings file, and any already in use.</summary>
    public List<string> DistrictChoices { get; private set; } = new();

    public async Task<IActionResult> OnPostDistrictAsync(int id, string? district)
    {
        var location = await _db.Locations.FirstOrDefaultAsync(l => l.Id == id);
        if (location is null) return NotFound();
        district = (district ?? "").Trim();
        if (district.Length > 40) return Flash("A district code is at most 40 characters.", false);
        location.District = district.Length == 0 ? null : district;
        location.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Flash(district.Length == 0 ? $"{location.Name} is no longer in a district." : $"{location.Name} is in {district}.", true);
    }

    /// <summary>The district's locations named in the settings file's location matches.</summary>
    public IReadOnlyList<string> SettingsLocations => StartingLocations.From(_config);

    /// <summary>Adds the district's locations from the settings file (Students:Match:Locations) that are not here yet.</summary>
    /// <summary>
    /// Adds the district's locations from the settings file (Students:Match:Locations) that are not here yet,
    /// and sets the district of every location from it that has none, from the staff list (which district
    /// the staff working there are in).
    /// </summary>
    public async Task<IActionResult> OnPostFromSettingsAsync(CancellationToken ct)
    {
        var places = StartingLocations.WithStaffNames(_config);
        if (places.Count == 0) return Flash("The settings file has no location list (Students:Match:Locations).", false);
        var districtOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var staffNote = "";
        if (_staff.IsConfigured)
        {
            try
            {
                foreach (var (district, location) in await _staff.PlacesAsync(ct))
                    if (district.Length > 0 && location.Length > 0) districtOf.TryAdd(location, district);
            }
            catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
            {
                staffNote = " The staff list could not be read, so districts were not filled in: " + ex.Message;
            }
        }
        var rows = await _db.Locations.ToListAsync(ct);
        var byName = rows.GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        int added = 0, districted = 0;
        foreach (var (name, staffNames) in places)
        {
            var district = staffNames.Select(k => districtOf.GetValueOrDefault(k)).FirstOrDefault(d => !string.IsNullOrEmpty(d));
            if (!byName.TryGetValue(name, out var location))
            {
                location = new Location { Name = name, UpdatedAt = DateTime.UtcNow };
                _db.Locations.Add(location);
                byName[name] = location;
                added++;
            }
            if (string.IsNullOrEmpty(location.District) && district is not null)
            {
                location.District = district.Length > 40 ? district[..40] : district;
                location.UpdatedAt = DateTime.UtcNow;
                districted++;
            }
        }
        if (added + districted > 0) await _db.SaveChangesAsync(ct);
        var message = added == 0 && districted == 0
            ? "Every location in the settings file is already here."
            : $"Added {added} location{(added == 1 ? "" : "s")}; set the district of {districted}.";
        return Flash(message + staffNote, staffNote.Length == 0);
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
