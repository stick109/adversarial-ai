return SecurityAnalyzer.Harness.PenetrationHarness.RunOnce(
    Environment.GetEnvironmentVariable("SECURITY_ANALYZER_DB")!,
    Environment.GetEnvironmentVariable("COPILOT_BASE_URL")
        ?? "https://openemr-web-production.up.railway.app");
