using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Controllers;

/// <summary>
/// What is new for the signed-in person since a notification they already know about. Every open page
/// asks every 30 seconds (`common.js`), to update the bell's count, play a sound and show a desktop
/// pop-up. Only the person's own notifications, at most 5, plain text.
/// </summary>
[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api/notifications")]
public sealed class NotificationsController : ControllerBase
{
    private readonly AppDbContext _db;
    public NotificationsController(AppDbContext db) => _db = db;

    /// <param name="after">The newest notification this device has seen; 0 = just tell me where things stand (nothing to announce).</param>
    [HttpGet("new")]
    public async Task<IActionResult> New(int after, CancellationToken ct)
    {
        var email = User.Email();
        if (email.Length == 0) return new JsonResult(new { latest = 0, unread = 0, items = Array.Empty<object>() });
        var mine = _db.Notifications.AsNoTracking().Where(n => n.UserEmail == email);
        var latest = await mine.MaxAsync(n => (int?)n.Id, ct) ?? 0;
        var unread = await mine.CountAsync(n => n.ReadAt == null, ct);
        var items = after <= 0 ? new List<object>() : (await mine.Where(n => n.Id > after && n.ReadAt == null).OrderByDescending(n => n.Id).Take(5)
            .Select(n => new { n.Id, n.Title, n.Body }).ToListAsync(ct))
            .Select(n => (object)new { id = n.Id, title = n.Title, body = n.Body.Length > 200 ? n.Body[..200] + "…" : n.Body, link = "/Notifications?open=" + n.Id }).ToList();
        return new JsonResult(new { latest, unread, items });
    }
}
