# SecurityAnalyzer

Two C# console apps and two ASP.NET Core services, wired around a SQL
Server database. Together they form an iteration of an adversarial
loop against the OpenEMR Clinical Co-Pilot:

| Component             | What it does                                                                                                                                       |
| --------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| `SecurityAnalyzer.RedTeam`  | Invents **one** new penetration test and writes it to `PenetrationTests`.                                                                          |
| `SecurityAnalyzer.Harness`  | Picks the oldest unrun test and exercises it against the live Co-Pilot.                                                                            |
| `SecurityAnalyzer.Web`      | Razor Pages dashboard: shows the DB tables, starts new RedTeam runs, tracks status.                                                                |
| `SecurityAnalyzer.Executor` | Runs the Harness on a timer (every `executor-loop-minutes` from `dbo.Parameters`); also exposes `POST /runs` to trigger an immediate run.          |

The full specification is in [..\plan.md](..\plan.md). This README only
covers how to build and run.

---

## Prerequisites

- **Docker Desktop** (the schema runs in a containerised SQL Server 2022; the Web app also ships as a container).
- **.NET 9 SDK** if you want to run the apps outside Docker (`dotnet --version` should print `9.x`).
- **sqlcmd** on PATH if you want to use `deploy-sql-schema.ps1` (ships with the SQL Server Client SDK at `C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\`).
- The Clinical Co-Pilot reachable at `https://openemr-web-production.up.railway.app` (or your own deploy URL via `COPILOT_BASE_URL`).

---

## One-time setup

```powershell
# from this directory
.\deploy-sql-schema.ps1
```

That brings the `security-analyzer-db` container up, waits for SQL Server to
accept connections, applies `db\001_schema.sql`, and prints the table
list plus seed-toggle count. Re-running the script is a no-op (the
schema script is idempotent for both fresh installs and upgrade-in-place).

Then set the env vars you need for the rest of the session:

```powershell
$env:SECURITY_ANALYZER_DB       = "Server=localhost,14330;Database=SecurityAnalyzer;User Id=sa;Password=AgentForge!2026;TrustServerCertificate=true"
$env:COPILOT_BASE_URL    = "https://openemr-web-production.up.railway.app"   # optional; the default already points here
$env:OPENROUTER_API_KEY  = "sk-or-..."                                       # required to invent new tests
```

---

## Running the console apps

```powershell
# invent one new test
dotnet run --project SecurityAnalyzer.RedTeam

# run the oldest unrun test against the live Co-Pilot
dotnet run --project SecurityAnalyzer.Harness
```

Each app does exactly one DB write and exits. A future Orchestrator can
call `RedTeamAgent.RunOnce(...)` / `PenetrationHarness.RunOnce(...)`
directly in-process. The Executor (below) already does this for the
harness on a timer.

---

## Running the Web dashboard

The dashboard is a Razor Pages app that reads the DB tables and
exposes one button to start a new RedTeam run.

### Locally (`dotnet run`)

```powershell
dotnet run --project SecurityAnalyzer.Web
# defaults to http://localhost:5024 (see launchSettings.json)
```

### In Docker (`docker compose up`)

```powershell
$env:OPENROUTER_API_KEY = "sk-or-..."   # picked up by docker compose substitution
docker compose up -d --build
# dashboard at http://localhost:5080, executor at http://localhost:5081
```

Three containers come up together: `security-analyzer-db`,
`security-analyzer-web`, `security-analyzer-executor`. The Web app
applies `db/001_schema.sql` automatically on startup (idempotent), so
no separate `deploy-sql-schema.ps1` step is required. Set
`SECURITY_ANALYZER_SKIP_SCHEMA=1` to disable the auto-apply if you'd rather
own the schema externally.

For incremental rebuilds after a code change use the wrapper:

```powershell
.\rebuild-docker-image.ps1                                       # rebuild + restart security-analyzer-web (default)
.\rebuild-docker-image.ps1 -Service security-analyzer-executor
.\rebuild-docker-image.ps1 -Service all
```

(Compose bakes source at build time — a plain `docker compose restart`
reuses the stale image and silently keeps running the old code.)

### On Railway

Live at <https://security-analyzer-web-production.up.railway.app>. The
Railway project `security-analyzer` has three services:

- `security-analyzer-db` — `mcr.microsoft.com/mssql/server:2022-latest`, persistent volume at `/var/opt/mssql`.
- `security-analyzer-web` — built from `SecurityAnalyzer.Web/Dockerfile` (selected per-service via the `RAILWAY_DOCKERFILE_PATH` env var); reaches the DB at `security-analyzer-db.railway.internal:1433`.
- `security-analyzer-executor` — built from `SecurityAnalyzer.Executor/Dockerfile`; same DB hostname; exposes the `POST /runs` trigger publicly (see "Running the Executor" below).

Deploys are manual. From this directory:

```powershell
railway up --service security-analyzer-web      --path-as-root .
railway up --service security-analyzer-executor --path-as-root .
```

`--path-as-root .` is required: without it, the Railway CLI (4.44+) archives
files relative to the git repo root, so `RAILWAY_DOCKERFILE_PATH` resolves
against the wrong prefix and the build errors with
"couldn't locate the dockerfile in code archive".

Env vars on the Web service:

```
SECURITY_ANALYZER_DB              Server=security-analyzer-db.railway.internal,1433;Database=SecurityAnalyzer;User Id=sa;Password=...;TrustServerCertificate=true
COPILOT_BASE_URL           https://openemr-web-production.up.railway.app
OPENROUTER_API_KEY         sk-or-...
RAILWAY_DOCKERFILE_PATH    SecurityAnalyzer.Web/Dockerfile
```

The Executor service has the same `SECURITY_ANALYZER_DB` and `COPILOT_BASE_URL`,
plus `RAILWAY_DOCKERFILE_PATH=SecurityAnalyzer.Executor/Dockerfile`. It
does not need `OPENROUTER_API_KEY` (only RedTeam/Web invent tests).

The Web container references the RedTeam project, so clicking
"Start RedTeam run" in the dashboard:

1. Inserts a row in `RedTeamRuns` with `Status='running'`.
2. Returns the row id immediately and redirects to `/Run/{id}`.
3. Calls `RedTeamAgent.RunOnce(...)` on a background `Task.Run`.
4. Updates the same `RedTeamRuns` row when it finishes
   (`Status='ok'`/`'failed'`, `FinishedAt`, `ResultTestId`).

The `/Run/{id}` page auto-refreshes every 2 seconds while the run is in
flight (via meta refresh — no JS), then renders the inserted
`PenetrationTests` row once the background task completes.

---

## Running the Executor

`SecurityAnalyzer.Executor` is an ASP.NET Core service that calls
`PenetrationHarness.RunOnce` two ways:

- **Schedule** — a `BackgroundService` re-reads `dbo.Parameters` for
  key `executor-loop-minutes` (seeded to `5`) on every iteration, so
  the interval is tunable at runtime without redeploying. Update the
  value:

  ```sql
  UPDATE dbo.Parameters SET [Value] = N'2' WHERE [Key] = N'executor-loop-minutes';
  ```

- **HTTP** — `POST /runs` returns `202 Accepted` with body
  `{"executorRunId":N}` immediately, then runs the harness on a
  thread-pool task. Useful for one-off triggers between schedule ticks.

Each invocation inserts one row into `dbo.ExecutorRuns` with
`Status='running'` and `TriggeredBy='schedule'` / `'http'`. When the
background task finishes it updates the row with `FinishedAt`,
`Status='ok'`/`'failed'`, `ExitCode`, the FK to the
`dbo.PenetrationTestExecutions` row it produced, and (on exception)
an FK to `dbo.ErrorMessages`.

Trigger an immediate run:

```powershell
# local
Invoke-WebRequest -Uri http://localhost:5081/runs -Method POST -UseBasicParsing

# Railway
Invoke-WebRequest -Uri https://security-analyzer-executor-production.up.railway.app/runs -Method POST -UseBasicParsing
```

---

## Environment variables

| Name                  | Used by                  | Default                                          | What it is                                                                                  |
| --------------------- | ------------------------ | ------------------------------------------------ | ------------------------------------------------------------------------------------------- |
| `SECURITY_ANALYZER_DB`       | all                      | *(none — required)*                              | SQL Server connection string for the `SecurityAnalyzer` DB.                                       |
| `COPILOT_BASE_URL`    | Harness, Executor        | `https://openemr-web-production.up.railway.app`  | Base URL of the Clinical Co-Pilot.                                                          |
| `OPENROUTER_API_KEY`  | RedTeam, Web             | *(none — required to invent new tests)*          | OpenRouter API key; used with `Authorization: Bearer ...` against the chat-completions API. |
| `SECURITY_ANALYZER_SKIP_SCHEMA` | Web                   | `0`                                              | Set to `1` to skip the Web container's startup schema apply (e.g. when an external operator owns the schema). |

Inside the docker network the Web and Executor containers reach the
database at `security-analyzer-db:1433` (the compose file sets that
connection string for both services automatically).

---

## Layout

```
SecurityAnalyzer/
├── SecurityAnalyzer.sln
├── docker-compose.yml         # security-analyzer-db + -web + -executor
├── deploy-sql-schema.ps1      # one-shot schema deployer via host sqlcmd
├── rebuild-docker-image.ps1   # stop -> build -> up -d wrapper
├── run-harness.ps1            # one-shot harness invocation outside Docker
├── railway.json               # restart policy only; Dockerfile per service via env var
├── db/
│   └── 001_schema.sql         # CREATE IF NOT EXISTS + ALTER guards + MERGE seed
├── SecurityAnalyzer.RedTeam/        # console exe; RedTeamAgent.RunOnce
├── SecurityAnalyzer.Harness/        # console exe; PenetrationHarness.RunOnce
├── SecurityAnalyzer.Web/            # Razor Pages dashboard + Dockerfile
├── SecurityAnalyzer.Executor/       # Harness scheduler + POST /runs + Dockerfile
├── evidence/                  # gitignored, mirrors POC-1
└── README.md
```
