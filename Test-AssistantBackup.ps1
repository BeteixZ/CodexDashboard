#requires -Version 7.0
param([Parameter(Mandatory)][string]$BackupDirectory)
$ErrorActionPreference = 'Stop'
if (![IO.Path]::IsPathFullyQualified($BackupDirectory)) { throw 'Use an absolute backup directory.' }
$root = [IO.Path]::GetFullPath($BackupDirectory)
for ($part = $root; $part; $part = [IO.Path]::GetDirectoryName($part)) {
    if ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Backup paths must not traverse links.'
    }
}
$entries = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
    throw 'Backup contains links.'
}
$manifest = Get-Content -LiteralPath (Join-Path $root 'backup-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.version -ne 1 -or !$manifest.completedAtUtc -or $manifest.files -isnot [array]) {
    throw 'Incomplete or unsupported backup manifest.'
}
$payload = Join-Path $root 'data'
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -Force -File)
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($record in $manifest.files) {
    # Compare against names discovered inside the payload, never open a manifest-supplied path.
    if ([string]::IsNullOrWhiteSpace($record.path) -or !$expected.Add($record.path) -or
        $record.sha256 -notmatch '\A[0-9a-fA-F]{64}\z' -or $null -eq $record.bytes -or $record.bytes -lt 0) {
        throw 'Invalid or duplicate manifest entry.'
    }
}
if ($files.Count -ne $expected.Count -or !$expected.Contains('.codex-migration.lock')) {
    throw 'Backup file inventory differs from manifest.'
}
$records = @{}
foreach ($record in $manifest.files) { $records[$record.path] = $record }
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($payload, $file.FullName)
    if (!$expected.Contains($relative)) { throw 'Unexpected file in backup payload.' }
    $record = $records[$relative]
    if ($file.Length -ne $record.bytes -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $record.sha256) {
        throw 'Backup file length or hash differs from manifest.'
    }
}
# Byte integrity is not proof of semantic compatibility, decryptability, or safe replay.
[pscustomobject]@{backupDirectory=$root; fileCount=$files.Count; integrityVerified=$true; restoreAuthorized=$false}
