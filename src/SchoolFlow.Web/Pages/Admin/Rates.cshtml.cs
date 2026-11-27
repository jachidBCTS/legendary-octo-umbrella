using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Admin;

/// <summary>Fixed amounts (mileage rate, meal rates…) that forms use as <c>rates.key</c>, kept by admins.</summary>
public class RatesModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly SchoolCalendar _reference;
    private readonly AuditService _audit;

    public RatesModel(AppDbContext db, SchoolCalendar reference, AuditService audit)
    {
        _db = db;
        _reference = reference;
        _audit = audit;
    }

    public List<Rate> Rates { get; private set; } = new();

    public async Task OnGetAsync() => Rates = await _db.Rates.AsNoTracking().OrderBy(r => r.Label).ToListAsync();

    private IActionResult Flash(string message, bool ok)
    {
        TempData["Flash"] = message;
        if (!ok) TempData["FlashKind"] = "bad";
        return RedirectToPage();
    }

    private static bool TryAmount(string? text, out decimal amount) =>
        decimal.TryParse((text ?? "").Trim().TrimStart('$'), NumberStyles.Number, CultureInfo.GetCultureInfo("en-US"), out amount) && amount >= 0 && amount <= 1_000_000;

    private static string Show(decimal value) => value.ToString("0.00##", CultureInfo.GetCultureInfo("en-US"));

    public async Task<IActionResult> OnPostSaveAsync(int id, string? label, string? value)
    {
        var rate = await _db.Rates.FirstOrDefaultAsync(r => r.Id == id);
        if (rate is null) return NotFound();
        label = (label ?? "").Trim();
        if (label.Length == 0 || label.Length > 200) return Flash("Give the rate a name (up to 200 characters).", false);
        if (!TryAmount(value, out var amount)) return Flash($"“{label}”: enter an amount such as 0.70 or 59.", false);
        if (amount == rate.Value && label == rate.Label) return Flash("Nothing changed.", true);
        _audit.Record(User, "rate.changed", label, amount == rate.Value ? $"Renamed from “{rate.Label}”." : $"Changed from {Show(rate.Value)} to {Show(amount)}.");
        rate.Label = label;
        rate.Value = amount;
        rate.UpdatedAt = DateTime.UtcNow;
        rate.UpdatedBy = User.Email();
        await _db.SaveChangesAsync();
        _reference.InvalidateRates();
        return Flash($"Saved. New requests use {Show(amount)} for “{label}”; requests already submitted keep the rate they were submitted with.", true);
    }

    public async Task<IActionResult> OnPostAddAsync(string? key, string? label, string? value)
    {
        key = (key ?? "").Trim().ToLowerInvariant();
        label = (label ?? "").Trim();
        if (!DefinitionValidator.IsValidKey(key) || key.Length > 60) return Flash("The short name must start with a letter and use only letters, digits and underscores, for example mileage.", false);
        if (label.Length == 0 || label.Length > 200) return Flash("Give the rate a name (up to 200 characters).", false);
        if (!TryAmount(value, out var amount)) return Flash("Enter an amount such as 0.70 or 59.", false);
        if (await _db.Rates.AnyAsync(r => r.Key == key)) return Flash($"There is already a rate called “{key}”.", false);
        _db.Rates.Add(new Rate { Key = key, Label = label, Value = amount, UpdatedAt = DateTime.UtcNow, UpdatedBy = User.Email() });
        _audit.Record(User, "rate.added", label, $"rates.{key} = {Show(amount)}.");
        await _db.SaveChangesAsync();
        _reference.InvalidateRates();
        return Flash($"Added. Use it in a form's formulas as rates.{key}.", true);
    }

    public async Task<IActionResult> OnPostRemoveAsync(int id)
    {
        var rate = await _db.Rates.FirstOrDefaultAsync(r => r.Id == id);
        if (rate is null) return NotFound();
        if (rate.Key is PerDiemService.StandardMealsKey or PerDiemService.StandardLodgingKey)
            return Flash($"“{rate.Label}” is used whenever gsa.gov cannot be reached, so it cannot be removed. Keep it up to date instead.", false);
        // A form that still uses it would quietly work out to nothing.
        var users = (await _db.Workflows.AsNoTracking().Select(w => new { w.Name, w.DraftJson, w.PublishedJson }).ToListAsync())
            .Where(w => (w.DraftJson + w.PublishedJson).Contains("rates." + rate.Key, StringComparison.OrdinalIgnoreCase)).Select(w => w.Name).ToList();
        if (users.Count > 0) return Flash($"“{rate.Label}” is used by {string.Join(", ", users)}. Change those forms first.", false);
        _db.Rates.Remove(rate);
        _audit.Record(User, "rate.removed", rate.Label, $"rates.{rate.Key} was {Show(rate.Value)}.");
        await _db.SaveChangesAsync();
        _reference.InvalidateRates();
        return Flash($"Removed “{rate.Label}”.", true);
    }
}
