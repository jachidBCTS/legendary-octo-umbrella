using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Admin;

/// <summary>Named groups of staff that workflow steps refer to as [Team name].</summary>
public class TeamsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public TeamsModel(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public List<Team> Teams { get; private set; } = new();
    public Dictionary<string, List<string>> UsedBy { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task OnGetAsync()
    {
        Teams = await _db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
        UsedBy = await UsageAsync();
    }

    /// <summary>Which workflows name each team in one of their steps.</summary>
    private async Task<Dictionary<string, List<string>>> UsageAsync()
    {
        var usage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in await _db.Workflows.AsNoTracking().Select(w => new { w.Name, w.DraftJson, w.PublishedJson }).ToListAsync())
        {
            var named = new[] { w.DraftJson, w.PublishedJson }.Where(j => !string.IsNullOrEmpty(j))
                .SelectMany(j => Json.ParseDefinition(j!).Flow.Nodes.SelectMany(SchoolFlow.Core.Workflows.WorkflowEngine.TeamsNamedBy)).ToList();
            foreach (var name in named)
            {
                if (!usage.TryGetValue(name, out var list)) usage[name] = list = new List<string>();
                if (!list.Contains(w.Name)) list.Add(w.Name);
            }
        }
        return usage;
    }

    private IActionResult Flash(string message, bool ok, int? openTeam = null)
    {
        TempData["Flash"] = message;
        if (openTeam is int id) TempData["OpenTeam"] = id;   // the team just changed stays open; the others are folded
        if (!ok) TempData["FlashKind"] = "bad";
        return RedirectToPage();
    }

    private static (List<string> Members, string? Error) ParseMembers(string? text)
    {
        var members = (text ?? "").Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.Trim().ToLowerInvariant()).Distinct().ToList();
        var bad = members.FirstOrDefault(m => m.Length > 320 || !System.Text.RegularExpressions.Regex.IsMatch(m, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"));
        if (bad is not null) return (members, $"“{(bad.Length > 60 ? bad[..60] : bad)}” is not an email address.");
        return members.Count > 50 ? (members, "A team can have at most 50 people.") : (members, null);
    }

    public async Task<IActionResult> OnPostAddAsync(string? name, string? members)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 200 || name.Contains('[') || name.Contains(']'))
            return Flash("Give the team a name (up to 200 characters, without square brackets).", false);
        if (await _db.Teams.AnyAsync(t => t.Name == name)) return Flash($"There is already a team called “{name}”.", false);
        var (list, error) = ParseMembers(members);
        if (error is not null) return Flash(error, false);
        _db.Teams.Add(new Team { Name = name, Members = list.Count == 0 ? "" : ApprovalTask.Pack(list), UpdatedAt = DateTime.UtcNow, UpdatedBy = User.Email() });
        _audit.Record(User, "team.added", name, list.Count == 0 ? "No members yet." : $"Members: {string.Join(", ", list)}.");
        await _db.SaveChangesAsync();
        return Flash($"Added. In a workflow step, write [{name}] where the people go.", true);
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, string? members)
    {
        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == id);
        if (team is null) return NotFound();
        var (list, error) = ParseMembers(members);
        if (error is not null) return Flash($"{team.Name}: {error}", false, team.Id);
        var packed = list.Count == 0 ? "" : ApprovalTask.Pack(list);
        if (packed == team.Members) return Flash("Nothing changed.", true, team.Id);
        _audit.Record(User, "team.changed", team.Name, $"Members changed from [{string.Join(", ", team.MemberList)}] to [{string.Join(", ", list)}].");
        team.Members = packed;
        team.UpdatedAt = DateTime.UtcNow;
        team.UpdatedBy = User.Email();
        await _db.SaveChangesAsync();
        return Flash($"Saved. New approvals and tasks for “{team.Name}” go to these people; ones already waiting keep the people they were sent to.", true, team.Id);
    }

    public async Task<IActionResult> OnPostRemoveAsync(int id)
    {
        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == id);
        if (team is null) return NotFound();
        if ((await UsageAsync()).TryGetValue(team.Name, out var users))
            return Flash($"“{team.Name}” is used by {string.Join(", ", users)}. Change those workflows first.", false);
        _db.Teams.Remove(team);
        _audit.Record(User, "team.removed", team.Name);
        await _db.SaveChangesAsync();
        return Flash($"Removed “{team.Name}”.", true);
    }
}
