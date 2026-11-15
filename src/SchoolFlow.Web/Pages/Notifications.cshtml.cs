using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages;

public class NotificationsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly EmailSettings _email;

    public NotificationsModel(AppDbContext db, EmailSettings email)
    {
        _db = db;
        _email = email;
    }

    public List<Notification> Items { get; private set; } = new();
    public bool WantsEmail { get; private set; } = true;
    public bool EmailEnabled => _email.IsEnabled;

    public async Task<IActionResult> OnGetAsync(int? open)
    {
        var email = User.Email();
        if (open is int id)
        {
            // Opening a notification marks it read, then goes to what it is about.
            var note = await _db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserEmail == email && email != "");
            if (note is null) return RedirectToPage();
            note.ReadAt ??= DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return note.Link.Length > 0 && Url.IsLocalUrl(note.Link) ? LocalRedirect(note.Link) : RedirectToPage();
        }
        Items = await _db.Notifications.AsNoTracking().Where(n => n.UserEmail == email && email != "")
            .OrderByDescending(n => n.Id).Take(100).ToListAsync();
        WantsEmail = await _db.Users.AsNoTracking().Where(u => u.Email == email).Select(u => (bool?)u.EmailNotifications).FirstOrDefaultAsync() ?? true;
        return Page();
    }

    public async Task<IActionResult> OnPostReadAllAsync()
    {
        var email = User.Email();
        var unread = await _db.Notifications.Where(n => n.UserEmail == email && email != "" && n.ReadAt == null).ToListAsync();
        foreach (var n in unread) n.ReadAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPreferenceAsync(bool email)
    {
        var address = User.Email();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == address);
        if (user is not null)
        {
            user.EmailNotifications = email;
            await _db.SaveChangesAsync();
            TempData["Flash"] = email ? "You will also get notifications by email." : "Email notifications are off. You will still see them here.";
        }
        return RedirectToPage();
    }
}
