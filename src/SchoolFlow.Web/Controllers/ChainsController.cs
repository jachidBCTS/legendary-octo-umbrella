using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Controllers;

public sealed class SaveChainRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    /// <summary>True for a template everyone can use (builders and admins only); false for the caller's own.</summary>
    public bool Shared { get; set; }
    public JsonElement Steps { get; set; }
}

/// <summary>Departments (read-only here; admins edit them on the Departments page) and approval chain templates.</summary>
[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api")]
public sealed class ChainsController : ControllerBase
{
    private const int MaxPersonalChains = 50;
    private readonly AppDbContext _db;
    public ChainsController(AppDbContext db) => _db = db;

    [HttpGet("departments")]
    public async Task<IActionResult> Departments(CancellationToken ct)
    {
        var rows = await _db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name).ToListAsync(ct);
        return Ok(new
        {
            items = rows.Select(d => new { id = d.Id, name = d.Name, finalApprovers = d.FinalApproverList, rule = d.FinalRule }),
        });
    }

    [HttpGet("locations")]
    public async Task<IActionResult> Locations(CancellationToken ct)
    {
        var rows = await _db.Locations.AsNoTracking().Where(l => l.IsActive).OrderBy(l => l.Name)
            .Select(l => new { id = l.Id, name = l.Name, district = l.District ?? "" }).ToListAsync(ct);
        // The names each district goes by, so a form's district question ("Bergen County Technical
        // Schools") can narrow the list to the locations marked BCTS.
        var aliases = SchoolCalendar.DistrictAliases(HttpContext.RequestServices.GetService<IConfiguration>());
        return Ok(new { items = rows, districts = aliases });
    }

    private static object Shape(ApprovalChainTemplate c, bool canEdit) => new
    {
        id = c.Id,
        name = c.Name,
        description = c.Description,
        shared = c.OwnerEmail.Length == 0,
        canEdit,
        steps = ChainValue.ToSteps(Json.ToValues("{\"steps\":" + c.StepsJson + "}"))
            .Select(s => new { name = s.Name, approvers = s.Approvers, rule = s.Rule }),
    };

    [HttpGet("chains")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var email = User.Email();
        var rows = await _db.ApprovalChains.AsNoTracking()
            .Where(c => c.OwnerEmail == "" || (c.OwnerEmail == email && email != ""))
            .OrderBy(c => c.Name).ToListAsync(ct);
        return Ok(new
        {
            shared = rows.Where(c => c.OwnerEmail.Length == 0).Select(c => Shape(c, User.IsBuilder())),
            mine = rows.Where(c => c.OwnerEmail.Length > 0).Select(c => Shape(c, true)),
            canManageShared = User.IsBuilder(),
        });
    }

    /// <summary>Validates the request and copies it onto the template. Returns an error message, or null.</summary>
    private static string? Apply(ApprovalChainTemplate chain, SaveChainRequest request, IReadOnlyCollection<string> allowedDomains)
    {
        var name = (request.Name ?? "").Trim();
        if (name.Length == 0) return "Give the approval chain a name.";
        if (name.Length > 200) return "The name is too long (200 characters at most).";

        var raw = Values.NewDict();
        raw["name"] = name;
        raw["steps"] = request.Steps.ValueKind == JsonValueKind.Array ? Json.ToValue(request.Steps) : new List<object?>();
        var errors = new List<string>();
        var cleaned = ChainValue.Normalize(raw, null, errors, allowedDomains);
        if (cleaned is null) return "Add at least one step.";
        if (errors.Count > 0) return string.Join(" ", errors.Take(3));

        var description = (request.Description ?? "").Trim();
        chain.Name = name;
        chain.Description = description.Length > 500 ? description[..500] : description;
        chain.StepsJson = Json.Serialize(cleaned["steps"]!);
        chain.UpdatedAt = DateTime.UtcNow;
        return null;
    }

    [HttpPost("chains")]
    public async Task<IActionResult> Create([FromBody] SaveChainRequest request, CancellationToken ct)
    {
        var email = User.Email();
        if (email.Length == 0) return BadRequest(new { error = "Your account has no email address." });
        if (request.Shared && !User.IsBuilder()) return Forbid();
        if (!request.Shared && await _db.ApprovalChains.CountAsync(c => c.OwnerEmail == email, ct) >= MaxPersonalChains)
            return BadRequest(new { error = $"You can save up to {MaxPersonalChains} approval chains. Delete one you no longer use." });

        var chain = new ApprovalChainTemplate { OwnerEmail = request.Shared ? "" : email };
        var error = Apply(chain, request, HttpContext.RequestServices.GetRequiredService<DomainPolicy>().Domains);
        if (error is not null) return BadRequest(new { error });
        _db.ApprovalChains.Add(chain);
        await _db.SaveChangesAsync(ct);
        return Ok(Shape(chain, true));
    }

    private async Task<ApprovalChainTemplate?> FindEditableAsync(int id, CancellationToken ct)
    {
        var chain = await _db.ApprovalChains.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (chain is null) return null;
        var email = User.Email();
        var allowed = chain.OwnerEmail.Length == 0 ? User.IsBuilder() : (email.Length > 0 && chain.OwnerEmail == email);
        return allowed ? chain : null; // someone else's personal chain looks the same as a missing one
    }

    [HttpPut("chains/{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] SaveChainRequest request, CancellationToken ct)
    {
        var chain = await FindEditableAsync(id, ct);
        if (chain is null) return NotFound();
        var error = Apply(chain, request, HttpContext.RequestServices.GetRequiredService<DomainPolicy>().Domains);
        if (error is not null) return BadRequest(new { error });
        await _db.SaveChangesAsync(ct);
        return Ok(Shape(chain, true));
    }

    /// <summary>
    /// Turns one of your own chains into a shared template (builders and admins; owner, Nov 23, 2026).
    /// The chain is moved, not copied, so it leaves "My chains". Requests already using it are unaffected.
    /// </summary>
    [HttpPost("chains/{id:int}/share")]
    public async Task<IActionResult> Share(int id, CancellationToken ct)
    {
        var email = User.Email();
        var chain = await _db.ApprovalChains.FirstOrDefaultAsync(c => c.Id == id && c.OwnerEmail == email && email != "", ct);
        if (chain is null) return NotFound();
        if (!User.IsBuilder()) return Forbid();
        if (await _db.ApprovalChains.AnyAsync(c => c.OwnerEmail == "" && c.Name == chain.Name, ct))
            return BadRequest(new { error = $"There is already a shared template called “{chain.Name}”. Rename this chain first." });
        chain.OwnerEmail = "";
        HttpContext.RequestServices.GetRequiredService<AuditService>().Record(User, "chain.shared", chain.Name, "Personal approval chain made a shared template.");
        await _db.SaveChangesAsync(ct);
        return Ok(Shape(chain, true));
    }

    [HttpDelete("chains/{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var chain = await FindEditableAsync(id, ct);
        if (chain is null) return NotFound();
        if (chain.OwnerEmail.Length == 0)
        {
            // A shared template may be wired into a workflow's flow; refuse rather than break it.
            var needle = $"\"chainId\":\"{chain.Id}\"";
            var users = await _db.Workflows.AsNoTracking()
                .Where(w => w.DraftJson.Contains(needle) || (w.PublishedJson != null && w.PublishedJson.Contains(needle)))
                .Select(w => w.Name).ToListAsync(ct);
            if (users.Count > 0)
                return BadRequest(new { error = "This chain is used by: " + string.Join(", ", users) + ". Change those workflows first." });
        }
        _db.ApprovalChains.Remove(chain);
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }
}
