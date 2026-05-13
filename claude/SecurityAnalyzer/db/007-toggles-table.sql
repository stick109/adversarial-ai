IF OBJECT_ID(N'dbo.VariabilityToggles', N'U') IS NULL
CREATE TABLE dbo.VariabilityToggles (
    FieldPath     NVARCHAR(128) NOT NULL PRIMARY KEY, -- e.g. "turn.user_goal"
    Priority      INT           NOT NULL,             -- 1 = highest expected attack-surface value
    IsEnabled     BIT           NOT NULL DEFAULT 0,
    DefaultJson   NVARCHAR(MAX) NULL,                 -- value the Harness uses when IsEnabled = 0
    Description   NVARCHAR(500) NOT NULL
);
