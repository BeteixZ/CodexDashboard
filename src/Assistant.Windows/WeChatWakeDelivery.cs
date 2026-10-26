using System;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal sealed record WakeSendAttempt(ChannelWakeLease Lease, WakeDeliveryOutcome Outcome,
    string Evidence, string? PlatformReceipt = null);

internal sealed partial class WeChatService
{
    internal const string WakeTitle = "有待查看的消息";
    internal const string WakeBody = ChannelWakeRecord.ShortBody;
    private string? wakePresentationError;
    internal bool WakeRateWaitActive { get; private set; }

    private void ShowWakeStatus(string value)
    {
        PushStatus = value;
        try { Changed?.Invoke(); }
        catch { wakePresentationError = "wake_status_observer_failed"; }
    }

    // Called on the dispatcher. Configuration describes a route, never its
    // credential. An unreadable quiet policy disables this secondary channel.
    internal WakeChannelConfiguration ReadWakeConfiguration()
    {
        var options = settings.WeChat;
        bool valid = options.TryReadWakeQuietHours(out var quiet, out _);
        bool enabled = valid && options.PushPlusEnabled && options.ClawBotEnabled
            && !string.IsNullOrEmpty(options.ProtectedPushPlusToken) && options.PushPlusWakeRouteId != Guid.Empty;
        return new(options.PushPlusWakeRouteId, enabled,
            valid && quiet != null ? quiet.TimeZoneId : TimeZoneInfo.Local.Id,
            valid ? quiet?.Start : null, valid ? quiet?.End : null);
    }

    private bool WakeRouteCurrent(ConversationIdentity identity, WakeChannelConfiguration configuration)
        => !disposed && !stopRequested && session?.IsCancellationRequested == false
            && binding != null && DurableRuntime?.Identity == identity && DurableRuntime.MatchesBinding(binding)
            && DurableRuntime.IsCurrentBinding(identity)
            && configuration.Enabled && ReadWakeConfiguration() == configuration;

    // No detached timeout wrapper: the owning runtime waits for this actual
    // task before releasing its durable send lease or old receiver identity.
    internal async Task<WakeSendAttempt?> SendWakeAsync(ConversationIdentity identity,
        WakeChannelConfiguration configuration, Func<CancellationToken, Task<ChannelWakeLease>> beginAfterGate,
        CancellationToken cancellation)
    {
        if (!WakeRouteCurrent(identity, configuration)) return null;
        string protectedRoute = settings.WeChat.ProtectedPushPlusToken;
        using var linked = session == null ? CancellationTokenSource.CreateLinkedTokenSource(cancellation)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellation, session.Token);
        cancellation = linked.Token;
        await pushSendGate.WaitAsync(cancellation);
        try
        {
            var delay = TimeSpan.FromSeconds(13) - (DateTimeOffset.UtcNow - lastPushAt);
            if (delay > TimeSpan.Zero)
            {
                WakeRateWaitActive = true;
                try { await Task.Delay(delay, cancellation); }
                finally { WakeRateWaitActive = false; }
            }
            cancellation.ThrowIfCancellationRequested();
            if (!WakeRouteCurrent(identity, configuration)
                || settings.WeChat.ProtectedPushPlusToken != protectedRoute) return null;
            string token;
            try { token = settings.WeChat.ReadPushPlusToken(); }
            catch
            {
                ShowWakeStatus("短唤醒令牌无法读取；待补报正文已保留。");
                return null;
            }
            if (string.IsNullOrWhiteSpace(token)) return null;
            // The ledger rechecks fresh input, source state, quiet hours and
            // quota only after the real shared rate limit wait has ended.
            var lease = await beginAfterGate(cancellation);
            bool requestStarted = false;
            try
            {
                if (cancellation.IsCancellationRequested || !WakeRouteCurrent(identity, configuration)
                    || settings.WeChat.ProtectedPushPlusToken != protectedRoute)
                    return new(lease, WakeDeliveryOutcome.NotStarted,
                        "发送预留后连接已变化，尚未调用渠道；本次预留不自动重试。");
                lastPushAt = DateTimeOffset.UtcNow;
                requestStarted = true;
                string receipt = await api.SendPushPlusAsync(token, WakeTitle, WakeBody, cancellation);
                ShowWakeStatus("pushplus 已受理短提示；手机送达未知，正文仍待补报。");
                return new(lease, WakeDeliveryOutcome.Accepted, "pushplus 接口返回受理流水号，手机送达未核实。", receipt);
            }
            catch (WeChatApiException error) when (!error.IsHttpStatus)
            {
                ShowWakeStatus("pushplus 明确拒绝短提示；正文仍待补报。");
                return new(lease, WakeDeliveryOutcome.Rejected, "pushplus 返回明确业务拒绝，未取得受理流水号。");
            }
            catch
            {
                ShowWakeStatus("短提示的发送结果未知，不自动重发；正文仍保留。");
                return new(lease, requestStarted ? WakeDeliveryOutcome.Unknown : WakeDeliveryOutcome.NotStarted,
                    requestStarted ? "渠道调用开始后未取得确定结果，不自动重发。" : "尚未调用渠道，不自动重试此预留。");
            }
        }
        finally { pushSendGate.Release(); }
    }
}
