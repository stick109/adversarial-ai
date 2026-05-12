# AgentForge

Two C# console apps wired around a SQL Server database. Together they
form a single iteration of an adversarial loop against the OpenEMR
Clinical Co-Pilot:

| App                   | What it does                                                                 |
| --------------------- | ---------------------------------------------------------------------------- |
| `AgentForge.RedTeam`  | Invents **one** new penetration test and writes it to `PenetrationTests`.    |
| `AgentForge.Harness`  | Picks the oldest unrun test and exercises it against the live Co-Pilot.      |

The full specification is in [..\plan.md](..\plan.md). This README only
covers how to build and run.

---

## Prerequisites

- **Docker Desktop** (the schema runs in a containerised SQL Server 2022).
- **.NET 9 SDK** (`dotnet --version` should print `9.x`).
- **sqlcmd** on PATH (the schema deployer shells out to it; ships with
  the SQL Server Client SDK at
  `C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\`).
- The Clinical Co-Pilot reachable at
  `https://openemr-web-production.up.railway.app` (or your own deploy
  URL via `COPILOT_BASE_URL`).

---

## One-time setup

```powershell
# from this directory
.\deploy-sql-schema.ps1
```

That brings the `agentforge-db` container up, waits for SQL Server to
accept connections, applies `db\001_schema.sql`, and prints the table
list plus seed-toggle count. Re-running the script is a no-op.

Then set the env vars you need for the rest of the session:

```powershell
$env:AGENTFORGE_DB       = "Server=localhost,14330;Database=AgentForge;User Id=sa;Password=AgentForge!2026;TrustServerCertificate=true"
$env:COPILOT_BASE_URL    = "https://openemr-web-production.up.railway.app"   # optional; the default already points here
$env:OPENROUTER_API_KEY  = "sk-or-..."                                       # RedTeam: required to invent new tests
```

---

## Running each app

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

## Environment variables

| Name                  | Used by | Default                                          | What it is                                                                                |
| --------------------- | ------- | ------------------------------------------------ | ----------------------------------------------------------------------------------------- |
| `AGENTFORGE_DB`       | both    | *(none — required)*                              | SQL Server connection string for the `AgentForge` DB.                                     |
| `COPILOT_BASE_URL`    | Harness | `https://openemr-web-production.up.railway.app`  | Base URL of the Clinical Co-Pilot.                                                        |
| `OPENROUTER_API_KEY`  | RedTeam | *(none — required to invent new tests)*          | OpenRouter API key; used with `Authorization: Bearer ...` against the chat-completions API. |

---

## Layout

```
AgentForge/
├── AgentForge.sln
├── docker-compose.yml
├── deploy-sql-schema.ps1
├── db/
│   └── 001_schema.sql            # CREATE IF NOT EXISTS + MERGE seed
├── src/
│   ├── AgentForge.RedTeam/
│   │   ├── Program.cs
│   │   └── RedTeamAgent.cs
│   └── AgentForge.Harness/
│       ├── Program.cs
│       └── PenetrationHarness.cs
├── evidence/                     # gitignored, mirrors POC-1
└── README.md
```
