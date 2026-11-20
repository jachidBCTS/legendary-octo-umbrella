using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

public static class UserExtensions
{
    private static readonly string[] EmailClaims =
    {
        "preferred_username", "email", ClaimTypes.Email, ClaimTypes.Upn, "upn", "unique_name",
    };

    /// <summary>Lower-case email of the signed-in person, or "" when it cannot be determined.</summary>
    public static string Email(this ClaimsPrincipal user)
    {
        foreach (var type in EmailClaims)
        {
            var value = user.FindFirst(type)?.Value;
            if (!string.IsNullOrWhiteSpace(value) && value.Contains('@')) return value.Trim().ToLowerInvariant();
        }
        return "";
    }

    public static string DisplayName(this ClaimsPrincipal user)
    {
        var name = user.FindFirst("name")?.Value ?? user.FindFirst(ClaimTypes.GivenName)?.Value;
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        var email = user.Email();
        return email.Length > 0 ? email.Split('@')[0] : "Unknown";
    }

    /// <summary>
    /// The Entra group IDs in the person's sign-in token (lower-case). Needs the "groups" claim to be
    /// switched on for the app registration; without it this is simply empty.
    /// </summary>
    public static HashSet<string> Groups(this ClaimsPrincipal user) =>
        user.FindAll("groups").Select(c => c.Value.Trim().ToLowerInvariant()).Where(v => v.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>An app role assigned in Entra itself ("Admin" or "Builder"), if the token carries one.</summary>
    public static string EntraRole(this ClaimsPrincipal user)
    {
        var roles = user.FindAll("roles").Select(c => c.Value).ToList();
        return roles.Contains(Roles.Admin, StringComparer.OrdinalIgnoreCase) ? Roles.Admin
            : roles.Contains(Roles.Builder, StringComparer.OrdinalIgnoreCase) ? Roles.Builder : "";
    }

    /// <summary>
    /// True when Entra left the group list out because the person is in too many groups (over about 200).
    /// Group-based roles and form audiences cannot work for them; Entra app roles still do.
    /// </summary>
    public static bool HasGroupOverage(this ClaimsPrincipal user) =>
        user.HasClaim(c => c.Type == "hasgroups") || (user.FindFirst("_claim_names")?.Value.Contains("groups") ?? false);

    public static bool IsBuilder(this ClaimsPrincipal user) => user.IsInRole(Roles.Builder) || user.IsInRole(Roles.Admin);
    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Roles.Admin);
}

/// <summary>
/// Adds the app role (Staff / Builder / Admin) to the signed-in user. A role can come from the Users
/// table (set by hand), a registered Entra group, an Entra app role, or the Auth:Admins setting (so
/// nobody can lock themselves out); the highest one wins.
/// </summary>
public sealed class RoleClaimsTransformation : IClaimsTransformation
{
    private const string Marker = "schoolflow_role_loaded";
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _config;
    private readonly GroupDirectory _groups;
    private readonly ILogger<RoleClaimsTransformation> _log;

    public RoleClaimsTransformation(AppDbContext db, IMemoryCache cache, IConfiguration config, GroupDirectory groups, ILogger<RoleClaimsTransformation> log)
    {
        _db = db;
        _cache = cache;
        _config = config;
        _groups = groups;
        _log = log;
    }

    public static string CacheKey(string email) => "role:" + email;

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == Marker)) return principal;
        var email = principal.Email();
        if (email.Length == 0) return principal;

        var groups = principal.Groups();
        var entraRole = principal.EntraRole();
        var manualRole = await _cache.GetOrCreateAsync(CacheKey(email), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            if (principal.HasGroupOverage())
                _log.LogWarning("{Email} is in too many Entra groups for them to be listed at sign-in; group-based roles and form audiences will not apply to them. Use Entra app roles for this person.", email);
            return await TouchUserAsync(email, principal.DisplayName(), groups, entraRole);
        }) ?? Roles.Staff;

        var role = RoleResolver.Resolve(manualRole, IsConfiguredAdmin(email), entraRole, groups, await _groups.RoleGrantsAsync()).Role;

        var identity = new ClaimsIdentity();
        identity.AddClaim(new Claim(Marker, "1"));
        identity.AddClaim(new Claim(ClaimTypes.Role, role));
        // The primary identity decides IsInRole's claim type, so add the role there as well.
        if (principal.Identity is ClaimsIdentity primary) primary.AddClaim(new Claim(primary.RoleClaimType, role));
        principal.AddIdentity(identity);
        return principal;
    }

    private bool IsConfiguredAdmin(string email) =>
        (_config.GetSection("Auth:Admins").Get<string[]>() ?? Array.Empty<string>())
        .Any(a => string.Equals(a?.Trim(), email, StringComparison.OrdinalIgnoreCase));

    /// <summary>Records that the person was seen (name, groups, Entra role) and returns the role set for them by hand.</summary>
    private async Task<string> TouchUserAsync(string email, string name, HashSet<string> groups, string entraRole)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            user = new AppUser { Email = email, Name = name, Role = Roles.Staff };
            _db.Users.Add(user);
        }
        user.LastSeenAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(name)) user.Name = name.Length > 300 ? name[..300] : name;
        user.Groups = groups.Count == 0 ? "" : ApprovalTask.Pack(groups.OrderBy(g => g).Take(400));
        user.EntraRole = entraRole;
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Two first requests from the same new user raced; the other one created the row.
            _db.ChangeTracker.Clear();
            user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email) ?? user;
        }
        return Array.IndexOf(Roles.All, user.Role) >= 0 ? user.Role : Roles.Staff;
    }
}
