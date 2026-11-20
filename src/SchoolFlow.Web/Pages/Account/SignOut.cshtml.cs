using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SchoolFlow.Web.Pages.Account;

/// <summary>
/// Signing out is a POST with the anti-forgery token, so another site cannot sign people out by
/// linking to it. Opening the address directly shows a button that does the same.
/// </summary>
public class SignOutModel : PageModel
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public SignOutModel(IConfiguration config, IWebHostEnvironment env)
    {
        _config = config;
        _env = env;
    }

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? Page() : Redirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var devAuth = _env.IsDevelopment() && string.Equals(_config["Auth:Mode"], "Dev", StringComparison.OrdinalIgnoreCase);
        if (devAuth) return Redirect("/Account/DevLogin");
        return SignOut(new AuthenticationProperties { RedirectUri = "/" }, OpenIdConnectDefaults.AuthenticationScheme);
    }
}
