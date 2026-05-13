using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;

namespace SecurityAnalyzer.Web.Pages;

// Detail page for a single dbo.PenetrationTestExecutions row.  Replaces
// the old /ExecutorRun/{id} page that read the (now-removed) ExecutorRuns
// table -- after db/019-merge-executor-runs.sql the lifecycle columns
// (StartedAt/FinishedAt/TriggeredBy/ExitCode/ErrorMessageId) live on
// PenetrationTestExecutions itself.
public class ExecutionModel : PageModel
{
    public ExecutionRow? Row { get; private set; }

    public IActionResult OnGet(int id)
    {
        var connStr = Environment.GetEnvironmentVariable("SECURITY_ANALYZER_DB")
            ?? throw new InvalidOperationException("SECURITY_ANALYZER_DB env var is not set");

        using var db = new SqlConnection(connStr);
        db.Open();

        Row = db.QueryFirstOrDefault<ExecutionRow>(@"
            SELECT e.Id, e.TestId, e.StartedAt, e.FinishedAt, e.TriggeredBy,
                   e.Outcome, e.ExitCode, e.ErrorClass, e.ErrorMessageId,
                   DATALENGTH(e.StepResultsJson)              AS StepBytes,
                   (SELECT COUNT(*) FROM OPENJSON(e.StepResultsJson)) AS StepCount
              FROM dbo.PenetrationTestExecutions e
             WHERE e.Id = @Id;", new { Id = id });

        if (Row is null) return NotFound();

        if (Row.Outcome == "running")
        {
            ViewData["MetaRefresh"] = 2;
        }

        return Page();
    }

    public sealed record ExecutionRow(
        int Id,
        int TestId,
        DateTime StartedAt,
        DateTime? FinishedAt,
        string TriggeredBy,
        string Outcome,
        int? ExitCode,
        string? ErrorClass,
        int? ErrorMessageId,
        long? StepBytes,
        int StepCount);
}
