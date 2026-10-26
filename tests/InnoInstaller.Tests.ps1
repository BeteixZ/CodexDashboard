#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$InnoCompilerPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version='0.1.0'
)
$ErrorActionPreference='Stop'
if (![IO.Path]::IsPathFullyQualified($OutputDirectory) -or (Test-Path -LiteralPath $OutputDirectory)) { throw 'Use a fresh absolute test directory.' }
$root=[IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($root)
$checks=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[bool]$Passed) {
    $checks.Add(@{name=$Name;passed=$Passed})
    $checks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'checks.json') -Encoding utf8
    if (!$Passed) { throw "Inno installer check failed: $Name" }
}
function Run-Installer([string]$Executable,[string[]]$Arguments) {
    $process=Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    try {
        if (!$process.WaitForExit(60000)) { throw 'Isolated installer exceeded 60 seconds; inspect the saved log.' }
        return $process.ExitCode
    } finally {$process.Dispose()}
}
function Test-Registration([string]$AppId) {
    $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("Software\Microsoft\Windows\CurrentVersion\Uninstall\${AppId}_is1")
    try { return $null -ne $key } finally { if($key) {$key.Dispose()} }
}
foreach($product in @(@{Folder='dashboard';Name='CodexDashboard'},@{Folder='assistant';Name='CodexAssistant'})) {
    # Compile the exact installer source against the real payload, with unique
    # test-only registration and a workspace data directory. No installed or
    # production application, account, binding or start-menu entry is reused.
    $testId=[Guid]::NewGuid().ToString('N').Substring(0,12)
    $name="$($product.Name)-SetupTest-$testId"
    $appId="BeteixZ.$name"
    $program=Join-Path $root "$($product.Folder)/program"
    $data=Join-Path $root "$($product.Folder)/data"
    $compilerOutput=Join-Path $root "$($product.Folder)/compiler"
    [void][IO.Directory]::CreateDirectory($data)
    $sentinel=Join-Path $data 'synthetic-data.txt'
    'SYNTHETIC_INSTALLER_DATA' | Set-Content -LiteralPath $sentinel
    $sentinelHash=(Get-FileHash -LiteralPath $sentinel).Hash
    Check "$($product.Name) unique registration does not exist" (!(Test-Registration $appId))
    & $InnoCompilerPath /Qp "/DProductName=$name" "/DProductExe=$($product.Name).exe" "/DProductAppId=$appId" "/DProductVersion=$Version" "/DPackageDirectory=$([IO.Path]::GetFullPath((Join-Path $PackageDirectory $product.Folder)))" "/DReleaseDirectory=$compilerOutput" "/DProductDataDirectory=$data" (Join-Path $PSScriptRoot '../packaging/windows-products.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile isolated test installer.' }
    $setup=Join-Path $compilerOutput "$name-$Version-win-x64-Setup.exe"
    $installArgs=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('"/DIR='+$program+'"'))
    $shortcutPath=Join-Path ([Environment]::GetFolderPath('Programs')) "$name/$name.lnk"
    $noIconsProgram=Join-Path $root "$($product.Folder)/noicons-program"
    $noIconsArgs=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS',('"/DIR='+$noIconsProgram+'"'),('"/LOG='+(Join-Path $root "$($product.Folder)-noicons.log")+'"'))
    $exitCode=Run-Installer $setup $noIconsArgs
    Check "$($product.Name) NOICONS setup installs with isolated registration" ($exitCode -eq 0 -and (Test-Registration $appId))
    Check "$($product.Name) NOICONS suppresses the unique test start-menu shortcut" (!(Test-Path -LiteralPath $shortcutPath))
    $exitCode=Run-Installer (Join-Path $noIconsProgram 'uninstall/unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
    Check "$($product.Name) NOICONS trial uninstalls and retains configured data" ($exitCode -eq 0 -and !(Test-Registration $appId) -and (Get-FileHash -LiteralPath $sentinel).Hash -eq $sentinelHash)
    $exitCode=Run-Installer $setup ($installArgs+('"/LOG='+(Join-Path $root "$($product.Folder)-install.log")+'"'))
    Check "$($product.Name) native setup installs" ($exitCode -eq 0 -and (Test-Registration $appId))
    $firstRelease=@(Get-ChildItem -LiteralPath (Join-Path $program 'releases') -Directory)
    Check "$($product.Name) install has one fresh release" ($firstRelease.Count -eq 1)
    $oldExe=Join-Path $firstRelease[0].FullName "$($product.Name).exe"
    $oldHash=(Get-FileHash -LiteralPath $oldExe).Hash
    $upgradeHeld=[IO.FileStream]::new($oldExe,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $exitCode=Run-Installer $setup ($installArgs+('"/LOG='+(Join-Path $root "$($product.Folder)-upgrade.log")+'"'))
    } finally {$upgradeHeld.Dispose()}
    Check "$($product.Name) native setup upgrades without replacing old binary" ($exitCode -eq 0 -and @(Get-ChildItem -LiteralPath (Join-Path $program 'releases') -Directory).Count -eq 2 -and (Get-FileHash -LiteralPath $oldExe).Hash -eq $oldHash)
    $shell=New-Object -ComObject WScript.Shell
    $shortcut=$shell.CreateShortcut($shortcutPath)
    $newRelease=@(Get-ChildItem -LiteralPath (Join-Path $program 'releases') -Directory | Where-Object FullName -ne $firstRelease[0].FullName)
    Check "$($product.Name) test shortcut launches new EXE with explicit isolated data" ($shortcut.TargetPath -eq (Join-Path $newRelease[0].FullName "$($product.Name).exe") -and $shortcut.Arguments.Contains($data) -and $shortcut.Arguments.Contains('--data-dir'))
    Check "$($product.Name) upgrade preserves configured data" ((Get-FileHash -LiteralPath $sentinel).Hash -eq $sentinelHash)
    $uninstaller=Join-Path $program 'uninstall/unins000.exe'
    $fileCount=@(Get-ChildItem -LiteralPath $program -Recurse -File).Count
    $held=[IO.FileStream]::new($oldExe,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $exitCode=Run-Installer $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('"/LOG='+(Join-Path $root "$($product.Folder)-busy-uninstall.log")+'"'))
        Check "$($product.Name) occupied binary rejects uninstall before deletion" ($exitCode -ne 0 -and @(Get-ChildItem -LiteralPath $program -Recurse -File).Count -eq $fileCount)
    } finally {$held.Dispose()}
    $exitCode=Run-Installer $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('"/LOG='+(Join-Path $root "$($product.Folder)-uninstall.log")+'"'))
    Check "$($product.Name) native uninstall removes test registration and payload" ($exitCode -eq 0 -and !(Test-Registration $appId) -and !(Test-Path -LiteralPath $oldExe))
    Check "$($product.Name) native uninstall preserves configured data" ((Get-FileHash -LiteralPath $sentinel).Hash -eq $sentinelHash)
    Check "$($product.Name) native uninstall removes its unique test shortcut" (!(Test-Path -LiteralPath $shortcutPath))
}
Write-Output "$($checks.Count) isolated native Inno installation checks passed; no normal application or account was started."
