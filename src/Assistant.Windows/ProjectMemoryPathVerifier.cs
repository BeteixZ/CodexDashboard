using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexDashboard;

internal interface IProjectMemoryPathVerifier
{
    bool IsCaseInsensitiveLocalDirectory(string sourceDirectory, string expectedCanonicalDirectory);
}

internal sealed record ProjectMemoryDirectoryFacts(string PhysicalPath, string FileIdentity,
    uint CaseSensitiveFlags, bool IsDirectory, bool IsReparsePoint);
internal interface IProjectMemoryDirectoryHandle : IDisposable
{
    ProjectMemoryDirectoryFacts Facts { get; }
}
internal interface IProjectMemoryDirectoryProbe
{
    // A failed or unsupported native query is unavailable evidence, never Flags = 0.
    IProjectMemoryDirectoryHandle? Open(string path);
}

/// <summary>
/// The shared workspace lock deliberately folds case. Project knowledge must additionally
/// prove that this cannot conflate distinct directories. Walk the filesystem's VolumeGUID
/// name, not the caller's drive ancestors (a SUBST root can conceal a sensitive parent).
/// This is observation only: no directory flags, aliases or permissions are changed.
/// </summary>
internal sealed class ProjectMemoryPathVerifier : IProjectMemoryPathVerifier
{
    private readonly IProjectMemoryDirectoryProbe probe;
    internal ProjectMemoryPathVerifier(IProjectMemoryDirectoryProbe? probe = null)
        => this.probe = probe ?? new NativeProjectMemoryDirectoryProbe();

    public bool IsCaseInsensitiveLocalDirectory(string sourceDirectory, string expectedCanonicalDirectory)
    {
        var handles = new List<IProjectMemoryDirectoryHandle>();
        try
        {
            if (sourceDirectory.Length is < 3 or > 4096 || !char.IsAsciiLetter(sourceDirectory[0])
                || sourceDirectory[1] != ':' || sourceDirectory[2] is not ('\\' or '/')
                || sourceDirectory.Any(char.IsControl)) return false;
            var original = probe.Open(sourceDirectory);
            if (original == null) return false;
            handles.Add(original);
            var fact = original.Facts;
            if (!Safe(fact)) return false;
            var ancestors = PhysicalAncestors(fact.PhysicalPath);
            if (ancestors == null || fact.PhysicalPath.TrimEnd('\\').ToUpperInvariant() != expectedCanonicalDirectory)
                return false;
            // Root-to-leaf: reject a sensitive ancestor before opening any of its descendants.
            var observed = new List<ProjectMemoryDirectoryFacts>();
            foreach (string ancestor in ancestors)
            {
                var opened = probe.Open(ancestor);
                if (opened == null) return false;
                handles.Add(opened);
                if (!Safe(opened.Facts) || !string.Equals(opened.Facts.PhysicalPath.TrimEnd('\\'),
                    ancestor.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
                observed.Add(opened.Facts);
            }
            // Detect replacement or alias changes between the original and physical opens.
            if (!SameDirectory(observed[^1], fact) || !Reobserve(sourceDirectory, fact)) return false;
            for (int index = 0; index < ancestors.Length; index++)
                if (!Reobserve(ancestors[index], observed[index])) return false;
            // Facts on retained handles are snapshots, not locks against flag/alias changes.
            // Reopen both namespace and ancestors, then recheck the caller's alias once more.
            return Reobserve(sourceDirectory, fact);

            bool Reobserve(string path, ProjectMemoryDirectoryFacts previous)
            {
                var current = probe.Open(path);
                if (current == null) return false;
                handles.Add(current);
                return Safe(current.Facts) && SameDirectory(previous, current.Facts);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Security.SecurityException)
        { return false; }
        finally { foreach (var handle in handles) handle.Dispose(); }
    }

    private static bool Safe(ProjectMemoryDirectoryFacts facts)
        => facts.IsDirectory && !facts.IsReparsePoint && facts.CaseSensitiveFlags == 0
            && !string.IsNullOrWhiteSpace(facts.FileIdentity);
    private static bool SameDirectory(ProjectMemoryDirectoryFacts first, ProjectMemoryDirectoryFacts second)
        => first.FileIdentity == second.FileIdentity && string.Equals(first.PhysicalPath.TrimEnd('\\'),
            second.PhysicalPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    internal static string[]? PhysicalAncestors(string physical)
    {
        const string prefix = @"\\?\Volume{";
        if (physical.Length > 32767 || !physical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || physical.Any(char.IsControl) || physical.Contains('/')) return null;
        int close = physical.IndexOf(@"}\", StringComparison.Ordinal);
        if (close < prefix.Length || !Guid.TryParseExact(physical[prefix.Length..close], "D", out _)) return null;
        string root = physical[..(close + 2)];
        string rest = physical[(close + 2)..].TrimEnd('\\');
        var result = new List<string> { root };
        if (rest.Length == 0) return result.ToArray();
        string current = root.TrimEnd('\\');
        foreach (string part in rest.Split('\\'))
        {
            if (part.Length == 0 || part is "." or ".." || part.Contains(':') || part.EndsWith('.') || part.EndsWith(' ')) return null;
            current += "\\" + part; result.Add(current);
        }
        return result.ToArray();
    }
}

internal sealed class NativeProjectMemoryDirectoryProbe : IProjectMemoryDirectoryProbe
{
    public IProjectMemoryDirectoryHandle? Open(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)
                || !GetFileInformationByHandleEx(handle, 23 /* FileCaseSensitiveInfo */, out uint flags, sizeof(uint))) return null;
            var name = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(handle, name, (uint)name.Capacity, 1 /* VOLUME_NAME_GUID */);
            if (length == 0 || length >= name.Capacity) return null;
            var result = new Handle(handle, new(name.ToString(), $"{info.VolumeSerial:X8}/{info.IndexHigh:X8}{info.IndexLow:X8}",
                flags, (info.Attributes & (uint)FileAttributes.Directory) != 0,
                (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0));
            handle = null!; return result;
        }
        finally { handle?.Dispose(); }
    }
    private sealed class Handle(SafeFileHandle handle, ProjectMemoryDirectoryFacts facts) : IProjectMemoryDirectoryHandle
    {
        public ProjectMemoryDirectoryFacts Facts => facts;
        public void Dispose() => handle.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out uint information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
