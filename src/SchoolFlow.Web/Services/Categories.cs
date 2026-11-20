using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>
/// The categories forms are grouped under (Admin → Categories). A workflow keeps its category by name, so
/// a rename here renames it on every workflow; the list sets the choices in the builder and the order on Home.
/// </summary>
public static class Categories
{
    public const int MaxLength = 100;

    /// <summary>On start: an empty list is filled from the categories workflows already use, so nothing is lost.</summary>
    public static void EnsureFromWorkflows(AppDbContext db)
    {
        if (db.Categories.Any()) return;
        var used = db.Workflows.Select(w => w.Category).Where(c => c != "").Distinct().ToList()
            .Select(c => c.Trim()).Where(c => c.Length is > 0 and <= MaxLength).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < used.Count; i++) db.Categories.Add(new WorkflowCategory { Name = used[i], SortOrder = (i + 1) * 10 });
        if (used.Count > 0) db.SaveChanges();
    }

    /// <summary>The category names in the admins' order.</summary>
    public static Task<List<string>> NamesAsync(AppDbContext db, CancellationToken ct = default) =>
        db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(c => c.Name).ToListAsync(ct);
}
