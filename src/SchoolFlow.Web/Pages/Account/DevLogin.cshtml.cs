using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SchoolFlow.Web.Pages.Account;

public class DevLoginModel : PageModel
{
    public sealed record Persona(string Name, string Email, string Note, params string[] Groups);

    // Stand-ins for Entra group Object IDs; the matching groups are registered by the development seed data.
    public const string StaffGroup = "00000000-0000-0000-0000-000000000001";
    public const string ScienceGroup = "00000000-0000-0000-0000-000000000002";
    public const string BuildersGroup = "00000000-0000-0000-0000-000000000003";

    public static readonly Persona[] Personas =
    {
        new("Avery Admin", "admin@example.org", "admin (listed under Auth:Admins)", StaffGroup),
        new("Taylor Teacher", "teacher@example.org", "staff, in the Science group", StaffGroup, ScienceGroup),
        new("Pat Principal", "principal@example.org", "staff, use as an approver", StaffGroup),
        new("Bailey Builder", "builder@example.org", "builder through the “Workflow Builders” group", StaffGroup, BuildersGroup),
    };

    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public DevLoginModel(IConfiguration config, IWebHostEnvironment env)
    {
        _config = config;
        _env = env;
    }

    public string ReturnUrl { get; private set; } = "/";

    /// <summary>
    /// Development mode only, and only for a browser on the same machine. The second check means that
    /// even a server wrongly switched to Development never offers this page to the network.
    /// </summary>
    private bool Enabled =>
        _env.IsDevelopment() && string.Equals(_config["Auth:Mode"], "Dev", StringComparison.OrdinalIgnoreCase)
        && HttpContext.Connection.RemoteIpAddress is { } remote
        && (System.Net.IPAddress.IsLoopback(remote) || remote.Equals(HttpContext.Connection.LocalIpAddress));

    public IActionResult OnGet(string? returnUrl)
    {
        if (!Enabled) return NotFound();
        ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? email, string? custom, string? returnUrl)
    {
        if (!Enabled) return NotFound();
        var chosen = (string.IsNullOrWhiteSpace(custom) ? email : custom)?.Trim().ToLowerInvariant() ?? "";
        if (!chosen.Contains('@') || chosen.Length > 320) return RedirectToPage();
        var name = Personas.FirstOrDefault(p => p.Email == chosen)?.Name
                   ?? string.Join(' ', chosen.Split('@')[0].Split('.', '_').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        var claims = new List<Claim> { new("preferred_username", chosen), new("name", name) };
        // Like Entra's "groups" claim. Anyone signing in by typing an email is treated as ordinary staff.
        claims.AddRange((Personas.FirstOrDefault(p => p.Email == chosen)?.Groups ?? new[] { StaffGroup }).Select(g => new Claim("groups", g)));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, "name", ClaimTypes.Role);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
}
