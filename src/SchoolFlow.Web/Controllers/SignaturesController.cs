using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Controllers;

/// <summary>
/// Signature pictures: a person's own (for their settings page and the "Sign" preview), and the one that
/// signed a field of a request, for anyone who may open that request. Anything else is "not found".
/// </summary>
[Authorize]
public sealed class SignaturesController : Controller
{
    private readonly AppDbContext _db;
    private readonly AccessService _access;

    public SignaturesController(AppDbContext db, AccessService access)
    {
        _db = db;
        _access = access;
    }

    [HttpGet("/signatures/mine")]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var email = User.Email();
        if (await Signatures.CurrentAsync(_db, email, ct) is not int id) return NotFound();
        var image = await _db.SignatureImages.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.OwnerEmail == email, ct);
        return image is null ? NotFound() : Picture(image);
    }

    [HttpGet("/signatures/run/{runId:int}/{key}")]
    public async Task<IActionResult> OnRequest(int runId, string key, CancellationToken ct)
    {
        if (key.Length > 100) return NotFound();
        var run = await _db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || !await _access.CanViewRunAsync(User, run, ct)) return NotFound();
        if (Signatures.PictureFor(SchoolFlow.Core.Json.ToValues(run.ContextJson), key) is not int id) return NotFound();
        var image = await _db.SignatureImages.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        return image is null ? NotFound() : Picture(image);
    }

    private FileContentResult Picture(SignatureImage image)
    {
        // Each picture's address and bytes never change (a new upload is a new picture).
        Response.Headers.CacheControl = "private, max-age=604800";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(image.Data, "image/png");
    }
}
