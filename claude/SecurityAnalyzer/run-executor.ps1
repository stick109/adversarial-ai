# Invoke PenetrationHarness.RunOnce once from the command line.
#
# Calls `dotnet run --project SecurityAnalyzer.Executor -- --once`, which
# bypasses the executor's web host + scheduler and invokes the
# PenetrationHarness static method directly in-process.  The harness
# picks the oldest unrun penetration test, exercises it against the
# live Co-Pilot, and writes one row to PenetrationTestExecutions.  No
# ExecutorRuns row is written (the scheduler + POST /runs path are the
# ones that track lifecycle).
#
# Usage:
#   .\run-executor.ps1
#   .\run-executor.ps1 -CopilotBaseUrl 'https://my-deploy.example.com'
#   .\run-executor.ps1 -SecurityAnalyzerDb 'Server=...;Database=SecurityAnalyzer;...'

[CmdletBinding()]
param(
    [string]$SecurityAnalyzerDb = 'Server=localhost,14330;Database=SecurityAnalyzer;User Id=sa;Password=AgentForge!2026;TrustServerCertificate=true',
    [string]$CopilotBaseUrl     = 'https://openemr-web-production.up.railway.app'
)

# Note: we don't set $ErrorActionPreference='Stop' here.  In Windows
# PowerShell 5.1, native commands that emit anything to stderr can get
# wrapped in a NativeCommandError and trip the global Stop.  We check
# $LASTEXITCODE explicitly after the dotnet call instead.

$scriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$executorProj = Join-Path $scriptDir 'SecurityAnalyzer.Executor'

if (-not (Test-Path $executorProj)) {
    Write-Error "Executor project not found: $executorProj"
    exit 1
}

Write-Host "==> invoking PenetrationHarness.RunOnce (one run) via SecurityAnalyzer.Executor --once" -ForegroundColor Cyan
Write-Host "    SECURITY_ANALYZER_DB = $SecurityAnalyzerDb"
Write-Host "    COPILOT_BASE_URL     = $CopilotBaseUrl"

$env:SECURITY_ANALYZER_DB = $SecurityAnalyzerDb
$env:COPILOT_BASE_URL     = $CopilotBaseUrl

# Do NOT pipe `2>&1` here -- Windows PowerShell 5.1 wraps native stderr
# lines into NativeCommandError records and trips $? even on a clean
# exit 0.  Let dotnet write straight to the console.
& dotnet run --project $executorProj -- --once
$exit = $LASTEXITCODE

if ($exit -eq 0) {
    Write-Host "==> harness exited 0" -ForegroundColor Green
} else {
    Write-Host "==> harness exited $exit" -ForegroundColor Yellow
}

exit $exit
