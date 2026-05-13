-- Rename the SecurityAnalyzer DB's *logical* file names from AgentForge
-- to SecurityAnalyzer.  This is an online ALTER DATABASE MODIFY FILE
-- NEWNAME, so no xp_cmdshell or shell access is required.
--
-- Renaming the *physical* .mdf/.ldf files on disk is NOT done here,
-- because that needs either (a) xp_cmdshell, which Railway's SQL Server
-- edition does not support (sp_configure errors with "option not
-- supported by this edition"), or (b) shell access to the DB container,
-- which we don't have over the standard SQL connection.  The physical
-- file names (still AgentForge.mdf / AgentForge_log.ldf on Railway) are
-- internal to SQL Server and never referenced by the application code,
-- so this leftover is purely cosmetic on the data volume.
--
-- Recovery clause: a prior version of this script tried to do the full
-- rename via xp_cmdshell on Railway; when xp_cmdshell was blocked, the
-- script still updated FILENAME metadata to point at SecurityAnalyzer.*
-- paths that don't exist on disk, leaving the DB in RECOVERY_PENDING.
-- The first IF detects that broken state (DB not ONLINE) and points
-- FILENAME back at AgentForge.* so SET ONLINE succeeds.  Idempotent on
-- the state_desc guard; on healthy installs it short-circuits.

-- Recovery: if a prior FILENAME update made SecurityAnalyzer
-- inaccessible, point FILENAME back at the AgentForge.* files that
-- actually exist on disk, and bring the DB online.
IF DB_ID(N'SecurityAnalyzer') IS NOT NULL
   AND EXISTS (
       SELECT 1 FROM sys.databases
        WHERE name = N'SecurityAnalyzer' AND state_desc <> N'ONLINE'
   )
BEGIN
    ALTER DATABASE [SecurityAnalyzer] MODIFY FILE (
        NAME = N'SecurityAnalyzer',
        FILENAME = N'/var/opt/mssql/data/AgentForge.mdf'
    );
    ALTER DATABASE [SecurityAnalyzer] MODIFY FILE (
        NAME = N'SecurityAnalyzer_log',
        FILENAME = N'/var/opt/mssql/data/AgentForge_log.ldf'
    );
    ALTER DATABASE [SecurityAnalyzer] SET ONLINE;
END

-- Logical name rename: AgentForge / AgentForge_log -> SecurityAnalyzer
-- / SecurityAnalyzer_log.  Online operation.  Idempotent on the LIKE
-- guard; local (renamed manually) and fresh installs skip.
IF DB_ID(N'SecurityAnalyzer') IS NOT NULL
   AND EXISTS (
       SELECT 1 FROM sys.master_files
        WHERE database_id = DB_ID(N'SecurityAnalyzer')
          AND name LIKE N'AgentForge%'
   )
BEGIN
    ALTER DATABASE [SecurityAnalyzer] MODIFY FILE (NAME = N'AgentForge',     NEWNAME = N'SecurityAnalyzer');
    ALTER DATABASE [SecurityAnalyzer] MODIFY FILE (NAME = N'AgentForge_log', NEWNAME = N'SecurityAnalyzer_log');
END
