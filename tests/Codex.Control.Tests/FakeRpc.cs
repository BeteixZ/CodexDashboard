using System.Text.Json;
using Codex.Control;

internal sealed class FakeRpc(OwnedServerConfiguration configuration) : IJsonRpcConnection
{
    public ConnectionIdentity Identity => configuration.Connection;
    public bool IsConnected { get; private set; } = true;
    public event Action<RpcIncoming>? Incoming;
    public event Action? Disconnected;
    internal List<(string Method, JsonElement Parameters)> Calls { get; } = [];
    internal List<(JsonElement Id, JsonElement Result)> Responses { get; } = [];
    internal Dictionary<string, JsonElement> Threads { get; } = new();
    internal Func<string, JsonElement, CancellationToken, Task<JsonElement>>? Override;
    internal string Version = configuration.ExpectedVersion;
    internal string? UserAgent;
    internal string Home = configuration.ExpectedCodexHome;
    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToElement(parameters);
        Calls.Add((method, json));
        if (Override != null) return await Override(method, json, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Reply(method, json);
    }
    internal JsonElement Reply(string method, JsonElement parameters)
    {
        if (method == "initialize") return J(new { userAgent = UserAgent ?? "codex_cli_rs/" + Version,
            platformOs = configuration.ExpectedPlatformOs, platformFamily = configuration.ExpectedPlatformOs == "windows" ? "windows" : "unix", codexHome = Home });
        if (method == "thread/start")
        {
            string id = Guid.NewGuid().ToString();
            string cwd = parameters.GetProperty("cwd").GetString()!;
            Threads[id] = Thread(id, cwd);
            return J(new
            {
                thread = Threads[id], cwd, model = parameters.GetProperty("model").GetString(),
                approvalPolicy = parameters.GetProperty("approvalPolicy").GetString(), approvalsReviewer = "user",
                sandbox = new { type = parameters.GetProperty("sandbox").GetString() == "read-only" ? "readOnly" : "workspaceWrite", networkAccess = false }
            });
        }
        if (method == "turn/start") return J(new { turn = Turn(Guid.NewGuid().ToString(), "inProgress") });
        if (method == "thread/resume") return J(new { thread = Threads[parameters.GetProperty("threadId").GetString()!],
            cwd = parameters.GetProperty("cwd").GetString(), model = parameters.GetProperty("model").GetString(),
            approvalPolicy = parameters.GetProperty("approvalPolicy").GetString(), approvalsReviewer = "user",
            sandbox = new { type = parameters.GetProperty("sandbox").GetString() == "read-only" ? "readOnly" : "workspaceWrite", networkAccess = false } });
        if (method == "turn/steer") return J(new { turnId = parameters.GetProperty("expectedTurnId").GetString() });
        if (method is "thread/read" or "thread/unarchive") return J(new { thread = Threads[parameters.GetProperty("threadId").GetString()!] });
        return J(new { });
    }
    internal static JsonElement Thread(string id, string cwd, params JsonElement[] turns) => J(new { id, cwd, turns });
    internal static JsonElement Turn(string id, string status, params JsonElement[] items) => J(new { id, status, items });
    internal static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
    internal void Emit(string method, object parameters, object? requestId = null)
        => Incoming?.Invoke(new(method, J(parameters), requestId == null ? null : J(requestId)));
    internal Action<RpcIncoming>? CaptureIncoming() => Incoming;
    internal void Disconnect() { IsConnected = false; Disconnected?.Invoke(); }
    public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken)
    { Calls.Add((method, J(parameters))); return Task.CompletedTask; }
    public Task RespondAsync(JsonElement requestId, object result, CancellationToken cancellationToken)
    { Responses.Add((requestId.Clone(), J(result))); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}
