param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'artifacts\assistant\CodexAssistant.exe'),
    [string]$WorkingDirectory = $PSScriptRoot,
    [string]$DataDirectory,
    [string]$ProtocolProfile = 'default'
)

function ConvertTo-ProductWindowsArgument([string]$Value) {
    if ($null -eq $Value -or $Value.IndexOf([char]0) -ge 0) { throw 'Invalid command-line argument.' }
    $quoted = [Text.StringBuilder]::new()
    [void]$quoted.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$quoted.Append(('\' * (2 * $slashes + 1)))
        } else { [void]$quoted.Append(('\' * $slashes)) }
        [void]$quoted.Append($character)
        $slashes = 0
    }
    [void]$quoted.Append(('\' * (2 * $slashes)))
    [void]$quoted.Append('"')
    return $quoted.ToString()
}

function ConvertTo-ProductDataDirectory([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -notmatch '^(?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/]+[\\/][^\\/]+)' -or
        $Value.IndexOfAny([IO.Path]::GetInvalidPathChars()) -ge 0 -or $Value -match '[\x00-\x1f]') {
        throw 'DataDirectory must be a valid absolute path.'
    }
    $result = [IO.Path]::GetFullPath($Value)
    if ($result.Length -gt [IO.Path]::GetPathRoot($result).Length) { $result = $result.TrimEnd('\', '/') }
    if ([IO.File]::Exists($result)) { throw 'DataDirectory names a file.' }
    for ($current = [IO.DirectoryInfo]::new($result); $null -ne $current; $current = $current.Parent) {
        if ($current.Exists -and ($current.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Redirected DataDirectory cannot be confirmed by this launcher.'
        }
    }
    return $result
}

function Resolve-ProductLaunch([string]$Executable, [string]$RequestedDataDirectory, [string]$RequestedProfile) {
    if ([string]::IsNullOrWhiteSpace($RequestedProfile) -or $RequestedProfile.Length -gt 256 -or
        $RequestedProfile -match '[\x00-\x1f]') { throw 'Invalid ProtocolProfile.' }
    $source = 'default'
    $resolved = $null
    if (-not [string]::IsNullOrEmpty($RequestedDataDirectory)) {
        $resolved = ConvertTo-ProductDataDirectory $RequestedDataDirectory
        $source = 'explicit'
    } else {
        $selector = Join-Path ([IO.Path]::GetDirectoryName($Executable)) 'data-directory.txt'
        if ([IO.File]::Exists($selector)) {
            $info = Get-Item -LiteralPath $selector
            if ($info.Length -gt 8192 -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'The data-directory selector cannot be confirmed.'
            }
            $selected = [IO.File]::ReadAllText($selector, [Text.UTF8Encoding]::new($false, $true)).Trim()
            $resolved = ConvertTo-ProductDataDirectory $selected
            $source = 'selector'
        }
    }
    $arguments = @()
    if ($null -ne $resolved) { $arguments += @('--data-dir', $resolved) }
    $arguments += @('--protocol-profile', $RequestedProfile)
    [pscustomobject]@{
        Mode = $(if ($null -eq $resolved) { 'default' } else { 'explicit' })
        DataDirectory = $resolved
        DataDirectorySource = $source
        ProtocolProfile = $RequestedProfile
        Arguments = (($arguments | ForEach-Object { ConvertTo-ProductWindowsArgument $_ }) -join ' ')
    }
}

function Get-ProductCommandIdentity([string[]]$Arguments) {
    if ($null -eq $Arguments -or $Arguments.Count -eq 0) { throw 'Process command line is unavailable.' }
    $directory = $null
    $profile = $null
    for ($index = 1; $index -lt $Arguments.Count; $index++) {
        switch -CaseSensitive ($Arguments[$index]) {
            '--data-dir' {
                if ($null -ne $directory -or $index + 1 -ge $Arguments.Count) { throw 'Ambiguous process data directory.' }
                $rawDirectory = $Arguments[++$index]
                [void](ConvertTo-ProductDataDirectory $rawDirectory)
                # Match the host's GetFullPath exactly: an existing trailing
                # separator also participates in its singleton/profile hash.
                $directory = [IO.Path]::GetFullPath($rawDirectory)
            }
            '--protocol-profile' {
                if ($null -ne $profile -or $index + 1 -ge $Arguments.Count) { throw 'Ambiguous process protocol profile.' }
                $profile = $Arguments[++$index]
                if ([string]::IsNullOrWhiteSpace($profile) -or $profile.Length -gt 256 -or $profile -match '[\x00-\x1f]') {
                    throw 'Invalid process protocol profile.'
                }
            }
            '--background' { }
            '--wechat-settings' { }
            '--chatgpt-connect' { }
            default { throw 'The process is not a confirmable normal product launch.' }
        }
    }
    if ($null -eq $profile) {
        $profile = if ($null -eq $directory) { 'default' } else {
            $hasher = [Security.Cryptography.SHA256]::Create()
            try {
                [BitConverter]::ToString($hasher.ComputeHash(
                    [Text.Encoding]::UTF8.GetBytes($directory.ToUpperInvariant()))).Replace('-', '').Substring(0, 24)
            }
            finally { $hasher.Dispose() }
        }
    }
    [pscustomobject]@{
        Mode = $(if ($null -eq $directory) { 'default' } else { 'explicit' })
        DataDirectory = $directory
        ProtocolProfile = $profile
    }
}

function Test-ProductCommandIdentity($Expected, $Actual) {
    return $Expected.Mode -ceq $Actual.Mode -and
        $Expected.DataDirectory -ieq $Actual.DataDirectory -and
        $Expected.ProtocolProfile -ceq $Actual.ProtocolProfile
}

$ErrorActionPreference = 'Stop'
$assistantExecutable = (Get-Item -LiteralPath $ExecutablePath).FullName
$assistantWorkingDirectory = (Get-Item -LiteralPath $WorkingDirectory).FullName
if ([IO.Path]::GetFileName($assistantExecutable) -ne 'CodexAssistant.exe') { throw 'Expected CodexAssistant.exe.' }
$assistantLaunch = Resolve-ProductLaunch $assistantExecutable $DataDirectory $ProtocolProfile

if (-not ('CodexAssistantProcessMembership' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class CodexAssistantProcessMembership
{
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static string[] ParseArguments(string commandLine)
    {
        if (String.IsNullOrWhiteSpace(commandLine)) throw new InvalidOperationException("Process command line unavailable.");
        int count;
        var argv = CommandLineToArgvW(commandLine, out count);
        if (argv == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new string[count];
            for (int index = 0; index < count; index++)
                result[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size));
            return result;
        }
        finally { LocalFree(argv); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr buffer, uint length, out uint returned);
    public static bool InJob(int processId)
    {
        var process = OpenProcess(0x1000, false, processId);
        if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            bool result;
            if (!IsProcessInJob(process, IntPtr.Zero, out result)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return result;
        }
        finally { CloseHandle(process); }
    }
    public static bool InCurrentJob(int processId)
    {
        if (!InJob(System.Diagnostics.Process.GetCurrentProcess().Id)) return false;
        for (int size = 4096; size <= 524288; size *= 2)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint returned;
                if (QueryInformationJobObject(IntPtr.Zero, 3, buffer, (uint)size, out returned))
                {
                    int count = Marshal.ReadInt32(buffer, 4);
                    if (count < 0 || 8L + (long)count * IntPtr.Size > size) throw new InvalidOperationException("Invalid job process list.");
                    for (int index = 0; index < count; index++)
                        if (Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size).ToInt64() == processId) return true;
                    return false;
                }
                int error = Marshal.GetLastWin32Error();
                if (error != 234) throw new Win32Exception(error);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("Job process list too large.");
    }
}
'@
}

$assistantSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
$assistantCandidates = @(Get-CimInstance Win32_Process -Filter "Name='CodexAssistant.exe'" |
    Where-Object { $_.ExecutablePath -eq $assistantExecutable -and $_.SessionId -eq $assistantSession })
$assistantExisting = @($assistantCandidates | Where-Object {
    $candidateIdentity = Get-ProductCommandIdentity ([CodexAssistantProcessMembership]::ParseArguments($_.CommandLine))
    if (Test-ProductCommandIdentity $assistantLaunch $candidateIdentity) { return $true }
    if ($candidateIdentity.Mode -ne $assistantLaunch.Mode -or
        $candidateIdentity.DataDirectory -ieq $assistantLaunch.DataDirectory -or
        $candidateIdentity.ProtocolProfile -ceq $assistantLaunch.ProtocolProfile) {
        throw '已有进程与请求的数据目录或协议配置不能确认相容；未将另一配置当作启动成功，请先核对或退出该进程。'
    }
    return $false
})
if ($assistantExisting.Count -gt 1) { throw 'Unexpected multiple Assistant instances.' }
if ($assistantExisting.Count -eq 1) {
    $assistantExistingParent = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $assistantExisting[0].ParentProcessId)
    if ([CodexAssistantProcessMembership]::InCurrentJob($assistantExisting[0].ProcessId) -or $assistantExistingParent.Name -ne 'explorer.exe') {
        throw '已有 Assistant 的独立启动状态未确认；请先从托盘退出，再运行此脚本。'
    }
}

# Invoke the existing desktop's Shell interface in Explorer. A Shell object
# created in this tool process alone does not ensure independent launch.
$assistantShell = New-Object -ComObject Shell.Application
$assistantDesktopHandle = 0
$assistantDesktop = $assistantShell.Windows().FindWindowSW(0, 0, 8, [ref]$assistantDesktopHandle, 1)
if ($null -eq $assistantDesktop -or $null -eq $assistantDesktop.Document.Application) {
    throw '未找到 Windows 桌面，未改用依赖终端的方式启动；请从资源管理器双击程序。'
}
$assistantDesktop.Document.Application.ShellExecute($assistantExecutable, $assistantLaunch.Arguments, $assistantWorkingDirectory, 'open', 0)

$assistantDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
do {
    $assistantRunning = @(Get-CimInstance Win32_Process -Filter "Name='CodexAssistant.exe'" |
        Where-Object { $_.ExecutablePath -eq $assistantExecutable -and $_.SessionId -eq $assistantSession } |
        Where-Object {
            $candidateIdentity = Get-ProductCommandIdentity ([CodexAssistantProcessMembership]::ParseArguments($_.CommandLine))
            Test-ProductCommandIdentity $assistantLaunch $candidateIdentity
        })
    if ($assistantRunning.Count -eq 1) { break }
    Start-Sleep -Milliseconds 200
} while ([DateTimeOffset]::UtcNow -lt $assistantDeadline)
if ($assistantRunning.Count -ne 1) { throw '桌面启动后未检测到唯一的 Assistant 进程。' }
$assistantParent = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $assistantRunning[0].ParentProcessId)
$assistantInToolJob = [CodexAssistantProcessMembership]::InCurrentJob($assistantRunning[0].ProcessId)
if ($assistantInToolJob -or $assistantParent.Name -ne 'explorer.exe') {
    throw '启动后仍在工具进程组中，或并非由 Windows 桌面启动，未确认独立运行。'
}
[pscustomobject]@{
    ProcessId = $assistantRunning[0].ProcessId
    ParentProcessId = $assistantRunning[0].ParentProcessId
    ParentProcessName = $assistantParent.Name
    InToolJob = $assistantInToolJob
    Independent = $true
    Method = 'WindowsDesktop'
    AlreadyRunning = ($assistantExisting.Count -eq 1)
    DataDirectory = $assistantLaunch.DataDirectory
    DataDirectorySource = $assistantLaunch.DataDirectorySource
    DefaultDataDirectoryUnresolved = ($assistantLaunch.Mode -eq 'default')
    ProtocolProfile = $assistantLaunch.ProtocolProfile
}
