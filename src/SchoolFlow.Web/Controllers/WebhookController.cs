using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Forms;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Web.Controllers;

/// <summary>Receives ticket events from Freshservice's Workflow Automator ("Trigger Webhook" action).</summary>
[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("api/webhooks")]
public sealed class WebhookController : ControllerBase
{
    public const string SecretHeader = "X-SchoolFlow-Secret";

    private readonly AppDbContext _db;
    private readonly RunService _runs;
    private readonly IConfiguration _config;
    private readonly ILogger<WebhookController> _log;

    public WebhookController(AppDbContext db, RunService runs, IConfiguration config, ILogger<WebhookController> log)
    {
        _db = db;
        _runs = runs;
        _config = config;
        _log = log;
    }

    [HttpPost("freshservice")]
    [RequestSizeLimit(256_000)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("webhook")]
    public async Task<IActionResult> Freshservice([FromBody] JsonElement body, CancellationToken ct)
    {
        var secret = _config["Freshservice:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 16)
            return StatusCode(503, new { error = "Webhooks are disabled until Freshservice:WebhookSecret (16+ characters) is configured." });

        var supplied = Request.Headers[SecretHeader].ToString();
        // Hash both sides so the comparison is constant-time regardless of length.
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(secret))))
            return Unauthorized(new { error = "Missing or wrong webhook secret." });

        if (body.ValueKind != JsonValueKind.Object) return BadRequest(new { error = "Send a JSON object." });

        string outcome;
        var payload = Json.ToValues(body);
        try
        {
            outcome = await _runs.HandleTicketEventAsync(payload, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Webhook handling failed");
            outcome = "Error: " + ex.Message;
        }

        var ticket = payload.GetValueOrDefault("ticket") as Dictionary<string, object?>;
        var summary = $"event={Values.Describe(payload.GetValueOrDefault("event"))} ticket={Values.Describe(ticket?.GetValueOrDefault("id"))}";
        _db.ChangeTracker.Clear();
        _db.WebhookLogs.Add(new WebhookLog { At = DateTime.UtcNow, Summary = summary, Outcome = outcome.Length > 1000 ? outcome[..1000] : outcome });
        await _db.SaveChangesAsync(ct);

        return outcome.StartsWith("Error", StringComparison.Ordinal)
            ? StatusCode(500, new { error = "The event could not be processed." })
            : Ok(new { ok = true, result = outcome });
    }
}
