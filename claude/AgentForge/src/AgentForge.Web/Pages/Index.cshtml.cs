using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;

namespace AgentForge.Web.Pages;

// Antiforgery is intentionally skipped for this whole page.  This is a
// dev tool with a single-button form (no user-supplied fields), and on
// Railway the data protection key ring isn't sticky across deploys --
// which would CSRF-fail otherwise-valid clicks every time the container
// restarts.  The button only triggers a Red Team probe run; the
// worst-case CSRF impact is a few cents of LLM spend.
// (Handler-level [IgnoreAntiforgeryToken] is silently ignored on Razor
// Pages -- see MVC1001 -- so the attribute lives on the class instead.)
[IgnoreAntiforgeryToken]
public class IndexModel : PageModel
{
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(ILogger<IndexModel> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ToggleRow> Toggles { get; private set; } = Array.Empty<ToggleRow>();
    public IReadOnlyList<TestRow> Tests { get; private set; } = Array.Empty<TestRow>();
    public IReadOnlyList<ExecutionRow> Executions { get; private set; } = Array.Empty<ExecutionRow>();
    public IReadOnlyList<RunRow> Runs { get; private set; } = Array.Empty<RunRow>();

    public bool HasApiKey { get; private set; }

    public void OnGet()
    {
        var connStr = Environment.GetEnvironmentVariable("AGENTFORGE_DB")
            ?? throw new InvalidOperationException("AGENTFORGE_DB env var is not set");
        HasApiKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"));

        using var db = new SqlConnection(connStr);
        db.Open();

        Toggles = db.Query<ToggleRow>(@"
            SELECT FieldPath, Priority, IsEnabled, DefaultJson, Description
              FROM dbo.VariabilityToggles
             ORDER BY Priority").AsList();

        Tests = db.Query<TestRow>(@"
            SELECT TOP 50
                   p.Id, p.CreatedAt, p.Category, p.GeneratorModel, p.CreatedBy,
                   (SELECT COUNT(*) FROM OPENJSON(p.Turns)) AS TurnCount,
                   JSON_VALUE(p.Bootstrap, '$.patient_id') AS PatientId,
                   p.Description,
                   (SELECT COUNT(*) FROM dbo.PenetrationTestExecutions e WHERE e.TestId = p.Id) AS ExecutionCount
              FROM dbo.PenetrationTests p
             ORDER BY p.Id DESC").AsList();

        Executions = db.Query<ExecutionRow>(@"
            SELECT TOP 50 e.Id, e.TestId, e.ExecutedAt, e.Outcome, e.ErrorClass,
                          DATALENGTH(e.StepResultsJson) AS StepBytes,
                          (SELECT COUNT(*) FROM OPENJSON(e.StepResultsJson)) AS StepCount
              FROM dbo.PenetrationTestExecutions e
             ORDER BY e.Id DESC").AsList();

        // Only the small ErrorMessageId FK is selected here -- the
        // potentially-large message text lives in dbo.ErrorMessages
        // and is fetched on-demand by the /Error/{id} page.
        Runs = db.Query<RunRow>(@"
            SELECT TOP 20 r.Id, r.StartedAt, r.FinishedAt, r.Status, r.ExitCode,
                          r.ResultTestId, r.ErrorMessageId
              FROM dbo.RedTeamRuns r
             ORDER BY r.Id DESC").AsList();
    }

    public IActionResult OnPostStartRedTeam()
    {
        var connStr = Environment.GetEnvironmentVariable("AGENTFORGE_DB")
            ?? throw new InvalidOperationException("AGENTFORGE_DB env var is not set");
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

        var runId = RedTeamRunner.Start(connStr, apiKey, _logger);
        return RedirectToPage("Run", new { id = runId });
    }

    // Flip VariabilityToggles.IsEnabled for the row whose FieldPath
    // matches.  Returns to the dashboard so the operator sees the new
    // state inline; no flash message needed.
    public IActionResult OnPostToggleVariability(string fieldPath)
    {
        if (string.IsNullOrWhiteSpace(fieldPath))
        {
            return RedirectToPage();
        }

        var connStr = Environment.GetEnvironmentVariable("AGENTFORGE_DB")
            ?? throw new InvalidOperationException("AGENTFORGE_DB env var is not set");

        using var db = new SqlConnection(connStr);
        db.Execute(@"
            UPDATE dbo.VariabilityToggles
               SET IsEnabled = CASE WHEN IsEnabled = 1 THEN 0 ELSE 1 END
             WHERE FieldPath = @fp",
            new { fp = fieldPath });

        return RedirectToPage();
    }

    public sealed record ToggleRow(string FieldPath, int Priority, bool IsEnabled, string? DefaultJson, string Description);
    public sealed record TestRow(int Id, DateTime CreatedAt, string Category, string? GeneratorModel, string CreatedBy, int TurnCount, string? PatientId, string Description, int ExecutionCount);
    public sealed record ExecutionRow(int Id, int TestId, DateTime ExecutedAt, string Outcome, string? ErrorClass, long? StepBytes, int StepCount);
    public sealed record RunRow(int Id, DateTime StartedAt, DateTime? FinishedAt, string Status, int? ExitCode, int? ResultTestId, int? ErrorMessageId);
}
