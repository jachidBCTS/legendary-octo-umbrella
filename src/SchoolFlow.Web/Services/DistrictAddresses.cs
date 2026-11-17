using SchoolFlow.Core;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Forms;

namespace SchoolFlow.Web.Services;

/// <summary>
/// With <c>Auth:AllowedDomains</c> set, anyone who must sign in to act on a request (an approver, a
/// task assignee, someone a step escalates to) has to have a district address — an outside address
/// could never sign in, so the request would sit waiting. When such a person comes from an answer
/// (for example an "Approver's email" field), that answer is checked on the form itself so the
/// person filling it in sees the problem straight away.
/// </summary>
public static class DistrictAddresses
{
    /// <summary>Step settings naming people who must sign in to act. Notification recipients are not among them.</summary>
    public static readonly string[] ActingSettings = { "approvers", "assignees", "escalateTo" };

    /// <summary>Answers that a step uses to choose who acts.</summary>
    public static HashSet<string> KeysChoosingPeople(WorkflowDefinition def)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in def.Flow.Nodes)
            foreach (var setting in ActingSettings)
                foreach (var expression in Template.GetExpressions(node.Get(setting)))
                    if (Formula.TryParse(expression, out var formula, out _))
                        keys.UnionWith(formula!.References);
        return keys;
    }

    /// <summary>Adds an error to every answer that chooses who acts and holds an address outside the district.</summary>
    public static void Check(WorkflowDefinition def, FormResult form, IReadOnlyCollection<string>? domains)
    {
        if (domains is not { Count: > 0 }) return;
        foreach (var key in KeysChoosingPeople(def))
        {
            if (!form.Fields.TryGetValue(key, out var state) || !state.Visible) continue;
            if (form.Values.GetValueOrDefault(key) is not string email || !email.Contains('@')) continue;
            var domain = email[(email.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant();
            if (!domains.Contains(domain))
                state.Errors.Add($"This person has to sign in to act on the request, so use a district address ({string.Join(" or ", domains.Select(d => "@" + d))}).");
        }
    }
}
