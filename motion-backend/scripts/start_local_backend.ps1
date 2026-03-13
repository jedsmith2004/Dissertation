param(
    [int]$Port = 50051,
    [string]$PythonExe = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$backendRoot = Resolve-Path (Join-Path $scriptDir "..")

if ([string]::IsNullOrWhiteSpace($PythonExe)) {
    $venvPython = Join-Path $backendRoot "venv\Scripts\python.exe"
    if (Test-Path $venvPython) {
        $PythonExe = $venvPython
    } else {
        $PythonExe = "python"
    }
}

$entrypoint = Join-Path $backendRoot "server\app.py"
if (-not (Test-Path $entrypoint)) {
    throw "Backend entrypoint not found: $entrypoint"
}

Set-Location $backendRoot
$env:MOTIONGEN_BACKEND_PORT = "$Port"
Write-Host "[MotionGen] Starting backend with $PythonExe on port $Port"
& $PythonExe $entrypoint
