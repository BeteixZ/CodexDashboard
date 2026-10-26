#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [string]$OutputDirectory,
    [switch]$RequireCleanSource
)
$ErrorActionPreference='Stop'
$checks=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[bool]$Passed) {
    $checks.Add(@{name=$Name;passed=$Passed})
    if (!$Passed) { throw "Release check failed: $Name" }
}
if ($OutputDirectory) {
    if (![IO.Path]::IsPathFullyQualified($OutputDirectory) -or (Test-Path -LiteralPath $OutputDirectory)) { throw 'Use a new absolute test output directory.' }
    [void][IO.Directory]::CreateDirectory($OutputDirectory)
}
try {
    $manifest=Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'release-manifest.json') -Raw | ConvertFrom-Json
    Check 'release explicitly declares self-contained Windows x64 and unsigned status' ($manifest.selfContained -eq $true -and $manifest.runtimeIdentifier -eq 'win-x64' -and $manifest.signed -eq $false)
    Check 'Python download comes from official host with pinned SHA256' (([uri]$manifest.python.url).Host -eq 'www.python.org')
    Check 'Python archive SHA256 is recorded' ($manifest.python.sha256 -match '^[0-9a-f]{64}$')
    Check 'release revision is absent or a Git object identity' (!$manifest.sourceRevision -or $manifest.sourceRevision -match '^[0-9a-f]{40}$')
    Check 'release declares source cleanliness or no Git context' ($null -eq $manifest.sourceDirty -or $manifest.sourceDirty -is [bool])
    if ($RequireCleanSource -or $env:GITHUB_ACTIONS -eq 'true') {
        Check 'CI release was built from a clean Git commit' ($manifest.sourceDirty -eq $false -and $manifest.sourceRevision -match '^[0-9a-f]{40}$')
    }
    $expected=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($product in @('CodexDashboard','CodexAssistant')) {
        [void]$expected.Add("$product-$($manifest.version)-win-x64-Setup.exe")
        [void]$expected.Add("$product-$($manifest.version)-win-x64-portable.zip")
    }
    Check 'manifest covers the four distributable binaries' ($manifest.files.Count -eq 4)
    foreach($entry in $manifest.files) {
        Check 'release manifest contains only expected base filenames' ($expected.Remove([string]$entry.file) -and [IO.Path]::GetFileName($entry.file) -eq $entry.file)
        $file=Get-Item -LiteralPath (Join-Path $ReleaseDirectory $entry.file)
        Check "$($entry.file) hash and length" ($file.Length -eq $entry.bytes -and (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -eq $entry.sha256)
    }
    Check 'all binaries were found in manifest' ($expected.Count -eq 0)
    $checksumLines=@(Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'SHA256SUMS.txt'))
    Check 'checksums include four binaries and release manifest' ($checksumLines.Count -eq 5)
    $checksumNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($line in $checksumLines) {
        if ($line -notmatch '^([0-9a-f]{64})  ([^\\/:]+)$') { throw 'Invalid checksum line.' }
        $hash=$Matches[1]; $name=$Matches[2]
        Check 'checksum file names are unique' $checksumNames.Add($name)
        Check "$name checksum" ((Get-FileHash -LiteralPath (Join-Path $ReleaseDirectory $name) -Algorithm SHA256).Hash -eq $hash)
    }
    Check 'release manifest has a checksum' $checksumNames.Contains('release-manifest.json')
    foreach($product in @(@{Directory='dashboard';Name='CodexDashboard'},@{Directory='assistant';Name='CodexAssistant'})) {
        $package=Join-Path $PackageDirectory $product.Directory
        $runtime=Get-Content -LiteralPath (Join-Path $package "$($product.Name).runtimeconfig.json") -Raw | ConvertFrom-Json
        Check "$($product.Name) does not require global .NET frameworks" ($null -eq $runtime.runtimeOptions.PSObject.Properties['frameworks'] -and @($runtime.runtimeOptions.includedFrameworks).Count -eq 3)
        foreach($asset in @('coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','PresentationFramework.dll','Microsoft.AspNetCore.dll','python/python.exe','python/LICENSE.txt','LICENSE','THIRD-PARTY-NOTICES.txt')) {
            Check "$($product.Name) bundles $asset" (Test-Path -LiteralPath (Join-Path $package $asset) -PathType Leaf)
        }
        $start=Get-Content -LiteralPath (Join-Path $package 'Start-Portable.cmd') -Raw
        Check "$($product.Name) portable launch explicitly isolates data" ($start.Contains('--data-dir') -and $start.Contains("$($product.Name)Portable") -and !$start.Contains('pwsh'))
        $distribution=Get-Content -LiteralPath (Join-Path $package 'distribution.json') -Raw | ConvertFrom-Json
        Check "$($product.Name) direct EXE has portable isolation marker" ($distribution.schemaVersion -eq 1 -and $distribution.mode -eq 'portable' -and $distribution.product -eq $product.Directory)
        foreach($framework in $runtime.runtimeOptions.includedFrameworks) {
            $packName=$framework.name.ToLowerInvariant()+'.runtime.win-x64'
            $licenseDirectory=Join-Path $package "licenses/dotnet/$packName/$($framework.version)"
            Check "$($product.Name) includes the official $packName license" (@(Get-ChildItem -LiteralPath $licenseDirectory -File | Where-Object Name -match '^(?i:LICENSE)').Count -gt 0)
        }
        Check "$($product.Name) includes original runtime third-party notices" (@(Get-ChildItem -LiteralPath (Join-Path $package 'licenses/dotnet') -File -Recurse | Where-Object Name -match '^(?i:THIRD[-_]?PARTY[-_]?NOTICES?)').Count -ge 2)
        $packageManifest=Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json
        Check "$($product.Name) manifests its portable entry" (@($packageManifest.files | Where-Object file -eq 'Start-Portable.cmd').Count -eq 1)
        $zipPath=Join-Path $ReleaseDirectory "$($product.Name)-$($manifest.version)-win-x64-portable.zip"
        $archive=[IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $zipNames=@($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
            Check "$($product.Name) ZIP includes portable launcher and Python" ($zipNames -contains 'Start-Portable.cmd' -and $zipNames -contains 'python/python.exe')
            Check "$($product.Name) ZIP contains no user settings or credentials" (@($zipNames | Where-Object { $_ -match '(?i)(^|/)(settings\.json|dashboard\.settings\.json|.*credentials.*|data-directory\.txt)$' }).Count -eq 0)
        } finally {$archive.Dispose()}
    }
    Write-Output "$($checks.Count) release packaging checks passed; no installed product was started."
} finally {
    if ($OutputDirectory) {$checks | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'checks.json') -Encoding utf8}
}
