using SchoolFlow.Core;
using SchoolFlow.Core.Forms;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Tests;

public class AttachmentTests
{
    private static FormDefinition Form(int? max = 2, bool required = false) => new()
    {
        Fields =
        {
            new FieldDef { Key = "docs", Label = "Documents", Type = "attachments", MaxFiles = max, Required = required },
            new FieldDef { Key = "how_many", Label = "How many", Type = "calculated", Formula = "COUNT(docs) & ' file(s): ' & JOIN(docs.name, ', ')" },
        },
    };

    private static readonly Dictionary<string, AttachmentInfo> Mine = new()
    {
        ["aaa"] = new("aaa", "quote.pdf", 2048), ["bbb"] = new("bbb", "photo.png", 100), ["ccc"] = new("ccc", "extra.pdf", 5),
    };

    [Fact]
    public void Field_TakesNamesFromTheServer_AndRefusesFilesThatAreNotThePersons()
    {
        var input = Json.ToValues("""{ "docs": [ {"id":"aaa","name":"../../evil.exe","size":1}, {"id":"aaa"}, {"id":"someone-elses","name":"secret.pdf"}, "junk" ] }""");
        var result = FormEvaluator.Evaluate(Form(), input, attachments: Mine);
        var files = ((List<object?>)result.Values["docs"]!).Cast<Dictionary<string, object?>>().ToList();
        Assert.Single(files);
        Assert.Equal("quote.pdf", files[0]["name"]);
        Assert.Equal(2048m, files[0]["size"]);
        Assert.Contains("could not be found", result.Fields["docs"].Errors.Single());
        Assert.Equal("1 file(s): quote.pdf", result.Values["how_many"]);
        Assert.DoesNotContain("secret", Json.Serialize(result.Values));
    }

    [Fact]
    public void Field_EnforcesTheLimit_AndRequired()
    {
        var three = Json.ToValues("""{ "docs": [ {"id":"aaa"}, {"id":"bbb"}, {"id":"ccc"} ] }""");
        Assert.Contains("No more than 2 files", FormEvaluator.Evaluate(Form(), three, attachments: Mine).Fields["docs"].Errors.Single());
        Assert.Empty(FormEvaluator.Evaluate(Form(max: null), three, attachments: Mine).Fields["docs"].Errors); // default is 5

        var none = FormEvaluator.Evaluate(Form(required: true), Json.ToValues("""{ "docs": [] }"""), attachments: Mine);
        Assert.Equal("This field is required.", none.Fields["docs"].Errors.Single());
        Assert.Equal("0 file(s): ", none.Values["how_many"]);
    }

    [Fact]
    public void Files_AreNotCopiedFromAPreviousRequest()
    {
        var previous = Json.ToValues("""{ "docs": [ {"id":"aaa","name":"quote.pdf","size":1} ] }""");
        Assert.Empty(FormCopy.Extract(Form(), previous));
    }

    // ---- sending files to the ticket ----------------------------------------------------

    private sealed class Store : IAttachmentStore
    {
        public Task<TicketFile?> ReadAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(id == "gone" ? null : new TicketFile(id + ".pdf", "application/pdf", new byte[] { 1, 2, 3 }));
    }

    private sealed class RefusingFreshservice : IFreshserviceClient
    {
        private readonly SimulatedFreshserviceClient _inner = new();
        public bool IsLive => true;
        public Task<Dictionary<string, object?>> CreateTicketAsync(TicketCreate ticket, CancellationToken ct = default) => _inner.CreateTicketAsync(ticket, ct);
        public Task AttachFilesAsync(long ticketId, IReadOnlyList<TicketFile> files, CancellationToken ct = default) => throw new FreshserviceException("attachments: Invalid file type");
        public Task<Dictionary<string, object?>> UpdateTicketAsync(long ticketId, TicketUpdate update, CancellationToken ct = default) => _inner.UpdateTicketAsync(ticketId, update, ct);
        public Task AddNoteAsync(long ticketId, string bodyHtml, bool isPrivate, CancellationToken ct = default) => _inner.AddNoteAsync(ticketId, bodyHtml, isPrivate, ct);
        public Task<Dictionary<string, object?>?> GetTicketAsync(long ticketId, CancellationToken ct = default) => _inner.GetTicketAsync(ticketId, ct);
        public Task<Dictionary<string, object?>?> FindRequesterAsync(string email, CancellationToken ct = default) => _inner.FindRequesterAsync(email, ct);
        public Task<Dictionary<string, object?>> PlaceServiceRequestAsync(long itemDisplayId, string requesterEmail, int quantity, Dictionary<string, object?> customFields, CancellationToken ct = default) => _inner.PlaceServiceRequestAsync(itemDisplayId, requesterEmail, quantity, customFields, ct);
        public Task<IReadOnlyList<LookupItem>> LookupAsync(string source, string? query, string? userEmail, CancellationToken ct = default) => _inner.LookupAsync(source, query, userEmail, ct);
        public Task<string> TestConnectionAsync(CancellationToken ct = default) => _inner.TestConnectionAsync(ct);
    }

    private static FlowDefinition Flow(string attach) => new()
    {
        Nodes =
        {
            new NodeDef { Id = "s", Type = "start" },
            new NodeDef { Id = "t", Type = "fs_create_ticket", Label = "Ticket", Config = new() { ["subject"] = "Files: {{JOIN(attachments.name, ', ')}}", ["attachFiles"] = attach } },
            new NodeDef { Id = "e", Type = "end", Label = "Done" },
        },
        Edges = { new EdgeDef { From = "s", To = "t" }, new EdgeDef { From = "t", To = "e" } },
    };

    private static RunState State(params string[] ids) => new()
    {
        Context =
        {
            ["submitter"] = new Dictionary<string, object?> { ["email"] = "t@school.org" },
            ["attachments"] = ids.Select(id => (object?)new Dictionary<string, object?> { ["id"] = id, ["name"] = id + ".pdf", ["size"] = 3m }).ToList(),
        },
    };

    [Fact]
    public async Task Ticket_GetsTheFiles_AndAMissingOneIsReported()
    {
        var fs = new SimulatedFreshserviceClient();
        var state = State("a", "gone", "b");
        await new WorkflowEngine(fs, files: new Store()).StartAsync(Flow("true"), state);

        Assert.Equal(RunStatus.Completed, state.Status);
        Assert.Equal("Files: a.pdf, gone.pdf, b.pdf", ((TicketCreate)fs.Calls[0].Payload).Subject);
        var (ticketId, files) = ((long, IReadOnlyList<TicketFile>))fs.Calls.Single(c => c.Action == "attach_files").Payload;
        Assert.Equal(90001, ticketId);
        Assert.Equal(new[] { "a.pdf", "b.pdf" }, files.Select(f => f.Name).ToArray());
        Assert.Contains(state.NewEvents, e => e.Kind == "warning" && e.Message.Contains("gone.pdf (file missing)"));
    }

    [Fact]
    public async Task IfFreshserviceRefusesTheFiles_TheTicketStands_AndTheRequestCarriesOn()
    {
        var state = State("a");
        await new WorkflowEngine(new RefusingFreshservice(), files: new Store()).StartAsync(Flow("true"), state);
        Assert.Equal(RunStatus.Completed, state.Status);
        Assert.Null(state.Error);
        var warning = state.NewEvents.Single(e => e.Kind == "warning").Message;
        Assert.Contains("Ticket #90001 was created, but the files could not be attached", warning);
        Assert.Contains("Invalid file type", warning);
        Assert.Contains("still available on this request", warning);
    }

    [Fact]
    public async Task NothingIsSent_WhenTheStepIsNotAskedTo_OrThereAreNoFiles()
    {
        var off = new SimulatedFreshserviceClient();
        await new WorkflowEngine(off, files: new Store()).StartAsync(Flow(""), State("a"));
        Assert.DoesNotContain(off.Calls, c => c.Action == "attach_files");

        var empty = new SimulatedFreshserviceClient();
        await new WorkflowEngine(empty, files: new Store()).StartAsync(Flow("true"), State());
        Assert.DoesNotContain(empty.Calls, c => c.Action == "attach_files");
    }

    [Theory]
    [InlineData("C:\\Users\\me\\Quote.pdf", "Quote.pdf")]
    [InlineData("../../etc/passwd.txt", "passwd.txt")]
    [InlineData("a<b>:c|d?.pdf", "abcd.pdf")]
    [InlineData("  spaced name .pdf ", "spaced name .pdf")]
    [InlineData("...", "")]
    [InlineData(null, "")]
    public void FileNames_AreReducedToASafeDisplayName(string? input, string expected) =>
        Assert.Equal(expected, AttachmentService.CleanName(input));

    [Fact]
    public void LongFileNames_KeepTheirExtension()
    {
        var cleaned = AttachmentService.CleanName(new string('a', 400) + ".docx");
        Assert.Equal(150, cleaned.Length);
        Assert.EndsWith(".docx", cleaned);
    }

    [Theory]
    [InlineData(".pdf", "%PDF-1.4", true)]
    [InlineData(".pdf", "PK\u0003\u0004", false)]
    [InlineData(".docx", "PK\u0003\u0004", true)]
    [InlineData(".jpg", "ÿØÿà", true)]
    [InlineData(".gif", "GIF89a", true)]
    [InlineData(".csv", "name,qty\n", true)]
    [InlineData(".csv", "MZ", false)]
    [InlineData(".exe", "MZ", false)]
    public void Content_MustMatchTheFileType(string extension, string start, bool expected) =>
        Assert.Equal(expected, AttachmentService.ContentMatches(extension, System.Text.Encoding.Latin1.GetBytes(start)));
}
