IF OBJECT_ID(N'dbo.PenetrationTestExecutions', N'U') IS NULL
CREATE TABLE dbo.PenetrationTestExecutions (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    TestId          INT            NOT NULL REFERENCES dbo.PenetrationTests(Id),
    ExecutedAt      DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    Outcome         NVARCHAR(32)   NOT NULL,    -- ok, http_error, exception
    StepResultsJson NVARCHAR(MAX)  NULL,        -- JSON array of {method, url, status, body, ms} per HTTP call
    ErrorClass      NVARCHAR(128)  NULL         -- exception type if blew up
);
