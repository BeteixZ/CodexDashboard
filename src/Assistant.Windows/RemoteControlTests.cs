using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

internal static class RemoteControlTests
{
    private sealed class Server : IRemoteAppServer
    {
        public bool Connected { get; set; } = true;
        public event Action<string, string, string>? Attention;
        public void NeedsAttention(string id) => Attention?.Invoke(id, "item/commandExecution/requestApproval", "request-1");
        public bool Disposed;
        public string Id = "", State = "idle", ResumedState = "idle", Cwd = "/project";
        public string FailMethod = "";
        public bool AfterWrite, WrongId, EmptyTurn;
        public readonly List<(string Method, JsonElement Params)> Calls = new();
        public Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct, Action? beforeSend = null)
        {
            Calls.Add((method, JsonSerializer.SerializeToElement(parameters)));
            ct.ThrowIfCancellationRequested();
            if (method == FailMethod && !AfterWrite) throw new IOException("before send");
            beforeSend?.Invoke();
            if (method == FailMethod) throw new IOException("after send");
            return Task.FromResult(method == "turn/start"
                ? JsonSerializer.SerializeToElement(new { turn = new { id = EmptyTurn ? "" : "confirmed-turn" } })
                : JsonSerializer.SerializeToElement(new { cwd = Cwd, thread = new { id = WrongId ? Guid.NewGuid().ToString() : Id,
                    status = new { type = method == "thread/read" ? State : ResumedState } } }));
        }
        public ValueTask DisposeAsync() { Disposed = true; Connected = false; return ValueTask.CompletedTask; }
    }
    internal static async Task RunAndSaveAsync(string directory)
    {
        Directory.CreateDirectory(directory); var checks = new List<object>();
        void Check(string name, bool ok) { checks.Add(new { name, ok }); if (!ok) throw new InvalidOperationException(name); }
        var settings = new Settings { RemoteHosts = new() { "host-a", "host-b" }, ChatGpt = new() { Enabled = true, TaskControlEnabled = true }, WeChat = new() { ClawBotEnabled = true } };
        var task = new TaskRecord { Id = Guid.NewGuid().ToString(), Host = "host-a", Cwd = "/project", Title = "remote test", Status = TaskStatusNames.Completed };
        var server = new Server { Id = task.Id }; int connections = 0;
        var transport = new RemoteTaskTransport(settings, (_, _) => { connections++; return Task.FromResult<IRemoteAppServer>(server); });
        try
        {
            const string original = "先解释结果\n不要改代码；`$HOME` $(whoami) \"原样\"";
            var result = await transport.DeliverAsync(task, original, "stable-user-id", default);
            Check("remote idle task gets confirmed original turn", result.State == "submitted" && result.TurnId == "confirmed-turn");
            var turn = server.Calls.Single(c => c.Method == "turn/start").Params;
            Check("original unicode multiline instruction stays unchanged", turn.GetProperty("input")[0].GetProperty("text").GetString() == original);
            Check("same thread id and stable user message identity", turn.GetProperty("threadId").GetString() == task.Id && turn.GetProperty("clientUserMessageId").GetString() == "stable-user-id");
            Check("resume only specifies existing id and pagination", server.Calls.Single(c => c.Method == "thread/resume").Params.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "excludeTurns", "threadId" }));
            Check("turn never changes model permissions or cwd", turn.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "clientUserMessageId", "input", "threadId" }));
            server.NeedsAttention(task.Id);
            Check("controlled remote approval produces attention without reply", transport.DrainAttention().Single().TaskKey == task.Key && server.Calls.Count == 3);
            server.NeedsAttention(Guid.NewGuid().ToString());
            Check("other remote sessions do not leak approval notices", !transport.DrainAttention().Any());
            server.Calls.Clear(); await transport.DeliverAsync(task, "继续", "second-id", default);
            Check("live ssh channel is reused", connections == 1);
            server.Calls.Clear(); server.State = "active";
            result = await transport.DeliverAsync(task, "continue", "busy-id", default);
            Check("live active task is not resumed or interrupted", result.State == "busy" && server.Calls.Count == 1);
            server.Calls.Clear(); server.State = "systemError";
            result = await transport.DeliverAsync(task, "continue", "error-id", default);
            Check("unknown remote state cannot dispatch", result.State == "unavailable" && server.Calls.Count == 1);
            server.Calls.Clear(); server.State = "notLoaded";
            result = await transport.DeliverAsync(task, "continue", "cold-id", default);
            Check("stored task resumes same thread before turn", result.State == "submitted" && server.Calls.Select(c => c.Method).SequenceEqual(new[] { "thread/read", "thread/resume", "turn/start" }));
            server.Calls.Clear(); server.ResumedState = "active";
            result = await transport.DeliverAsync(task, "continue", "race-id", default);
            Check("another client starting during resume blocks dispatch", result.State == "busy" && server.Calls.Count == 2);
            server.Calls.Clear(); server.ResumedState = "idle"; server.WrongId = true;
            result = await transport.DeliverAsync(task, "continue", "wrong-id", default);
            Check("different returned thread fails closed", result.State == "unavailable" && server.Calls.Count == 1);
            server.Calls.Clear(); server.WrongId = false; server.Cwd = "/other";
            result = await transport.DeliverAsync(task, "continue", "cwd-id", default);
            Check("cwd mismatch never sends instruction", result.State == "failed" && server.Calls.All(c => c.Method != "turn/start"));
            server.Calls.Clear(); server.Cwd = "/project"; server.FailMethod = "turn/start"; server.AfterWrite = false;
            result = await transport.DeliverAsync(task, "continue", "prewrite-id", default);
            Check("failure before write remains retryable", result.State == "unavailable");
            server.AfterWrite = true;
            result = await transport.DeliverAsync(task, "continue", "postwrite-id", default);
            Check("failure after write is unconfirmed", result.State == "unconfirmed");
            server.Calls.Clear(); var queue = new TaskControlQueue(settings, transport, null); queue.SetOwner("remote-test-owner");
            var snapshot = new WeChatSnapshot(new[] { task }, DateTimeOffset.UtcNow, new[] { new HostSnapshot { Name = task.Host, LastSuccess = DateTimeOffset.UtcNow } });
            await queue.SubmitAsync(task, "must not replay", "uncertain-id", snapshot, default);
            int sent = server.Calls.Count; await queue.PumpAsync(snapshot, default);
            Check("remote uncertain send cannot replay from queue", server.Calls.Count == sent && queue.Entries.Single().State == "unconfirmed");
            server.FailMethod = ""; server.EmptyTurn = true;
            result = await transport.DeliverAsync(task, "continue", "empty-turn-id", default);
            Check("missing turn confirmation is never success", result.State == "unconfirmed");
            server.EmptyTurn = false; server.Calls.Clear(); task.Host = "unconfigured";
            result = await transport.DeliverAsync(task, "continue", "nohost-id", default);
            Check("unconfigured host cannot connect or dispatch", result.State == "failed" && server.Calls.Count == 0 && connections == 1);
            task.Host = "host-a; touch /tmp/no";
            result = await transport.DeliverAsync(task, "continue", "unsafe-id", default);
            Check("shell injection host is rejected", result.State == "failed" && server.Calls.Count == 0);
            task.Host = "host-a"; server.Connected = false;
            await transport.DeliverAsync(task, "continue", "reconnect-id", default);
            Check("disconnected channel is replaced before writing", server.Disposed && connections == 2);
            var info = RemoteAppServer.StartInfo("host-a", "print('fixed proxy')");
            Check("ssh uses fixed configured alias and no interactive shell", info.ArgumentList.Contains("host-a") && info.ArgumentList.Contains("-T") && info.ArgumentList.Contains("BatchMode=yes") && !info.Arguments.Contains(original));
            string command = RemoteAppServer.ProxyCommand("STATIC\nONLY");
            string encoded = command.Split('\'')[1];
            Check("proxy source survives exact base64 transfer", Encoding.UTF8.GetString(Convert.FromBase64String(encoded)) == "STATIC\nONLY");
            bool invalid = false; try { RemoteAppServer.StartInfo("-oProxyCommand=evil", ""); } catch (InvalidOperationException) { invalid = true; }
            Check("ssh option injection rejected before process launch", invalid);
        }
        finally
        {
            await transport.DisposeAsync();
            Check("dispose closes only owned client channel", server.Disposed);
            await File.WriteAllTextAsync(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(checks, Settings.Json));
        }
    }
}
