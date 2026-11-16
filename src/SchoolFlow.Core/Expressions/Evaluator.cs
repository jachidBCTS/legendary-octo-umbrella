namespace SchoolFlow.Core.Expressions;

/// <summary>Variable scope for evaluation. Lookups are case-insensitive and fall back to the parent scope.</summary>
public sealed class Scope
{
    private readonly IDictionary<string, object?> _values;
    private readonly Scope? _parent;

    public Scope(IDictionary<string, object?>? values = null, Scope? parent = null, Func<DateTime>? now = null,
        IReadOnlySet<DateTime>? closedDays = null)
    {
        _values = values ?? Values.NewDict();
        _parent = parent;
        Now = now ?? parent?.Now ?? (() => DateTime.Now);
        ClosedDays = closedDays ?? parent?.ClosedDays;
    }

    public Func<DateTime> Now { get; }

    /// <summary>Weekdays the school is closed (holidays, breaks). "Weekday"/"school day" date maths skips them.</summary>
    public IReadOnlySet<DateTime>? ClosedDays { get; }

    /// <summary>Monday to Friday, and not a day the school calendar marks as closed.</summary>
    public bool IsSchoolDay(DateTime date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && (ClosedDays is null || !ClosedDays.Contains(date.Date));

    public bool TryGet(string name, out object? value)
    {
        if (_values.TryGetValue(name, out value)) return true;
        // Dictionaries handed to us may be case-sensitive; fall back to a scan.
        foreach (var kv in _values)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }
        if (_parent is not null) return _parent.TryGet(name, out value);
        value = null;
        return false;
    }

    public void Set(string name, object? value) => _values[name] = value;
}

public static class Evaluator
{
    public static object? Evaluate(Node node, Scope scope)
    {
        try
        {
            return Values.Normalize(Eval(node, scope));
        }
        catch (OverflowException)
        {
            throw new ExpressionException("Result is too large to calculate");
        }
        catch (DivideByZeroException)
        {
            throw new ExpressionException("Cannot divide by zero");
        }
    }

    internal static object? Eval(Node node, Scope scope)
    {
        switch (node)
        {
            case LiteralNode lit:
                return lit.Value;

            case IdentifierNode id:
                return scope.TryGet(id.Name, out var v) ? Values.Normalize(v) : null;

            case MemberNode member:
                return Member(Eval(member.Target, scope), member.Name);

            case ListNode list:
                return list.Items.Select(i => Eval(i, scope)).ToList();

            case UnaryNode unary:
                var operand = Eval(unary.Operand, scope);
                return unary.Op == "!" ? !Values.ToBool(operand) : -Values.ToNumber(operand);

            case ConditionalNode cond:
                return Values.ToBool(Eval(cond.Test, scope)) ? Eval(cond.WhenTrue, scope) : Eval(cond.WhenFalse, scope);

            case BinaryNode bin:
                return Binary(bin, scope);

            case CallNode call:
                return Functions.Invoke(call, scope);

            default:
                throw new ExpressionException("Unsupported formula element");
        }
    }

    private static object? Member(object? target, string name)
    {
        switch (target)
        {
            case null:
                return null;
            case Dictionary<string, object?> dict:
                if (dict.TryGetValue(name, out var v)) return Values.Normalize(v);
                foreach (var kv in dict)
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return Values.Normalize(kv.Value);
                return null;
            case List<object?> list:
                // table.column → list of that column's values across all rows
                return list.Select(item => Member(item, name)).ToList();
            case string s when s.Length == 0:
                return null;
            default:
                throw new ExpressionException($"Cannot read '.{name}' from a value that is not a record");
        }
    }

    private static object? Binary(BinaryNode bin, Scope scope)
    {
        // Short-circuit logic first.
        if (bin.Op == "&&") return Values.ToBool(Eval(bin.Left, scope)) && Values.ToBool(Eval(bin.Right, scope));
        if (bin.Op == "||") return Values.ToBool(Eval(bin.Left, scope)) || Values.ToBool(Eval(bin.Right, scope));

        var left = Values.Normalize(Eval(bin.Left, scope));
        var right = Values.Normalize(Eval(bin.Right, scope));

        switch (bin.Op)
        {
            case "==": return Values.AreEqual(left, right);
            case "!=": return !Values.AreEqual(left, right);
            case "<": return Values.Compare(left, right) is int lt && lt < 0;
            case ">": return Values.Compare(left, right) is int gt && gt > 0;
            case "<=": return Values.Compare(left, right) is int le && le <= 0;
            case ">=": return Values.Compare(left, right) is int ge && ge >= 0;
            case "&": return Values.ToText(left) + Values.ToText(right);
            case "+":
                // Text on either side that is not a number means "join", like most form tools.
                if ((left is string ls && !Values.TryToNumber(ls, out _)) || (right is string rs && !Values.TryToNumber(rs, out _)))
                    return Values.ToText(left) + Values.ToText(right);
                return Arithmetic(left, right, "add", (a, b) => a + b);
            case "-": return Arithmetic(left, right, "subtract", (a, b) => a - b);
            case "*": return Arithmetic(left, right, "multiply", (a, b) => a * b);
            case "/":
                return Arithmetic(left, right, "divide", (a, b) =>
                    b == 0 ? throw new ExpressionException("Cannot divide by zero") : a / b);
            case "%":
                return Arithmetic(left, right, "divide", (a, b) =>
                    b == 0 ? throw new ExpressionException("Cannot divide by zero") : a % b);
            case "^":
                return Arithmetic(left, right, "raise", Power);
            default:
                throw new ExpressionException($"Unknown operator '{bin.Op}'");
        }
    }

    private static decimal Arithmetic(object? left, object? right, string verb, Func<decimal, decimal, decimal> op)
    {
        if (left is DateTime || right is DateTime)
            throw new ExpressionException($"Cannot {verb} dates directly — use DATEADD or DATEDIFF");
        if (!Values.TryToNumber(left, out var a))
            throw new ExpressionException($"Cannot {verb}: '{Values.Describe(left)}' is not a number");
        if (!Values.TryToNumber(right, out var b))
            throw new ExpressionException($"Cannot {verb}: '{Values.Describe(right)}' is not a number");
        return op(a, b);
    }

    internal static decimal Power(decimal a, decimal b)
    {
        var result = Math.Pow((double)a, (double)b);
        if (double.IsNaN(result) || double.IsInfinity(result))
            throw new ExpressionException("Result of ^ is not a real number");
        return (decimal)result;
    }
}
