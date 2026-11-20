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
    private readonly AccessService _access;

    public FilesController(AppDbContext db, AttachmentService attachments, AccessService access)
    {
        _db = db;
        _attachments = attachments;
        _access = access;
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

    /// <summary>Every file on a request in one .zip, for anyone who can open the request.</summary>
    [HttpGet("/files/run/{runId:int}/zip")]
    public async Task<IActionResult> Zip(int runId, CancellationToken ct)
    {
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || !await _access.CanViewRunAsync(User, run, ct)) return NotFound();
        var rows = await _db.Attachments.AsNoTracking().Where(a => a.RunId == runId).OrderBy(a => a.Id).ToListAsync(ct);
        if (rows.Count == 0) return NotFound();

        // Built in a temporary file (removed when the download ends) so large files never sit in memory.
        var temp = Path.Combine(Path.GetTempPath(), "schoolflow-" + Guid.NewGuid().ToString("N") + ".zip");
        var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        try
        {
            using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var missing = new List<string>();
                foreach (var row in rows)
                {
                    var opened = await _attachments.OpenAsync(row.PublicId, ct);
                    if (opened is null) { missing.Add(row.FileName); continue; }
                    await using var source = opened.Value.Stream;
                    var entry = zip.CreateEntry(UniqueName(AttachmentService.CleanName(row.FileName), used), System.IO.Compression.CompressionLevel.Fastest);
                    await using var target = entry.Open();
                    await source.CopyToAsync(target, ct);
                }
                if (missing.Count > 0)
                {
                    var note = zip.CreateEntry(UniqueName("missing-files.txt", used));
                    await using var writer = new StreamWriter(note.Open());
                    await writer.WriteAsync("These files are no longer on the server:\r\n" + string.Join("\r\n", missing));
                }
            }
            output.Position = 0;
            return File(output, "application/zip", $"request-{runId}-files.zip");
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    /// <summary>"report.pdf", then "report (2).pdf", "report (3).pdf"… so no file in the zip replaces another.</summary>
    public static string UniqueName(string name, HashSet<string> used)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "file";
        if (used.Add(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){extension}";
            if (used.Add(candidate)) return candidate;
        }
    }

    /// <summary>
    /// The uploader can always get their own file back. Once it is part of a request, so can anyone who
    /// can open that request (see <see cref="AccessService"/>).
    /// </summary>
    private async Task<bool> CanSeeAsync(Attachment attachment, CancellationToken ct)
    {
        var email = User.Email();
        if (email.Length > 0 && attachment.OwnerEmail == email) return true;
        if (attachment.RunId is not int runId) return false;
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        return run is not null && await _access.CanViewRunAsync(User, run, ct);
    }
}
