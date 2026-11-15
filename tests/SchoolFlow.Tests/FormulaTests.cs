using SchoolFlow.Core.Expressions;

namespace SchoolFlow.Tests;

public class FormulaTests
{
    private static readonly DateTime FixedNow = new(2026, 11, 16, 9, 30, 0); // a Monday

    private static object? Eval(string source, Dictionary<string, object?>? vars = null) =>
        Formula.Evaluate(source, new Scope(vars, now: () => FixedNow));

    [Theory]
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("10 / 4", "2.5")]
    [InlineData("10 % 4", "2")]
    [InlineData("2 ^ 3 ^ 2", "512")]
    [InlineData("-2 ^ 2", "-4")]
    [InlineData("0.1 + 0.2", "0.3")]
    [InlineData("ROUND(2.345, 2)", "2.35")]
    [InlineData("ROUND(2.5)", "3")]
    [InlineData("ROUND(1234, -2)", "1200")]
    [InlineData("ROUNDUP(2.01, 1)", "2.1")]
    [InlineData("ROUNDUP(-2.01, 1)", "-2.1")]
    [InlineData("ROUNDDOWN(2.99, 1)", "2.9")]
    [InlineData("SUM(1, 2, [3, 4])", "10")]
    [InlineData("AVG(2, 4)", "3")]
    [InlineData("MIN(3, 1, 2)", "1")]
    [InlineData("MAX([3, 9], 2)", "9")]
    [InlineData("ABS(-4)", "4")]
    [InlineData("MOD(7, 3)", "1")]
    [InlineData("PERCENT(1, 4)", "25")]
    [InlineData("PERCENT(1, 0)", "0")]
    [InlineData("\"5\" + 2", "7")]
    [InlineData("\"a\" + 2", "a2")]
    [InlineData("\"a\" & \"b\" & 1", "ab1")]
    [InlineData("IF(3 > 2, \"yes\", \"no\")", "yes")]
    [InlineData("3 > 2 ? 'y' : 'n'", "y")]
    [InlineData("IFS(1 > 2, 'a', 2 > 1, 'b', 'c')", "b")]
    [InlineData("IFS(1 > 2, 'a', 'c')", "c")]
    [InlineData("SWITCH('B', 'a', 1, 'b', 2, 0)", "2")]
    [InlineData("SWITCH('z', 'a', 1, 0)", "0")]
    [InlineData("true and not false", "true")]
    [InlineData("AND(true, 1 = 1)", "true")]
    [InlineData("OR(false, 1 = 2)", "false")]
    [InlineData("1 = 1 && 2 <> 3 || false", "true")]
    [InlineData("!true", "false")]
    [InlineData("'Abc' == 'aBC'", "true")]
    [InlineData("'10' == 10", "true")]
    [InlineData("'9' < '10'", "true")]
    [InlineData("'apple' < 'banana'", "true")]
    [InlineData("IN('b', 'a', 'b')", "true")]
    [InlineData("IN(3, [1, 2])", "false")]
    [InlineData("CONTAINS('Hello World', 'world')", "true")]
    [InlineData("CONTAINS(['a', 'b'], 'B')", "true")]
    [InlineData("CONTAINS(['a', 'b'], 'c')", "false")]
    [InlineData("LEN('abc')", "3")]
    [InlineData("UPPER('abc') & LOWER('DEF')", "ABCdef")]
    [InlineData("LEFT('abcdef', 2) & RIGHT('abcdef', 2) & MID('abcdef', 3, 2)", "abefcd")]
    [InlineData("LEFT('ab', 10)", "ab")]
    [InlineData("REPLACE('a-b-c', '-', '')", "abc")]
    [InlineData("MATCHES('123456', '^[0-9]{6}$')", "true")]
    [InlineData("MATCHES('12345', '^[0-9]{6}$')", "false")]
    [InlineData("JOIN(['a', '', 'b'], '; ')", "a; b")]
    [InlineData("TEXT(1234.5, 'C2')", "$1,234.50")]
    [InlineData("TEXT(DATE(2026, 3, 5), 'MMM d, yyyy')", "Mar 5, 2026")]
    [InlineData("COALESCE('', null, 'x')", "x")]
    [InlineData("ISBLANK('')", "true")]
    [InlineData("ISBLANK(0)", "false")]
    [InlineData("ISNUMBER('12.5')", "true")]
    [InlineData("ISNUMBER('abc')", "false")]
    [InlineData("COUNT([1, '', 2])", "2")]
    [InlineData("TODAY()", "2026-11-16")]
    [InlineData("NOW()", "2026-11-16 09:30")]
    [InlineData("DATEADD(TODAY(), 5)", "2026-11-21")]
    [InlineData("DATEADD(TODAY(), 5, 'weekdays')", "2026-11-23")]
    [InlineData("DATEADD('2026-01-31', 1, 'month')", "2026-02-28")]
    [InlineData("DATEDIFF('2026-11-16', '2026-11-20')", "4")]
    [InlineData("DATEDIFF('2026-11-20', '2026-11-16')", "-4")]
    [InlineData("DATEDIFF('2026-11-13', '2026-11-16', 'weekdays')", "1")]
    [InlineData("DATEDIFF('2026-11-14', '2026-11-16', 'weekdays')", "1")]
    [InlineData("DATEDIFF('2026-11-16', '2026-11-16', 'weekdays')", "0")]
    [InlineData("DATEDIFF('2026-11-16', '2026-11-13', 'weekdays')", "-1")]
    [InlineData("DATEDIFF('2026-01-31', '2026-03-01', 'months')", "1")]
    [InlineData("DATEDIFF('2010-06-15', '2026-06-14', 'years')", "15")]
    [InlineData("WEEKDAYS('2026-11-16', '2026-11-29')", "10")]
    [InlineData("WEEKDAYS('2026-11-14', '2026-11-15')", "0")]
    [InlineData("YEAR('2026-03-05') + MONTH('2026-03-05') + DAY('2026-03-05')", "2034")]
    [InlineData("WEEKDAY('2026-11-16')", "Monday")]
    [InlineData("SCHOOLYEAR()", "2026-2027")]
    [InlineData("SCHOOLYEAR('2026-03-01')", "2025-2026")]
    [InlineData("'2026-11-20' > TODAY()", "true")]
    [InlineData("'2026-11-16' == TODAY()", "true")]
    [InlineData("FIRST([4, 5]) + LAST([4, 5])", "9")]
    [InlineData("LEN(UNIQUE(['a', 'A', 'b', '']))", "2")]
    [InlineData("COUNTIF(['x', 'y', 'X'], 'x')", "2")]
    [InlineData("SUMIF(['a', 'b', 'a'], 'a', [1, 2, 3])", "4")]
    public void Evaluates(string source, string expected) =>
        Assert.Equal(expected, Values.ToText(Eval(source)));

    [Fact]
    public void Variables_AreCaseInsensitive_AndUnknownIsBlank()
    {
        var vars = new Dictionary<string, object?> { ["Quantity"] = 3m, ["price"] = "2.50" };
        Assert.Equal(7.5m, Eval("quantity * PRICE", vars));
        Assert.Equal(0m, Eval("missing * 2", vars));
        Assert.Equal(true, Eval("ISBLANK(missing)", vars));
    }

    [Fact]
    public void TableColumns_MapOverRows()
    {
        var rows = new List<object?>
        {
            new Dictionary<string, object?> { ["qty"] = 2m, ["total"] = 20m, ["kind"] = "Software" },
            new Dictionary<string, object?> { ["qty"] = 1m, ["total"] = 5.5m, ["kind"] = "Hardware" },
        };
        var vars = new Dictionary<string, object?> { ["items"] = rows };
        Assert.Equal(25.5m, Eval("SUM(items.total)", vars));
        Assert.Equal(2m, Eval("COUNT(items)", vars));
        Assert.Equal(20m, Eval("SUMIF(items.kind, 'software', items.total)", vars));
        Assert.Equal(true, Eval("CONTAINS(items.kind, 'Hardware')", vars));
    }

    [Fact]
    public void NestedRecords_Resolve()
    {
        var vars = new Dictionary<string, object?>
        {
            ["ticket"] = new Dictionary<string, object?> { ["id"] = 42L, ["Status"] = 5 },
        };
        Assert.Equal(42m, Eval("ticket.id", vars));
        Assert.Equal(true, Eval("ticket.status == 5", vars));
        Assert.Null(Eval("ticket.nothing.deeper", vars));
        Assert.Null(Eval("nope.id", vars));
    }

    [Fact]
    public void BlankDates_CompareAsFalse_NotError()
    {
        var vars = new Dictionary<string, object?> { ["end_date"] = null, ["start"] = new DateTime(2026, 1, 1) };
        Assert.Equal(false, Eval("end_date > start", vars));
        Assert.Equal(false, Eval("end_date <= start", vars));
        Assert.Null(Eval("DATEDIFF(start, end_date)", vars));
    }

    [Fact]
    public void ShortCircuit_AvoidsErrorsInUntakenBranch()
    {
        Assert.Equal(0m, Eval("IF(0 = 0, 0, 1 / 0)"));
        Assert.Equal(false, Eval("false && 1 / 0"));
        Assert.Equal(true, Eval("true || 1 / 0"));
    }

    [Theory]
    [InlineData("1 / 0", "divide by zero")]
    [InlineData("1 +", "ends unexpectedly")]
    [InlineData("(1 + 2", "Expected ')'")]
    [InlineData("NOPE(1)", "Unknown function")]
    [InlineData("ROUND()", "takes")]
    [InlineData("'abc' * 2", "not a number")]
    [InlineData("'abc", "closing quote")]
    [InlineData("1 2", "Unexpected")]
    [InlineData("a ? b", "Expected ':'")]
    [InlineData("#", "Unexpected character")]
    [InlineData("DATE(2026, 2, 30)", "not a real calendar date")]
    [InlineData("MATCHES('a', '(')", "not a valid regular expression")]
    [InlineData("TODAY() + 1", "DATEADD")]
    [InlineData("99999999999999999999 * 99999999999999999999", "too large")]
    [InlineData("and 1", "both sides")]
    public void ReportsFriendlyErrors(string source, string expectedFragment)
    {
        var ex = Assert.Throws<ExpressionException>(() => Eval(source));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeepNesting_IsRejected_NotStackOverflow()
    {
        var source = new string('(', 500) + "1" + new string(')', 500);
        Assert.Throws<ExpressionException>(() => Eval(source));
    }

    [Fact]
    public void References_ListRootNamesOnly()
    {
        var f = Formula.Parse("IF(items.total > limit, submitter.email, UPPER(name))");
        Assert.Equal(new[] { "items", "limit", "name", "submitter" }, f.References.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Template_RendersAndEncodes()
    {
        var scope = new Scope(new Dictionary<string, object?> { ["name"] = "<b>Ann</b>", ["n"] = 2m }, now: () => FixedNow);
        Assert.Equal("Hi <b>Ann</b>, total 6", Template.Render("Hi {{name}}, total {{ n * 3 }}", scope));
        Assert.Equal("Hi &lt;b&gt;Ann&lt;/b&gt;", Template.Render("Hi {{name}}", scope, System.Net.WebUtility.HtmlEncode));
        Assert.Equal(6m, Template.RenderValue("{{ n * 3 }}", scope));
        Assert.Equal("x6", Template.RenderValue("x{{ n * 3 }}", scope));
        Assert.Equal("plain", Template.RenderValue("plain", scope));
        Assert.Throws<ExpressionException>(() => Template.Render("{{ n ", scope));
        Assert.Contains("{{1/0}}", Assert.Throws<ExpressionException>(() => Template.Render("a {{1/0}}", scope)).Message);
    }
}
