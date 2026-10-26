using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

internal static class CodexGatewayTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--codex-gateway-test");
        if (index < 0) return false;
        try { await RunAsync(args[index + 1]); exit(0); }
        catch (Exception ex) { Console.Error.WriteLine(ex); exit(1); }
        return true;
    }
    private sealed class FakeTransport : ITaskTransport
    {
        internal List<TaskRecord> Delivered { get; } = new();
        internal string State { get; set; } = "submitted";
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Delivered.Add(task);
            return Task.FromResult(new TaskDelivery(State, "synthetic receipt", "accepted-turn"));
        }
    }
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string key, bool ok)
        {
            checks.Add(key, ok);
            if (!ok) throw new InvalidOperationException(key);
        }
        try
        {
            var now = DateTimeOffset.UtcNow;
            var settings = new Settings
            {
                LocalEnabled = true, RemoteHosts = new() { "test-remote" },
                ChatGpt = new() { Enabled = true, TaskControlEnabled = true },
                WeChat = new() { ClawBotEnabled = true }
            };
            settings.Normalize();
            string id = "23869080-9835-45ed-8840-bb190da64712";
            var local = new TaskRecord { Id = id, Host = "本机", Title = "local", Cwd = "synthetic/local", TurnId = "turn-a",
                Status = TaskStatusNames.Completed, Source = "synthetic-log", FinalReport = "report-a",
                FinalReportTurnId = "turn-a", FinalReportSource = "final-event", FinalReportTruncated = true,
                Plan = new() { new() { Step = "one", Status = "completed" } } };
            var remote = new TaskRecord { Id = id, Host = "test-remote", Cwd = "synthetic/remote",
                TurnId = "turn-r", Status = TaskStatusNames.Completed };
            var hosts = new[] { new HostSnapshot { Name = "本机", LastSuccess = now },
                new HostSnapshot { Name = "test-remote", LastSuccess = now } };
            WeChatSnapshot snapshot = new(new[] { local, remote }, now, hosts);
            var transport = new FakeTransport();
            var gateway = new CodexGateway(settings, () => snapshot, transport, () => now);
            var localId = CodexGateway.Identity(local);
            var remoteId = CodexGateway.Identity(remote);
            Check("same_thread_distinct_host_identities", localId != remoteId && localId.Key != remoteId.Key);
            Check("legacy_key_preserved", local.Key == "本机:" + id);
            Check("backend_capabilities_differ_explicitly", CodexGateway.Backend(localId) == CodexBackendKind.DesktopExisting
                && CodexGateway.Backend(remoteId) == CodexBackendKind.SharedRemoteExisting);
            var first = gateway.ReadTask(localId);
            Check("read_has_origin_time_and_canonical_identity", first.Value is { Fresh: true } value
                && value.Identity == localId && value.ObservedAt == now && value.EvidenceSource == "synthetic-log");
            first.Value!.Record.Plan[0].Step = "mutation";
            first.Value.Record.Title = "mutation";
            Check("read_is_deep_copy", gateway.ReadTask(localId).Value!.Record.Plan[0].Step == "one"
                && local.Title == "local");
            Check("profiles_never_guessed", !gateway.ReadTask(new(localId.HostId, id, "second")).Found);
            Check("report_needs_turn", !gateway.ReadReport(localId, "").Found);
            Check("report_no_other_turn_fallback", !gateway.ReadReport(localId, "turn-b").Found);
            Check("report_preserves_source_and_truncation", gateway.ReadReport(localId, "turn-a").Value is
                { Text: "report-a", Source: "final-event", Truncated: true });
            var capabilities = gateway.Capabilities(localId);
            Check("continue_requires_live_verification", capabilities.Single(c => c.Capability == CodexCapability.Continue) is
                { Enabled: true, RequiresLiveCheck: true });
            Check("unverified_mutations_disabled", capabilities.Where(c => c.Capability is not
                (CodexCapability.Continue or CodexCapability.ReadTask or CodexCapability.ReadReport)).All(c => !c.Enabled));
            var accepted = await gateway.DeliverAsync(local, "synthetic instruction", "message-1", default);
            Check("acceptance_not_promoted_to_completion", accepted.State == "submitted" && accepted.TurnId == "accepted-turn" && transport.Delivered.Count == 1);
            transport.State = "unconfirmed";
            Check("unknown_preserved_without_gateway_retry", (await gateway.DeliverAsync(remote, "text", "message-2", default)).State == "unconfirmed"
                && transport.Delivered.Count == 2);
            int before = transport.Delivered.Count;
            settings.ChatGpt.TaskControlEnabled = false;
            Check("disabled_control_no_dispatch", (await gateway.DeliverAsync(local, "text", "message-3", default)).State == "unavailable");
            settings.ChatGpt.TaskControlEnabled = true;
            snapshot = snapshot with { ReadAt = now.AddMinutes(-3) };
            Check("stale_snapshot_no_dispatch", (await gateway.DeliverAsync(local, "text", "message-4", default)).State == "unavailable");
            snapshot = snapshot with { ReadAt = now };
            hosts[0].LastSuccess = now.AddMinutes(-3);
            Check("host_age_independent_of_refresh", (await gateway.DeliverAsync(local, "text", "message-5", default)).State == "unavailable");
            hosts[0].LastSuccess = now.AddMinutes(1);
            Check("future_host_observation_no_dispatch", (await gateway.DeliverAsync(local, "text", "message-6", default)).State == "unavailable");
            hosts[0].LastSuccess = now; hosts[0].Error = "offline";
            Check("host_error_no_dispatch", (await gateway.DeliverAsync(local, "text", "message-7", default)).State == "unavailable");
            hosts[0].Error = null;
            local.Status = TaskStatusNames.Active;
            Check("active_task_is_queued", (await gateway.DeliverAsync(local, "text", "message-8", default)).State == "busy");
            local.Status = TaskStatusNames.Completed;
            var oldTask = gateway.ReadTask(localId).Value!.Record;
            local.TurnId = "new-turn";
            Check("turn_changed_requires_new_observation", (await gateway.DeliverAsync(oldTask, "text", "message-9", default)).State == "unavailable");
            local.TurnId = oldTask.TurnId; local.Cwd = "changed-directory";
            Check("directory_changed_no_dispatch", (await gateway.DeliverAsync(oldTask, "text", "message-10", default)).State == "unavailable");
            local.Cwd = oldTask.Cwd;
            snapshot = snapshot with { Tasks = new[] { local, local, remote } };
            Check("duplicate_identity_no_dispatch", (await gateway.DeliverAsync(local, "text", "message-11", default)).State == "failed");
            snapshot = snapshot with { Tasks = new[] { local, remote } };
            var registry = new HostRegistry(settings.HostRegistry);
            var mapping = registry.ProposeAliasRename(remoteId.HostId, "remote-ssh-discovered:new-remote", now);
            registry.ConfirmAliasRename(mapping, new("synthetic-current-user-message", now));
            settings.HostRegistry = registry.Export();
            Check("confirmed_rename_never_redirects_old_control", (await gateway.DeliverAsync(remote, "text", "message-12", default)).State == "failed");
            Check("all_guard_failures_have_no_side_effect", transport.Delivered.Count == before);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await gateway.DeliverAsync(local, "text", "message-13", cancelled.Token); Check("cancellation", false); }
            catch (OperationCanceledException) { Check("cancellation", true); }
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "gateway-checks.json"), JsonSerializer.Serialize(checks, Settings.Json));
        }
    }
}
