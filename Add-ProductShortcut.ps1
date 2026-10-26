#requires -Version 7.0
param([Parameter(Mandatory)][string]$InstallationDirectory,
    [string]$ShortcutDirectory=[Environment]::GetFolderPath('Programs'))
$ErrorActionPreference='Stop'
foreach ($path in @($InstallationDirectory,$ShortcutDirectory)) {
    if (![IO.Path]::IsPathFullyQualified($path)) { throw 'Use absolute paths.' }
    for ($parent=[IO.DirectoryInfo]::new([IO.Path]::GetFullPath($path)); $null -ne $parent; $parent=$parent.Parent) {
        if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected shortcut paths are unsupported.' }
    }
}
$root=[IO.Path]::GetFullPath($InstallationDirectory)
$lease=[IO.FileStream]::new((Join-Path $root '.install.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    $state=Get-Content -LiteralPath (Join-Path $root 'installation.json') -Raw | ConvertFrom-Json
    if ($state.version -ne 1 -or $state.product -notin @('assistant','dashboard') -or $state.activeRelease -notmatch '^[0-9a-f]{32}$') { throw 'Invalid installation record.' }
    $name=if ($state.product -eq 'assistant') { 'CodexAssistant' } else { 'CodexDashboard' }
    $launcher=Join-Path $root 'Launch-InstalledProduct.ps1'
    $target=(Get-Command pwsh.exe -CommandType Application | Select-Object -First 1).Source
    $arguments='-NoProfile -WindowStyle Hidden -File "' + $launcher + '"'
    [void][IO.Directory]::CreateDirectory($ShortcutDirectory)
    $path=Join-Path ([IO.Path]::GetFullPath($ShortcutDirectory)) ($name+'.lnk')
    $shell=New-Object -ComObject WScript.Shell
    $link=$shell.CreateShortcut($path)
    if ((Test-Path -LiteralPath $path) -and ($link.TargetPath -ne $target -or $link.Arguments -ne $arguments)) {
        throw '同名快捷方式属于其他安装，未覆盖。请使用另一个快捷方式目录。'
    }
    $receiptPath=Join-Path $root 'shortcuts.json'
    $entries=@()
    if (Test-Path -LiteralPath $receiptPath) {
        $saved=Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        if ($saved.version -ne 1) { throw 'Unsupported shortcut record.' }
        $entries=@($saved.entries | Where-Object path -ne $path)
    }
    $link.TargetPath=$target; $link.Arguments=$arguments; $link.WorkingDirectory=$root
    $link.IconLocation=Join-Path $root "releases/$($state.activeRelease)/$name.exe"
    $link.Description=$name; $link.WindowStyle=7; $link.Save()
    $entries+=@{ path=$path; sha256=(Get-FileHash -LiteralPath $path).Hash }
    $temporary=Join-Path $root ('shortcut-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    @{version=1; entries=$entries} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporary -Encoding utf8
    [IO.File]::Move($temporary,$receiptPath,$true)
    [pscustomobject]@{ Shortcut=$path; Created=$true; ApplicationStarted=$false }
} finally { $lease.Dispose() }
