#requires -Version 7.0
param([Parameter(Mandatory)][string]$BuildDirectory, [Parameter(Mandatory)][string]$OutputDirectory, [string]$FixtureDirectory)
$ErrorActionPreference='Stop'
if (![IO.Path]::IsPathFullyQualified($OutputDirectory) -or (Test-Path -LiteralPath $OutputDirectory)) { throw 'Use a fresh absolute output directory.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$installer = Join-Path $PSScriptRoot '../Install-Product.ps1'
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Passed) {
    $checks.Add(@{ name=$Name; passed=$Passed })
    $checks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'checks.json') -Encoding utf8
    if (!$Passed) { throw "Check failed: $Name" }
}
$assistant = Join-Path $OutputDirectory 'assistant-app'
$dashboard = Join-Path $OutputDirectory 'dashboard-app'
$dataA = Join-Path $OutputDirectory 'assistant-data'
$dataD = Join-Path $OutputDirectory 'dashboard-data'
[void][IO.Directory]::CreateDirectory($dataA)
$sentinel=Join-Path $dataA 'synthetic-data.txt'
'SYNTHETIC_DATA_RETAINED' | Set-Content -LiteralPath $sentinel
$before=(Get-FileHash -LiteralPath $sentinel).Hash
$a1 = & $installer -PackageDirectory (Join-Path $BuildDirectory 'assistant') -InstallationDirectory $assistant -DataDirectory $dataA -ProtocolProfile 'synthetic-install'
$d1 = & $installer -PackageDirectory (Join-Path $BuildDirectory 'dashboard') -InstallationDirectory $dashboard -DataDirectory $dataD -ProtocolProfile 'synthetic-install'
Check 'independent products installed without starting' (!$a1.Started -and !$d1.Started -and $a1.Product -eq 'assistant' -and $d1.Product -eq 'dashboard')
$shortcutTool=Join-Path $PSScriptRoot '../Add-ProductShortcut.ps1'
$shortcutFolder=Join-Path $OutputDirectory 'shortcuts'
$shortcut=& $shortcutTool -InstallationDirectory $assistant -ShortcutDirectory $shortcutFolder
$dashboardShortcut=& $shortcutTool -InstallationDirectory $dashboard -ShortcutDirectory $shortcutFolder
$shell=New-Object -ComObject WScript.Shell
$link=$shell.CreateShortcut($shortcut.Shortcut)
Check 'shortcut targets stable launcher without starting software' ($link.Arguments.Contains((Join-Path $assistant 'Launch-InstalledProduct.ps1')) -and !$shortcut.ApplicationStarted)
$shortcutHash=(Get-FileHash -LiteralPath $shortcut.Shortcut).Hash
$oldExe=Join-Path $assistant "releases/$($a1.ActiveRelease)/CodexAssistant.exe"
$oldHash=(Get-FileHash -LiteralPath $oldExe).Hash
$dashboardHash=(Get-FileHash -LiteralPath (Join-Path $dashboard 'installation.json')).Hash
$a2 = & $installer -PackageDirectory (Join-Path $BuildDirectory 'assistant') -InstallationDirectory $assistant -DataDirectory $dataA -ProtocolProfile 'synthetic-install'
Check 'upgrade retains previous release and data' ($a2.PreviousRelease -eq $a1.ActiveRelease -and $a2.ActiveRelease -ne $a1.ActiveRelease -and (Get-FileHash -LiteralPath $oldExe).Hash -eq $oldHash -and (Get-FileHash -LiteralPath $sentinel).Hash -eq $before)
Check 'shortcut remains valid across release selection' ((Get-FileHash -LiteralPath $shortcut.Shortcut).Hash -eq $shortcutHash)
Check 'assistant upgrade does not change dashboard' ((Get-FileHash -LiteralPath (Join-Path $dashboard 'installation.json')).Hash -eq $dashboardHash)
$preview = & (Join-Path $assistant 'Launch-InstalledProduct.ps1') -Preview
Check 'stable launcher resolves new release and same data' ($preview.ExecutablePath -eq (Join-Path $assistant "releases/$($a2.ActiveRelease)/CodexAssistant.exe") -and $preview.DataDirectory -eq $dataA -and $preview.ProtocolProfile -eq 'synthetic-install')
$pointerHash=(Get-FileHash -LiteralPath (Join-Path $assistant 'installation.json')).Hash
$rejected=$false
try { & $installer -PackageDirectory (Join-Path $BuildDirectory 'dashboard') -InstallationDirectory $assistant -DataDirectory $dataA -ProtocolProfile 'synthetic-install' | Out-Null } catch { $rejected=$true }
Check 'cannot replace a different product' ($rejected -and (Get-FileHash -LiteralPath (Join-Path $assistant 'installation.json')).Hash -eq $pointerHash)
$damaged=Join-Path $OutputDirectory 'damaged-package'
Copy-Item -LiteralPath (Join-Path $BuildDirectory 'assistant') -Destination $damaged -Recurse
Add-Content -LiteralPath (Join-Path $damaged 'collector.py') -Value '# synthetic damage'
$rejected=$false
try { & $installer -PackageDirectory $damaged -InstallationDirectory $assistant -DataDirectory $dataA -ProtocolProfile 'synthetic-install' | Out-Null } catch { $rejected=$true }
Check 'corrupt upgrade preserves current selection' ($rejected -and (Get-FileHash -LiteralPath (Join-Path $assistant 'installation.json')).Hash -eq $pointerHash)
$rejected=$false
try { & $installer -PackageDirectory (Join-Path $BuildDirectory 'assistant') -InstallationDirectory $assistant -DataDirectory (Join-Path $assistant 'data') | Out-Null } catch { $rejected=$true }
Check 'data cannot be installed inside program directory' $rejected
$changed=& $shortcutTool -InstallationDirectory $assistant -ShortcutDirectory (Join-Path $OutputDirectory 'modified-shortcut')
$changedLink=$shell.CreateShortcut($changed.Shortcut)
$changedLink.Arguments='-NoProfile -Command exit'
$changedLink.Save()
$changedHash=(Get-FileHash -LiteralPath $changed.Shortcut).Hash
$rejected=$false
try { & $shortcutTool -InstallationDirectory $assistant -ShortcutDirectory (Join-Path $OutputDirectory 'modified-shortcut') | Out-Null } catch { $rejected=$true }
Check 'shortcut creation does not overwrite another target' ($rejected -and (Get-FileHash -LiteralPath $changed.Shortcut).Hash -eq $changedHash)
$uninstaller = Join-Path $assistant 'Uninstall-Product.ps1'
$unknown = Join-Path $assistant 'user-note.txt'
'SYNTHETIC_UNLISTED_NOTE' | Set-Content -LiteralPath $unknown
$rejected=$false
try { & $uninstaller -InstallationDirectory $assistant | Out-Null } catch { $rejected=$true }
Check 'unlisted user files prevent uninstall before removal' ($rejected -and (Test-Path -LiteralPath $oldExe) -and (Test-Path -LiteralPath $unknown))
Remove-Item -LiteralPath $unknown
$preview = & $uninstaller -InstallationDirectory $assistant -Preview
Check 'uninstall preview preserves program and data' (!$preview.Removed -and (Test-Path -LiteralPath $oldExe) -and $preview.DataDirectoryPreserved -eq $dataA)
& $uninstaller -InstallationDirectory $assistant | Out-Null
Check 'uninstall removes own unchanged shortcut and preserves modified and other product links' (!(Test-Path -LiteralPath $shortcut.Shortcut) -and (Get-FileHash -LiteralPath $changed.Shortcut).Hash -eq $changedHash -and (Test-Path -LiteralPath $dashboardShortcut.Shortcut))
Check 'uninstall removes only selected product and preserves external data' (!(Test-Path -LiteralPath $assistant) -and (Get-FileHash -LiteralPath $sentinel).Hash -eq $before -and (Get-FileHash -LiteralPath (Join-Path $dashboard 'installation.json')).Hash -eq $dashboardHash)
if ($FixtureDirectory) {
    $package = Join-Path $OutputDirectory 'synthetic-process-package'
    [void][IO.Directory]::CreateDirectory($package)
    Get-ChildItem -LiteralPath $FixtureDirectory -File | Copy-Item -Destination $package
    foreach ($script in @('Launch-InstalledProduct.ps1','Uninstall-Product.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../$script") -Destination $package
    }
    'throw "Synthetic fixture has no normal launch mode."' | Set-Content -LiteralPath (Join-Path $package 'Start-Assistant.ps1')
    $entries = @(Get-ChildItem -LiteralPath $package -File | ForEach-Object { @{ file=$_.Name; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash } })
    @{ version=1; product='assistant'; files=$entries } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $package 'package-manifest.json')
    $liveRoot = Join-Path $OutputDirectory 'synthetic-process-installed'
    $live = & $installer -PackageDirectory $package -InstallationDirectory $liveRoot -DataDirectory (Join-Path $OutputDirectory 'synthetic-process-data')
    $uninstaller = Join-Path $liveRoot 'Uninstall-Product.ps1'
    $executable = Join-Path $liveRoot "releases/$($live.ActiveRelease)/CodexAssistant.exe"
    $ready = Join-Path $OutputDirectory 'fixture-ready.txt'
    $release = Join-Path $OutputDirectory 'fixture-exit.txt'
    $process = Start-Process -FilePath $executable -ArgumentList @(('"'+$ready+'"'), ('"'+$release+'"')) -WindowStyle Hidden -PassThru
    try {
        $deadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
        while (!(Test-Path -LiteralPath $ready) -and !$process.HasExited -and [DateTimeOffset]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
        Check 'synthetic installed process is running' ((Test-Path -LiteralPath $ready) -and !$process.HasExited)
        $fileCount=@(Get-ChildItem -LiteralPath $liveRoot -Recurse -File -Force).Count
        $rejected=$false
        try { & $uninstaller -InstallationDirectory $liveRoot | Out-Null } catch { $rejected=$true }
        Check 'running program prevents all uninstall deletions' ($rejected -and !$process.HasExited -and @(Get-ChildItem -LiteralPath $liveRoot -Recurse -File -Force).Count -eq $fileCount -and (Test-Path -LiteralPath $executable))
    } finally {
        'exit' | Set-Content -LiteralPath $release
        if (!$process.WaitForExit(10000)) { Stop-Process -Id $process.Id; throw 'Synthetic process did not exit.' }
        $process.Dispose()
    }
    & $uninstaller -InstallationDirectory $liveRoot | Out-Null
    Check 'exited synthetic program can be uninstalled' (!(Test-Path -LiteralPath $liveRoot))
}
Write-Output "$($checks.Count) isolated install/uninstall checks passed; only the explicit synthetic fixture was started."
