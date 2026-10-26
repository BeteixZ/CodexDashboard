using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;

internal enum CodexBackendKind { DesktopExisting, SharedRemoteExisting, AssistantOwned }
internal enum CodexCapability { ReadTask, ReadReport, Continue, Create, Steer, Interrupt, RespondApproval, Rename, Archive }
internal sealed record GatewayCapability(CodexCapability Capability, bool Enabled, string Reason, bool RequiresLiveCheck);
internal sealed record GatewayTask(CanonicalTaskIdentity Identity, TaskRecord Record,
    DateTimeOffset? ObservedAt, bool Fresh, string EvidenceSource);
internal sealed record GatewayReport(CanonicalTaskIdentity Identity, string TurnId, string Text,
    string Source, bool Truncated, DateTimeOffset? ObservedAt);
internal sealed record GatewayRead<T>(T? Value, string Code, string Detail) where T : class
{
    internal bool Found => Value != null;
}

// One boundary for existing backends and future owned app-server sessions.
// No method here treats collector observations as execution or approval authority.
internal interface ICodexGateway : ITaskTransport
{
    IReadOnlyList<GatewayTask> ListTasks();
    GatewayRead<GatewayTask> ReadTask(CanonicalTaskIdentity identity);
    GatewayRead<GatewayReport> ReadReport(CanonicalTaskIdentity identity, string turnId);
    IReadOnlyList<GatewayCapability> Capabilities(CanonicalTaskIdentity identity);
}

internal sealed class CodexGateway : ICodexGateway, IReceiptScopedTaskTransport
{
    private readonly Settings settings;
    private readonly Func<WeChatSnapshot> snapshot;
    private readonly ITaskTransport transport;
    private readonly Func<DateTimeOffset> clock;
    internal Func<CanonicalTaskIdentity, string, CodexCapability, GatewayCapability?>? ExistingControlCapability { get; set; }
    internal CodexGateway(Settings settings, Func<WeChatSnapshot> snapshot, ITaskTransport transport,
        Func<DateTimeOffset>? clock = null)
    {
        this.settings = settings; this.snapshot = snapshot; this.transport = transport;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }
    internal static CanonicalTaskIdentity Identity(TaskRecord task)
        => new(task.Host == HostIdentity.LocalDisplayName ? "local" : "remote-ssh-discovered:" + task.Host, task.Id);
    internal static CodexBackendKind Backend(CanonicalTaskIdentity identity)
        => identity.HostId == "local" ? CodexBackendKind.DesktopExisting : CodexBackendKind.SharedRemoteExisting;

    public IReadOnlyList<GatewayTask> ListTasks()
    {
        var current = snapshot();
        var now = clock();
        var result = new List<GatewayTask>();
        foreach (var task in current.Tasks)
        {
            try { result.Add(ToGateway(task, current, now)); }
            catch (ArgumentException) { /* Invalid collector identities cannot become control targets. */ }
        }
        return result;
    }
    public GatewayRead<GatewayTask> ReadTask(CanonicalTaskIdentity identity)
        => ReadTask(identity, snapshot(), clock());
    private static GatewayRead<GatewayTask> ReadTask(CanonicalTaskIdentity identity, WeChatSnapshot current, DateTimeOffset now)
    {
        // The legacy collectors do not identify multiple profiles; never guess one.
        if (identity.ProfileId != null) return new(null, "profile_not_supported", "当前采集来源不能区分这个配置目录。");
        var matches = current.Tasks.Where(t => Matches(t, identity)).Take(2).ToArray();
        if (matches.Length == 0) return new(null, "task_not_observed", "当前记录中没有这项任务，未改选其他任务。");
        if (matches.Length > 1) return new(null, "ambiguous_task_identity", "记录中有重复任务身份，需要先核对来源。");
        return new(ToGateway(matches[0], current, now), "observed", "这是采集记录；执行前仍需核对原服务。");
    }
    public GatewayRead<GatewayReport> ReadReport(CanonicalTaskIdentity identity, string turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId)) return new(null, "turn_required", "读取报告需要明确轮次。");
        var read = ReadTask(identity);
        if (read.Value == null) return new(null, read.Code, read.Detail);
        var task = read.Value.Record;
        if (task.FinalReportTurnId != turnId || string.IsNullOrWhiteSpace(task.FinalReport))
            return new(null, "report_not_observed_for_turn", "还没有读到该轮的最终报告。");
        return new(new(identity, turnId, task.FinalReport, task.FinalReportSource,
            task.FinalReportTruncated, read.Value.ObservedAt), "report_observed", "报告内容为该轮记录，不代表已经额外复核。");
    }
    public IReadOnlyList<GatewayCapability> Capabilities(CanonicalTaskIdentity identity)
    {
        var current = snapshot();
        var read = ReadTask(identity, current, clock());
        var check = CheckAddress(identity, current, clock());
        bool enabled = settings.ChatGpt.Enabled && settings.ChatGpt.TaskControlEnabled && settings.WeChat.ClawBotEnabled;
        string continueReason = !enabled ? "原任务接续尚未启用。"
            : !check.CanAddress ? Explain(check.Code)
            : read.Value == null ? read.Detail
            : !read.Value.Fresh ? "任务记录已过期或主机离线。"
            : "已接入原任务接续；投递时核对原服务的身份和空闲状态。";
        return Enum.GetValues<CodexCapability>().Select(capability => capability switch
        {
            CodexCapability.ReadTask => new GatewayCapability(capability, read.Found, read.Detail, false),
            CodexCapability.ReadReport => new GatewayCapability(capability, read.Found, "只读取指定轮次的已观察报告。", false),
            CodexCapability.Continue => new GatewayCapability(capability, enabled && check.CanAddress && read.Value?.Fresh == true, continueReason, true),
            CodexCapability.Steer or CodexCapability.Interrupt when Backend(identity) == CodexBackendKind.SharedRemoteExisting
                && check.CanAddress && read.Value?.Fresh == true && ExistingControlCapability?.Invoke(identity, read.Value.Record.Cwd, capability) is { } shared => shared,
            _ => new GatewayCapability(capability, false,
                Backend(identity) == CodexBackendKind.DesktopExisting
                    ? "现有桌面任务适配器尚未开放此操作。"
                    : "共享远程服务的此项能力尚未完成实测，保持关闭。", true)
        }).ToArray();
    }
    public async Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
        => await DeliverCoreAsync(task, text, messageId, null, ct);
    public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId,
        ControlDispatchProvenance provenance, CancellationToken ct)
        => DeliverCoreAsync(task, text, messageId, provenance, ct);
    private async Task<TaskDelivery> DeliverCoreAsync(TaskRecord task, string text, string messageId,
        ControlDispatchProvenance? provenance, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CanonicalTaskIdentity identity;
        try { identity = Identity(task); }
        catch (ArgumentException) { return new("failed", "任务身份无效，未投递。"); }
        if (!settings.ChatGpt.Enabled || !settings.ChatGpt.TaskControlEnabled || !settings.WeChat.ClawBotEnabled)
            return new("unavailable", "原任务接续尚未启用，指令仍在等待。");
        var current = snapshot();
        var now = clock();
        var check = CheckAddress(identity, current, now);
        if (!check.CanAddress)
        {
            bool identityFailure = check.Code is "renamed_identity_requires_new_explicit_target" or "mapping_unconfirmed"
                or "identity_not_resolved" or "unknown_host_or_profile" or "invalid_host_registry";
            return new(identityFailure ? "failed" : "unavailable", Explain(check.Code));
        }
        var read = ReadTask(identity, current, now);
        if (read.Value == null) return new("failed", read.Detail);
        if (!read.Value.Fresh) return new("unavailable", "任务记录过期或主机离线，指令仍在等待。");
        var latest = read.Value.Record;
        if (task.Cwd != latest.Cwd || task.TurnId != latest.TurnId)
            return new("unavailable", "任务目录或轮次刚发生变化，等待重新核对后再投递。");
        if (latest.Status is TaskStatusNames.Active or TaskStatusNames.Waiting)
            return new("busy", "原任务仍在运行或等待决定，指令保持排队。");
        // The adapter performs live backend read/resume and preserves the original thread settings.
        // Its submitted result means accepted; neither execution nor verification is inferred here.
        return provenance != null && transport is IReceiptScopedTaskTransport scoped
            ? await scoped.DeliverAsync(latest, text, messageId, provenance, ct)
            : await transport.DeliverAsync(latest, text, messageId, ct);
    }
    private HostControlCheck CheckAddress(CanonicalTaskIdentity identity, WeChatSnapshot current, DateTimeOffset now)
    {
        try
        {
            var registry = new HostRegistry(settings.HostRegistry);
            foreach (var host in current.Hosts)
            {
                string hostId = host.Name == HostIdentity.LocalDisplayName ? "local" : "remote-ssh-discovered:" + host.Name;
                var registered = registry.Registrations.SingleOrDefault(h => h.HostId == hostId && h.ProfileId == null);
                if (registered == null) continue;
                if (host.Error != null)
                    registry.Observe(registered.Identity, HostAvailability.Offline, now);
                else if (host.LastSuccess is { } observed)
                    registry.Observe(registered.Identity, HostAvailability.Online, observed);
            }
            return registry.CheckControlTarget(identity, now);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { return new(identity, false, "invalid_host_registry"); }
    }
    private static bool Matches(TaskRecord task, CanonicalTaskIdentity identity)
    {
        try { return Identity(task) == identity; }
        catch (ArgumentException) { return false; }
    }
    private static GatewayTask ToGateway(TaskRecord task, WeChatSnapshot current, DateTimeOffset now)
    {
        var hosts = current.Hosts.Where(h => h.Name == task.Host).Take(2).ToArray();
        var host = hosts.Length == 1 ? hosts[0] : null;
        var observed = host?.LastSuccess;
        bool fresh = !task.Disconnected && host != null && host.Error == null && observed != null
            && now - observed <= TimeSpan.FromMinutes(2) && observed <= now.AddSeconds(30)
            && current.ReadAt != null && now - current.ReadAt <= TimeSpan.FromMinutes(2)
            && current.ReadAt <= now.AddSeconds(30);
        return new(Identity(task), new TaskCopy(task), observed, fresh, task.Source);
    }
    private sealed class TaskCopy(TaskRecord source) : TaskRecord(source);
    private static string Explain(string code) => code switch
    {
        "renamed_identity_requires_new_explicit_target" => "主机别名已经变更，旧指令不会自动改投新主机；需要重新指定目标。",
        "mapping_unconfirmed" => "主机身份变更尚未确认，未投递。",
        "identity_not_resolved" => "主机配置身份有冲突或需要指定配置目录，未投递。",
        "unknown_host_or_profile" => "没有找到这个主机与配置身份，未投递。",
        "host_not_configured" => "该主机已不在当前配置中，指令保持等待。",
        "host_disabled" => "该主机已停用，指令保持等待。",
        "host_offline" => "主机离线，指令保持等待。",
        "host_observation_unknown" => "尚未成功读取该主机，指令保持等待。",
        "host_observation_stale_or_invalid" => "主机记录已过期，指令保持等待。",
        _ => "主机注册信息无效，未投递；原始记录保持不变。"
    };
}
