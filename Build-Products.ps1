#requires -Version 7.0
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot ('artifacts\builds\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string]$Configuration = 'Release',
    [switch]$SelfContained,
    [ValidateSet('win-x64')][string]$RuntimeIdentifier = 'win-x64',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$PythonEmbedArchive,
    [string]$PythonEmbedSha256
)

function Assert-ProductPackage([string]$Directory, [string]$Product) {
    if ($Product -notin @('dashboard', 'assistant')) { throw 'Unknown product.' }
    $name = if ($Product -eq 'dashboard') { 'CodexDashboard' } else { 'CodexAssistant' }
    $other = if ($Product -eq 'dashboard') { 'CodexAssistant' } else { 'CodexDashboard' }
    $launcherName = if ($Product -eq 'dashboard') { 'Start-Dashboard.ps1' } else { 'Start-Assistant.ps1' }
    $required = @("$name.exe", "$name.dll", "$name.deps.json", "$name.runtimeconfig.json",
        'collector.py', 'THIRD-PARTY-NOTICES.txt', $launcherName)
    if ($Product -eq 'assistant') { $required += @('remote_socket_proxy.py', 'Backup-AssistantData.ps1', 'Test-AssistantBackup.ps1', 'Prepare-AssistantRecovery.ps1', 'BACKUP.md') }
    else { $required += @('Enable-LanAccess.ps1', 'web/index.html', 'web/app.js', 'web/app.css', 'web/model.mjs') }
    foreach ($relative in $required) {
        if (-not [IO.File]::Exists((Join-Path $Directory $relative))) { throw "Required package file missing: $Product/$relative" }
    }
    if ([IO.File]::Exists((Join-Path $Directory "$other.exe")) -or [IO.File]::Exists((Join-Path $Directory "$other.dll"))) {
        throw 'A product package contains the other product.'
    }
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected package entries are not permitted.' }
        if (-not $item.PSIsContainer -and ($item.Name -match '(?i)(credentials|^data-directory\.txt$|^settings\.json$|^dashboard\.settings\.json$|\.state$)')) {
            throw 'The distribution contains user data or credentials.'
        }
    }
    $deps = Get-Content -LiteralPath (Join-Path $Directory "$name.deps.json") -Raw | ConvertFrom-Json
    $target = $deps.targets.PSObject.Properties[$deps.runtimeTarget.name].Value
    if ($null -eq $target) { throw 'Dependency target is missing.' }
    $assemblies = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($library in $target.PSObject.Properties) {
        if ($library.Name.StartsWith("$other/", [StringComparison]::OrdinalIgnoreCase)) { throw 'Cross-product runtime dependency.' }
        foreach ($group in @('runtime', 'native', 'resources')) {
            $assets = $library.Value.PSObject.Properties[$group]
            if ($null -eq $assets) { continue }
            foreach ($asset in $assets.Value.PSObject.Properties) {
                $fileName = [IO.Path]::GetFileName($asset.Name)
                if ($fileName -eq '_._') { continue }
                $relative = if ($group -eq 'resources') {
                    Join-Path $asset.Value.locale $fileName
                } else { $fileName }
                if (-not [IO.File]::Exists((Join-Path $Directory $relative))) { throw "Local runtime asset missing: $Product/$relative" }
                [void]$assemblies.Add($relative)
            }
        }
        if (!$SelfContained -and $null -ne $library.Value.PSObject.Properties['runtimeTargets']) {
            throw 'RID-specific runtime assets require an explicit package validation rule.'
        }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $Directory "$name.runtimeconfig.json") -Raw | ConvertFrom-Json
    $frameworks = if ($SelfContained) { @($runtime.runtimeOptions.includedFrameworks) } else { @($runtime.runtimeOptions.frameworks) }
    foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App', 'Microsoft.AspNetCore.App')) {
        if (@($frameworks | Where-Object { $_.name -eq $framework -and $_.version -match '^10\.' }).Count -ne 1) {
            throw "Required .NET 10 framework declaration missing: $framework"
        }
    }
    if ($SelfContained) {
        if ($null -ne $runtime.runtimeOptions.PSObject.Properties['frameworks']) { throw 'Self-contained product still requires shared frameworks.' }
        foreach ($asset in @('hostfxr.dll','hostpolicy.dll','coreclr.dll','System.Private.CoreLib.dll','PresentationFramework.dll','Microsoft.AspNetCore.dll','python/python.exe','python/LICENSE.txt')) {
            if (![IO.File]::Exists((Join-Path $Directory $asset))) { throw "Self-contained asset missing: $asset" }
        }
        & (Join-Path $Directory 'python/python.exe') -I -c 'import ctypes, hashlib, json, pathlib, sqlite3, ssl; c=sqlite3.connect(":memory:"); assert c.execute("select 42").fetchone()[0] == 42; print("Bundled Python and SQLite available")'
        if ($LASTEXITCODE -ne 0) { throw 'Bundled Python/SQLite verification failed.' }
    }
    $launcher = [IO.File]::ReadAllText((Join-Path $Directory $launcherName))
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($launcher, [ref]$tokens, [ref]$parseErrors)
    if (@($parseErrors).Count -ne 0) { throw 'Published launcher has syntax errors.' }
    $executableParameter = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'ExecutablePath' })
    if ($executableParameter.Count -ne 1 -or $executableParameter[0].DefaultValue.Extent.Text -ne "(Join-Path `$PSScriptRoot '$name.exe')") {
        throw 'Published launcher does not target its own package executable.'
    }
    [pscustomobject]@{
        Product = $Product
        RuntimeAssets = @($assemblies | Sort-Object)
        RequiredFrameworks = $frameworks
        FrameworkDependent = !$SelfContained
        PythonRequired = if ($SelfContained) { 'bundled' } else { '3.10+ (local collection)' }
        RemoteRequirements = @('Windows OpenSSH client', 'configured SSH alias', 'remote python3', 'remote Codex environment')
    }
}

function Assert-ProductManifest([string]$Root) {
    $manifest = Get-Content -LiteralPath (Join-Path $Root 'manifest.json') -Raw | ConvertFrom-Json
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($manifest)) {
        if ($entry.product -notin @('dashboard', 'assistant') -or [string]::IsNullOrWhiteSpace($entry.file) -or
            [IO.Path]::IsPathRooted($entry.file)) { throw 'Invalid product manifest entry.' }
        $packageRoot = [IO.Path]::GetFullPath((Join-Path $Root $entry.product)).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        $path = [IO.Path]::GetFullPath((Join-Path $packageRoot $entry.file))
        if (-not $path.StartsWith($packageRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not $seen.Add($path)) { throw 'Escaping or duplicate manifest entry.' }
        for ($parent = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($path)); $null -ne $parent; $parent = $parent.Parent) {
            if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Redirected manifest paths are not permitted.'
            }
        }
        $file = Get-Item -LiteralPath $path
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $file.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $entry.sha256) {
            throw 'Product manifest content mismatch.'
        }
    }
    $actual = @(foreach ($product in @('dashboard', 'assistant')) {
        Get-ChildItem -LiteralPath (Join-Path $Root $product) -File -Recurse -Force
    })
    if ($seen.Count -ne $actual.Count) { throw 'Product manifest does not cover every packaged file.' }
    return $seen.Count
}

function Copy-DotnetRuntimeLicenses([string]$Project, [string]$Directory, [string]$ProductExecutable) {
    $assetsPath = Join-Path ([IO.Path]::GetDirectoryName($Project)) 'obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
    $runtime = Get-Content -LiteralPath (Join-Path $Directory "$ProductExecutable.runtimeconfig.json") -Raw | ConvertFrom-Json
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        $packageName = $framework.name.ToLowerInvariant() + '.runtime.' + $RuntimeIdentifier
        $packageRoot = $null
        foreach ($folder in $packageFolders) {
            $candidate = Join-Path $folder "$packageName/$($framework.version)"
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packageRoot=$candidate; break }
        }
        if (!$packageRoot) { throw "Restored runtime pack not found for license redistribution: $packageName" }
        $notices = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match '^(?i:LICENSE(?:\..*)?|THIRD[-_]?PARTY[-_]?NOTICES?(?:\..*)?)$' })
        if (@($notices | Where-Object Name -match '^(?i:LICENSE)').Count -eq 0) { throw "Runtime pack license missing: $packageName" }
        $licenseDirectory = Join-Path $Directory "licenses/dotnet/$packageName/$($framework.version)"
        [void][IO.Directory]::CreateDirectory($licenseDirectory)
        foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $licenseDirectory }
    }
}

$ErrorActionPreference = 'Stop'
if ($SelfContained) {
    if (!$PythonEmbedArchive -or $PythonEmbedSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Self-contained products require a verified CPython embeddable archive and its official SHA256.' }
    $PythonEmbedArchive = [IO.Path]::GetFullPath($PythonEmbedArchive)
    if ((Get-FileHash -LiteralPath $PythonEmbedArchive -Algorithm SHA256).Hash -ne $PythonEmbedSha256) { throw 'CPython embeddable archive SHA256 mismatch.' }
}
$productOutput = [IO.Path]::GetFullPath($OutputDirectory)
for ($parent = [IO.DirectoryInfo]::new($productOutput); $null -ne $parent; $parent = $parent.Parent) {
    if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Build output must not be redirected.'
    }
}
if (Test-Path -LiteralPath $productOutput) {
    if (@(Get-ChildItem -LiteralPath $productOutput -Force).Count -gt 0) {
        throw "Choose a new output directory; existing artifacts are preserved: $productOutput"
    }
}
New-Item -ItemType Directory -Path $productOutput -Force | Out-Null
$buildTemp = Join-Path $productOutput 'build-temp'
New-Item -ItemType Directory -Path $buildTemp -Force | Out-Null
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$previousNuGetHttpCache = $env:NUGET_HTTP_CACHE_PATH
$previousNuGetScratch = $env:NUGET_SCRATCH
$previousDotnetCliHome = $env:DOTNET_CLI_HOME
$previousDotnetCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
$previousDotnetAddTools = $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH
try {
    $env:TEMP = $buildTemp
    $env:TMP = $buildTemp
    # Keep restore and vulnerability metadata in this build's writable workspace.
    # Do not disable NuGet audit when a user's default cache is inaccessible.
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $buildTemp 'nuget-http-cache'
    $env:NUGET_SCRATCH = Join-Path $buildTemp 'nuget-scratch'
    $env:DOTNET_CLI_HOME = Join-Path $buildTemp 'dotnet-cli-home'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    New-Item -ItemType Directory -Path $env:NUGET_HTTP_CACHE_PATH, $env:NUGET_SCRATCH -Force | Out-Null
    $products = @(
        @{Name='dashboard'; Project='CodexDashboard.csproj'; Launcher='Start-Dashboard.ps1'; OldPath='artifacts\app\CodexDashboard.exe'; Exe='CodexDashboard.exe'},
        @{Name='assistant'; Project='src\Assistant.Windows\Assistant.Windows.csproj'; Launcher='Start-Assistant.ps1'; OldPath='artifacts\assistant\CodexAssistant.exe'; Exe='CodexAssistant.exe'}
    )
    foreach ($product in $products) {
        $destination = Join-Path $productOutput $product.Name
        $project = Join-Path $PSScriptRoot $product.Project
        $publishArguments = @('publish', $project, '-c', $Configuration, '--self-contained', $SelfContained.IsPresent.ToString().ToLowerInvariant(), '-o', $destination, '-m:1', '-nr:false', '--nologo')
        if ($SelfContained) { $publishArguments += @('-r', $RuntimeIdentifier, '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false') }
        if ($Version) { $publishArguments += "-p:Version=$Version" }
        & dotnet @publishArguments
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($product.Name)" }
        if ($SelfContained) {
            Expand-Archive -LiteralPath $PythonEmbedArchive -DestinationPath (Join-Path $destination 'python')
            @{schemaVersion=1;mode='portable';product=$product.Name} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'distribution.json') -Encoding utf8
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $destination
            Copy-DotnetRuntimeLicenses $project $destination ([IO.Path]::GetFileNameWithoutExtension($product.Exe))
        }
        $launcher = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $product.Launcher))
        if ([regex]::Matches($launcher, [regex]::Escape($product.OldPath)).Count -ne 1) {
            throw 'Launcher default-path replacement is not unambiguous.'
        }
        $launcher = $launcher.Replace($product.OldPath, $product.Exe)
        [IO.File]::WriteAllText((Join-Path $destination $product.Launcher), $launcher, [Text.UTF8Encoding]::new($true))
        if ($product.Name -eq 'assistant') {
            foreach ($script in @('Backup-AssistantData.ps1', 'Test-AssistantBackup.ps1', 'Prepare-AssistantRecovery.ps1')) {
                Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $destination
            }
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs/assistant-backup.md') -Destination (Join-Path $destination 'BACKUP.md')
        }
        foreach ($script in @('Install-Product.ps1', 'Launch-InstalledProduct.ps1', 'Uninstall-Product.ps1', 'Install-ProductGui.ps1', 'Add-ProductShortcut.ps1', 'Setup.cmd')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $destination
        }
        $packageFiles = @(Get-ChildItem -LiteralPath $destination -File -Recurse | ForEach-Object {
            [pscustomobject]@{ file=[IO.Path]::GetRelativePath($destination, $_.FullName); bytes=$_.Length;
                sha256=(Get-FileHash -LiteralPath $_.FullName).Hash }
        })
        @{ version=1; product=$product.Name; files=$packageFiles } | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $destination 'package-manifest.json') -Encoding utf8
        [void](Assert-ProductPackage $destination $product.Name)
    }
    $manifest = foreach ($product in $products) {
        $destination = Join-Path $productOutput $product.Name
        foreach ($file in Get-ChildItem -LiteralPath $destination -File -Recurse) {
            [pscustomobject]@{
                product = $product.Name
                file = [IO.Path]::GetRelativePath($destination, $file.FullName)
                sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
                bytes = $file.Length
            }
        }
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $productOutput 'manifest.json') -Encoding utf8
    [void](Assert-ProductManifest $productOutput)
    Write-Output "Independent products: $productOutput"
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    $env:NUGET_HTTP_CACHE_PATH = $previousNuGetHttpCache
    $env:NUGET_SCRATCH = $previousNuGetScratch
    $env:DOTNET_CLI_HOME = $previousDotnetCliHome
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $previousDotnetCertificate
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = $previousDotnetAddTools
}
