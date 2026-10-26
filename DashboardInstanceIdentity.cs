using System;
using System.IO;

namespace CodexDashboard;

// Retain legacy signals for the default deployment. Explicit profiles must never
// activate or close a different installation which happens to be in the same session.
public static class DashboardInstanceIdentity
{
    public static string Prefix(string? dataDirectory)
    {
        if (dataDirectory == null) return "Local\\CodexDashboard";
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).ToUpperInvariant();
        return "Local\\CodexDashboard." + TextSafety.Hash(normalized)[..24];
    }
}
