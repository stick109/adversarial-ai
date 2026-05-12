return AgentForge.Harness.PenetrationHarness.RunOnce(
    Environment.GetEnvironmentVariable("AGENTFORGE_DB")!,
    Environment.GetEnvironmentVariable("COPILOT_BASE_URL")
        ?? "https://openemr-web-production.up.railway.app");
