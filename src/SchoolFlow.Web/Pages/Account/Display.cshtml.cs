using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SchoolFlow.Web.Pages.Account;

/// <summary>Each person's own display choice. Kept in a cookie on their device: it is a preference, not a record about them.</summary>
public class DisplayModel : PageModel
{
    public const string CookieName = "sf_a11y";

    public bool On => Request.Cookies[CookieName] == "1";

    public void OnGet() { }

    public IActionResult OnPost(bool on, string? returnUrl)
    {
        if (on)
            Response.Cookies.Append(CookieName, "1", new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(2), HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax, Secure = Request.IsHttps,
            });
        else
            Response.Cookies.Delete(CookieName);
        TempData["Flash"] = on ? "Accessibility mode is on for this device." : "Accessibility mode is off.";
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToPage();
    }
}
