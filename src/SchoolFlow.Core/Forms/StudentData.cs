using SchoolFlow.Core.Expressions;

namespace SchoolFlow.Core.Forms;

/// <summary>
/// Which answers on a workflow carry student information: Student fields, and anything worked out
/// from them (a calculated field, a default value, a table column, a "Set value" step). People
/// without student access see none of these, and none may be written into a message or ticket.
/// </summary>
public static class StudentData
{
    /// <summary>Settings that are formulas evaluated internally and never shown to anyone.</summary>
    private static readonly HashSet<string> HiddenFormulaSettings = new(StringComparer.OrdinalIgnoreCase) { "expression", "priorityFormula", "dueDate", "until" };

    private static IEnumerable<string> FormulaRefs(string? source) =>
        !string.IsNullOrWhiteSpace(source) && Formula.TryParse(source, out var f, out _) ? f!.References : Enumerable.Empty<string>();

    private static IEnumerable<string> TemplateRefs(string? text)
    {
        foreach (var expression in Template.GetExpressions(text ?? ""))
            foreach (var r in FormulaRefs(expression)) yield return r;
    }

    /// <summary>The keys whose values carry student information. Empty when the workflow has no Student field.</summary>
    public static HashSet<string> KeysOf(WorkflowDefinition def)
    {
        var tainted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = def.Form.Fields
            .Concat(def.Flow.Nodes.Where(n => n.Type == NodeTypes.Task).SelectMany(n => n.Fields ?? new List<FieldDef>()))
            .Where(f => !string.IsNullOrWhiteSpace(f.Key)).ToList();
        foreach (var f in fields.Where(f => f.Type == FieldTypes.Student)) tainted.Add(f.Key);
        if (tainted.Count == 0) return tainted;

        // Anything computed from a tainted value is tainted too, through any number of steps.
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var f in fields.Where(f => !tainted.Contains(f.Key)))
            {
                var refs = FormulaRefs(f.Type == FieldTypes.Calculated ? f.Formula : null)
                    .Concat(FormulaRefs(f.Default))
                    .Concat((f.Columns ?? new List<ColumnDef>()).Where(c => c.Type == FieldTypes.Calculated).SelectMany(c => FormulaRefs(c.Formula)));
                if (refs.Any(tainted.Contains)) changed |= tainted.Add(f.Key);
            }
            foreach (var n in def.Flow.Nodes.Where(n => n.Type == NodeTypes.SetVariable))
            {
                var name = n.Get("name").Trim();
                if (name.Length > 0 && !tainted.Contains(name) && FormulaRefs(n.Get("expression")).Any(tainted.Contains))
                    changed |= tainted.Add(name);
            }
        }
        return tainted;
    }

    /// <summary>
    /// What is kept of student information when answers are stored (owner's rule: the student number
    /// and nothing else). A Student field keeps only <c>{ id }</c>; anything worked out from student
    /// details is not kept at all. The details are looked up again from the roster when needed.
    /// </summary>
    public static void Minimize(WorkflowDefinition def, IDictionary<string, object?> values)
    {
        var tainted = KeysOf(def);
        if (tainted.Count == 0) return;
        var studentFields = def.Form.Fields.Where(f => f.Type == FieldTypes.Student && !string.IsNullOrWhiteSpace(f.Key))
            .Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in values.Keys.Where(tainted.Contains).ToList())
        {
            if (!studentFields.Contains(key)) { values.Remove(key); continue; }
            var id = FormEvaluator.StudentIdOf(values[key]);
            if (id.Length == 0) { values[key] = null; continue; }
            var kept = Values.NewDict();
            kept["id"] = id;
            values[key] = kept;
        }
    }

    /// <summary>The steps whose shown text (messages, instructions, tickets) would contain student information.</summary>
    public static IEnumerable<(NodeDef Node, string Setting)> Leaks(WorkflowDefinition def, HashSet<string>? tainted = null)
    {
        tainted ??= KeysOf(def);
        if (tainted.Count == 0) yield break;
        foreach (var node in def.Flow.Nodes)
        {
            foreach (var (key, value) in node.Config.Concat(node.CustomFields))
            {
                if (HiddenFormulaSettings.Contains(key)) continue;
                if (TemplateRefs(value).Any(tainted.Contains)) yield return (node, key);
            }
        }
    }
}
