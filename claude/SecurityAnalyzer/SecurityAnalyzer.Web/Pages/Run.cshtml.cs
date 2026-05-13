using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;

namespace SecurityAnalyzer.Web.Pages;

public class RunModel : PageModel
{
    public RunRow? Run { get; private set; }
    public TestRow? Test { get; private set; }
    public string? TurnsJson { get; private set; }
    public string? BootstrapJson { get; private set; }
    public IReadOnlyList<ExecRow> Executions { get; private set; } = Array.Empty<ExecRow>();

    public IActionResult OnGet(int id)
    {
        var connStr = Environment.GetEnvironmentVariable("SECURITY_ANALYZER_DB")
            ?? throw new InvalidOperationException("SECURITY_ANALYZER_DB env var is not set");

        using var db = new SqlConnection(connStr);
        db.Open();

        Run = db.QueryFirstOrDefault<RunRow>(@"
            SELECT Id, StartedAt, FinishedAt, Status, ExitCode, ResultTestId, ErrorMessageId
              FROM dbo.RedTeamRuns
             WHERE Id = @Id;", new { Id = id });

        if (Run is null) return NotFound();

        if (Run.ResultTestId is int testId)
        {
            Test = db.QueryFirstOrDefault<TestRow>(@"
                SELECT p.Id, p.CreatedAt, p.Category, p.GeneratorModel, p.CreatedBy,
                       p.Description, p.Bootstrap, p.Turns
                  FROM dbo.PenetrationTests p
                 WHERE p.Id = @Id;", new { Id = testId });

            if (Test is not null)
            {
                TurnsJson = PrettyJson(Test.Turns);
                BootstrapJson = PrettyJson(Test.Bootstrap);
            }

            Executions = db.Query<ExecRow>(@"
                SELECT TOP 10 Id, ExecutedAt, Outcome, ErrorClass
                  FROM dbo.PenetrationTestExecutions
                 WHERE TestId = @Id
                 ORDER BY Id DESC;", new { Id = testId }).AsList();
        }

        if (Run.Status == "running")
        {
            ViewData["MetaRefresh"] = 2;
        }

        return Page();
    }

    private static string PrettyJson(string raw)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            return System.Text.Json.JsonSerializer.Serialize(
                doc.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return raw;
        }
    }

    public sealed record RunRow(int Id, DateTime StartedAt, DateTime? FinishedAt, string Status, int? ExitCode, int? ResultTestId, int? ErrorMessageId);
    public sealed record TestRow(int Id, DateTime CreatedAt, string Category, string? GeneratorModel, string CreatedBy, string Description, string Bootstrap, string Turns);
    public sealed record ExecRow(int Id, DateTime ExecutedAt, string Outcome, string? ErrorClass);
}
