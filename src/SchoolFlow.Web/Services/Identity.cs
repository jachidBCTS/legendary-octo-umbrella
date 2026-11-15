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

    public static bool IsBuilder(this ClaimsPrincipal user) => user.IsInRole(Roles.Builder) || user.IsInRole(Roles.Admin);
    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Roles.Admin);
}

/// <summary>
/// Adds the app role (Staff / Builder / Admin) to the signed-in user. Roles live in the Users table;
/// emails listed under Auth:Admins in configuration are always Admin so nobody can lock themselves out.
/// </summary>
public sealed class RoleClaimsTransformation : IClaimsTransformation
{
    private const string Marker = "schoolflow_role_loaded";
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _config;

    public RoleClaimsTransformation(AppDbContext db, IMemoryCache cache, IConfiguration config)
    {
        _db = db;
        _cache = cache;
        _config = config;
    }

    public static string CacheKey(string email) => "role:" + email;

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == Marker)) return principal;
        var email = principal.Email();
        if (email.Length == 0) return principal;

        var role = await _cache.GetOrCreateAsync(CacheKey(email), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return await LoadRoleAsync(email, principal.DisplayName());
        }) ?? Roles.Staff;

        var identity = new ClaimsIdentity();
        identity.AddClaim(new Claim(Marker, "1"));
        identity.AddClaim(new Claim(ClaimTypes.Role, role));
        // The primary identity decides IsInRole's claim type, so add the role there as well.
        if (principal.Identity is ClaimsIdentity primary) primary.AddClaim(new Claim(primary.RoleClaimType, role));
        principal.AddIdentity(identity);
        return principal;
    }

    private async Task<string> LoadRoleAsync(string email, string name)
    {
        var configuredAdmins = _config.GetSection("Auth:Admins").Get<string[]>() ?? Array.Empty<string>();
        var isConfiguredAdmin = configuredAdmins.Any(a => string.Equals(a?.Trim(), email, StringComparison.OrdinalIgnoreCase));

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            user = new AppUser { Email = email, Name = name, Role = isConfiguredAdmin ? Roles.Admin : Roles.Staff };
            _db.Users.Add(user);
        }
        user.LastSeenAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(name)) user.Name = name.Length > 300 ? name[..300] : name;
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
        return isConfiguredAdmin ? Roles.Admin : (Array.IndexOf(Roles.All, user.Role) >= 0 ? user.Role : Roles.Staff);
    }
}
