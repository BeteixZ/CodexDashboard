using System.Security.Cryptography;
using System.Text;

namespace CodexDashboard;

public static class TextSafety
{
    public static string Clean(string? text, int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        string value = string.Concat((text ?? "").Where(c => !char.IsControl(c) || c is '\n' or '\t')).Trim();
        if (value.Length <= max) return value;
        int length = max > 0 && char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..length] + "…";
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
