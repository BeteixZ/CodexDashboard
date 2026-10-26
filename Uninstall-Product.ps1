#requires -Version 7.0
param([string]$InstallationDirectory = $PSScriptRoot, [switch]$Preview)
$ErrorActionPreference = 'Stop'
if (![IO.Path]::IsPathFullyQualified($InstallationDirectory)) { throw 'Use an absolute installation directory.' }
$root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($InstallationDirectory))
for ($parent = [IO.DirectoryInfo]::new($root); $null -ne $parent; $parent = $parent.Parent) {
    if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected installation paths are unsupported.' }
}
$lease = [IO.FileStream]::new((Join-Path $root '.install.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$handles = [Collections.Generic.List[IO.FileStream]]::new()
try {
    $state = Get-Content -LiteralPath (Join-Path $root 'installation.json') -Raw | ConvertFrom-Json
    if ($state.version -ne 1 -or $state.product -notin @('assistant','dashboard') -or $state.activeRelease -notmatch '^[0-9a-f]{32}$') { throw 'Invalid installation record.' }
    if (![IO.Path]::IsPathFullyQualified($state.dataDirectory)) { throw 'Invalid data directory.' }
    $data = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($state.dataDirectory))
    if ($data.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or $data.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or $root.StartsWith($data + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Data and program directories overlap; nothing removed.' }
    $items = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) { throw 'Redirected entries prevent uninstall.' }
    $owned = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('installation.json','Launch-InstalledProduct.ps1','Uninstall-Product.ps1','shortcuts.json')) { [void]$owned.Add((Join-Path $root $name)) }
    foreach ($release in Get-ChildItem -LiteralPath (Join-Path $root 'releases') -Directory) {
        if ($release.Name -notmatch '^[0-9a-f]{32}$') { throw 'Unrecognized release directory; nothing removed.' }
        $manifestPath = Join-Path $release.FullName 'package-manifest.json'
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.version -ne 1 -or $manifest.product -ne $state.product) { throw 'Release ownership does not match installation.' }
        [void]$owned.Add($manifestPath)
        foreach ($entry in $manifest.files) {
            $path = [IO.Path]::GetFullPath((Join-Path $release.FullName $entry.file))
            if ([IO.Path]::IsPathRooted($entry.file) -or !$path.StartsWith($release.FullName + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path leaves the release directory.' }
            [void]$owned.Add($path)
        }
    }
    $lockPath = Join-Path $root '.install.lock'
    $files = @($items | Where-Object { !$_.PSIsContainer -and $_.FullName -ne $lockPath })
    foreach ($file in $files) { if (!$owned.Contains($file.FullName)) { throw 'Unlisted files exist; preserve them before uninstalling.' } }
    $shortcuts=@()
    $shortcutReceipt=Join-Path $root 'shortcuts.json'
    if (Test-Path -LiteralPath $shortcutReceipt) {
        $savedShortcuts=Get-Content -LiteralPath $shortcutReceipt -Raw | ConvertFrom-Json
        if ($savedShortcuts.version -ne 1) { throw 'Unsupported shortcut record.' }
        $shortcuts=@($savedShortcuts.entries)
    }
    if ($Preview) {
        [pscustomobject]@{ Product=$state.product; ProgramFiles=$files.Count; DataDirectoryPreserved=$data; Removed=$false }; return
    }
    # Acquire every file before deleting any. Write access detects loaded images;
    # delete sharing lets this process unlink its own handles while preventing new readers.
    foreach ($file in $files) {
        $handles.Add([IO.FileStream]::new($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Delete))
    }
    $removedShortcuts=0; $preservedShortcuts=0
    foreach ($shortcut in $shortcuts) {
        if (![IO.Path]::IsPathFullyQualified($shortcut.path) -or [IO.Path]::GetExtension($shortcut.path) -ne '.lnk') { $preservedShortcuts++; continue }
        if (!(Test-Path -LiteralPath $shortcut.path)) { continue }
        $redirected=((Get-Item -LiteralPath $shortcut.path).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        for ($parent=[IO.FileInfo]::new($shortcut.path).Directory; $null -ne $parent; $parent=$parent.Parent) {
            if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { $redirected=$true; break }
        }
        if ($redirected) { $preservedShortcuts++; continue }
        $handle=$null
        try {
            $handle=[IO.FileStream]::new($shortcut.path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Delete)
            $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($handle))
            if ($hash -eq $shortcut.sha256) { Remove-Item -LiteralPath $shortcut.path -Force; $removedShortcuts++ }
            else { $preservedShortcuts++ }
        } catch { $preservedShortcuts++ }
        finally { if ($null -ne $handle) { $handle.Dispose() } }
    }
    foreach ($file in $files) { Remove-Item -LiteralPath $file.FullName -Force }
    foreach ($handle in $handles) { $handle.Dispose() }; $handles.Clear()
    foreach ($directory in @($items | Where-Object PSIsContainer | Sort-Object { $_.FullName.Length } -Descending)) {
        [IO.Directory]::Delete($directory.FullName, $false)
    }
    [pscustomobject]@{ Product=$state.product; Removed=$true; DataDirectoryPreserved=$data; ShortcutsRemoved=$removedShortcuts; ShortcutsPreserved=$preservedShortcuts }
} finally {
    foreach ($handle in $handles) { $handle.Dispose() }
    $lease.Dispose()
}
# Nonrecursive cleanup cannot remove a concurrently added file or directory.
Remove-Item -LiteralPath (Join-Path $root '.install.lock') -Force
[IO.Directory]::Delete($root, $false)
