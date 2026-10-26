#requires -Version 5.1
param(
    [Parameter(Mandatory)][string]$ProductRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SourceRoot
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrEmpty($SourceRoot)) {
    $SourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
}
if ($ProductRoot -notmatch '^(?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/]+[\\/][^\\/]+)' -or
    $OutputDirectory -notmatch '^(?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/]+[\\/][^\\/]+)') {
    throw 'Explicit absolute package and new output directories are required.'
}
$packageRoot = [IO.Path]::GetFullPath($ProductRoot)
$testOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $testOutput) { throw 'Test output must not exist.' }
foreach ($candidate in @($packageRoot, (Split-Path -Parent $testOutput))) {
    for ($parent = [IO.DirectoryInfo]::new($candidate); $null -ne $parent; $parent = $parent.Parent) {
        if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected paths rejected.' }
    }
}
if ($testOutput.StartsWith($packageRoot.TrimEnd('\', '/') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test evidence must be outside the inspected distribution.'
}
[void][IO.Directory]::CreateDirectory($testOutput)
$checks = [Collections.Generic.List[object]]::new()
$sourceHashes = foreach ($relative in @('Start-Assistant.ps1', 'Start-Dashboard.ps1', 'Build-Products.ps1',
    'tests/Product.Lifecycle.Tests/Test-Packaging.ps1')) {
    [pscustomobject]@{file=$relative;sha256=(Get-FileHash -LiteralPath (Join-Path $SourceRoot $relative) -Algorithm SHA256).Hash}
}
function Check([string]$Name, [scriptblock]$Action) {
    try { & $Action; $checks.Add([pscustomobject]@{name=$Name;passed=$true}) }
    catch { $checks.Add([pscustomobject]@{name=$Name;passed=$false;errorType=$_.Exception.GetType().Name;reason=$_.Exception.Message}) }
    $failures = @($checks | Where-Object { -not $_.passed }).Count
    [pscustomobject]@{
        scope='package-files-and-pure-launch-parameters'
        powershellVersion=$PSVersionTable.PSVersion.ToString()
        productRoot=$packageRoot
        sourceHashes=$sourceHashes
        count=$checks.Count
        failures=$failures
        processStarted=$false
        productionConfigurationRead=$false
        windowsProjectBuilt=$false
        runtimePrerequisitesVerifiedInstalled=$false
        checks=$checks
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $testOutput 'packaging-checks.json') -Encoding utf8
}
function Require([bool]$Value) { if (-not $Value) { throw 'Assertion failed.' } }
function Hash-UpperText([string]$Value) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value.ToUpperInvariant()))).Replace('-', '') }
    finally { $hasher.Dispose() }
}
function Expect-Throws([scriptblock]$Action) {
    try { & $Action | Out-Null }
    catch { return }
    throw 'Expected rejection did not occur.'
}
function Source-Ast([string]$Path) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if (@($errors).Count) { throw 'Source script has parse errors.' }
    return $ast
}

foreach ($product in @('Assistant', 'Dashboard')) {
    $source = Join-Path $SourceRoot "Start-$product.ps1"
    $ast = Source-Ast $source
    # Load only reviewed function definitions and the P/Invoke helper type.
    # Do not dot-source the launcher: its process enumeration/COM launch is excluded.
    foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $typeName = "Codex${product}ProcessMembership"
    $nativeSource = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.StringConstantExpressionAst] -and
            $node.Value.Contains('public static class Codex') -and $node.Value.Contains('CommandLineToArgvW')
    }, $false))
    if ($nativeSource.Count -ne 1) { throw 'Expected one isolated command-line parser type.' }
    if (-not ($typeName -as [type])) { Add-Type -TypeDefinition $nativeSource[0].Value }
    $native = $typeName -as [type]
    $fixture = Join-Path $testOutput $product
    [void][IO.Directory]::CreateDirectory($fixture)
    $exe = Join-Path $fixture "Codex$product.exe"
    [IO.File]::WriteAllText($exe, 'Synthetic placeholder. This file is never executed.')
    $selector = Join-Path $fixture 'data-directory.txt'
    $data = Join-Path $fixture '用户 folder with spaces'
    $different = Join-Path $fixture 'another-profile'

    Check "$product.launcher_default_parameters_remain_package_relative" {
        # Execute only the parameter block in a synthetic script. No launcher
        # body, process enumeration, COM call, or executable is invoked.
        $parameterProbe = Join-Path $fixture 'parameter-probe.ps1'
        $probeBody = $ast.ParamBlock.Extent.Text + "`r`n" +
            '[pscustomobject]@{ExecutablePath=$ExecutablePath;WorkingDirectory=$WorkingDirectory;ProtocolProfile=$ProtocolProfile}'
        [IO.File]::WriteAllText($parameterProbe, $probeBody, [Text.UTF8Encoding]::new($true))
        $observed = & $parameterProbe
        $relative = if ($product -eq 'Assistant') { 'artifacts\assistant\CodexAssistant.exe' } else { 'artifacts\app\CodexDashboard.exe' }
        Require ($observed.ExecutablePath -eq (Join-Path $fixture $relative))
        Require ($observed.WorkingDirectory -eq $fixture -and $observed.ProtocolProfile -ceq 'default')
    }
    Check "$product.default_without_selector_preserves_default_launch" {
        $launch = Resolve-ProductLaunch $exe '' 'default'
        $parsedArguments = $native::ParseArguments((ConvertTo-ProductWindowsArgument $exe) + ' ' + $launch.Arguments)
        Require ($launch.Mode -eq 'default' -and $null -eq $launch.DataDirectory -and $parsedArguments -notcontains '--data-dir')
        Require (Test-ProductCommandIdentity $launch (Get-ProductCommandIdentity $parsedArguments))
        Require (Test-ProductCommandIdentity $launch (Get-ProductCommandIdentity @($exe)))
    }
    Check "$product.explicit_directory_and_profile_roundtrip" {
        $launch = Resolve-ProductLaunch $exe $data 'pair "unicode λ" \ end'
        $parsedArguments = $native::ParseArguments((ConvertTo-ProductWindowsArgument $exe) + ' ' + $launch.Arguments)
        Require ($parsedArguments.Count -eq 5 -and $parsedArguments[2] -ceq $data -and $parsedArguments[4] -ceq 'pair "unicode λ" \ end')
        Require (Test-ProductCommandIdentity $launch (Get-ProductCommandIdentity $parsedArguments))
    }
    Check "$product.windows_argument_quoting_roundtrips_special_characters" {
        foreach ($value in @('', 'space path', '中文', 'literal $() ` and apostrophe''', 'quoted"value', 'C:\trailing\', 'slash\\\"quote', 'C:\two trailing\\')) {
            $parsed = $native::ParseArguments((ConvertTo-ProductWindowsArgument $exe) + ' ' + (ConvertTo-ProductWindowsArgument $value))
            Require ($parsed.Count -eq 2 -and $parsed[1] -ceq $value)
        }
    }
    Check "$product.other_directory_is_not_same_process_profile" {
        $launch = Resolve-ProductLaunch $exe $data 'same-protocol'
        $actual = Get-ProductCommandIdentity @($exe, '--data-dir', $different, '--protocol-profile', 'same-protocol')
        Require (-not (Test-ProductCommandIdentity $launch $actual))
    }
    Check "$product.default_and_explicit_profiles_are_not_inferred_equivalent" {
        $launch = Resolve-ProductLaunch $exe $data 'default'
        Require (-not (Test-ProductCommandIdentity $launch (Get-ProductCommandIdentity @($exe))))
    }
    Check "$product.protocol_profile_is_exact_and_case_sensitive" {
        $launch = Resolve-ProductLaunch $exe $data 'Pair'
        Require (-not (Test-ProductCommandIdentity $launch (Get-ProductCommandIdentity @($exe, '--data-dir', $data, '--protocol-profile', 'pair'))))
    }
    Check "$product.legacy_explicit_directory_retains_host_derived_profile" {
        $actual = Get-ProductCommandIdentity @($exe, '--data-dir', $data)
        $expectedHash = (Hash-UpperText $data).Substring(0,24)
        Require ($actual.ProtocolProfile -ceq $expectedHash)
        Require (-not (Test-ProductCommandIdentity (Resolve-ProductLaunch $exe $data 'default') $actual))
    }
    Check "$product.trailing_separator_existing_hash_is_not_guessed_equivalent" {
        $actual = Get-ProductCommandIdentity @($exe, '--data-dir', ($data + '\'))
        $expectedHash = (Hash-UpperText ($data + '\')).Substring(0,24)
        Require ($actual.ProtocolProfile -ceq $expectedHash)
        Require (-not (Test-ProductCommandIdentity (Resolve-ProductLaunch $exe $data $expectedHash) $actual))
    }
    Check "$product.relative_and_drive_relative_directories_rejected" {
        foreach ($invalid in @('relative', 'C:relative', '\root-relative', "C:\bad`npath")) {
            Expect-Throws { Resolve-ProductLaunch $exe $invalid 'default' }
        }
    }
    Check "$product.duplicate_missing_or_unsupported_arguments_rejected" {
        foreach ($invalid in @(
            @($exe, '--data-dir'),
            @($exe, '--data-dir', $data, '--data-dir', $different),
            @($exe, '--protocol-profile', 'a', '--protocol-profile', 'b'),
            @($exe, '--DATA-DIR', $data),
            @($exe, '--isolated-lifecycle-test', $data),
            @($exe, '--data-dir', '--background')
        )) { Expect-Throws { Get-ProductCommandIdentity $invalid } }
        Expect-Throws { $native::ParseArguments('') }
        Expect-Throws { Get-ProductCommandIdentity @() }
    }
    Check "$product.valid_selector_is_passed_explicitly" {
        [IO.File]::WriteAllText($selector, $data, [Text.UTF8Encoding]::new($false))
        $launch = Resolve-ProductLaunch $exe '' 'default'
        Require ($launch.DataDirectorySource -eq 'selector' -and $launch.DataDirectory -eq $data)
        Require (-not (Test-ProductCommandIdentity $launch (Get-ProductCommandIdentity @($exe))))
        Require (Test-ProductCommandIdentity $launch (Get-ProductCommandIdentity ($native::ParseArguments(
            (ConvertTo-ProductWindowsArgument $exe) + ' ' + $launch.Arguments))))
    }
    Check "$product.explicit_directory_overrides_selector_without_reading_it" {
        [IO.File]::WriteAllBytes($selector, [byte[]]@(255,255))
        Require ((Resolve-ProductLaunch $exe $data 'default').DataDirectorySource -eq 'explicit')
    }
    Check "$product.invalid_selector_never_falls_back_silently" {
        foreach ($invalid in @('', 'relative', ('x' * 8193))) {
            [IO.File]::WriteAllText($selector, $invalid)
            Expect-Throws { Resolve-ProductLaunch $exe '' 'default' }
        }
        [IO.File]::WriteAllBytes($selector, [byte[]]@(255,255))
        Expect-Throws { Resolve-ProductLaunch $exe '' 'default' }
    }
}

$buildAst = Source-Ast (Join-Path $SourceRoot 'Build-Products.ps1')
foreach ($definition in $buildAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
Check 'existing_packages_manifest_hashes_and_inventory_match' {
    Require ((Assert-ProductManifest $packageRoot) -gt 0)
}
foreach ($product in @('dashboard','assistant')) {
    Check "$product.existing_package_has_all_declared_local_runtime_dependencies" {
        $observed = Assert-ProductPackage (Join-Path $packageRoot $product) $product
        Require ($observed.FrameworkDependent -and $observed.RuntimeAssets.Count -gt 0)
        $observed | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $testOutput "$product-dependencies.json") -Encoding utf8
    }
}
Check 'both_collectors_and_assistant_proxy_match_reviewed_source' {
    $expected = (Get-FileHash -LiteralPath (Join-Path $SourceRoot 'collector.py')).Hash
    Require ((Get-FileHash -LiteralPath (Join-Path $packageRoot 'dashboard\collector.py')).Hash -eq $expected)
    Require ((Get-FileHash -LiteralPath (Join-Path $packageRoot 'assistant\collector.py')).Hash -eq $expected)
    Require ((Get-FileHash -LiteralPath (Join-Path $packageRoot 'assistant\remote_socket_proxy.py')).Hash -eq
        (Get-FileHash -LiteralPath (Join-Path $SourceRoot 'remote_socket_proxy.py')).Hash)
}

# Mutations below affect only new copies of already validated public distributions.
# Each negative fixture is retained; there is no delete, move, process or build.
function Copy-PackageFixture([string]$Name, [string]$Skip = '') {
    $source = Join-Path $packageRoot 'assistant'
    [void](Assert-ProductPackage $source 'assistant')
    $target = Join-Path $testOutput $Name
    if (Test-Path -LiteralPath $target) { throw 'Fixture path already exists.' }
    [void][IO.Directory]::CreateDirectory($target)
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        $relative = $file.FullName.Substring($source.TrimEnd('\', '/').Length + 1)
        if ($relative -eq $Skip) { continue }
        $destination = Join-Path $target $relative
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        [IO.File]::Copy($file.FullName, $destination, $false)
    }
    return $target
}
Check 'missing_remote_proxy_rejected' {
    $fixture = Copy-PackageFixture 'missing-proxy' 'remote_socket_proxy.py'
    Expect-Throws { Assert-ProductPackage $fixture 'assistant' }
}
Check 'missing_declared_managed_dependency_rejected' {
    $fixture = Copy-PackageFixture 'missing-qrcode' 'QRCoder.dll'
    Expect-Throws { Assert-ProductPackage $fixture 'assistant' }
}
Check 'launcher_with_repository_relative_default_rejected' {
    $fixture = Copy-PackageFixture 'wrong-launcher'
    $path = Join-Path $fixture 'Start-Assistant.ps1'
    $body = [IO.File]::ReadAllText($path).Replace("'CodexAssistant.exe'", "'artifacts\assistant\CodexAssistant.exe'")
    [IO.File]::WriteAllText($path, $body)
    Expect-Throws { Assert-ProductPackage $fixture 'assistant' }
}
Check 'missing_framework_declaration_rejected' {
    $fixture = Copy-PackageFixture 'wrong-runtime'
    [IO.File]::WriteAllText((Join-Path $fixture 'CodexAssistant.runtimeconfig.json'), '{"runtimeOptions":{"frameworks":[]}}')
    Expect-Throws { Assert-ProductPackage $fixture 'assistant' }
}
Check 'foreign_product_dependency_rejected' {
    $fixture = Copy-PackageFixture 'foreign-product'
    [IO.File]::WriteAllText((Join-Path $fixture 'CodexDashboard.dll'), 'synthetic foreign assembly marker')
    Expect-Throws { Assert-ProductPackage $fixture 'assistant' }
}
$manifestFixture = Join-Path $testOutput 'manifest-fixture'
[void][IO.Directory]::CreateDirectory((Join-Path $manifestFixture 'assistant'))
[void][IO.Directory]::CreateDirectory((Join-Path $manifestFixture 'dashboard'))
$samplePath = Join-Path $manifestFixture 'assistant\sample.txt'
[IO.File]::WriteAllText($samplePath, 'synthetic manifest content')
$sampleHash = (Get-FileHash -LiteralPath $samplePath).Hash
$sampleLength = (Get-Item -LiteralPath $samplePath).Length
function Write-FixtureManifest($Entries) {
    ConvertTo-Json -InputObject @($Entries) -Depth 5 |
        Set-Content -LiteralPath (Join-Path $manifestFixture 'manifest.json') -Encoding utf8
}
$validEntry = @{product='assistant';file='sample.txt';sha256=$sampleHash;bytes=$sampleLength}
Check 'synthetic_manifest_positive_control' {
    Write-FixtureManifest @($validEntry)
    Require ((Assert-ProductManifest $manifestFixture) -eq 1)
}
Check 'manifest_duplicate_entries_rejected' {
    Write-FixtureManifest @($validEntry, $validEntry)
    Expect-Throws { Assert-ProductManifest $manifestFixture }
}
Check 'manifest_path_escape_rejected_before_read' {
    Write-FixtureManifest @(@{product='assistant';file='..\..\outside.txt';sha256=$sampleHash;bytes=$sampleLength})
    Expect-Throws { Assert-ProductManifest $manifestFixture }
}
Check 'manifest_changed_hash_or_length_rejected' {
    Write-FixtureManifest @(@{product='assistant';file='sample.txt';sha256=('0' * 64);bytes=$sampleLength})
    Expect-Throws { Assert-ProductManifest $manifestFixture }
    Write-FixtureManifest @(@{product='assistant';file='sample.txt';sha256=$sampleHash;bytes=($sampleLength+1)})
    Expect-Throws { Assert-ProductManifest $manifestFixture }
}
Check 'manifest_unknown_product_rejected' {
    Write-FixtureManifest @(@{product='other';file='sample.txt';sha256=$sampleHash;bytes=$sampleLength})
    Expect-Throws { Assert-ProductManifest $manifestFixture }
}
Check 'manifest_unlisted_file_rejected' {
    Write-FixtureManifest @($validEntry)
    [IO.File]::WriteAllText((Join-Path $manifestFixture 'assistant\unlisted.txt'), 'synthetic')
    Expect-Throws { Assert-ProductManifest $manifestFixture }
}
Check 'manifest_path_alias_cannot_hide_unlisted_file' {
    # There are now two files. Two spellings of sample.txt must not satisfy
    # inventory coverage by count while omitting unlisted.txt.
    Write-FixtureManifest @($validEntry,
        @{product='assistant';file='.\sample.txt';sha256=$sampleHash;bytes=$sampleLength})
    Expect-Throws { Assert-ProductManifest $manifestFixture }
}
$failed = @($checks | Where-Object { -not $_.passed }).Count
Write-Output "Packaging parameter/dependency checks: $($checks.Count); failures: $failed; no processes started."
if ($failed) { exit 1 }
