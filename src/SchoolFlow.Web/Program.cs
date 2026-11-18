using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using SchoolFlow.Core.Freshservice;
using SchoolFlow.Web.Data;
using SchoolFlow.Web.Services;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- Database ---------------------------------------------------------------------------
builder.Services.AddSchoolFlowDatabase(config, builder.Environment.ContentRootPath);

// ---- Sign-in cookie and form-token keys --------------------------------------------------
// Kept on disk so that an IIS app-pool recycle or a server restart does not sign everyone out or
// invalidate the forms they have open. (By default IIS app pools have nowhere to keep them.)
var keysPath = config["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(keysPath)) keysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
Directory.CreateDirectory(keysPath);
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("SchoolFlow")
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
// On Windows, encrypt the key files so they are only usable on this server.
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);

// ---- Sign-in ----------------------------------------------------------------------------
var authMode = config["Auth:Mode"] ?? "EntraId";
var devAuth = authMode.Equals("Dev", StringComparison.OrdinalIgnoreCase);
if (devAuth)
{
    // Dev sign-in lets anyone pick any identity. It must never be reachable on a real server.
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("Auth:Mode=Dev is only allowed when ASPNETCORE_ENVIRONMENT=Development. Configure AzureAd and set Auth:Mode=EntraId.");
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.LoginPath = "/Account/DevLogin";
            o.AccessDeniedPath = "/Account/Denied";
        });
}
else
{
    if (string.IsNullOrWhiteSpace(config["AzureAd:ClientId"]) || string.IsNullOrWhiteSpace(config["AzureAd:TenantId"]))
        throw new InvalidOperationException("Microsoft Entra ID sign-in is not configured. Set AzureAd:TenantId, AzureAd:ClientId and AzureAd:ClientSecret (see README).");
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(config.GetSection("AzureAd"));
    builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme,
        o => o.AccessDeniedPath = "/Account/Denied");
}

// API calls must get a status code, not a redirect to a sign-in or "no access" page.
builder.Services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o =>
{
    var toLogin = o.Events.OnRedirectToLogin;
    var toDenied = o.Events.OnRedirectToAccessDenied;
    o.Events.OnRedirectToLogin = context =>
    {
        if (!context.Request.Path.StartsWithSegments("/api")) return toLogin(context);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    o.Events.OnRedirectToAccessDenied = context =>
    {
        if (!context.Request.Path.StartsWithSegments("/api")) return toDenied(context);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy("Builder", p => p.RequireRole(Roles.Builder, Roles.Admin));
    options.AddPolicy("Admin", p => p.RequireRole(Roles.Admin));
});
builder.Services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, RoleClaimsTransformation>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<GroupDirectory>();
builder.Services.AddScoped<AccessService>();
// The student roster is read live from the district's SQL Server (Students:ConnectionString and
// Students:View); a development site without one uses a few made-up students.
var rosterConnection = config["Students:ConnectionString"];
if (!string.IsNullOrWhiteSpace(rosterConnection))
    builder.Services.AddSingleton<IStudentRoster>(new SqlServerStudentRoster(rosterConnection, config["Students:View"],
        SqlServerStudentRoster.Parts.ToDictionary(p => p, p => config[$"Students:Columns:{p}"])));
else if (builder.Environment.IsDevelopment())
    builder.Services.AddSingleton<IStudentRoster, SampleStudentRoster>();
else
    builder.Services.AddSingleton<IStudentRoster, NoStudentRoster>();
// The staff list (same SQL Server; Staff:ConnectionString defaults to the roster's) says where each
// person works, which decides the students they may see, and supplies staff email suggestions.
var staffConnection = config["Staff:ConnectionString"];
if (string.IsNullOrWhiteSpace(staffConnection)) staffConnection = rosterConnection;
if (!string.IsNullOrWhiteSpace(staffConnection))
    builder.Services.AddSingleton<IStaffDirectory>(new SqlServerStaffDirectory(staffConnection, config["Staff:View"],
        SqlServerStaffDirectory.Parts.ToDictionary(p => p.Part, p => config[$"Staff:Columns:{p.Part}"])));
else if (builder.Environment.IsDevelopment())
    builder.Services.AddSingleton<IStaffDirectory, SampleStaffDirectory>();
else
    builder.Services.AddSingleton<IStaffDirectory, NoStaffDirectory>();
builder.Services.AddScoped<StudentScopeService>();
builder.Services.AddSingleton<DomainPolicy>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<SchoolCalendar>();
builder.Services.AddHostedService<FollowUpWorker>();

// ---- GSA per diem rates (travel forms) --------------------------------------------------------
// With a key (Gsa:ApiKey) rates come from GSA; a development site without one uses example rates;
// a real site without one uses the standard rate kept under Admin → Rates, and says so.
var gsaKey = config["Gsa:ApiKey"];
if (!string.IsNullOrWhiteSpace(gsaKey))
{
    var gsaBase = config["Gsa:BaseUrl"];
    builder.Services.AddSingleton<IPerDiemSource>(_ => new GsaPerDiemClient(new HttpClient
    {
        BaseAddress = new Uri((string.IsNullOrWhiteSpace(gsaBase) ? "https://api.gsa.gov/travel/perdiem" : gsaBase.TrimEnd('/')) + "/"),
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "X-API-KEY", gsaKey.Trim() }, { "Accept", "application/json" } },
    }));
}
else if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IPerDiemSource, SimulatedPerDiemSource>();
}
builder.Services.AddScoped<PerDiemService>();

// ---- Freshservice -----------------------------------------------------------------------
var fsDomain = config["Freshservice:Domain"];
var fsKey = config["Freshservice:ApiKey"];
if (!string.IsNullOrWhiteSpace(fsDomain) && !string.IsNullOrWhiteSpace(fsKey))
{
    var baseAddress = FreshserviceClient.BaseAddressFor(fsDomain);
    builder.Services.AddSingleton<IFreshserviceClient>(_ =>
        new FreshserviceClient(new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(20) }, fsKey));
}
else
{
    builder.Services.AddSingleton<IFreshserviceClient, SimulatedFreshserviceClient>();
}

// ---- Notifications and email ------------------------------------------------------------
var emailSettings = new EmailSettings(config);
builder.Services.AddSingleton(emailSettings);
builder.Services.AddSingleton<IEmailSender>(_ => new GraphEmailSender(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, emailSettings));
builder.Services.AddScoped<NotificationService>();
builder.Services.AddHostedService<EmailOutboxWorker>();

// The Freshservice webhook is the one address anyone on the internet can call without signing in;
// cap how fast a single caller can hit it (this also slows guessing of the shared secret).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("webhook", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Every other /api call: a generous ceiling per signed-in person (or per address when signed out),
    // well above what a person filling in forms produces, to stop runaway scripts and scraping.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        !context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/api/webhooks")
            ? RateLimitPartition.GetNoLimiter("other")
            : RateLimitPartition.GetFixedWindowLimiter(
                context.User.Identity?.IsAuthenticated == true ? "user:" + context.User.Email() : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 1200, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// ---- Uploaded files ---------------------------------------------------------------------
var attachmentSettings = new AttachmentSettings(config, builder.Environment.ContentRootPath);
Directory.CreateDirectory(attachmentSettings.Folder); // fail at start-up, not on someone's first upload, if the folder cannot be made
builder.Services.AddSingleton(attachmentSettings);
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddHostedService<AttachmentCleanupWorker>();
builder.Services.AddScoped<RetentionService>();
builder.Services.AddHostedService<RetentionWorker>();

builder.Services.AddScoped<RunService>();
builder.Services.AddRazorPages(options =>
{
    // Workflow owners need not hold the Builder role: each page and API call checks ownership itself.
    options.Conventions.AuthorizeFolder("/Builder");
    options.Conventions.AuthorizeFolder("/Admin", "Admin");
    options.Conventions.AllowAnonymousToPage("/Account/DevLogin");
    options.Conventions.AllowAnonymousToPage("/Account/Denied");
    options.Conventions.AllowAnonymousToPage("/Account/SignOut");
    options.Conventions.AllowAnonymousToPage("/Error");
});
builder.Services.AddControllers();
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    DatabaseSetup.Migrate(db, scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SchoolFlow.Database"));
    SeedData.EnsureStarterWorkflows(db, sampleDepartments: app.Environment.IsDevelopment());
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "same-origin";
    headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'; form-action 'self' https://login.microsoftonline.com";
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
// Signed-out API calls get a plain 401 whatever the sign-in scheme, never a redirect to a login page.
// (The Freshservice webhook is the one anonymous API; it checks its own shared secret.)
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/api/webhooks")
        && context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});
// After sign-in is read, so the API limit is per person rather than per (often shared) school address.
app.UseRateLimiter();
app.UseAuthorization();
app.MapRazorPages();
app.MapControllers();

app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

app.Run();

public partial class Program
{
}
