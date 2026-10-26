#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$InnoCompilerPath,
    [string]$PythonEmbedArchive,
    [ValidatePattern('^[0-9a-fA-F]{64}$')][string]$PythonEmbedSha256 = '3c01d7cfc666f746a6f064f60fb2156ab0ec464d3d738ab796371a46a9bb24a2',
    [uri]$PythonEmbedUrl = 'https://www.python.org/ftp/python/3.14.9/python-3.14.9-embed-amd64.zip',
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
if (![IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'Use a new absolute output directory.' }
$outputRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($OutputDirectory))
if (Test-Path -LiteralPath $outputRoot) { throw 'Release output must be new; existing artifacts are preserved.' }
for ($parent = [IO.DirectoryInfo]::new($outputRoot); $null -ne $parent; $parent = $parent.Parent) {
    if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Release output cannot use redirected directories.' }
}
$compiler = (Get-Item -LiteralPath $InnoCompilerPath).FullName
if ([IO.Path]::GetFileName($compiler) -ne 'ISCC.exe') { throw 'Provide the Inno Setup ISCC.exe compiler.' }
if ($PythonEmbedUrl.Scheme -ne 'https' -or $PythonEmbedUrl.Host -ne 'www.python.org') { throw 'CPython downloads must use the official HTTPS host.' }
[void][IO.Directory]::CreateDirectory($outputRoot)
$release = Join-Path $outputRoot 'release'
[void][IO.Directory]::CreateDirectory($release)
if (!$PythonEmbedArchive) {
    $downloadDirectory = Join-Path $outputRoot 'downloads'
    [void][IO.Directory]::CreateDirectory($downloadDirectory)
    $PythonEmbedArchive = Join-Path $downloadDirectory 'python-embed-amd64.zip'
    Invoke-WebRequest -Uri $PythonEmbedUrl -OutFile $PythonEmbedArchive
}
$PythonEmbedArchive = (Get-Item -LiteralPath $PythonEmbedArchive).FullName
if ((Get-FileHash -LiteralPath $PythonEmbedArchive -Algorithm SHA256).Hash -ne $PythonEmbedSha256) {
    throw 'CPython download does not match its pinned official SHA256; no archive content was executed.'
}
$packages = Join-Path $outputRoot 'packages'
& (Join-Path $PSScriptRoot 'Build-Products.ps1') -OutputDirectory $packages -Configuration $Configuration -Version $Version -SelfContained -RuntimeIdentifier win-x64 -PythonEmbedArchive $PythonEmbedArchive -PythonEmbedSha256 $PythonEmbedSha256
if (!$?) { throw 'Product publishing failed.' }
$products = @(
    @{ Name='dashboard'; ProductName='CodexDashboard'; Exe='CodexDashboard.exe'; AppId='BeteixZ.CodexDashboard.Windows' },
    @{ Name='assistant'; ProductName='CodexAssistant'; Exe='CodexAssistant.exe'; AppId='BeteixZ.CodexAssistant.Windows' }
)
foreach ($product in $products) {
    $package = Join-Path $packages $product.Name
    $start = @'
@echo off
start "" "%~dp0__EXE__" --data-dir "%LOCALAPPDATA%\__PRODUCT__Portable" --protocol-profile portable
'@.Replace('__EXE__', $product.Exe).Replace('__PRODUCT__', $product.ProductName)
    [IO.File]::WriteAllText((Join-Path $package 'Start-Portable.cmd'), $start, [Text.ASCIIEncoding]::new())
    $readme = @"
$($product.ProductName) $Version / Windows x64

安装版：运行同名 Setup.exe。无需预装 .NET、Python 或 PowerShell。
免安装版：完整解压后双击 Start-Portable.cmd。
免安装数据：%LOCALAPPDATA%\$($product.ProductName)Portable
安装版数据：%LOCALAPPDATA%\$($product.ProductName)Installed
安装版与免安装版使用独立新数据，不迁移旧版记录或登录。
升级前请正常退出该产品。安装器不会强制退出任何程序；卸载保留数据。
首次使用请自行配置 Codex、微信与模型；远程 SSH 仍需用户配置。
本发行包未使用发布者代码签名。SHA256SUMS.txt 用于核对下载完整性。

https://github.com/BeteixZ/CodexDashboard
"@
    [IO.File]::WriteAllText((Join-Path $package 'READ-ME.txt'), $readme, [Text.UTF8Encoding]::new($true))
    # These files are part of the package. Refresh both manifests after adding
    # the portable entry point, before the compiler or archive consumes it.
    $files = @(Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object Name -ne 'package-manifest.json' | ForEach-Object {
        @{ file=[IO.Path]::GetRelativePath($package,$_.FullName); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    @{version=1; product=$product.Name; files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding utf8
    $compilerArgs = @('/Qp', "/DProductName=$($product.ProductName)", "/DProductExe=$($product.Exe)", "/DProductAppId=$($product.AppId)", "/DProductVersion=$Version", "/DPackageDirectory=$package", "/DReleaseDirectory=$release", "/DProductDataDirectory={localappdata}\$($product.ProductName)Installed", (Join-Path $PSScriptRoot 'packaging/windows-products.iss'))
    & $compiler @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed: $($product.Name)" }
    $setup = Join-Path $release "$($product.ProductName)-$Version-win-x64-Setup.exe"
    if (!(Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'The compiler did not create the expected Setup.exe.' }
    [IO.Compression.ZipFile]::CreateFromDirectory($package, (Join-Path $release "$($product.ProductName)-$Version-win-x64-portable.zip"), [IO.Compression.CompressionLevel]::Optimal, $false)
}
$sourceRevision = $null
$sourceDirty = $null
if (Get-Command git -ErrorAction SilentlyContinue) {
    $revisionResult = & git -C $PSScriptRoot rev-parse HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $revisionResult -match '^[0-9a-f]{40}$') {
        $sourceRevision = $revisionResult
        $workingChanges = @(& git -C $PSScriptRoot status --porcelain --untracked-files=normal 2>$null)
        if ($LASTEXITCODE -ne 0) { throw 'Could not determine release source cleanliness.' }
        $sourceDirty = $workingChanges.Count -ne 0
    }
}
$packageManifest = @(foreach ($product in $products) {
    $package = Join-Path $packages $product.Name
    foreach ($file in Get-ChildItem -LiteralPath $package -File -Recurse) {
        @{product=$product.Name; file=[IO.Path]::GetRelativePath($package,$file.FullName); bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
    }
})
$packageManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packages 'manifest.json') -Encoding utf8
$assets = @(Get-ChildItem -LiteralPath $release -File | Sort-Object Name | ForEach-Object {
    @{file=$_.Name; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
@{schemaVersion=1; version=$Version; runtimeIdentifier='win-x64'; selfContained=$true; signed=$false; sourceRevision=$sourceRevision; sourceDirty=$sourceDirty;
    python=@{url=$PythonEmbedUrl.AbsoluteUri; sha256=$PythonEmbedSha256.ToLowerInvariant()}; files=$assets} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $release 'release-manifest.json') -Encoding utf8
$checksums = @(Get-ChildItem -LiteralPath $release -File | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
})
[IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), $checksums, [Text.UTF8Encoding]::new($false))
Write-Output "Unsigned Windows x64 release: $release"
