using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>A form's letterhead image: where it is served, and which files are accepted.</summary>
public static class Letterhead
{
    /// <summary>The address of a form's letterhead; the time stamp changes the address whenever the image changes.</summary>
    public static string UrlFor(int workflowId, DateTime updatedAt) => $"/forms/{workflowId}/letterhead?v={updatedAt.Ticks:x}";

    /// <summary>The image type from the file's first bytes (never from its name): PNG, JPEG, GIF or WebP; null otherwise. SVG is refused (it can carry script).</summary>
    public static string? ContentTypeOf(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "image/gif";
        if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        return null;
    }

    /// <summary>The letterhead address for a workflow, or null when it has none.</summary>
    public static async Task<string?> UrlAsync(AppDbContext db, int workflowId, CancellationToken ct = default) =>
        await db.FormImages.AsNoTracking().Where(i => i.WorkflowId == workflowId).Select(i => (DateTime?)i.UpdatedAt).FirstOrDefaultAsync(ct) is DateTime at
            ? UrlFor(workflowId, at) : null;
}

/// <summary>Serves letterheads to anyone signed in (they are a school's letterhead, not private).</summary>
[ApiController]
[Authorize]
public sealed class LetterheadController : ControllerBase
{
    private readonly AppDbContext _db;
    public LetterheadController(AppDbContext db) => _db = db;

    [HttpGet("/forms/{id:int}/letterhead")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var image = await _db.FormImages.AsNoTracking().FirstOrDefaultAsync(i => i.WorkflowId == id, ct);
        if (image is null) return NotFound();
        // The address changes with every new image, so the browser may keep it.
        Response.Headers.CacheControl = "private, max-age=604800";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(image.Data, image.ContentType);
    }
}
