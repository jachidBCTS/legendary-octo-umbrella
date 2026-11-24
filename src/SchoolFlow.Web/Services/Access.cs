using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SchoolFlow.Core;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>Works out a person's role from every place one can come from. The highest wins.</summary>
public static class RoleResolver
{
    public sealed record Result(string Role, string Source);

    private static int Rank(string role) => role switch { Roles.Admin => 2, Roles.Builder => 1, _ => 0 };

    /// <param name="manualRole">The role set by hand on the Settings page.</param>
    /// <param name="isConfiguredAdmin">Listed under Auth:Admins in the server's settings.</param>
    /// <param name="entraRole">An app role assigned in Entra ("Admin" or "Builder"), if any.</param>
    /// <param name="userGroups">Entra group IDs the person belongs to.</param>
    /// <param name="groupRoles">Registered groups that grant a role: group ID → (role, group name).</param>
    public static Result Resolve(string? manualRole, bool isConfiguredAdmin, string? entraRole,
        IEnumerable<string> userGroups, IReadOnlyDictionary<string, (string Role, string Name)> groupRoles)
    {
        var best = new Result(Roles.Staff, "default");
        void Offer(string? role, string source)
        {
            if (role is Roles.Admin or Roles.Builder && Rank(role) > Rank(best.Role)) best = new Result(role, source);
        }
        Offer(manualRole, "set by an admin");
        foreach (var group in userGroups)
            if (groupRoles.TryGetValue(group.Trim().ToLowerInvariant(), out var granted)) Offer(granted.Role, $"Entra group “{granted.Name}”");
        Offer(entraRole, "Entra app role");
        if (isConfiguredAdmin) Offer(Roles.Admin, "server settings");
        return best;
    }
}

/// <summary>
/// The one place that answers "may this person do that?" for workflows and requests, so the pages,
/// the APIs and file downloads can never disagree.
///
///  - Submit a form: everyone, unless the form is limited to certain Entra groups.
///  - Edit a workflow / see all of its requests / retry or cancel them: its owners and admins.
///  - See one request: its requester, anyone asked to approve it, the workflow's owners, the final
///    approvers of the department it is filed under, and admins.
/// </summary>
public sealed class AccessService
{
    private readonly AppDbContext _db;
    private readonly IStudentRoster _roster;

    public AccessService(AppDbContext db, IStudentRoster? roster = null)
    {
        _db = db;
        _roster = roster ?? new NoStudentRoster();
    }

    /// <summary>Whether this person has been given access to student information (a flag an admin sets per person).</summary>
    public Task<bool> CanViewStudentsAsync(ClaimsPrincipal user, CancellationToken ct = default) => CanViewStudentsAsync(user.Email(), ct);

    public async Task<bool> CanViewStudentsAsync(string email, CancellationToken ct = default) =>
        email.Length > 0 && await _db.Users.AsNoTracking().AnyAsync(u => u.Email == email && u.StudentAccess, ct);

    /// <summary>
    /// The roster entries a form's answers refer to, for the evaluator — or null when the form has no
    /// student field. Someone without access gets an empty directory, so their answer is refused.
    /// </summary>
    public async Task<SchoolFlow.Core.StudentDirectory?> StudentsForAsync(SchoolFlow.Core.FormDefinition form, IDictionary<string, object?> input, string email, CancellationToken ct = default,
        StudentScopeService? scope = null)
    {
        var fields = form.Fields.Where(f => f.Type == SchoolFlow.Core.FieldTypes.Student).ToList();
        if (fields.Count == 0) return null;
        var known = new Dictionary<string, SchoolFlow.Core.StudentInfo>(StringComparer.OrdinalIgnoreCase);
        if (!await CanViewStudentsAsync(email, ct)) return new SchoolFlow.Core.StudentDirectory(false, known);
        var lookup = input.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var ids = fields.Select(f => SchoolFlow.Core.Forms.FormEvaluator.StudentIdOf(lookup.GetValueOrDefault(f.Key))).Where(id => id.Length > 0).Distinct().ToList();
        // Looked up live in the district's roster; nothing about students is stored here except what a request keeps.
        // Only students this person may see count as found (their location, district, or all districts).
        var filter = scope is null ? StudentFilter.Everyone : await scope.FilterForAsync(email, ct) ?? StudentFilter.Nobody;
        if (ids.Count > 0)
            foreach (var (id, student) in await _roster.FindAsync(ids, ct))
                if (filter.Allows(student)) known[id] = student;
        return new SchoolFlow.Core.StudentDirectory(true, known);
    }

    /// <summary>
    /// Admins, and the people an admin named as the workflow's owners. Owners can change the flow
    /// (steps, approvers, tasks, notifications) and publish it; the form itself is admin-only
    /// (see <see cref="CanEditForm"/>).
    /// </summary>
    public static bool CanManage(ClaimsPrincipal user, Workflow workflow) =>
        user.IsAdmin() || workflow.IsOwnedBy(user.Email());

    /// <summary>Creating forms and changing their questions, name, owners or audience: administrators only.</summary>
    public static bool CanEditForm(ClaimsPrincipal user) => user.IsAdmin();

    /// <summary>Whether this person looks after at least one workflow (for the Build menu).</summary>
    public async Task<bool> OwnsAnyWorkflowAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var email = user.Email();
        if (email.Length == 0) return false;
        var needle = ";" + email + ";";
        return await _db.Workflows.AsNoTracking().AnyAsync(w => w.Owners.Contains(needle), ct);
    }

    public static bool CanSubmit(ClaimsPrincipal user, Workflow workflow)
    {
        if (workflow.AudienceGroups.Length == 0) return true;
        // Owners and admins can always open a form they look after, to test it.
        if (CanManage(user, workflow)) return true;
        var mine = user.Groups();
        return workflow.AudienceList.Any(mine.Contains);
    }

    /// <summary>IDs of departments where this person is a final approver.</summary>
    public async Task<List<long>> DepartmentsApprovedByAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var email = user.Email();
        if (email.Length == 0) return new List<long>();
        var needle = ";" + email + ";";
        return (await _db.Departments.AsNoTracking().Where(d => d.FinalApprovers.Contains(needle)).Select(d => d.Id).ToListAsync(ct))
            .Select(id => (long)id).ToList();
    }

    /// <summary>IDs of workflows this person may manage; null means "all of them" (an admin).</summary>
    public async Task<List<int>?> ManagedWorkflowIdsAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (user.IsAdmin()) return null;
        var email = user.Email();
        if (email.Length == 0) return new List<int>();
        var needle = ";" + email + ";";
        return await _db.Workflows.AsNoTracking().Where(w => w.Owners.Contains(needle)).Select(w => w.Id).ToListAsync(ct);
    }

    public async Task<bool> CanManageRunAsync(ClaimsPrincipal user, WorkflowRun run, CancellationToken ct = default)
    {
        if (user.IsAdmin()) return true;
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == run.WorkflowId, ct);
        return workflow is not null && workflow.IsOwnedBy(user.Email());
    }

    public async Task<bool> CanViewRunAsync(ClaimsPrincipal user, WorkflowRun run, CancellationToken ct = default)
    {
        var email = user.Email();
        if (email.Length > 0 && run.SubmitterEmail == email) return true;
        if (await CanManageRunAsync(user, run, ct)) return true;
        if (email.Length == 0) return false;
        var needle = ";" + email + ";";
        // Asked to approve it — directly, as a stand-in, or after escalation.
        if (await _db.ApprovalTasks.AsNoTracking().AnyAsync(t => t.RunId == run.Id && (t.Assignees.Contains(needle) || t.Extra.Contains(needle)), ct)) return true;
        // Told that a step is overdue: the notice links to the request, so they can see what is late.
        if (await _db.ApprovalTasks.AsNoTracking().AnyAsync(t => t.RunId == run.Id && t.OverdueSentAt != null && t.DueNotify.Contains(needle), ct)) return true;
        return run.DepartmentId is long departmentId
               && await _db.Departments.AsNoTracking().AnyAsync(d => d.Id == departmentId && d.FinalApprovers.Contains(needle), ct);
    }
}

/// <summary>Writes the audit trail. Entries are added to the current database context and saved with the change they describe.</summary>
public sealed class AuditService
{
    private readonly AppDbContext _db;
    public AuditService(AppDbContext db) => _db = db;

    public void Record(ClaimsPrincipal actor, string action, string target, string detail = "") =>
        Record(actor.Email(), actor.DisplayName(), action, target, detail);

    public void Record(string actorEmail, string actorName, string action, string target, string detail = "")
    {
        static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
        _db.AuditEntries.Add(new AuditEntry
        {
            At = DateTime.UtcNow,
            ActorEmail = Clip(actorEmail, 320),
            ActorName = Clip(actorName, 300),
            Action = Clip(action, 60),
            Target = Clip(target, 300),
            Detail = Clip(detail, 2000),
        });
    }
}

/// <summary>The registered Entra groups, cached briefly because every request needs them to work out roles.</summary>
public sealed class GroupDirectory
{
    private const string CacheKey = "directory-groups";
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    public GroupDirectory(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<List<DirectoryGroup>> AllAsync() =>
        (await _cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return await _db.DirectoryGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
        }))!;

    public async Task<Dictionary<string, (string Role, string Name)>> RoleGrantsAsync() =>
        (await AllAsync()).Where(g => g.Role is Roles.Admin or Roles.Builder).ToDictionary(g => g.ObjectId, g => (g.Role, g.Name));

    public void Invalidate() => _cache.Remove(CacheKey);
}

public enum ApprovalAction
{
    Approve,
    Reject,
    /// <summary>Send the request back to the requester for changes.</summary>
    Return,
}
