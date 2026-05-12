return AgentForge.RedTeam.RedTeamAgent.RunOnce(
    Environment.GetEnvironmentVariable("AGENTFORGE_DB")!,
    Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"));
