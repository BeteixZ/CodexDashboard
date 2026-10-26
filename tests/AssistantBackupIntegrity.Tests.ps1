#requires -Version 7.0
param([Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ScriptDirectory = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Use a new test directory.' }
$source = Join-Path $root 'source'
[void][IO.Directory]::CreateDirectory($source)
[IO.File]::WriteAllText((Join-Path $source '.codex-migration.lock'), '')
[IO.File]::WriteAllText((Join-Path $source 'synthetic.state'), 'original')
$backup = Join-Path $root 'backup'
& (Join-Path $ScriptDirectory 'Backup-AssistantData.ps1') -DataDirectory $source -BackupDirectory $backup | Out-Null
$verify = Join-Path $ScriptDirectory 'Test-AssistantBackup.ps1'
$checks = [ordered]@{}
function Reject { try { & $verify -BackupDirectory $backup | Out-Null } catch { return $true }; return $false }
$valid = & $verify -BackupDirectory $backup
$checks.validBackup = $valid.integrityVerified -and !$valid.restoreAuthorized -and $valid.fileCount -eq 2
$body = Join-Path $backup 'data/synthetic.state'
[IO.File]::WriteAllText($body, 'modified')
$checks.sameLengthCorruption = Reject
[IO.File]::WriteAllText($body, 'original')
$manifestPath = Join-Path $backup 'backup-manifest.json'
$original = [IO.File]::ReadAllText($manifestPath)
$manifest = $original | ConvertFrom-Json
$manifest.files[0].path = '../../outside'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath
$checks.foreignPath = Reject
[IO.File]::WriteAllText($manifestPath, $original)
Move-Item -LiteralPath $body -Destination (Join-Path $root 'held.state')
$checks.missingFile = Reject
Move-Item -LiteralPath (Join-Path $root 'held.state') -Destination $body
[IO.File]::WriteAllText((Join-Path $backup 'data/extra'), 'extra')
$checks.extraFile = Reject
Move-Item -LiteralPath (Join-Path $backup 'data/extra') -Destination (Join-Path $root 'held-extra')
Move-Item -LiteralPath $manifestPath -Destination (Join-Path $root 'held-manifest.json')
$checks.incompleteBackup = Reject
Move-Item -LiteralPath (Join-Path $root 'held-manifest.json') -Destination $manifestPath
$checks.validAgain = (& $verify -BackupDirectory $backup).integrityVerified
$checks | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'checks.json')
if ($checks.Values -contains $false) { throw 'Backup integrity tests failed.' }
$checks | ConvertTo-Json
