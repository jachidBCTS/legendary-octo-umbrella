using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
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
    public string Greeting => DateTime.Now.Hour switch { < 12 => "Good morning", < 17 => "Good afternoon", _ => "Good evening" };

    public async Task OnGetAsync()
    {
        var email = User.Email();
        // Forms limited to particular groups only appear for their members (and the people who run them).
        var live = (await _db.Workflows.AsNoTracking()
            .Where(w => w.PublishedJson != null && w.IsEnabled && w.TriggerType == TriggerTypes.Form)
            .OrderBy(w => w.Name).ToListAsync()).Where(w => AccessService.CanSubmit(User, w)).ToList();
        Groups = live.GroupBy(w => w.Category).OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key).ToList();
        Recent = await _db.Runs.AsNoTracking().Where(r => r.SubmitterEmail == email && email != "")
            .OrderByDescending(r => r.UpdatedAt).Take(6).Select(WorkflowRun.ListColumns).ToListAsync();
        Stops = await Journey.CurrentStopsAsync(_db, _calendar, Recent);
        var drafts = await _db.FormDrafts.AsNoTracking().Where(d => d.OwnerEmail == email && email != "")
            .OrderByDescending(d => d.UpdatedAt).ToListAsync();
        // A draft of a form that has since been switched off cannot be finished, so it is not offered.
        Drafts = drafts.Select(d => (d, live.FirstOrDefault(w => w.Id == d.WorkflowId))).Where(x => x.Item2 is not null).Select(x => (x.d, x.Item2!)).ToList();
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
