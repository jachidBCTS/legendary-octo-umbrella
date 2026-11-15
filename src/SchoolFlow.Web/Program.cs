using Microsoft.AspNetCore.Authentication.Cookies;
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
var provider = config["Database:Provider"] ?? "Sqlite";
var connectionString = config["Database:ConnectionString"];
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Database:ConnectionString is required when Database:Provider is SqlServer.");
        options.UseSqlServer(connectionString);
    }
    else
    {
        options.UseSqlite(string.IsNullOrWhiteSpace(connectionString) ? "Data Source=schoolflow.db" : connectionString);
    }
});

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

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy("Builder", p => p.RequireRole(Roles.Builder, Roles.Admin));
    options.AddPolicy("Admin", p => p.RequireRole(Roles.Admin));
});
builder.Services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, RoleClaimsTransformation>();
builder.Services.AddMemoryCache();

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

builder.Services.AddScoped<RunService>();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Builder", "Builder");
    options.Conventions.AuthorizeFolder("/Admin", "Admin");
    options.Conventions.AllowAnonymousToPage("/Account/DevLogin");
    options.Conventions.AllowAnonymousToPage("/Account/Denied");
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
    db.Database.EnsureCreated();
    SeedData.EnsureStarterWorkflows(db);
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
app.UseAuthorization();
app.MapRazorPages();
app.MapControllers();

app.MapGet("/Account/SignOut", async (HttpContext http) =>
{
    await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignOutAsync(http, CookieAuthenticationDefaults.AuthenticationScheme);
    if (!devAuth)
        await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignOutAsync(http, OpenIdConnectDefaults.AuthenticationScheme,
            new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/" });
    else
        http.Response.Redirect("/Account/DevLogin");
}).AllowAnonymous();

app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

app.Run();

public partial class Program
{
}
