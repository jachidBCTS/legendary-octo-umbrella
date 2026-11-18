using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SchoolFlow.Core;
using SchoolFlow.Core.Workflows;
using SchoolFlow.Web.Data;

namespace SchoolFlow.Web.Services;

/// <summary>Email settings. Email is on only when Mode is "Graph" and a sender mailbox and app credentials are present.</summary>
public sealed class EmailSettings
{
    public string Mode { get; }
    public string From { get; }
    public string BaseUrl { get; }
    public string TenantId { get; }
    public string ClientId { get; }
    public string ClientSecret { get; }

    public EmailSettings(IConfiguration config)
    {
        Mode = config["Email:Mode"] ?? "Off";
        From = (config["Email:From"] ?? "").Trim();
        BaseUrl = (config["Email:BaseUrl"] ?? "").Trim().TrimEnd('/');
        // By default the sign-in app registration is reused; Email:* values override it.
        TenantId = First(config["Email:TenantId"], config["AzureAd:TenantId"]);
        ClientId = First(config["Email:ClientId"], config["AzureAd:ClientId"]);
        ClientSecret = First(config["Email:ClientSecret"], config["AzureAd:ClientSecret"]);
    }

    private static string First(string? a, string? b) => !string.IsNullOrWhiteSpace(a) ? a.Trim() : (b ?? "").Trim();

    public bool IsEnabled => Mode.Equals("Graph", StringComparison.OrdinalIgnoreCase) && MissingSettings.Count == 0;

    public List<string> MissingSettings
    {
        get
        {
            var missing = new List<string>();
            if (From.Length == 0) missing.Add("Email:From");
            if (BaseUrl.Length == 0) missing.Add("Email:BaseUrl");
            if (TenantId.Length == 0) missing.Add("AzureAd:TenantId");
            if (ClientId.Length == 0) missing.Add("AzureAd:ClientId");
            if (ClientSecret.Length == 0) missing.Add("AzureAd:ClientSecret");
            return missing;
        }
    }
}

public sealed class EmailException : Exception
{
    /// <summary>True when trying again later could work (throttling, outage); false for configuration problems.</summary>
    public bool Transient { get; }
    public EmailException(string message, bool transient) : base(message) => Transient = transient;
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct);
}

/// <summary>Sends mail through Microsoft Graph as the configured mailbox, using the app's own credentials.</summary>
public sealed class GraphEmailSender : IEmailSender
{
    private readonly HttpClient _http;
    private readonly EmailSettings _settings;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTime _tokenExpiresUtc;

    public GraphEmailSender(HttpClient http, EmailSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTime.UtcNow < _tokenExpiresUtc) return _token;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(_settings.TenantId)}/oauth2/v2.0/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _settings.ClientId,
                    ["client_secret"] = _settings.ClientSecret,
                    ["scope"] = "https://graph.microsoft.com/.default",
                    ["grant_type"] = "client_credentials",
                }),
            };
            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new EmailException("Could not sign in to Microsoft 365 to send email: " + Describe(text, (int)response.StatusCode),
                    transient: (int)response.StatusCode >= 500);
            using var doc = JsonDocument.Parse(text);
            _token = doc.RootElement.GetProperty("access_token").GetString()
                     ?? throw new EmailException("Microsoft 365 did not return an access token.", false);
            var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3000;
            _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 120));
            return _token;
        }
        catch (JsonException)
        {
            throw new EmailException("Microsoft 365 returned an unreadable sign-in response.", true);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);
        var payload = new
        {
            message = new
            {
                subject,
                body = new { contentType = "HTML", content = htmlBody },
                toRecipients = new[] { new { emailAddress = new { address = to } } },
            },
            saveToSentItems = false,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(_settings.From)}/sendMail")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return;

        var text = await response.Content.ReadAsStringAsync(ct);
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.Unauthorized) _token = null; // force a fresh token next time
        var hint = response.StatusCode switch
        {
            HttpStatusCode.Forbidden => " The app registration needs the Microsoft Graph application permission Mail.Send (with admin consent), and any application access policy must include the sender mailbox.",
            HttpStatusCode.NotFound => $" Check that the mailbox '{_settings.From}' exists.",
            _ => "",
        };
        throw new EmailException($"Microsoft 365 refused the email ({status}): {Describe(text, status)}{hint}",
            transient: status is 429 or 401 || status >= 500);
    }

    private static string Describe(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String) return Clip(d.GetString()!);
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return Clip(m.GetString()!);
        }
        catch (JsonException)
        {
            // Not JSON; fall through.
        }
        return $"HTTP {status}";
    }

    private static string Clip(string s)
    {
        s = s.Split('\n')[0].Trim();
        return s.Length > 300 ? s[..300] : s;
    }
}

/// <summary>Creates notifications. They are added to the current database context and saved with whatever caused them.</summary>
public sealed class NotificationService
{
    private readonly AppDbContext _db;
    private readonly EmailSettings _email;
    private readonly IConfiguration _config;

    public NotificationService(AppDbContext db, EmailSettings email, IConfiguration config)
    {
        _db = db;
        _email = email;
        _config = config;
    }

    /// <summary>
    /// Queues a notification for each address. People who use Bergen Forms see it in the app and get a link;
    /// outside addresses (a parent, a vendor) only ever get the email, without a link they could not open.
    /// </summary>
    public async Task NotifyAsync(IEnumerable<string> emails, string title, string body, string link, CancellationToken ct)
    {
        var recipients = emails.Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Contains('@') && e.Length <= 320).Distinct().ToList();
        if (recipients.Count == 0) return;
        var known = await _db.Users.AsNoTracking().Where(u => recipients.Contains(u.Email))
            .Select(u => new { u.Email, u.EmailNotifications }).ToDictionaryAsync(u => u.Email, u => u.EmailNotifications, ct);

        foreach (var email in recipients)
        {
            var isUser = known.TryGetValue(email, out var wantsEmail);
            var sendEmail = _email.IsEnabled && (!isUser || wantsEmail);
            // Someone who has not signed in yet may still be staff, so they keep the link and will find
            // the notification waiting in the app. Only clearly outside addresses lose the link.
            var outside = !isUser && _email.IsEnabled && !IsStaffDomain(email);
            _db.Notifications.Add(new Notification
            {
                UserEmail = email,
                Title = title.Length > 300 ? title[..300] : title,
                Body = body.Length > 4000 ? body[..4000] : body,
                Link = outside ? "" : link,
                CreatedAt = DateTime.UtcNow,
                EmailStatus = sendEmail ? EmailStatus.Pending : EmailStatus.None,
            });
        }
    }

    /// <summary>Addresses on the same domain as the sender mailbox are treated as staff who can sign in.</summary>
    private bool IsStaffDomain(string email)
    {
        var at = _email.From.IndexOf('@');
        return at > 0 && email.EndsWith(_email.From[at..], StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<string>> AdminEmailsAsync(CancellationToken ct)
    {
        var admins = await _db.Users.AsNoTracking().Where(u => u.Role == Roles.Admin).Select(u => u.Email).ToListAsync(ct);
        admins.AddRange((_config.GetSection("Auth:Admins").Get<string[]>() ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim().ToLowerInvariant()));
        return admins.Distinct().ToList();
    }

    public static string BuildHtml(string title, string body, string? absoluteLink)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var paragraphs = string.Join("", body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => $"<p style=\"margin:0 0 12px\">{E(p.Trim()).Replace("\n", "<br>")}</p>"));
        var button = string.IsNullOrEmpty(absoluteLink) ? "" :
            $"<p style=\"margin:20px 0 0\"><a href=\"{E(absoluteLink)}\" style=\"background:#4f46e5;color:#ffffff;text-decoration:none;padding:10px 18px;border-radius:8px;font-weight:600;display:inline-block\">Open in Bergen Forms</a></p>";
        return "<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:15px;line-height:1.5;color:#1b1f33;max-width:560px\">" +
               $"<h2 style=\"font-size:18px;margin:0 0 12px\">{E(title)}</h2>{paragraphs}{button}" +
               "<p style=\"margin:24px 0 0;color:#6c728c;font-size:12.5px\">Sent by SchoolFlow. You can turn off these emails on the Notifications page.</p></div>";
    }
}

/// <summary>Background worker that delivers queued emails, retrying temporary failures with a growing delay.</summary>
public sealed class EmailOutboxWorker : BackgroundService
{
    public const int MaxAttempts = 5;
    private readonly IServiceScopeFactory _scopes;
    private readonly EmailSettings _settings;
    private readonly ILogger<EmailOutboxWorker> _log;

    public EmailOutboxWorker(IServiceScopeFactory scopes, EmailSettings settings, ILogger<EmailOutboxWorker> log)
    {
        _scopes = scopes;
        _settings = settings;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.IsEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await ProcessBatchAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IEmailSender>(), _settings.BaseUrl, DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Email outbox pass failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Sends up to 20 due emails. Returns how many were sent.</summary>
    public static async Task<int> ProcessBatchAsync(AppDbContext db, IEmailSender sender, string baseUrl, DateTime nowUtc, CancellationToken ct)
    {
        var due = await db.Notifications
            .Where(n => n.EmailStatus == EmailStatus.Pending && (n.EmailNextAttemptAt == null || n.EmailNextAttemptAt <= nowUtc))
            .OrderBy(n => n.Id).Take(20).ToListAsync(ct);
        var sent = 0;
        foreach (var n in due)
        {
            try
            {
                var link = n.Link.StartsWith('/') && baseUrl.Length > 0 ? baseUrl + n.Link : null;
                await sender.SendAsync(n.UserEmail, n.Title, NotificationService.BuildHtml(n.Title, n.Body, link), ct);
                n.EmailStatus = EmailStatus.Sent;
                n.EmailError = null;
                sent++;
            }
            catch (Exception ex) when (ex is EmailException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                n.EmailAttempts++;
                var message = ex is EmailException ? ex.Message : "Could not reach Microsoft 365: " + ex.Message;
                n.EmailError = message.Length > 1000 ? message[..1000] : message;
                var retry = ex is not EmailException { Transient: false } && n.EmailAttempts < MaxAttempts;
                if (retry) n.EmailNextAttemptAt = nowUtc.AddMinutes(Math.Pow(2, n.EmailAttempts)); // 2, 4, 8, 16 minutes
                else n.EmailStatus = EmailStatus.Failed;
            }
            await db.SaveChangesAsync(ct);
        }
        return sent;
    }
}

/// <summary>Chain templates and departments for the workflow engine, read from the database.</summary>
public sealed class ApprovalDirectory : IApprovalDirectory
{
    private readonly AppDbContext _db;
    public ApprovalDirectory(AppDbContext db) => _db = db;

    public async Task<List<ChainStep>?> GetChainAsync(long chainId, CancellationToken ct = default)
    {
        var json = await _db.ApprovalChains.AsNoTracking().Where(c => c.Id == chainId).Select(c => c.StepsJson).FirstOrDefaultAsync(ct);
        return json is null ? null : ChainValue.ToSteps(Json.ToValues("{\"steps\":" + json + "}"));
    }

    public async Task<DepartmentInfo?> GetDepartmentAsync(long departmentId, CancellationToken ct = default)
    {
        var d = await _db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == departmentId, ct);
        return d is null ? null : new DepartmentInfo(d.Id, d.Name, d.FinalApproverList, d.FinalRule);
    }
}
