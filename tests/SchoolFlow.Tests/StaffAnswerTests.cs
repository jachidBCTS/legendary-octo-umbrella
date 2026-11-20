using SchoolFlow.Core;
using SchoolFlow.Core.Expressions;
using SchoolFlow.Core.Forms;
using SchoolFlow.Core.Workflows;
using Xunit;

namespace SchoolFlow.Tests;

/// <summary>A person picked in a Staff field, used where an address is expected (review of Nov 20, 2026).</summary>
public class StaffAnswerTests
{
    private static Dictionary<string, object?> Person(string email, string name, string title = "Teacher") =>
        new(StringComparer.OrdinalIgnoreCase) { ["email"] = email, ["name"] = name, ["title"] = title };

    [Fact]
    public void A_staff_answer_gives_its_email_in_people_settings_and_its_name_in_text()
    {
        var scope = new Scope(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["requester"] = Person("jane.doe@school.org", "Jane Doe"),
            ["helpers"] = new List<object?> { Person("a@school.org", "A"), Person("b@school.org", "B") },
        });
        Assert.Equal("jane.doe@school.org", Template.RenderAddresses("{{requester}}", scope));
        Assert.Equal("jane.doe@school.org, [IT]", Template.RenderAddresses("{{requester}}, [IT]", scope));
        Assert.Equal("a@school.org, b@school.org", Template.RenderAddresses("{{helpers}}", scope));
        Assert.Equal("Hello Jane Doe", Template.Render("Hello {{requester}}", scope));
    }

    [Fact]
    public void A_staff_answer_counts_as_the_same_person_after_a_title_change()
    {
        var field = new FieldDef { Key = "requester", Type = FieldTypes.Staff };
        Assert.True(ResubmitGuard.SameAnswer(field, Person("Jane.Doe@school.org", "Jane Doe", "Teacher"), Person("jane.doe@school.org", "Jane Doe", "Lead Teacher")));
        Assert.False(ResubmitGuard.SameAnswer(field, Person("jane.doe@school.org", "Jane Doe"), Person("john@school.org", "John")));
    }

    [Fact]
    public void A_staff_answer_is_not_copied_from_someone_elses_request()
    {
        var field = new FieldDef { Key = "requester", Label = "Requester", Type = FieldTypes.Staff };
        Assert.True(FormCopy.IsCopyable(field, fromSomeoneElse: false));
        Assert.False(FormCopy.IsCopyable(field, fromSomeoneElse: true));
    }

    [Fact]
    public void The_checker_reads_cc_item_category_and_task_field_limits()
    {
        var def = new WorkflowDefinition();
        def.Flow.Nodes.Add(new NodeDef { Id = "start", Type = NodeTypes.Start });
        var ticket = new NodeDef { Id = "t", Type = NodeTypes.CreateTicket, Label = "Ticket" };
        ticket.Config["subject"] = "Help";
        ticket.Config["cc"] = "{{nobody_here}}";
        ticket.Config["itemCategory"] = "{{also_missing}}";
        def.Flow.Nodes.Add(ticket);
        var task = new NodeDef { Id = "k", Type = NodeTypes.Task, Label = "Check" };
        task.Config["assignees"] = "someone@school.org";
        task.Fields = new List<FieldDef>
        {
            new() { Key = "count", Label = "Count", Type = FieldTypes.Number, Min = 10, Max = 5 },
            new() { Key = "stamp", Label = "Stamp", Type = FieldTypes.Text, ReadOnly = true },
        };
        def.Flow.Nodes.Add(task);
        var issues = DefinitionValidator.Validate(def);
        Assert.Contains(issues, i => i.Message.Contains("nobody_here"));
        Assert.Contains(issues, i => i.Message.Contains("also_missing"));
        Assert.Contains(issues, i => i.Severity == "error" && i.Message.Contains("“Count”: minimum is greater than maximum"));
        Assert.Contains(issues, i => i.Severity == "warning" && i.Message.Contains("“Stamp” is read only"));
    }
}
