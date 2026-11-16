using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Reports;

/// <summary>Counts and turnaround for the requests a person is entitled to see beyond their own: workflows they own, their department, or everything for admins.</summary>
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AccessService _access;
    private readonly SchoolCalendar _calendar;

    public IndexModel(AppDbContext db, AccessService access, SchoolCalendar calendar)
    {
        _db = db;
        _access = access;
        _calendar = calendar;
    }

    public sealed record Row(int Id, string Name, int Count, int Open, int Completed, int NotApproved, double? AverageDays);
    public sealed record WaitingRow(int RunId, string Workflow, string Step, string WaitingOn, int Days);

    [BindProperty(SupportsGet = true)] public string? Scope { get; set; }
    [BindProperty(SupportsGet = true)] public int Period { get; set; } = 90;
    public bool CanSeeManaged { get; private set; }
    public bool CanSeeDepartment { get; private set; }
    public string ScopeLabel { get; private set; } = "";
    public int Total { get; private set; }
    public int Open { get; private set; }
    public int Completed { get; private set; }
    public int NotApproved { get; private set; }
    public int Failed { get; private set; }
    public double? AverageDays { get; private set; }
    public List<Row> ByWorkflow { get; private set; } = new();
    public List<Row> ByDepartment { get; private set; } = new();
    public List<WaitingRow> Waiting { get; private set; } = new();
    public int MaxWorkflow => Math.Max(1, ByWorkflow.Select(r => r.Count).DefaultIfEmpty(1).Max());
    public int MaxDepartment => Math.Max(1, ByDepartment.Select(r => r.Count).DefaultIfEmpty(1).Max());

    public async Task<IActionResult> OnGetAsync()
    {
        var managed = await _access.ManagedWorkflowIdsAsync(User);
        var myDepartments = await _access.DepartmentsApprovedByAsync(User);
        CanSeeManaged = managed is null || managed.Count > 0;
        CanSeeDepartment = myDepartments.Count > 0;
        // Reports are about other people's requests, so they need one of the wider views.
        if (!CanSeeManaged && !CanSeeDepartment) return Redirect("/Account/Denied");
        if (Scope is not ("managed" or "department") || (Scope == "managed" && !CanSeeManaged) || (Scope == "department" && !CanSeeDepartment))
            Scope = CanSeeManaged ? "managed" : "department";
        if (Period is not (30 or 90 or 365)) Period = 90;
        ScopeLabel = Scope == "department" ? "The departments you give final approval for" : User.IsAdmin() ? "Every request across the school" : "The workflows you own";

        var since = DateTime.UtcNow.AddDays(-Period);
        var query = _db.Runs.AsNoTracking().Where(r => r.CreatedAt >= since);
        query = Scope == "department"
            ? query.Where(r => r.DepartmentId != null && myDepartments.Contains(r.DepartmentId.Value))
            : managed is null ? query : query.Where(r => managed.Contains(r.WorkflowId));
        var runs = await query.Select(r => new { r.Id, r.WorkflowId, r.WorkflowName, r.Status, r.DepartmentId, r.DepartmentName, r.CreatedAt, r.UpdatedAt }).ToListAsync();

        var closed = await _calendar.ClosedDaysAsync();
        static bool IsOpen(string s) => s is RunStatus.Running or RunStatus.WaitingApproval or RunStatus.WaitingTicket or RunStatus.ChangesRequested;
        double? Average(IEnumerable<(DateTime Created, DateTime Updated)> finished)
        {
            var days = finished.Select(f => SchoolCalendar.SchoolDaysBetween(Ui.Local(f.Created), Ui.Local(f.Updated), closed)).ToList();
            return days.Count == 0 ? null : days.Average();
        }

        Total = runs.Count;
        Open = runs.Count(r => IsOpen(r.Status));
        Completed = runs.Count(r => r.Status == RunStatus.Completed);
        NotApproved = runs.Count(r => r.Status == RunStatus.Rejected);
        Failed = runs.Count(r => r.Status == RunStatus.Failed);
        AverageDays = Average(runs.Where(r => r.Status == RunStatus.Completed).Select(r => (r.CreatedAt, r.UpdatedAt)));

        ByWorkflow = runs.GroupBy(r => r.WorkflowId).Select(g => new Row(g.Key, g.First().WorkflowName, g.Count(), g.Count(r => IsOpen(r.Status)),
            g.Count(r => r.Status == RunStatus.Completed), g.Count(r => r.Status == RunStatus.Rejected),
            Average(g.Where(r => r.Status == RunStatus.Completed).Select(r => (r.CreatedAt, r.UpdatedAt))))).OrderByDescending(r => r.Count).ToList();
        ByDepartment = runs.Where(r => r.DepartmentId != null).GroupBy(r => r.DepartmentId!.Value).Select(g => new Row((int)g.Key, g.First().DepartmentName, g.Count(),
            g.Count(r => IsOpen(r.Status)), g.Count(r => r.Status == RunStatus.Completed), g.Count(r => r.Status == RunStatus.Rejected), null)).OrderByDescending(r => r.Count).ToList();

        var waitingIds = runs.Where(r => r.Status == RunStatus.WaitingApproval).Select(r => r.Id).ToList();
        var names = runs.ToDictionary(r => r.Id, r => r.WorkflowName);
        var tasks = await _db.ApprovalTasks.AsNoTracking().Where(t => t.Status == ApprovalStatus.Pending && waitingIds.Contains(t.RunId)).ToListAsync();
        var now = DateTime.Now;
        Waiting = tasks.Select(t => new WaitingRow(t.RunId, names[t.RunId], t.Title,
                string.Join(", ", t.AssigneeList.Where(a => !t.HasApproved(a))), SchoolCalendar.SchoolDaysBetween(Ui.Local(t.CreatedAt), now, closed)))
            .OrderByDescending(w => w.Days).Take(15).ToList();
        return Page();
    }
}
