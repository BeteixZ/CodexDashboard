using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Assistant.Core;
using ChannelConnectionState = Codex.Protocol.ConnectionState;

namespace CodexDashboard;

// All entry points run on the WPF dispatcher. Awaiting HTTP never blocks the UI.
internal sealed partial class WeChatService : IAsyncDisposable
{
    private readonly SemaphoreSlim typingGate = new(1, 1);
    internal IAsyncDisposable? StartTyping(CancellationToken generation)
    {
        if (!settings.ActiveMedia.Typing || disposed || stopRequested
            || session == null || binding == null || contextBlocked || binding.ContextToken.Length == 0) return null;
        var captured = new ClawBinding { BaseUrl = binding.BaseUrl, BotId = binding.BotId, UserId = binding.UserId,
            Token = binding.Token, ContextToken = binding.ContextToken };
        return new WeChatTypingScope(new WeChatApi(), captured, typingGate, generation, session.Token);
    }
    private readonly Settings settings;
    private readonly Func<WeChatSnapshot> snapshot;
    private readonly Action persist;
    private readonly WeChatApi api;
    private readonly bool receiveMessages;
    private readonly IWeChatAssistant? assistant;
    private readonly ICompletionSummarizer? summarizer;
    private readonly bool requireDurableAssistant;
    internal DurableAssistantRuntime? DurableRuntime { get; set; }
    private Task? aiWork;
    private readonly WeChatReports reports = new();
    private readonly WeChatFieldProbe fieldProbe = new();
    private readonly SemaphoreSlim configureGate = new(1, 1);
    private readonly SemaphoreSlim clawSendGate = new(1, 1);
    private readonly SemaphoreSlim pushSendGate = new(1, 1);
    private CancellationTokenSource? session;
    private ReceiverLease? receiverLease;
    private Task? stopping;
    private bool stopRequested;
    private Task? receiver, sender, durableNotifications;
    private Channel<WeChatNotification>? queue;
    private ClawBinding? binding;
    private sealed class BindingProgress(ClawBinding account, string expectedCipher)
    {
        internal ClawBinding Account { get; } = account;
        internal string ExpectedCipher { get; set; } = expectedCipher;
    }
    private BindingProgress? bindingProgress;
    private string signature = "";
    private DateTimeOffset lastPushAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastClawAt = DateTimeOffset.MinValue;
    private bool contextBlocked;
    private string? channelPresentationError;
    private sealed class ContextUnavailableException(string message) : InvalidOperationException(message);
    private sealed class DeliveryNoLongerCurrentException() : InvalidOperationException("The delivery was invalidated before the channel call.");
    private sealed record DeferredClaw(WeChatNotification Message, string? PreparedBody);
    private readonly Dictionary<string, DeferredClaw> deferredClaw = new();
    private readonly Dictionary<string, string> preparedClaw = new();
    internal object Diagnostics => new {
        clawStatus = ClawStatus, pushStatus = PushStatus, contextBlocked,
        bound = binding != null, contextPresent = binding?.ContextToken.Length > 0,
        senderRunning = sender is { IsCompleted: false }, receiverRunning = receiver is { IsCompleted: false },
        pendingClawNotifications = deferredClaw.Count,
        lastNotificationAttempt = LastNotificationAttempt,
        connectionState = ConnectionState, receiverLeaseHeld = receiverLease != null, stopping = stopRequested,
        wakePresentationError, wakeRateWaitActive = WakeRateWaitActive, channelPresentationError
    };
    internal WeChatReceipt? LastNotificationAttempt { get; private set; }
    private bool disposed;
    public string ClawStatus { get; private set; } = "ClawBot 未启用";
    public string PushStatus { get; private set; } = "公众号通知未启用";
    internal ChannelConnectionState ConnectionState { get; private set; } = ChannelConnectionState.NotConfigured;
    public event Action? Changed;
    public event Action<WeChatReceipt>? NotificationAttempted;
    internal TimeSpan SummaryTimeout { get; init; } = TimeSpan.FromSeconds(45);
    internal TimeSpan ReportWaitTimeout { get; init; } = TimeSpan.FromSeconds(15);
    internal TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>After DisposeAsync, await this before releasing the profile runtime lease.</summary>
    internal Task WhenStopped => stopping ?? Task.CompletedTask;
    internal bool HasPendingStop => stopping is { IsCompleted: false };
    public event Action<CompletionSummary>? CompletionPrepared;
    internal PreparedCompletionInfo? LastPreparedCompletion { get; private set; }
    public WeChatService(Settings settings, Func<WeChatSnapshot> snapshot, Action persist, HttpMessageHandler? handler = null, bool receiveMessages = true, IWeChatAssistant? assistant = null, ICompletionSummarizer? summarizer = null, bool requireDurableAssistant = false)
    { this.settings = settings; this.snapshot = snapshot; this.persist = persist; this.receiveMessages = receiveMessages; this.assistant = assistant; this.summarizer = summarizer ?? assistant as ICompletionSummarizer; this.requireDurableAssistant = requireDurableAssistant; api = new(handler); }

    public async Task ConfigureAsync()
    {
        await configureGate.WaitAsync();
        try
        {
            // Settings already holds the requested identity. Notify memory views
            // before early failure/stop paths, without recursively reconfiguring.
            if (assistant is ChatGptAssistant memoryOwner) memoryOwner.NotifyMemoryContextChanged();
            if (disposed) return;
            ClawBinding? candidate;
            bool corruptBinding = false;
            try { candidate = settings.WeChat.ReadBinding(); }
            catch (Exception) { candidate = null; corruptBinding = true; }
            if (settings.WeChat.PushPlusEnabled && !string.IsNullOrEmpty(settings.WeChat.ProtectedPushPlusToken)
                && settings.WeChat.EnsureWakeRouteId())
            {
                var initializedRoute = settings.WeChat.PushPlusWakeRouteId;
                try { persist(); }
                catch
                {
                    if (settings.WeChat.PushPlusWakeRouteId == initializedRoute)
                        settings.WeChat.PushPlusWakeRouteId = Guid.Empty;
                    throw; // No activation may rely on an unpersisted route.
                }
            }
            string next = ConfigurationSignature(candidate);
            if (next == signature && !stopRequested
                && (candidate == null ? bindingProgress == null : BindingProgressCurrent(bindingProgress))) return;
            if (!await StopAsync()) return;
            stopping = null; stopRequested = false;
            // The previous receiver may have durably advanced its cursor while
            // draining, or the user may have saved another binding meanwhile.
            // Never reactivate the candidate captured before that await.
            corruptBinding = false;
            try { candidate = settings.WeChat.ReadBinding(); }
            catch (Exception) { candidate = null; corruptBinding = true; }
            next = ConfigurationSignature(candidate);
            if (receiveMessages && settings.WeChat.ClawBotEnabled && candidate != null)
            {
                try { receiverLease = ReceiverLease.TryAcquire(candidate.BotId); }
                catch
                {
                    ConnectionState = ChannelConnectionState.Degraded;
                    ClawStatus = "无法取得微信接收权，未启动轮询。";
                    Changed?.Invoke();
                    return;
                }
                if (receiverLease == null)
                {
                    ConnectionState = ChannelConnectionState.Degraded;
                    ClawStatus = "此微信绑定已由另一个助手接收，当前实例未启动轮询或通知。";
                    Changed?.Invoke();
                    return;
                }
            }
            try
            {
            signature = next; binding = candidate; contextBlocked = false;
            bindingProgress = candidate == null ? null : new(candidate, settings.WeChat.ProtectedBinding);
            if (assistant is ChatGptAssistant ai) ai.Reset();
            if (DurableRuntime == null && assistant is TaskControlAssistant control)
                control.SetOwner(WeChatText.Hash((candidate?.BotId ?? "") + "\n" + (candidate?.UserId ?? "") + "\n" + (candidate?.Token ?? "")));
            if (!settings.WeChat.ReportQueriesEnabled && summarizer?.CompletionSummariesEnabled != true) reports.Clear();
            session = new CancellationTokenSource();
            if (DurableRuntime != null && settings.WeChat.ClawBotEnabled && candidate != null)
                await DurableRuntime.ActivateAsync(candidate, session.Token);
            queue = Channel.CreateBounded<WeChatNotification>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
            ConnectionState = corruptBinding ? ChannelConnectionState.Degraded
                : !settings.WeChat.ClawBotEnabled || binding == null ? ChannelConnectionState.NotConfigured
                : receiveMessages ? ChannelConnectionState.Connecting : ChannelConnectionState.Unknown;
            ClawStatus = corruptBinding ? "凭据无法解密，请重新绑定。"
                : !settings.WeChat.ClawBotEnabled ? "ClawBot 未启用" : binding == null ? "尚未绑定，请扫码连接" : "正在连接 ClawBot…";
            PushStatus = settings.WeChat.PushPlusEnabled
                ? DurableRuntime != null
                    ? !settings.WeChat.ClawBotEnabled || binding == null
                        ? "短唤醒已勾选；需连接并启用 ClawBot 才能生效。"
                        : !ReadWakeConfiguration().Enabled ? "短唤醒配置需检查；未启动发送，待补报正文保留。"
                        : "短唤醒已启用；仅在微信正文需要补报时尝试"
                    : "公众号通知已启用，等待状态变化"
                : "公众号通知未启用";
            sender = SendLoopAsync(queue.Reader, session.Token);
            if (DurableRuntime?.Identity != null)
                durableNotifications = DurableNotificationLoopAsync(session.Token);
            if (receiveMessages && settings.WeChat.ClawBotEnabled && bindingProgress != null) receiver = ReceiveLoopAsync(bindingProgress, session.Token);
            Changed?.Invoke();
            }
            catch
            {
                // Setup callbacks may fail after a receiver has already started.
                // Stop all owned work before returning its binding lease.
                await StopAsync();
                throw;
            }
        }
        finally { configureGate.Release(); }
    }
    private string ConfigurationSignature(ClawBinding? candidate)
    {
        string value = $"{settings.WeChat.ClawBotEnabled}:{settings.WeChat.PushPlusEnabled}:{settings.WeChat.IncludeSummary}:{settings.WeChat.ReportQueriesEnabled}:{settings.WeChat.NotifyCompleted}:{settings.WeChat.NotifyFailed}:{settings.WeChat.NotifyWaiting}:{settings.WeChat.ProtectedPushPlusToken}:{candidate?.Token}:{candidate?.BotId}:{candidate?.UserId}:{candidate?.BaseUrl}";
        if (requireDurableAssistant) value += $":{settings.ChatGpt.ActiveAccount}:{settings.ChatGpt.Model}:{settings.ChatGpt.Enabled}:{settings.ChatGpt.TaskControlEnabled}";
        if (requireDurableAssistant) value += $":{settings.WeChat.PushPlusWakeRouteId}:{settings.WeChat.PushPlusWakeQuietStart}:{settings.WeChat.PushPlusWakeQuietEnd}:{settings.WeChat.PushPlusWakeTimeZoneId}";
        return value;
    }
    private bool BindingProgressCurrent(BindingProgress? progress) => progress != null
        && ReferenceEquals(bindingProgress, progress) && ReferenceEquals(binding, progress.Account)
        && settings.WeChat.ProtectedBinding == progress.ExpectedCipher;
    private bool MayContinueReceiving(BindingProgress progress, CancellationToken cancellation) => !disposed
        && !stopRequested && !cancellation.IsCancellationRequested && BindingProgressCurrent(progress);
    public async Task ObserveAsync(TaskRecord task)
    {
        if (!settings.WeChat.Wants(task.Status) || (!settings.WeChat.ClawBotEnabled && !settings.WeChat.PushPlusEnabled)) return;
        if (disposed || stopRequested || queue == null)
            throw new IOException("通知连接正在切换，本次状态变化尚未登记。");
        if (requireDurableAssistant && DurableRuntime == null)
            throw new IOException("持久对话记录不可用，本次通知尚未登记。");
        if (requireDurableAssistant && settings.WeChat.ClawBotEnabled && DurableRuntime?.Identity == null)
            throw new IOException("微信绑定尚未激活，本次通知尚未登记。");
        if (DurableRuntime?.Identity != null && settings.WeChat.ClawBotEnabled)
        {
            if (!await DurableRuntime.ShouldNotifyDefaultTaskAsync(task, session?.Token ?? CancellationToken.None)) return;
            var message = WeChatText.Notification(task, settings.WeChat.IncludeSummary, DateTimeOffset.UtcNow, settings.WeChat.ReportQueriesEnabled);
            await DurableRuntime.CaptureNotificationAsync(message, session?.Token ?? CancellationToken.None);
        }
        Observe(task);
    }
    public void Observe(TaskRecord task)
    {
        if (disposed || stopRequested || queue == null || (!settings.WeChat.ClawBotEnabled && !settings.WeChat.PushPlusEnabled) || !settings.WeChat.Wants(task.Status)) return;
        if (requireDurableAssistant && DurableRuntime == null) return;
        if (settings.WeChat.ReportQueriesEnabled || summarizer?.CompletionSummariesEnabled == true) reports.Capture(new[] { task });
        // Durable notifications and short wakeups have independent journals.
        // The legacy queue must not mirror their complete bodies to pushplus.
        if (DurableRuntime != null) return;
        var message = WeChatText.Notification(task, settings.WeChat.IncludeSummary, DateTimeOffset.UtcNow, settings.WeChat.ReportQueriesEnabled);
        if (!queue.Writer.TryWrite(message)) { PushStatus = "通知队列已满；请用“最近完成”查询遗漏结果。"; Changed?.Invoke(); }
    }
    public void PublishReports()
    {
        if (!disposed && (settings.WeChat.ReportQueriesEnabled || summarizer?.CompletionSummariesEnabled == true)) reports.Capture(snapshot().Tasks);
    }
    private bool Attempted(string channel, string destination, string key) => settings.WeChat.RecentAttempts.Contains(WeChatText.Hash(channel + "\n" + destination + "\n" + key));
    private bool Attempt(string channel, string destination, string key)
    {
        string hash = WeChatText.Hash(channel + "\n" + destination + "\n" + key);
        if (settings.WeChat.RecentAttempts.Contains(hash)) return false;
        settings.WeChat.RecentAttempts.Add(hash);
        settings.WeChat.RecentAttempts = settings.WeChat.RecentAttempts.TakeLast(512).ToList();
        // At most one automatic attempt per channel/recipient/turn/status. Ambiguous
        // network failures must not generate duplicate or runaway outbound messages.
        persist(); return true;
    }
    private void DeferClawNotification(WeChatNotification message, string? body)
    {
        foreach (var key in deferredClaw.Where(p => DateTimeOffset.UtcNow - p.Value.Message.CreatedAt > TimeSpan.FromMinutes(15)).Select(p => p.Key).ToArray()) deferredClaw.Remove(key);
        if (deferredClaw.TryGetValue(message.Key, out var existing)) deferredClaw[message.Key] = new(message, body ?? existing.PreparedBody);
        else if (deferredClaw.Count < 64) deferredClaw[message.Key] = new(message, body);
        else ClawStatus = "待恢复的微信通知已满；请用“最近完成”查询遗漏结果。";
        Changed?.Invoke();
    }
    private void ResumeDeferredClaw()
    {
        if (deferredClaw.Count == 0 || contextBlocked || binding?.ContextToken.Length is not > 0 || queue == null) return;
        foreach (var pair in deferredClaw.ToArray())
        {
            if (DateTimeOffset.UtcNow - pair.Value.Message.CreatedAt > TimeSpan.FromMinutes(15)) { deferredClaw.Remove(pair.Key); continue; }
            if (!queue.Writer.TryWrite(pair.Value.Message)) break;
            if (pair.Value.PreparedBody != null) preparedClaw[pair.Key] = pair.Value.PreparedBody;
            deferredClaw.Remove(pair.Key);
        }
        Changed?.Invoke();
    }
    private void RecordNotificationAttempt(WeChatReceipt receipt)
    {
        LastNotificationAttempt = receipt;
        // Presentation observers cannot change a channel result already obtained.
        try { NotificationAttempted?.Invoke(receipt); }
        catch { channelPresentationError = "notification_receipt_observer_failed"; }
        try { Changed?.Invoke(); }
        catch { channelPresentationError = "channel_status_observer_failed"; }
    }
    private async Task DurableNotificationLoopAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            bool failed = false;
            try
            {
                if (DurableRuntime == null) return;
                foreach (var message in await DurableRuntime.PendingNotificationsAsync(cancellation))
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        await DurableRuntime.PrepareNotificationAsync(message.Key, async (captured, ct) =>
                            captured.Completion != null && (settings.ChatGpt.CompletionSummariesEnabled || summarizer?.CompletionSummariesEnabled == true)
                                ? await PrepareCompletionWithDependenciesAsync(captured, ct, durablePreparation: true)
                                : new PreparedCompletion(captured.NaturalBody ?? captured.Body, Assistant.Core.OutputDependencies.Independent), cancellation);
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch (Assistant.Core.LedgerException error) when (error.Code == Assistant.Core.LedgerErrors.NotificationSuppressed)
                    { /* A new subscription changed this pending default notice. */ }
                    catch (Exception) { failed = true; }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch (Exception) { failed = true; }
            if (failed)
            {
                ClawStatus = "有通知尚未完成持久准备，原记录已保留；正在等待恢复。";
                Changed?.Invoke();
            }
            try { await Task.Delay(failed ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(500), cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
        }
    }
    private async Task SendLoopAsync(ChannelReader<WeChatNotification> reader, CancellationToken cancellation)
    {
        try
        {
            await foreach (var message in reader.ReadAllAsync(cancellation))
            {
                preparedClaw.Remove(message.Key, out string? preparedBody);
                if (DurableRuntime == null && DateTimeOffset.UtcNow - message.CreatedAt > TimeSpan.FromMinutes(15)) continue;
                if (requireDurableAssistant && DurableRuntime == null) continue;
                if (DurableRuntime != null && !await DurableRuntime.ShouldNotifyDefaultTaskAsync(message.Task, cancellation)) continue;
                bool clawDue = settings.WeChat.ClawBotEnabled && binding != null
                    && (DurableRuntime != null
                        ? !await DurableRuntime.NotificationKnownAsync("notice/" + message.Key, cancellation)
                        : !Attempted("claw", binding.BotId + ":" + binding.UserId, message.Key));
                string pushToken = "";
                if (DurableRuntime == null && settings.WeChat.PushPlusEnabled)
                {
                    try { pushToken = settings.WeChat.ReadPushPlusToken(); }
                    catch (Exception ex) { PushStatus = WeChatApi.Explain(ex); Changed?.Invoke(); }
                }
                bool pushDue = pushToken.Length > 0 && !Attempted("pushplus", WeChatText.Hash(pushToken), message.Key);
                if (!clawDue && !pushDue) continue;
                bool wantsSummary = message.Completion != null && (settings.ChatGpt.CompletionSummariesEnabled || summarizer?.CompletionSummariesEnabled == true);
                string body = preparedBody ?? (wantsSummary
                    ? CompletionSummaryText.Body(message.Completion!.Report, new("", "", false, "微信会话尚不可用，摘要还未生成。", FailureKind: CompletionSummaryFailureKind.ChannelUnavailable))
                    : message.Body);
                if (wantsSummary && preparedBody == null
                    && (pushDue || clawDue && !contextBlocked && binding!.ContextToken.Length > 0))
                {
                    body = await PrepareCompletionAsync(message, cancellation);
                    preparedBody = body;
                }
                if (DurableRuntime == null && DateTimeOffset.UtcNow - message.CreatedAt > TimeSpan.FromMinutes(15)) continue;
                if (DurableRuntime != null && !await DurableRuntime.ShouldNotifyDefaultTaskAsync(message.Task, cancellation)) continue;
                if (clawDue && binding != null && DurableRuntime != null)
                {
                    try
                    {
                        await DurableRuntime.EnqueueNotificationAsync("notice/" + message.Key,
                            message.Completion == null ? message.NaturalBody ?? body : body, message.Task, cancellation,
                            isDefaultTaskNotification: true);
                    }
                    catch (Assistant.Core.LedgerException error) when (error.Code == Assistant.Core.LedgerErrors.NotificationSuppressed)
                    { continue; }
                }
                else if (clawDue && binding != null && (contextBlocked || binding.ContextToken.Length == 0))
                    DeferClawNotification(message, preparedBody);
                else if (clawDue && binding != null && Attempt("claw", binding.BotId + ":" + binding.UserId, message.Key))
                {
                    try
                    {
                        bool? accepted = await SendClawAsync(message.Completion == null ? message.Title + "\n" + body : body, "codex-" + message.Key[..40], cancellation);
                        if (message.Completion != null && assistant is TaskControlAssistant control) control.NoteNotification(message.Completion.TaskKey);
                        RecordNotificationAttempt(new(message.Key, "clawbot", accepted, ClawStatus));
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        ClawStatus = WeChatApi.Explain(ex); Changed?.Invoke();
                        if (ex is WeChatApiException { Code: -2 } or ContextUnavailableException)
                        {
                            // The server explicitly rejected this send. Releasing only
                            // this marker is safe; ambiguous outcomes stay deduplicated.
                            settings.WeChat.RecentAttempts.Remove(WeChatText.Hash("claw\n" + binding.BotId + ":" + binding.UserId + "\n" + message.Key));
                            persist();
                            DeferClawNotification(message, preparedBody);
                        }
                        RecordNotificationAttempt(new(message.Key, "clawbot", ex is WeChatApiException ? false : null, ClawStatus));
                    }
                }
                if (pushDue)
                {
                    try
                    {
                        if (Attempt("pushplus", WeChatText.Hash(pushToken), message.Key)) await SendPushAsync(pushToken,
                            message.Completion == null ? message.Title : WeChatText.Clean(body.Split('\n')[0], 100), body, cancellation,
                            DurableRuntime == null ? null : ct => DurableRuntime.ShouldNotifyDefaultTaskAsync(message.Task, ct));
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch (Exception ex) { PushStatus = WeChatApi.Explain(ex); Changed?.Invoke(); }
                }
                ResumeDeferredClaw();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { PushStatus = "通知处理已停止：" + WeChatApi.Explain(ex); Changed?.Invoke(); }
    }
    private async Task<string> PrepareCompletionAsync(WeChatNotification message, CancellationToken cancellation, bool durablePreparation = false)
        => (await PrepareCompletionWithDependenciesAsync(message, cancellation, durablePreparation)).Text;

    private async Task<PreparedCompletion> PrepareCompletionWithDependenciesAsync(WeChatNotification message, CancellationToken cancellation, bool durablePreparation = false)
    {
        var notice = message.Completion!;
        WeChatReport? report = notice.Report;
        CompletionSummary result;
        try
        {
            if (summarizer?.CompletionSummariesEnabled != true) throw new ChatGptException("摘要助手暂时不可用，未转发原始报告。", ChatGptFailureKind.Unavailable);
            DateTimeOffset until = DateTimeOffset.UtcNow + ReportWaitTimeout;
            while (report == null && notice.TurnId.Length > 0)
            {
                report = reports.Find(notice.ReportId);
                if (report?.TurnId != notice.TurnId) report = null;
                if (report == null)
                {
                    var task = snapshot().Tasks.FirstOrDefault(t => t.Key == notice.TaskKey && t.TurnId == notice.TurnId && t.Status == TaskStatusNames.Completed);
                    if (task != null) report = WeChatReports.FromTask(task);
                }
                if (report != null || DateTimeOffset.UtcNow >= until) break;
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation);
            }
            if (report == null) result = new("", "", false, "尚未读取到对应轮次的公开最终报告。", FailureKind: CompletionSummaryFailureKind.ReportUnavailable);
            else if (!durablePreparation && !Attempt("chatgpt-summary", "completion", message.Key)) result = new("", "", false, "本轮摘要已尝试，未重复调用模型。", FailureKind: CompletionSummaryFailureKind.AlreadyAttempted);
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(SummaryTimeout);
                result = await summarizer!.SummarizeAsync(report, timeout.Token);
                if (result.Generated) result = result with { Text = CompletionSummaryText.Validate(result.Text, report) };
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { result = new("", "", false, "生成摘要等待超时，已保留完成提醒。", FailureKind: CompletionSummaryFailureKind.Timeout); }
        catch (Exception ex) { result = new("", "", false, ChatGptAssistant.Explain(ex), FailureKind: CompletionSummaryText.FailureFrom(ex)); }
        string body = CompletionSummaryText.Body(report, result);
        LastPreparedCompletion = new(DateTimeOffset.UtcNow, result.Generated, report?.Text.Length ?? 0, body.Length,
            result.Generated ? result.ModelRequests : null, result.RewriteRetried, result.NetworkRetried, result.FailureKind);
        CompletionPrepared?.Invoke(result);
        // A generated body without capture is legacy/unknown, never implicitly
        // independent. Only this program-authored fallback has no memory input.
        return new(body, result.Generated ? result.Dependencies ?? Assistant.Core.OutputDependencies.LegacyUnknown : Assistant.Core.OutputDependencies.Independent);
    }
    private async Task<bool?> SendClawAsync(string text, string id, CancellationToken cancellation, Func<bool>? stillCurrent = null,
        bool preserveText = false, Action<string>? observeMessageId = null)
    {
        if (disposed || stopRequested || session == null) throw new InvalidOperationException("微信连接已停止，未发送消息。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, session.Token);
        cancellation = linked.Token;
        await clawSendGate.WaitAsync(cancellation);
        try
        {
            if (binding == null) throw new InvalidOperationException("请先扫码绑定 ClawBot。");
            if (contextBlocked) throw new ContextUnavailableException("会话凭据已失效；请先在微信给 ClawBot 发一条新消息，再查询或测试。");
            if (binding.ContextToken.Length == 0) throw new ContextUnavailableException("请先在微信给 ClawBot 发送“帮助”，激活收发消息。");
            var delay = TimeSpan.FromSeconds(1) - (DateTimeOffset.UtcNow - lastClawAt);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
            if (stillCurrent != null && !stillCurrent()) throw new DeliveryNoLongerCurrentException();
            lastClawAt = DateTimeOffset.UtcNow;
            bool? accepted;
            string sentContext = binding.ContextToken;
            try { accepted = await api.SendClawAsync(binding, text, id, cancellation, preserveText, observeMessageId); }
            catch (WeChatApiException ex) when (ex.Code == -2)
            {
                contextBlocked = binding.ContextToken == sentContext;
                if (contextBlocked) ConnectionState = ChannelConnectionState.Degraded;
                throw;
            }
            ClawStatus = accepted == true ? $"微信已受理 {DateTime.Now:HH:mm:ss}；请以手机收信为准"
                : $"微信请求已完成 {DateTime.Now:HH:mm:ss}；接口未提供确认状态，请以手机收信为准";
            try { Changed?.Invoke(); }
            catch { channelPresentationError = "channel_status_observer_failed"; }
            return accepted;
        }
        finally { clawSendGate.Release(); }
    }
    private async Task SendPushAsync(string token, string title, string body, CancellationToken cancellation,
        Func<CancellationToken, Task<bool>>? stillAllowed = null)
    {
        if (disposed || stopRequested) throw new InvalidOperationException("通知连接已停止，未发送消息。");
        using var linked = session == null ? CancellationTokenSource.CreateLinkedTokenSource(cancellation)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellation, session.Token);
        cancellation = linked.Token;
        await pushSendGate.WaitAsync(cancellation);
        try
        {
            var delay = TimeSpan.FromSeconds(13) - (DateTimeOffset.UtcNow - lastPushAt);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation);
            if (stillAllowed != null && !await stillAllowed(cancellation)) return;
            lastPushAt = DateTimeOffset.UtcNow;
            await api.SendPushPlusAsync(token, title, body, cancellation);
            PushStatus = $"pushplus 已受理 {DateTime.Now:HH:mm:ss}；尚未确认手机送达"; Changed?.Invoke();
        }
        finally { pushSendGate.Release(); }
    }
    public async Task<string> TestClawAsync(CancellationToken cancellation)
    {
        try { await SendClawAsync("助手测试通知\n微信连接可用；请回复“状态”查询当前任务。", "codex-test-" + Guid.NewGuid().ToString("N"), cancellation); return ClawStatus; }
        catch (Exception ex) { ClawStatus = WeChatApi.Explain(ex); Changed?.Invoke(); return ClawStatus; }
    }
    internal Task<bool?> SendAiTestAsync(string text, CancellationToken cancellation) => SendClawAsync("[测试] ChatGPT 微信问答\n" + WeChatText.Clean(text, 1800), "codex-ai-test-" + Guid.NewGuid().ToString("N"), cancellation);
    public async Task<string> TestPushAsync(string token, CancellationToken cancellation)
    {
        try { await SendPushAsync(token, "助手测试通知", "这是一条测试消息，请确认手机微信是否收到。", cancellation); return PushStatus; }
        catch (Exception ex) { PushStatus = WeChatApi.Explain(ex); Changed?.Invoke(); return PushStatus; }
    }
    private async Task ReceiveLoopAsync(BindingProgress progress, CancellationToken cancellation)
    {
        var account = progress.Account;
        int failures = 0;
        try
        {
            try { await api.LifecycleAsync(account, true, cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch { /* Lifecycle hints are best effort; getUpdates is authoritative. */ }
            while (MayContinueReceiving(progress, cancellation))
            {
                try
                {
                    using var response = await api.GetUpdatesAsync(account, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (!MayContinueReceiving(progress, cancellation)) break;
                    bool wasConnected = ConnectionState == ChannelConnectionState.Connected;
                    ConnectionState = contextBlocked ? ChannelConnectionState.Degraded : ChannelConnectionState.Connected;
                    var root = response.RootElement;
                    if (!await ProcessUpdateBatchAsync(progress, root, cancellation)) break;
                    string cursor = WeChatApi.String(root, "get_updates_buf");
                    if (cursor.Length > 0 && cursor != account.Cursor)
                    { account.Cursor = cursor; if (!SaveBinding(progress)) break; }
                    if (!MayContinueReceiving(progress, cancellation)) break;
                    if (failures > 0 || !wasConnected || ClawStatus == "正在连接 ClawBot…")
                    {
                        ClawStatus = account.ContextToken.Length == 0 ? "ClawBot 已连接，请在微信发送“帮助”激活" : "ClawBot 已连接，等待消息或状态变化";
                        Changed?.Invoke();
                    }
                    failures = 0;
                    // Some backends answer empty polls immediately. Avoid a busy loop.
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (WeChatApiException ex) when (ex.Code == -14 || ex.Code == 401 || ex.Code == 403)
                {
                    if (!MayContinueReceiving(progress, cancellation)) break;
                    ConnectionState = ChannelConnectionState.Degraded; ClawStatus = WeChatApi.Explain(ex); Changed?.Invoke(); break;
                }
                catch (Exception ex)
                {
                    if (!MayContinueReceiving(progress, cancellation)) break;
                    ConnectionState = ChannelConnectionState.Disconnected;
                    ClawStatus = WeChatApi.Explain(ex) + " 正在等待重连。"; Changed?.Invoke();
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(failures++, 4)))), cancellation);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await api.LifecycleAsync(account, false, cleanup.Token); } catch { }
        }
    }
    internal Task ProcessUpdatesAsync(JsonElement root, CancellationToken cancellation)
        => ProcessUpdateBatchAsync(bindingProgress ?? throw new InvalidOperationException("当前没有微信绑定。"), root, cancellation);
    internal Task ProcessUpdatesAsync(ClawBinding account, JsonElement root, CancellationToken cancellation)
    {
        if (bindingProgress == null || !ReferenceEquals(bindingProgress.Account, account))
            throw new InvalidOperationException("消息来源不属于当前微信连接。");
        return ProcessUpdateBatchAsync(bindingProgress, root, cancellation);
    }
    private async Task<bool> ProcessUpdateBatchAsync(BindingProgress progress, JsonElement root, CancellationToken cancellation)
    {
        if (!MayContinueReceiving(progress, cancellation)) return false;
        var account = progress.Account;
        if (!root.TryGetProperty("msgs", out var messages) || messages.ValueKind != JsonValueKind.Array) return true;
        if (requireDurableAssistant && DurableRuntime == null)
            throw new IOException("持久对话记录不可用；收件游标未前移，尚未处理本批消息。");
        bool accepted = false, complete = false;
        try
        {
            foreach (var message in messages.EnumerateArray())
            {
                if (!MayContinueReceiving(progress, cancellation)) return false;
                if (WeChatApi.String(message, "from_user_id") != account.UserId || WeChatApi.Number(message, "message_type") != 1
                    || WeChatApi.String(message, "group_id").Length > 0) continue;
                string recipient = WeChatApi.String(message, "to_user_id");
                if (recipient.Length > 0 && recipient != account.BotId) continue;
                string id = WeChatApi.String(message, "message_id");
                if (id.Length == 0) id = WeChatApi.String(message, "seq");
                if (id.Length == 0) continue; // An unidentifiable message cannot be safely deduplicated.
                id = WeChatText.Hash(id);
                if (account.SeenMessages.Contains(id)) continue;
                fieldProbe.Capture(message);
                WeChatFieldProbe.CompareReference(account, message);
                long.TryParse(WeChatApi.String(message, "create_time_ms"), out long timestamp);
                bool stale = timestamp > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - timestamp > 5 * 60 * 1000;
                string context = WeChatApi.String(message, "context_token");
                bool freshChannelContext = !stale && context.Length > 0
                    && (timestamp == 0 || timestamp >= account.LastContextMessageAt);
                if (freshChannelContext)
                {
                    account.ContextToken = context; account.LastContextMessageAt = timestamp; contextBlocked = false;
                }
                string text = "";
                bool hasUnresolvedReference = false;
                bool hasUnsupportedMedia = false;
                if (message.TryGetProperty("item_list", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    text = string.Join("\n", items.EnumerateArray().Where(i => WeChatApi.Number(i, "type") == 1 && i.TryGetProperty("text_item", out _)).Select(i => WeChatApi.String(i.GetProperty("text_item"), "text")));
                    hasUnresolvedReference = items.EnumerateArray().Any(i =>
                        i.TryGetProperty("ref_msg", out var reference) && reference.ValueKind != JsonValueKind.Null);
                    hasUnsupportedMedia = items.EnumerateArray().Any(i => WeChatApi.Number(i, "type") != 1);
                    // A single voice item with a channel-provided transcript is user input.
                    // Mixed attachments remain unresolved rather than executing a partial caption.
                    if (items.GetArrayLength() == 1 && WeChatApi.Number(items[0], "type") == 3
                        && items[0].TryGetProperty("voice_item", out var voice) && voice.ValueKind == JsonValueKind.Object
                        && voice.TryGetProperty("text", out var transcript) && transcript.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(transcript.GetString()))
                    {
                        text = transcript.GetString()!;
                        hasUnsupportedMedia = false;
                    }
                }
                if (!stale && DurableRuntime != null)
                {
                    string body = text.Length > 128000 ? "[无法处理的微信消息] 文本超出接收上限；内容哈希：" + WeChatText.Hash(text)
                        : hasUnsupportedMedia ? "[无法处理的微信消息] 含未支持的附件或消息项。\n已收到的文字：\n" + text
                        : text.Length == 0 ? "[无法处理的微信消息] 暂未取得可读文字。" : text;
                    DateTimeOffset occurredAt;
                    if (hasUnsupportedMedia && !hasUnresolvedReference && text.Length <= 4000)
                    {
                        string? attachmentId = DurableRuntime.CaptureAttachment(account, id, items);
                        if (attachmentId != null)
                        {
                            body = AssistantInboundAttachments.Marker + attachmentId + "\n附言（仅资料）：" + text;
                            hasUnresolvedReference = true;
                        }
                    }
                    try { occurredAt = timestamp > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp) : DateTimeOffset.UtcNow; }
                    catch (ArgumentOutOfRangeException) { occurredAt = DateTimeOffset.UtcNow; }
                    // No seen/cursor persistence may precede durable inbox acceptance.
                    await DurableRuntime.AcceptAsync(account, id, body, occurredAt, cancellation, freshChannelContext, hasUnresolvedReference,
                        WeChatReferenceReceipt.ReadReferenceId(message));
                    account.SeenMessages.Add(id); account.SeenMessages = account.SeenMessages.TakeLast(256).ToList();
                    accepted = true;
                    if (MayContinueReceiving(progress, cancellation)) ResumeDeferredClaw();
                    // Historical presentation is captured atomically with the actual
                    // inbox message. Rejected originals are never blindly requeued.
                    continue;
                }
                account.SeenMessages.Add(id); account.SeenMessages = account.SeenMessages.TakeLast(256).ToList();
                if (!SaveBinding(progress)) return false;
                if (!MayContinueReceiving(progress, cancellation)) return false;
                ResumeDeferredClaw();
                if (stale) continue;
                if (hasUnsupportedMedia)
                {
                    await SendClawAsync("这条消息包含暂未支持的附件或消息项。请把要做的事和准确目标单独发成文字；这次没有执行工作指令。", "codex-reply-" + id[..40], cancellation);
                    continue;
                }
                text = text.Trim().TrimStart('/').Trim();
                bool localAi = assistant is IWeChatLocalCommands localCommands ? localCommands.CanReplyLocally(text) : assistant != null && ChatGptAssistant.IsLocalCommand(text);
                bool askAi = assistant != null && (assistant.Enabled || text.StartsWith("问 ", StringComparison.Ordinal)) && !WeChatText.IsQuery(text);
                if ((localAi || askAi) && text.Length is > 0 and <= 4000)
                {
                    if (aiWork is { IsCompleted: false })
                    {
                        // Clearing history is safe while awaiting inference; the result
                        // carries a conversation identity and will be discarded.
                        if (localAi && (text == "新对话" || assistant is IAssistantLocalMemory memory && memory.IsMemoryCommand(text))) await ReplyAiAsync(text, id, cancellation);
                        else await SendClawAsync("正在处理上一条 AI 消息，请稍后再试。状态与报告查询仍可使用。", "codex-busy-" + id[..40], cancellation);
                    }
                    else if (localAi || !assistant!.Enabled) await ReplyAiAsync(text, id, cancellation);
                    else
                    {
                        // One generation at a time; polling and completion notifications
                        // continue while the model works. No background task is orphaned.
                        aiWork = ReplyAiAsync(text, id, cancellation);
                    }
                    continue;
                }
                string reply = text.Length == 0 || text.Length > 512 ? WeChatText.Help : WeChatText.Reply(text, snapshot(), settings.WeChat.IncludeSummary, DateTimeOffset.UtcNow, reports, settings.WeChat.ReportQueriesEnabled);
                if (reply == WeChatText.Help && assistant != null) reply += "\nAI 问答：发送“问 你的问题”或直接提问（需在助手中登录并开启）。\nAI状态、新对话、回答 2：查看状态、清空上下文、翻页。";
                if (reply.StartsWith(WeChatText.Help, StringComparison.Ordinal) && assistant is IAssistantLocalMemory)
                    reply += "\n长期记忆：记住：内容 · 查看记忆 · 修改记忆 编号 新内容 · 删除记忆 编号。重启后保留，记忆帮助查看用法。\n查看规则：读取 AGENTS.md；在 AI 助手 → 规则与长期记忆中编辑。";
                if (reply.StartsWith(WeChatText.Help, StringComparison.Ordinal) && assistant is TaskControlAssistant)
                    reply += "\n开启任务接续后：直接发“让 host-a 上的任务继续测试”等自然语言，支持本机与已配置远程服务器；任务不明确时先选择。\n“汇报 P0 当前进展”直接读取目标公开记录，运行中也可查，无需另选聊天代问。\n任务列表、当前任务、取消接续：选择目标、查看关联、取消待发指令。";
                try { await SendClawAsync(reply, "codex-reply-" + id[..40], cancellation); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception ex) { ClawStatus = WeChatApi.Explain(ex); Changed?.Invoke(); }
            }
            complete = true;
            return true;
        }
        finally
        {
            if (accepted)
            {
                // Publish seen IDs and a completed batch's cursor in one save.
                // A stopped partial batch keeps only its committed seen IDs.
                string cursor = complete ? WeChatApi.String(root, "get_updates_buf") : "";
                if (cursor.Length > 0) account.Cursor = cursor;
                SaveBinding(progress);
            }
        }
    }
    private async Task ReplyAiAsync(string text, string id, CancellationToken cancellation)
    {
        string reply;
        try { reply = assistant is TaskControlAssistant control
            ? await control.ReplyMessageAsync(text, snapshot(), id, cancellation)
            : await assistant!.ReplyAsync(text, snapshot(), cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            if (assistant is ChatGptAssistant ai) ai.ReportError(ex, "wechat_message");
            if (assistant is TaskControlAssistant control) control.ReportError(ex);
            reply = ChatGptAssistant.Explain(ex);
        }
        if (cancellation.IsCancellationRequested) return;
        var context = assistant as IWeChatReplyContext; long revision = context?.ReplyRevision ?? 0;
        bool StillCurrent() => !cancellation.IsCancellationRequested && (context == null || context.ReplyRevision == revision);
        try
        {
            var parts = WeChatReports.Pages(reply);
            for (int index = 0; index < parts.Count && StillCurrent(); index++)
                await SendClawAsync(parts[index], "codex-ai-" + id[..40] + (index == 0 ? "" : "-" + index), cancellation, StillCurrent);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { ClawStatus = WeChatApi.Explain(ex); Changed?.Invoke(); }
    }
    private bool SaveBinding(BindingProgress progress)
    {
        // Stop/cancellation alone does not revoke a committed inbox's progress.
        // Only this exact activation may update the binding it last published;
        // an explicit replacement (even the same identity) must never be lost.
        if (!BindingProgressCurrent(progress)) return false;
        settings.WeChat.WriteBinding(progress.Account);
        progress.ExpectedCipher = settings.WeChat.ProtectedBinding;
        persist();
        return true;
    }
    internal string ReadOnlyReply(string text) => WeChatText.Reply(text, snapshot(), settings.WeChat.IncludeSummary,
        DateTimeOffset.UtcNow, reports, settings.WeChat.ReportQueriesEnabled);
    internal async Task<DurableDeliveryResult> SendDurableAsync(DurableDeliveryRequest request, CancellationToken ct)
    {
        DurableDeliveryResult Observed(DurableDeliveryResult result)
        {
            if (request.Message.Kind == OutboxKind.Notification)
                RecordNotificationAttempt(new(request.Message.NotificationEventId?.ToString("N") ?? request.Message.Id.ToString("N"),
                    "clawbot", result.Stage == OutboxStage.Accepted ? true : result.Stage == OutboxStage.Rejected ? false : null,
                    result.Receipt ?? "尚未确认通知结果。"));
            return result;
        }
        if (binding == null || DurableRuntime == null || request.Message.Identity != DurableRuntime.Identity
            || !DurableRuntime.MatchesBinding(binding) || !DurableRuntime.IsCurrentBinding(request.Message.Identity)
            || !request.StillCurrent())
            return Observed(new(OutboxStage.Rejected, "绑定或对话已变化，未发送。", OutboxRejectionReason.BindingUnavailable));
        if (contextBlocked || binding.ContextToken.Length == 0)
            return Observed(new(OutboxStage.Rejected, "微信会话上下文不可用，未发送。", OutboxRejectionReason.ContextUnavailable));
        try
        {
            string? channelMessageId = null;
            bool? accepted = await SendClawAsync(request.Message.Body, "assistant-outbox-" + request.Message.Id.ToString("N"), ct,
                () => request.StillCurrent() && binding != null && DurableRuntime.MatchesBinding(binding)
                    && DurableRuntime.IsCurrentBinding(request.Message.Identity), preserveText: true, observeMessageId: id => channelMessageId = id);
            return Observed(accepted switch
            {
                true => new(OutboxStage.Accepted, WeChatReferenceReceipt.Encode(channelMessageId, "微信接口已受理，手机送达尚未确认。")),
                false => new(OutboxStage.Rejected, "微信接口明确拒绝。", OutboxRejectionReason.OtherRejected),
                _ => new(OutboxStage.Unknown, WeChatReferenceReceipt.Encode(channelMessageId, "微信接口没有返回确定受理结果。"))
            });
        }
        catch (ContextUnavailableException)
        { return Observed(new(OutboxStage.Rejected, "微信会话上下文不可用，未发送。", OutboxRejectionReason.ContextUnavailable)); }
        catch (DeliveryNoLongerCurrentException)
        { return Observed(new(OutboxStage.Rejected, "发送等待期间授权条件已变化，尚未调用渠道。", OutboxRejectionReason.BindingUnavailable)); }
        catch (ClawTextRejectedException ex)
        { return Observed(new(OutboxStage.Rejected, ex.Message, OutboxRejectionReason.OtherRejected)); }
        catch (WeChatApiException ex) when (ex.Code == -2 && !ex.IsHttpStatus)
        { return Observed(new(OutboxStage.Rejected, "微信接口拒绝了当前发送上下文。", OutboxRejectionReason.ContextExpired)); }
        catch (WeChatApiException ex) when (ex.Code is -14 or 401 or 403)
        { return Observed(new(OutboxStage.Rejected, "微信接口拒绝了当前绑定。", OutboxRejectionReason.BindingUnavailable)); }
        catch (Exception) { return Observed(new(OutboxStage.Unknown, "发送开始后没有取得确定结果，不自动重发。")); }
    }
    internal async Task SendControlReceiptAsync(ControlReceipt receipt, CancellationToken cancellation)
    {
        if (!settings.WeChat.ClawBotEnabled || binding == null) return;
        if (DurableRuntime != null)
        {
            await DurableRuntime.EnqueueControlReceiptAsync(receipt, cancellation);
            return;
        }
        try { await SendClawAsync(receipt.Body, "codex-control-" + receipt.Id[..32], cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { ClawStatus = WeChatApi.Explain(ex); Changed?.Invoke(); }
    }
    private async Task<bool> StopAsync()
    {
        if (session == null && receiverLease == null) return true;
        stopRequested = true;
        session?.Cancel(); queue?.Writer.TryComplete();
        stopping ??= FinishStopAsync();
        try { await stopping.WaitAsync(StopTimeout); return true; }
        catch (TimeoutException)
        {
            // Cancellation is a request, not proof that a long poll has ended.
            // Retain the lease and refuse replacement until FinishStopAsync ends.
            ConnectionState = ChannelConnectionState.Degraded;
            ClawStatus = "旧微信连接仍在停止，接收权暂时保留；尚未启动新的接收者。";
            Changed?.Invoke();
            return false;
        }
    }

    private async Task FinishStopAsync()
    {
        if (DurableRuntime != null) await DurableRuntime.StopSessionAsync();
        try { await Task.WhenAll(sender ?? Task.CompletedTask, receiver ?? Task.CompletedTask, durableNotifications ?? Task.CompletedTask); }
        catch { /* Faulted tasks are terminal; both tasks were awaited by WhenAll. */ }
        try { if (aiWork != null) await aiWork; }
        catch { /* A failed generation must not prevent receiver cleanup. */ }
        // Explicit tests and control receipts can send outside the two loops.
        // Their session token is cancelled above; drain those operations as well.
        await clawSendGate.WaitAsync(); clawSendGate.Release();
        await pushSendGate.WaitAsync(); pushSendGate.Release();
        await typingGate.WaitAsync(); typingGate.Release();
        aiWork = null;
        session?.Dispose(); session = null; sender = receiver = durableNotifications = null; queue = null;
        binding = null; bindingProgress = null; signature = "";
        deferredClaw.Clear(); preparedClaw.Clear();
        if (receiverLease != null) { await receiverLease.DisposeAsync(); receiverLease = null; }
        ConnectionState = ChannelConnectionState.Disconnected;
    }
    public async ValueTask DisposeAsync()
    {
        await configureGate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            if (await StopAsync()) api.Dispose();
            else _ = DisposeApiAfterStopAsync();
        }
        finally { configureGate.Release(); }
    }

    private async Task DisposeApiAfterStopAsync()
    {
        try { if (stopping != null) await stopping; }
        finally { api.Dispose(); }
    }
}
