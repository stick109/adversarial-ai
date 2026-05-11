# run.ps1
# Executes the POC-1 Clinical Co-Pilot probe against the configured
# OpenEMR deployment. Expects install-environment.ps1 to have been
# run previously to create the venv.

[CmdletBinding()]
param(
    [int]    $PatientId = 1,
    [string] $BaseUrl   = 'https://openemr-web-production.up.railway.app',
    [string] $Site      = 'default',
    [string] $Username  = 'admin',
    [string] $Password  = 'pass',
    [string] $IntentId  = 'basic_patient_data',
    [string] $UserGoal  = 'show basic patient data'
)

$ErrorActionPreference = 'Stop'

$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

$VenvPython = Join-Path $ScriptDir '.venv\Scripts\python.exe'
$PocScript  = Join-Path $ScriptDir 'poc.py'

if (-not (Test-Path $VenvPython)) {
    Write-Error "Venv missing. Run .\install-environment.ps1 first."
    exit 2
}
if (-not (Test-Path $PocScript)) {
    Write-Error "poc.py missing next to run.ps1."
    exit 2
}

& $VenvPython $PocScript `
    --base-url $BaseUrl `
    --site     $Site `
    --username $Username `
    --password $Password `
    --pid      $PatientId `
    --intent-id $IntentId `
    --user-goal $UserGoal

exit $LASTEXITCODE
