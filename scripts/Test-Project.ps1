#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (!$IsWindows) { throw 'Run these Windows isolation tests in PowerShell 7 on Windows.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repository ('artifacts/tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
if (![IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'OutputDirectory must be an absolute new directory.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory already exists. Preserve results and choose a new directory.' }
[void][IO.Directory]::CreateDirectory($output)
$temporary = Join-Path $output 'temp'
[void][IO.Directory]::CreateDirectory($temporary)
$environmentNames = @('TEMP', 'TMP', 'NUGET_HTTP_CACHE_PATH', 'NUGET_SCRATCH', 'DOTNET_CLI_HOME', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'PYTHONDONTWRITEBYTECODE', 'PYTHONUTF8')
$previousEnvironment = @{}
foreach ($name in $environmentNames) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$env:TEMP = $temporary
$env:TMP = $temporary
$env:NUGET_HTTP_CACHE_PATH = Join-Path $output 'nuget-http-cache'
$env:NUGET_SCRATCH = Join-Path $output 'nuget-scratch'
$env:DOTNET_CLI_HOME = Join-Path $output 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
$env:PYTHONUTF8 = '1'
$results = [Collections.Generic.List[object]]::new()
$started = [DateTimeOffset]::UtcNow
function Invoke-Check {
    param([string]$Name, [string]$Executable, [string[]]$Arguments)
    Write-Host "Testing $Name"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $output ($Name + '.log')
    & $Executable @Arguments 2>&1 | Tee-Object -LiteralPath $log | Out-Host
    $exitCode = $LASTEXITCODE
    $results.Add([ordered]@{ name = $Name; exitCode = $exitCode; elapsedMs = $timer.ElapsedMilliseconds; log = [IO.Path]::GetFileName($log) })
    [ordered]@{ syntheticOnly = $true; startedAt = $started; updatedAt = [DateTimeOffset]::UtcNow; checks = @($results.ToArray()) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
    if ($exitCode -ne 0) { throw "$Name failed with exit code $exitCode. See $log" }
}
function Invoke-ConsoleSuite {
    param([string]$Project, [string[]]$TestArguments = @(), [string]$Name = $Project)
    $projectFile = Join-Path $repository "tests/$Project/$Project.csproj"
    Invoke-Check -Name ($Name + '-build') -Executable 'dotnet' -Arguments @('build', $projectFile, '-c', $Configuration, '-m:1', '-nr:false', '--nologo')
    $assembly = Join-Path $repository "tests/$Project/bin/$Configuration/net10.0/$Project.dll"
    Invoke-Check -Name $Name -Executable 'dotnet' -Arguments (@($assembly) + $TestArguments)
}
Push-Location $repository
try {
    # These suites use new synthetic fixtures, isolated pipe names, and fake model/channel transports.
    # No product GUI, live Codex task, real collector/SSH, or user credential store is opened.
    Invoke-Check -Name 'python' -Executable 'python' -Arguments @('-m', 'unittest', 'discover', '-s', 'tests', '-p', 'test_*.py', '-v')
    Invoke-Check -Name 'web-model' -Executable 'node' -Arguments @('--test', 'tests/web_model.test.mjs')
    Invoke-ConsoleSuite -Project 'Codex.ReadOnly.Tests' -TestArguments @((Join-Path $output 'readonly'))
    Invoke-ConsoleSuite -Project 'Codex.Protocol.Tests'
    Invoke-ConsoleSuite -Project 'Codex.HostRegistry.Tests' -TestArguments @('--output', (Join-Path $output 'host-registry'))
    Invoke-ConsoleSuite -Project 'Codex.Migration.Tests' -TestArguments @('--output', (Join-Path $output 'migration'))
    # The optional historical generated-schema comparison requires a frozen artifact and is not a clean-checkout test.
    Invoke-ConsoleSuite -Project 'Codex.Control.Tests' -TestArguments @((Join-Path $output 'control'))
    Invoke-ConsoleSuite -Project 'OwnedControlAdapter.Tests' -TestArguments @((Join-Path $output 'owned-adapter'))
    $ownedAssembly = Join-Path $repository "tests/OwnedControlAdapter.Tests/bin/$Configuration/net10.0/OwnedControlAdapter.Tests.dll"
    Invoke-Check -Name 'owned-configuration' -Executable 'dotnet' -Arguments @($ownedAssembly, '--configuration', (Join-Path $output 'owned-configuration'))
    Invoke-ConsoleSuite -Project 'WorkspaceWriteCoordinator.Tests' -TestArguments @((Join-Path $output 'workspace-coordinator'))
    Invoke-ConsoleSuite -Project 'Assistant.Core.Tests'
    $coreAssembly = Join-Path $repository "tests/Assistant.Core.Tests/bin/$Configuration/net10.0/Assistant.Core.Tests.dll"
    Invoke-Check -Name 'core-inspection' -Executable 'dotnet' -Arguments @($coreAssembly, '--inspection-tests')
    Invoke-Check -Name 'backup' -Executable 'pwsh' -Arguments @('-NoLogo', '-NoProfile', '-File', (Join-Path $repository 'tests/Backup-AssistantData.Tests.ps1'), '-OutputDirectory', (Join-Path $output 'backup'))
    Invoke-Check -Name 'backup-integrity' -Executable 'pwsh' -Arguments @('-NoLogo', '-NoProfile', '-File', (Join-Path $repository 'tests/AssistantBackupIntegrity.Tests.ps1'), '-OutputDirectory', (Join-Path $output 'backup-integrity'))
    Invoke-Check -Name 'dashboard-build' -Executable 'dotnet' -Arguments @('build', (Join-Path $repository 'CodexDashboard.csproj'), '-c', $Configuration, '-m:1', '-nr:false', '--nologo')
    Invoke-Check -Name 'assistant-build' -Executable 'dotnet' -Arguments @('build', (Join-Path $repository 'src/Assistant.Windows/Assistant.Windows.csproj'), '-c', $Configuration, '-m:1', '-nr:false', '--nologo')
    [ordered]@{ syntheticOnly = $true; completed = $true; passed = $true; startedAt = $started; completedAt = [DateTimeOffset]::UtcNow; checks = @($results.ToArray()) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
    Write-Host "All isolated project checks passed. Evidence: $output"
}
finally {
    Pop-Location
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
}
