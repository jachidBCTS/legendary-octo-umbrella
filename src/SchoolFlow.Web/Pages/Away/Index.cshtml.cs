using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Away;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;

    public IndexModel(AppDbContext db, NotificationService notifications)
    {
        _db = db;
        _notifications = notifications;
    }

    public List<Delegation> Mine { get; private set; } = new();
    public List<Delegation> ForOthers { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var email = User.Email();
        var today = DateTime.Now.Date;
        Mine = await _db.Delegations.AsNoTracking().Where(d => d.FromEmail == email && email != "").OrderByDescending(d => d.EndsOn).Take(30).ToListAsync();
        ForOthers = await _db.Delegations.AsNoTracking().Where(d => d.ToEmail == email && email != "" && d.EndsOn >= today).OrderBy(d => d.StartsOn).ToListAsync();
    }

    public async Task<IActionResult> OnPostAddAsync(string? to, DateTime? startsOn, DateTime? endsOn, CancellationToken ct)
    {
        var email = User.Email();
        to = (to ?? "").Trim().ToLowerInvariant();
        if (email.Length == 0) return Fail("Your account has no email address.");
        if (to.Length > 320 || !System.Text.RegularExpressions.Regex.IsMatch(to, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return Fail("Enter your stand-in's email address.");
        if (to == email) return Fail("You cannot be your own stand-in.");
        if (startsOn is null || endsOn is null) return Fail("Choose both dates.");
        var start = startsOn.Value.Date;
        var end = endsOn.Value.Date;
        if (end < start) return Fail("The end date is before the start date.");
        if (end < DateTime.Now.Date) return Fail("Those dates are already in the past.");
        if ((end - start).TotalDays > 366) return Fail("A stand-in can cover at most a year at a time.");
        // Passing approvals along a chain of stand-ins would be impossible to follow; one hop only.
        if (await _db.Delegations.AnyAsync(d => d.FromEmail == to && d.StartsOn <= end && d.EndsOn >= start, ct))
            return Fail($"{to} has named a stand-in of their own for those dates. Choose someone who will be in.");

        var delegation = new Delegation { FromEmail = email, ToEmail = to, StartsOn = start, EndsOn = end, CreatedAt = DateTime.UtcNow };
        _db.Delegations.Add(delegation);

        // Approvals already waiting on this person are opened to the stand-in straight away.
        var opened = 0;
        if (delegation.IsActiveOn(DateTime.Now))
        {
            var needle = ";" + email + ";";
            var waiting = await _db.ApprovalTasks.Where(t => t.Status == ApprovalStatus.Pending && t.Assignees.Contains(needle)).ToListAsync(ct);
            var runIds = waiting.Select(t => t.RunId).ToList();
            var runs = await _db.Runs.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
            foreach (var task in waiting)
            {
                if (!runs.TryGetValue(task.RunId, out var run) || run.Status != RunStatus.WaitingApproval) continue;
                if (run.SubmitterEmail == to || task.CanBeDecidedBy(to) || task.HasApproved(email)) continue;
                task.StandIns = (task.StandIns.Length == 0 ? ";" : task.StandIns) + $"{to}={email};";
                task.RebuildExtra();
                _db.RunEvents.Add(new RunEventRow { RunId = run.Id, At = DateTime.UtcNow, NodeId = task.NodeId, Kind = "info", Message = $"{email} is away, so {to} can decide in their place." });
                await _notifications.NotifyAsync(new[] { to }, $"Approval needed: {run.WorkflowName} #{run.Id}",
                    $"You are standing in for {User.DisplayName()}. {(string.IsNullOrEmpty(run.SubmitterName) ? "A request" : run.SubmitterName + "’s request")} is waiting for a decision: {task.Title}.",
                    $"/Requests/Detail/{run.Id}", ct);
                opened++;
            }
        }
        await _notifications.NotifyAsync(new[] { to }, $"You are {User.DisplayName()}’s stand-in",
            $"{User.DisplayName()} has named you to approve requests in their place from {start:MMM d, yyyy} to {end:MMM d, yyyy}.", "/Away", ct);
        await _db.SaveChangesAsync(ct);
        TempData["Flash"] = $"{to} can approve for you from {start:MMM d} to {end:MMM d}." + (opened > 0 ? $" {opened} approval{(opened == 1 ? "" : "s")} already waiting on you {(opened == 1 ? "has" : "have")} been opened to them." : "");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(int id, CancellationToken ct)
    {
        var email = User.Email();
        var delegation = await _db.Delegations.FirstOrDefaultAsync(d => d.Id == id && d.FromEmail == email && email != "", ct);
        if (delegation is null) return NotFound();
        _db.Delegations.Remove(delegation);
        // Approvals the stand-in could act on go back to being this person's alone.
        var pair = $"{delegation.ToEmail}={email}";
        foreach (var task in await _db.ApprovalTasks.Where(t => t.Status == ApprovalStatus.Pending && t.StandIns.Contains(pair)).ToListAsync(ct))
        {
            var kept = task.StandInPairs.Where(p => !(p.StandIn == delegation.ToEmail && p.For == email)).ToList();
            task.StandIns = kept.Count == 0 ? "" : ";" + string.Join(";", kept.Select(p => $"{p.StandIn}={p.For}")) + ";";
            task.RebuildExtra();
        }
        await _db.SaveChangesAsync(ct);
        TempData["Flash"] = $"{delegation.ToEmail} is no longer your stand-in.";
        return RedirectToPage();
    }

    private IActionResult Fail(string message)
    {
        TempData["Flash"] = message;
        TempData["FlashKind"] = "bad";
        return RedirectToPage();
    }
}
