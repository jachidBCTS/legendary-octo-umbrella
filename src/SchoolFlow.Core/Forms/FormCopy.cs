using SchoolFlow.Core.Expressions;

namespace SchoolFlow.Core.Forms;

/// <summary>
/// Support for "start from a previous request": decides which answers of an earlier request may be
/// offered to prefill a new one, and builds the short description shown in the picker.
/// </summary>
public static class FormCopy
{
    /// <summary>Calculated fields are recomputed, headings have no value, and builders can exclude fields.</summary>
    public static bool IsCopyable(FieldDef field) =>
        field.NoCopy != true && !string.IsNullOrWhiteSpace(field.Key)
        // Files belong to the request they were uploaded for, and a signature to the person who gave it.
        && !FieldTypes.IsLayout(field.Type) && field.Type is not (FieldTypes.Calculated or FieldTypes.Attachments or FieldTypes.Signature)
        // Student details are only for people cleared to see them; they are never offered for copying.
        && field.Type != FieldTypes.Student;

    /// <summary>
    /// The answers from <paramref name="previous"/> that the current form still has and allows copying.
    /// Only keys of copyable fields are returned, so nothing else stored with the old request
    /// (approval comments, ticket details, excluded fields) can leak through.
    /// </summary>
    public static Dictionary<string, object?> Extract(FormDefinition form, IDictionary<string, object?> previous)
    {
        var result = Values.NewDict();
        foreach (var field in form.Fields.Where(IsCopyable))
        {
            if (!TryGet(previous, field.Key, out var value) || Values.IsBlank(value)) continue;
            result[field.Key] = value;
        }
        return result;
    }

    /// <summary>A one-line description of a previous request, built from its first few short answers.</summary>
    public static string Summary(FormDefinition form, IDictionary<string, object?> previous, int maxParts = 3)
    {
        var parts = new List<string>();
        foreach (var field in form.Fields.Where(IsCopyable))
        {
            if (parts.Count >= maxParts) break;
            if (field.Type is FieldTypes.TextArea or FieldTypes.Table or FieldTypes.Checkbox or FieldTypes.MultiSelect or FieldTypes.ApprovalChain) continue;
            if (!TryGet(previous, field.Key, out var value) || Values.IsBlank(value)) continue;
            var text = field.Type == FieldTypes.Date && Values.TryToDate(value, out var date)
                ? date.ToString("MMM d, yyyy", Values.DisplayCulture)
                : Values.ToText(value).Trim();
            if (text.Length == 0) continue;
            parts.Add(text.Length > 60 ? text[..60] + "…" : text);
        }
        return string.Join(" · ", parts);
    }

    /// <summary>All copyable answers as plain text, for searching the picker.</summary>
    public static string SearchText(FormDefinition form, IDictionary<string, object?> previous) =>
        string.Join(' ', Extract(form, previous).Values.Select(Flatten));

    private static string Flatten(object? value) => value switch
    {
        List<object?> list => string.Join(' ', list.Select(Flatten)),
        Dictionary<string, object?> record => string.Join(' ', record.Values.Select(Flatten)),
        _ => Values.ToText(value),
    };

    private static bool TryGet(IDictionary<string, object?> values, string key, out object? value)
    {
        if (values.TryGetValue(key, out value)) return true;
        foreach (var kv in values)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { value = kv.Value; return true; }
        }
        value = null;
        return false;
    }
}
