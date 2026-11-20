using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages;

public class ApprovalsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly SchoolCalendar _calendar;
    public ApprovalsModel(AppDbContext db, SchoolCalendar calendar)
    {
        _db = db;
        _calendar = calendar;
    }

    /// <summary>Per request: the stop, who it is waiting on (others too, on "all must approve" steps) and for how long.</summary>
    public Dictionary<int, CurrentStop> Stops { get; private set; } = new();

    public List<(ApprovalTask Task, WorkflowRun Run)> Pending { get; private set; } = new();
    public List<ApprovalTask> Decided { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var email = User.Email();
        if (email.Length == 0) return;
        var needle = ";" + email + ";";
        var tasks = await _db.ApprovalTasks.AsNoTracking()
            .Where(t => t.Status == ApprovalStatus.Pending && (t.Assignees.Contains(needle) || t.Extra.Contains(needle)) && !t.ApprovedBy.Contains(needle)).OrderBy(t => t.CreatedAt).ToListAsync();
        var runIds = tasks.Select(t => t.RunId).ToList();
        var runs = await _db.Runs.AsNoTracking().Where(r => runIds.Contains(r.Id)).Select(WorkflowRun.ListColumns).ToDictionaryAsync(r => r.Id);
        Pending = tasks.Where(t => runs.ContainsKey(t.RunId)).Select(t => (t, runs[t.RunId])).ToList();
        Stops = await Journey.CurrentStopsAsync(_db, _calendar, runs.Values.ToList());
        Decided = await _db.ApprovalTasks.AsNoTracking().Where(t => t.DecidedBy == email)
            .OrderByDescending(t => t.DecidedAt).Take(15).ToListAsync();
    }
}
