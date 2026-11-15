using System.Net;
using System.Text;
using SchoolFlow.Core;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Core.Workflows;

namespace SchoolFlow.Tests;

public class DefinitionValidatorTests
{
    private static WorkflowDefinition Valid() => Json.ParseDefinition("""
    {
      "trigger": { "type": "form" },
      "form": { "fields": [
        { "key": "qty", "label": "Quantity", "type": "number" },
        { "key": "total", "label": "Total", "type": "calculated", "formula": "qty * 2" }
      ] },
      "flow": {
        "nodes": [
          { "id": "s", "type": "start" },
          { "id": "c", "type": "fs_create_ticket", "label": "Create", "config": { "subject": "Order {{total}}" } },
          { "id": "e", "type": "end" }
        ],
        "edges": [ { "from": "s", "to": "c", "port": "next" }, { "from": "c", "to": "e", "port": "next" } ]
      }
    }
    """);

    [Fact]
    public void ValidDefinition_HasNoIssues() => Assert.Empty(DefinitionValidator.Validate(Valid()));

    [Fact]
    public void FormProblems_AreReported()
    {
        var def = Valid();
        def.Form.Fields.Add(new FieldDef { Key = "qty", Label = "Dup", Type = "text" });
        def.Form.Fields.Add(new FieldDef { Key = "ticket", Label = "Reserved", Type = "text" });
        def.Form.Fields.Add(new FieldDef { Key = "9bad", Label = "Bad key", Type = "text" });
        def.Form.Fields.Add(new FieldDef { Key = "pick", Label = "Pick", Type = "select" });
        def.Form.Fields.Add(new FieldDef { Key = "calc", Label = "Calc", Type = "calculated", Formula = "qty +" });
        def.Form.Fields.Add(new FieldDef { Key = "typo", Label = "Typo", Type = "calculated", Formula = "qtty * 2" });
        def.Form.Fields.Add(new FieldDef { Key = "loop", Label = "Loop", Type = "calculated", Formula = "loop + 1" });
        def.Form.Fields.Add(new FieldDef { Key = "arity", Label = "Arity", Type = "calculated", Formula = "ROUND(1, 2, 3)" });
        def.Form.Fields.Add(new FieldDef { Key = "tbl", Label = "Tbl", Type = "table" });

        var issues = DefinitionValidator.Validate(def);
        string All(string sev) => string.Join("\n", issues.Where(i => i.Severity == sev).Select(i => i.Message));
        Assert.Contains("share the key 'qty'", All("error"));
        Assert.Contains("reserved name", All("error"));
        Assert.Contains("must start with a letter", All("error"));
        Assert.Contains("at least one option", All("error"));
        Assert.Contains("“Calc” formula", All("error"));
        Assert.Contains("circular", All("error"));
        Assert.Contains("ROUND() takes 1 to 2", All("error"));
        Assert.Contains("at least one column", All("error"));
        Assert.Contains("'qtty'", All("warning"));
    }

    [Fact]
    public void FlowProblems_AreReported()
    {
        var def = Valid();
        def.Flow.Nodes.Add(new NodeDef { Id = "orphan", Type = "fs_add_note", Label = "Orphan" });
        def.Flow.Nodes.Add(new NodeDef { Id = "cond", Type = "condition", Label = "Cond", Config = new() { ["expression"] = "total >" } });
        def.Flow.Edges.Add(new EdgeDef { From = "c", To = "cond", Port = "next" });       // second edge from same port
        def.Flow.Edges.Add(new EdgeDef { From = "cond", To = "gone", Port = "true" });    // dangling
        def.Flow.Edges.Add(new EdgeDef { From = "cond", To = "s", Port = "false" });      // back into start
        def.Flow.Nodes.Single(n => n.Id == "c").Config["subject"] = "Order {{total";

        var errors = string.Join("\n", DefinitionValidator.Validate(def).Where(i => i.IsError).Select(i => i.Message));
        Assert.Contains("two connections leaving the same output", errors);
        Assert.Contains("no longer exists", errors);
        Assert.Contains("back into the Start", errors);
        Assert.Contains("“Cond” condition", errors);
        Assert.Contains("missing its closing }}", errors);
        Assert.Contains("“Orphan”: note is empty", errors);
        Assert.Contains("is not connected to the flow", string.Join("\n", DefinitionValidator.Validate(def).Select(i => i.Message)));
    }

    [Fact]
    public void TicketDependentStep_WithoutTicket_IsAnError_ButFineForTicketTrigger()
    {
        var def = Valid();
        var create = def.Flow.Nodes.Single(n => n.Id == "c");
        create.Type = "fs_add_note";
        create.Config = new() { ["body"] = "hello" };
        Assert.Contains(DefinitionValidator.Validate(def), i => i.IsError && i.Message.Contains("needs a ticket"));

        def.Trigger.Type = "ticket";
        def.Trigger.Condition = "ticket.priority >= 3";
        Assert.DoesNotContain(DefinitionValidator.Validate(def), i => i.IsError);
    }

    [Fact]
    public void MissingStart_And_EmptyDefinition()
    {
        var issues = DefinitionValidator.Validate(Json.ParseDefinition("{}"));
        Assert.Contains(issues, i => i.IsError && i.Message.Contains("needs a Start"));
        Assert.NotNull(Json.ParseDefinition("{\"form\":null,\"flow\":{\"nodes\":[null]}}"));
    }
}

public class FreshserviceClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? Auth)> Requests { get; } = new();
        public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct), request.Headers.Authorization?.ToString()));
            var (status, body) = Responses.Dequeue();
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (status == HttpStatusCode.TooManyRequests) response.Headers.RetryAfter = new(TimeSpan.Zero);
            return response;
        }
    }

    private static (FreshserviceClient, FakeHandler) Client()
    {
        var handler = new FakeHandler();
        var http = new HttpClient(handler) { BaseAddress = FreshserviceClient.BaseAddressFor("myschool") };
        return (new FreshserviceClient(http, "KEY"), handler);
    }

    [Fact]
    public async Task CreateTicket_SendsExpectedPayload_AndParsesResult()
    {
        var (client, handler) = Client();
        handler.Responses.Enqueue((HttpStatusCode.Created, "{\"ticket\":{\"id\":77,\"status\":2,\"subject\":\"S\"}}"));
        var ticket = await client.CreateTicketAsync(new TicketCreate
        {
            Subject = "S", DescriptionHtml = "D", RequesterEmail = "a@b.org", Priority = 3, GroupId = 9,
            CustomFields = { ["cf_total"] = 12.5m, ["cf_days"] = new List<string> { "Mon" } },
        });

        Assert.Equal(77m, ticket["id"]);
        var request = handler.Requests.Single();
        Assert.Equal("/api/v2/tickets", request.Url);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("KEY:X")), request.Auth);
        Assert.Contains("\"email\":\"a@b.org\"", request.Body);
        Assert.Contains("\"priority\":3", request.Body);
        Assert.Contains("\"group_id\":9", request.Body);
        Assert.Contains("\"custom_fields\":{\"cf_total\":12.5,\"cf_days\":[\"Mon\"]}", request.Body);
        Assert.DoesNotContain("department_id", request.Body); // nulls are omitted
    }

    [Fact]
    public async Task ValidationError_IsReadable()
    {
        var (client, handler) = Client();
        handler.Responses.Enqueue((HttpStatusCode.BadRequest,
            "{\"description\":\"Validation failed\",\"errors\":[{\"field\":\"email\",\"message\":\"It should be a valid email\",\"code\":\"invalid_value\"}]}"));
        var ex = await Assert.ThrowsAsync<FreshserviceException>(() => client.CreateTicketAsync(new TicketCreate()));
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("email: It should be a valid email", ex.Message);
    }

    [Fact]
    public async Task RateLimit_IsRetried_ButPostIsNotRetriedOnServerError()
    {
        var (client, handler) = Client();
        handler.Responses.Enqueue((HttpStatusCode.TooManyRequests, ""));
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"ticket\":{\"id\":5}}"));
        Assert.Equal(5m, (await client.UpdateTicketAsync(5, new TicketUpdate { Status = 4 }))["id"]);
        Assert.Equal(2, handler.Requests.Count);

        handler.Responses.Enqueue((HttpStatusCode.BadGateway, "oops"));
        await Assert.ThrowsAsync<FreshserviceException>(() => client.CreateTicketAsync(new TicketCreate()));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task GetTicket_NotFound_IsNull_And_FindRequester_FallsBackToAgents()
    {
        var (client, handler) = Client();
        handler.Responses.Enqueue((HttpStatusCode.NotFound, "{}"));
        Assert.Null(await client.GetTicketAsync(1));

        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"requesters\":[]}"));
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"agents\":[{\"id\":8,\"first_name\":\"Ann\",\"last_name\":\"Lee\",\"email\":\"ann+x@b.org\"}]}"));
        var person = await client.FindRequesterAsync("ann+x@b.org");
        Assert.Equal("Ann Lee", person!["name"]);
        Assert.Equal(true, person["is_agent"]);
        Assert.Equal("/api/v2/requesters?email=ann%2Bx%40b.org", handler.Requests[1].Url);
    }

    [Fact]
    public async Task Lookup_Departments_PagesAndFilters()
    {
        var (client, handler) = Client();
        var page1 = "{\"departments\":[" + string.Join(",", Enumerable.Range(1, 100).Select(i => $"{{\"id\":{i},\"name\":\"Dept {i:000}\"}}")) + "]}";
        handler.Responses.Enqueue((HttpStatusCode.OK, page1));
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"departments\":[{\"id\":900,\"name\":\"Science\"}]}"));
        var items = await client.LookupAsync("departments", "sci", null);
        Assert.Equal(new LookupItem("900", "Science"), items.Single());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("myschool", "https://myschool.freshservice.com/")]
    [InlineData("https://help.myschool.org/", "https://help.myschool.org/")]
    [InlineData(" myschool.freshservice.com ", "https://myschool.freshservice.com/")]
    public void BaseAddress_IsNormalised(string input, string expected) =>
        Assert.Equal(expected, FreshserviceClient.BaseAddressFor(input).ToString());

    [Theory]
    [InlineData("http://myschool.freshservice.com")]
    [InlineData("myschool.freshservice.com/evil/path")]
    public void BaseAddress_RejectsUnsafe(string input) =>
        Assert.Throws<FreshserviceException>(() => FreshserviceClient.BaseAddressFor(input));
}
