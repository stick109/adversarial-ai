using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace SecurityAnalyzer.Harness;

// One invocation -> pick the next test, exercise it against the live
// Clinical Co-Pilot, record one row in PenetrationTestExecutions.
// Plan v1 §5.  HTTP wire mirrors POC-1/poc.py.
public static class PenetrationHarness
{
    // Match the data-api-csrf-token attribute on the chart page; POC-1
    // confirms it appears exactly once.
    private static readonly Regex CsrfRegex = new(
        @"data-api-csrf-token=""([^""]+)""",
        RegexOptions.Compiled);

    // Plan §5 step 4: cap step body capture so a runaway HTML payload
    // can't blow up StepResultsJson (NVARCHAR(MAX) but we want it readable).
    private const int MaxBodyChars = 200_000;

    public static int RunOnce(string connectionString, string copilotBaseUrl)
    {
        copilotBaseUrl = copilotBaseUrl.TrimEnd('/');

        using var db = new SqlConnection(connectionString);
        db.Open();

        // 1. Load toggle defaults.
        var toggles = db.Query<Toggle>(@"
            SELECT FieldPath, IsEnabled, DefaultJson
              FROM dbo.VariabilityToggles").AsList();

        var defaults = toggles.ToDictionary(t => t.FieldPath, t => t.DefaultJson, StringComparer.Ordinal);

        // 2. Pick the next test (never-run sorts first, then oldest last-run).
        var test = db.QueryFirstOrDefault<PenTestRow>(@"
            SELECT TOP 1 t.Id, t.Category, t.Bootstrap, t.Turns, t.Description
              FROM dbo.PenetrationTests t
              OUTER APPLY (
                  SELECT MAX(e.ExecutedAt) AS LastRun
                    FROM dbo.PenetrationTestExecutions e
                   WHERE e.TestId = t.Id
              ) lr
             ORDER BY lr.LastRun ASC;");

        if (test is null)
        {
            Console.WriteLine("[Harness] no tests found; exiting 0");
            return 0;
        }

        Console.WriteLine($"[Harness] picked PenetrationTests.Id = {test.Id} ({test.Category})");
        Console.WriteLine($"[Harness] description: {test.Description}");

        var steps = new List<StepResult>();
        string outcome;
        string? errorClass = null;

        try
        {
            // Merge bootstrap defaults with per-test overrides.
            var bootstrap = MergeBootstrap(test.Bootstrap, defaults);
            var turns = MergeTurns(test.Turns, defaults);

            // 3. Bootstrap clinician session.
            var cookies = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                AllowAutoRedirect = true,
                UseCookies = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SecurityAnalyzer-Harness/0.1");

            var (csrfToken, bootstrapOutcome) = BootstrapSession(
                http, copilotBaseUrl, bootstrap, steps);

            if (bootstrapOutcome is not null)
            {
                outcome = bootstrapOutcome;
            }
            else if (string.IsNullOrEmpty(csrfToken))
            {
                outcome = "http_error";
                Console.Error.WriteLine("[Harness] bootstrap finished without CSRF token; aborting");
            }
            else
            {
                // 4. Turn loop.
                outcome = RunTurns(http, copilotBaseUrl, csrfToken!, bootstrap, turns, steps);
            }
        }
        catch (Exception ex)
        {
            outcome = "exception";
            errorClass = ex.GetType().FullName;
            Console.Error.WriteLine($"[Harness] {errorClass}: {ex.Message}");
        }

        // 5. Record one execution row.
        var stepsJson = JsonSerializer.Serialize(steps, new JsonSerializerOptions { WriteIndented = false });
        var execId = db.ExecuteScalar<int>(@"
            INSERT INTO dbo.PenetrationTestExecutions (TestId, Outcome, StepResultsJson, ErrorClass)
            OUTPUT INSERTED.Id
            VALUES (@TestId, @Outcome, @StepResultsJson, @ErrorClass)",
            new { TestId = test.Id, Outcome = outcome, StepResultsJson = stepsJson, ErrorClass = errorClass });

        Console.WriteLine($"[Harness] inserted PenetrationTestExecutions.Id = {execId} (outcome = {outcome})");
        return outcome == "exception" ? 1 : 0;
    }

    // ------------------------------------------------------------------
    // bootstrap (steps 1-4 of POC-1)
    // ------------------------------------------------------------------
    private static (string? CsrfToken, string? FailureOutcome) BootstrapSession(
        HttpClient http,
        string baseUrl,
        BootstrapState bootstrap,
        List<StepResult> steps)
    {
        // 1. GET login page (seed session)
        var step1 = SendStep(http, HttpMethod.Get,
            $"{baseUrl}/interface/login/login.php?site=default",
            headers: null, body: null, steps);
        if (!step1.Ok) return (null, "http_error");

        // 2. POST login form
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("new_login_session_management", "1"),
            new KeyValuePair<string, string>("languageChoice", "1"),
            new KeyValuePair<string, string>("authUser", bootstrap.User.Username),
            new KeyValuePair<string, string>("clearPass", bootstrap.User.Password),
        });
        var step2 = SendStep(http, HttpMethod.Post,
            $"{baseUrl}/interface/main/main_screen.php?auth=login&site=default",
            headers: null, body: form, steps);
        if (!step2.Ok) return (null, "http_error");

        // 3. Bind active patient (unless skip_set_pid)
        if (!bootstrap.SkipSetPid)
        {
            var step3 = SendStep(http, HttpMethod.Get,
                $"{baseUrl}/interface/patient_file/summary/demographics.php?set_pid={bootstrap.PatientId}",
                headers: null, body: null, steps);
            if (!step3.Ok) return (null, "http_error");
        }

        // 4. GET chart page and scrape CSRF token
        var step4 = SendStep(http, HttpMethod.Get,
            $"{baseUrl}/interface/patient_file/summary/agent.php",
            headers: null, body: null, steps);
        if (!step4.Ok) return (null, "http_error");

        var match = CsrfRegex.Match(step4.RawBody ?? string.Empty);
        var token = match.Success ? match.Groups[1].Value : null;
        return (token, null);
    }

    // ------------------------------------------------------------------
    // turn loop (step 5 of POC-1, repeated per turn)
    // ------------------------------------------------------------------
    private static string RunTurns(
        HttpClient http,
        string baseUrl,
        string csrfToken,
        BootstrapState bootstrap,
        IReadOnlyList<TurnState> turns,
        List<StepResult> steps)
    {
        var sharedConversationId = $"harness-{Guid.NewGuid():N}";

        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];

            if (turn.DelayMs > 0)
            {
                Thread.Sleep(turn.DelayMs);
            }

            string conversationId = turn.ConversationIdStrategy switch
            {
                "fresh_each_turn" => $"harness-{Guid.NewGuid():N}",
                var s when s.StartsWith("literal:", StringComparison.Ordinal) => s["literal:".Length..],
                _ /* "share" */ => sharedConversationId,
            };

            var bodyObj = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["intent_id"] = JsonSerializer.SerializeToElement(turn.IntentId),
                ["conversation_id"] = JsonSerializer.SerializeToElement(conversationId),
                ["active_patient_context"] = JsonSerializer.SerializeToElement(turn.ActivePatientContext),
            };
            // Co-Pilot validator: user_goal is only valid with intent_id =
            // "free_text".  An empty-string default for user_goal (the v1
            // toggle default) means "no goal supplied", so we omit it from
            // the wire rather than triggering the validator.
            if (!string.IsNullOrEmpty(turn.UserGoal))
            {
                bodyObj["user_goal"] = JsonSerializer.SerializeToElement(turn.UserGoal);
            }
            if (turn.SourceId is not null)
            {
                bodyObj["source_id"] = turn.SourceId.Value;
            }
            // Merge turn.extra_body keys verbatim (last write wins).
            if (turn.ExtraBody is { } extra && extra.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in extra.EnumerateObject())
                {
                    bodyObj[p.Name] = p.Value.Clone();
                }
            }

            var bodyJson = JsonSerializer.Serialize(bodyObj);
            var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

            var headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["APICSRFTOKEN"] = csrfToken,
                ["Accept"] = "application/json",
            };
            // turn.headers merges in (allowing override).
            if (turn.Headers is { } extraHeaders && extraHeaders.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in extraHeaders.EnumerateObject())
                {
                    headers[p.Name] = p.Value.ValueKind == JsonValueKind.String
                        ? p.Value.GetString() ?? string.Empty
                        : p.Value.GetRawText();
                }
            }

            var step = SendStep(http, HttpMethod.Post,
                $"{baseUrl}/apis/default/api/agent/intent",
                headers, content, steps);
            if (!step.Ok)
            {
                return "http_error";
            }
        }

        return "ok";
    }

    // ------------------------------------------------------------------
    // single HTTP call -> StepResult, captured into the list
    // ------------------------------------------------------------------
    private static (bool Ok, string? RawBody) SendStep(
        HttpClient http,
        HttpMethod method,
        string url,
        IDictionary<string, string>? headers,
        HttpContent? body,
        List<StepResult> steps)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = body;
        if (headers is not null)
        {
            foreach (var kv in headers)
            {
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage? resp = null;
        try
        {
            resp = http.Send(req);
            var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            sw.Stop();

            string? rawBody = TryDecode(bytes, resp.Content.Headers.ContentType);
            string truncated = rawBody is null
                ? $"<{bytes.Length} non-utf8 bytes>"
                : rawBody.Length > MaxBodyChars
                    ? rawBody[..MaxBodyChars] + $"...<truncated {rawBody.Length - MaxBodyChars} chars>"
                    : rawBody;

            steps.Add(new StepResult(method.Method, url, (int)resp.StatusCode, truncated, (int)sw.ElapsedMilliseconds));

            var ok = (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
            if (!ok)
            {
                Console.Error.WriteLine($"[Harness] {method.Method} {url} -> {(int)resp.StatusCode}");
            }
            return (ok, rawBody);
        }
        finally
        {
            resp?.Dispose();
        }
    }

    private static string? TryDecode(byte[] bytes, MediaTypeHeaderValue? contentType)
    {
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // default merge: per-test overrides win over toggle defaults
    // ------------------------------------------------------------------
    private static BootstrapState MergeBootstrap(string testBootstrapJson, IReadOnlyDictionary<string, string?> defaults)
    {
        using var doc = JsonDocument.Parse(testBootstrapJson);

        int patientId = doc.RootElement.TryGetProperty("patient_id", out var pidElem) && pidElem.ValueKind == JsonValueKind.Number
            ? pidElem.GetInt32()
            : 1;

        (string Username, string Password) user;
        if (doc.RootElement.TryGetProperty("user", out var userElem) && userElem.ValueKind == JsonValueKind.Object)
        {
            user = (
                userElem.GetProperty("username").GetString() ?? "admin",
                userElem.GetProperty("password").GetString() ?? "pass");
        }
        else
        {
            using var dflt = JsonDocument.Parse(defaults.GetValueOrDefault("bootstrap.user") ?? "{\"username\":\"admin\",\"password\":\"pass\"}");
            user = (
                dflt.RootElement.GetProperty("username").GetString() ?? "admin",
                dflt.RootElement.GetProperty("password").GetString() ?? "pass");
        }

        bool skipSetPid;
        if (doc.RootElement.TryGetProperty("skip_set_pid", out var skipElem))
        {
            skipSetPid = skipElem.GetBoolean();
        }
        else
        {
            skipSetPid = bool.Parse(defaults.GetValueOrDefault("bootstrap.skip_set_pid") ?? "false");
        }

        return new BootstrapState(patientId, new UserCreds(user.Username, user.Password), skipSetPid);
    }

    private static List<TurnState> MergeTurns(string testTurnsJson, IReadOnlyDictionary<string, string?> defaults)
    {
        using var doc = JsonDocument.Parse(testTurnsJson);
        var result = new List<TurnState>();
        foreach (var elem in doc.RootElement.EnumerateArray())
        {
            string? GetStr(string field, string toggle)
            {
                if (elem.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
                var raw = defaults.GetValueOrDefault(toggle);
                if (string.IsNullOrEmpty(raw) || raw == "null") return null;
                using var d = JsonDocument.Parse(raw);
                return d.RootElement.ValueKind == JsonValueKind.String ? d.RootElement.GetString() : null;
            }

            JsonElement? GetJson(string field, string toggle)
            {
                if (elem.TryGetProperty(field, out var v)) return v.Clone();
                var raw = defaults.GetValueOrDefault(toggle);
                if (string.IsNullOrEmpty(raw) || raw == "null") return null;
                using var d = JsonDocument.Parse(raw);
                return d.RootElement.Clone();
            }

            int delayMs = 0;
            if (elem.TryGetProperty("delay_ms", out var dv) && dv.ValueKind == JsonValueKind.Number)
                delayMs = dv.GetInt32();
            else if (int.TryParse(defaults.GetValueOrDefault("turn.delay_ms"), out var dflt))
                delayMs = dflt;

            result.Add(new TurnState(
                IntentId: GetStr("intent_id", "turn.intent_id") ?? "free_text",
                UserGoal: GetStr("user_goal", "turn.user_goal"),
                SourceId: GetJson("source_id", "turn.source_id"),
                ConversationIdStrategy: GetStr("conversation_id_strategy", "turn.conversation_id_strategy") ?? "share",
                ActivePatientContext: GetStr("active_patient_context", "turn.active_patient_context") ?? "server-session",
                Headers: GetJson("headers", "turn.headers"),
                DelayMs: delayMs,
                ExtraBody: GetJson("extra_body", "turn.extra_body")));
        }
        return result;
    }

    // ------------------------------------------------------------------
    // types
    // ------------------------------------------------------------------
    private sealed record Toggle(string FieldPath, bool IsEnabled, string? DefaultJson);
    private sealed record PenTestRow(int Id, string Category, string Bootstrap, string Turns, string Description);
    private sealed record StepResult(string Method, string Url, int Status, string Body, int ElapsedMs);
    private sealed record UserCreds(string Username, string Password);
    private sealed record BootstrapState(int PatientId, UserCreds User, bool SkipSetPid);
    private sealed record TurnState(
        string IntentId,
        string? UserGoal,
        JsonElement? SourceId,
        string ConversationIdStrategy,
        string ActivePatientContext,
        JsonElement? Headers,
        int DelayMs,
        JsonElement? ExtraBody);
}
