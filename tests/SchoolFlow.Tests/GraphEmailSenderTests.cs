using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using SchoolFlow.Web.Services;

namespace SchoolFlow.Tests;

public class GraphEmailSenderTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(string Url, string Body, string? Auth)> Requests { get; } = new();
        public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct), request.Headers.Authorization?.ToString()));
            var (status, body) = Responses.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static EmailSettings Settings(params (string k, string? v)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["Email:Mode"] = "Graph", ["Email:From"] = "no-reply@school.org", ["Email:BaseUrl"] = "https://flow.school.org/",
            ["AzureAd:TenantId"] = "tenant-1", ["AzureAd:ClientId"] = "client-1", ["AzureAd:ClientSecret"] = "secret-1",
        };
        foreach (var (k, v) in overrides) values[k] = v;
        return new EmailSettings(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [Fact]
    public async Task Sends_ThroughGraph_AndReusesTheToken()
    {
        var handler = new FakeHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"access_token\":\"TOKEN\",\"expires_in\":3600}"));
        handler.Responses.Enqueue((HttpStatusCode.Accepted, ""));
        handler.Responses.Enqueue((HttpStatusCode.Accepted, ""));
        var sender = new GraphEmailSender(new HttpClient(handler), Settings());

        await sender.SendAsync("a@school.org", "Subject \"1\"", "<p>Hi</p>", default);
        await sender.SendAsync("b@school.org", "Subject 2", "<p>Hi</p>", default);

        Assert.Equal(3, handler.Requests.Count); // one token request, two sends
        Assert.Equal("https://login.microsoftonline.com/tenant-1/oauth2/v2.0/token", handler.Requests[0].Url);
        Assert.Contains("grant_type=client_credentials", handler.Requests[0].Body);
        Assert.Contains("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default", handler.Requests[0].Body);
        Assert.Equal("https://graph.microsoft.com/v1.0/users/no-reply%40school.org/sendMail", handler.Requests[1].Url);
        Assert.Equal("Bearer TOKEN", handler.Requests[1].Auth);
        Assert.Contains("\"address\":\"a@school.org\"", handler.Requests[1].Body);
        Assert.Contains("\"contentType\":\"HTML\"", handler.Requests[1].Body);
        Assert.Contains("\"saveToSentItems\":false", handler.Requests[1].Body);
    }

    [Fact]
    public async Task MissingPermission_IsExplained_AndNotRetried()
    {
        var handler = new FakeHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"access_token\":\"T\",\"expires_in\":3600}"));
        handler.Responses.Enqueue((HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"ErrorAccessDenied\",\"message\":\"Access is denied. Check credentials and try again.\"}}"));
        var ex = await Assert.ThrowsAsync<EmailException>(() => new GraphEmailSender(new HttpClient(handler), Settings()).SendAsync("a@b.org", "s", "b", default));
        Assert.False(ex.Transient);
        Assert.Contains("Access is denied", ex.Message);
        Assert.Contains("Mail.Send", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Throttling_AndOutages_AreTransient(HttpStatusCode status)
    {
        var handler = new FakeHandler();
        handler.Responses.Enqueue((HttpStatusCode.OK, "{\"access_token\":\"T\",\"expires_in\":3600}"));
        handler.Responses.Enqueue((status, "busy"));
        var ex = await Assert.ThrowsAsync<EmailException>(() => new GraphEmailSender(new HttpClient(handler), Settings()).SendAsync("a@b.org", "s", "b", default));
        Assert.True(ex.Transient);
    }

    [Fact]
    public async Task BadSecret_IsReportedFromTheTokenStep()
    {
        var handler = new FakeHandler();
        handler.Responses.Enqueue((HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\",\"error_description\":\"AADSTS7000215: Invalid client secret provided.\\r\\nTrace ID: abc\"}"));
        var ex = await Assert.ThrowsAsync<EmailException>(() => new GraphEmailSender(new HttpClient(handler), Settings()).SendAsync("a@b.org", "s", "b", default));
        Assert.Contains("Invalid client secret", ex.Message);
        Assert.DoesNotContain("Trace ID", ex.Message);
        Assert.DoesNotContain("secret-1", ex.Message);
    }

    [Fact]
    public void Settings_ReportWhatIsMissing_AndStayOffByDefault()
    {
        Assert.True(Settings().IsEnabled);
        Assert.Equal("https://flow.school.org", Settings().BaseUrl);
        Assert.False(Settings(("Email:Mode", "Off")).IsEnabled);
        var incomplete = Settings(("Email:From", ""), ("AzureAd:ClientSecret", ""));
        Assert.False(incomplete.IsEnabled);
        Assert.Equal(new[] { "Email:From", "AzureAd:ClientSecret" }, incomplete.MissingSettings.ToArray());
        Assert.Equal("other", Settings(("Email:ClientId", "other")).ClientId); // Email:* overrides the sign-in app
        Assert.False(new EmailSettings(new ConfigurationBuilder().Build()).IsEnabled);
    }
}
