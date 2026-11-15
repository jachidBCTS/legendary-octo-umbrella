using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>Where uploads go and what is allowed. Read once from configuration.</summary>
public sealed class AttachmentSettings
{
    /// <summary>File types people may upload, with the content type used when the file is downloaded again.</summary>
    public static readonly IReadOnlyDictionary<string, string> AllowedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".heic"] = "image/heic",
        [".doc"] = "application/msword", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel", [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint", [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".txt"] = "text/plain", [".csv"] = "text/csv", [".rtf"] = "application/rtf",
    };

    public string Folder { get; }
    public long MaxBytes { get; }
    public int MaxFileMb { get; }

    public AttachmentSettings(IConfiguration config, string contentRoot)
    {
        var folder = config["Attachments:Path"];
        Folder = string.IsNullOrWhiteSpace(folder) ? Path.Combine(contentRoot, "App_Data", "uploads") : folder;
        MaxFileMb = int.TryParse(config["Attachments:MaxFileMb"], out var mb) ? Math.Clamp(mb, 1, 25) : 10;
        MaxBytes = MaxFileMb * 1024L * 1024L;
    }

    public static string AllowedList => string.Join(", ", AllowedTypes.Keys.Select(k => k.TrimStart('.')).OrderBy(k => k));
}

public sealed record UploadResult(Attachment? Attachment, string? Error);

/// <summary>Stores uploaded files in a folder on the server and keeps a database row describing each one.</summary>
public sealed class AttachmentService : IAttachmentStore
{
    /// <summary>How many files one person may have uploaded but not yet submitted, to stop the folder being filled.</summary>
    public const int MaxPendingPerPerson = 60;

    private readonly AppDbContext _db;
    private readonly AttachmentSettings _settings;
    private readonly ILogger<AttachmentService> _log;

    public AttachmentService(AppDbContext db, AttachmentSettings settings, ILogger<AttachmentService> log)
    {
        _db = db;
        _settings = settings;
        _log = log;
    }

    /// <summary>The name on disk is random and never the uploaded name, so a file name can never escape the folder or be run.</summary>
    private string PathOf(Attachment attachment) => Path.Combine(_settings.Folder, attachment.StoredName);

    /// <summary>Keeps the visible part of a file name and drops anything that could confuse a browser, a header or a path.</summary>
    public static string CleanName(string? name)
    {
        name = Path.GetFileName((name ?? "").Replace('\\', '/'));
        var cleaned = new string(name.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or ':' or '|' or '?' or '*' or '/' or '\\')).ToArray()).Trim().Trim('.');
        if (cleaned.Length > 150)
        {
            var extension = Path.GetExtension(cleaned);
            cleaned = cleaned[..(150 - extension.Length)] + extension;
        }
        return cleaned;
    }

    /// <summary>Checks that the first bytes look like the kind of file the name says it is.</summary>
    public static bool ContentMatches(string extension, ReadOnlySpan<byte> head)
    {
        static bool Starts(ReadOnlySpan<byte> data, params byte[] magic) => data.Length >= magic.Length && data[..magic.Length].SequenceEqual(magic);
        switch (extension.ToLowerInvariant())
        {
            case ".pdf": return Starts(head, 0x25, 0x50, 0x44, 0x46);                           // %PDF
            case ".png": return Starts(head, 0x89, 0x50, 0x4E, 0x47);
            case ".jpg" or ".jpeg": return Starts(head, 0xFF, 0xD8, 0xFF);
            case ".gif": return Starts(head, 0x47, 0x49, 0x46, 0x38);                           // GIF8
            case ".webp": return Starts(head, 0x52, 0x49, 0x46, 0x46) && head.Length >= 12 && head[8..12].SequenceEqual("WEBP"u8);
            case ".heic": return head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8);
            case ".docx" or ".xlsx" or ".pptx": return Starts(head, 0x50, 0x4B, 0x03, 0x04);    // zip container
            case ".doc" or ".xls" or ".ppt": return Starts(head, 0xD0, 0xCF, 0x11, 0xE0);       // legacy Office container
            case ".rtf": return Starts(head, 0x7B, 0x5C, 0x72, 0x74, 0x66);                     // {\rtf
            case ".txt" or ".csv":
                // Plain text has no signature; reject programs and other binaries dressed up as text.
                return !Starts(head, 0x4D, 0x5A) && head.IndexOf((byte)0) < 0;
            default: return false;
        }
    }

    public async Task<UploadResult> SaveAsync(IFormFile? file, int workflowId, string ownerEmail, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return new UploadResult(null, "The file is empty.");
        var name = CleanName(file.FileName);
        var extension = Path.GetExtension(name);
        if (name.Length == 0 || extension.Length == 0 || !AttachmentSettings.AllowedTypes.TryGetValue(extension, out var contentType))
            return new UploadResult(null, $"That type of file cannot be uploaded. Allowed: {AttachmentSettings.AllowedList}.");
        if (file.Length > _settings.MaxBytes)
            return new UploadResult(null, $"“{name}” is larger than the {_settings.MaxFileMb} MB limit.");
        if (await _db.Attachments.CountAsync(a => a.OwnerEmail == ownerEmail && a.RunId == null, ct) >= MaxPendingPerPerson)
            return new UploadResult(null, "You have too many uploaded files waiting on unfinished forms. Submit or delete some drafts first.");

        var attachment = new Attachment
        {
            PublicId = Guid.NewGuid().ToString("N"),
            StoredName = Guid.NewGuid().ToString("N") + ".bin",
            FileName = name,
            ContentType = contentType,
            Size = file.Length,
            OwnerEmail = ownerEmail,
            WorkflowId = workflowId,
            CreatedAt = DateTime.UtcNow,
        };

        Directory.CreateDirectory(_settings.Folder);
        var path = PathOf(attachment);
        try
        {
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var source = file.OpenReadStream())
            {
                var head = new byte[16];
                var read = await source.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
                if (!ContentMatches(extension, head.AsSpan(0, read)))
                {
                    target.Close();
                    File.Delete(path);
                    return new UploadResult(null, $"“{name}” does not look like a real {extension.TrimStart('.').ToUpperInvariant()} file, so it was not uploaded.");
                }
                await target.WriteAsync(head.AsMemory(0, read), ct);
                await source.CopyToAsync(target, ct);
            }
            _db.Attachments.Add(attachment);
            await _db.SaveChangesAsync(ct);
            return new UploadResult(attachment, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Could not save an uploaded file to {Folder}", _settings.Folder);
            TryDelete(path);
            return new UploadResult(null, "The file could not be saved on the server. Please tell the help desk.");
        }
        catch
        {
            TryDelete(path); // never leave a file on disk without its database row
            throw;
        }
    }

    /// <summary>Files this person has uploaded for this form and not yet submitted: the only ones a form will accept.</summary>
    public async Task<Dictionary<string, AttachmentInfo>> PendingForAsync(string ownerEmail, int workflowId, CancellationToken ct) =>
        ownerEmail.Length == 0 ? new Dictionary<string, AttachmentInfo>()
        : (await _db.Attachments.AsNoTracking().Where(a => a.OwnerEmail == ownerEmail && a.WorkflowId == workflowId && a.RunId == null)
            .Select(a => new { a.PublicId, a.FileName, a.Size }).ToListAsync(ct))
        .ToDictionary(a => a.PublicId, a => new AttachmentInfo(a.PublicId, a.FileName, a.Size));

    /// <summary>Removes a file the person uploaded but has not submitted. Submitted files are part of the request's record.</summary>
    public async Task<bool> DeletePendingAsync(string publicId, string ownerEmail, CancellationToken ct)
    {
        var attachment = await _db.Attachments.FirstOrDefaultAsync(a => a.PublicId == publicId && a.OwnerEmail == ownerEmail && a.RunId == null, ct);
        if (attachment is null) return false;
        _db.Attachments.Remove(attachment);
        await _db.SaveChangesAsync(ct);
        TryDelete(PathOf(attachment));
        return true;
    }

    public async Task<(Attachment Attachment, FileStream Stream)?> OpenAsync(string publicId, CancellationToken ct)
    {
        var attachment = await _db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.PublicId == publicId, ct);
        if (attachment is null) return null;
        try
        {
            return (attachment, new FileStream(PathOf(attachment), FileMode.Open, FileAccess.Read, FileShare.Read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deleted from the folder, or quarantined by antivirus.
            _log.LogWarning("Attachment {Id} has a database row but its file could not be opened: {Message}", publicId, ex.Message);
            return null;
        }
    }

    /// <summary>For the workflow engine (attaching to a Freshservice ticket).</summary>
    public async Task<TicketFile?> ReadAsync(string attachmentId, CancellationToken ct = default)
    {
        var opened = await OpenAsync(attachmentId, ct);
        if (opened is null) return null;
        await using var stream = opened.Value.Stream;
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        return new TicketFile(opened.Value.Attachment.FileName, opened.Value.Attachment.ContentType, memory.ToArray());
    }

    /// <summary>
    /// Deletes uploads that were never submitted: older than <paramref name="olderThan"/> and not mentioned
    /// by any saved draft. Returns how many were removed.
    /// </summary>
    public async Task<int> RemoveAbandonedAsync(TimeSpan olderThan, DateTime nowUtc, CancellationToken ct)
    {
        var cutoff = nowUtc - olderThan;
        var candidates = await _db.Attachments.Where(a => a.RunId == null && a.CreatedAt < cutoff).OrderBy(a => a.Id).Take(500).ToListAsync(ct);
        var removed = 0;
        foreach (var attachment in candidates)
        {
            if (await _db.FormDrafts.AnyAsync(d => d.OwnerEmail == attachment.OwnerEmail && d.ValuesJson.Contains(attachment.PublicId), ct)) continue;
            _db.Attachments.Remove(attachment);
            TryDelete(PathOf(attachment));
            removed++;
        }
        if (removed > 0) await _db.SaveChangesAsync(ct);
        return removed;
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not delete {Path}: {Message}", path, ex.Message);
        }
    }

    public static string DisplaySize(long bytes) =>
        bytes >= 1024 * 1024 ? (bytes / 1024d / 1024d).ToString("0.#", Core.Expressions.Values.DisplayCulture) + " MB"
        : Math.Max(1, bytes / 1024).ToString(Core.Expressions.Values.DisplayCulture) + " KB";
}

/// <summary>Once a day, clears out files that were uploaded to a form that was never submitted or saved.</summary>
public sealed class AttachmentCleanupWorker : BackgroundService
{
    public static readonly TimeSpan KeepUnsubmittedFor = TimeSpan.FromDays(30);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AttachmentCleanupWorker> _log;

    public AttachmentCleanupWorker(IServiceScopeFactory scopes, ILogger<AttachmentCleanupWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); // not during start-up
                using var scope = _scopes.CreateScope();
                var removed = await scope.ServiceProvider.GetRequiredService<AttachmentService>().RemoveAbandonedAsync(KeepUnsubmittedFor, DateTime.UtcNow, stoppingToken);
                if (removed > 0) _log.LogInformation("Removed {Count} uploaded file(s) that were never submitted", removed);
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Attachment clean-up failed");
            }
        }
    }
}
