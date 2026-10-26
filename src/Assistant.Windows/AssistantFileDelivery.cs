using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;
internal sealed class AssistantArtifactStore(string directory)
{
    private readonly AssistantAttachmentStore store = new(directory);
    internal string Save(ConversationIdentity identity, string name, byte[] content, TaskReference? task = null)
        => store.Save(identity, "outbound-artifact/" + Guid.NewGuid().ToString("N"), 0, name, 4, content, task);
    internal AssistantAttachment Load(ConversationIdentity identity, string id)
    {
        var artifact = store.Load(identity, id);
        if (!artifact.SourceMessageId.StartsWith("outbound-artifact/", StringComparison.Ordinal))
            throw new WeChatMediaException("not_an_outbound_artifact");
        return artifact;
    }
}
internal sealed record FileSendReceipt(bool? Accepted, string? MessageId);
// No polling or automatic retry: the host explicitly dispatches a current authorized operation.
internal sealed class AssistantFileDelivery(DurableConversationLedger ledger, AssistantArtifactStore artifacts,
    Func<ConversationIdentity, bool> currentBinding,
    Func<AssistantAttachment, CancellationToken, Task<WeChatUploadedFile>> upload,
    Func<WeChatUploadedFile, string, string, CancellationToken, Task<FileSendReceipt>> send)
{
    internal async Task<OperationRecord> QueueAsync(InboxMessage message, string artifactId, CancellationToken ct)
    {
        var saved = await ledger.ReadConversationAsync(message.Identity, ct);
        var actual = saved.Inbox.Single(m => m.SourceMessageId == message.SourceMessageId);
        if (actual.Body != message.Body || actual.Revision != message.Revision) throw new WeChatMediaException("file_source_changed");
        string command = message.Body.Trim();
        bool named = command == "发送文件 " + artifactId;
        if (!named && command.StartsWith("发送文件 ", StringComparison.Ordinal))
            named = await new AssistantArtifactSelection(artifacts, ledger, currentBinding)
                .ResolveChoiceAsync(message.Identity, command[5..].Trim(), ct) == artifactId;
        if (!currentBinding(message.Identity) || !named)
            throw new WeChatMediaException("explicit_file_target_required");
        var artifact = artifacts.Load(message.Identity, artifactId);
        string arguments = JsonSerializer.Serialize(new { artifactId, artifact.ContentHash, artifact.DisplayName });
        var grant = await ledger.AuthorizeCurrentUserRequestAsync(message.Identity, message.SourceMessageId,
            "send_file", artifactId, arguments, message.Body.Trim(), artifact.Task, ct);
        return await ledger.QueueOperationAsync(message.Identity, grant.Id, "send-file/" + message.SourceMessageId, ct);
    }
    internal async Task<OperationRecord> DispatchAsync(ConversationIdentity identity, Guid operationId, CancellationToken ct)
    {
        var snapshot = await ledger.ReadConversationAsync(identity, ct);
        var operation = snapshot.Operations.Single(o => o.Id == operationId);
        if (operation.Action != "send_file") throw new WeChatMediaException("not_a_file_operation");
        if (operation.Stage != OperationStage.Queued) return operation;
        if (!currentBinding(identity)) throw new WeChatMediaException("file_binding_changed");
        var artifact = artifacts.Load(identity, operation.TargetKey);
        string expected = JsonSerializer.Serialize(new { artifactId = operation.TargetKey, artifact.ContentHash, artifact.DisplayName });
        if (operation.ArgumentsJson != expected || operation.Task != artifact.Task) throw new WeChatMediaException("file_target_changed");
        operation = await ledger.BeginOperationDispatchAsync(identity, operation.Id, ct);
        bool sendStarted = false;
        OperationStage stage;
        string evidence;
        try
        {
            var uploaded = await upload(artifact, ct);
            ct.ThrowIfCancellationRequested();
            var latest = await ledger.ReadConversationAsync(identity, ct);
            if (!currentBinding(identity) || latest.Revision != operation.Revision)
                throw new WeChatMediaException("file_request_changed_before_send");
            sendStarted = true;
            var receipt = await send(uploaded, artifact.DisplayName, operation.IdempotencyKey, ct);
            stage = receipt.Accepted == true ? OperationStage.Accepted : OperationStage.Unknown;
            evidence = receipt.Accepted == true ? "file_api_accepted_not_phone_delivery" : "file_send_outcome_unknown_no_retry";
            if (!string.IsNullOrEmpty(receipt.MessageId)) evidence += ";message_id_sha256=" + WeChatText.Hash(receipt.MessageId);
        }
        catch (FileSendNotStartedException)
        { stage = OperationStage.Failed; evidence = "file_not_sent_before_api_dispatch"; }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException
            or WeChatApiException or WeChatMediaException or CryptographicException or JsonException)
        {
            stage = sendStarted ? OperationStage.Unknown : OperationStage.Failed;
            evidence = sendStarted ? "file_send_outcome_unknown_no_retry" : "file_not_sent_preparation_failed";
        }
        return await ledger.RecordOperationObservationAsync(identity, operation.Id,
            new OperationObservation(stage, evidence, operation.TargetKey, operation.Task), CancellationToken.None);
    }
}
