#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$BackupDirectory,
    [Parameter(Mandatory)][string]$RecoveryDirectory,
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'CodexAssistant.exe')
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-AssistantBackup.ps1') -BackupDirectory $BackupDirectory | Out-Null
if (![IO.Path]::IsPathFullyQualified($RecoveryDirectory)) { throw 'Use an absolute recovery directory.' }
$source = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($BackupDirectory))
$destination = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($RecoveryDirectory))
if ((Test-Path -LiteralPath $destination) -or $destination.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use a new recovery directory outside the backup.'
}
for ($part = [IO.Path]::GetDirectoryName($destination); $part; $part = [IO.Path]::GetDirectoryName($part)) {
    if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Recovery paths must not traverse links.'
    }
}
$executable = (Get-Item -LiteralPath $ExecutablePath).FullName
$payload = Join-Path $destination 'data'
[void][IO.Directory]::CreateDirectory($payload)
# Block normal startup before copying anything, including after an interrupted preparation.
$hold = Join-Path $payload '.codex-recovery-hold.json'
[IO.File]::WriteAllText($hold, '{"version":1,"reason":"offline-recovery-preparation"}')
$backupPayload = Join-Path $source 'data'
foreach ($entry in Get-ChildItem -LiteralPath $backupPayload -Recurse -Force) {
    $relative = [IO.Path]::GetRelativePath($backupPayload, $entry.FullName)
    $target = Join-Path $payload $relative
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Backup changed to contain a link.' }
    if ($entry.PSIsContainer) { [void][IO.Directory]::CreateDirectory($target); continue }
    if ($relative -eq '.codex-recovery-hold.json') { continue }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    [IO.File]::Copy($entry.FullName, $target, $false)
}
$manifest = Get-Content -LiteralPath (Join-Path $source 'backup-manifest.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest.files) {
    if ($entry.path -eq '.codex-recovery-hold.json') { continue }
    $target = Join-Path $payload $entry.path
    if ((Get-FileHash -LiteralPath $target).Hash -ne $entry.sha256 -or (Get-Item -LiteralPath $target).Length -ne $entry.bytes) {
        throw 'Copied data differs from the backup. The held working directory was retained.'
    }
}
$report = Join-Path $destination 'inspection.json'
$process = Start-Process -FilePath $executable -ArgumentList @('--inspect-ledger', ('"' + $payload + '"'), ('"' + $report + '"')) -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report)) {
    throw 'Offline inspection failed. The held working directory was retained.'
}
$inspection = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (!$inspection.recoveryHoldPresent -or $inspection.restoreAuthorized -ne $false) { throw 'Inspection did not confirm the recovery hold.' }
[pscustomobject]@{recoveryDirectory=$destination; inspectionReport=$report; held=$true; restoreAuthorized=$false}
