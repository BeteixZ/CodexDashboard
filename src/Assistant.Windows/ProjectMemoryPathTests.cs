using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexDashboard;

internal static class ProjectMemoryPathTests
{
    private const string Volume = @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\";
    private const string Parent = Volume + "SensitiveParent";
    private const string Leaf = Parent + @"\A";
    private sealed class Probe : IProjectMemoryDirectoryProbe
    {
        internal readonly Dictionary<string, ProjectMemoryDirectoryFacts?> Facts = new(StringComparer.Ordinal);
        internal readonly List<string> Opened = [];
        internal int Disposed;
        internal Action<string>? AfterOpen;
        internal Probe()
        {
            Facts[@"Q:\A"] = Fact(Leaf);
            foreach (string path in new[] { Volume, Parent, Leaf }) Facts[path] = Fact(path);
        }
        public IProjectMemoryDirectoryHandle? Open(string path)
        {
            Opened.Add(path);
            var opened = Facts.TryGetValue(path, out var fact) && fact != null ? new Handle(fact, () => Disposed++) : null;
            AfterOpen?.Invoke(path); return opened;
        }
        private sealed class Handle(ProjectMemoryDirectoryFacts facts, Action disposed) : IProjectMemoryDirectoryHandle
        { public ProjectMemoryDirectoryFacts Facts => facts; public void Dispose() => disposed(); }
    }
    private static ProjectMemoryDirectoryFacts Fact(string path) => new(path, "identity/" + path, 0, true, false);
    private static bool Verify(Probe probe) => new ProjectMemoryPathVerifier(probe)
        .IsCaseInsensitiveLocalDirectory(@"Q:\A", Leaf.ToUpperInvariant());
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("project_path_assertion"); }

    internal static async Task RunAsync(string directory, Func<string, Action, Task> check)
    {
        await check("project_path_checks_physical_ancestors_beyond_subst_root", () =>
        {
            var p = new Probe(); Require(Verify(p) && p.Opened.SequenceEqual(new[] { @"Q:\A", Volume, Parent, Leaf,
                @"Q:\A", Volume, Parent, Leaf, @"Q:\A" }) && p.Disposed == 9);
        });
        await check("project_path_sensitive_physical_parent_refuses_before_descending", () =>
        {
            var p = new Probe(); p.Facts[Parent] = Fact(Parent) with { CaseSensitiveFlags = 1 };
            Require(!Verify(p) && !p.Opened.Contains(Leaf) && p.Disposed == 3);
        });
        await check("project_path_case_distinct_A_and_a_cannot_share_scope", () =>
        {
            foreach (string leaf in new[] { Leaf, Parent + @"\a" })
            {
                var p = new Probe(); p.Facts[@"Q:\A"] = Fact(leaf); p.Facts[leaf] = Fact(leaf);
                p.Facts[Parent] = Fact(Parent) with { CaseSensitiveFlags = 1 };
                Require(!Verify(p));
            }
        });
        await check("project_path_sensitive_leaf_or_root_and_unknown_flags_fail_closed", () =>
        {
            foreach (string path in new[] { @"Q:\A", Volume, Parent })
            {
                var p = new Probe(); p.Facts[path] = p.Facts[path]! with { CaseSensitiveFlags = 0x80000000 };
                Require(!Verify(p));
            }
        });
        await check("project_path_query_unavailable_never_defaults_to_case_insensitive", () =>
        {
            foreach (string path in new[] { @"Q:\A", Volume, Parent, Leaf })
            { var p = new Probe(); p.Facts[path] = null; Require(!Verify(p) && p.Disposed == p.Opened.Count - 1); }
        });
        await check("project_path_reparse_and_non_directory_ancestors_refuse", () =>
        {
            var p = new Probe(); p.Facts[Parent] = Fact(Parent) with { IsReparsePoint = true }; Require(!Verify(p));
            p = new Probe(); p.Facts[Parent] = Fact(Parent) with { IsDirectory = false }; Require(!Verify(p));
        });
        await check("project_path_changed_physical_leaf_identity_refuses", () =>
        {
            var p = new Probe(); p.Facts[Leaf] = Fact(Leaf) with { FileIdentity = "replacement" }; Require(!Verify(p));
        });
        await check("project_path_alias_changed_during_ancestor_walk_refuses", () =>
        {
            var p = new Probe(); p.AfterOpen = path => { if (path == Leaf) p.Facts[@"Q:\A"] = Fact(Volume + "OtherProject"); };
            Require(!Verify(p));
        });
        await check("project_path_ancestor_flags_changed_after_first_query_refuses", () =>
        {
            var p = new Probe(); p.AfterOpen = path => { if (path == Leaf) p.Facts[Parent] = Fact(Parent) with { CaseSensitiveFlags = 1 }; };
            Require(!Verify(p));
        });
        await check("project_path_canonical_mismatch_and_non_volume_source_refuse", () =>
        {
            var p = new Probe(); Require(!new ProjectMemoryPathVerifier(p).IsCaseInsensitiveLocalDirectory(@"Q:\A", Leaf + "OTHER"));
            p = new Probe(); p.Facts[@"Q:\A"] = Fact(@"\\server\share\A"); Require(!Verify(p));
            Require(ProjectMemoryPathVerifier.PhysicalAncestors(Parent + @"\..\A") == null
                && ProjectMemoryPathVerifier.PhysicalAncestors(Parent + @"\\A") == null);
        });
        await check("project_path_native_normal_directory_preserves_existing_canonical_scope", () =>
        {
            string path = Path.Combine(directory, "native-ordinary", "MixedCase"); Directory.CreateDirectory(path);
            using var handle = new NativeProjectMemoryDirectoryProbe().Open(path);
            Require(handle != null && handle.Facts.CaseSensitiveFlags == 0);
            string canonical = handle!.Facts.PhysicalPath.TrimEnd('\\').ToUpperInvariant();
            Require(new ProjectMemoryPathVerifier().IsCaseInsensitiveLocalDirectory(path, canonical));
        });
        // The privileged capability is attempted on a newly created empty directory only.
        // Unavailable capability is explicit evidence, not a passing native A/a claim.
        string sensitive = Path.Combine(directory, "native-sensitive-capability"); Directory.CreateDirectory(sensitive);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "fsutil.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("file"); start.ArgumentList.Add("setCaseSensitiveInfo"); start.ArgumentList.Add(sensitive); start.ArgumentList.Add("enable");
        int? exitCode = null; string stdout = "", stderr = "";
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("capability_probe_not_started");
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(); stderr = "probe_timeout;"; }
            exitCode = process.ExitCode; stdout = await output; stderr += await error;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { stderr = error.GetType().Name; }
        bool enabled; uint? nativeFlags;
        using (var opened = new NativeProjectMemoryDirectoryProbe().Open(sensitive))
        { nativeFlags = opened?.Facts.CaseSensitiveFlags; enabled = nativeFlags == 1; }
        await File.WriteAllTextAsync(Path.Combine(directory, "project-path-platform-capability.json"), JsonSerializer.Serialize(new
        { attemptedOnlyNewIsolatedDirectory = true, enabled, nativeFlags, exitCode, stdout, stderr,
            limitation = enabled ? null : "Native case-sensitive A/a validation remains unavailable; synthetic physical-ancestor tests still required." }, new JsonSerializerOptions { WriteIndented = true }));
        if (enabled)
        {
            await check("project_path_native_case_sensitive_leaf_is_rejected", () =>
            {
                using var handle = new NativeProjectMemoryDirectoryProbe().Open(sensitive);
                Require(handle != null && !new ProjectMemoryPathVerifier().IsCaseInsensitiveLocalDirectory(sensitive,
                    handle.Facts.PhysicalPath.TrimEnd('\\').ToUpperInvariant()));
            });
            await check("project_path_native_case_distinct_children_are_both_rejected", () =>
            {
                string first = Path.Combine(sensitive, "A"), second = Path.Combine(sensitive, "a");
                Directory.CreateDirectory(first); Directory.CreateDirectory(second);
                using var a = new NativeProjectMemoryDirectoryProbe().Open(first);
                using var b = new NativeProjectMemoryDirectoryProbe().Open(second);
                Require(a != null && b != null && a.Facts.FileIdentity != b.Facts.FileIdentity);
                var verifier = new ProjectMemoryPathVerifier();
                Require(!verifier.IsCaseInsensitiveLocalDirectory(first, a!.Facts.PhysicalPath.TrimEnd('\\').ToUpperInvariant())
                    && !verifier.IsCaseInsensitiveLocalDirectory(second, b!.Facts.PhysicalPath.TrimEnd('\\').ToUpperInvariant()));
            });
        }
    }
}
