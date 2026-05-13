IF OBJECT_ID(N'dbo.PenetrationTests', N'U') IS NULL
CREATE TABLE dbo.PenetrationTests (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    CreatedAt       DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    Category        NVARCHAR(64)   NOT NULL,    -- jailbreak, phi_leak, ...
    Bootstrap       NVARCHAR(MAX)  NOT NULL,    -- JSON; always has patient_id, plus any toggle-enabled keys
    Turns           NVARCHAR(MAX)  NOT NULL,    -- JSON array; turns carry only enabled-toggle fields
    Description     NVARCHAR(1000) NOT NULL,    -- what the test is trying to break
    CreatedBy       NVARCHAR(64)   NOT NULL DEFAULT N'red_team_agent',
    GeneratorModel  NVARCHAR(128)  NULL         -- OpenRouter model id; NULL for seeds / manual rows
);
