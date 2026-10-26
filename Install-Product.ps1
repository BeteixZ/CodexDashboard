#requires -Version 7.0
param(
    [string]$PackageDirectory = $PSScriptRoot,
    [Parameter(Mandatory)][string]$InstallationDirectory,
    [Parameter(Mandatory)][string]$DataDirectory,
    [string]$ProtocolProfile = 'default'
)
$ErrorActionPreference = 'Stop'
function FullPath([string]$Path) {
    if (![IO.Path]::IsPathFullyQualified($Path)) { throw 'Use absolute paths.' }
    $resolved = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    for ($parent = [IO.DirectoryInfo]::new($resolved); $null -ne $parent; $parent = $parent.Parent) {
        if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected installation paths are unsupported.' }
    }
    return $resolved
}
function Overlaps([string]$Left, [string]$Right) {
    return $Left.Equals($Right, [StringComparison]::OrdinalIgnoreCase) -or
        $Left.StartsWith($Right + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $Right.StartsWith($Left + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}
$package = FullPath $PackageDirectory
$installation = FullPath $InstallationDirectory
$data = FullPath $DataDirectory
if ((Overlaps $package $installation) -or (Overlaps $data $installation) -or (Overlaps $package $data)) {
    throw 'Package, installation and data directories must be separate.'
}
if ([string]::IsNullOrWhiteSpace($ProtocolProfile) -or $ProtocolProfile.Length -gt 256 -or $ProtocolProfile -match '[\x00-\x1f]') { throw 'Invalid protocol profile.' }
$manifest = Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.version -ne 1 -or $manifest.product -notin @('assistant','dashboard')) { throw 'Unsupported product package.' }
$files = @($manifest.files)
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $files) {
    $relative = [string]$file.file
    if ([IO.Path]::IsPathRooted($relative) -or !$names.Add($relative)) { throw 'Invalid package manifest path.' }
    $path = [IO.Path]::GetFullPath((Join-Path $package $relative))
    if (!$path.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package file escapes its directory.' }
    $item = Get-Item -LiteralPath $path
    if ($item.Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) { throw 'Package integrity check failed.' }
}
foreach ($item in Get-ChildItem -LiteralPath $package -Recurse -Force) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected package entries are unsupported.' }
    if (!$item.PSIsContainer) {
        $relative = [IO.Path]::GetRelativePath($package, $item.FullName)
        if ($relative -ne 'package-manifest.json' -and !$names.Contains($relative)) { throw 'Package contains unlisted files.' }
    }
}
$name = if ($manifest.product -eq 'assistant') { 'Assistant' } else { 'Dashboard' }
foreach ($required in @("Codex$name.exe", "Start-$name.ps1", 'Launch-InstalledProduct.ps1', 'Uninstall-Product.ps1')) {
    if (!$names.Contains($required)) { throw 'Package entry point is missing.' }
}
[void][IO.Directory]::CreateDirectory($installation)
$lease = [IO.FileStream]::new((Join-Path $installation '.install.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $statePath = Join-Path $installation 'installation.json'
    $previous = $null
    if (Test-Path -LiteralPath $statePath) {
        $previous = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if ($previous.version -ne 1 -or $previous.product -ne $manifest.product -or $previous.dataDirectory -ne $data -or $previous.protocolProfile -ne $ProtocolProfile) {
            throw 'Upgrade must preserve product, data directory and protocol profile. Data migration is a separate operation.'
        }
    } elseif (@(Get-ChildItem -LiteralPath $installation -Force | Where-Object Name -ne '.install.lock').Count -ne 0) {
        throw 'A new installation requires an empty directory.'
    }
    $release = [Guid]::NewGuid().ToString('N')
    $target = FullPath (Join-Path $installation "releases/$release")
    [void][IO.Directory]::CreateDirectory($target)
    foreach ($file in $files) {
        $destination = Join-Path $target $file.file
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        [IO.File]::Copy((Join-Path $package $file.file), $destination, $false)
        if ((Get-FileHash -LiteralPath $destination).Hash -ne $file.sha256) { throw 'Installed file integrity check failed; previous release remains selected.' }
    }
    [IO.File]::Copy((Join-Path $package 'package-manifest.json'), (Join-Path $target 'package-manifest.json'), $false)
    $launcher = Join-Path $installation 'Launch-InstalledProduct.ps1'
    if (![IO.File]::Exists($launcher)) { [IO.File]::Copy((Join-Path $target 'Launch-InstalledProduct.ps1'), $launcher, $false) }
    elseif ((Get-FileHash -LiteralPath $launcher).Hash -ne (Get-FileHash -LiteralPath (Join-Path $target 'Launch-InstalledProduct.ps1')).Hash) {
        throw 'The stable launcher format changed; automatic upgrade is unsupported.'
    }
    $uninstaller = Join-Path $installation 'Uninstall-Product.ps1'
    $uninstallTemporary = Join-Path $installation ("uninstall-" + $release + '.tmp')
    [IO.File]::Copy((Join-Path $target 'Uninstall-Product.ps1'), $uninstallTemporary, $false)
    [IO.File]::Move($uninstallTemporary, $uninstaller, $true)
    $state = [ordered]@{ version=1; product=$manifest.product; activeRelease=$release; previousRelease=$previous.activeRelease;
        dataDirectory=$data; protocolProfile=$ProtocolProfile; installedAtUtc=[DateTimeOffset]::UtcNow }
    $temporary = Join-Path $installation ("installation-" + $release + '.tmp')
    $state | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8
    [IO.File]::Move($temporary, $statePath, $true)
    [pscustomobject]@{ Product=$manifest.product; InstallationDirectory=$installation; ActiveRelease=$release;
        PreviousRelease=$previous.activeRelease; DataDirectory=$data; Started=$false; DataMigrated=$false }
} finally { $lease.Dispose() }
