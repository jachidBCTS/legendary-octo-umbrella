using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Admin;

/// <summary>The categories forms are grouped under on Home, in order. Renaming one renames it on every workflow.</summary>
public class CategoriesModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public CategoriesModel(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public List<WorkflowCategory> List { get; private set; } = new();
    public Dictionary<string, int> Counts { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Categories workflows use that are not in the list (typed before the list existed, or imported).</summary>
    public List<string> Unlisted { get; private set; } = new();

    public async Task OnGetAsync()
    {
        List = await _db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
        foreach (var name in await _db.Workflows.AsNoTracking().Select(w => w.Category).ToListAsync())
            if (name.Length > 0) Counts[name] = Counts.GetValueOrDefault(name) + 1;
        Unlisted = Counts.Keys.Where(k => !List.Any(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))).OrderBy(k => k).ToList();
    }

    private static string? Clean(string? name, out string? error)
    {
        name = (name ?? "").Trim();
        error = name.Length is 0 or > Categories.MaxLength ? $"Give the category a name (up to {Categories.MaxLength} characters)." : null;
        return error is null ? name : null;
    }

    public async Task<IActionResult> OnPostAddAsync(string? name)
    {
        var clean = Clean(name, out var error);
        if (clean is null) return Flash(error!, false);
        if (await _db.Categories.AnyAsync(c => c.Name == clean)) return Flash($"There is already a category called “{clean}”.", false);
        var last = await _db.Categories.MaxAsync(c => (int?)c.SortOrder) ?? 0;
        _db.Categories.Add(new WorkflowCategory { Name = clean, SortOrder = last + 10 });
        _audit.Record(User, "category.added", clean, "Category added.");
        await _db.SaveChangesAsync();
        return Flash($"Added {clean}.", true);
    }

    public async Task<IActionResult> OnPostRenameAsync(int id, string? name)
    {
        var category = await _db.Categories.FirstOrDefaultAsync(c => c.Id == id);
        if (category is null) return NotFound();
        var clean = Clean(name, out var error);
        if (clean is null) return Flash(error!, false);
        if (clean == category.Name) return Flash("Nothing changed.", true);
        if (await _db.Categories.AnyAsync(c => c.Name == clean && c.Id != id)) return Flash($"There is already a category called “{clean}”.", false);
        var old = category.Name;
        var workflows = await _db.Workflows.Where(w => w.Category == old).ToListAsync();
        foreach (var w in workflows) w.Category = clean;
        category.Name = clean;
        _audit.Record(User, "category.renamed", clean, $"Renamed from “{old}”; {workflows.Count} workflow(s) moved with it.");
        await _db.SaveChangesAsync();
        return Flash($"Renamed to {clean}. {workflows.Count} workflow{(workflows.Count == 1 ? "" : "s")} moved with it.", true);
    }

    public async Task<IActionResult> OnPostMoveAsync(int id, int by)
    {
        var all = await _db.Categories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
        var index = all.FindIndex(c => c.Id == id);
        if (index < 0) return NotFound();
        var target = Math.Clamp(index + Math.Sign(by), 0, all.Count - 1);
        if (target != index)
        {
            var item = all[index];
            all.RemoveAt(index);
            all.Insert(target, item);
            for (var i = 0; i < all.Count; i++) all[i].SortOrder = (i + 1) * 10;
            await _db.SaveChangesAsync();
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(int id)
    {
        var category = await _db.Categories.FirstOrDefaultAsync(c => c.Id == id);
        if (category is null) return NotFound();
        var workflows = await _db.Workflows.Where(w => w.Category == category.Name).ToListAsync();
        foreach (var w in workflows) w.Category = "";
        _db.Categories.Remove(category);
        _audit.Record(User, "category.removed", category.Name, $"Removed; {workflows.Count} workflow(s) now have no category.");
        await _db.SaveChangesAsync();
        return Flash(workflows.Count == 0 ? $"Removed {category.Name}." : $"Removed {category.Name}. Its {workflows.Count} workflow{(workflows.Count == 1 ? " is" : "s are")} now under “Other”.", true);
    }

    private IActionResult Flash(string message, bool ok)
    {
        TempData["Flash"] = message;
        if (!ok) TempData["FlashKind"] = "bad";
        return RedirectToPage();
    }
}
