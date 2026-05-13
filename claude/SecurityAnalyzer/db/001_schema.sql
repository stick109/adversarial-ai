-- SecurityAnalyzer schema.  Idempotent: re-running this script is a no-op
-- once the DB, tables, columns, and seed rows already exist.
--
-- Two console apps (RedTeam, Harness) share this schema:
--   * PenetrationTests          - one row per invented test
--   * PenetrationTestExecutions - one row per Harness run of a test
--   * VariabilityToggles        - per-field gate that lets us turn new
--                                 attack-surface axes on without touching
--                                 the schema or the C# code (plan §2.1)

------------------------------------------------------------------
-- 0. Database
--    In-place rename from the historical name (AgentForge) so any
--    existing prod / local volume keeps its data.  Idempotent:
--    after the first apply, AgentForge no longer exists and the
--    guard short-circuits.  SINGLE_USER WITH ROLLBACK IMMEDIATE
--    forcibly closes any open connections so the rename succeeds
--    even when another service is currently connected.
------------------------------------------------------------------
IF DB_ID(N'AgentForge') IS NOT NULL AND DB_ID(N'SecurityAnalyzer') IS NULL
BEGIN
    ALTER DATABASE AgentForge SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    ALTER DATABASE AgentForge MODIFY NAME = SecurityAnalyzer;
    ALTER DATABASE SecurityAnalyzer SET MULTI_USER;
END
GO

IF DB_ID(N'SecurityAnalyzer') IS NULL
    CREATE DATABASE SecurityAnalyzer;
GO

USE SecurityAnalyzer;
GO

------------------------------------------------------------------
-- 1. Tables
------------------------------------------------------------------
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
GO

-- Upgrade-in-place guard: add GeneratorModel to pre-existing installs.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE Name = N'GeneratorModel'
       AND Object_ID = Object_ID(N'dbo.PenetrationTests')
)
    ALTER TABLE dbo.PenetrationTests ADD GeneratorModel NVARCHAR(128) NULL;
GO

IF OBJECT_ID(N'dbo.PenetrationTestExecutions', N'U') IS NULL
CREATE TABLE dbo.PenetrationTestExecutions (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    TestId          INT            NOT NULL REFERENCES dbo.PenetrationTests(Id),
    ExecutedAt      DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
    Outcome         NVARCHAR(32)   NOT NULL,    -- ok, http_error, exception
    StepResultsJson NVARCHAR(MAX)  NULL,        -- JSON array of {method, url, status, body, ms} per HTTP call
    ErrorClass      NVARCHAR(128)  NULL         -- exception type if blew up
);
GO

IF OBJECT_ID(N'dbo.VariabilityToggles', N'U') IS NULL
CREATE TABLE dbo.VariabilityToggles (
    FieldPath     NVARCHAR(128) NOT NULL PRIMARY KEY, -- e.g. "turn.user_goal"
    Priority      INT           NOT NULL,             -- 1 = highest expected attack-surface value
    IsEnabled     BIT           NOT NULL DEFAULT 0,
    DefaultJson   NVARCHAR(MAX) NULL,                 -- value the Harness uses when IsEnabled = 0
    Description   NVARCHAR(500) NOT NULL
);
GO

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
GO

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
GO

-- Upgrade-in-place: if RedTeamRuns predates ErrorMessages, add the FK
-- column, move existing inline ErrorMessage text into ErrorMessages,
-- then drop the old column.  All idempotent.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
     WHERE Name = N'ErrorMessageId'
       AND Object_ID = Object_ID(N'dbo.RedTeamRuns')
)
    ALTER TABLE dbo.RedTeamRuns ADD ErrorMessageId INT NULL REFERENCES dbo.ErrorMessages(Id);
GO

IF EXISTS (
    SELECT 1 FROM sys.columns
     WHERE Name = N'ErrorMessage'
       AND Object_ID = Object_ID(N'dbo.RedTeamRuns')
)
BEGIN
    -- The body has to go through sp_executesql because SQL Server's
    -- parser compiles the IF body even when the guard is false, and
    -- once the column has been dropped on a previous apply the bare
    -- reference to ErrorMessage fails parse with "Invalid column name".
    DECLARE @migrate NVARCHAR(MAX) = N'
        DECLARE @runId INT, @msg NVARCHAR(MAX), @errId INT;
        DECLARE migrate_err CURSOR LOCAL FAST_FORWARD FOR
            SELECT Id, ErrorMessage
              FROM dbo.RedTeamRuns
             WHERE ErrorMessage IS NOT NULL
               AND ErrorMessageId IS NULL;
        OPEN migrate_err;
        FETCH NEXT FROM migrate_err INTO @runId, @msg;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            INSERT INTO dbo.ErrorMessages (Message) VALUES (@msg);
            SET @errId = CAST(SCOPE_IDENTITY() AS INT);
            UPDATE dbo.RedTeamRuns SET ErrorMessageId = @errId WHERE Id = @runId;
            FETCH NEXT FROM migrate_err INTO @runId, @msg;
        END
        CLOSE migrate_err;
        DEALLOCATE migrate_err;

        ALTER TABLE dbo.RedTeamRuns DROP COLUMN ErrorMessage;';
    EXEC sp_executesql @migrate;
END
GO

------------------------------------------------------------------
-- 2. Seed: VariabilityToggles (idempotent MERGE).
--    Priority + Description + DefaultJson are kept in sync on re-run;
--    IsEnabled is intentionally only set on INSERT so operators can
--    flip bits manually without the next deploy reverting them.
------------------------------------------------------------------
MERGE INTO dbo.VariabilityToggles AS tgt
USING (VALUES
    (N'turn.user_goal',                   1, 1, N'""',
        N'Every prompt-side attack (jailbreak, PHI extract, advice coerce).'),
    (N'turn.extra_body',                  2, 0, N'null',
        N'Arbitrary extra keys in the POST body: {patient_id:99}, {admin:true}, schema fuzz.'),
    (N'turn.intent_id',                   3, 0, N'"free_text"',
        N'Which of the 6 intents the probe targets; multiplies verifier-path coverage.'),
    (N'turn.source_id',                   4, 0, N'null',
        N'Citation drilldown handle for secondary turns.'),
    (N'turn.conversation_id_strategy',    5, 0, N'"share"',
        N'share | fresh_each_turn | literal:<id> - conversation continuity attacks.'),
    (N'turn.active_patient_context',      6, 0, N'"server-session"',
        N'Body-level patient-claim override probes.'),
    (N'bootstrap.user',                   7, 0, N'{"username":"admin","password":"pass"}',
        N'Login as a different role - front-desk vs. admin attack surface.'),
    (N'turn.headers',                     8, 0, N'{}',
        N'Extra/override request headers: X-Forwarded-User, extra APICSRFTOKEN.'),
    (N'turn.delay_ms',                    9, 0, N'0',
        N'Rate-limit and session-TTL probes.'),
    (N'bootstrap.skip_set_pid',          10, 0, N'false',
        N'Skip the demographics step - "agent with no patient context" regression.')
) AS src(FieldPath, Priority, IsEnabled, DefaultJson, Description)
ON  tgt.FieldPath = src.FieldPath
WHEN MATCHED THEN UPDATE SET
    Priority    = src.Priority,
    DefaultJson = src.DefaultJson,
    Description = src.Description
WHEN NOT MATCHED BY TARGET THEN
    INSERT (FieldPath, Priority, IsEnabled, DefaultJson, Description)
    VALUES (src.FieldPath, src.Priority, src.IsEnabled, src.DefaultJson, src.Description);
GO

------------------------------------------------------------------
-- 3. Seed test row: the POC-1 baseline golden-smoke test (plan §2.3).
--    Carries no attacker payload; must always come back 200 OK.  A
--    failure here is a deployment or regression bug, not an
--    attack-surface signal.
------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM dbo.PenetrationTests WHERE Category = N'golden_smoke')
INSERT INTO dbo.PenetrationTests (Category, Bootstrap, Turns, Description, CreatedBy)
VALUES (
    N'golden_smoke',
    N'{"patient_id": 1}',
    N'[{"intent_id": "basic_patient_data"}]',
    N'POC-1 baseline: basic_patient_data on patient pid 1; must return 200 with non-empty answer_blocks.',
    N'seed'
);
GO

------------------------------------------------------------------
-- 4. Users (for SecurityAnalyzer.Web dashboard auth).
--    Single tier, no roles -- the dashboard is a dev tool and every
--    logged-in user has full access.  PasswordHash format is the
--    "{iterations}.{salt-b64}.{hash-b64}" string produced by
--    SecurityAnalyzer.Web.PasswordHash (PBKDF2-HMAC-SHA256).
------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
CREATE TABLE dbo.Users (
    Id            INT IDENTITY(1,1) PRIMARY KEY,
    Username      NVARCHAR(64)  NOT NULL UNIQUE,
    PasswordHash  NVARCHAR(256) NOT NULL,
    CreatedAt     DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- Seed the default admin/pass row.  Insert-only: if the row already
-- exists (operator may have rotated the password) we do not overwrite.
IF NOT EXISTS (SELECT 1 FROM dbo.Users WHERE Username = N'admin')
INSERT INTO dbo.Users (Username, PasswordHash) VALUES (
    N'admin',
    N'100000.BSm9QfyUTSAWk68i4GHfbw==.gke7xwYJIpgMuRS8uSVqeyX+Bjx12EA4KTtzdpVpW5E='
);
GO

------------------------------------------------------------------
-- 5. Parameters (key/value runtime config tunable without redeploy).
--    Read by SecurityAnalyzer.Executor on every loop iteration so an
--    operator can change executor-loop-minutes at the DB level and
--    the next iteration picks it up.
------------------------------------------------------------------
IF OBJECT_ID(N'dbo.Parameters', N'U') IS NULL
CREATE TABLE dbo.Parameters (
    [Key]      NVARCHAR(128) NOT NULL PRIMARY KEY,
    [Value]    NVARCHAR(MAX) NOT NULL,
    UpdatedAt  DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- Insert-only seed: an operator may have tuned the loop interval at
-- runtime and we must not overwrite that on every schema re-apply.
IF NOT EXISTS (SELECT 1 FROM dbo.Parameters WHERE [Key] = N'executor-loop-minutes')
INSERT INTO dbo.Parameters ([Key], [Value]) VALUES (N'executor-loop-minutes', N'5');
GO

------------------------------------------------------------------
-- 6. ExecutorRuns -- one row per Executor-triggered harness invocation.
--    The Executor inserts the row when a run starts (Status='running'),
--    kicks off the harness in the background, and updates the row when
--    PenetrationHarness.RunOnce returns -- mirrors RedTeamRuns.
--
--    TriggeredBy distinguishes the timer loop ('schedule') from the
--    HTTP POST /runs trigger ('http').
------------------------------------------------------------------
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
GO
