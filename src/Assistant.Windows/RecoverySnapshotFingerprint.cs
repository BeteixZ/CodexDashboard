using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexDashboard;

internal sealed record RecoverySnapshotFingerprint(string Sha256, int FileCount, long Bytes)
{
    // Caller holds the data-directory exclusion lease. Hash protected bytes as-is;
    // do not deserialize settings or decrypt credentials to identify a snapshot.
    internal static RecoverySnapshotFingerprint Capture(string directory)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count = 0; long bytes = 0;
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (relative == ".codex-migration.lock") continue;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = file.Length;
            string sha256 = Convert.ToHexString(SHA256.HashData(file));
            digest.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { path = relative, length, sha256 }) + "\n"));
            count++; bytes += length;
        }
        return new(Convert.ToHexString(digest.GetHashAndReset()), count, bytes);
    }
}
