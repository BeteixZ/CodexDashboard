$ErrorActionPreference = 'Stop'
$dashboardRoot = $PSScriptRoot
$dashboardExe = Join-Path $dashboardRoot 'artifacts\app\CodexDashboard.exe'
if (-not (Test-Path -LiteralPath $dashboardExe)) {
    Push-Location $dashboardRoot
    try {
        dotnet publish -c Release -o artifacts/app
        if ($LASTEXITCODE -ne 0) { throw 'Codex Dashboard build failed.' }
    }
    finally { Pop-Location }
}
& (Join-Path $dashboardRoot 'Start-Dashboard.ps1') -ExecutablePath $dashboardExe -WorkingDirectory $dashboardRoot
