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
    public AccessService(AppDbContext db) => _db = db;

    public static bool CanManage(ClaimsPrincipal user, Workflow workflow) =>
        user.IsAdmin() || (user.IsBuilder() && workflow.IsOwnedBy(user.Email()));

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
        if (!user.IsBuilder() || email.Length == 0) return new List<int>();
        var needle = ";" + email + ";";
        return await _db.Workflows.AsNoTracking().Where(w => w.Owners.Contains(needle)).Select(w => w.Id).ToListAsync(ct);
    }

    public async Task<bool> CanManageRunAsync(ClaimsPrincipal user, WorkflowRun run, CancellationToken ct = default)
    {
        if (user.IsAdmin()) return true;
        if (!user.IsBuilder()) return false;
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
        if (await _db.ApprovalTasks.AsNoTracking().AnyAsync(t => t.RunId == run.Id && t.Assignees.Contains(needle), ct)) return true;
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
