using Dapper;
using Microsoft.Data.SqlClient;

namespace SecurityAnalyzer.Executor;

// Wraps one Harness invocation in a tracked DB lifecycle, mirroring the
// pattern in SecurityAnalyzer.Web.RedTeamRunner:
//   1. Insert an ExecutorRuns row with Status='running', return its Id.
//   2. Kick off a background Task.Run that calls PenetrationHarness.RunOnce.
//   3. When RunOnce returns (or throws), update the row with FinishedAt,
//      Status, ExitCode, the PenetrationTestExecution it produced, and --
//      if there was an exception -- a row in ErrorMessages stamped onto
//      the run.
//
// HTTP POST /runs and the BackgroundService loop both call Start(...).
// Start returns immediately with the runId; the harness work happens on
// the thread pool so the HTTP request is not blocked.
public static class ExecutorRunner
{
    public static int Start(
        string connectionString,
        string copilotBaseUrl,
        string triggeredBy,
        ILogger? logger = null)
    {
        DateTime startedAt;
        int runId;
        using (var db = new SqlConnection(connectionString))
        {
            db.Open();
            var inserted = db.QuerySingle<(int Id, DateTime StartedAt)>(
                @"INSERT INTO dbo.ExecutorRuns (TriggeredBy)
                  OUTPUT INSERTED.Id, INSERTED.StartedAt
                  VALUES (@TriggeredBy);",
                new { TriggeredBy = triggeredBy });
            runId = inserted.Id;
            startedAt = inserted.StartedAt;
        }

        logger?.LogInformation(
            "ExecutorRuns.Id={RunId} started (triggeredBy={Trigger})", runId, triggeredBy);

        _ = Task.Run(() => RunInBackground(runId, startedAt, connectionString, copilotBaseUrl, logger));

        return runId;
    }

    private static void RunInBackground(
        int runId,
        DateTime startedAt,
        string connectionString,
        string copilotBaseUrl,
        ILogger? logger)
    {
        int exitCode = -1;
        string? errorMessage = null;

        try
        {
            exitCode = PenetrationHarness.RunOnce(connectionString, copilotBaseUrl);
        }
        catch (Exception ex)
        {
            errorMessage = $"{ex.GetType().FullName}: {ex.Message}\n\n{ex.StackTrace}";
            logger?.LogError(ex, "Executor run {RunId} threw", runId);
        }

        // Find the PenetrationTestExecutions row that this harness call
        // produced.  The harness writes exactly one row per RunOnce; pick
        // the newest one inserted since StartedAt as a best-effort link.
        int? executionId = null;
        try
        {
            using var db = new SqlConnection(connectionString);
            db.Open();
            executionId = db.QueryFirstOrDefault<int?>(
                @"SELECT TOP 1 Id FROM dbo.PenetrationTestExecutions
                   WHERE ExecutedAt >= @StartedAt
                   ORDER BY Id DESC;",
                new { StartedAt = startedAt });
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to look up execution id for run {RunId}", runId);
        }

        var status = exitCode == 0 ? "ok" : "failed";
        if (errorMessage is null && exitCode != 0)
        {
            errorMessage = $"PenetrationHarness.RunOnce returned exit code {exitCode} with no exception (see container logs)";
        }

        try
        {
            using var db = new SqlConnection(connectionString);
            db.Open();

            int? errorMessageId = null;
            if (errorMessage is not null)
            {
                errorMessageId = db.ExecuteScalar<int>(
                    @"INSERT INTO dbo.ErrorMessages (Message)
                      OUTPUT INSERTED.Id
                      VALUES (@Message);",
                    new { Message = errorMessage });
            }

            db.Execute(
                @"UPDATE dbo.ExecutorRuns
                     SET FinishedAt                  = SYSUTCDATETIME(),
                         Status                      = @Status,
                         ExitCode                    = @ExitCode,
                         PenetrationTestExecutionId  = @ExecutionId,
                         ErrorMessageId              = @ErrorMessageId
                   WHERE Id = @Id;",
                new
                {
                    Id = runId,
                    Status = status,
                    ExitCode = exitCode,
                    ExecutionId = executionId,
                    ErrorMessageId = errorMessageId,
                });
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to persist final state for run {RunId}", runId);
        }
    }
}
