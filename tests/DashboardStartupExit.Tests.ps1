#requires -Version 7.0
# Use a synthetic settings fixture. Only a new owned payload copy is instrumented.
param(
    [Parameter(Mandatory=$true)][string]$PayloadDir,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [Parameter(Mandatory=$true)][string]$SettingsFixture,
    [string]$OverlayDir=''
)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputRoot)
$sourcePayload=[IO.Path]::GetFullPath($PayloadDir).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
if(!(Test-Path -LiteralPath $sourcePayload -PathType Container)) { throw 'PayloadDir must be an existing package directory.' }
if($output.Equals($sourcePayload,[StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($sourcePayload+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'OutputRoot must be outside the source payload.' }
if(Test-Path -LiteralPath $output) { throw 'Preserve prior probe evidence; use a new output directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
$payload=Join-Path $output 'payload'
Copy-Item -LiteralPath $PayloadDir -Destination $payload -Recurse
if($OverlayDir) { Get-ChildItem -LiteralPath $OverlayDir | Copy-Item -Destination $payload -Recurse -Force }
$data=Join-Path $output 'data'
New-Item -ItemType Directory -Path $data | Out-Null
$settings=Get-Content -LiteralPath $SettingsFixture -Raw | ConvertFrom-Json
$settings.pythonPath=Join-Path $payload 'python/python.exe'
$settings.localEnabled=$true
$settings.remoteHosts=@()
$settings.webEnabled=$false
$settings.notifications=$false
$settings | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $data 'dashboard.settings.json') -Encoding utf8
$marker=Join-Path $output 'first-refresh-inflight.marker'
$markerLiteral=ConvertTo-Json $marker -Compress
$collector="import pathlib, time`npathlib.Path($markerLiteral).write_text('first-refresh-inflight', encoding='utf-8')`nwhile True: time.sleep(1)`n"
[IO.File]::WriteAllText((Join-Path $payload 'collector.py'),$collector,[Text.UTF8Encoding]::new($false))
$exe=Join-Path $payload 'CodexDashboard.exe'
$profile='refresh-exit-'+[guid]::NewGuid().ToString('N')
function Start-Owned([string[]]$Arguments) {
    $info=[Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute=$false
    $info.CreateNoWindow=$true
    $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory=$output
    foreach($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach($name in @('TEMP','TMP','DOTNET_CLI_HOME','APPDATA','LOCALAPPDATA')) {
        $path=Join-Path $output "environment/$name"
        [IO.Directory]::CreateDirectory($path) | Out-Null
        $info.Environment[$name]=$path
    }
    $info.Environment['CODEX_HOME']=$settings.codexHome
    $info.Environment['PYTHONUTF8']='1'
    $info.Environment['PYTHONDONTWRITEBYTECODE']='1'
    return [Diagnostics.Process]::Start($info)
}
$process=Start-Owned @('--background','--data-dir',$data,'--protocol-profile',$profile)
$started=$process.StartTime.ToUniversalTime()
$identity=[ordered]@{pid=$process.Id; executable=$exe; startedAtUtc=$started; profile=$profile; data=$data}
$identity | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'process.json') -Encoding utf8
$ready=[Diagnostics.Stopwatch]::StartNew()
$forced=$false
$requestCode=$null
$failure=$null
try {
    while(!(Test-Path -LiteralPath $marker) -and !$process.HasExited -and $ready.Elapsed.TotalSeconds -lt 45) { Start-Sleep -Milliseconds 50 }
    if(!(Test-Path -LiteralPath $marker)) { throw 'The controlled first refresh did not enter the blocker.' }
    $markedAt=[datetimeoffset]::UtcNow
    $request=Start-Owned @('--exit','--data-dir',$data)
    if(!$request.WaitForExit(20000)) { $request.Kill($true); throw 'The exact-data exit request did not finish.' }
    $requestCode=$request.ExitCode
    if(!$process.WaitForExit(20000)) { throw 'The controlled first-refresh App did not exit within 20 seconds.' }
}
catch { $failure=$_.Exception.ToString() }
finally {
    if(!$process.HasExited) {
        $process.Refresh()
        if($process.StartTime.ToUniversalTime() -ne $started -or $process.MainModule.FileName -ine $exe) { throw 'Owned process identity changed; refusing cleanup.' }
        $process.Kill($true)
        $process.WaitForExit()
        $forced=$true
    }
    $errorPath=Join-Path $data 'startup-error.txt'
    $errorText=if(Test-Path -LiteralPath $errorPath) { [IO.File]::ReadAllText($errorPath) } else { $null }
    $result=[ordered]@{
        scope='Instrumented copy of normal Dashboard entry: controlled synthetic collector marker proves initial refresh in flight, then exact-data exit. Frozen payload and real user sessions untouched.'
        passed=(!$failure -and !$forced -and $requestCode -eq 0 -and $process.ExitCode -eq 0 -and !$errorText)
        firstRefreshMarkerObserved=(Test-Path -LiteralPath $marker)
        markerObservedAtUtc=$markedAt
        appExitCode=$process.ExitCode
        exitRequestCode=$requestCode
        forcedCleanup=$forced
        failure=$failure
        startupError=$errorText
        process=$identity
        executableSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        assemblySha256=(Get-FileHash -LiteralPath (Join-Path $payload 'CodexDashboard.dll') -Algorithm SHA256).Hash
        completedAtUtc=[datetimeoffset]::UtcNow
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'probe.json') -Encoding utf8
    [pscustomobject]@{passed=$result.passed; appExitCode=$result.appExitCode; exitRequestCode=$requestCode; marker=$result.firstRefreshMarkerObserved; forcedCleanup=$forced; startupError=$errorText; result=(Join-Path $output 'probe.json')} | ConvertTo-Json -Compress
}
if(!$result.passed) { exit 1 }
