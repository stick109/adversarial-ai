using SecurityAnalyzer.Executor;

// --once: invoke PenetrationHarness.RunOnce in-process and exit with
// its status code.  Skips the web host and the scheduler -- this is
// the equivalent of the old `dotnet run --project SecurityAnalyzer.Harness`
// CLI invocation.  TriggeredBy is recorded as 'direct' on the resulting
// dbo.PenetrationTestExecutions row.
if (args.Length > 0 && args[0] == "--once")
{
    return PenetrationHarness.RunOnce(
        Environment.GetEnvironmentVariable("SECURITY_ANALYZER_DB")
            ?? throw new InvalidOperationException("SECURITY_ANALYZER_DB env var is not set"),
        Environment.GetEnvironmentVariable("COPILOT_BASE_URL")
            ?? "https://openemr-web-production.up.railway.app",
        triggeredBy: "direct");
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
// scheduled cadence.  Two-phase: PenetrationHarness.Start synchronously
// inserts the dbo.PenetrationTestExecutions row with Outcome='running'
// and returns its Id; the actual test work then runs on a thread-pool
// task so the caller is not blocked.  Body is { executionId }.
app.MapPost("/runs", (ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("PenetrationHarness.Http");
    var executionId = PenetrationHarness.Start(connStr, triggeredBy: "http");
    if (executionId is null)
    {
        logger.LogWarning("POST /runs invoked but no PenetrationTests rows exist");
        return Results.NoContent();
    }

    logger.LogInformation("Started PenetrationTestExecutions.Id={Id} (triggeredBy=http)", executionId.Value);

    _ = Task.Run(() =>
    {
        try
        {
            PenetrationHarness.Continue(connStr, copilotBaseUrl, executionId.Value);
        }
        catch (Exception ex)
        {
            // PenetrationHarness.Continue catches its own exceptions and
            // records them on the row; this handler only fires if the
            // bookkeeping UPDATE itself threw (e.g. transient SQL blip
            // post-test).  Log and drop.
            logger.LogError(ex, "Background harness run for execution {Id} threw outside Continue", executionId.Value);
        }
    });

    return Results.Accepted($"/Execution/{executionId.Value}", new { executionId = executionId.Value });
});

app.Run();
return 0;
