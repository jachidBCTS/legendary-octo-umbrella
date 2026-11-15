using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SchoolFlow.Web.Pages.Account;

public class DevLoginModel : PageModel
{
    public sealed record Persona(string Name, string Email, string Note);

    public static readonly Persona[] Personas =
    {
        new("Avery Admin", "admin@example.org", "admin (listed under Auth:Admins)"),
        new("Taylor Teacher", "teacher@example.org", "staff"),
        new("Pat Principal", "principal@example.org", "staff, use as an approver"),
    };

    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public DevLoginModel(IConfiguration config, IWebHostEnvironment env)
    {
        _config = config;
        _env = env;
    }

    public string ReturnUrl { get; private set; } = "/";

    private bool Enabled => _env.IsDevelopment() && string.Equals(_config["Auth:Mode"], "Dev", StringComparison.OrdinalIgnoreCase);

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
        var identity = new ClaimsIdentity(new[] { new Claim("preferred_username", chosen), new Claim("name", name) },
            CookieAuthenticationDefaults.AuthenticationScheme, "name", ClaimTypes.Role);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
}
