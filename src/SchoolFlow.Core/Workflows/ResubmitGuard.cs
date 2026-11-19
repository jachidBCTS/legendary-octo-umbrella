using SchoolFlow.Core.Expressions;

namespace SchoolFlow.Core.Workflows;

/// <summary>
/// Decides whether a request that was sent back and resubmitted must start its approvals again, so an
/// approval given for one amount (or one route) cannot be carried over to a bigger amount or another route.
/// </summary>
public static class ResubmitGuard
{
    /// <summary>True when two stored answers are the same (numbers compare by value, so 5 equals 5.00).</summary>
    public static bool Same(object? a, object? b)
    {
        if (Values.IsBlank(a) && Values.IsBlank(b)) return true;
        if (Values.IsBlank(a) || Values.IsBlank(b)) return false;
        if (a is decimal x && b is decimal y) return x == y;
        return Json.Serialize(a!) == Json.Serialize(b!);
    }

    /// <summary>
    /// Whether an answer is unchanged. A student is the same student if the number is the same: the
    /// details come from the live roster and may differ between two reads (or be missing when the roster
    /// could not be reached), without the requester having changed anything.
    /// </summary>
    public static bool SameAnswer(FieldDef field, object? a, object? b) =>
        field.Type == FieldTypes.Student
            ? string.Equals(Forms.FormEvaluator.StudentIdOf(a), Forms.FormEvaluator.StudentIdOf(b), StringComparison.OrdinalIgnoreCase)
            : Same(a, b);

    /// <summary>
    /// The money fields (currency, and calculated amounts shown as currency) whose value went up. Plain
    /// numbers (students, chaperones, miles) are not costs on their own; what they cost shows in a total.
    /// </summary>
    public static List<FieldDef> Increased(IEnumerable<FieldDef> fields, IDictionary<string, object?> before, IDictionary<string, object?> after)
    {
        var result = new List<FieldDef>();
        foreach (var field in fields)
        {
            if (FieldTypes.IsLayout(field.Type) || string.IsNullOrWhiteSpace(field.Key)) continue;
            if (field.Type != FieldTypes.Currency && !(field.Type == FieldTypes.Calculated && field.Format == "currency")) continue;
            var now = after.TryGetValue(field.Key, out var n) ? n : null;
            if (now is not decimal newer) continue;
            var then = before.TryGetValue(field.Key, out var o) ? o : null;
            var older = then is decimal d ? d : Values.IsBlank(then) ? 0m : (decimal?)null;
            if (older is decimal value && newer > value) result.Add(field);
        }
        return result;
    }

    /// <summary>
    /// The names read by the steps that brought the request to <paramref name="currentNodeId"/>: the
    /// conditions on the way, and the settings of the earlier approval steps (who approves, which chain,
    /// which department). A change to any of them could have sent the request to different approvers.
    /// </summary>
    public static HashSet<string> RoutingNames(FlowDefinition flow, string? currentNodeId)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (currentNodeId is null) return names;
        // Every step from which the current one can be reached (and the current one itself).
        var earlier = new HashSet<string> { currentNodeId };
        var queue = new Queue<string>(earlier);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var edge in flow.Edges.Where(e => e.To == id))
                if (earlier.Add(edge.From)) queue.Enqueue(edge.From);
        }
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in flow.Nodes.Where(n => earlier.Contains(n.Id)))
        {
            switch (node.Type)
            {
                case NodeTypes.Condition:
                    AddFormula(names, node.Get("expression"));
                    break;
                case NodeTypes.SetVariable:
                    variables[node.Get("name").Trim()] = node.Get("expression");
                    break;
                case NodeTypes.Approval:
                case NodeTypes.ApprovalChain:
                    // The step being returned to works out its approvers again on its own (a chain keeps
                    // approvals only for steps that are unchanged), so only the steps before it count here.
                    if (node.Id == currentNodeId) break;
                    foreach (var (key, value) in node.Config)
                    {
                        if (key is "instructions" or "dueDate" or "dueNotify" or "remindDays" or "escalateDays" or "escalateTo") continue;
                        if (key is "chainField" or "departmentField") { if (!string.IsNullOrWhiteSpace(value)) names.Add(value.Trim()); continue; }
                        foreach (var expression in Template.GetExpressions(value)) AddFormula(names, expression);
                    }
                    break;
            }
        }
        // A condition on a calculated step ("needs_board") really depends on what that step reads.
        for (var pass = 0; pass < 10; pass++)
        {
            var added = false;
            foreach (var (name, expression) in variables)
                if (names.Contains(name))
                    foreach (var read in References(expression))
                        added |= names.Add(read);
            if (!added) break;
        }
        return names;
    }

    private static void AddFormula(HashSet<string> names, string? source)
    {
        foreach (var name in References(source)) names.Add(name);
    }

    private static IEnumerable<string> References(string? source) =>
        Formula.TryParse(source, out var formula, out _) && formula is not null ? formula.References : Enumerable.Empty<string>();

    /// <summary>
    /// Why approvals already given must be asked for again, or an empty list when they can stand.
    /// <paramref name="fields"/> are the request's own fields (form and tasks) as they were answered.
    /// </summary>
    public static List<string> Reasons(FlowDefinition flow, string? currentNodeId, IEnumerable<FieldDef> fields,
        IDictionary<string, object?> before, IDictionary<string, object?> after)
    {
        var list = fields.Where(f => !FieldTypes.IsLayout(f.Type) && !string.IsNullOrWhiteSpace(f.Key)).ToList();
        var reasons = Increased(list, before, after).Select(f => $"{Label(f)} went up").ToList();
        var routing = RoutingNames(flow, currentNodeId);
        foreach (var field in list.Where(f => routing.Contains(f.Key)))
        {
            var changed = !SameAnswer(field, before.TryGetValue(field.Key, out var o) ? o : null, after.TryGetValue(field.Key, out var n) ? n : null);
            if (changed && !reasons.Contains($"{Label(field)} went up")) reasons.Add($"{Label(field)} changed");
        }
        return reasons;
    }

    private static string Label(FieldDef field) => string.IsNullOrWhiteSpace(field.Label) ? field.Key : field.Label.Trim();
}
