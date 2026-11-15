using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Pages.Admin;

public class DepartmentsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly SchoolFlow.Web.Services.AuditService _audit;

    public DepartmentsModel(AppDbContext db, SchoolFlow.Web.Services.AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public List<Department> Departments { get; private set; } = new();
    public Dictionary<int, int> RunCounts { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Departments = await _db.Departments.AsNoTracking().OrderByDescending(d => d.IsActive).ThenBy(d => d.Name).ToListAsync();
        RunCounts = (await _db.Runs.Where(r => r.DepartmentId != null).GroupBy(r => r.DepartmentId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync()).ToDictionary(x => (int)x.Key, x => x.Count);
    }

    public async Task<IActionResult> OnPostSaveAsync(int? id, string? name, string? approvers, string? rule, bool active)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 200) return Fail("Give the department a name (up to 200 characters).");

        var entries = (approvers ?? "").Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().ToLowerInvariant()).Distinct().ToList();
        var invalid = entries.Where(e => !System.Text.RegularExpressions.Regex.IsMatch(e, @"^[^@\s]+@[^@\s]+\.[^@\s]+$") || e.Length > 320).ToList();
        if (invalid.Count > 0) return Fail($"Not an email address: {string.Join(", ", invalid.Take(3))}");
        if (entries.Count > 10) return Fail("A department can have at most 10 final approvers.");

        if (await _db.Departments.AnyAsync(d => d.Name == name && d.Id != (id ?? 0)))
            return Fail($"There is already a department called “{name}”.");

        Department department;
        if (id is int existing)
        {
            var found = await _db.Departments.FirstOrDefaultAsync(d => d.Id == existing);
            if (found is null) return NotFound();
            department = found;
            department.IsActive = active;
        }
        else
        {
            department = new Department { IsActive = true };
            _db.Departments.Add(department);
        }
        var newApprovers = entries.Count == 0 ? "" : ApprovalTask.Pack(entries);
        var newRule = ApprovalRules.Normalize(rule);
        // Final approvers decide requests and can see their department's requests, so changes are recorded.
        if (id is null)
            _audit.Record(User, "department.created", name, $"Final approvers: {(entries.Count == 0 ? "none" : string.Join(", ", entries))} ({newRule}).");
        else if (department.FinalApprovers != newApprovers || department.FinalRule != newRule)
            _audit.Record(User, "department.approvers", name,
                $"Final approvers changed from [{string.Join(", ", department.FinalApproverList)}] ({department.FinalRule}) to [{string.Join(", ", entries)}] ({newRule}).");
        department.Name = name;
        department.FinalApprovers = newApprovers;
        department.FinalRule = newRule;
        await _db.SaveChangesAsync();
        TempData["Flash"] = $"Saved {name}.";
        return RedirectToPage();
    }

    private IActionResult Fail(string message)
    {
        TempData["Flash"] = message;
        TempData["FlashKind"] = "bad";
        return RedirectToPage();
    }
}
