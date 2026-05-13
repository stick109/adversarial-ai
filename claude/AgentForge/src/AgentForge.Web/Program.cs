using AgentForge.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Cookie auth.  Every page requires a signed-in user except Login and
// Error -- see the AddRazorPages conventions below.  Seeded credential
// is admin/pass (see db\001_schema.sql §4).
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath        = "/Login";
        options.LogoutPath       = "/Logout";
        options.AccessDeniedPath = "/Login";
        options.ExpireTimeSpan   = TimeSpan.FromDays(1);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Error");
});

var app = builder.Build();

// Apply the SQL schema (idempotent) before serving any requests.  This
// lets containerised deployments come up without an external sqlcmd
// step.  AGENTFORGE_DB must be set.  Set AGENTFORGE_SKIP_SCHEMA=1 to
// disable (e.g. when an external operator owns the schema).
var connStr = Environment.GetEnvironmentVariable("AGENTFORGE_DB")
    ?? throw new InvalidOperationException("AGENTFORGE_DB env var is not set");
if (Environment.GetEnvironmentVariable("AGENTFORGE_SKIP_SCHEMA") != "1")
{
    var schemaPath = Path.Combine(app.Environment.ContentRootPath, "db", "001_schema.sql");
    SchemaApplier.Apply(connStr, schemaPath, app.Logger);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// Logout: minimal endpoint so we don't need a dedicated Razor page.
// Antiforgery isn't enforced on minimal endpoints -- the worst-case
// CSRF impact is "tricking an operator into logging themselves out."
app.MapPost("/Logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/Login");
}).AllowAnonymous();

app.Run();
