using System.Security.Cryptography;
using Codex.Control;

// Synthetic fixtures only. This fixed public test key must never protect production data.
internal sealed class TestProtector : IOwnedProvenanceProtector
{
    private static readonly byte[] Key = SHA256.HashData("PUBLIC SYNTHETIC TEST KEY ONLY"u8);
    public string Scheme => "synthetic-tests-aes256gcm-v1";
    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> purpose)
    {
        byte[] result = new byte[28 + plaintext.Length];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(result.AsSpan(0, 12), plaintext, result.AsSpan(28), result.AsSpan(12, 16), purpose);
        return result;
    }
    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> purpose)
    {
        if (ciphertext.Length < 28) throw new CryptographicException();
        byte[] plaintext = new byte[ciphertext.Length - 28];
        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(ciphertext[..12], ciphertext[28..], ciphertext.Slice(12, 16), plaintext, purpose);
        return plaintext;
    }
}

internal sealed class MemoryProvenance : IOwnedProvenanceStorage
{
    internal List<ProtectedOwnedProvenance> Records { get; } = [];
    internal Func<CancellationToken, Task>? BeforeAppend;
    internal Func<CancellationToken, Task>? AfterCommit;
    public bool IsReadOnly { get; init; }
    public bool RequiresRecovery { get; private set; }
    public ValueTask<IReadOnlyList<ProtectedOwnedProvenance>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<ProtectedOwnedProvenance>>(Records.ToArray());
    }
    public async ValueTask AppendAsync(long expectedSequence, ReadOnlyMemory<byte> protectedRecord, CancellationToken cancellationToken = default)
    {
        try
        {
            if (IsReadOnly || RequiresRecovery || expectedSequence != Records.Count) throw new IOException();
            if (BeforeAppend != null) await BeforeAppend(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add(new(expectedSequence + 1, protectedRecord.ToArray()));
            if (AfterCommit != null) await AfterCommit(cancellationToken);
        }
        catch { RequiresRecovery = true; throw; }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class TestProvenance
{
    internal static OwnedProvenanceStore New(OwnedServerConfiguration config) => new(config, new MemoryProvenance(), new TestProtector());
}
