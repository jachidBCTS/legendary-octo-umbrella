using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SchoolFlow.Core;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>Hourly: approval reminders and escalation. Every 15 minutes: check Freshservice for tickets that requests are waiting on.</summary>
public sealed class FollowUpWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FollowUpWorker> _log;

    public FollowUpWorker(IServiceScopeFactory scopes, ILogger<FollowUpWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pass = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(pass == 0 ? 2 : 15), stoppingToken); // first pass shortly after start-up
                using var scope = _scopes.CreateScope();
                var runs = scope.ServiceProvider.GetRequiredService<RunService>();
                var moved = await runs.CheckWaitingTicketsAsync(DateTime.UtcNow, stoppingToken);
                if (moved > 0) _log.LogInformation("Ticket check moved {Count} waiting request(s) on", moved);
                if (pass % 4 == 0)
                {
                    var (reminded, escalated) = await runs.FollowUpApprovalsAsync(DateTime.UtcNow, stoppingToken);
                    if (reminded + escalated > 0) _log.LogInformation("Approval follow-up: {Reminded} reminder(s), {Escalated} escalation(s)", reminded, escalated);
                }
                pass++;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Follow-up pass failed");
                pass++;
            }
        }
    }
}
