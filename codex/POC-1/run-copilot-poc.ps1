[CmdletBinding()]
param(
    [string]$BaseUrl = $(if (-not [string]::IsNullOrWhiteSpace($env:OPENEMR_PROD_URL)) { $env:OPENEMR_PROD_URL } else { "https://openemr-web-production.up.railway.app/" }),
    [string]$Username = $env:OPENEMR_PROD_USERNAME,
    [string]$Password = $env:OPENEMR_PROD_PASSWORD,
    [string]$PatientId = $(if (-not [string]::IsNullOrWhiteSpace($env:OPENEMR_PROD_PATIENT_ID)) { $env:OPENEMR_PROD_PATIENT_ID } else { "1" }),
    [string]$Prompt = "show basic patient data",
    [string]$EvidenceDir = "",
    [string]$Python = $(if (-not [string]::IsNullOrWhiteSpace($env:PYTHON)) { $env:PYTHON } elseif (Test-Path -LiteralPath "C:\Users\s-109\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe") { "C:\Users\s-109\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe" } else { "python" })
)

$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$PythonScript = Join-Path $ScriptRoot "invoke_copilot.py"

$PythonArgs = @(
    $PythonScript,
    "--base-url", $BaseUrl,
    "--prompt", $Prompt
)

if (-not [string]::IsNullOrWhiteSpace($Username)) {
    $PythonArgs += @("--username", $Username)
}

if ($null -ne $Password -and $Password.Length -gt 0) {
    $PythonArgs += @("--password", $Password)
}

if (-not [string]::IsNullOrWhiteSpace($PatientId)) {
    $PythonArgs += @("--patient-id", $PatientId)
}

if (-not [string]::IsNullOrWhiteSpace($EvidenceDir)) {
    $PythonArgs += @("--evidence-dir", $EvidenceDir)
}

Write-Host "Running Clinical Co-Pilot production POC..."
Write-Host "Base URL: $BaseUrl"
Write-Host "Username supplied: $([bool](-not [string]::IsNullOrWhiteSpace($Username)))"
Write-Host "Password supplied: $([bool]($null -ne $Password -and $Password.Length -gt 0))"
Write-Host "Patient ID supplied: $([bool](-not [string]::IsNullOrWhiteSpace($PatientId)))"

if ([string]::IsNullOrWhiteSpace($PatientId)) {
    Write-Warning "No patient id supplied. Production is expected to reject patient-scoped Co-Pilot prompts without a current patient."
}

& $Python @PythonArgs
$ExitCode = $LASTEXITCODE

if ($ExitCode -ne 0) {
    [Console]::Error.WriteLine("POC run did not complete successfully. Exit code: $ExitCode")
}

exit $ExitCode
