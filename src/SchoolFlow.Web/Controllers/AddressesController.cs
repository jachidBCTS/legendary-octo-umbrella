using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Controllers;

/// <summary>Address suggestions for text fields that have "Suggest addresses" switched on (queue #15).</summary>
[ApiController]
[Authorize]
[AutoValidateAntiforgeryToken]
[Route("api/addresses")]
public sealed class AddressesController : ControllerBase
{
    private readonly AddressLookup _lookup;
    public AddressesController(AddressLookup lookup) => _lookup = lookup;

    [HttpGet]
    public async Task<IActionResult> Search(string? q, CancellationToken ct)
    {
        if (!_lookup.IsConfigured) return Ok(new { items = Array.Empty<object>(), connected = false });
        q = (q ?? "").Trim();
        if (q.Length > 100) q = q[..100];
        // Three characters before searching ("540"), so a keystroke or two does not use up the shared limit.
        if (q.Length < 3) return Ok(new { items = Array.Empty<object>(), connected = true });
        var found = await _lookup.SearchAsync(q, ct);
        if (found is null) return Ok(new { items = Array.Empty<object>(), connected = false });
        return Ok(new { items = found.Take(6).Select(a => new { label = a.Label }), connected = true });
    }
}
