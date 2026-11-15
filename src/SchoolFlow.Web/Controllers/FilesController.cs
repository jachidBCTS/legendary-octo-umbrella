using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Controllers;

/// <summary>Downloads of uploaded files, for the people allowed to see the request they belong to.</summary>
[Authorize]
public sealed class FilesController : Controller
{
    private readonly AppDbContext _db;
    private readonly AttachmentService _attachments;

    public FilesController(AppDbContext db, AttachmentService attachments)
    {
        _db = db;
        _attachments = attachments;
    }

    [HttpGet("/files/{id}")]
    public async Task<IActionResult> Download(string id, CancellationToken ct)
    {
        if (id.Length > 64) return NotFound();
        var row = await _db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.PublicId == id, ct);
        // "Not allowed" and "does not exist" look the same, so links cannot be used to probe for files.
        if (row is null || !await CanSeeAsync(row, ct)) return NotFound();

        var opened = await _attachments.OpenAsync(id, ct);
        if (opened is null)
            return NotFound("This file is no longer available on the server. Please ask the person who uploaded it to send it again.");

        // Always a download, never shown inside the site: an uploaded file must not be able to run as a page here.
        return File(opened.Value.Stream, row.ContentType, row.FileName);
    }

    /// <summary>
    /// The uploader can always get their own file back. Once it is part of a request, so can anyone who
    /// can open that request: the requester, anyone asked to approve it, builders and admins.
    /// </summary>
    private async Task<bool> CanSeeAsync(Attachment attachment, CancellationToken ct)
    {
        var email = User.Email();
        if (email.Length > 0 && attachment.OwnerEmail == email) return true;
        if (attachment.RunId is not int runId) return false;
        if (User.IsBuilder()) return true;
        if (email.Length == 0) return false;
        var tasks = await _db.ApprovalTasks.AsNoTracking().Where(t => t.RunId == runId).ToListAsync(ct);
        return tasks.Any(t => t.IsAssignedTo(email));
    }
}
