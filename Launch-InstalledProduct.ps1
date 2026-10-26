#requires -Version 7.0
param([string]$InstallationDirectory = $PSScriptRoot, [switch]$Preview)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($InstallationDirectory)
$state = Get-Content -LiteralPath (Join-Path $root 'installation.json') -Raw | ConvertFrom-Json
if ($state.version -ne 1 -or $state.product -notin @('assistant','dashboard') -or $state.activeRelease -notmatch '^[0-9a-f]{32}$') {
    throw 'Invalid installation record.'
}
$name = if ($state.product -eq 'assistant') { 'Assistant' } else { 'Dashboard' }
$release = Join-Path $root "releases/$($state.activeRelease)"
$arguments = @{ ExecutablePath=(Join-Path $release "Codex$name.exe"); WorkingDirectory=$release;
    DataDirectory=$state.dataDirectory; ProtocolProfile=$state.protocolProfile }
if ($Preview) { [pscustomobject]$arguments; return }
& (Join-Path $release "Start-$name.ps1") @arguments
