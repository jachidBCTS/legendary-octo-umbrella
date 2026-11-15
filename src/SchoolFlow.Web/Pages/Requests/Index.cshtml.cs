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

    public IndexModel(AppDbContext db, AccessService access)
    {
        _db = db;
        _access = access;
    }

    /// <summary>"mine" (default), "managed" (workflows I own; everything for admins) or "department" (departments I give final approval for).</summary>
    [BindProperty(SupportsGet = true)] public string? Scope { get; set; }
    /// <summary>Older links used ?all=true for what is now the "managed" scope.</summary>
    [BindProperty(SupportsGet = true)] public bool All { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public int? Department { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public List<Department> Departments { get; private set; } = new();
    public List<WorkflowRun> Runs { get; private set; } = new();
    public bool HasMore { get; private set; }
    public bool CanSeeManaged { get; private set; }
    public bool CanSeeDepartment { get; private set; }
    public bool ShowsOthers => Scope is "managed" or "department";

    public string PageUrl(int page) =>
        $"/Requests?p={page}&scope={Scope}{(string.IsNullOrEmpty(Status) ? "" : "&status=" + Uri.EscapeDataString(Status))}{(Department is int d ? "&department=" + d : "")}";

    public async Task OnGetAsync()
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
        if (Department is int departmentId) query = query.Where(r => r.DepartmentId == departmentId);

        var departments = await _db.Departments.AsNoTracking().OrderBy(d => d.Name).ToListAsync();
        Departments = Scope == "department" ? departments.Where(d => myDepartments.Contains(d.Id)).ToList() : departments;

        var rows = await query.OrderByDescending(r => r.Id).Skip((PageNumber - 1) * PageSize).Take(PageSize + 1).ToListAsync();
        HasMore = rows.Count > PageSize;
        Runs = rows.Take(PageSize).ToList();
    }
}
