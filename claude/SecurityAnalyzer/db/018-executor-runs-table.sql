-- ExecutorRuns -- one row per Executor-triggered harness invocation.
-- The Executor inserts the row when a run starts (Status='running'),
-- kicks off the harness in the background, and updates the row when
-- PenetrationHarness.RunOnce returns -- mirrors RedTeamRuns.
--
-- TriggeredBy distinguishes the timer loop ('schedule') from the
-- HTTP POST /runs trigger ('http').
IF OBJECT_ID(N'dbo.ExecutorRuns', N'U') IS NULL
CREATE TABLE dbo.ExecutorRuns (
    Id                          INT IDENTITY(1,1) PRIMARY KEY,
    StartedAt                   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    FinishedAt                  DATETIME2     NULL,
    Status                      NVARCHAR(16)  NOT NULL DEFAULT N'running',  -- running, ok, failed
    TriggeredBy                 NVARCHAR(16)  NOT NULL,                     -- schedule, http
    ExitCode                    INT           NULL,
    PenetrationTestExecutionId  INT           NULL REFERENCES dbo.PenetrationTestExecutions(Id),
    ErrorMessageId              INT           NULL REFERENCES dbo.ErrorMessages(Id)
);
