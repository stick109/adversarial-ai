return SecurityAnalyzer.RedTeam.RedTeamAgent.RunOnce(
    Environment.GetEnvironmentVariable("SECURITY_ANALYZER_DB")!,
    Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"));
