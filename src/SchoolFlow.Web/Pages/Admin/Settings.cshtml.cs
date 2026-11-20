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
    private readonly GroupDirectory _groupDirectory;
    private readonly AuditService _audit;

    public sealed record PersonRow(AppUser User, string EffectiveRole, string Source, bool Locked);
    public List<PersonRow> People { get; private set; } = new();
    public List<DirectoryGroup> Groups { get; private set; } = new();

    public SettingsModel(AppDbContext db, IFreshserviceClient freshservice, IConfiguration config, IMemoryCache cache,
        EmailSettings email, IEmailSender sender, GroupDirectory groupDirectory, AuditService audit)
    {
        _groupDirectory = groupDirectory;
        _audit = audit;
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
    /// <summary>The Freshservice ticket form's fields, when an admin asked to see them.</summary>
    public IReadOnlyList<SchoolFlow.Core.Freshservice.TicketField>? TicketFields { get; private set; }
    public string? TicketFieldsError { get; private set; }

    public async Task OnPostTicketFieldsAsync(CancellationToken ct)
    {
        try
        {
            TicketFields = await _freshservice.TicketFieldsAsync(ct);
            if (TicketFields.Count == 0) TicketFieldsError = "Freshservice returned no ticket fields.";
        }
        catch (Exception ex) when (ex is FreshserviceException or HttpRequestException or TaskCanceledException)
        {
            TicketFieldsError = ex is FreshserviceException ? ex.Message : "Could not reach Freshservice: " + ex.Message;
        }
        await LoadAsync();
    }
    public bool TestOk { get; private set; }

    private async Task LoadAsync()
    {
        ConfigAdmins = new HashSet<string>((_config.GetSection("Auth:Admins").Get<string[]>() ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase);
        Users = await _db.Users.AsNoTracking().OrderBy(u => u.Name).ToListAsync();
        Groups = await _db.DirectoryGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
        var grants = Groups.Where(g => g.Role is Roles.Admin or Roles.Builder).ToDictionary(g => g.ObjectId, g => (g.Role, g.Name));
        People = Users.Select(u =>
        {
            var resolved = RoleResolver.Resolve(u.Role, ConfigAdmins.Contains(u.Email), u.EntraRole,
                u.Groups.Split(';', StringSplitOptions.RemoveEmptyEntries), grants);
            // "Locked" means the role shown does not come from the drop-down, so changing the drop-down alone would not lower it.
            return new PersonRow(u, resolved.Role, resolved.Source, resolved.Source is not ("set by an admin" or "default"));
        }).ToList();
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
                await _sender.SendAsync(to, "Greenlight test email",
                    NotificationService.BuildHtml("Greenlight test email", "If you are reading this, Greenlight can send email through Microsoft 365.", _email.BaseUrl + "/"), ct);
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

    public async Task<IActionResult> OnPostGroupAsync(int? id, string? name, string? objectId, string? role)
    {
        name = (name ?? "").Trim();
        objectId = (objectId ?? "").Trim().ToLowerInvariant();
        role = role is Roles.Admin or Roles.Builder ? role : "";
        if (name.Length == 0 || name.Length > 200) return GroupFlash("Give the group a name (up to 200 characters).", false);
        if (!Guid.TryParse(objectId, out var parsed))
            return GroupFlash("The Object ID must look like 1a2b3c4d-1111-2222-3333-1a2b3c4d5e6f. Copy it from the group's Overview page in Entra.", false);
        objectId = parsed.ToString("D");
        if (await _db.DirectoryGroups.AnyAsync(g => g.ObjectId == objectId && g.Id != (id ?? 0)))
            return GroupFlash("That group is already registered.", false);

        DirectoryGroup group;
        if (id is int existing)
        {
            var found = await _db.DirectoryGroups.FirstOrDefaultAsync(g => g.Id == existing);
            if (found is null) return NotFound();
            group = found;
            if (group.Role != role || group.ObjectId != objectId)
                _audit.Record(User, "group.changed", name, $"Role granted changed from {Describe(group.Role)} to {Describe(role)} (group {objectId}).");
        }
        else
        {
            group = new DirectoryGroup();
            _db.DirectoryGroups.Add(group);
            _audit.Record(User, "group.registered", name, $"Grants {Describe(role)} (group {objectId}).");
        }
        group.Name = name;
        group.ObjectId = objectId;
        group.Role = role!;
        await _db.SaveChangesAsync();
        _groupDirectory.Invalidate();
        return GroupFlash($"Saved {name}.", true);

        static string Describe(string? r) => string.IsNullOrEmpty(r) ? "no role" : r;
    }

    public async Task<IActionResult> OnPostRemoveGroupAsync(int id)
    {
        var group = await _db.DirectoryGroups.FirstOrDefaultAsync(g => g.Id == id);
        if (group is null) return NotFound();
        var needle = ";" + group.ObjectId + ";";
        var users = await _db.Workflows.AsNoTracking().Where(w => w.AudienceGroups.Contains(needle)).Select(w => w.Name).ToListAsync();
        // Removing it silently would either open those forms to everyone or lock everyone out; make the admin decide.
        if (users.Count > 0)
            return GroupFlash($"“{group.Name}” limits who can submit: {string.Join(", ", users)}. Change those workflows first.", false);
        _audit.Record(User, "group.removed", group.Name, $"Had granted {(group.Role.Length == 0 ? "no role" : group.Role)} (group {group.ObjectId}).");
        _db.DirectoryGroups.Remove(group);
        await _db.SaveChangesAsync();
        _groupDirectory.Invalidate();
        return GroupFlash($"Removed {group.Name}.", true);
    }

    private IActionResult GroupFlash(string message, bool ok)
    {
        TempData["Flash"] = message;
        TempData["FlashKind"] = ok ? "ok" : "bad";
        return RedirectToPage();
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
        if (user.Role != role)
            _audit.Record(User, "role.changed", $"{user.Name} ({user.Email})", $"Role set by hand changed from {user.Role} to {role}.");
        user.Role = role!;
        await _db.SaveChangesAsync();
        _cache.Remove(RoleClaimsTransformation.CacheKey(user.Email));
        TempData["Flash"] = $"{user.Name} is now {role}.";
        return RedirectToPage();
    }
}
