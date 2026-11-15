using SchoolFlow.Core;
using SchoolFlow.Core.Forms;

namespace SchoolFlow.Tests;

public class FormEvaluatorTests
{
    private static readonly Func<DateTime> Now = () => new DateTime(2026, 11, 16, 9, 0, 0);

    private static FormDefinition PurchaseForm() => new()
    {
        Fields =
        {
            new FieldDef { Key = "kind", Label = "Kind", Type = "select", Options = new() { "Hardware", "Software" }, Required = true },
            new FieldDef { Key = "license_years", Label = "Years", Type = "number", VisibleWhen = "kind == 'Software'", Required = true, Min = 1, Max = 5 },
            new FieldDef
            {
                Key = "items", Label = "Items", Type = "table", Required = true, MaxRows = 3,
                Columns = new()
                {
                    new ColumnDef { Key = "name", Label = "Item", Type = "text", Required = true },
                    new ColumnDef { Key = "qty", Label = "Qty", Type = "number" },
                    new ColumnDef { Key = "price", Label = "Price", Type = "currency" },
                    new ColumnDef { Key = "line", Label = "Line", Type = "calculated", Formula = "qty * price * IF(kind == 'Software', license_years, 1)", Decimals = 2 },
                },
            },
            new FieldDef { Key = "subtotal", Label = "Subtotal", Type = "calculated", Formula = "SUM(items.line)" },
            new FieldDef { Key = "shipping", Label = "Shipping", Type = "calculated", Formula = "IF(subtotal > 100 || subtotal = 0, 0, 9.99)" },
            new FieldDef { Key = "total", Label = "Total", Type = "calculated", Formula = "subtotal + shipping", Decimals = 2 },
            new FieldDef { Key = "justification", Label = "Why", Type = "textarea", RequiredWhen = "total >= 500" },
            new FieldDef
            {
                Key = "needed_by", Label = "Needed by", Type = "date", Default = "DATEADD(TODAY(), 14)",
                Rules = new() { new RuleDef { Check = "needed_by >= TODAY()", Message = "Pick a date in the future." } },
            },
            new FieldDef { Key = "contact", Label = "Contact", Type = "email", Default = "submitter.email" },
        },
    };

    private static List<object?> Rows(params (string name, object? qty, object? price)[] rows) =>
        rows.Select(r => (object?)new Dictionary<string, object?> { ["name"] = r.name, ["qty"] = r.qty, ["price"] = r.price }).ToList();

    [Fact]
    public void CalculatesAcrossTableAndDependentFields_RegardlessOfDefinitionOrder()
    {
        var form = PurchaseForm();
        // Put "total" before the fields it depends on to prove ordering is by dependency.
        var total = form.Fields.Single(f => f.Key == "total");
        form.Fields.Remove(total);
        form.Fields.Insert(0, total);

        var result = FormEvaluator.Evaluate(form, new Dictionary<string, object?>
        {
            ["kind"] = "software",
            ["license_years"] = "3",
            ["items"] = Rows(("Adobe", 2m, "19.995"), ("Zoom", "1", 10m)),
            ["needed_by"] = "2026-12-01",
        }, now: Now);

        var rows = (List<object?>)result.Values["items"]!;
        Assert.Equal(119.97m, ((Dictionary<string, object?>)rows[0]!)["line"]);
        Assert.Equal(30m, ((Dictionary<string, object?>)rows[1]!)["line"]);
        Assert.Equal(149.97m, result.Values["subtotal"]);
        Assert.Equal(0m, result.Values["shipping"]);
        Assert.Equal(149.97m, result.Values["total"]);
        Assert.Equal("Software", result.Values["kind"]); // normalised to the option's casing
        Assert.True(result.IsValid, string.Join(" | ", result.Fields.SelectMany(f => f.Value.Errors)));
    }

    [Fact]
    public void HiddenFields_LoseTheirValue_AndAreNotRequired()
    {
        var result = FormEvaluator.Evaluate(PurchaseForm(), new Dictionary<string, object?>
        {
            ["kind"] = "Hardware",
            ["license_years"] = 4m, // stale value from before the user switched kind
            ["items"] = Rows(("Mouse", 2m, 10m)),
        }, now: Now);

        Assert.False(result.Fields["license_years"].Visible);
        Assert.Null(result.Values["license_years"]);
        Assert.Empty(result.Fields["license_years"].Errors);
        Assert.Equal(20m, result.Values["subtotal"]); // multiplier fell back to 1, not the stale 4
        Assert.Equal(29.99m, result.Values["total"]);
    }

    [Fact]
    public void RequiredWhen_AndBuiltInChecks()
    {
        var result = FormEvaluator.Evaluate(PurchaseForm(), new Dictionary<string, object?>
        {
            ["kind"] = "Software",
            ["license_years"] = 9m,
            ["items"] = Rows(("Big", 1m, 500m), ("", 1m, 1m)),
            ["needed_by"] = "2026-01-01",
            ["contact"] = "not-an-email",
        }, now: Now);

        Assert.Contains("at most 5", result.Fields["license_years"].Errors.Single());
        Assert.True(result.Fields["justification"].Required);
        Assert.Equal("This field is required.", result.Fields["justification"].Errors.Single());
        Assert.Equal("Pick a date in the future.", result.Fields["needed_by"].Errors.Single());
        Assert.Contains("valid email", result.Fields["contact"].Errors.Single());
        Assert.Equal("Required.", result.Fields["items"].CellErrors!["1.name"]);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Empty_Form_ReportsRequiredOnly_AndRulesSkipBlankValues()
    {
        var result = FormEvaluator.Evaluate(PurchaseForm(), new Dictionary<string, object?>(), now: Now);
        Assert.Equal("This field is required.", result.Fields["kind"].Errors.Single());
        Assert.Equal("Add at least one row.", result.Fields["items"].Errors.Single());
        Assert.Empty(result.Fields["needed_by"].Errors);
        Assert.Empty(result.Fields["justification"].Errors);
        Assert.Equal(0m, result.Values["total"]);
    }

    [Fact]
    public void Defaults_ApplyOnlyWhenAsked_AndOnlyToMissingKeys()
    {
        var ambient = new Dictionary<string, object?> { ["submitter"] = new Dictionary<string, object?> { ["email"] = "t@school.org" } };
        var initial = FormEvaluator.Evaluate(PurchaseForm(), new Dictionary<string, object?>(), ambient, applyDefaults: true, now: Now);
        Assert.Equal(new DateTime(2026, 11, 30), initial.Values["needed_by"]);
        Assert.Equal("t@school.org", initial.Values["contact"]);

        var cleared = FormEvaluator.Evaluate(PurchaseForm(), new Dictionary<string, object?> { ["contact"] = "" }, ambient, applyDefaults: true, now: Now);
        Assert.Null(cleared.Values["contact"]);

        var later = FormEvaluator.Evaluate(PurchaseForm(), new Dictionary<string, object?>(), ambient, now: Now);
        Assert.Null(later.Values["needed_by"]);
    }

    [Fact]
    public void BadInput_IsReportedNotThrown()
    {
        var result = FormEvaluator.Evaluate(PurchaseForm(), new Dictionary<string, object?>
        {
            ["kind"] = "Furniture",
            ["items"] = "not a list",
            ["needed_by"] = "soon",
        }, now: Now);
        Assert.Contains("listed options", result.Fields["kind"].Errors.Single());
        Assert.Contains("valid date", result.Fields["needed_by"].Errors.Single());
        Assert.Equal("Add at least one row.", result.Fields["items"].Errors.Single());
    }

    [Fact]
    public void TooManyRows_IsAnError()
    {
        var result = FormEvaluator.Evaluate(PurchaseForm(), new Dictionary<string, object?>
        {
            ["kind"] = "Hardware",
            ["items"] = Rows(("a", 1m, 1m), ("b", 1m, 1m), ("c", 1m, 1m), ("d", 1m, 1m)),
        }, now: Now);
        Assert.Contains("No more than 3 rows", result.Fields["items"].Errors.Single());
    }

    [Fact]
    public void CircularFormulas_AreFlagged_OthersStillWork()
    {
        var form = new FormDefinition
        {
            Fields =
            {
                new FieldDef { Key = "a", Label = "A", Type = "calculated", Formula = "b + 1" },
                new FieldDef { Key = "b", Label = "B", Type = "calculated", Formula = "a + 1" },
                new FieldDef { Key = "c", Label = "C", Type = "calculated", Formula = "2 * 21" },
                new FieldDef { Key = "d", Label = "D", Type = "text", VisibleWhen = "d == 'x'" },
            },
        };
        var result = FormEvaluator.Evaluate(form, new Dictionary<string, object?>());
        Assert.Contains("circular", result.Fields["a"].Errors.Single());
        Assert.Contains("circular", result.Fields["b"].Errors.Single());
        Assert.Contains("circular", result.Fields["d"].Errors.Single());
        Assert.Equal(42m, result.Values["c"]);
    }

    [Fact]
    public void FormulaErrors_SurfaceOnTheField()
    {
        var form = new FormDefinition
        {
            Fields =
            {
                new FieldDef { Key = "n", Label = "N", Type = "number" },
                new FieldDef { Key = "ratio", Label = "Ratio", Type = "calculated", Formula = "10 / n" },
                new FieldDef { Key = "broken", Label = "Broken", Type = "text", VisibleWhen = "1 +" },
            },
        };
        var result = FormEvaluator.Evaluate(form, new Dictionary<string, object?> { ["n"] = 0m });
        Assert.Contains("divide by zero", result.Fields["ratio"].Errors.Single());
        Assert.True(result.Fields["broken"].Visible);
        Assert.Contains("Show-when", result.Fields["broken"].Errors.Single());
    }

    [Fact]
    public void MultiSelect_Checkbox_Lookup_Normalise()
    {
        var form = new FormDefinition
        {
            Fields =
            {
                new FieldDef { Key = "days", Label = "Days", Type = "multiselect", Options = new() { "Mon", "Tue", "Wed" } },
                new FieldDef { Key = "agree", Label = "Agree", Type = "checkbox", Required = true },
                new FieldDef { Key = "dept", Label = "Dept", Type = "lookup", Source = "departments" },
                new FieldDef { Key = "count", Label = "Count", Type = "calculated", Formula = "LEN(days) & '/' & dept.id & '/' & dept.name" },
                new FieldDef { Key = "wed_note", Label = "Wed", Type = "text", VisibleWhen = "CONTAINS(days, 'Wed')" },
            },
        };
        var result = FormEvaluator.Evaluate(form, new Dictionary<string, object?>
        {
            ["days"] = new List<object?> { "wed", "Fri", "Mon" },
            ["agree"] = false,
            ["dept"] = new Dictionary<string, object?> { ["id"] = 104m, ["name"] = "Mathematics", ["evil"] = "x" },
        });
        Assert.Equal(new object?[] { "Mon", "Wed" }, (List<object?>)result.Values["days"]!);
        Assert.Equal("This field is required.", result.Fields["agree"].Errors.Single());
        Assert.Equal("2/104/Mathematics", result.Values["count"]);
        Assert.False(((Dictionary<string, object?>)result.Values["dept"]!).ContainsKey("evil"));
        Assert.True(result.Fields["wed_note"].Visible);
    }

    [Fact]
    public void Result_RoundTripsThroughJson()
    {
        var result = FormEvaluator.Evaluate(PurchaseForm(), new Dictionary<string, object?>
        {
            ["kind"] = "Hardware", ["items"] = Rows(("Mouse", 2m, 10.5m)), ["needed_by"] = "2026-12-01",
        }, now: Now);
        var json = Json.Serialize(result.Values);
        Assert.Contains("\"needed_by\":\"2026-12-01\"", json);
        var back = Json.ToValues(json);
        var again = FormEvaluator.Evaluate(PurchaseForm(), back, now: Now);
        Assert.Equal(result.Values["total"], again.Values["total"]);
        Assert.Equal(new DateTime(2026, 12, 1), again.Values["needed_by"]);
    }
}
