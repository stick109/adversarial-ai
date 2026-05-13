# Idempotent schema deployer for SecurityAnalyzer.
#
# Brings the security-analyzer-db container up if it isn't already,
# waits for SQL Server to accept connections, then applies
# db\001_schema.sql via sqlcmd.  Both operations are safe to re-run --
# compose is a no-op when the container is already healthy, and the
# schema script uses IF NOT EXISTS guards plus a MERGE seed.
#
# Requirements: Docker Desktop, sqlcmd on PATH.

[CmdletBinding()]
param(
    [string]$SaPassword = 'AgentForge!2026',
    [string]$Server     = 'localhost,14330',
    [int]   $WaitSeconds = 60
)

# Note: we don't set $ErrorActionPreference='Stop' here.  In Windows
# PowerShell 5.1, native commands that emit anything to stderr (e.g.
# docker printing "Container ... Running" as an info line) get wrapped
# in a NativeCommandError and trip the global Stop.  We check
# $LASTEXITCODE explicitly after each native call instead.

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$schemaFile = Join-Path $scriptDir 'db\001_schema.sql'
$composeFile = Join-Path $scriptDir 'docker-compose.yml'

if (-not (Test-Path $schemaFile)) {
    Write-Error "Schema file not found: $schemaFile"
    exit 1
}

Write-Host "==> docker compose up -d security-analyzer-db" -ForegroundColor Cyan
& docker compose -f $composeFile up -d security-analyzer-db 2>&1 | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) {
    Write-Error "docker compose up failed (exit $LASTEXITCODE)"
    exit 1
}

Write-Host "==> waiting for SQL Server to accept connections (max ${WaitSeconds}s)" -ForegroundColor Cyan
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$ready = $false
while ((Get-Date) -lt $deadline) {
    # -C trusts the self-signed cert that ships in the container image.
    # -l 3 sets a short login timeout so we poll quickly.
    & sqlcmd -S $Server -U sa -P $SaPassword -C -l 3 -Q "SELECT 1" 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { $ready = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $ready) {
    Write-Error "SQL Server did not accept connections within ${WaitSeconds}s"
    exit 1
}
Write-Host "    ready" -ForegroundColor Green

Write-Host "==> applying schema: $schemaFile" -ForegroundColor Cyan
& sqlcmd -S $Server -U sa -P $SaPassword -C -b -i $schemaFile
if ($LASTEXITCODE -ne 0) {
    Write-Error "sqlcmd apply failed (exit $LASTEXITCODE)"
    exit 1
}

Write-Host "==> verifying objects" -ForegroundColor Cyan
$verify = @'
USE SecurityAnalyzer;
SELECT name FROM sys.tables ORDER BY name;
SELECT COUNT(*) AS toggle_count FROM dbo.VariabilityToggles;
'@
& sqlcmd -S $Server -U sa -P $SaPassword -C -b -d SecurityAnalyzer -Q $verify
if ($LASTEXITCODE -ne 0) {
    Write-Error "verification query failed (exit $LASTEXITCODE)"
    exit 1
}

Write-Host "==> schema deployment complete" -ForegroundColor Green
