#requires -Version 7.0
param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$exe = [IO.Path]::GetFullPath($Executable)
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Use a fresh fixture.' }
$checks = [ordered]@{}
function EventName([string]$directory) {
    $canonical = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($directory)).ToUpperInvariant()
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
    'Local\CodexAssistant.' + $hash.Substring(0,24) + '.Exit'
}
function RequestExit([string]$directory) {
    $request = Start-Process -FilePath $exe -ArgumentList @('--request-exit','--data-dir', ('"' + $directory + '"')) -WindowStyle Hidden -PassThru
    if (!$request.WaitForExit(10000)) { throw 'Exit request timed out.' }
    $request.ExitCode
}
function WaitReady([string]$directory, $process) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        if ($process.HasExited) { throw "Offline host failed: $($process.ExitCode)" }
        try { $handle = [Threading.EventWaitHandle]::OpenExisting((EventName $directory)); $handle.Dispose(); return }
        catch [Threading.WaitHandleCannotBeOpenedException] { Start-Sleep -Milliseconds 100 }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Offline host did not become ready.'
}
$instances = @()
try {
    foreach ($name in @('a','b')) {
        $directory = Join-Path $root $name
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        @{ version=1; localEnabled=$false; remoteHosts=@(); refreshSeconds=30;
            chatGpt=@{enabled=$false;taskControlEnabled=$false};
            weChat=@{clawBotEnabled=$false;pushPlusEnabled=$false} } | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $directory 'dashboard.settings.json')
        $process = Start-Process -FilePath $exe -ArgumentList @('--data-dir',('"' + $directory + '"'),'--background') -WindowStyle Hidden -PassThru
        $instances += @{Directory=$directory;Process=$process}
        WaitReady $directory $process
    }
    $missing = Join-Path $root 'absent'
    $checks.absentReturnsThreeWithoutCreatingData = (RequestExit $missing) -eq 3 -and !(Test-Path -LiteralPath $missing)
    $invalid = Start-Process -FilePath $exe -ArgumentList @('--request-exit') -WindowStyle Hidden -PassThru
    if (!$invalid.WaitForExit(10000)) { throw 'Invalid request did not exit.' }
    $checks.requiresExplicitDirectory = $invalid.ExitCode -eq 2
    $first = $instances[0]; $second = $instances[1]
    $checks.signalAccepted = (RequestExit ($first.Directory + '/')) -eq 0
    $checks.targetExitedCleanly = $first.Process.WaitForExit(15000) -and $first.Process.ExitCode -eq 0
    $second.Process.Refresh()
    $checks.otherDirectoryStillRunning = !$second.Process.HasExited
    $checks.closedTargetReturnsThree = (RequestExit $first.Directory) -eq 3
    $checks.secondExitedCleanly = (RequestExit $second.Directory) -eq 0 -and $second.Process.WaitForExit(15000) -and $second.Process.ExitCode -eq 0
    $restarted = Start-Process -FilePath $exe -ArgumentList @('--data-dir',('"' + $first.Directory + '"'),'--background') -WindowStyle Hidden -PassThru
    $instances += @{Directory=$first.Directory;Process=$restarted}
    WaitReady $first.Directory $restarted
    $checks.releasesLeaseAndCanReopen = (RequestExit $first.Directory) -eq 0 -and $restarted.WaitForExit(15000) -and $restarted.ExitCode -eq 0
    $checks | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'checks.json')
    if ($checks.Values -contains $false) { throw 'Graceful exit regression failed.' }
    $checks | ConvertTo-Json
} finally {
    foreach ($instance in $instances) {
        if (!$instance.Process.HasExited) { $null = RequestExit $instance.Directory; $null = $instance.Process.WaitForExit(15000) }
        $instance.Process.Dispose()
    }
}
