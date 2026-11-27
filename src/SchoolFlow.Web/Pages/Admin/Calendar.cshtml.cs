using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Admin;

public class CalendarModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly SchoolCalendar _calendar;
    private readonly AuditService _audit;

    public CalendarModel(AppDbContext db, SchoolCalendar calendar, AuditService audit)
    {
        _db = db;
        _calendar = calendar;
        _audit = audit;
    }

    /// <summary>Consecutive closed weekdays with the same name, shown as one closure.</summary>
    public sealed record Closure(string Name, DateTime First, DateTime Last, int Days);
    public List<Closure> Closures { get; private set; } = new();

    public async Task OnGetAsync()
    {
        var days = await _db.ClosedDays.AsNoTracking().OrderBy(d => d.Date).ToListAsync();
        foreach (var day in days)
        {
            var last = Closures.LastOrDefault();
            // "Consecutive" allows for the weekend in between (Friday then Monday).
            if (last is not null && last.Name == day.Name && SchoolCalendar.SchoolDaysBetween(last.Last, day.Date, new HashSet<DateTime>()) <= 1)
                Closures[^1] = last with { Last = day.Date, Days = last.Days + 1 };
            else
                Closures.Add(new Closure(day.Name, day.Date, day.Date, 1));
        }
        Closures.Reverse(); // latest first
    }

    public async Task<IActionResult> OnPostAddAsync(string? name, DateTime? from, DateTime? to)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 200) return Flash("Say what the closure is (up to 200 characters).", false);
        if (from is null) return Flash("Choose the first day.", false);
        var first = from.Value.Date;
        var last = (to ?? from).Value.Date;
        if (last < first) return Flash("The last day is before the first day.", false);
        if ((last - first).TotalDays > 120) return Flash("A single closure can cover at most 120 days. Add longer ones in parts.", false);

        var existing = (await _db.ClosedDays.Where(d => d.Date >= first && d.Date <= last).Select(d => d.Date).ToListAsync()).ToHashSet();
        var added = 0;
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || existing.Contains(day)) continue;
            _db.ClosedDays.Add(new ClosedDay { Date = day, Name = name });
            added++;
        }
        if (added == 0) return Flash("Nothing to add: those days are weekends or already marked closed.", false);
        _audit.Record(User, "calendar.closed", name, $"{added} school day(s) from {first:MMM d, yyyy} to {last:MMM d, yyyy} marked closed.");
        await _db.SaveChangesAsync();
        _calendar.Invalidate();
        return Flash($"Added {name}: {added} school day{(added == 1 ? "" : "s")} marked closed.", true);
    }

    public async Task<IActionResult> OnPostRemoveAsync(string? name, DateTime? from, DateTime? to)
    {
        if (from is null || to is null) return BadRequest();
        var days = await _db.ClosedDays.Where(d => d.Date >= from.Value.Date && d.Date <= to.Value.Date && d.Name == name).ToListAsync();
        if (days.Count == 0) return NotFound();
        _db.ClosedDays.RemoveRange(days);
        _audit.Record(User, "calendar.reopened", name ?? "", $"{days.Count} day(s) from {from:MMM d, yyyy} to {to:MMM d, yyyy} count as school days again.");
        await _db.SaveChangesAsync();
        _calendar.Invalidate();
        return Flash($"Removed {name}.", true);
    }

    private IActionResult Flash(string message, bool ok)
    {
        TempData["Flash"] = message;
        TempData["FlashKind"] = ok ? "ok" : "bad";
        return RedirectToPage();
    }
}
