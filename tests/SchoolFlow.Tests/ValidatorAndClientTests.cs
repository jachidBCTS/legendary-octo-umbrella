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
    [Fact]
    public async Task Connection_test_says_the_address_is_wrong_when_the_api_is_not_there()
    {
        var (client, handler) = Client();
        handler.Responses.Enqueue((HttpStatusCode.NotFound, "<html>Not found</html>"));
        var ex = await Assert.ThrowsAsync<FreshserviceException>(() => client.TestConnectionAsync());
        Assert.Contains("Freshservice:Domain", ex.Message);
        Assert.Equal("/api/v2/tickets?per_page=1", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Connection_test_falls_back_to_agent_groups_and_never_fails_on_groups_alone()
    {
        var (client, handler) = Client();
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"tickets\":[]}"));
        handler.Responses.Enqueue((HttpStatusCode.NotFound, "{}"));
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"agent_groups\":[{\"id\":1,\"name\":\"Tech\"}]}"));
        var message = await client.TestConnectionAsync();
        Assert.Contains("1 agent group", message);
        Assert.StartsWith("/api/v2/agent_groups", handler.Requests[2].Url);

        var (second, again) = Client();
        again.Responses.Enqueue((HttpStatusCode.OK, "{\"tickets\":[]}"));
        again.Responses.Enqueue((HttpStatusCode.NotFound, "{}"));
        again.Responses.Enqueue((HttpStatusCode.NotFound, "{}"));
        Assert.Contains("tickets can be read", await second.TestConnectionAsync());
    }

    [Fact]
    public void Ticket_form_fields_list_api_names_and_nested_choices()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
        {"ticket_fields":[
          {"name":"category","label":"Category","field_type":"default_category","default_field":true,
           "choices":[{"id":1,"value":"Hardware","nested_options":[{"id":2,"value":"Computer","nested_options":[{"id":3,"value":"Laptop"}]}]},{"id":4,"value":"Software"}]},
          {"name":"cf_room_number","label":"Room #","field_type":"custom_text","default_field":false},
          {"name":"cf_building","label":"Location","field_type":"custom_dropdown","default_field":false,"choices":["Academy","Teterboro"]}
        ]}
        """);
        var fields = FreshserviceClient.ParseTicketFields(doc.RootElement);
        Assert.Equal(3, fields.Count);
        Assert.Equal(new[] { "Hardware", "Hardware > Computer", "Hardware > Computer > Laptop", "Software" }, fields[0].Choices);
        Assert.Equal("cf_room_number", fields[1].Name);
        Assert.False(fields[1].IsDefault);
        Assert.Equal(new[] { "Academy", "Teterboro" }, fields[2].Choices);
    }

    [Fact]
    public async Task Create_ticket_sends_cc_and_item_category()
    {
        var (client, handler) = Client();
        handler.Responses.Enqueue((HttpStatusCode.Created, "{\"ticket\":{\"id\":5}}"));
        await client.CreateTicketAsync(new TicketCreate { Subject = "s", DescriptionHtml = "d", RequesterEmail = "a@x.org", Category = "Hardware",
            ItemCategory = "Laptop", CcEmails = { "b@x.org", "c@x.org" } });
        var body = handler.Requests[0].Body!;
        Assert.Contains("\"cc_emails\":[\"b@x.org\",\"c@x.org\"]", body);
        Assert.Contains("\"item_category\":\"Laptop\"", body);
    }

    [Fact]
    public async Task Custom_fields_are_matched_to_the_ticket_form_by_name_or_label_and_choices_spelled_its_way()
    {
        var (client, handler) = Client();
        handler.FormFields = """
        {"ticket_fields":[
          {"name":"category","label":"Category","field_type":"default_category","default_field":true,"choices":[{"value":"Printer & Copier Supplies"},{"value":"Hardware","nested_options":[{"value":"Projector"}]}]},
          {"name":"cf_room_number","label":"Room Number","field_type":"custom_text","default_field":false},
          {"name":"cf_please_note_your_availability__free_periods","label":"Please Note Your Availability / Free Periods","field_type":"custom_text","default_field":false},
          {"name":"cf_location","label":"Location","field_type":"custom_lookup_bigint","default_field":false},
          {"name":"cf_building","label":"Building","field_type":"custom_dropdown","default_field":false,"choices":["540 Farview","Teterboro"]}
        ]}
        """;
        handler.Locations = "{\"locations\":[{\"id\":77,\"name\":\"540 Farview\"}]}";
        handler.Responses.Enqueue((HttpStatusCode.Created, "{\"ticket\":{\"id\":9}}"));
        var ticket = new TicketCreate
        {
            Subject = "s", DescriptionHtml = "d", RequesterEmail = "a@x.org", Category = "hardware", SubCategory = "projector",
            CustomFields =
            {
                ["Room Number"] = "214",                                              // by label
                ["cf_please_note_your_availability_free_periods"] = "Period 3",       // a near miss of the real name
                ["Location"] = "540 farview",                                         // a lookup: sent as the location's number
                ["cf_building"] = "teterboro",                                        // a dropdown: its own spelling
                ["cf_nonsense"] = "x",                                                // not on the form: left out
            },
        };
        await client.CreateTicketAsync(ticket);
        var body = handler.Requests.Single().Body!;
        Assert.Contains("\"cf_room_number\":\"214\"", body);
        Assert.Contains("\"cf_please_note_your_availability__free_periods\":\"Period 3\"", body);
        Assert.Contains("\"cf_location\":77", body);
        Assert.Contains("\"cf_building\":\"Teterboro\"", body);
        Assert.Contains("\"category\":\"Hardware\"", body);
        Assert.Contains("\"sub_category\":\"Projector\"", body);
        Assert.DoesNotContain("cf_nonsense", body);
        Assert.Contains(ticket.Notes, n => n.Contains("cf_nonsense"));
    }

    [Fact]
    public void A_category_that_is_not_a_choice_is_left_out_with_a_note()
    {
        var notes = new List<string>();
        var form = new[] { new TicketField("cf_building", "Building", "custom_dropdown", false, new[] { "Teterboro" }) };
        var result = FreshserviceClient.MatchCustomFields(form, new Dictionary<string, object?> { ["Building"] = "Mars" }, notes);
        Assert.Empty(result);
        Assert.Contains("not one of the choices", notes.Single());
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? Auth)> Requests { get; } = new();
        public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();
        /// <summary>What GET api/v2/ticket_form_fields answers (read before a ticket is created); not found when null.</summary>
        public string? FormFields { get; set; }
        /// <summary>What GET api/v2/locations answers.</summary>
        public string? Locations { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v2/ticket_form_fields")
                return new HttpResponseMessage(FormFields is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(FormFields ?? "{}", Encoding.UTF8, "application/json") };
            if (path == "/api/v2/locations")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.Query.Contains("page=1") ? Locations ?? "{\"locations\":[]}" : "{\"locations\":[]}", Encoding.UTF8, "application/json") };
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

    [Fact]
    public async Task AttachFiles_SendsAMultipartUpdate_WithOnePartPerFile()
    {
        var (client, handler) = Client();
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"ticket\":{\"id\":77}}"));
        await client.AttachFilesAsync(77, new[]
        {
            new TicketFile("quote \"final\".pdf", "application/pdf", "%PDF-1.7"u8.ToArray()),
            new TicketFile("photo.png", "image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
        });
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/v2/tickets/77", request.Url);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(request.Body!, "name=\"attachments\\[\\]\"|name=attachments\\[\\]").Count);
        Assert.Contains("quote final.pdf", request.Body);     // the quotes that would break the header are removed
        Assert.Contains("Content-Type: application/pdf", request.Body);
        Assert.Contains("Content-Type: image/png", request.Body);
        Assert.Contains("%PDF-1.7", request.Body);

        await client.AttachFilesAsync(77, Array.Empty<TicketFile>()); // nothing to send: no request at all
        Assert.Single(handler.Requests);

        handler.Responses.Enqueue((HttpStatusCode.BadRequest, "{\"errors\":[{\"field\":\"attachments\",\"message\":\"Invalid file type\"}]}"));
        var ex = await Assert.ThrowsAsync<FreshserviceException>(() => client.AttachFilesAsync(77, new[] { new TicketFile("a.pdf", "application/pdf", new byte[] { 1 }) }));
        Assert.Contains("attachments: Invalid file type", ex.Message);
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
