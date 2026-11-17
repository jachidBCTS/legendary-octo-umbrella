using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>
/// Checks the [Team name] references in a workflow's steps against Admin → Teams. A team that does not
/// exist, or has nobody in it, would stop every request that reaches that step, so publishing is refused.
/// </summary>
public static class TeamCheck
{
    public static async Task<List<Issue>> IssuesAsync(AppDbContext db, WorkflowDefinition def, CancellationToken ct = default)
    {
        var used = def.Flow.Nodes.SelectMany(n => WorkflowEngine.TeamsNamedBy(n).Select(team => (Node: n, Team: team))).ToList();
        if (used.Count == 0) return new List<Issue>();
        var teams = (await db.Teams.AsNoTracking().ToListAsync(ct)).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var issues = new List<Issue>();
        foreach (var (node, name) in used.DistinctBy(u => (u.Node.Id, u.Team.ToLowerInvariant())))
        {
            var step = string.IsNullOrWhiteSpace(node.Label) ? node.Type : node.Label;
            if (!teams.TryGetValue(name, out var team))
                issues.Add(new Issue("error", "flow", node.Id, $"“{step}”: there is no team called “{name}”. Add it under Admin → Teams, or correct the name."));
            else if (!team.MemberList.Any())
                issues.Add(new Issue("error", "flow", node.Id, $"“{step}”: the team “{team.Name}” has nobody in it yet. Add people under Admin → Teams before publishing."));
        }
        return issues;
    }
}
