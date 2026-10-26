#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][string]$BackupDirectory
)
$ErrorActionPreference = 'Stop'
# Explicit paths only: this command never discovers or opens the live profile.
function Get-PlainDirectoryPath([string]$Path) {
    if (![IO.Path]::IsPathFullyQualified($Path)) { throw 'Use an absolute directory path.' }
    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    for ($part = $full; $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if (Test-Path -LiteralPath $part) {
            if ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Backup paths must not traverse links.'
            }
        }
    }
    return $full
}
$source = Get-PlainDirectoryPath $DataDirectory
$destination = Get-PlainDirectoryPath $BackupDirectory
if (!(Test-Path -LiteralPath $source -PathType Container)) { throw 'Source directory does not exist.' }
if ((Test-Path -LiteralPath $destination) -or $destination.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or
    $destination.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $source.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use a new backup directory outside the source directory.'
}
# Matches the independent host lease. This is not proof that legacy Dashboard writers stopped.
# Open, not OpenOrCreate: an unknown directory must not be mistaken for an assistant profile.
$lease = [IO.File]::Open((Join-Path $source '.codex-migration.lock'), [IO.FileMode]::Open,
    [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $entries = @(Get-ChildItem -LiteralPath $source -Recurse -Force)
    if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw 'Source contains links; no backup was created.'
    }
    $payload = Join-Path $destination 'data'
    [void][IO.Directory]::CreateDirectory($payload)
    $records = @(
        foreach ($entry in $entries) {
            $relative = [IO.Path]::GetRelativePath($source, $entry.FullName)
            $target = Join-Path $payload $relative
            if ($entry.PSIsContainer) { [void][IO.Directory]::CreateDirectory($target); continue }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            if ($relative -eq '.codex-migration.lock') {
                # Read the locked file through our own handle without releasing exclusion.
                $output = [IO.File]::Create($target)
                try { $lease.Position = 0; $lease.CopyTo($output) } finally { $output.Dispose() }
            } else { [IO.File]::Copy($entry.FullName, $target, $false) }
            $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            if ($relative -ne '.codex-migration.lock' -and
                $hash -ne (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash) {
                throw 'Source changed while copying. The incomplete backup was retained.'
            }
            [pscustomobject]@{path=$relative; bytes=(Get-Item -LiteralPath $target).Length; sha256=$hash}
        }
    )
    # Written last; missing manifest means incomplete. No decrypted data is produced.
    [pscustomobject]@{version=1; completedAtUtc=[DateTimeOffset]::UtcNow; files=$records} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'backup-manifest.json') -Encoding utf8
    [pscustomobject]@{backupDirectory=$destination; fileCount=$records.Count; complete=$true}
}
finally { $lease.Dispose() }
