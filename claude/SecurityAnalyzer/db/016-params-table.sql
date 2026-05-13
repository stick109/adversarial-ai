-- Parameters (key/value runtime config tunable without redeploy).
-- Read by SecurityAnalyzer.Executor on every loop iteration so an
-- operator can change executor-loop-minutes at the DB level and
-- the next iteration picks it up.
IF OBJECT_ID(N'dbo.Parameters', N'U') IS NULL
CREATE TABLE dbo.Parameters (
    [Key]      NVARCHAR(128) NOT NULL PRIMARY KEY,
    [Value]    NVARCHAR(MAX) NOT NULL,
    UpdatedAt  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
