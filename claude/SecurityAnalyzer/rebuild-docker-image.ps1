# Rebuild and restart an SecurityAnalyzer Docker service after a code change.
#
# The Dockerfile copies source at build time (no bind mounts), so a plain
# `docker compose restart` reuses the stale image and silently keeps
# running the old code.  This wrapper does the full cycle:
#
#     1. docker compose stop <service>
#     2. docker compose build <service>
#     3. docker compose up -d <service>
#
# Defaults to the security-analyzer-web service since that's what
# changes most often; pass -Service to target a different one (or
# 'all' to rebuild every service in the compose file).
#
# Usage:
#   .\rebuild-docker-image.ps1
#   .\rebuild-docker-image.ps1 -Service security-analyzer-web
#   .\rebuild-docker-image.ps1 -Service all

[CmdletBinding()]
param(
    [string]$Service = 'security-analyzer-web'
)

# Don't set $ErrorActionPreference='Stop' -- docker writes informational
# output to stderr (BuildKit progress, "Container ... Started", etc.) and
# in Windows PowerShell 5.1 that gets wrapped in NativeCommandError
# records that would trip a global Stop.  Check $LASTEXITCODE explicitly.

$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$composeFile = Join-Path $scriptDir 'docker-compose.yml'

if (-not (Test-Path $composeFile)) {
    Write-Error "Compose file not found: $composeFile"
    exit 1
}

# `all` means: no service argument, so docker compose acts on every service.
# Type the array explicitly: PowerShell's `if` expression unwraps a
# single-element array back into a scalar, after which `@serviceArgs`
# would splat a string char-by-char ('a','g','e',...) instead of as one
# argument.  Declaring [string[]] preserves the array shape.
[string[]]$serviceArgs = if ($Service -eq 'all') { @() } else { @($Service) }
$targetLabel = if ($Service -eq 'all') { '(all services)' } else { $Service }

Write-Host "==> rebuild cycle for $targetLabel" -ForegroundColor Cyan
Write-Host "    compose file = $composeFile"

Write-Host "==> [1/3] stop" -ForegroundColor Cyan
& docker compose -f $composeFile stop @serviceArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "==> stop failed (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "==> [2/3] build" -ForegroundColor Cyan
& docker compose -f $composeFile build @serviceArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "==> build failed (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "==> [3/3] up -d" -ForegroundColor Cyan
& docker compose -f $composeFile up -d @serviceArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "==> up failed (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "==> rebuild complete; current status:" -ForegroundColor Green
& docker compose -f $composeFile ps

exit 0
