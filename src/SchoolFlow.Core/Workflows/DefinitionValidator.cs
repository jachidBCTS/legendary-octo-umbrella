using System.Text.RegularExpressions;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Forms;

namespace SchoolFlow.Core.Workflows;

public sealed record Issue(string Severity, string Area, string? Target, string Message)
{
    public bool IsError => Severity == "error";
}

/// <summary>Design-time checks run when a builder validates or publishes a workflow.</summary>
public static class DefinitionValidator
{
    private static readonly Regex KeyPattern = new("^[A-Za-z_][A-Za-z0-9_]{0,59}$", RegexOptions.Compiled);

    /// <summary>Names formulas can always read, so fields and variables may not reuse them.</summary>
    public static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "submitter", "ticket", "approval", "approvals", "run", "event",
        "true", "false", "null", "blank", "and", "or", "not",
    };

    public static bool IsValidKey(string? key) => key is not null && KeyPattern.IsMatch(key);

    public static List<Issue> Validate(WorkflowDefinition def)
    {
        var issues = new List<Issue>();
        var isForm = def.Trigger.Type != TriggerTypes.Ticket;

        if (def.Trigger.Type is not (TriggerTypes.Form or TriggerTypes.Ticket))
            issues.Add(new Issue("error", "trigger", null, $"Unknown trigger type '{def.Trigger.Type}'."));
        if (!isForm && !string.IsNullOrWhiteSpace(def.Trigger.Condition))
            CheckFormula(def.Trigger.Condition, "trigger", null, "Start condition", null, issues);

        var known = new HashSet<string>(Reserved, StringComparer.OrdinalIgnoreCase);
        if (isForm) ValidateForm(def.Form, known, issues);
        ValidateFlow(def, known, isForm, issues);
        return issues;
    }

    // ---- form ------------------------------------------------------------------------

    private static void ValidateForm(FormDefinition form, HashSet<string> known, List<Issue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in form.Fields)
        {
            var name = string.IsNullOrWhiteSpace(f.Label) ? f.Key : f.Label;
            if (!IsValidKey(f.Key))
                issues.Add(new Issue("error", "form", f.Key, $"“{name}”: the key must start with a letter and use only letters, digits and underscores."));
            else if (Reserved.Contains(f.Key))
                issues.Add(new Issue("error", "form", f.Key, $"“{name}”: '{f.Key}' is a reserved name — choose a different key."));
            else if (!seen.Add(f.Key))
                issues.Add(new Issue("error", "form", f.Key, $"Two fields share the key '{f.Key}'. Keys must be unique."));
            else if (f.Type != FieldTypes.Section)
                known.Add(f.Key);
        }

        foreach (var f in form.Fields)
        {
            var name = string.IsNullOrWhiteSpace(f.Label) ? f.Key : f.Label;
            void Error(string message) => issues.Add(new Issue("error", "form", f.Key, $"“{name}”: {message}"));

            if (Array.IndexOf(FieldTypes.All, f.Type) < 0) { Error($"unknown field type '{f.Type}'."); continue; }
            if (string.IsNullOrWhiteSpace(f.Label)) Error("needs a label.");

            if (FieldTypes.HasOptions(f.Type)) CheckOptions(f.Options, Error);
            if (f.Type == FieldTypes.Lookup && Array.IndexOf(LookupSources.FormSources, f.Source ?? "") < 0)
                Error("choose what to look up in Freshservice.");
            if (f.Min is decimal min && f.Max is decimal max && min > max) Error("minimum is greater than maximum.");
            if (f.MinRows is int minRows && f.MaxRows is int maxRows && minRows > maxRows) Error("minimum rows is greater than maximum rows.");

            if (f.Type == FieldTypes.Calculated)
            {
                if (string.IsNullOrWhiteSpace(f.Formula)) Error("needs a formula.");
                else CheckFormula(f.Formula, "form", f.Key, $"“{name}” formula", known, issues);
            }
            if (!string.IsNullOrWhiteSpace(f.VisibleWhen)) CheckFormula(f.VisibleWhen, "form", f.Key, $"“{name}” show-when", known, issues);
            if (!string.IsNullOrWhiteSpace(f.RequiredWhen)) CheckFormula(f.RequiredWhen, "form", f.Key, $"“{name}” required-when", known, issues);
            if (!string.IsNullOrWhiteSpace(f.Default)) CheckFormula(f.Default, "form", f.Key, $"“{name}” default value", known, issues);

            foreach (var rule in f.Rules ?? new List<RuleDef>())
            {
                if (string.IsNullOrWhiteSpace(rule.Check)) { Error("has a validation rule with no formula."); continue; }
                CheckFormula(rule.Check, "form", f.Key, $"“{name}” validation rule", known, issues);
                if (string.IsNullOrWhiteSpace(rule.Message))
                    issues.Add(new Issue("warning", "form", f.Key, $"“{name}”: a validation rule has no message to show the person filling in the form."));
            }

            if (f.Type == FieldTypes.Table) ValidateTable(f, name, known, issues);
        }

        // Circular references between calculated fields / visibility rules.
        var valid = form.Fields.Where(f => IsValidKey(f.Key)).GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var keys = new HashSet<string>(valid.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);
        FormEvaluator.TopologicalOrder(valid, f => f.Key, f =>
        {
            var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in new[] { f.VisibleWhen, f.Type == FieldTypes.Calculated ? f.Formula : null })
                if (!string.IsNullOrWhiteSpace(src) && Formula.TryParse(src, out var parsed, out _)) refs.UnionWith(parsed!.References);
            return refs.Where(keys.Contains);
        }, out var cyclic);
        foreach (var key in cyclic)
            issues.Add(new Issue("error", "form", key, $"'{key}' is part of a circular reference: its formula or show-when rule depends on itself, directly or through other fields."));
    }

    private static void CheckOptions(List<string>? options, Action<string> error)
    {
        var list = (options ?? new List<string>()).Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
        if (list.Count == 0) error("needs at least one option.");
        else if (list.Select(o => o.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count) error("has duplicate options.");
    }

    private static void ValidateTable(FieldDef f, string name, HashSet<string> known, List<Issue> issues)
    {
        void Error(string message) => issues.Add(new Issue("error", "form", f.Key, $"“{name}”: {message}"));
        var columns = f.Columns ?? new List<ColumnDef>();
        if (columns.Count == 0) { Error("a table needs at least one column."); return; }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in columns)
        {
            if (!IsValidKey(c.Key)) Error($"column key '{c.Key}' must start with a letter and use only letters, digits and underscores.");
            else if (!seen.Add(c.Key)) Error($"two columns share the key '{c.Key}'.");
            if (string.IsNullOrWhiteSpace(c.Label)) Error($"column '{c.Key}' needs a label.");
            if (Array.IndexOf(FieldTypes.Column, c.Type) < 0) Error($"column '{c.Key}' has unsupported type '{c.Type}'.");
            if (c.Type == FieldTypes.Select) CheckOptions(c.Options, m => Error($"column '{c.Key}' {m}"));
        }

        var rowKnown = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        rowKnown.UnionWith(seen);
        foreach (var c in columns.Where(c => c.Type == FieldTypes.Calculated))
        {
            if (string.IsNullOrWhiteSpace(c.Formula)) Error($"column '{c.Key}' needs a formula.");
            else CheckFormula(c.Formula, "form", f.Key, $"“{name}” column '{c.Key}' formula", rowKnown, issues);
        }

        var valid = columns.Where(c => IsValidKey(c.Key)).GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        FormEvaluator.TopologicalOrder(valid, c => c.Key, c =>
            c.Type == FieldTypes.Calculated && Formula.TryParse(c.Formula, out var parsed, out _)
                ? parsed!.References.Where(seen.Contains) : Enumerable.Empty<string>(), out var cyclic);
        foreach (var key in cyclic) Error($"column '{key}' is part of a circular formula.");
    }

    // ---- flow ------------------------------------------------------------------------

    private static void ValidateFlow(WorkflowDefinition def, HashSet<string> known, bool isForm, List<Issue> issues)
    {
        var flow = def.Flow;
        void Error(string? target, string message) => issues.Add(new Issue("error", "flow", target, message));
        void Warn(string? target, string message) => issues.Add(new Issue("warning", "flow", target, message));

        var ids = new HashSet<string>();
        foreach (var n in flow.Nodes)
        {
            if (string.IsNullOrWhiteSpace(n.Id)) Error(null, "A step is missing its ID.");
            else if (!ids.Add(n.Id)) Error(n.Id, $"Two steps share the ID '{n.Id}'.");
            if (Array.IndexOf(NodeTypes.All, n.Type) < 0) Error(n.Id, $"Unknown step type '{n.Type}'.");
        }

        var starts = flow.Nodes.Where(n => n.Type == NodeTypes.Start).ToList();
        if (starts.Count == 0) Error(null, "The flow needs a Start step.");
        if (starts.Count > 1) Error(starts[1].Id, "The flow has more than one Start step.");

        var byId = flow.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.Id)).GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        var usedPorts = new HashSet<string>();
        foreach (var e in flow.Edges)
        {
            if (!byId.TryGetValue(e.From, out var from) || !byId.TryGetValue(e.To, out var to))
            {
                Error(null, "A connection points to a step that no longer exists. Delete and redraw it.");
                continue;
            }
            if (Array.IndexOf(NodeTypes.Ports(from.Type), e.Port) < 0)
                Error(from.Id, $"“{Name(from)}” has a connection from an output it does not have ('{e.Port}').");
            else if (!usedPorts.Add(from.Id + "\n" + e.Port))
                Error(from.Id, $"“{Name(from)}” has two connections leaving the same output. Each output can go to one step.");
            if (to.Type == NodeTypes.Start) Error(to.Id, "Connections cannot lead back into the Start step.");
        }

        // Variables created by steps are readable by later formulas.
        foreach (var n in flow.Nodes)
        {
            if (n.Type == NodeTypes.SetVariable && IsValidKey(n.Get("name").Trim())) known.Add(n.Get("name").Trim());
            if (n.Type == NodeTypes.Lookup && IsValidKey(n.Get("saveAs").Trim())) known.Add(n.Get("saveAs").Trim());
        }

        var producesTicket = !isForm || flow.Nodes.Any(n =>
            n.Type is NodeTypes.CreateTicket or NodeTypes.ServiceRequest ||
            (n.Type == NodeTypes.Lookup && n.Get("saveAs").Trim().Equals("ticket", StringComparison.OrdinalIgnoreCase)));

        foreach (var n in flow.Nodes)
        {
            var name = Name(n);
            void Formula_(string key, string what, bool required)
            {
                var src = n.Get(key);
                if (string.IsNullOrWhiteSpace(src))
                {
                    if (required) Error(n.Id, $"“{name}”: {what} is empty.");
                    return;
                }
                CheckFormula(src, "flow", n.Id, $"“{name}” {what}", known, issues);
            }
            void Text(string key, string what, bool required)
            {
                var src = n.Get(key);
                if (string.IsNullOrWhiteSpace(src))
                {
                    if (required) Error(n.Id, $"“{name}”: {what} is empty.");
                    return;
                }
                CheckTemplate(src, n.Id, $"“{name}” {what}", known, issues);
            }
            void NeedsTicket()
            {
                Text("ticketId", "ticket ID", false);
                if (!producesTicket && string.IsNullOrWhiteSpace(n.Get("ticketId")))
                    Error(n.Id, $"“{name}” needs a ticket, but no earlier step creates one. Add a “Create ticket” step or set the Ticket ID.");
            }

            switch (n.Type)
            {
                case NodeTypes.Start:
                    if (!flow.Edges.Any(e => e.From == n.Id)) Error(n.Id, "Connect the Start step to the first step of your flow.");
                    break;
                case NodeTypes.Condition:
                    Formula_("expression", "condition", true);
                    foreach (var port in NodeTypes.Ports(n.Type))
                        if (!flow.Edges.Any(e => e.From == n.Id && e.Port == port))
                            Warn(n.Id, $"“{name}”: the {(port == "true" ? "Yes" : "No")} branch is not connected, so the request simply completes there.");
                    break;
                case NodeTypes.Approval:
                    Text("approvers", "approvers", true);
                    Text("instructions", "instructions", false);
                    if (!flow.Edges.Any(e => e.From == n.Id && e.Port == "approved"))
                        Warn(n.Id, $"“{name}”: the Approved branch is not connected, so nothing happens after approval.");
                    break;
                case NodeTypes.SetVariable:
                    if (!IsValidKey(n.Get("name").Trim())) Error(n.Id, $"“{name}”: the value name must start with a letter and use only letters, digits and underscores.");
                    else if (Reserved.Contains(n.Get("name").Trim())) Error(n.Id, $"“{name}”: '{n.Get("name").Trim()}' is a reserved name.");
                    Formula_("expression", "formula", true);
                    break;
                case NodeTypes.CreateTicket:
                    Text("subject", "subject", true);
                    Text("description", "description", false);
                    Text("requesterEmail", "requester email", false);
                    Formula_("priorityFormula", "priority formula", false);
                    foreach (var key in new[] { "groupId", "departmentId", "category", "subCategory", "tags" }) Text(key, key, false);
                    if (!isForm && string.IsNullOrWhiteSpace(n.Get("requesterEmail")))
                        Error(n.Id, $"“{name}”: set the requester email — a ticket-triggered workflow has no submitter.");
                    break;
                case NodeTypes.ServiceRequest:
                    Text("itemId", "catalog item ID", true);
                    Text("requesterEmail", "requester email", false);
                    Text("quantity", "quantity", false);
                    if (!isForm && string.IsNullOrWhiteSpace(n.Get("requesterEmail")))
                        Error(n.Id, $"“{name}”: set the requester email — a ticket-triggered workflow has no submitter.");
                    break;
                case NodeTypes.UpdateTicket:
                    NeedsTicket();
                    Formula_("priorityFormula", "priority formula", false);
                    foreach (var key in new[] { "groupId", "responderId" }) Text(key, key, false);
                    if (new[] { "priority", "priorityFormula", "status", "groupId", "responderId" }.All(k => string.IsNullOrWhiteSpace(n.Get(k)))
                        && n.CustomFields.Count == 0)
                        Error(n.Id, $"“{name}”: choose at least one thing to change on the ticket.");
                    break;
                case NodeTypes.AddNote:
                    NeedsTicket();
                    Text("body", "note", true);
                    break;
                case NodeTypes.Lookup:
                    if (!IsValidKey(n.Get("saveAs").Trim())) Error(n.Id, $"“{name}”: “Save as” must start with a letter and use only letters, digits and underscores.");
                    Text("value", "lookup value", false);
                    if (!isForm && n.Get("kind") != "ticket" && string.IsNullOrWhiteSpace(n.Get("value")))
                        Error(n.Id, $"“{name}”: set the email to look up — a ticket-triggered workflow has no submitter.");
                    break;
                case NodeTypes.WaitTicket:
                    NeedsTicket();
                    Formula_("until", "continue-when formula", false);
                    break;
            }

            foreach (var (field, template) in n.CustomFields)
            {
                if (string.IsNullOrWhiteSpace(field)) Error(n.Id, $"“{name}”: a custom field mapping has no field name.");
                else if (!string.IsNullOrWhiteSpace(template)) CheckTemplate(template, n.Id, $"“{name}” custom field '{field}'", known, issues);
            }
        }

        // Reachability.
        if (starts.Count == 1)
        {
            var reached = new HashSet<string> { starts[0].Id };
            var queue = new Queue<string>(reached);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                foreach (var e in flow.Edges.Where(e => e.From == id))
                    if (byId.ContainsKey(e.To) && reached.Add(e.To)) queue.Enqueue(e.To);
            }
            foreach (var n in flow.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.Id) && !reached.Contains(n.Id)))
                Warn(n.Id, $"“{Name(n)}” is not connected to the flow and will never run.");
            if (!flow.Nodes.Any(n => reached.Contains(n.Id) && n.Type != NodeTypes.Start && n.Type != NodeTypes.End))
                Warn(null, "The flow has no steps between Start and End, so submitting it does nothing.");
        }
    }

    private static string Name(NodeDef n) => string.IsNullOrWhiteSpace(n.Label) ? n.Type : n.Label!;

    private static void CheckFormula(string source, string area, string? target, string what, HashSet<string>? known, List<Issue> issues)
    {
        if (!Formula.TryParse(source, out var formula, out var error))
        {
            issues.Add(new Issue("error", area, target, $"{what}: {error}"));
            return;
        }
        if (known is null) return;
        foreach (var name in formula!.References.Where(r => !known.Contains(r)))
            issues.Add(new Issue("warning", area, target, $"{what} mentions '{name}', which is not a field or known value. It will be treated as blank."));
    }

    private static void CheckTemplate(string text, string? target, string what, HashSet<string> known, List<Issue> issues)
    {
        try
        {
            foreach (var source in Template.GetExpressions(text).ToList())
                CheckFormula(source, "flow", target, $"{what} placeholder {{{{{source.Trim()}}}}}", known, issues);
        }
        catch (ExpressionException ex)
        {
            issues.Add(new Issue("error", "flow", target, $"{what}: {ex.Message}"));
        }
    }
}
