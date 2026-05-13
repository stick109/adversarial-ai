using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;

namespace SecurityAnalyzer.Web.Pages;

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

    // Static HttpClient: the "Start Executor run" handler posts one
    // request per click; reuse the same client across page lifetimes
    // to avoid socket-exhaustion from per-request `new HttpClient()`.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

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
        var connStr = Environment.GetEnvironmentVariable("SECURITY_ANALYZER_DB")
            ?? throw new InvalidOperationException("SECURITY_ANALYZER_DB env var is not set");
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
        var connStr = Environment.GetEnvironmentVariable("SECURITY_ANALYZER_DB")
            ?? throw new InvalidOperationException("SECURITY_ANALYZER_DB env var is not set");
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

        var runId = RedTeamRunner.Start(connStr, apiKey, _logger);
        return RedirectToPage("Run", new { id = runId });
    }

    // POSTs to the Executor's HTTP trigger (EXECUTOR_BASE_URL/runs),
    // parses the returned executorRunId, and redirects to the run page
    // so the operator sees the run progress live.  On HTTP failure we
    // stash the error in TempData and bounce back to the Executions
    // tab so it renders on the next OnGet.
    public async Task<IActionResult> OnPostStartExecutorRunAsync()
    {
        var executorBase = (Environment.GetEnvironmentVariable("EXECUTOR_BASE_URL")
            ?? "http://security-analyzer-executor:8080").TrimEnd('/');
        var url = $"{executorBase}/runs";

        int runId;
        try
        {
            using var resp = await _http.PostAsync(url, content: null);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Executor returned HTTP {(int)resp.StatusCode}: {body}");
            }
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            runId = doc.RootElement.GetProperty("executorRunId").GetInt32();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger executor run at {Url}", url);
            TempData["ExecutorError"] =
                $"Could not start executor run at {url}: {ex.GetType().Name}: {ex.Message}";
            return LocalRedirect("/#tab-executions");
        }

        return RedirectToPage("ExecutorRun", new { id = runId });
    }

    // Flip VariabilityToggles.IsEnabled for the row whose FieldPath
    // matches.  Returns to the dashboard so the operator sees the new
    // state inline; the #tab-toggles fragment keeps the user on the
    // Toggles tab instead of bouncing back to the default tab.
    public IActionResult OnPostToggleVariability(string fieldPath)
    {
        if (string.IsNullOrWhiteSpace(fieldPath))
        {
            return LocalRedirect("/#tab-toggles");
        }

        var connStr = Environment.GetEnvironmentVariable("SECURITY_ANALYZER_DB")
            ?? throw new InvalidOperationException("SECURITY_ANALYZER_DB env var is not set");

        using var db = new SqlConnection(connStr);
        db.Execute(@"
            UPDATE dbo.VariabilityToggles
               SET IsEnabled = CASE WHEN IsEnabled = 1 THEN 0 ELSE 1 END
             WHERE FieldPath = @fp",
            new { fp = fieldPath });

        return LocalRedirect("/#tab-toggles");
    }

    public sealed record ToggleRow(string FieldPath, int Priority, bool IsEnabled, string? DefaultJson, string Description);
    public sealed record TestRow(int Id, DateTime CreatedAt, string Category, string? GeneratorModel, string CreatedBy, int TurnCount, string? PatientId, string Description, int ExecutionCount);
    public sealed record ExecutionRow(int Id, int TestId, DateTime ExecutedAt, string Outcome, string? ErrorClass, long? StepBytes, int StepCount);
    public sealed record RunRow(int Id, DateTime StartedAt, DateTime? FinishedAt, string Status, int? ExitCode, int? ResultTestId, int? ErrorMessageId);
}
