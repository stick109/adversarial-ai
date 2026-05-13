-- One-off in-place rename of the SQL Server data files from
-- AgentForge.mdf / AgentForge_log.ldf to SecurityAnalyzer.mdf /
-- SecurityAnalyzer_log.ldf.  Paired with 001-rename-db.sql, which only
-- changes the *logical* DB name; this script changes the logical file
-- names plus the physical paths on disk so nothing about the old name
-- remains.  Idempotent: after the first apply the guard sees the file
-- logical names are already SecurityAnalyzer* and short-circuits, so
-- this is safe to re-run on every container start (including fresh
-- installs, where SecurityAnalyzer does not yet exist and the IF
-- short-circuits on the DB_ID check).
--
-- Mechanism: enable xp_cmdshell briefly, take the DB offline so its
-- file handles are released, mv the files on disk, point the FILENAME
-- metadata at the new paths, bring the DB back online, disable
-- xp_cmdshell.  Path /var/opt/mssql/data/ is the standard mssql image
-- layout and is identical between local docker compose and Railway.
IF DB_ID(N'SecurityAnalyzer') IS NOT NULL
   AND EXISTS (
       SELECT 1 FROM sys.master_files
        WHERE database_id = DB_ID(N'SecurityAnalyzer')
          AND name LIKE N'AgentForge%'
   )
BEGIN
    -- Rename the logical file names; this is an online operation.
    ALTER DATABASE [SecurityAnalyzer] MODIFY FILE (NAME = N'AgentForge',     NEWNAME = N'SecurityAnalyzer');
    ALTER DATABASE [SecurityAnalyzer] MODIFY FILE (NAME = N'AgentForge_log', NEWNAME = N'SecurityAnalyzer_log');

    -- Enable xp_cmdshell so we can issue mv from inside the DB engine.
    EXEC sp_configure 'show advanced options', 1; RECONFIGURE;
    EXEC sp_configure 'xp_cmdshell', 1; RECONFIGURE;

    -- Offline -> mv -> point FILENAME at the new paths -> online.
    ALTER DATABASE [SecurityAnalyzer] SET OFFLINE WITH ROLLBACK IMMEDIATE;
    EXEC xp_cmdshell 'mv /var/opt/mssql/data/AgentForge.mdf     /var/opt/mssql/data/SecurityAnalyzer.mdf';
    EXEC xp_cmdshell 'mv /var/opt/mssql/data/AgentForge_log.ldf /var/opt/mssql/data/SecurityAnalyzer_log.ldf';
    ALTER DATABASE [SecurityAnalyzer] MODIFY FILE (NAME = N'SecurityAnalyzer',     FILENAME = N'/var/opt/mssql/data/SecurityAnalyzer.mdf');
    ALTER DATABASE [SecurityAnalyzer] MODIFY FILE (NAME = N'SecurityAnalyzer_log', FILENAME = N'/var/opt/mssql/data/SecurityAnalyzer_log.ldf');
    ALTER DATABASE [SecurityAnalyzer] SET ONLINE;

    -- Lock xp_cmdshell back down.
    EXEC sp_configure 'xp_cmdshell', 0; RECONFIGURE;
    EXEC sp_configure 'show advanced options', 0; RECONFIGURE;
END
