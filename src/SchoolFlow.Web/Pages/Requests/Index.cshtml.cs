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
    public IndexModel(AppDbContext db) => _db = db;

    [BindProperty(SupportsGet = true)] public bool All { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public int? Department { get; set; }
    public List<Department> Departments { get; private set; } = new();
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;
    public List<WorkflowRun> Runs { get; private set; } = new();
    public bool HasMore { get; private set; }

    public string PageUrl(int page) =>
        $"/Requests?p={page}{(All ? "&all=true" : "")}{(string.IsNullOrEmpty(Status) ? "" : "&status=" + Uri.EscapeDataString(Status))}{(Department is int d ? "&department=" + d : "")}";

    public async Task OnGetAsync()
    {
        if (!User.IsBuilder()) All = false;
        if (PageNumber < 1) PageNumber = 1;
        var email = User.Email();
        var query = _db.Runs.AsNoTracking().AsQueryable();
        if (!All) query = query.Where(r => r.SubmitterEmail == email && email != "");
        if (!string.IsNullOrEmpty(Status)) query = query.Where(r => r.Status == Status);
        if (Department is int departmentId) query = query.Where(r => r.DepartmentId == departmentId);
        Departments = await _db.Departments.AsNoTracking().OrderBy(d => d.Name).ToListAsync();
        var rows = await query.OrderByDescending(r => r.Id).Skip((PageNumber - 1) * PageSize).Take(PageSize + 1).ToListAsync();
        HasMore = rows.Count > PageSize;
        Runs = rows.Take(PageSize).ToList();
    }
}
