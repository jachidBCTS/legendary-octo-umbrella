using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>
/// How long requests are kept. The owner's policy: requests and their files for 7 years, the audit
/// log forever. <c>Retention:Years</c> can change the period; 0 switches deletion off.
/// </summary>
public sealed class RetentionService
{
    public const int DefaultYears = 7;
    private const int BatchSize = 100;
    private readonly AppDbContext _db;
    private readonly AttachmentService _files;
    private readonly IConfiguration _config;

    public RetentionService(AppDbContext db, AttachmentService files, IConfiguration config)
    {
        _db = db;
        _files = files;
        _config = config;
    }

    /// <summary>Years a request is kept after its last activity; 0 means requests are never deleted.</summary>
    public int Years => int.TryParse(_config["Retention:Years"], out var years) ? Math.Clamp(years, 0, 100) : DefaultYears;

    public sealed record PurgeResult(int Requests, int Files, int Notifications, int Drafts)
    {
        public bool Any => Requests + Files + Notifications + Drafts > 0;
    }

    /// <summary>
    /// Permanently deletes requests whose last activity is older than the retention period, with their
    /// history, approvals, discussion and uploaded files; also notifications and unfinished drafts of
    /// that age. A request that is still in progress is never deleted, however old. The audit log is
    /// never touched, and each purge adds an entry to it.
    /// </summary>
    public async Task<PurgeResult> PurgeAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (Years <= 0) return new PurgeResult(0, 0, 0, 0);
        var cutoff = nowUtc.AddYears(-Years);
        int requests = 0, files = 0;
        DateTime? oldest = null, newest = null;

        while (true)
        {
            var batch = await _db.Runs
                .Where(r => r.UpdatedAt < cutoff && (r.Status == RunStatus.Completed || r.Status == RunStatus.Rejected || r.Status == RunStatus.Cancelled || r.Status == RunStatus.Failed))
                .OrderBy(r => r.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            var ids = batch.Select(r => r.Id).ToList();
            var attachments = await _db.Attachments.Where(a => a.RunId != null && ids.Contains(a.RunId.Value)).ToListAsync(ct);

            _db.RunEvents.RemoveRange(_db.RunEvents.Where(e => ids.Contains(e.RunId)));
            _db.ApprovalTasks.RemoveRange(_db.ApprovalTasks.Where(t => ids.Contains(t.RunId)));
            _db.RunComments.RemoveRange(_db.RunComments.Where(c => ids.Contains(c.RunId)));
            _db.Attachments.RemoveRange(attachments);
            _db.Runs.RemoveRange(batch);
            await _db.SaveChangesAsync(ct);
            // Files go only after the database agrees the request is gone, so a failure never leaves
            // a request pointing at a file that no longer exists.
            _files.DeleteFiles(attachments);

            requests += batch.Count;
            files += attachments.Count;
            oldest = oldest is null || batch.Min(r => r.UpdatedAt) < oldest ? batch.Min(r => r.UpdatedAt) : oldest;
            newest = newest is null || batch.Max(r => r.UpdatedAt) > newest ? batch.Max(r => r.UpdatedAt) : newest;
        }

        var notifications = await _db.Notifications.Where(n => n.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
        // Files uploaded to a draft are cleared by the daily clean-up once no draft mentions them.
        var drafts = await _db.FormDrafts.Where(d => d.UpdatedAt < cutoff).ExecuteDeleteAsync(ct);

        var result = new PurgeResult(requests, files, notifications, drafts);
        if (result.Any)
        {
            _db.AuditEntries.Add(new AuditEntry
            {
                At = nowUtc, ActorEmail = "", ActorName = "SchoolFlow (automatic)", Action = "retention.purged",
                Target = $"Retention policy ({Years} year{(Years == 1 ? "" : "s")})",
                Detail = $"Deleted {requests} request(s) and {files} file(s)"
                    + (oldest is not null ? $" last active between {oldest:yyyy-MM-dd} and {newest:yyyy-MM-dd}" : "")
                    + $", {notifications} notification(s) and {drafts} unfinished draft(s) older than {cutoff:yyyy-MM-dd}.",
            });
            await _db.SaveChangesAsync(ct);
        }
        return result;
    }
}

/// <summary>Applies the retention policy once a day.</summary>
public sealed class RetentionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RetentionWorker> _log;

    public RetentionWorker(IServiceScopeFactory scopes, ILogger<RetentionWorker> log)
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
                await Task.Delay(TimeSpan.FromMinutes(20), stoppingToken); // not during start-up
                using var scope = _scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<RetentionService>().PurgeAsync(DateTime.UtcNow, stoppingToken);
                if (result.Any)
                    _log.LogInformation("Retention: deleted {Requests} request(s), {Files} file(s), {Notifications} notification(s), {Drafts} draft(s)",
                        result.Requests, result.Files, result.Notifications, result.Drafts);
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Retention pass failed");
            }
        }
    }
}
