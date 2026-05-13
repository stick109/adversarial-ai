# Invoke AgentForge.Harness once from the command line.
#
# Sets the env vars the harness reads (AGENTFORGE_DB, COPILOT_BASE_URL)
# for this process only, then runs `dotnet run --project src\AgentForge.Harness`.
# The harness picks the oldest unrun penetration test, exercises it
# against the live Co-Pilot, and writes one row to PenetrationTestExecutions.
#
# Usage:
#   .\run-harness.ps1
#   .\run-harness.ps1 -CopilotBaseUrl 'https://my-deploy.example.com'
#   .\run-harness.ps1 -AgentForgeDb 'Server=...;Database=AgentForge;...'

[CmdletBinding()]
param(
    [string]$AgentForgeDb   = 'Server=localhost,14330;Database=AgentForge;User Id=sa;Password=AgentForge!2026;TrustServerCertificate=true',
    [string]$CopilotBaseUrl = 'https://openemr-web-production.up.railway.app'
)

# Note: we don't set $ErrorActionPreference='Stop' here.  In Windows
# PowerShell 5.1, native commands that emit anything to stderr can get
# wrapped in a NativeCommandError and trip the global Stop.  We check
# $LASTEXITCODE explicitly after the dotnet call instead.

$scriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$harnessProj = Join-Path $scriptDir 'src\AgentForge.Harness'

if (-not (Test-Path $harnessProj)) {
    Write-Error "Harness project not found: $harnessProj"
    exit 1
}

Write-Host "==> invoking AgentForge.Harness (one run)" -ForegroundColor Cyan
Write-Host "    AGENTFORGE_DB    = $AgentForgeDb"
Write-Host "    COPILOT_BASE_URL = $CopilotBaseUrl"

$env:AGENTFORGE_DB    = $AgentForgeDb
$env:COPILOT_BASE_URL = $CopilotBaseUrl

# Do NOT pipe `2>&1` here -- Windows PowerShell 5.1 wraps native stderr
# lines into NativeCommandError records and trips $? even on a clean
# exit 0.  Let dotnet write straight to the console.
& dotnet run --project $harnessProj
$exit = $LASTEXITCODE

if ($exit -eq 0) {
    Write-Host "==> harness exited 0" -ForegroundColor Green
} else {
    Write-Host "==> harness exited $exit" -ForegroundColor Yellow
}

exit $exit
