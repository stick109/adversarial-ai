-- Two responsibilities, both idempotent:
--
--   1. If SecurityAnalyzer exists but is NOT online (e.g. RECOVERY_PENDING
--      because an earlier migration pointed FILENAME at .mdf/.ldf paths
--      that are not on disk), drop it.  002-create-db.sql will recreate
--      it cleanly with default file names (SecurityAnalyzer.mdf /
--      SecurityAnalyzer_log.ldf), and the rest of the schema fills in
--      the tables and seeds.  This is the data-loss recovery path -- we
--      use it only when the DB is already inaccessible.
--
--   2. If SecurityAnalyzer is online and healthy but its logical file
--      names are still AgentForge / AgentForge_log (the case on a volume
--      that was renamed from the old AgentForge DB in place), rename the
--      logical names to match.  This is an online ALTER MODIFY FILE
--      NEWNAME, no xp_cmdshell required.  We do NOT touch FILENAME or
--      move physical files: doing that needs either xp_cmdshell (not
--      supported on Railway's SQL edition) or shell access to the DB
--      container, and the physical file names are internal to SQL
--      Server -- the application never references them.
--
-- Healthy installs (local that already has SecurityAnalyzer.* logical
-- names, and fresh installs where SecurityAnalyzer does not exist yet)
-- short-circuit on both guards.

-- 1. Drop a broken SecurityAnalyzer so create-db can rebuild it.
IF DB_ID(N'SecurityAnalyzer') IS NOT NULL
   AND EXISTS (
       SELECT 1 FROM sys.databases
        WHERE name = N'SecurityAnalyzer' AND state_desc <> N'ONLINE'
   )
BEGIN
    ALTER DATABASE [SecurityAnalyzer] SET OFFLINE WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [SecurityAnalyzer];
END

-- 2. Logical file rename for SecurityAnalyzer DBs that came from the
-- AgentForge in-place rename and still carry the old logical names.
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
