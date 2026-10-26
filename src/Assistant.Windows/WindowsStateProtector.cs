using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Assistant.Core;

namespace CodexDashboard;

// Sensitive ledger state is protected for the current Windows user. The core's
// purpose is additional DPAPI entropy, so ciphertext cannot be moved to a
// different storage-purpose envelope and interpreted as that record.
internal sealed class WindowsStateProtector : IStateProtector
{
    public string Scheme => "windows-dpapi-current-user/v1";
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> purpose)
        => Transform(plaintext, purpose, true);
    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> purpose)
        => Transform(ciphertext, purpose, false);
    private static byte[] Transform(ReadOnlySpan<byte> value, ReadOnlySpan<byte> purpose, bool protect)
    {
        if (value.IsEmpty || value.Length > 16 * 1024 * 1024 || purpose.IsEmpty || purpose.Length > 4096)
            throw new CryptographicException("受保护状态的大小或用途无效。");
        Blob input = default, entropy = default, output = default;
        try
        {
            input = Allocate(value);
            entropy = Allocate(purpose);
            bool success = protect
                ? CryptProtectData(ref input, "CodexAssistant state", ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success || output.Length <= 0 || output.Length > 16 * 1024 * 1024)
                throw new CryptographicException("无法保护或读取助手状态；原记录保持不变。");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Clear(input, false); Clear(entropy, false); Clear(output, true);
        }
    }
    private static Blob Allocate(ReadOnlySpan<byte> value)
    {
        var bytes = value.ToArray();
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); return new() { Length = bytes.Length, Data = pointer }; }
        catch { Marshal.FreeHGlobal(pointer); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static void Clear(Blob value, bool native)
    {
        if (value.Data == IntPtr.Zero) return;
        for (int i = 0; i < value.Length; i++) Marshal.WriteByte(value.Data, i, 0);
        if (native) LocalFree(value.Data); else Marshal.FreeHGlobal(value.Data);
    }
}
