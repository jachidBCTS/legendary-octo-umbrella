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
        "submitter", "ticket", "approval", "approvals", "run", "event", "chain", "attachments", "rates", "task", "_previous", "_restart", "_signatures",
        "true", "false", "null", "blank", "and", "or", "not",
    };

    public const int MaxFields = 250;

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
        // Student information may only be seen by people given student access, so it is never written
        // into a message, instruction or ticket, which reach people regardless of that access.
        foreach (var (node, setting) in Forms.StudentData.Leaks(def).DistinctBy(l => l.Node.Id))
            issues.Add(new Issue("error", "flow", node.Id,
                $"“{(string.IsNullOrWhiteSpace(node.Label) ? node.Type : node.Label)}”: student details cannot be used in messages, instructions or tickets — the people who receive them may not have student access. Remove them from “{setting}”."));
        return issues;
    }

    // ---- form ------------------------------------------------------------------------

    private static void ValidateWorksheets(FormDefinition form, List<Issue> issues)
    {
        var byKey = form.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Key)).GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in form.Fields.Where(f => f.Type == FieldTypes.Worksheet))
        {
            var name = string.IsNullOrWhiteSpace(sheet.Label) ? sheet.Key : sheet.Label;
            void Error(string message) => issues.Add(new Issue("error", "form", sheet.Key, $"“{name}”: {message}"));
            if (sheet.Rows is null || sheet.Rows.Count == 0) { Error("a worksheet needs at least one row."); continue; }
            if (sheet.Rows.Count > 60) Error("a worksheet can have at most 60 rows.");
            foreach (var key in sheet.Rows.SelectMany(r => r.FieldKeys()))
            {
                if (!byKey.TryGetValue(key, out var field)) Error($"a row mentions '{key}', which is not a field on this form.");
                else if (Array.IndexOf(FieldTypes.InWorksheet, field.Type) < 0) Error($"'{key}' cannot go inside a worksheet. Use text, number, currency, date, time, dropdown or calculated fields.");
                else if (!used.Add(key)) Error($"'{key}' is used more than once in a worksheet. Each field can appear in one place.");
            }
        }
    }

    private static void ValidateForm(FormDefinition form, HashSet<string> known, List<Issue> issues)
    {
        if (!CopyModes.IsValid(form.AllowCopy))
            issues.Add(new Issue("error", "form", null, $"Unknown “start from a previous request” setting '{form.AllowCopy}'."));

        if (form.Style is not (null or "" or "paper"))
            issues.Add(new Issue("error", "form", null, $"Unknown form style '{form.Style}'."));
        ValidateWorksheets(form, issues);

        if (form.Fields.Count > MaxFields)
            issues.Add(new Issue("error", "form", null, $"A form can have at most {MaxFields} fields (this one has {form.Fields.Count}). Split it into separate forms."));

        var hasOpens = FormDefinition.TryParseDay(form.OpensOn, out var opens);
        var hasCloses = FormDefinition.TryParseDay(form.ClosesOn, out var closes);
        if (!string.IsNullOrWhiteSpace(form.OpensOn) && !hasOpens) issues.Add(new Issue("error", "form", null, "The form's opening date is not a valid date."));
        if (!string.IsNullOrWhiteSpace(form.ClosesOn) && !hasCloses) issues.Add(new Issue("error", "form", null, "The form's closing date is not a valid date."));
        if (hasOpens && hasCloses && closes < opens) issues.Add(new Issue("error", "form", null, "The form closes before it opens."));
        if (form.Fields.Count > 0 && form.Fields[^1].Type == FieldTypes.Page)
            issues.Add(new Issue("warning", "form", form.Fields[^1].Key, "The last page of the form is empty: a page break has nothing after it."));

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
            else if (!FieldTypes.IsLayout(f.Type))
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
            if (f.Type == FieldTypes.PerDiem)
            {
                var types = form.Fields.Where(x => !string.IsNullOrWhiteSpace(x.Key)).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Type, StringComparer.OrdinalIgnoreCase);
                void Needs(string? key, string what, string[] allowed, bool required)
                {
                    if (string.IsNullOrWhiteSpace(key)) { if (required) Error($"choose the field that holds {what}."); return; }
                    if (!types.TryGetValue(key.Trim(), out var type)) Error($"'{key}' ({what}) is not a field on this form.");
                    else if (Array.IndexOf(allowed, type) < 0) Error($"'{key}' ({what}) must be a {string.Join(" or ", allowed)} field.");
                }
                Needs(f.PerDiem?.Zip, "the destination ZIP code", new[] { FieldTypes.Text, FieldTypes.Number }, true);
                Needs(f.PerDiem?.From, "the first day of travel", new[] { FieldTypes.Date }, true);
                Needs(f.PerDiem?.To, "the last day of travel", new[] { FieldTypes.Date }, false);
            }
            if (f.IsReadOnly && string.IsNullOrWhiteSpace(f.Default))
                issues.Add(new Issue("warning", "form", f.Key, $"“{name}” is read only but has no default value, so it will always be blank."));
            if (f.Type is FieldTypes.Location or FieldTypes.Staff && !string.IsNullOrWhiteSpace(f.DistrictField))
            {
                var source = form.Fields.FirstOrDefault(x => string.Equals(x.Key, f.DistrictField.Trim(), StringComparison.OrdinalIgnoreCase));
                if (source is null) Error($"its district comes from '{f.DistrictField}', which is not a field on this form.");
                else if (FieldTypes.IsLayout(source.Type) || source.Type is FieldTypes.Table or FieldTypes.Attachments or FieldTypes.MultiSelect or FieldTypes.Checkbox)
                    Error($"its district cannot come from “{(string.IsNullOrWhiteSpace(source.Label) ? source.Key : source.Label)}”: choose a choice, text, location or staff field.");
            }
        }

        // Circular references between calculated fields / visibility rules.
        var valid = form.Fields.Where(f => IsValidKey(f.Key)).GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var keys = new HashSet<string>(valid.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);
        FormEvaluator.TopologicalOrder(valid, f => f.Key, f =>
        {
            var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in new[] { f.VisibleWhen, f.Type == FieldTypes.Calculated ? f.Formula : null, f.Default })
                if (!string.IsNullOrWhiteSpace(src) && Formula.TryParse(src, out var parsed, out _)) refs.UnionWith(parsed!.References);
            // A per diem field reads its ZIP code and date fields (as the form evaluator orders it).
            if (f.Type == FieldTypes.PerDiem && f.PerDiem is { } perDiem)
                foreach (var key in new[] { perDiem.Zip, perDiem.From, perDiem.To })
                    if (!string.IsNullOrWhiteSpace(key)) refs.Add(key.Trim());
            if (!string.IsNullOrWhiteSpace(f.DistrictField)) refs.Add(f.DistrictField.Trim());
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
        // What a Task step collects joins the request under the field's key, so each key must be new.
        var taskFields = new List<(NodeDef Node, FieldDef Field)>();
        foreach (var n in flow.Nodes.Where(n => n.Type == NodeTypes.Task))
        {
            foreach (var f in n.Fields ?? new List<FieldDef>())
            {
                var label = string.IsNullOrWhiteSpace(f.Label) ? f.Key : f.Label;
                if (Array.IndexOf(FieldTypes.InTask, f.Type) < 0) { Error(n.Id, $"“{Name(n)}”: “{label}” is a kind of field a task cannot ask for."); continue; }
                if (string.IsNullOrWhiteSpace(f.Label)) Error(n.Id, $"“{Name(n)}”: a field needs a label.");
                if (!IsValidKey(f.Key)) Error(n.Id, $"“{Name(n)}”: “{label}” needs a key that starts with a letter and uses only letters, digits and underscores.");
                else if (FieldTypes.IsLayout(f.Type)) continue;
                else if (!known.Add(f.Key)) Error(n.Id, $"“{Name(n)}”: the key '{f.Key}' is already used by the form, another step or a reserved name. Choose a different key.");
                else taskFields.Add((n, f));
            }
        }
        foreach (var (n, f) in taskFields)
        {
            var label = string.IsNullOrWhiteSpace(f.Label) ? f.Key : f.Label;
            if (FieldTypes.HasOptions(f.Type)) CheckOptions(f.Options, message => Error(n.Id, $"“{Name(n)}”, “{label}”: {message}"));
            if (f.Min is decimal min && f.Max is decimal max && min > max) Error(n.Id, $"“{Name(n)}”, “{label}”: minimum is greater than maximum.");
            if (f.IsReadOnly && string.IsNullOrWhiteSpace(f.Default))
                issues.Add(new Issue("warning", "flow", n.Id, $"“{Name(n)}”, “{label}” is read only but has no default value, so it will be blank the first time."));
            if (f.Type == FieldTypes.Calculated)
            {
                if (string.IsNullOrWhiteSpace(f.Formula)) Error(n.Id, $"“{Name(n)}”, “{label}”: needs a formula.");
                else CheckFormula(f.Formula, "flow", n.Id, $"“{label}” formula", known, issues);
            }
            foreach (var (source, what) in new[] { (f.VisibleWhen, "show-when"), (f.RequiredWhen, "required-when"), (f.Default, "default value") })
                if (!string.IsNullOrWhiteSpace(source)) CheckFormula(source, "flow", n.Id, $"“{label}” {what}", known, issues);
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
                    CheckFollowUp(n, name, Text, Error);
                    Formula_("dueDate", "due date", false);
                    if (!flow.Edges.Any(e => e.From == n.Id && e.Port == "approved"))
                        Warn(n.Id, $"“{name}”: the Approved branch is not connected, so nothing happens after approval.");
                    break;
                case NodeTypes.Task:
                    Text("assignees", "“Who does this”", true);
                    Text("instructions", "instructions", false);
                    CheckFollowUp(n, name, Text, Error);
                    Formula_("dueDate", "due date", false);
                    if (!flow.Edges.Any(e => e.From == n.Id)) Warn(n.Id, $"“{name}”: nothing is connected after this task.");
                    break;
                case NodeTypes.ApprovalChain:
                {
                    Text("instructions", "instructions", false);
                    CheckFollowUp(n, name, Text, Error);
                    Formula_("dueDate", "due date", false);
                    var chainSource = n.Get("chainSource");
                    var departmentSource = n.Get("departmentSource");
                    FieldDef? FieldOf(string key) => def.Form.Fields.FirstOrDefault(f => string.Equals(f.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (chainSource == "template" && !long.TryParse(n.Get("chainId"), out _))
                        Error(n.Id, $"“{name}”: choose which approval chain template to use.");
                    if (chainSource == "field")
                    {
                        if (!isForm) Error(n.Id, $"“{name}”: a ticket-triggered workflow has no form, so the chain cannot come from a form field.");
                        else if (FieldOf(n.Get("chainField"))?.Type != FieldTypes.ApprovalChain)
                            Error(n.Id, $"“{name}”: choose an Approval chain field from the form (add one on the Form tab if there is none).");
                    }
                    if (departmentSource == "fixed" && !long.TryParse(n.Get("departmentId"), out _))
                        Error(n.Id, $"“{name}”: choose the department whose final approver should sign off.");
                    if (departmentSource == "field")
                    {
                        if (!isForm) Error(n.Id, $"“{name}”: a ticket-triggered workflow has no form, so the department cannot come from a form field.");
                        else if (FieldOf(n.Get("departmentField"))?.Type != FieldTypes.Department)
                            Error(n.Id, $"“{name}”: choose a Department field from the form (add one on the Form tab if there is none).");
                    }
                    if (chainSource is not ("template" or "field") && departmentSource is not ("fixed" or "field"))
                        Error(n.Id, $"“{name}”: nobody would approve. Choose an approval chain, a department, or both.");
                    if (!flow.Edges.Any(e => e.From == n.Id && e.Port == "approved"))
                        Warn(n.Id, $"“{name}”: the Approved branch is not connected, so nothing happens after approval.");
                    break;
                }
                case NodeTypes.Notify:
                    Text("to", "recipients", true);
                    Text("subject", "subject", true);
                    Text("message", "message", false);
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
                    foreach (var key in new[] { "groupId", "departmentId", "category", "subCategory", "itemCategory", "cc", "tags" }) Text(key, key, false);
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

    /// <summary>Reminder and escalation settings shared by the approval steps.</summary>
    private static void CheckFollowUp(NodeDef n, string name, Action<string, string, bool> text, Action<string?, string> error)
    {
        text("escalateTo", "escalate-to", false);
        text("dueNotify", "“if overdue, also tell”", false);
        foreach (var (key, what) in new[] { ("remindDays", "Remind after"), ("escalateDays", "Escalate after") })
        {
            var value = n.Get(key).Trim();
            if (value.Length > 0 && (!int.TryParse(value, out var days) || days < 0 || days > 60))
                error(n.Id, $"“{name}”: “{what}” must be a whole number of school days from 0 to 60.");
        }
        if (int.TryParse(n.Get("escalateDays").Trim(), out var escalate) && escalate > 0 && string.IsNullOrWhiteSpace(n.Get("escalateTo")))
            error(n.Id, $"“{name}”: say who to escalate to, or clear “Escalate after”.");
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
