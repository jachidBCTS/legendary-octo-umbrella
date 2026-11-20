using System.Collections.Concurrent;

namespace SchoolFlow.Core.Expressions;

/// <summary>A parsed formula. Use <see cref="Parse"/> (cached) then <see cref="Evaluate"/>.</summary>
public sealed class Formula
{
    private static readonly ConcurrentDictionary<string, Formula> Cache = new();
    private const int MaxCacheEntries = 5000;

    public string Source { get; }
    public Node Root { get; }

    /// <summary>Top-level variable names the formula reads (e.g. "items" for items.total).</summary>
    public IReadOnlySet<string> References { get; }

    private Formula(string source, Node root)
    {
        Source = source;
        Root = root;
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(root, refs);
        References = refs;
    }

    /// <summary>Parses a formula, checking syntax, function names and argument counts.</summary>
    public static Formula Parse(string source)
    {
        source = source?.Trim() ?? "";
        if (Cache.TryGetValue(source, out var cached)) return cached;
        var formula = new Formula(source, Parser.Parse(source));
        if (Cache.Count >= MaxCacheEntries) Cache.Clear();
        Cache[source] = formula;
        return formula;
    }

    public static bool TryParse(string? source, out Formula? formula, out string? error)
    {
        try
        {
            formula = Parse(source ?? "");
            error = null;
            return true;
        }
        catch (ExpressionException ex)
        {
            formula = null;
            error = ex.Message;
            return false;
        }
    }

    public object? Evaluate(Scope scope) => Evaluator.Evaluate(Root, scope);

    public static object? Evaluate(string source, Scope scope) => Parse(source).Evaluate(scope);

    private static void Collect(Node node, HashSet<string> refs)
    {
        switch (node)
        {
            case IdentifierNode id: refs.Add(id.Name); break;
            case MemberNode m: Collect(m.Target, refs); break;
            case UnaryNode u: Collect(u.Operand, refs); break;
            case BinaryNode b: Collect(b.Left, refs); Collect(b.Right, refs); break;
            case ConditionalNode c: Collect(c.Test, refs); Collect(c.WhenTrue, refs); Collect(c.WhenFalse, refs); break;
            case ListNode l: foreach (var i in l.Items) Collect(i, refs); break;
            case CallNode call:
                Functions.CheckArity(call.Name, call.Args.Count);
                foreach (var a in call.Args) Collect(a, refs);
                break;
        }
    }
}

/// <summary>Text with {{ formula }} placeholders, used for ticket subjects, descriptions, notes, etc.</summary>
public static class Template
{
    public static IEnumerable<string> GetExpressions(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var i = 0;
        while (true)
        {
            var open = text.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) yield break;
            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) throw new ExpressionException("A {{ placeholder is missing its closing }}");
            yield return text[(open + 2)..close];
            i = close + 2;
        }
    }

    /// <summary>Renders the template. <paramref name="encode"/> is applied to each substituted value (e.g. HTML-encoding).</summary>
    public static string Render(string? text, Scope scope, Func<string, string>? encode = null) => Render(text, scope, encode, Values.ToText);

    /// <summary>
    /// Renders a setting that names people (approvers, recipients, CC, a requester). A person picked in a
    /// Staff field is a record whose text is their name; here it gives their email address instead, so
    /// <c>{{requester}}</c> works the same as <c>{{requester.email}}</c>.
    /// </summary>
    public static string RenderAddresses(string? text, Scope scope) => Render(text, scope, null, Values.ToAddressText);

    private static string Render(string? text, Scope scope, Func<string, string>? encode, Func<object?, string> toText)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (true)
        {
            var open = text.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(text, i, text.Length - i); break; }
            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) throw new ExpressionException("A {{ placeholder is missing its closing }}");
            sb.Append(text, i, open - i);
            var source = text[(open + 2)..close];
            string value;
            try
            {
                value = toText(Formula.Evaluate(source, scope));
            }
            catch (ExpressionException ex)
            {
                throw new ExpressionException($"In {{{{{source.Trim()}}}}}: {ex.Message}");
            }
            sb.Append(encode is null ? value : encode(value));
            i = close + 2;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Evaluates a setting that may be a single {{formula}} (returns the typed value, e.g. a number),
    /// a mixed template (returns text), or plain text.
    /// </summary>
    public static object? RenderValue(string? text, Scope scope)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("{{", StringComparison.Ordinal) && trimmed.EndsWith("}}", StringComparison.Ordinal)
            && trimmed.IndexOf("}}", StringComparison.Ordinal) == trimmed.Length - 2)
        {
            var source = trimmed[2..^2];
            try
            {
                return Formula.Evaluate(source, scope);
            }
            catch (ExpressionException ex)
            {
                throw new ExpressionException($"In {{{{{source.Trim()}}}}}: {ex.Message}");
            }
        }
        return Render(text, scope);
    }
}
