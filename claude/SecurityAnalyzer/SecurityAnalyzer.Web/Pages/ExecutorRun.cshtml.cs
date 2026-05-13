using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;

namespace SecurityAnalyzer.Web.Pages;

// Mirror of /Run/{id} (which surfaces RedTeamRuns) but for the
// ExecutorRuns table: one row per harness invocation triggered by
// either the loop service or the HTTP /runs endpoint (incl. the
// "Start Executor run" button on the dashboard's Executions tab).
public class ExecutorRunModel : PageModel
{
    public RunRow? Run { get; private set; }
    public ExecutionRow? Execution { get; private set; }

    public IActionResult OnGet(int id)
    {
        var connStr = Environment.GetEnvironmentVariable("SECURITY_ANALYZER_DB")
            ?? throw new InvalidOperationException("SECURITY_ANALYZER_DB env var is not set");

        using var db = new SqlConnection(connStr);
        db.Open();

        Run = db.QueryFirstOrDefault<RunRow>(@"
            SELECT Id, StartedAt, FinishedAt, Status, TriggeredBy, ExitCode,
                   PenetrationTestExecutionId, ErrorMessageId
              FROM dbo.ExecutorRuns
             WHERE Id = @Id;", new { Id = id });

        if (Run is null) return NotFound();

        if (Run.PenetrationTestExecutionId is int execId)
        {
            Execution = db.QueryFirstOrDefault<ExecutionRow>(@"
                SELECT e.Id, e.TestId, e.ExecutedAt, e.Outcome, e.ErrorClass,
                       DATALENGTH(e.StepResultsJson) AS StepBytes,
                       (SELECT COUNT(*) FROM OPENJSON(e.StepResultsJson)) AS StepCount
                  FROM dbo.PenetrationTestExecutions e
                 WHERE e.Id = @Id;", new { Id = execId });
        }

        if (Run.Status == "running")
        {
            ViewData["MetaRefresh"] = 2;
        }

        return Page();
    }

    public sealed record RunRow(
        int Id, DateTime StartedAt, DateTime? FinishedAt, string Status,
        string TriggeredBy, int? ExitCode, int? PenetrationTestExecutionId,
        int? ErrorMessageId);

    public sealed record ExecutionRow(
        int Id, int TestId, DateTime ExecutedAt, string Outcome, string? ErrorClass,
        long? StepBytes, int StepCount);
}
