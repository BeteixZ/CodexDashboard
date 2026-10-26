$ErrorActionPreference = 'Stop'
$dashboardProgram = Join-Path $PSScriptRoot 'CodexDashboard.exe'
$dashboardSettingsDir = Join-Path $env:LOCALAPPDATA 'CodexDashboard'
$dashboardSettingsFile = Join-Path $dashboardSettingsDir 'settings.json'
$dashboardStatusFile = Join-Path $dashboardSettingsDir 'firewall-status.json'
$dashboardRuleName = 'CodexDashboard-LAN-Monitor'
try {
    if (-not (Test-Path -LiteralPath $dashboardProgram)) { throw 'Run this script from the published application folder.' }
    $dashboardProgram = (Resolve-Path -LiteralPath $dashboardProgram).Path
    $dashboardSettings = Get-Content -LiteralPath $dashboardSettingsFile -Raw | ConvertFrom-Json
    $dashboardPort = [int]$dashboardSettings.webPort
    if ($dashboardPort -lt 1024 -or $dashboardPort -gt 65535) { throw 'Save a valid web port in the app settings first.' }
    $dashboardPrivateRanges = @('10.0.0.0/8','172.16.0.0/12','192.168.0.0/16','100.64.0.0/10')
    $dashboardExisting = Get-NetFirewallRule -Name $dashboardRuleName -ErrorAction SilentlyContinue
    if ($dashboardExisting) {
        if ($dashboardExisting.DisplayName -ne 'Codex Dashboard (LAN monitoring)') { throw 'A firewall rule with this name already exists and is not managed by this app.' }
        Set-NetFirewallRule -Name $dashboardRuleName -Program $dashboardProgram -Direction Inbound -Protocol TCP -LocalPort $dashboardPort -Action Allow -Profile Any -RemoteAddress $dashboardPrivateRanges -Enabled True -EdgeTraversalPolicy Block | Out-Null
    }
    else {
        New-NetFirewallRule -Name $dashboardRuleName -DisplayName 'Codex Dashboard (LAN monitoring)' -Description 'Authenticated read-only Codex Dashboard access from private LAN or VPN addresses.' -Program $dashboardProgram -Direction Inbound -Protocol TCP -LocalPort $dashboardPort -Action Allow -Profile Any -RemoteAddress $dashboardPrivateRanges -Enabled True -EdgeTraversalPolicy Block | Out-Null
    }
    @{ success = $true; port = $dashboardPort; program = $dashboardProgram; updatedAt = (Get-Date).ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $dashboardStatusFile -Encoding utf8
    Write-Output 'Codex Dashboard LAN firewall rule configured.'
}
catch {
    if (Test-Path -LiteralPath $dashboardSettingsDir) {
        @{ success = $false; error = $_.Exception.Message; updatedAt = (Get-Date).ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $dashboardStatusFile -Encoding utf8
    }
    throw
}
