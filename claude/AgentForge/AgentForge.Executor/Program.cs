using AgentForge.Executor;

var builder = WebApplication.CreateBuilder(args);

// Surface AGENTFORGE_* environment variables through IConfiguration so
// the background service and the HTTP endpoint can read them from DI.
// (ASP.NET picks up env vars with the ASPNETCORE_ prefix by default;
// we want everything.)
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddHostedService<ExecutorLoopService>();

var app = builder.Build();

var connStr = app.Configuration["AGENTFORGE_DB"]
    ?? throw new InvalidOperationException("AGENTFORGE_DB env var is not set");
var copilotBaseUrl = app.Configuration["COPILOT_BASE_URL"]
    ?? "https://openemr-web-production.up.railway.app";

// Health probe -- handy from `docker logs` / curl while debugging.
app.MapGet("/", () => Results.Ok(new
{
    service = "agentforge-executor",
    copilotBaseUrl,
}));

// POST /runs -- trigger an immediate harness invocation outside the
// scheduled cadence.  Returns 202 with the inserted ExecutorRuns.Id;
// the harness work runs on the thread pool so the caller is not
// blocked.  No body required.
app.MapPost("/runs", (ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("ExecutorRunner.Http");
    var runId = ExecutorRunner.Start(connStr, copilotBaseUrl, "http", logger);
    return Results.Accepted($"/runs/{runId}", new { executorRunId = runId });
});

app.Run();
