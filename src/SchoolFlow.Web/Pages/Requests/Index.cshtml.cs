using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Requests;

public class IndexModel : PageModel
{
    private const int PageSize = 25;
    private readonly AppDbContext _db;
    private readonly AccessService _access;
    private readonly SchoolCalendar _calendar;

    public IndexModel(AppDbContext db, AccessService access, SchoolCalendar calendar)
    {
        _calendar = calendar;
        _db = db;
        _access = access;
    }

    /// <summary>"mine" (default), "managed" (workflows I own; everything for admins) or "department" (departments I give final approval for).</summary>
    [BindProperty(SupportsGet = true)] public string? Scope { get; set; }
    /// <summary>Older links used ?all=true for what is now the "managed" scope.</summary>
    [BindProperty(SupportsGet = true)] public bool All { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public int? Department { get; set; }
    [BindProperty(SupportsGet = true)] public int? Workflow { get; set; }
    /// <summary>Workflows that have requests in the current view, for the filter.</summary>
    public List<(int Id, string Name)> Workflows { get; private set; } = new();
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public List<Department> Departments { get; private set; } = new();
    public List<WorkflowRun> Runs { get; private set; } = new();
    /// <summary>For requests waiting on someone: the stop they are at, whose desk, and for how long.</summary>
    public Dictionary<int, CurrentStop> Stops { get; private set; } = new();
    public bool HasMore { get; private set; }
    public bool CanSeeManaged { get; private set; }
    public bool CanSeeDepartment { get; private set; }
    public bool ShowsOthers => Scope is "managed" or "department";

    public string PageUrl(int page) =>
        $"/Requests?p={page}&scope={Scope}{(string.IsNullOrEmpty(Status) ? "" : "&status=" + Uri.EscapeDataString(Status))}{(Department is int d ? "&department=" + d : "")}{(Workflow is int w ? "&workflow=" + w : "")}";

    public string ExportUrl => PageUrl(1).Replace("/Requests?", "/Requests?handler=Export&");

    public async Task OnGetAsync()
    {
        var query = await FilteredAsync();
        var rows = await query.OrderByDescending(r => r.Id).Skip((PageNumber - 1) * PageSize).Take(PageSize + 1).ToListAsync();
        HasMore = rows.Count > PageSize;
        Runs = rows.Take(PageSize).ToList();
        Stops = await Journey.CurrentStopsAsync(_db, _calendar, Runs);
    }

    /// <summary>Downloads what the list is showing (same scope and filters) as a spreadsheet file, one column per form answer.</summary>
    public async Task<IActionResult> OnGetExportAsync([FromServices] AuditService audit, [FromServices] IStudentRoster roster, [FromServices] StudentScopeService scope)
    {
        const int MaxRows = 5000;
        var runs = await (await FilteredAsync()).OrderByDescending(r => r.Id).Take(MaxRows).ToListAsync();
        // Requests keep only student numbers. Each request's students are looked up now (all at once) and shown
        // only to people who may see them: those asked to act on it, its requester with student access, or
        // people whose student access covers the students' location or district.
        var email = User.Email();
        var filter = await scope.FilterForAsync(email, HttpContext.RequestAborted);
        var contexts = new Dictionary<int, Dictionary<string, object?>>();
        var hidden = new HashSet<int>();
        var answers = new StudentAnswers(roster, _calendar);
        var withStudents = runs.Select(r => (Run: r, Def: SchoolFlow.Core.Json.ParseDefinition(r.DefinitionJson), Context: SchoolFlow.Core.Json.ToValues(r.ContextJson)))
            .Where(x => StudentAnswers.IdsOf(x.Def, x.Context).Any()).ToList();
        if (withStudents.Count > 0)
        {
            var runIds = withStudents.Select(x => x.Run.Id).ToList();
            var involved = (await _db.ApprovalTasks.AsNoTracking().Where(t => runIds.Contains(t.RunId)).ToListAsync())
                .Where(t => StudentVisibility.Involves(t, email)).Select(t => t.RunId).ToHashSet();
            var known = await answers.LookupAsync(withStudents.SelectMany(x => StudentAnswers.IdsOf(x.Def, x.Context)), HttpContext.RequestAborted);
            foreach (var (run, def, context) in withStudents)
            {
                var ids = StudentAnswers.IdsOf(def, context).ToList();
                if (!StudentVisibility.MaySee(involved.Contains(run.Id), run.SubmitterEmail == email && email.Length > 0, filter, ids, known)) { hidden.Add(run.Id); continue; }
                await answers.RestoreAsync(def, context, known, HttpContext.RequestAborted);
                contexts[run.Id] = context;
            }
        }
        var includeStudents = true;
        var csv = RequestExport.ToCsv(runs, includeStudents, contexts, hidden);
        if (Scope != "mine")
        {
            // Other people's answers are leaving the system: note who took them.
            audit.Record(User, "requests.exported", Scope == "department" ? "Department requests" : "Managed requests",
                $"{runs.Count} request(s){(Workflow is int w ? $", workflow #{w}" : "")}{(Department is int d ? $", department #{d}" : "")}{(string.IsNullOrEmpty(Status) ? "" : $", status {Status}")}.");
            await _db.SaveChangesAsync();
        }
        return File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv",
            $"schoolflow-requests-{DateTime.Now:yyyy-MM-dd}.csv");
    }

    /// <summary>The requests this person may see in the chosen scope, with the page's filters applied.</summary>
    private async Task<IQueryable<WorkflowRun>> FilteredAsync()
    {
        var email = User.Email();
        var managed = await _access.ManagedWorkflowIdsAsync(User);          // null = everything (admin)
        var myDepartments = await _access.DepartmentsApprovedByAsync(User);
        CanSeeManaged = managed is null || managed.Count > 0;
        CanSeeDepartment = myDepartments.Count > 0;

        if (All && Scope is null) Scope = "managed";
        // A scope the person is not entitled to quietly falls back to their own requests.
        if ((Scope == "managed" && !CanSeeManaged) || (Scope == "department" && !CanSeeDepartment) || Scope is not ("managed" or "department")) Scope = "mine";
        if (PageNumber < 1) PageNumber = 1;

        var query = _db.Runs.AsNoTracking().AsQueryable();
        query = Scope switch
        {
            "managed" when managed is null => query,
            "managed" => query.Where(r => managed!.Contains(r.WorkflowId)),
            "department" => query.Where(r => r.DepartmentId != null && myDepartments.Contains(r.DepartmentId.Value)),
            _ => query.Where(r => r.SubmitterEmail == email && email != ""),
        };
        if (!string.IsNullOrEmpty(Status)) query = query.Where(r => r.Status == Status);
        Workflows = (await query.Select(r => new { r.WorkflowId, r.WorkflowName }).Distinct().ToListAsync())
            .GroupBy(x => x.WorkflowId).Select(g => (g.Key, g.First().WorkflowName)).OrderBy(x => x.WorkflowName).ToList();
        if (Department is int departmentId) query = query.Where(r => r.DepartmentId == departmentId);
        if (Workflow is int workflowId) query = query.Where(r => r.WorkflowId == workflowId);

        var departments = await _db.Departments.AsNoTracking().OrderBy(d => d.Name).ToListAsync();
        Departments = Scope == "department" ? departments.Where(d => myDepartments.Contains(d.Id)).ToList() : departments;

        return query;
    }
}
