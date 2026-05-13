# Invoke SecurityAnalyzer.Harness once from the command line.
#
# Sets the env vars the harness reads (SECURITY_ANALYZER_DB, COPILOT_BASE_URL)
# for this process only, then runs `dotnet run --project SecurityAnalyzer.Harness`.
# The harness picks the oldest unrun penetration test, exercises it
# against the live Co-Pilot, and writes one row to PenetrationTestExecutions.
#
# Usage:
#   .\run-harness.ps1
#   .\run-harness.ps1 -CopilotBaseUrl 'https://my-deploy.example.com'
#   .\run-harness.ps1 -SecurityAnalyzerDb 'Server=...;Database=SecurityAnalyzer;...'

[CmdletBinding()]
param(
    [string]$SecurityAnalyzerDb   = 'Server=localhost,14330;Database=SecurityAnalyzer;User Id=sa;Password=AgentForge!2026;TrustServerCertificate=true',
    [string]$CopilotBaseUrl = 'https://openemr-web-production.up.railway.app'
)

# Note: we don't set $ErrorActionPreference='Stop' here.  In Windows
# PowerShell 5.1, native commands that emit anything to stderr can get
# wrapped in a NativeCommandError and trip the global Stop.  We check
# $LASTEXITCODE explicitly after the dotnet call instead.

$scriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$harnessProj = Join-Path $scriptDir 'SecurityAnalyzer.Harness'

if (-not (Test-Path $harnessProj)) {
    Write-Error "Harness project not found: $harnessProj"
    exit 1
}

Write-Host "==> invoking SecurityAnalyzer.Harness (one run)" -ForegroundColor Cyan
Write-Host "    SECURITY_ANALYZER_DB    = $SecurityAnalyzerDb"
Write-Host "    COPILOT_BASE_URL = $CopilotBaseUrl"

$env:SECURITY_ANALYZER_DB    = $SecurityAnalyzerDb
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
