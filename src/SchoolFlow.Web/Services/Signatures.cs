using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>
/// Uploaded signature pictures (owner, Nov 24, 2026). A person may upload a PNG of their signature on
/// "My Signature"; "Sign" on a Signature field then signs with it. The browser only ever fills in the
/// person's name: the server decides whether a picture goes with it, and only ever the signer's own,
/// recorded per field in the request's <c>_signatures</c> (field key → picture id).
/// </summary>
public static class Signatures
{
    public const int MaxBytes = 500 * 1024;
    public const int MinPixels = 20;
    public const int MaxPixels = 4000;
    /// <summary>Where a request keeps which picture signed which field.</summary>
    public const string ContextKey = "_signatures";

    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Checks an upload is a real PNG of a sensible size. Returns the problem in plain words, or null.</summary>
    public static string? Check(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length == 0) return "Choose a PNG picture of your signature.";
        if (data.Length > MaxBytes) return "The picture is too large (500 KB at most). Save it smaller, about 600 × 200 pixels.";
        // PNG: 8-byte signature, then the IHDR chunk (length 13) with width and height.
        if (data.Length < 33 || !data[..8].SequenceEqual(PngMagic)
            || BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4)) != 13
            || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
            return "That is not a PNG picture. Save or export your signature as a .png file.";
        var w = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        var h = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        if (w < MinPixels || h < MinPixels) return "The picture is too small. About 600 × 200 pixels works best.";
        if (w > MaxPixels || h > MaxPixels) return "The picture is too big (4000 pixels at most each way). About 600 × 200 pixels works best.";
        if (!data[^12..].Slice(4, 4).SequenceEqual("IEND"u8)) return "That PNG file looks damaged or incomplete. Try saving it again.";
        width = (int)w; height = (int)h;
        return null;
    }

    /// <summary>
    /// Records, for the Signature fields just answered, whether the signer's picture goes with each.
    /// A field signed with the signer's own name (what "Sign" fills in) gets their current picture; any
    /// other name gets none. An answer that did not change keeps the picture it was signed with.
    /// Returns the picture ids newly put on the request.
    /// </summary>
    public static List<int> Stamp(Dictionary<string, object?> context, IEnumerable<FieldDef> fields, IReadOnlyDictionary<string, object?> answers,
        IReadOnlyDictionary<string, object?>? before, string signerName, int? pictureId)
    {
        var map = context.GetValueOrDefault(ContextKey) as Dictionary<string, object?> ?? Values.NewDict();
        var added = new List<int>();
        foreach (var field in fields.Where(f => f.Type == FieldTypes.Signature && !string.IsNullOrWhiteSpace(f.Key)))
        {
            var now = Values.ToText(answers.GetValueOrDefault(field.Key)).Trim();
            if (now.Length == 0) { map.Remove(field.Key); continue; }
            if (before is not null && map.ContainsKey(field.Key)
                && string.Equals(Values.ToText(before.GetValueOrDefault(field.Key)).Trim(), now, StringComparison.Ordinal))
                continue;
            if (pictureId is int id && signerName.Trim().Length > 0 && string.Equals(now, signerName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                map[field.Key] = (decimal)id;
                added.Add(id);
            }
            else map.Remove(field.Key);
        }
        if (map.Count == 0) context.Remove(ContextKey); else context[ContextKey] = map;
        return added;
    }

    /// <summary>The picture that signed this field of a request, if any.</summary>
    public static int? PictureFor(IReadOnlyDictionary<string, object?> context, string fieldKey) =>
        context.GetValueOrDefault(ContextKey) is Dictionary<string, object?> map && map.GetValueOrDefault(fieldKey) is { } value
            && Values.TryToNumber(value, out var id) && id > 0
            ? (int)id : null;

    public static string UrlFor(int runId, string fieldKey) => $"/signatures/run/{runId}/{Uri.EscapeDataString(fieldKey)}";

    /// <summary>The person's current picture id.</summary>
    public static Task<int?> CurrentAsync(AppDbContext db, string email, CancellationToken ct) =>
        email.Length == 0 ? Task.FromResult<int?>(null)
            : db.Users.AsNoTracking().Where(u => u.Email == email).Select(u => u.SignatureImageId).FirstOrDefaultAsync(ct);

    /// <summary>Marks pictures as used on a request, so they are kept when the person replaces theirs.</summary>
    public static async Task MarkUsedAsync(AppDbContext db, IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        foreach (var row in await db.SignatureImages.Where(i => ids.Contains(i.Id) && i.UsedAt == null).ToListAsync(ct))
            row.UsedAt = DateTime.UtcNow;
    }

    /// <summary>Takes the person's current picture off them; deleted outright if no request uses it.</summary>
    public static async Task ClearAsync(AppDbContext db, AppUser user, CancellationToken ct)
    {
        if (user.SignatureImageId is not int old) return;
        user.SignatureImageId = null;
        var row = await db.SignatureImages.FirstOrDefaultAsync(i => i.Id == old && i.OwnerEmail == user.Email, ct);
        if (row is not null && row.UsedAt is null) db.SignatureImages.Remove(row);
    }
}
