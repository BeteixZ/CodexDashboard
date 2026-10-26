using System;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;
internal sealed class FileSendNotStartedException : Exception;
internal sealed partial class WeChatService
{
    internal Func<WeChatMediaClient> FileMediaFactory { get; set; } = () => new WeChatMediaClient();
    internal async Task<WeChatUploadedFile> UploadArtifactAsync(AssistantAttachment artifact, CancellationToken ct)
    {
        if (disposed || stopRequested || session == null || binding == null || contextBlocked || binding.ContextToken.Length == 0)
            throw new WeChatMediaException("file_channel_unavailable");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        var captured = new ClawBinding { BaseUrl = binding.BaseUrl, BotId = binding.BotId, UserId = binding.UserId, Token = binding.Token, ContextToken = binding.ContextToken };
        using var cdn = FileMediaFactory();
        return await api.UploadFileAsync(captured, artifact.Content, cdn, linked.Token);
    }
    internal async Task<FileSendReceipt> SendArtifactAsync(WeChatUploadedFile file, string name, string clientId,
        ConversationIdentity identity, CancellationToken ct, Func<bool> stillCurrent)
    {
        if (disposed || stopRequested || session == null) throw new FileSendNotStartedException();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Token);
        try { await clawSendGate.WaitAsync(linked.Token); }
        catch (OperationCanceledException) { throw new FileSendNotStartedException(); }
        bool requestStarted = false;
        try
        {
            if (binding == null || contextBlocked || binding.ContextToken.Length == 0 || DurableRuntime?.IsCurrentBinding(identity) != true)
                throw new WeChatMediaException("file_channel_unavailable");
            var delay = TimeSpan.FromSeconds(1) - (DateTimeOffset.UtcNow - lastClawAt);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, linked.Token);
            if (contextBlocked || !stillCurrent() || DurableRuntime?.IsCurrentBinding(identity) != true) throw new WeChatMediaException("file_binding_changed");
            lastClawAt = DateTimeOffset.UtcNow;
            string? messageId = null;
            string sentContext = binding.ContextToken;
            try
            {
                requestStarted = true;
                bool? accepted = await api.SendUploadedFileAsync(binding, file, name, clientId, linked.Token, id => messageId = id);
                return new(accepted, messageId);
            }
            catch (WeChatApiException error) when (error.Code == -2)
            {
                contextBlocked = binding.ContextToken == sentContext;
                if (contextBlocked) ConnectionState = Codex.Protocol.ConnectionState.Degraded;
                throw;
            }
        }
        catch (Exception error) when (!requestStarted && error is OperationCanceledException or WeChatMediaException)
        { throw new FileSendNotStartedException(); }
        finally { clawSendGate.Release(); }
    }
}
