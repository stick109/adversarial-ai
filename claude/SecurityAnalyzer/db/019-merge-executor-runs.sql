-- Absorb ExecutorRuns into PenetrationTestExecutions so there is one
-- row (and one Id) per harness invocation.  Before this migration the
-- harness inserted a PenetrationTestExecutions row AFTER the test ran,
-- and the Executor wrapped that call with its own ExecutorRuns row
-- (lifecycle + trigger source).  The --once path skipped the wrapper,
-- producing PTE rows with no matching ExecutorRuns row.
--
-- New model: the harness inserts the PTE row up front with Outcome
-- 'running' + TriggeredBy ('schedule' | 'http' | 'direct'), then
-- updates it when the test finishes.  ExecutorRuns is dropped.
--
-- This file is idempotent: re-running on a fully-migrated DB is a
-- no-op.

-- 1. Add the lifecycle columns to PenetrationTestExecutions (nullable
--    so we can backfill from ExecutorRuns / existing rows first).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'StartedAt' AND Object_ID = OBJECT_ID(N'dbo.PenetrationTestExecutions'))
    ALTER TABLE dbo.PenetrationTestExecutions ADD StartedAt DATETIME2 NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'FinishedAt' AND Object_ID = OBJECT_ID(N'dbo.PenetrationTestExecutions'))
    ALTER TABLE dbo.PenetrationTestExecutions ADD FinishedAt DATETIME2 NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'TriggeredBy' AND Object_ID = OBJECT_ID(N'dbo.PenetrationTestExecutions'))
    ALTER TABLE dbo.PenetrationTestExecutions ADD TriggeredBy NVARCHAR(16) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'ExitCode' AND Object_ID = OBJECT_ID(N'dbo.PenetrationTestExecutions'))
    ALTER TABLE dbo.PenetrationTestExecutions ADD ExitCode INT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'ErrorMessageId' AND Object_ID = OBJECT_ID(N'dbo.PenetrationTestExecutions'))
    ALTER TABLE dbo.PenetrationTestExecutions ADD ErrorMessageId INT NULL REFERENCES dbo.ErrorMessages(Id);
GO

-- 2. Backfill from ExecutorRuns where there is a linked PTE row.
IF OBJECT_ID(N'dbo.ExecutorRuns', N'U') IS NOT NULL
BEGIN
    UPDATE pte
       SET StartedAt      = er.StartedAt,
           FinishedAt     = er.FinishedAt,
           TriggeredBy    = er.TriggeredBy,
           ExitCode       = er.ExitCode,
           ErrorMessageId = er.ErrorMessageId
      FROM dbo.PenetrationTestExecutions pte
      INNER JOIN dbo.ExecutorRuns er ON er.PenetrationTestExecutionId = pte.Id
     WHERE pte.StartedAt IS NULL;
END
GO

-- 3. Backfill the rest -- rows produced outside the Executor wrapper
--    (old harness CLI runs, --once invocations).  StartedAt/FinishedAt
--    fall back to ExecutedAt; TriggeredBy is set to 'direct'.
UPDATE dbo.PenetrationTestExecutions
   SET StartedAt   = ISNULL(StartedAt, ExecutedAt),
       FinishedAt  = ISNULL(FinishedAt, ExecutedAt),
       TriggeredBy = ISNULL(TriggeredBy, N'direct')
 WHERE StartedAt IS NULL OR FinishedAt IS NULL OR TriggeredBy IS NULL;
GO

-- 4. Tighten NOT NULL on the columns that should always be populated
--    going forward.  StartedAt is always set on INSERT; TriggeredBy is
--    always passed by the caller.  FinishedAt/ExitCode/ErrorMessageId
--    stay nullable so the running-but-not-finished state is expressible.
IF EXISTS (SELECT 1 FROM sys.columns
            WHERE Name = N'StartedAt' AND Object_ID = OBJECT_ID(N'dbo.PenetrationTestExecutions')
              AND is_nullable = 1)
    ALTER TABLE dbo.PenetrationTestExecutions ALTER COLUMN StartedAt DATETIME2 NOT NULL;
GO

IF EXISTS (SELECT 1 FROM sys.columns
            WHERE Name = N'TriggeredBy' AND Object_ID = OBJECT_ID(N'dbo.PenetrationTestExecutions')
              AND is_nullable = 1)
    ALTER TABLE dbo.PenetrationTestExecutions ALTER COLUMN TriggeredBy NVARCHAR(16) NOT NULL;
GO

-- 5. Drop the (now-redundant) ExecutorRuns table.  All data has been
--    migrated above.
IF OBJECT_ID(N'dbo.ExecutorRuns', N'U') IS NOT NULL
    DROP TABLE dbo.ExecutorRuns;
GO
