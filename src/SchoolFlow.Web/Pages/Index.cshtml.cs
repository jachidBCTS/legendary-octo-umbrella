using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly SchoolCalendar _calendar;
    public IndexModel(AppDbContext db, SchoolCalendar calendar)
    {
        _db = db;
        _calendar = calendar;
    }

    /// <summary>Where each recent request that is waiting on someone is now.</summary>
    public Dictionary<int, CurrentStop> Stops { get; private set; } = new();

    public List<IGrouping<string, Workflow>> Groups { get; private set; } = new();
    public List<WorkflowRun> Recent { get; private set; } = new();
    public List<ApprovalTask> Pending { get; private set; } = new();
    public List<(FormDraft Draft, Workflow Workflow)> Drafts { get; private set; } = new();
    /// <summary>The home page's number tiles: the person's own requests still under way, and finished this month.</summary>
    public int InProgress { get; private set; }
    public int CompletedThisMonth { get; private set; }
    public string Greeting => DateTime.Now.Hour switch { < 12 => "Good morning", < 17 => "Good afternoon", _ => "Good evening" };

    public async Task OnGetAsync()
    {
        var email = User.Email();
        // Forms limited to particular groups only appear for their members (and the people who run them).
        var live = (await _db.Workflows.AsNoTracking()
            .Where(w => w.PublishedJson != null && w.IsEnabled && w.TriggerType == TriggerTypes.Form)
            .OrderBy(w => w.Name).ToListAsync()).Where(w => AccessService.CanSubmit(User, w)).ToList();
        // In the order set on Admin → Categories; anything not in that list after, then "Other".
        var order = await Categories.NamesAsync(_db);
        int Rank(string name) { var i = order.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; }
        Groups = live.GroupBy(w => w.Category).OrderBy(g => g.Key.Length == 0).ThenBy(g => Rank(g.Key)).ThenBy(g => g.Key).ToList();
        Recent = await _db.Runs.AsNoTracking().Where(r => r.SubmitterEmail == email && email != "")
            .OrderByDescending(r => r.UpdatedAt).Take(6).Select(WorkflowRun.ListColumns).ToListAsync();
        Stops = await Journey.CurrentStopsAsync(_db, _calendar, Recent);
        var drafts = await _db.FormDrafts.AsNoTracking().Where(d => d.OwnerEmail == email && email != "")
            .OrderByDescending(d => d.UpdatedAt).ToListAsync();
        // A draft of a form that has since been switched off cannot be finished, so it is not offered.
        Drafts = drafts.Select(d => (d, live.FirstOrDefault(w => w.Id == d.WorkflowId))).Where(x => x.Item2 is not null).Select(x => (x.d, x.Item2!)).ToList();
        if (email.Length > 0)
        {
            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var mine = _db.Runs.AsNoTracking().Where(r => r.SubmitterEmail == email);
            InProgress = await mine.CountAsync(r => r.Status != RunStatus.Completed && r.Status != RunStatus.Rejected && r.Status != RunStatus.Cancelled);
            CompletedThisMonth = await mine.CountAsync(r => r.Status == RunStatus.Completed && r.UpdatedAt >= monthStart);
        }
        var needle = ";" + email + ";";
        Pending = email.Length == 0 ? new() : await _db.ApprovalTasks.AsNoTracking()
            .Where(t => t.Status == ApprovalStatus.Pending && (t.Assignees.Contains(needle) || t.Extra.Contains(needle)) && !t.ApprovedBy.Contains(needle)).OrderBy(t => t.CreatedAt).ToListAsync();
    }

    public async Task<Microsoft.AspNetCore.Mvc.IActionResult> OnPostDeleteDraftAsync(int id)
    {
        var email = User.Email();
        var draft = await _db.FormDrafts.FirstOrDefaultAsync(d => d.Id == id && d.OwnerEmail == email && email != "");
        if (draft is not null)
        {
            _db.FormDrafts.Remove(draft);
            await _db.SaveChangesAsync();
            TempData["Flash"] = "Draft deleted.";
        }
        return RedirectToPage();
    }
}
