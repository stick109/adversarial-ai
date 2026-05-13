# Trigger one immediate run on the SecurityAnalyzer.Executor container.
#
# Posts to /runs on the running executor service (default localhost:5081
# via docker-compose).  The executor inserts an ExecutorRuns row in
# 'running' state, runs PenetrationHarness.RunOnce on a worker thread,
# then updates the row with FinishedAt/Status/ExitCode and a link to the
# PenetrationTestExecutions row it produced.  The HTTP call itself
# returns 202 immediately; this script then polls ExecutorRuns until the
# run completes (or hits the timeout) and reports the final status.
#
# Usage:
#   .\run-executor.ps1
#   .\run-executor.ps1 -ExecutorBaseUrl 'http://localhost:5081'
#   .\run-executor.ps1 -SecurityAnalyzerDb 'Server=...;Database=SecurityAnalyzer;...' -TimeoutSeconds 300

[CmdletBinding()]
param(
    [string]$ExecutorBaseUrl    = 'http://localhost:5081',
    [string]$SecurityAnalyzerDb = 'Server=localhost,14330;Database=SecurityAnalyzer;User Id=sa;Password=AgentForge!2026;TrustServerCertificate=true',
    [int]   $TimeoutSeconds     = 180
)

Write-Host "==> POST $ExecutorBaseUrl/runs" -ForegroundColor Cyan

try {
    $resp = Invoke-RestMethod -Uri "$ExecutorBaseUrl/runs" -Method Post -UseBasicParsing
} catch {
    Write-Error "POST /runs failed: $_"
    exit 1
}

$runId = $resp.executorRunId
if (-not $runId) {
    Write-Error "Executor did not return an executorRunId; got: $($resp | ConvertTo-Json -Compress)"
    exit 1
}

Write-Host "    ExecutorRuns.Id = $runId (polling for completion)" -ForegroundColor Cyan

Add-Type -AssemblyName System.Data
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$status   = $null
$exitCode = $null
$execId   = $null

while ((Get-Date) -lt $deadline) {
    try {
        $conn = New-Object System.Data.SqlClient.SqlConnection $SecurityAnalyzerDb
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = "SELECT Status, ExitCode, PenetrationTestExecutionId FROM dbo.ExecutorRuns WHERE Id = $runId"
        $reader = $cmd.ExecuteReader()
        if ($reader.Read()) {
            $status   = if ($reader.IsDBNull(0)) { $null } else { $reader.GetString(0) }
            $exitCode = if ($reader.IsDBNull(1)) { $null } else { $reader.GetInt32(1) }
            $execId   = if ($reader.IsDBNull(2)) { $null } else { $reader.GetInt32(2) }
        }
        $reader.Close()
        $conn.Close()
    } catch {
        Write-Warning "DB poll failed: $_"
    }

    if ($status -and $status -ne 'running') { break }
    Start-Sleep -Seconds 2
}

if (-not $status -or $status -eq 'running') {
    Write-Host "==> run $runId did not finish within $TimeoutSeconds s (still '$status')" -ForegroundColor Yellow
    exit 2
}

$color = if ($status -eq 'ok') { 'Green' } else { 'Yellow' }
Write-Host "==> run $runId finished: status=$status, exitCode=$exitCode, PenetrationTestExecutions.Id=$execId" -ForegroundColor $color

if ($null -eq $exitCode) { exit 0 } else { exit $exitCode }
