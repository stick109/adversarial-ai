using SecurityAnalyzer.Executor;

// --once: run PenetrationHarness.RunOnce against the configured DB +
// Co-Pilot exactly once, then exit with its status code.  Skips the
// web host, the scheduler, and the ExecutorRuns wrapper -- this is the
// in-process equivalent of the old `dotnet run --project SecurityAnalyzer.Harness`.
if (args.Length > 0 && args[0] == "--once")
{
    return PenetrationHarness.RunOnce(
        Environment.GetEnvironmentVariable("SECURITY_ANALYZER_DB")
            ?? throw new InvalidOperationException("SECURITY_ANALYZER_DB env var is not set"),
        Environment.GetEnvironmentVariable("COPILOT_BASE_URL")
            ?? "https://openemr-web-production.up.railway.app");
}

var builder = WebApplication.CreateBuilder(args);

// Surface SECURITY_ANALYZER_* environment variables through IConfiguration so
// the background service and the HTTP endpoint can read them from DI.
// (ASP.NET picks up env vars with the ASPNETCORE_ prefix by default;
// we want everything.)
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddHostedService<ExecutorLoopService>();

var app = builder.Build();

var connStr = app.Configuration["SECURITY_ANALYZER_DB"]
    ?? throw new InvalidOperationException("SECURITY_ANALYZER_DB env var is not set");
var copilotBaseUrl = app.Configuration["COPILOT_BASE_URL"]
    ?? "https://openemr-web-production.up.railway.app";

// Health probe -- handy from `docker logs` / curl while debugging.
app.MapGet("/", () => Results.Ok(new
{
    service = "security-analyzer-executor",
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
return 0;
