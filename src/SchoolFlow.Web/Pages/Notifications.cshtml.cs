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
    public int? DigestMinute { get; private set; }
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
        var me = await _db.Users.AsNoTracking().Where(u => u.Email == email).Select(u => new { u.EmailNotifications, u.DigestMinute }).FirstOrDefaultAsync();
        WantsEmail = me?.EmailNotifications ?? true;
        DigestMinute = me?.DigestMinute;
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

    public async Task<IActionResult> OnPostPreferenceAsync(bool email, string? digest)
    {
        var address = User.Email();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == address);
        if (user is not null)
        {
            int? minute = int.TryParse(digest, out var m) && m >= 0 && m < 24 * 60 ? m : null;
            // Unticking email keeps the chosen summary time for when it is ticked again (the box is not posted then).
            if (email) user.DigestMinute = minute;
            var wasDigest = user.EmailNotifications && user.DigestMinute is not null;
            user.EmailNotifications = email;
            // Anything already held follows the new choice: sent now, or not emailed at all.
            if (!email || user.DigestMinute is null)
            {
                var held = await _db.Notifications.Where(n => n.UserEmail == address && n.EmailStatus == EmailStatus.Held).ToListAsync();
                foreach (var n in held) n.EmailStatus = email ? EmailStatus.Pending : EmailStatus.None;
            }
            await _db.SaveChangesAsync();
            TempData["Flash"] = !email ? "Email notifications are off. You will still see them here."
                : user.DigestMinute is int at ? $"You will get one summary email a day at {DateTime.Today.AddMinutes(at):h:mm tt} on school days (urgent ones still come right away)."
                : "You will get each notification by email right away.";
        }
        return RedirectToPage();
    }
}
