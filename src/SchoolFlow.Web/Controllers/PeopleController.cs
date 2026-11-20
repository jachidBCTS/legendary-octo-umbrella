using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SchoolFlow.Core;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Controllers;

/// <summary>
/// Suggestions for "type an email address" boxes: people who have signed in, team members, department
/// approvers and (when the help desk is connected) Freshservice requesters. Only a search — never a list
/// of everyone — and only addresses the district allows when <c>Auth:AllowedDomains</c> is set.
/// </summary>
[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api/people")]
public sealed class PeopleController : ControllerBase
{
    public const int MaxResults = 20;
    private readonly AppDbContext _db;
    private readonly IFreshserviceClient _freshservice;
    private readonly DomainPolicy _domains;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PeopleController> _log;

    private readonly IStaffDirectory _staff;

    public PeopleController(AppDbContext db, IFreshserviceClient freshservice, DomainPolicy domains, IMemoryCache cache, ILogger<PeopleController> log, IStaffDirectory staff)
    {
        _staff = staff;
        _db = db;
        _freshservice = freshservice;
        _domains = domains;
        _cache = cache;
        _log = log;
    }

    public sealed record Person(string Email, string Name);

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
    {
        q = (q ?? "").Trim().ToLowerInvariant();
        if (q.Length < 2) return Ok(Array.Empty<Person>());
        if (q.Length > 100) q = q[..100];

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? email, string? name)
        {
            email = (email ?? "").Trim().ToLowerInvariant();
            if (email.Length == 0 || !email.Contains('@') || !_domains.IsAllowed(email)) return;
            name = (name ?? "").Trim();
            if (!found.TryGetValue(email, out var known) || known.Length == 0) found[email] = name;
        }

        foreach (var user in await _db.Users.AsNoTracking()
                     .Where(u => u.Email.ToLower().Contains(q) || u.Name.ToLower().Contains(q))
                     .OrderBy(u => u.Name).Take(MaxResults).Select(u => new { u.Email, u.Name }).ToListAsync(ct))
            Add(user.Email, user.Name);

        // Team members and department approvers are stored as address lists; only the addresses are searchable.
        var lists = await _db.Teams.AsNoTracking().Where(t => t.Members.ToLower().Contains(q)).Select(t => t.Members).Take(50).ToListAsync(ct);
        lists.AddRange(await _db.Departments.AsNoTracking().Where(d => d.FinalApprovers.ToLower().Contains(q)).Select(d => d.FinalApprovers).Take(50).ToListAsync(ct));
        foreach (var email in lists.SelectMany(l => l.Split(';', StringSplitOptions.RemoveEmptyEntries)).Where(e => e.Contains(q, StringComparison.OrdinalIgnoreCase)))
            Add(email, null);

        // The district's staff list comes first; Freshservice is only asked when there is no staff list.
        if (_staff.IsConfigured)
        {
            try
            {
                var words = q.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Take(3).ToList();
                foreach (var person in await _staff.SearchAsync(words, MaxResults, ct)) Add(person.Email, person.Name);
            }
            catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
            {
                _log.LogWarning(ex, "Staff list search failed");
            }
        }
        else if (_freshservice.IsLive && found.Count < MaxResults)
        {
            try
            {
                var people = await _cache.GetOrCreateAsync("people:fs:" + q, entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                    return _freshservice.LookupAsync(LookupSources.Requesters, q, null, ct);
                });
                foreach (var person in people ?? Array.Empty<LookupItem>()) Add(person.Email, person.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Suggestions are a convenience: the box still takes a typed address.
                _log.LogWarning(ex, "Freshservice people search failed");
            }
        }

        // Fill in names for addresses found only in lists, from people who have signed in.
        var unnamed = found.Where(p => p.Value.Length == 0).Select(p => p.Key).ToList();
        if (unnamed.Count > 0)
            foreach (var user in await _db.Users.AsNoTracking().Where(u => unnamed.Contains(u.Email)).Select(u => new { u.Email, u.Name }).ToListAsync(ct))
                found[user.Email.ToLowerInvariant()] = user.Name;

        var result = found
            .OrderBy(p => p.Key.StartsWith(q) || p.Value.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.Value.Length == 0 ? p.Key : p.Value, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults).Select(p => new Person(p.Key, p.Value)).ToList();
        return Ok(result);
    }

    /// <summary>
    /// The district's staff list, for Staff fields: name, title, where they work. Only a search (2+
    /// characters, at most 20 people), optionally limited to one district.
    /// </summary>
    [HttpGet("/api/staff")]
    public async Task<IActionResult> Staff([FromQuery] string? q, [FromQuery] string? district, CancellationToken ct)
    {
        if (!_staff.IsConfigured) return Ok(new { connected = false, items = Array.Empty<object>() });
        q = (q ?? "").Trim();
        if (q.Length < 2) return Ok(new { connected = true, items = Array.Empty<object>() });
        if (q.Length > 100) q = q[..100];
        var aliases = SchoolCalendar.DistrictAliases(HttpContext.RequestServices.GetService<IConfiguration>());
        string Code(string? text) { var t = (text ?? "").Trim(); return aliases.TryGetValue(t, out var c) ? c : t; }
        var wanted = Code(district);
        IReadOnlyList<StaffMember> rows;
        try { rows = await _staff.SearchAsync(q.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Take(3).ToList(), 50, ct); }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Staff list search failed");
            return StatusCode(503, new { error = "The staff list could not be read just now. Try again in a minute." });
        }
        var people = rows.Where(r => r.Email.Contains('@') && _domains.IsAllowed(r.Email))
            .GroupBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
            .Select(g => StaffLookup.Merge(g.ToList())!)
            .Where(p => wanted.Length == 0 || p.District.Split(',', StringSplitOptions.TrimEntries).Any(d => string.Equals(Code(d), wanted, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxResults)
            .Select(p => new { email = p.Email, name = p.Name, title = p.Title, location = p.Location, department = p.Department, district = p.District });
        return Ok(new { connected = true, items = people });
    }
}
