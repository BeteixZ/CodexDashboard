#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$command = Join-Path $PSScriptRoot '../Backup-AssistantData.ps1'
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Use a fresh synthetic test directory.' }
$source = Join-Path $root 'synthetic-source'
[void][IO.Directory]::CreateDirectory((Join-Path $source 'history-archive/empty'))
[IO.File]::WriteAllText((Join-Path $source '.codex-migration.lock'), '')
[IO.File]::WriteAllText((Join-Path $source 'settings.json'), '{"synthetic":true}')
[IO.File]::WriteAllBytes((Join-Path $source 'history-archive/test.volume'), [byte[]](0..255))
$checks = [ordered]@{}
function Reject([scriptblock]$Action) {
    try { & $Action | Out-Null } catch { return $true }
    return $false
}
$lease = [IO.File]::Open((Join-Path $source '.codex-migration.lock'), 'Open', 'ReadWrite', 'Read')
try {
    $blocked = Join-Path $root 'blocked'
    $checks.runningRejected = (Reject { & $command -DataDirectory $source -BackupDirectory $blocked }) -and !(Test-Path -LiteralPath $blocked)
} finally { $lease.Dispose() }
$checks.nestedRejected = Reject { & $command -DataDirectory $source -BackupDirectory (Join-Path $source 'backup') }
$backup = Join-Path $root 'backup'
$result = & $command -DataDirectory $source -BackupDirectory $backup
$manifest = Get-Content -LiteralPath (Join-Path $backup 'backup-manifest.json') -Raw | ConvertFrom-Json
$checks.complete = $result.complete -and $manifest.files.Count -eq 3
$checks.hashes = @($manifest.files | Where-Object {
    $_.sha256 -ne (Get-FileHash -LiteralPath (Join-Path $source $_.path)).Hash -or
    $_.sha256 -ne (Get-FileHash -LiteralPath (Join-Path $backup ('data/' + $_.path))).Hash
}).Count -eq 0
$checks.emptyDirectory = Test-Path -LiteralPath (Join-Path $backup 'data/history-archive/empty') -PathType Container
$checks.existingRejected = Reject { & $command -DataDirectory $source -BackupDirectory $backup }
$unknown = Join-Path $root 'unknown'
[void][IO.Directory]::CreateDirectory($unknown)
$checks.unknownRejected = (Reject { & $command -DataDirectory $unknown -BackupDirectory (Join-Path $root 'unknown-backup') }) -and
    !(Test-Path -LiteralPath (Join-Path $unknown '.codex-migration.lock'))
# A directory junction must never expand the backup into another data tree.
$link = Join-Path $source 'linked'
New-Item -ItemType Junction -Path $link -Target $unknown | Out-Null
$checks.linkRejected = (Reject { & $command -DataDirectory $source -BackupDirectory (Join-Path $root 'linked-backup') }) -and
    !(Test-Path -LiteralPath (Join-Path $root 'linked-backup'))
$checks | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'checks.json') -Encoding utf8
if ($checks.Values -contains $false) { throw 'Backup checks failed.' }
$checks | ConvertTo-Json
