using System.Text.RegularExpressions;
using SchoolFlow.Core.Expressions;

namespace SchoolFlow.Core.Forms;

public sealed class FieldState
{
    public bool Visible { get; set; } = true;
    public bool Required { get; set; }
    public object? Value { get; set; }
    public List<string> Errors { get; set; } = new();
    /// <summary>For tables: per-row, per-column error messages (key "rowIndex.columnKey").</summary>
    public Dictionary<string, string>? CellErrors { get; set; }
}

public sealed class FormResult
{
    public Dictionary<string, object?> Values { get; set; } = Expressions.Values.NewDict();
    public Dictionary<string, FieldState> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsValid => Fields.Values.All(f => f.Errors.Count == 0 && (f.CellErrors is null || f.CellErrors.Count == 0));
}

/// <summary>
/// The single source of truth for field logic: visibility, calculations, required rules and validation.
/// The browser calls this (through the API) on every change, and the server calls it again on submit.
/// </summary>
public static class FormEvaluator
{
    public const int MaxTableRows = 200;
    private const int MaxTextLength = 10_000;
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <param name="form">The form definition.</param>
    /// <param name="input">Raw values keyed by field key (strings, numbers, bools, lists, records).</param>
    /// <param name="ambient">Extra variables formulas can read, e.g. submitter.</param>
    /// <param name="applyDefaults">When true, fields missing from <paramref name="input"/> get their default.</param>
    /// <param name="departments">
    /// Active departments (id → name). When given, department fields only accept these; when null
    /// (builder preview, unit tests) any well-formed department value is accepted.
    /// </param>
    /// <param name="locations">Active locations (id → name), checked the same way for location fields.</param>
    /// <param name="closedDays">Days the school is closed, so "school day" date formulas skip them.</param>
    /// <param name="attachments">
    /// Files this person has uploaded and may attach (id → details). When given, attachment fields only
    /// accept these and take the name and size from here, never from the browser.
    /// </param>
    public static FormResult Evaluate(FormDefinition form, IDictionary<string, object?> input,
        IDictionary<string, object?>? ambient = null, bool applyDefaults = false, Func<DateTime>? now = null,
        IReadOnlyDictionary<long, string>? departments = null, IReadOnlyDictionary<long, string>? locations = null,
        IReadOnlyDictionary<string, AttachmentInfo>? attachments = null, IReadOnlySet<DateTime>? closedDays = null,
        StudentDirectory? students = null)
    {
        var result = new FormResult();
        var submitterEmail = ambient is not null && ambient.TryGetValue("submitter", out var who) && who is Dictionary<string, object?> person
            ? Values.ToText(Workflows.ChainValue.Get(person, "email")).Trim() : "";
        var ambientScope = new Scope(ambient, now: now, closedDays: closedDays);
        var scope = new Scope(result.Values, ambientScope);
        var fields = form.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Key)).ToList();
        var byKey = new Dictionary<string, FieldDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields) byKey.TryAdd(f.Key, f);
        fields = byKey.Values.ToList();

        foreach (var f in fields) result.Fields[f.Key] = new FieldState();

        // Every field's value can depend on other fields through its formula and its visibility rule.
        var order = TopologicalOrder(fields, f => f.Key, f => Dependencies(f, byKey), out var cyclic);

        foreach (var field in order)
        {
            var state = result.Fields[field.Key];
            if (cyclic.Contains(field.Key))
            {
                state.Errors.Add("This field's formulas refer back to themselves (circular reference).");
                result.Values[field.Key] = null;
                continue;
            }

            state.Visible = EvaluateFlag(field.VisibleWhen, scope, true, state, "Show-when formula");
            if (FieldTypes.IsLayout(field.Type)) continue;

            object? value = null;
            if (state.Visible)
            {
                if (field.Type == FieldTypes.Calculated)
                {
                    value = Calculate(field.Formula, field.Decimals, scope, state.Errors);
                }
                else
                {
                    var hasInput = TryGetInput(input, field.Key, out var raw);
                    if (!hasInput && applyDefaults && !string.IsNullOrWhiteSpace(field.Default))
                    {
                        try { raw = Formula.Evaluate(field.Default, scope); }
                        catch (ExpressionException ex) { state.Errors.Add($"Default value formula: {ex.Message}"); }
                    }
                    value = field.Type switch
                    {
                        FieldTypes.Table => NormalizeTable(field, raw, scope, state),
                        FieldTypes.Department => NormalizeListed(raw, departments, "department", state.Errors),
                        FieldTypes.Location => NormalizeListed(raw, locations, "location", state.Errors),
                        FieldTypes.Attachments => NormalizeAttachments(field, raw, attachments, state.Errors),
                        FieldTypes.Student => NormalizeStudent(raw, students, state.Errors),
                        FieldTypes.ApprovalChain => Workflows.ChainValue.Normalize(raw, submitterEmail.Length > 0 ? submitterEmail : null, state.Errors),
                        _ => NormalizeScalar(field.Type, field.Options, raw, state.Errors),
                    };
                }
            }
            state.Value = value;
            result.Values[field.Key] = value;
        }

        // Second pass: required + validation, now that every value is known.
        foreach (var field in fields)
        {
            var state = result.Fields[field.Key];
            if (!state.Visible || FieldTypes.IsLayout(field.Type) || field.Type == FieldTypes.Calculated) continue;

            state.Required = field.Required || EvaluateFlag(field.RequiredWhen, scope, false, state, "Required-when formula");
            var blank = field.Type == FieldTypes.Checkbox ? state.Value is not true : Values.IsBlank(state.Value);

            if (blank)
            {
                // Don't stack "required" on top of a more specific message such as "must be a number".
                if (state.Required && state.Errors.Count == 0)
                    state.Errors.Add(field.Type == FieldTypes.Table ? "Add at least one row." : "This field is required.");
                if (field.Type != FieldTypes.Table) continue;
            }

            CheckBuiltIns(field, state);
            if (blank) continue;

            foreach (var rule in field.Rules ?? Enumerable.Empty<RuleDef>())
            {
                if (string.IsNullOrWhiteSpace(rule.Check)) continue;
                try
                {
                    if (!Values.ToBool(Formula.Evaluate(rule.Check, scope)))
                        state.Errors.Add(string.IsNullOrWhiteSpace(rule.Message) ? "This value is not allowed." : rule.Message);
                }
                catch (ExpressionException ex)
                {
                    state.Errors.Add($"Validation formula: {ex.Message}");
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> Dependencies(FieldDef field, Dictionary<string, FieldDef> byKey)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { field.VisibleWhen, field.Type == FieldTypes.Calculated ? field.Formula : null })
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            if (Formula.TryParse(source, out var formula, out _))
                refs.UnionWith(formula!.References);
        }
        if (field.Type == FieldTypes.Table)
        {
            // Row formulas may read fields outside the table.
            var columnKeys = new HashSet<string>((field.Columns ?? new()).Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var col in field.Columns ?? new())
            {
                if (col.Type != FieldTypes.Calculated || string.IsNullOrWhiteSpace(col.Formula)) continue;
                if (Formula.TryParse(col.Formula, out var formula, out _))
                    refs.UnionWith(formula!.References.Where(r => !columnKeys.Contains(r)));
            }
        }
        return refs.Where(byKey.ContainsKey);
    }

    private static bool TryGetInput(IDictionary<string, object?> input, string key, out object? value)
    {
        if (input.TryGetValue(key, out value)) return true;
        foreach (var kv in input)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
        }
        value = null;
        return false;
    }

    private static bool EvaluateFlag(string? source, Scope scope, bool whenEmpty, FieldState state, string what)
    {
        if (string.IsNullOrWhiteSpace(source)) return whenEmpty;
        try
        {
            return Values.ToBool(Formula.Evaluate(source, scope));
        }
        catch (ExpressionException ex)
        {
            state.Errors.Add($"{what}: {ex.Message}");
            return true; // fail visible so a broken rule never hides a field silently
        }
    }

    private static object? Calculate(string? source, int? decimals, Scope scope, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        try
        {
            var value = Formula.Evaluate(source, scope);
            if (value is decimal d && decimals is int places and >= 0 and <= 10)
                value = Math.Round(d, places, MidpointRounding.AwayFromZero);
            return value;
        }
        catch (ExpressionException ex)
        {
            errors.Add($"Formula: {ex.Message}");
            return null;
        }
    }

    private static object? NormalizeScalar(string type, List<string>? options, object? raw, List<string> errors)
    {
        raw = Values.Normalize(raw);
        switch (type)
        {
            case FieldTypes.Checkbox:
                return Values.ToBool(raw);

            case FieldTypes.Number:
            case FieldTypes.Currency:
                if (Values.IsBlank(raw)) return null;
                if (raw is not bool && raw is not DateTime && Values.TryToNumber(raw, out var number)) return number;
                errors.Add("Enter a number.");
                return null;

            case FieldTypes.Date:
                if (Values.IsBlank(raw)) return null;
                if (Values.TryToDate(raw, out var date)) return date.Date;
                errors.Add("Enter a valid date.");
                return null;

            case FieldTypes.MultiSelect:
            {
                var chosen = raw switch
                {
                    List<object?> list => Values.Flatten(list).Select(Values.ToText),
                    null => Enumerable.Empty<string>(),
                    _ => new[] { Values.ToText(raw) },
                };
                var allowed = options ?? new List<string>();
                // Keep the definition's option order and drop anything not offered.
                var set = new HashSet<string>(chosen.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);
                return allowed.Where(set.Contains).Cast<object?>().ToList();
            }

            case FieldTypes.Select:
            case FieldTypes.Radio:
            {
                if (Values.IsBlank(raw)) return null;
                var text = Values.ToText(raw).Trim();
                var match = (options ?? new List<string>()).FirstOrDefault(o => string.Equals(o.Trim(), text, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
                errors.Add("Choose one of the listed options.");
                return null;
            }

            case FieldTypes.Lookup:
            {
                if (raw is not Dictionary<string, object?> record) return null;
                var id = Member(record, "id");
                var name = Values.ToText(Member(record, "name"));
                if (Values.IsBlank(id) || name.Length == 0) return null;
                var clean = Values.NewDict();
                clean["id"] = Values.TryToNumber(id, out var numericId) && id is not bool ? numericId : Values.ToText(id);
                clean["name"] = Truncate(name, 300);
                var email = Values.ToText(Member(record, "email"));
                if (email.Length > 0) clean["email"] = Truncate(email, 300);
                return clean;
            }

            case FieldTypes.Email:
            {
                if (Values.IsBlank(raw)) return null;
                var text = Values.ToText(raw).Trim();
                if (text.Length > 320 || !EmailPattern.IsMatch(text)) errors.Add("Enter a valid email address.");
                return Truncate(text, 320);
            }

            case FieldTypes.Time:
            {
                if (Values.IsBlank(raw)) return null;
                var text = Values.ToText(raw).Trim();
                if (DateTime.TryParseExact(text, TimeFormats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time))
                    return time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);   // sorts and compares correctly as text
                errors.Add("Enter a time, for example 9:30 AM.");
                return null;
            }

            case FieldTypes.Phone:
            {
                if (Values.IsBlank(raw)) return null;
                var text = Values.ToText(raw).Trim();
                var digits = text.Count(char.IsDigit);
                if (text.Length > 40 || digits < 7 || digits > 15 || !PhonePattern.IsMatch(text)) errors.Add("Enter a valid phone number.");
                return Truncate(text, 40);
            }

            case FieldTypes.Signature:
            {
                if (Values.IsBlank(raw) || raw is List<object?> or Dictionary<string, object?>) return null;
                var text = Values.ToText(raw).Trim();
                if (text.Length < 2 || !text.Any(char.IsLetter)) errors.Add("Type your full name to sign.");
                return Truncate(text, 200);
            }

            default: // text, textarea
            {
                if (Values.IsBlank(raw)) return null;
                if (raw is List<object?> or Dictionary<string, object?>) { errors.Add("Enter text."); return null; }
                var text = Values.ToText(raw);
                if (type != FieldTypes.TextArea) text = text.Trim();
                if (text.Length > MaxTextLength) errors.Add($"Keep this under {MaxTextLength:N0} characters.");
                return Truncate(text, MaxTextLength);
            }
        }
    }

    private static readonly string[] TimeFormats = { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "h:mm tt", "h:mmtt", "h tt", "htt" };
    private static readonly System.Text.RegularExpressions.Regex PhonePattern = new(@"^\+?[0-9()\-. ]+( ?(x|ext\.?) ?[0-9]{1,6})?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public const int DefaultMaxFiles = 5;
    public const int AbsoluteMaxFiles = 20;

    /// <summary>Uploaded files: stored as a list of { id, name, size }.</summary>
    private static object? NormalizeAttachments(FieldDef field, object? raw, IReadOnlyDictionary<string, AttachmentInfo>? known, List<string> errors)
    {
        var files = new List<object?>();
        if (raw is not List<object?> list) return files;
        var limit = Math.Clamp(field.MaxFiles ?? DefaultMaxFiles, 1, AbsoluteMaxFiles);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var missing = false;
        foreach (var item in list.OfType<Dictionary<string, object?>>())
        {
            var id = Values.ToText(Member(item, "id")).Trim();
            if (id.Length is 0 or > 64 || !seen.Add(id)) continue;
            string name;
            decimal size;
            if (known is not null)
            {
                // The server's record is the authority; an ID that is not this person's own upload is dropped.
                if (!known.TryGetValue(id, out var info)) { missing = true; continue; }
                name = info.Name;
                size = info.Size;
            }
            else
            {
                name = Values.ToText(Member(item, "name")).Trim();
                if (name.Length == 0) continue;
                Values.TryToNumber(Member(item, "size"), out size);
            }
            var file = Values.NewDict();
            file["id"] = id;
            file["name"] = Truncate(name, 200);
            file["size"] = size;
            files.Add(file);
        }
        if (missing) errors.Add("One of the files could not be found. Remove it and upload it again.");
        if (files.Count > limit) errors.Add($"No more than {limit} file{(limit == 1 ? "" : "s")} can be added here.");
        return files.Take(AbsoluteMaxFiles).ToList();
    }

    /// <summary>The ID a student answer refers to, or "" — used by the server to look the student up before evaluating.</summary>
    public static string StudentIdOf(object? raw) =>
        Values.Normalize(raw) is Dictionary<string, object?> record ? Values.ToText(Member(record, "id")).Trim() : "";

    /// <summary>
    /// A student picked from the roster: stored as { id, name, first, last, school, grade, academy }.
    /// Everything but the ID comes from the server's roster, and only for people allowed to use it.
    /// </summary>
    private static object? NormalizeStudent(object? raw, StudentDirectory? students, List<string> errors)
    {
        var id = StudentIdOf(raw);
        if (id.Length == 0) return null;
        StudentInfo info;
        if (students is null)
        {
            // No roster to check against (the builder's preview, tests): keep what was given.
            var given = (Dictionary<string, object?>)Values.Normalize(raw)!;
            string Part(string key) => Truncate(Values.ToText(Member(given, key)).Trim(), 200);
            info = new StudentInfo(Truncate(id, 40), Part("first"), Part("last"), Part("school"), Part("grade"), Part("academy"));
        }
        else if (!students.Allowed)
        {
            errors.Add("You do not have access to student information. Ask an administrator.");
            return null;
        }
        else if (!students.Known.TryGetValue(id, out info!))
        {
            errors.Add("That student was not found. Search again.");
            return null;
        }
        var clean = Values.NewDict();
        clean["id"] = info.Id;
        clean["name"] = info.Name;
        clean["first"] = info.FirstName;
        clean["last"] = info.LastName;
        clean["school"] = info.School;
        clean["grade"] = info.Grade;
        clean["academy"] = info.Academy;
        return clean;
    }

    /// <summary>A choice from a list the admins maintain (departments, locations): stored as { id, name }.</summary>
    private static object? NormalizeListed(object? raw, IReadOnlyDictionary<long, string>? departments, string noun, List<string> errors)
    {
        if (raw is not Dictionary<string, object?> record) return null;
        var rawId = Member(record, "id");
        if (Values.IsBlank(rawId)) return null;
        if (rawId is bool || !Values.TryToNumber(rawId, out var number) || number != Math.Floor(number) || number <= 0 || number > int.MaxValue)
        {
            errors.Add($"Choose a {noun} from the list.");
            return null;
        }
        var id = (long)number;
        var name = Values.ToText(Member(record, "name")).Trim();
        if (departments is not null)
        {
            // The server's list is the authority: ignore whatever name the browser sent.
            if (!departments.TryGetValue(id, out var known))
            {
                errors.Add($"That {noun} is no longer available. Choose another.");
                return null;
            }
            name = known;
        }
        else if (name.Length == 0)
        {
            return null;
        }
        var clean = Values.NewDict();
        clean["id"] = (decimal)id;
        clean["name"] = Truncate(name, 200);
        return clean;
    }

    private static object? Member(Dictionary<string, object?> record, string key)
    {
        foreach (var kv in record)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static object? NormalizeTable(FieldDef field, object? raw, Scope parent, FieldState state)
    {
        var columns = (field.Columns ?? new List<ColumnDef>()).Where(c => !string.IsNullOrWhiteSpace(c.Key)).ToList();
        var rows = new List<object?>();
        if (raw is not List<object?> rawRows) return rows;

        var columnKeys = new HashSet<string>(columns.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);
        var order = TopologicalOrder(columns, c => c.Key, c =>
        {
            if (c.Type != FieldTypes.Calculated || !Formula.TryParse(c.Formula, out var f, out _)) return Enumerable.Empty<string>();
            return f!.References.Where(columnKeys.Contains);
        }, out var cyclic);

        var cellErrors = new Dictionary<string, string>();
        if (rawRows.Count > MaxTableRows) state.Errors.Add($"A table can have at most {MaxTableRows} rows.");

        foreach (var rawRow in rawRows.Take(MaxTableRows))
        {
            var source = rawRow as Dictionary<string, object?> ?? Values.NewDict();
            var row = Values.NewDict();
            var rowScope = new Scope(row, parent);
            var index = rows.Count;

            foreach (var col in order)
            {
                var errors = new List<string>();
                object? value;
                if (cyclic.Contains(col.Key))
                {
                    errors.Add("Circular formula.");
                    value = null;
                }
                else if (col.Type == FieldTypes.Calculated)
                {
                    value = Calculate(col.Formula, col.Decimals, rowScope, errors);
                }
                else
                {
                    value = NormalizeScalar(col.Type, col.Options, Member(source, col.Key), errors);
                    var blank = col.Type == FieldTypes.Checkbox ? value is not true : Values.IsBlank(value);
                    if (col.Required && blank && errors.Count == 0) errors.Add("Required.");
                }
                row[col.Key] = value;
                if (errors.Count > 0) cellErrors[$"{index}.{col.Key}"] = errors[0];
            }

            // Re-emit in the definition's column order for stable storage and display.
            var ordered = Values.NewDict();
            foreach (var col in columns) ordered[col.Key] = row.GetValueOrDefault(col.Key);
            rows.Add(ordered);
        }

        if (cellErrors.Count > 0) state.CellErrors = cellErrors;
        return rows;
    }

    private static void CheckBuiltIns(FieldDef field, FieldState state)
    {
        if (FieldTypes.IsNumeric(field.Type) && state.Value is decimal number)
        {
            if (field.Min is decimal min && number < min) state.Errors.Add($"Must be at least {Values.FormatNumber(min)}.");
            if (field.Max is decimal max && number > max) state.Errors.Add($"Must be at most {Values.FormatNumber(max)}.");
        }
        if (field.Type == FieldTypes.Table)
        {
            var count = (state.Value as List<object?>)?.Count ?? 0;
            if (field.MinRows is int minRows && count < minRows && (count > 0 || state.Required) && state.Errors.Count == 0)
                state.Errors.Add($"Add at least {minRows} row{(minRows == 1 ? "" : "s")}.");
            if (field.MaxRows is int maxRows && count > maxRows)
                state.Errors.Add($"No more than {maxRows} row{(maxRows == 1 ? "" : "s")} allowed.");
        }
    }

    /// <summary>
    /// Orders items so each comes after the items it depends on. Items that are part of (or depend on)
    /// a cycle are returned at the end and reported in <paramref name="cyclic"/>.
    /// </summary>
    public static List<T> TopologicalOrder<T>(IReadOnlyList<T> items, Func<T, string> key,
        Func<T, IEnumerable<string>> dependencies, out HashSet<string> cyclic)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var deps = items.ToDictionary(key, i => new HashSet<string>(dependencies(i), comparer), comparer);
        var ordered = new List<T>();
        var done = new HashSet<string>(comparer);
        var remaining = items.ToList();

        while (remaining.Count > 0)
        {
            // Stable: always take ready items in their original order.
            var ready = remaining.Where(i => deps[key(i)].All(d => done.Contains(d) || !deps.ContainsKey(d))).ToList();
            if (ready.Count == 0) break;
            foreach (var item in ready)
            {
                ordered.Add(item);
                done.Add(key(item));
            }
            remaining.RemoveAll(i => done.Contains(key(i)));
        }

        cyclic = new HashSet<string>(remaining.Select(key), comparer);
        ordered.AddRange(remaining);
        return ordered;
    }
}
