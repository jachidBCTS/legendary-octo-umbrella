using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Pages.Account;

/// <summary>
/// "My Signature" (owner, Nov 24, 2026): each person may upload a PNG of their signature. "Sign" on a
/// form's Signature field then signs with it instead of their name in script.
/// </summary>
[RequestSizeLimit(10_000_000)] // well above the 500 KB limit, so a big photo gets our own message rather than a bare error page
[RequestFormLimits(MultipartBodyLengthLimit = 10_000_000)]
public class SignatureModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly AuditService _audit;

    public SignatureModel(AppDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    /// <summary>The current picture's id (also used to refresh the preview address), if any.</summary>
    public int? PictureId { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == User.Email(), ct);
        if (user is null) return NotFound();
        string? problem;
        byte[] bytes = Array.Empty<byte>();
        int width = 0, height = 0;
        if (file is not null && file.Length > Signatures.MaxBytes)
            problem = Signatures.Check(new byte[Signatures.MaxBytes + 1], out _, out _);
        else
        {
            if (file is not null && file.Length > 0)
            {
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                bytes = buffer.ToArray();
            }
            problem = Signatures.Check(bytes, out width, out height);
        }
        if (problem is not null)
        {
            Error = problem;
            await LoadAsync(ct);
            return Page();
        }
        await Signatures.ClearAsync(_db, user, ct);
        var image = new SignatureImage { OwnerEmail = user.Email, Data = bytes, Width = width, Height = height, CreatedAt = DateTime.UtcNow };
        _db.SignatureImages.Add(image);
        await _db.SaveChangesAsync(ct);
        user.SignatureImageId = image.Id;
        _audit.Record(User, "signature.set", user.Email, $"Signature picture uploaded ({width} × {height} px, {bytes.Length / 1024} KB).");
        await _db.SaveChangesAsync(ct);
        TempData["Flash"] = "Your signature picture is saved. \"Sign\" on a form now signs with it.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == User.Email(), ct);
        if (user is null) return NotFound();
        if (user.SignatureImageId is not null)
        {
            await Signatures.ClearAsync(_db, user, ct);
            _audit.Record(User, "signature.removed", user.Email, "Signature picture removed.");
            await _db.SaveChangesAsync(ct);
        }
        TempData["Flash"] = "Your signature picture is removed. \"Sign\" now signs with your name.";
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var email = User.Email();
        var id = await Signatures.CurrentAsync(_db, email, ct);
        var row = id is int current
            ? await _db.SignatureImages.AsNoTracking().Where(i => i.Id == current && i.OwnerEmail == email)
                .Select(i => new { i.Id, i.Width, i.Height }).FirstOrDefaultAsync(ct)
            : null;
        PictureId = row?.Id;
        Width = row?.Width ?? 0;
        Height = row?.Height ?? 0;
    }
}
