# AgentForge

Two C# console apps and a Razor Pages dashboard, wired around a SQL
Server database. Together they form an iteration of an adversarial
loop against the OpenEMR Clinical Co-Pilot:

| Component             | What it does                                                                              |
| --------------------- | ----------------------------------------------------------------------------------------- |
| `AgentForge.RedTeam`  | Invents **one** new penetration test and writes it to `PenetrationTests`.                 |
| `AgentForge.Harness`  | Picks the oldest unrun test and exercises it against the live Co-Pilot.                   |
| `AgentForge.Web`      | Razor Pages dashboard: shows the four DB tables, starts new RedTeam runs, tracks status.  |

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

That brings the `agentforge-db` container up, waits for SQL Server to
accept connections, applies `db\001_schema.sql`, and prints the table
list plus seed-toggle count. Re-running the script is a no-op (the
schema script is idempotent for both fresh installs and upgrade-in-place).

Then set the env vars you need for the rest of the session:

```powershell
$env:AGENTFORGE_DB       = "Server=localhost,14330;Database=AgentForge;User Id=sa;Password=AgentForge!2026;TrustServerCertificate=true"
$env:COPILOT_BASE_URL    = "https://openemr-web-production.up.railway.app"   # optional; the default already points here
$env:OPENROUTER_API_KEY  = "sk-or-..."                                       # required to invent new tests
```

---

## Running the console apps

```powershell
# invent one new test
dotnet run --project src\AgentForge.RedTeam

# run the oldest unrun test against the live Co-Pilot
dotnet run --project src\AgentForge.Harness
```

Each app does exactly one DB write and exits. A future Orchestrator can
call `RedTeamAgent.RunOnce(...)` / `PenetrationHarness.RunOnce(...)`
directly in-process.

---

## Running the Web dashboard

The dashboard is a Razor Pages app that reads the four DB tables and
exposes one button to start a new RedTeam run.

### Locally (`dotnet run`)

```powershell
dotnet run --project src\AgentForge.Web
# defaults to http://localhost:5024 (see launchSettings.json)
```

### In Docker (`docker compose up`)

```powershell
$env:OPENROUTER_API_KEY = "sk-or-..."   # picked up by docker compose substitution
docker compose up -d --build
# dashboard at http://localhost:5080
# (the schema must already be applied; run .\deploy-sql-schema.ps1 first)
```

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

## Environment variables

| Name                  | Used by | Default                                          | What it is                                                                                  |
| --------------------- | ------- | ------------------------------------------------ | ------------------------------------------------------------------------------------------- |
| `AGENTFORGE_DB`       | all     | *(none — required)*                              | SQL Server connection string for the `AgentForge` DB.                                       |
| `COPILOT_BASE_URL`    | Harness | `https://openemr-web-production.up.railway.app`  | Base URL of the Clinical Co-Pilot.                                                          |
| `OPENROUTER_API_KEY`  | RedTeam, Web | *(none — required to invent new tests)*     | OpenRouter API key; used with `Authorization: Bearer ...` against the chat-completions API. |

Inside the docker network the Web container reaches the database at
`agentforge-db:1433` (the compose file sets that connection string for
the `agentforge-web` service automatically).

---

## Layout

```
AgentForge/
├── AgentForge.sln
├── docker-compose.yml         # agentforge-db + agentforge-web
├── deploy-sql-schema.ps1
├── db/
│   └── 001_schema.sql         # CREATE IF NOT EXISTS + ALTER guards + MERGE seed
├── src/
│   ├── AgentForge.RedTeam/    # console exe; RedTeamAgent.RunOnce
│   ├── AgentForge.Harness/    # console exe; PenetrationHarness.RunOnce
│   └── AgentForge.Web/        # Razor Pages dashboard + Dockerfile
├── evidence/                  # gitignored, mirrors POC-1
└── README.md
```
