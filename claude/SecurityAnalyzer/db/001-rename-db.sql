-- In-place rename from the historical name (AgentForge) so any
-- existing prod / local volume keeps its data.  Idempotent: after the
-- first apply, AgentForge no longer exists and the guard short-circuits.
-- SINGLE_USER WITH ROLLBACK IMMEDIATE forcibly closes any open
-- connections so the rename succeeds even when another service is
-- currently connected.
IF DB_ID(N'AgentForge') IS NOT NULL AND DB_ID(N'SecurityAnalyzer') IS NULL
BEGIN
    ALTER DATABASE AgentForge SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    ALTER DATABASE AgentForge MODIFY NAME = SecurityAnalyzer;
    ALTER DATABASE SecurityAnalyzer SET MULTI_USER;
END
