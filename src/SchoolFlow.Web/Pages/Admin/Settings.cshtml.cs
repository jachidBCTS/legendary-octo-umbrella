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

    private readonly EmailSettings _email;
    private readonly IEmailSender _sender;

    public SettingsModel(AppDbContext db, IFreshserviceClient freshservice, IConfiguration config, IMemoryCache cache,
        EmailSettings email, IEmailSender sender)
    {
        _email = email;
        _sender = sender;
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
    public EmailSettings Email => _email;
    public List<Notification> FailedEmails { get; private set; } = new();
    public int PendingEmails { get; private set; }
    public string? EmailTestResult { get; private set; }
    public bool EmailTestOk { get; private set; }
    public string? TestResult { get; private set; }
    public bool TestOk { get; private set; }

    private async Task LoadAsync()
    {
        ConfigAdmins = new HashSet<string>((_config.GetSection("Auth:Admins").Get<string[]>() ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase);
        Users = await _db.Users.AsNoTracking().OrderBy(u => u.Name).ToListAsync();
        WebhookLogs = await _db.WebhookLogs.AsNoTracking().OrderByDescending(l => l.Id).Take(10).ToListAsync();
        FailedEmails = await _db.Notifications.AsNoTracking().Where(n => n.EmailStatus == EmailStatus.Failed)
            .OrderByDescending(n => n.Id).Take(8).ToListAsync();
        PendingEmails = await _db.Notifications.CountAsync(n => n.EmailStatus == EmailStatus.Pending);
    }

    public async Task OnPostTestEmailAsync(CancellationToken ct)
    {
        var to = User.Email();
        if (!_email.IsEnabled)
        {
            EmailTestResult = "Email is not switched on, so nothing was sent.";
        }
        else
        {
            try
            {
                await _sender.SendAsync(to, "SchoolFlow test email",
                    NotificationService.BuildHtml("SchoolFlow test email", "If you are reading this, SchoolFlow can send email through Microsoft 365.", _email.BaseUrl + "/"), ct);
                EmailTestResult = $"Sent a test email to {to}. Check the inbox (and junk folder).";
                EmailTestOk = true;
            }
            catch (Exception ex) when (ex is EmailException or HttpRequestException or TaskCanceledException)
            {
                EmailTestResult = ex is EmailException ? ex.Message : "Could not reach Microsoft 365: " + ex.Message;
            }
        }
        await LoadAsync();
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
