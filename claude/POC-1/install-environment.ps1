# install-environment.ps1
# One-time setup: creates a Python venv at .\.venv and installs dependencies
# from requirements.txt. Re-runnable; existing venvs are reused.

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

$VenvDir = Join-Path $ScriptDir '.venv'
$VenvPython = Join-Path $VenvDir 'Scripts\python.exe'
$Requirements = Join-Path $ScriptDir 'requirements.txt'

# Locate a real Python interpreter.  On this Windows box, bare `python`
# resolves to the Microsoft Store stub (see ~/.claude/environment-notes.md),
# so we prefer the `py` launcher and fall back to known install paths.
function Resolve-Python {
    $candidates = @(
        @{ Cmd = 'py'; Args = @('-3') },
        @{ Cmd = "$env:LOCALAPPDATA\Programs\Python\Python313\python.exe"; Args = @() },
        @{ Cmd = "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe"; Args = @() },
        @{ Cmd = "$env:LOCALAPPDATA\Programs\Python\Python311\python.exe"; Args = @() }
    )
    foreach ($c in $candidates) {
        try {
            $version = & $c.Cmd @($c.Args + '--version') 2>$null
            if ($LASTEXITCODE -eq 0 -and $version) {
                return $c
            }
        } catch {
            continue
        }
    }
    throw "No working Python interpreter found. Install Python 3.11+ or disable the Microsoft Store python alias."
}

if (-not (Test-Path $VenvPython)) {
    $python = Resolve-Python
    Write-Host "Using interpreter: $($python.Cmd) $($python.Args -join ' ')"
    Write-Host "Creating venv at $VenvDir"
    & $python.Cmd @($python.Args + @('-m', 'venv', $VenvDir))
    if ($LASTEXITCODE -ne 0) {
        throw "python -m venv failed with exit code $LASTEXITCODE"
    }
} else {
    Write-Host "Venv already present at $VenvDir (reusing)"
}

Write-Host "Upgrading pip"
& $VenvPython -m pip install --upgrade pip --disable-pip-version-check --quiet
if ($LASTEXITCODE -ne 0) { throw "pip upgrade failed with exit code $LASTEXITCODE" }

Write-Host "Installing requirements from $Requirements"
& $VenvPython -m pip install -r $Requirements --disable-pip-version-check --quiet
if ($LASTEXITCODE -ne 0) { throw "pip install failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "Environment ready. Use run.ps1 to execute the POC."
