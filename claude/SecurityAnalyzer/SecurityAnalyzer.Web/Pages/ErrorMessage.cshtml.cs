using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;

namespace SecurityAnalyzer.Web.Pages;

// /ErrorMessage/{id} -- focused view of one row from dbo.ErrorMessages
// plus a back-link to the run that produced it (if any).
//
// The dashboard never selects the message text; it only shows a link
// to this page, which fetches the row on demand.
public class ErrorMessageModel : PageModel
{
    public int Id { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string? PrettyJson { get; private set; }
    public int? OwningRunId { get; private set; }

    public IActionResult OnGet(int id)
    {
        var connStr = Environment.GetEnvironmentVariable("SECURITY_ANALYZER_DB")
            ?? throw new InvalidOperationException("SECURITY_ANALYZER_DB env var is not set");

        using var db = new SqlConnection(connStr);
        db.Open();

        var row = db.QueryFirstOrDefault<(int Id, DateTime CreatedAt, string Message)?>(
            @"SELECT Id, CreatedAt, Message FROM dbo.ErrorMessages WHERE Id = @Id;",
            new { Id = id });

        if (row is null) return NotFound();

        Id = row.Value.Id;
        CreatedAt = row.Value.CreatedAt;
        Message = row.Value.Message;

        OwningRunId = db.QueryFirstOrDefault<int?>(
            @"SELECT TOP 1 Id FROM dbo.RedTeamRuns WHERE ErrorMessageId = @Id ORDER BY Id ASC;",
            new { Id = id });

        PrettyJson = TryPrettyPrintJson(Message);

        return Page();
    }

    // Many of our errors carry a JSON body inside an exception message
    // (e.g. "OpenRouter returned 429: {...}").  If we can extract the
    // first {...} block and parse it, format it indented for readability.
    // Returns null if there's no JSON to format.
    private static string? TryPrettyPrintJson(string text)
    {
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first < 0 || last <= first) return null;
        var slice = text[first..(last + 1)];
        try
        {
            using var doc = JsonDocument.Parse(slice);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return null;
        }
    }
}
