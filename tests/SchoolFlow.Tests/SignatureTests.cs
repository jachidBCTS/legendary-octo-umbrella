using System.Buffers.Binary;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Controllers;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Tests;

/// <summary>Uploaded signature pictures (queue #14): the upload checks, who signs with which picture, and who may see it.</summary>
public sealed class SignatureTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

    public SignatureTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>A minimal PNG: signature, IHDR with the given size, IEND.</summary>
    internal static byte[] Png(int width, int height, int padding = 0)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new byte[25];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), 13);
        "IHDR"u8.CopyTo(ihdr.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(8, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(12, 4), (uint)height);
        ihdr[16] = 8; ihdr[17] = 6;
        bytes.AddRange(ihdr);
        bytes.AddRange(new byte[padding]);
        bytes.AddRange(new byte[] { 0, 0, 0, 0 });
        bytes.AddRange("IEND"u8.ToArray());
        bytes.AddRange(new byte[] { 0xAE, 0x42, 0x60, 0x82 });
        return bytes.ToArray();
    }

    [Fact]
    public void Upload_MustBeARealPng_OfASensibleSize()
    {
        Assert.Null(Signatures.Check(Png(600, 200), out var w, out var h));
        Assert.Equal((600, 200), (w, h));

        Assert.Contains("Choose", Signatures.Check(Array.Empty<byte>(), out _, out _));
        Assert.Contains("not a PNG", Signatures.Check("GIF89a not really a picture at all, just text"u8.ToArray(), out _, out _));
        var jpeg = Png(600, 200); jpeg[0] = 0xFF; jpeg[1] = 0xD8;
        Assert.Contains("not a PNG", Signatures.Check(jpeg, out _, out _));
        var renamed = Png(600, 200); "abcd"u8.CopyTo(renamed.AsSpan(12));      // PNG magic but no IHDR
        Assert.Contains("not a PNG", Signatures.Check(renamed, out _, out _));
        Assert.Contains("too large", Signatures.Check(Png(600, 200, Signatures.MaxBytes), out _, out _));
        Assert.Contains("too small", Signatures.Check(Png(10, 200), out _, out _));
        Assert.Contains("too big", Signatures.Check(Png(5000, 200), out _, out _));
        var cut = Png(600, 200)[..^12].Concat(new byte[12]).ToArray();          // no IEND: cut off
        Assert.Contains("damaged", Signatures.Check(cut, out _, out _));
    }

    private static readonly List<FieldDef> Fields = new()
    {
        new FieldDef { Key = "sig", Label = "Signature", Type = FieldTypes.Signature },
        new FieldDef { Key = "note", Label = "Note", Type = "text" },
    };

    private static Dictionary<string, object?> Answers(string sig) => new(StringComparer.OrdinalIgnoreCase) { ["sig"] = sig, ["note"] = "Ann Lee" };

    [Fact]
    public void Stamp_UsesOnlyTheSignersOwnPicture_WhenTheySignWithTheirOwnName()
    {
        var context = Values.NewDict();
        Assert.Equal(new[] { 7 }, Signatures.Stamp(context, Fields, Answers(" ann lee "), null, "Ann Lee", 7));
        Assert.Equal(7, Signatures.PictureFor(context, "sig"));
        Assert.Null(Signatures.PictureFor(context, "note"));                     // only Signature fields

        // Someone else's name: no picture (nobody can sign with another person's picture).
        context = Values.NewDict();
        Assert.Empty(Signatures.Stamp(context, Fields, Answers("Bob Smith"), null, "Ann Lee", 7));
        Assert.False(context.ContainsKey(Signatures.ContextKey));
        // No picture uploaded: the name in script, as before.
        Assert.Empty(Signatures.Stamp(context, Fields, Answers("Ann Lee"), null, "Ann Lee", null));
        Assert.Null(Signatures.PictureFor(context, "sig"));
    }

    [Fact]
    public void Stamp_KeepsThePictureAnUnchangedAnswerWasSignedWith()
    {
        var context = Values.NewDict();
        Signatures.Stamp(context, Fields, Answers("Ann Lee"), null, "Ann Lee", 7);
        context["sig"] = "Ann Lee";
        var before = new Dictionary<string, object?>(context, StringComparer.OrdinalIgnoreCase);
        // Sent back and resubmitted after uploading a new picture, the signature unchanged: still the one it was signed with.
        Assert.Empty(Signatures.Stamp(context, Fields, Answers("Ann Lee"), before, "Ann Lee", 9));
        Assert.Equal(7, Signatures.PictureFor(context, "sig"));
        // Signed again with a different name: picture gone. Cleared: gone.
        Signatures.Stamp(context, Fields, Answers("A. Lee"), before, "Ann Lee", 9);
        Assert.Null(Signatures.PictureFor(context, "sig"));
        Signatures.Stamp(context, Fields, Answers(""), before, "Ann Lee", 9);
        Assert.False(context.ContainsKey(Signatures.ContextKey));
    }

    [Fact]
    public void SignaturesKey_IsReserved_SoNoFieldCanOverwriteIt() =>
        Assert.Contains(Signatures.ContextKey, SchoolFlow.Core.Workflows.DefinitionValidator.Reserved);

    private static ClaimsPrincipal Person(string email) => new(new ClaimsIdentity(
        new[] { new Claim("preferred_username", email), new Claim("name", email.Split('@')[0]), new Claim(ClaimTypes.Role, Roles.Staff) }, "test", "name", ClaimTypes.Role));

    private SignaturesController Controller(AppDbContext db, string email) => new(db, new AccessService(db))
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Person(email) } },
    };

    [Fact]
    public async Task Pictures_AreShownOnlyToTheOwner_AndToPeopleWhoCanOpenTheRequest()
    {
        int runId, annPic, bobPic;
        await using (var db = new AppDbContext(_options))
        {
            var ann = new SignatureImage { OwnerEmail = "ann@school.org", Data = Png(600, 200), Width = 600, Height = 200, CreatedAt = DateTime.UtcNow };
            var bob = new SignatureImage { OwnerEmail = "bob@school.org", Data = Png(300, 100), Width = 300, Height = 100, CreatedAt = DateTime.UtcNow };
            db.SignatureImages.AddRange(ann, bob);
            await db.SaveChangesAsync();
            (annPic, bobPic) = (ann.Id, bob.Id);
            db.Users.Add(new AppUser { Email = "ann@school.org", Name = "Ann Lee", SignatureImageId = annPic });
            db.Users.Add(new AppUser { Email = "bob@school.org", Name = "Bob Smith", SignatureImageId = bobPic });
            db.Users.Add(new AppUser { Email = "eve@school.org", Name = "Eve" });
            var context = Values.NewDict();
            context["sig"] = "Ann Lee";
            Signatures.Stamp(context, Fields, Answers("Ann Lee"), null, "Ann Lee", annPic);
            var run = new WorkflowRun { WorkflowName = "Trip", Status = RunStatus.Running, SubmitterEmail = "ann@school.org", ContextJson = Json.Serialize(context),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Runs.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        await using var check = new AppDbContext(_options);
        // Each person's own picture, never anyone else's.
        var mine = Assert.IsType<FileContentResult>(await Controller(check, "ann@school.org").Mine(default));
        Assert.Equal("image/png", mine.ContentType);
        Assert.Equal(Png(600, 200), mine.FileContents);
        Assert.Equal(Png(300, 100), Assert.IsType<FileContentResult>(await Controller(check, "bob@school.org").Mine(default)).FileContents);
        Assert.IsType<NotFoundResult>(await Controller(check, "eve@school.org").Mine(default));

        // On a request: whoever can open it sees the picture that signed it; anyone else gets "not found".
        Assert.Equal(Png(600, 200), Assert.IsType<FileContentResult>(await Controller(check, "ann@school.org").OnRequest(runId, "sig", default)).FileContents);
        Assert.IsType<NotFoundResult>(await Controller(check, "eve@school.org").OnRequest(runId, "sig", default));
        Assert.IsType<NotFoundResult>(await Controller(check, "ann@school.org").OnRequest(runId, "note", default));
        Assert.IsType<NotFoundResult>(await Controller(check, "ann@school.org").OnRequest(runId + 1, "sig", default));
    }

    [Fact]
    public async Task Removing_DeletesAnUnusedPicture_ButKeepsOneARequestWasSignedWith()
    {
        await using var db = new AppDbContext(_options);
        var unused = new SignatureImage { OwnerEmail = "ann@school.org", Data = Png(600, 200), CreatedAt = DateTime.UtcNow };
        var used = new SignatureImage { OwnerEmail = "ann@school.org", Data = Png(600, 200), CreatedAt = DateTime.UtcNow };
        db.SignatureImages.AddRange(unused, used);
        await db.SaveChangesAsync();
        var ann = new AppUser { Email = "ann@school.org", Name = "Ann Lee", SignatureImageId = unused.Id };
        db.Users.Add(ann);
        await Signatures.MarkUsedAsync(db, new[] { used.Id }, default);
        await db.SaveChangesAsync();

        await Signatures.ClearAsync(db, ann, default);
        await db.SaveChangesAsync();
        Assert.Null(ann.SignatureImageId);
        Assert.False(await db.SignatureImages.AnyAsync(i => i.Id == unused.Id));

        ann.SignatureImageId = used.Id;
        await Signatures.ClearAsync(db, ann, default);
        await db.SaveChangesAsync();
        Assert.True(await db.SignatureImages.AnyAsync(i => i.Id == used.Id));       // part of a signed request
    }
}
