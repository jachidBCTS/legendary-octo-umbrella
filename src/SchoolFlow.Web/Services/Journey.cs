using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>How long something has been waiting, in school days, and the colour that goes with it.</summary>
public sealed record WaitAge(int Days, string Label, string Tone)
{
    /// <summary>
    /// fresh (0–1 school days), aging (2–3, or due within 2 days), delayed (4+, escalated, or past its due date).
    /// </summary>
    public static WaitAge Of(DateTime sinceUtc, DateTime nowUtc, IReadOnlySet<DateTime> closed, DateTime? dueOn = null, bool escalated = false)
    {
        var days = SchoolCalendar.SchoolDaysBetween(Ui.Local(sinceUtc), Ui.Local(nowUtc), closed);
        var today = Ui.Local(nowUtc).Date;
        var overdue = dueOn is DateTime due && due.Date < today;
        var tone = overdue || escalated || days >= 4 ? "delayed"
            : days >= 2 || (dueOn is DateTime soon && soon.Date <= today.AddDays(2)) ? "aging"
            : "fresh";
        var label = overdue ? "Overdue"
            : days == 0 ? "Here now · today"
            : $"Waiting {days} school day{(days == 1 ? "" : "s")}";
        return new WaitAge(days, label, tone);
    }
}

/// <summary>One stop on a request's journey (the "train stop" track on the request page).</summary>
public sealed record JourneyStop(
    string Kind,          // submitted, approval, task, helpdesk, finish
    string Title,
    string State,         // done, current, returned, rejected, failed, cancelled, upcoming
    string? Who = null,   // who did it, who it is waiting on, or who will handle it
    string? When = null,
    string? Note = null,
    WaitAge? Age = null,
    string? Position = null);

/// <summary>Where a waiting request is now, for one row of a list.</summary>
public sealed record CurrentStop(string Title, string? Position, string WaitingOn, WaitAge? Age);

/// <summary>
/// Works out the stops a request passes through: what has happened (from its approval and task records)
/// and what is still to come (by following the flow from where it is, taking the route its answers lead to,
/// and the "approved" path through approvals). Read-only: it never changes the request.
/// </summary>
public static class Journey
{
    private static readonly Regex TeamName = new(@"\[([^\[\]]+)\]");

    public static List<JourneyStop> Build(WorkflowDefinition def, WorkflowRun run, Dictionary<string, object?> context,
        IReadOnlyList<ApprovalTask> tasks, DateTime nowUtc, IReadOnlySet<DateTime> closed, Func<string, string> nameOf,
        HashSet<string> hiddenKeys)
    {
        var stops = new List<JourneyStop>
        {
            new("submitted", "Submitted", "done", run.SubmitterName.Length > 0 ? run.SubmitterName : run.SubmitterEmail, Ui.When(run.CreatedAt)),
        };
        var finished = RunStatus.IsFinished(run.Status);
        var probe = new Dictionary<string, object?>(context, StringComparer.OrdinalIgnoreCase);
        var node = def.Flow.Nodes.FirstOrDefault(n => n.Type == NodeTypes.Start);
        var reachedCurrent = false;
        var seen = new HashSet<string>();
        var people = (string text) => string.Join(", ", text.Split(',', ';').Select(p => p.Trim()).Where(p => p.Length > 0).Select(nameOf));

        while (node is not null && seen.Add(node.Id) && stops.Count < 60)
        {
            var isCurrent = node.Id == run.CurrentNodeId;
            var rows = tasks.Where(t => t.NodeId == node.Id).OrderBy(t => t.Id).ToList();
            var port = "next";
            switch (node.Type)
            {
                case NodeTypes.Condition:
                    port = ConditionPort(node, probe, reachedCurrent);
                    break;
                case NodeTypes.SetVariable:
                    try { probe[node.Get("name").Trim()] = Formula.Evaluate(node.Get("expression"), new Scope(probe, closedDays: closed)); }
                    catch (ExpressionException) { }
                    break;
                case NodeTypes.Approval:
                case NodeTypes.Task:
                {
                    var kind = node.Type == NodeTypes.Task ? "task" : "approval";
                    var who = Planned(node, node.Type == NodeTypes.Task ? "assignees" : "approvers", probe, hiddenKeys, nameOf);
                    stops.Add(PeopleStop(kind, Name(node), rows.LastOrDefault(), run, isCurrent && !reachedCurrent, reachedCurrent, who, nowUtc, closed, nameOf, null));
                    if (rows.LastOrDefault()?.Status == ApprovalStatus.Rejected) port = "rejected";
                    break;
                }
                case NodeTypes.ApprovalChain:
                {
                    var chain = context.GetValueOrDefault("chain") as Dictionary<string, object?>;
                    if (chain is not null && Values.ToText(chain.GetValueOrDefault("node")) == node.Id && chain.GetValueOrDefault("steps") is List<object?> steps)
                    {
                        var index = Values.TryToNumber(chain.GetValueOrDefault("index"), out var i) ? (int)i : 0;
                        var list = steps.OfType<Dictionary<string, object?>>().ToList();
                        for (var k = 0; k < list.Count; k++)
                        {
                            var step = list[k];
                            var title = Values.ToText(step.GetValueOrDefault("name"));
                            var status = Values.ToText(step.GetValueOrDefault("status"));
                            var row = rows.LastOrDefault(t => t.Title == title || t.Title.Length > 300 && title.StartsWith(t.Title));
                            var approvers = step.GetValueOrDefault("approvers") is List<object?> a ? string.Join(", ", a.Select(x => nameOf(Values.ToText(x)))) : "";
                            var position = $"Step {k + 1} of {list.Count}";
                            var here = isCurrent && !reachedCurrent && k == index && status == "waiting";
                            JourneyStop stop = status switch
                            {
                                "approved" => new("approval", title, "done", "Approved by " + nameOf(Values.ToText(step.GetValueOrDefault("by")) is { Length: > 0 } by ? EmailIn(by) : row?.DecidedBy ?? ""),
                                    row?.DecidedAt is DateTime at ? Ui.When(at) : null, Position: position),
                                "rejected" => new("approval", title, "rejected", "Not approved by " + nameOf(EmailIn(Values.ToText(step.GetValueOrDefault("by"))))
                                    , row?.DecidedAt is DateTime rt ? Ui.When(rt) : null, row?.Comment, Position: position),
                                _ when here => PeopleStop("approval", title, rows.LastOrDefault(t => t.Status is ApprovalStatus.Pending or ApprovalStatus.Returned), run, true, false,
                                    approvers, nowUtc, closed, nameOf, position),
                                _ => new("approval", title, finished ? "cancelled" : "upcoming", approvers, Position: position),
                            };
                            stops.Add(stop);
                        }
                        if (list.Any(s => Values.ToText(s.GetValueOrDefault("status")) == "rejected")) port = "rejected";
                    }
                    else
                    {
                        // Not reached yet: who approves is worked out (from the chain and the department) when it gets there.
                        stops.Add(new("approval", Name(node), finished ? "cancelled" : "upcoming", "Approvers are set when it gets here"));
                    }
                    break;
                }
                case NodeTypes.CreateTicket:
                case NodeTypes.ServiceRequest:
                case NodeTypes.WaitTicket:
                {
                    var ticket = run.TicketId is long id ? $"Ticket #{id}" : "Help desk";
                    var state = isCurrent && !finished && !reachedCurrent ? (run.Status == RunStatus.Failed ? "failed" : "current")
                        : reachedCurrent ? "upcoming" : finished && isCurrent && run.Status != RunStatus.Completed ? "cancelled" : "done";
                    if (state == "upcoming" && finished) state = "cancelled";
                    stops.Add(new("helpdesk", Name(node), state, state == "current" ? "Waiting on the help desk · " + ticket : ticket));
                    break;
                }
                default:
                    if (isCurrent && run.Status == RunStatus.Failed && !reachedCurrent)
                        stops.Add(new("task", Name(node), "failed", Note: run.Error));
                    break;
            }

            if (isCurrent && !reachedCurrent)
            {
                reachedCurrent = true;
                if (finished) break;                     // the request ended here
            }
            if (finished && port == "rejected") break;
            var edge = def.Flow.Edges.FirstOrDefault(e => e.From == node.Id && e.Port == port)
                ?? (port == "next" ? def.Flow.Edges.FirstOrDefault(e => e.From == node.Id && e.Port == "approved") : null)
                ?? (port == "next" ? def.Flow.Edges.FirstOrDefault(e => e.From == node.Id && e.Port == "true") : null);
            node = edge is null ? null : def.Flow.Nodes.FirstOrDefault(n => n.Id == edge.To);
        }

        stops.Add(run.Status switch
        {
            RunStatus.Completed => new JourneyStop("finish", "Completed", "done", When: Ui.When(run.UpdatedAt)),
            RunStatus.Rejected => new JourneyStop("finish", "Not approved", "rejected", When: Ui.When(run.UpdatedAt)),
            RunStatus.Cancelled => new JourneyStop("finish", "Cancelled", "cancelled", When: Ui.When(run.UpdatedAt)),
            _ => new JourneyStop("finish", "Completed", "upcoming"),
        });
        // A failure on a step that is not itself a stop is still shown where it happened.
        if (run.Status == RunStatus.Failed && !stops.Any(s => s.State == "failed"))
            stops.Insert(stops.Count - 1, new JourneyStop("task", "Stopped by a problem", "failed", Note: run.Error));
        return stops;
    }

    private static string Name(NodeDef node) => string.IsNullOrWhiteSpace(node.Label) ? node.Type : node.Label!;

    /// <summary>"Pat Principal (principal@x.org)" → the address in it, or the text itself.</summary>
    private static string EmailIn(string text)
    {
        var open = text.IndexOf('(');
        var close = text.IndexOf(')', open + 1);
        return open >= 0 && close > open && text[(open + 1)..close].Contains('@') ? text[(open + 1)..close].Split(',')[0].Trim() : text;
    }

    /// <summary>
    /// A condition's way out. Before the request's current step its answers are known; after it, a condition
    /// that reads something not yet answered (a later task's decision) is assumed to take the "yes" path.
    /// </summary>
    private static string ConditionPort(NodeDef node, Dictionary<string, object?> probe, bool future)
    {
        try
        {
            var formula = Formula.Parse(node.Get("expression"));
            if (future && formula.References.Any(r => !probe.ContainsKey(r) || Values.IsBlank(probe[r]))) return "true";
            return Values.ToBool(formula.Evaluate(new Scope(probe))) ? "true" : "false";
        }
        catch (ExpressionException) { return "true"; }
    }

    /// <summary>Who a step will go to, from its settings: "[Team]" becomes "Team (team)", addresses become names.</summary>
    private static string? Planned(NodeDef node, string setting, Dictionary<string, object?> probe, HashSet<string> hiddenKeys, Func<string, string> nameOf)
    {
        var text = node.Get(setting);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (Template.GetExpressions(text).Any(e => Formula.TryParse(e, out var f, out _) && f!.References.Any(hiddenKeys.Contains))) return null;
        try { text = Template.Render(text, new Scope(probe)); }
        catch (ExpressionException) { return null; }
        var teams = TeamName.Matches(text).Select(m => m.Groups[1].Value.Trim() + " team").ToList();
        var rest = TeamName.Replace(text, " ");
        var addresses = rest.Split(',', ';', ' ').Select(p => p.Trim()).Where(p => p.Contains('@')).Select(nameOf);
        var all = teams.Concat(addresses).Distinct().ToList();
        return all.Count == 0 ? null : string.Join(", ", all);
    }

    private static JourneyStop PeopleStop(string kind, string title, ApprovalTask? row, WorkflowRun run, bool here, bool future, string? planned,
        DateTime nowUtc, IReadOnlySet<DateTime> closed, Func<string, string> nameOf, string? position)
    {
        var verb = kind == "task" ? "Completed by " : "Approved by ";
        if (here && run.Status == RunStatus.ChangesRequested)
        {
            var returned = row?.Status == ApprovalStatus.Returned ? row : null;
            return new(kind, title, "returned", "Sent back — waiting on " + (run.SubmitterName.Length > 0 ? run.SubmitterName : run.SubmitterEmail) + " to edit",
                returned?.DecidedAt is DateTime r ? Ui.When(r) : null, returned?.Comment,
                returned?.DecidedAt is DateTime since ? WaitAge.Of(since, nowUtc, closed) : null, position);
        }
        if (here && row is { Status: ApprovalStatus.Pending } pending && RunStatus.IsWaitingOnPeople(run.Status))
            return new(kind, title, "current", WaitingOn(pending, nameOf, planned), Ui.When(pending.CreatedAt), null,
                WaitAge.Of(pending.CreatedAt, nowUtc, closed, pending.DueOn, pending.EscalatedAt is not null), position ?? (pending.Position.Length > 0 ? pending.Position : null));
        if (here && run.Status == RunStatus.Failed)
            return new(kind, title, "failed", planned, Note: run.Error, Position: position);
        if (!future && row is not null)
        {
            return row.Status switch
            {
                ApprovalStatus.Approved or ApprovalStatus.Done => new(kind, title, "done", verb + nameOf(row.DecidedBy ?? ""), row.DecidedAt is DateTime at ? Ui.When(at) : null, Position: position),
                ApprovalStatus.Rejected => new(kind, title, "rejected", "Not approved by " + nameOf(row.DecidedBy ?? ""), row.DecidedAt is DateTime rt ? Ui.When(rt) : null, row.Comment, Position: position),
                ApprovalStatus.Cancelled => new(kind, title, "cancelled", planned, Position: position),
                _ => new(kind, title, RunStatus.IsFinished(run.Status) ? "cancelled" : "upcoming", planned, Position: position),
            };
        }
        return new(kind, title, RunStatus.IsFinished(run.Status) ? "cancelled" : "upcoming", planned, Position: position);
    }

    /// <summary>"Business Administrator team: Avery Admin (or Sam Stand-in) · all must approve, 1 of 2 so far".</summary>
    public static string WaitingOn(ApprovalTask task, Func<string, string> nameOf, string? planned = null)
    {
        var waiting = task.AssigneeList.Where(a => !task.HasApproved(a)).Select(nameOf).ToList();
        var text = string.Join(task.Rule == ApprovalRules.All ? " and " : " or ", waiting);
        var teams = planned is null ? new List<string>() : planned.Split(", ").Where(p => p.EndsWith(" team")).ToList();
        if (teams.Count > 0) text = string.Join(", ", teams) + ": " + text;
        var standIns = task.StandInPairs.Select(p => p.StandIn).Concat(task.EscalatedList).Distinct().Select(nameOf).ToList();
        if (standIns.Count > 0) text += $" (or {string.Join(", ", standIns)})";
        if (task.Rule == ApprovalRules.All && task.AssigneeList.Count() > 1)
            text += $" · all must approve, {task.ApprovedByList.Count()} of {task.AssigneeList.Count()} so far";
        return text;
    }

    /// <summary>Names for addresses, from the people who have signed in; unknown addresses stay as they are.</summary>
    public static async Task<Func<string, string>> NamesAsync(AppDbContext db, IEnumerable<string> emails, CancellationToken ct = default)
    {
        var wanted = emails.Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Contains('@')).Distinct().Take(500).ToList();
        var names = wanted.Count == 0 ? new Dictionary<string, string>()
            : (await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Email)).Select(u => new { u.Email, u.Name }).ToListAsync(ct))
                .Where(u => u.Name.Length > 0).GroupBy(u => u.Email.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Name);
        return email => names.TryGetValue(email.Trim().ToLowerInvariant(), out var name) ? name : email;
    }

    /// <summary>For a page of requests: where each one that is waiting on someone is now, and for how long.</summary>
    public static async Task<Dictionary<int, CurrentStop>> CurrentStopsAsync(AppDbContext db, SchoolCalendar calendar, IReadOnlyCollection<WorkflowRun> runs, CancellationToken ct = default)
    {
        var waiting = runs.Where(r => RunStatus.IsWaitingOnPeople(r.Status) || r.Status == RunStatus.ChangesRequested).ToList();
        var result = new Dictionary<int, CurrentStop>();
        if (waiting.Count == 0) return result;
        var ids = waiting.Select(r => r.Id).ToList();
        var tasks = await db.ApprovalTasks.AsNoTracking()
            .Where(t => ids.Contains(t.RunId) && (t.Status == ApprovalStatus.Pending || t.Status == ApprovalStatus.Returned)).ToListAsync(ct);
        var nameOf = await NamesAsync(db, tasks.SelectMany(t => t.AssigneeList.Concat(t.StandInPairs.Select(p => p.StandIn))), ct);
        var closed = await calendar.ClosedDaysAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var run in waiting)
        {
            if (run.Status == RunStatus.ChangesRequested)
            {
                var returned = tasks.Where(t => t.RunId == run.Id && t.Status == ApprovalStatus.Returned).OrderByDescending(t => t.Id).FirstOrDefault();
                result[run.Id] = new CurrentStop("Sent back for changes", null, run.SubmitterName.Length > 0 ? run.SubmitterName : run.SubmitterEmail,
                    returned?.DecidedAt is DateTime since ? WaitAge.Of(since, now, closed) : null);
                continue;
            }
            var pending = tasks.Where(t => t.RunId == run.Id && t.Status == ApprovalStatus.Pending && t.NodeId == run.CurrentNodeId).OrderByDescending(t => t.Id).FirstOrDefault();
            if (pending is null) continue;
            result[run.Id] = new CurrentStop(pending.Title, pending.Position.Length > 0 ? pending.Position : null, WaitingOn(pending, nameOf),
                WaitAge.Of(pending.CreatedAt, now, closed, pending.DueOn, pending.EscalatedAt is not null));
        }
        return result;
    }
}
