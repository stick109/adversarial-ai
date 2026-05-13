-- Error messages live in their own table so dashboard queries that
-- list runs (or executions, in the future) only have to select a small
-- INT FK.  The full text -- which can be a multi-KB OpenRouter
-- response or a stack trace -- is fetched only when the user clicks
-- through to the per-error page.
IF OBJECT_ID(N'dbo.ErrorMessages', N'U') IS NULL
CREATE TABLE dbo.ErrorMessages (
    Id          INT IDENTITY(1,1) PRIMARY KEY,
    CreatedAt   DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
    Message     NVARCHAR(MAX) NOT NULL
);
