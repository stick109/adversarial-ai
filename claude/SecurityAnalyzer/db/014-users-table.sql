-- Users (for SecurityAnalyzer.Web dashboard auth).
-- Single tier, no roles -- the dashboard is a dev tool and every
-- logged-in user has full access.  PasswordHash format is the
-- "{iterations}.{salt-b64}.{hash-b64}" string produced by
-- SecurityAnalyzer.Web.PasswordHash (PBKDF2-HMAC-SHA256).
IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
CREATE TABLE dbo.Users (
    Id            INT IDENTITY(1,1) PRIMARY KEY,
    Username      NVARCHAR(64)  NOT NULL UNIQUE,
    PasswordHash  NVARCHAR(256) NOT NULL,
    CreatedAt     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
