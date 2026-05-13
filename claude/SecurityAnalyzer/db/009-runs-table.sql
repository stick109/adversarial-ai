-- One row per UI-triggered Red Team invocation.  The Web app inserts
-- the row at the moment the user clicks "Start", returns the runId, and
-- updates the row from a background task once RedTeamAgent.RunOnce
-- completes.  Lets the UI render an honest "running / ok / failed"
-- status without polling the LLM client.
--
-- ErrorMessageId points at dbo.ErrorMessages -- see comment above.
IF OBJECT_ID(N'dbo.RedTeamRuns', N'U') IS NULL
CREATE TABLE dbo.RedTeamRuns (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    StartedAt       DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    FinishedAt      DATETIME2     NULL,
    Status          NVARCHAR(16)  NOT NULL DEFAULT N'running',  -- running, ok, failed
    ExitCode        INT           NULL,                          -- value RunOnce returned
    ResultTestId    INT           NULL REFERENCES dbo.PenetrationTests(Id),
    ErrorMessageId  INT           NULL REFERENCES dbo.ErrorMessages(Id)
);
