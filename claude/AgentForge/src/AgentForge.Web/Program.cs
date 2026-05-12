using AgentForge.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();

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
app.MapRazorPages();

app.Run();
