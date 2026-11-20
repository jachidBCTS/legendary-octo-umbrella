using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Admin;

/// <summary>Who may use student information, and whether the roster (on the district's SQL Server) can be read.</summary>
public class StudentAccessModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;
    private readonly IStudentRoster _roster;

    private readonly StudentScopeService _scope;
    private readonly IStaffDirectory _staff;

    public StudentAccessModel(AppDbContext db, AuditService audit, IStudentRoster roster, StudentScopeService scope, IStaffDirectory staff)
    {
        _scope = scope;
        _staff = staff;
        _db = db;
        _audit = audit;
        _roster = roster;
    }

    public List<AppUser> Allowed { get; private set; } = new();
    /// <summary>Whether a roster is connected (Students:ConnectionString), and the result of "Check the connection".</summary>
    public bool RosterConfigured => _roster.IsConfigured;
    public string? RosterCheck { get; private set; }
    public string? StaffCheck { get; private set; }
    public bool StaffConfigured => _staff.IsConfigured;
    /// <summary>Where the staff list says each person with access works ("Teterboro (BCTS)"), or why it is unknown.</summary>
    public Dictionary<string, string> WorksAt { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> FormsUsingStudents { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Allowed = await _db.Users.AsNoTracking().Where(u => u.StudentAccess).OrderBy(u => u.Name).ThenBy(u => u.Email).ToListAsync();
        foreach (var u in Allowed)
        {
            try
            {
                var places = await _scope.PlacesAsync(u.Email);
                WorksAt[u.Email] = places.Count == 0 ? "Not on the staff list"
                    : string.Join(", ", places.Select(p => $"{p.Location} ({p.District})").Distinct());
            }
            catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException) { WorksAt[u.Email] = "Staff list could not be read"; }
        }
        FormsUsingStudents = (await _db.Workflows.AsNoTracking().Select(w => new { w.Name, w.DraftJson, w.PublishedJson }).ToListAsync())
            .Where(w => (w.DraftJson + w.PublishedJson).Contains("\"type\":\"student\"")).Select(w => w.Name).OrderBy(n => n).ToList();
    }

    /// <summary>Reads the roster once and says how many students it offers, or why it cannot be read.</summary>
    public async Task<IActionResult> OnPostCheckAsync(CancellationToken ct)
    {
        await OnGetAsync();
        RosterCheck = await _roster.TestAsync(ct);
        StaffCheck = await _staff.TestAsync(ct);
        return Page();
    }

    private IActionResult Flash(string message, bool ok)
    {
        TempData["Flash"] = message;
        if (!ok) TempData["FlashKind"] = "bad";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostGrantAsync(string? email, string? scope)
    {
        scope = StudentScopes.Normalize(scope);
        email = (email ?? "").Trim().ToLowerInvariant();
        if (email.Length == 0 || email.Length > 320 || !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            return Flash("Enter the person's email address.", false);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            // Someone who has not signed in yet: the flag is waiting for them when they do.
            user = new AppUser { Email = email, Name = "", Role = Roles.Staff };
            _db.Users.Add(user);
        }
        if (user.StudentAccess) return Flash($"{email} already has access to student information. Change how far it reaches in the list below.", true);
        user.StudentAccess = true;
        user.StudentScope = scope;
        _audit.Record(User, "students.access-granted", email, StudentScopes.Describe(scope));
        await _db.SaveChangesAsync();
        return Flash($"{email} can now look up students ({StudentScopes.Describe(scope).ToLowerInvariant()}) and see them on requests.", true);
    }

    /// <summary>Changes how far a person's student access reaches: own location, whole district or all districts.</summary>
    public async Task<IActionResult> OnPostScopeAsync(string? email, string? scope)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        scope = StudentScopes.Normalize(scope);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null || !user.StudentAccess) return RedirectToPage();
        if (user.StudentScope == scope) return Flash("Nothing changed.", true);
        _audit.Record(User, "students.access-scope", email, $"{StudentScopes.Describe(user.StudentScope)} → {StudentScopes.Describe(scope)}");
        user.StudentScope = scope;
        await _db.SaveChangesAsync();
        return Flash($"{email} now sees students: {StudentScopes.Describe(scope).ToLowerInvariant()}.", true);
    }

    public async Task<IActionResult> OnPostRevokeAsync(string? email)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null || !user.StudentAccess) return RedirectToPage();
        user.StudentAccess = false;
        _audit.Record(User, "students.access-removed", email);
        await _db.SaveChangesAsync();
        return Flash($"{email} no longer has access to student information.", true);
    }
}
