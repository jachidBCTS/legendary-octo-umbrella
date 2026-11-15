using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Admin;

public class LocationsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IFreshserviceClient _freshservice;

    public LocationsModel(AppDbContext db, IFreshserviceClient freshservice)
    {
        _db = db;
        _freshservice = freshservice;
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
