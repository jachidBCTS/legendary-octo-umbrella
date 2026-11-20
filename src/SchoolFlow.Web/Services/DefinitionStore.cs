using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>
/// The definitions runs started with. Each distinct definition is stored once (<see cref="RunDefinition"/>,
/// found by its SHA-256), and every run on it points there, instead of each run carrying its own copy.
/// Stored definitions never change, so they are kept in memory once read: loading a run costs no extra
/// database work for its definition after the first time.
/// </summary>
public sealed class DefinitionStore
{
    private const int MaxCached = 2000;
    private readonly ConcurrentDictionary<int, string> _byId = new();
    private readonly ConcurrentDictionary<string, int> _byHash = new();

    public static string HashOf(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

    private void Remember(int id, string hash, string json)
    {
        if (_byId.Count >= MaxCached) { _byId.Clear(); _byHash.Clear(); }
        _byId[id] = json;
        _byHash[hash] = id;
    }

    /// <summary>The stored definition with this JSON, created if it is new. Used when a run starts.</summary>
    public async Task<int> IdForAsync(AppDbContext db, string json, CancellationToken ct = default)
    {
        var hash = HashOf(json);
        if (_byHash.TryGetValue(hash, out var known)) return known;
        var existing = await db.RunDefinitions.AsNoTracking().Where(d => d.Hash == hash).Select(d => (int?)d.Id).FirstOrDefaultAsync(ct);
        if (existing is int id) { Remember(id, hash, json); return id; }
        var row = new RunDefinition { Hash = hash, Json = json, CreatedAt = DateTime.UtcNow };
        db.RunDefinitions.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Someone else stored the same definition at the same moment: use theirs.
            db.Entry(row).State = EntityState.Detached;
            id = await db.RunDefinitions.AsNoTracking().Where(d => d.Hash == hash).Select(d => d.Id).FirstAsync(ct);
            Remember(id, hash, json);
            return id;
        }
        db.Entry(row).State = EntityState.Detached;
        Remember(row.Id, hash, json);
        return row.Id;
    }

    /// <summary>Fills in <see cref="WorkflowRun.DefinitionJson"/> for runs just read from the database (one query for any not yet in memory).</summary>
    public async Task FillAsync(AppDbContext db, IEnumerable<WorkflowRun?> runs, CancellationToken ct = default)
    {
        var list = runs.Where(r => r is not null && !r.HasDefinition).Select(r => r!).ToList();
        if (list.Count == 0) return;
        var missing = list.Where(r => r.DefinitionId is int id && !_byId.ContainsKey(id)).Select(r => r.DefinitionId!.Value).Distinct().ToList();
        foreach (var batch in missing.Chunk(500))
            foreach (var d in await db.RunDefinitions.AsNoTracking().Where(d => batch.Contains(d.Id)).Select(d => new { d.Id, d.Hash, d.Json }).ToListAsync(ct))
                Remember(d.Id, d.Hash, d.Json);
        foreach (var run in list)
            run.DefinitionJson = run.DefinitionId is int id && _byId.TryGetValue(id, out var json) ? json
                : run.LegacyDefinitionJson ?? "{}";   // a run not yet moved (only during the first start after the update)
    }

    public Task FillAsync(AppDbContext db, WorkflowRun? run, CancellationToken ct = default) => FillAsync(db, new[] { run }, ct);

    /// <summary>Forgets definitions that have been deleted (retention), so they are not handed out again.</summary>
    public void Forget(IEnumerable<int> ids)
    {
        foreach (var id in ids)
            if (_byId.TryRemove(id, out var json)) _byHash.TryRemove(HashOf(json), out _);
    }

    /// <summary>
    /// One-time move, run on start: runs that still carry their own copy of the definition (from before
    /// definitions were shared) are pointed at the shared row and their copy is emptied. In batches, so a
    /// large database is moved without holding much in memory. Does nothing once every run has moved.
    /// </summary>
    public static int MoveLegacy(AppDbContext db, ILogger logger)
    {
        var moved = 0;
        var byHash = new Dictionary<string, int>();
        while (true)
        {
            var batch = db.Runs.Where(r => r.DefinitionId == null).OrderBy(r => r.Id).Take(200).ToList();
            if (batch.Count == 0) break;
            foreach (var run in batch)
            {
                var json = string.IsNullOrEmpty(run.LegacyDefinitionJson) ? "{}" : run.LegacyDefinitionJson;
                var hash = HashOf(json);
                if (!byHash.TryGetValue(hash, out var id))
                {
                    id = db.RunDefinitions.Where(d => d.Hash == hash).Select(d => d.Id).FirstOrDefault();
                    if (id == 0)
                    {
                        var row = new RunDefinition { Hash = hash, Json = json, CreatedAt = DateTime.UtcNow };
                        db.RunDefinitions.Add(row);
                        db.SaveChanges();
                        id = row.Id;
                    }
                    byHash[hash] = id;
                }
                run.DefinitionId = id;
                run.LegacyDefinitionJson = null;
            }
            db.SaveChanges();
            db.ChangeTracker.Clear();
            moved += batch.Count;
        }
        if (moved > 0) logger.LogInformation("Moved {Count} request(s) to shared definitions ({Distinct} distinct).", moved, byHash.Count);
        return moved;
    }
}
