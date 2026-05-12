using AgentForge.RedTeam;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AgentForge.Web;

// Wraps a Red Team invocation in a tracked DB lifecycle:
//   1. Insert a RedTeamRuns row with Status='running', return its Id.
//   2. Kick off a background Task.Run that calls RedTeamAgent.RunOnce.
//   3. When RunOnce returns (or throws), update the row with FinishedAt
//      + Status + ExitCode + ResultTestId + ErrorMessage.
//
// The HTTP request returns the runId immediately; the UI redirects to
// /Run/{id} which auto-refreshes until Status != 'running'.
public static class RedTeamRunner
{
    public static int Start(string connectionString, string? openRouterApiKey, ILogger? logger = null)
    {
        DateTime startedAt;
        int runId;
        using (var db = new SqlConnection(connectionString))
        {
            db.Open();
            var inserted = db.QuerySingle<(int Id, DateTime StartedAt)>(
                @"INSERT INTO dbo.RedTeamRuns
                  OUTPUT INSERTED.Id, INSERTED.StartedAt
                  DEFAULT VALUES;");
            runId = inserted.Id;
            startedAt = inserted.StartedAt;
        }

        _ = Task.Run(() => RunInBackground(runId, startedAt, connectionString, openRouterApiKey, logger));

        return runId;
    }

    private static void RunInBackground(
        int runId,
        DateTime startedAt,
        string connectionString,
        string? openRouterApiKey,
        ILogger? logger)
    {
        int exitCode = -1;
        string? errorMessage = null;

        try
        {
            exitCode = RedTeamAgent.RunOnce(connectionString, openRouterApiKey);
        }
        catch (Exception ex)
        {
            errorMessage = $"{ex.GetType().FullName}: {ex.Message}";
            logger?.LogError(ex, "RedTeam run {RunId} threw", runId);
        }

        int? insertedTestId = null;
        if (exitCode == 0)
        {
            try
            {
                using var db = new SqlConnection(connectionString);
                db.Open();
                insertedTestId = db.QueryFirstOrDefault<int?>(
                    @"SELECT TOP 1 Id FROM dbo.PenetrationTests
                       WHERE CreatedAt >= @StartedAt
                         AND CreatedBy = N'red_team_agent'
                       ORDER BY Id DESC;",
                    new { StartedAt = startedAt });
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to look up inserted test id for run {RunId}", runId);
            }
        }

        var status = exitCode == 0 ? "ok" : "failed";
        if (errorMessage is null && exitCode != 0)
        {
            errorMessage = $"RedTeamAgent.RunOnce returned exit code {exitCode} (see container logs)";
        }

        try
        {
            using var db = new SqlConnection(connectionString);
            db.Open();
            db.Execute(
                @"UPDATE dbo.RedTeamRuns
                     SET FinishedAt   = SYSUTCDATETIME(),
                         Status       = @Status,
                         ExitCode     = @ExitCode,
                         ResultTestId = @TestId,
                         ErrorMessage = @ErrorMessage
                   WHERE Id = @Id;",
                new
                {
                    Id = runId,
                    Status = status,
                    ExitCode = exitCode,
                    TestId = insertedTestId,
                    ErrorMessage = errorMessage,
                });
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to persist final state for run {RunId}", runId);
        }
    }
}
