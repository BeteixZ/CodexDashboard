namespace CodexDashboard;

// The linked Dashboard VM depends only on these nonsecret configuration fields.
// The test host never reads application settings or the real Codex home.
public sealed class Settings : ICollectorSettings
{
    public string PythonPath { get; set; } = "TEST_COLLECTOR_MUST_NOT_START";
    public string CodexHome { get; set; } = Path.Combine(Path.GetTempPath(), "readonly-contract-" + Guid.NewGuid().ToString("N"));
    public List<string> RemoteHosts { get; set; } = new();
    public bool LocalEnabled { get; set; }
    public int RefreshSeconds { get; set; } = 10;
    public int HudRows { get; set; } = 4;
}
