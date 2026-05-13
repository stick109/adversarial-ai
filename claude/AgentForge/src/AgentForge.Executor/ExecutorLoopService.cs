using Dapper;
using Microsoft.Data.SqlClient;

namespace AgentForge.Executor;

// BackgroundService that fires the harness every N minutes, where N is
// read fresh from dbo.Parameters on every iteration so an operator can
// tune the interval at the DB level without restarting the container.
//
// On startup the service waits for the dbo.Parameters table to exist
// (the Web container applies the schema) and then runs immediately;
// each subsequent iteration sleeps Value(executor-loop-minutes) minutes.
public sealed class ExecutorLoopService : BackgroundService
{
    private const string IntervalKey = "executor-loop-minutes";
    private const int DefaultIntervalMinutes = 5;
    private static readonly TimeSpan SchemaPollDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SchemaPollDeadline = TimeSpan.FromMinutes(2);

    private readonly string _connectionString;
    private readonly string _copilotBaseUrl;
    private readonly ILogger<ExecutorLoopService> _logger;

    public ExecutorLoopService(IConfiguration config, ILogger<ExecutorLoopService> logger)
    {
        _connectionString = config["AGENTFORGE_DB"]
            ?? throw new InvalidOperationException("AGENTFORGE_DB env var is not set");
        _copilotBaseUrl = config["COPILOT_BASE_URL"]
            ?? "https://openemr-web-production.up.railway.app";
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForParametersTable(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ExecutorRunner.Start(_connectionString, _copilotBaseUrl, "schedule", _logger);
            }
            catch (Exception ex)
            {
                // ExecutorRunner.Start only does a single INSERT before
                // backgrounding the harness work; an exception here means
                // the DB rejected the insert.  Log + keep looping so a
                // transient SQL blip doesn't kill the schedule.
                _logger.LogError(ex, "Failed to start scheduled executor run; will retry next tick");
            }

            var interval = ReadIntervalOrFallback();
            _logger.LogInformation("Next scheduled run in {Minutes} minute(s)", interval.TotalMinutes);
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task WaitForParametersTable(CancellationToken token)
    {
        var deadline = DateTime.UtcNow + SchemaPollDeadline;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var db = new SqlConnection(_connectionString);
                db.Open();
                _ = db.ExecuteScalar<string?>(
                    @"SELECT [Value] FROM dbo.Parameters WHERE [Key] = @Key;",
                    new { Key = IntervalKey });
                _logger.LogInformation("dbo.Parameters reachable; starting loop");
                return;
            }
            catch (Exception ex) when (DateTime.UtcNow < deadline)
            {
                _logger.LogInformation(
                    "Waiting for dbo.Parameters to be ready: {Message}", ex.Message);
                await Task.Delay(SchemaPollDelay, token);
            }
        }
    }

    private TimeSpan ReadIntervalOrFallback()
    {
        try
        {
            using var db = new SqlConnection(_connectionString);
            db.Open();
            var raw = db.ExecuteScalar<string?>(
                @"SELECT [Value] FROM dbo.Parameters WHERE [Key] = @Key;",
                new { Key = IntervalKey });

            if (!string.IsNullOrWhiteSpace(raw) && double.TryParse(raw, out var minutes) && minutes > 0)
            {
                return TimeSpan.FromMinutes(minutes);
            }
            _logger.LogWarning(
                "Parameter {Key} missing/invalid (raw='{Raw}'); falling back to {Default}m",
                IntervalKey, raw, DefaultIntervalMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read {Key} from dbo.Parameters; falling back to {Default}m",
                IntervalKey, DefaultIntervalMinutes);
        }
        return TimeSpan.FromMinutes(DefaultIntervalMinutes);
    }
}
