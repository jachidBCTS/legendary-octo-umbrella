using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Admin;

public class SettingsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly IFreshserviceClient _freshservice;
    private readonly IConfiguration _config;
    private readonly IMemoryCache _cache;

    public SettingsModel(AppDbContext db, IFreshserviceClient freshservice, IConfiguration config, IMemoryCache cache)
    {
        _db = db;
        _freshservice = freshservice;
        _config = config;
        _cache = cache;
    }

    public bool IsLive => _freshservice.IsLive;
    public string Domain => _config["Freshservice:Domain"] ?? "";
    public bool WebhookEnabled => (_config["Freshservice:WebhookSecret"] ?? "").Length >= 16;
    public string WebhookUrl => $"{Request.Scheme}://{Request.Host}/api/webhooks/freshservice";
    public HashSet<string> ConfigAdmins { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AppUser> Users { get; private set; } = new();
    public List<WebhookLog> WebhookLogs { get; private set; } = new();
    public string? TestResult { get; private set; }
    public bool TestOk { get; private set; }

    private async Task LoadAsync()
    {
        ConfigAdmins = new HashSet<string>((_config.GetSection("Auth:Admins").Get<string[]>() ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase);
        Users = await _db.Users.AsNoTracking().OrderBy(u => u.Name).ToListAsync();
        WebhookLogs = await _db.WebhookLogs.AsNoTracking().OrderByDescending(l => l.Id).Take(10).ToListAsync();
    }

    public Task OnGetAsync() => LoadAsync();

    public async Task OnPostTestAsync(CancellationToken ct)
    {
        try
        {
            TestResult = await _freshservice.TestConnectionAsync(ct);
            TestOk = true;
        }
        catch (Exception ex) when (ex is FreshserviceException or HttpRequestException or TaskCanceledException)
        {
            TestResult = ex is FreshserviceException ? ex.Message : "Could not reach Freshservice: " + ex.Message;
        }
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostRoleAsync(string? email, string? role)
    {
        if (Array.IndexOf(Roles.All, role) < 0) return BadRequest();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null) return NotFound();
        if (user.Email == User.Email() && role != Roles.Admin)
        {
            TempData["Flash"] = "You cannot remove your own admin role. Ask another admin to do it.";
            TempData["FlashKind"] = "bad";
            return RedirectToPage();
        }
        user.Role = role!;
        await _db.SaveChangesAsync();
        _cache.Remove(RoleClaimsTransformation.CacheKey(user.Email));
        TempData["Flash"] = $"{user.Name} is now {role}.";
        return RedirectToPage();
    }
}
