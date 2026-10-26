using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;
internal static class AssistantFileDeliveryTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--file-delivery-test", var root]) return false;
        Directory.CreateDirectory(root);
        var checks = new Dictionary<string, bool>();
        void Check(string key, bool ok) { checks[key] = ok; if (!ok) throw new InvalidOperationException(key); }
        try
        {
            var identity = new ConversationIdentity("synthetic-file-user", "synthetic-binding", "synthetic-conversation");
            var artifacts = new AssistantArtifactStore(root);
            byte[] body = Encoding.UTF8.GetBytes("Immutable synthetic artifact");
            string artifactId = artifacts.Save(identity, "result.txt", body);
            body[0] = 0;
            Check("artifact_is_a_persisted_byte_snapshot", artifacts.Load(identity, artifactId).Content[0] == (byte)'I');
            Guid unknownId, crashId;
            string selectedId = "";
            int uploads = 0, sends = 0;
            var file = new WeChatUploadedFile { BotId = "synthetic", UserId = "synthetic", DownloadParameter = "private", Base64Key = "private", Length = body.Length };
            await using (var ledger = await AssistantLedgerStorage.OpenAsync(root, false))
            {
                bool selectionCurrent = true;
                var selection = new AssistantArtifactSelection(artifacts, ledger, _ => selectionCurrent);
                string selectedPath = Path.Combine(root, "selected-local.txt");
                await File.WriteAllTextAsync(selectedPath, "Selected content");
                var selected = await selection.ImportAsync(identity, selectedPath, default);
                selectedId = selected.Id;
                await File.WriteAllTextAsync(selectedPath, "Changed after selection");
                Check("selected_file_is_immutable_after_source_changes", Encoding.UTF8.GetString(artifacts.Load(identity, selected.Id).Content) == "Selected content");
                var context = (await ledger.ReadConversationAsync(identity)).Events.Single();
                Check("selection_context_has_no_path_or_authorization", context.Source == ConversationSource.UntrustedContent && !context.Body.Contains(selectedPath)
                    && context.Body.Contains(selected.Id) && (await ledger.ReadConversationAsync(identity)).Operations.IsEmpty);
                for (int index = 0; index < 11; index++)
                {
                    string extraPath = Path.Combine(root, "page-file-" + index + ".txt");
                    await File.WriteAllTextAsync(extraPath, "Synthetic page " + index);
                    await selection.ImportAsync(identity, extraPath, default);
                }
                var third = await selection.ListAsync(identity, default, 3);
                Check("prepared_file_pagination_reaches_oldest_file", third.Total == 12 && third.TotalPages == 3 && third.Items.Any(a => a.Id == selected.Id));
                var searched = await selection.ListAsync(identity, default, 1, "selected-local");
                Check("filename_search_includes_files_beyond_first_page", searched.Total == 1 && searched.Items.Single().Id == selected.Id);
                selectionCurrent = false;
                bool changedBinding = false;
                try { await selection.ImportAsync(identity, selectedPath, default); } catch (InvalidOperationException) { changedBinding = true; }
                Check("selection_rejects_changed_binding", changedBinding && (await ledger.ReadConversationAsync(identity)).Events.Length == 12);
                selectionCurrent = true;
                await ledger.ReceiveAsync(identity, "quoted-json-scalar", "42", MessageTrust.UntrustedContent, DateTimeOffset.UtcNow);
                await ledger.ReceiveAsync(identity, "quoted-file-record", context.Body, MessageTrust.UntrustedContent, DateTimeOffset.UtcNow);
                var afterQuotes = await selection.ListAsync(identity, default);
                Check("external_json_and_copied_records_do_not_break_or_duplicate_file_list", afterQuotes.Total == 12 && afterQuotes.Items.Length == 5);
                bool changeDuringUpload = false, timeout = false;
                var delivery = new AssistantFileDelivery(ledger, artifacts, _ => true,
                    async (artifact, ct) => { uploads++; if (changeDuringUpload) await ledger.ReceiveAsync(identity, "changed", "先别发送", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow); return file; },
                    (uploaded, name, clientId, ct) => { sends++; if (timeout) throw new OperationCanceledException(); return Task.FromResult(new FileSendReceipt(true, "1234567890123456789")); });
                async Task<OperationRecord> Queue(string source)
                {
                    var input = await ledger.ReceiveAsync(identity, source, "发送文件 " + artifactId, MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
                    return await delivery.QueueAsync(input.Message, artifactId, default);
                }
                var op = await Queue("accepted");
                var accepted = await delivery.DispatchAsync(identity, op.Id, default);
                Check("accepted_does_not_claim_phone_delivery", accepted.Stage == OperationStage.Accepted && accepted.Receipt!.Contains("not_phone_delivery") && !accepted.Receipt.Contains("1234567890123456789"));
                await delivery.DispatchAsync(identity, op.Id, default);
                Check("repeated_dispatch_does_not_repeat_network", uploads == 1 && sends == 1);
                var blockedDelivery = new AssistantFileDelivery(ledger, artifacts, _ => true,
                    (_, _) => Task.FromResult(file), (_, _, _, _) => throw new FileSendNotStartedException());
                var blockedInput = await ledger.ReceiveAsync(identity, "not-dispatched", "发送文件 " + artifactId, MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
                var blocked = await blockedDelivery.QueueAsync(blockedInput.Message, artifactId, default);
                Check("channel_pre_dispatch_rejection_records_not_sent", (await blockedDelivery.DispatchAsync(identity, blocked.Id, default)).Stage == OperationStage.Failed);
                timeout = true;
                op = await Queue("unknown"); unknownId = op.Id;
                var unknown = await delivery.DispatchAsync(identity, op.Id, default);
                Check("send_timeout_is_unknown", unknown.Stage == OperationStage.Unknown);
                await delivery.DispatchAsync(identity, op.Id, default);
                Check("unknown_is_not_retried", uploads == 2 && sends == 2);
                timeout = false; changeDuringUpload = true;
                op = await Queue("superseded");
                var changed = await delivery.DispatchAsync(identity, op.Id, default);
                Check("new_input_during_upload_prevents_send", changed.Stage == OperationStage.Failed && uploads == 3 && sends == 2);
                changeDuringUpload = false;
                var negative = await ledger.ReceiveAsync(identity, "negative", "不要发送文件 " + artifactId, MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
                bool denied = false;
                try { await delivery.QueueAsync(negative.Message with { Body = "发送文件 " + artifactId }, artifactId, default); } catch (WeChatMediaException) { denied = true; }
                Check("fabricated_positive_excerpt_cannot_authorize", denied);
                op = await Queue("crashed"); crashId = op.Id;
                await ledger.BeginOperationDispatchAsync(identity, crashId);
                foreach (bool uncertain in new[] { true, false })
                {
                    timeout = uncertain;
                    var request = await ledger.ReceiveAsync(identity, "selected-status-" + uncertain, "发送文件 " + selected.Id, MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
                    var selectedOperation = await delivery.QueueAsync(request.Message, selected.Id, default);
                    await delivery.DispatchAsync(identity, selectedOperation.Id, default);
                }
                var withHistory = await selection.ListAsync(identity, default, query: "selected-local");
                Check("later_acceptance_keeps_previous_unknown_send_visible", withHistory.Sends[selected.Id] is { Requests: 2, LatestStage: OperationStage.Accepted, UnknownResults: 1 });
            }
            await using (var reopened = await AssistantLedgerStorage.OpenAsync(root, false))
            {
                var delivery = new AssistantFileDelivery(reopened, artifacts, _ => true,
                    (_, _) => throw new InvalidOperationException("Must not upload after uncertain dispatch"),
                    (_, _, _, _) => throw new InvalidOperationException("Must not resend"));
                Check("unknown_survives_reopen_without_retry", (await delivery.DispatchAsync(identity, unknownId, default)).Stage == OperationStage.Unknown);
                Check("crashed_dispatch_recovers_without_retry", (await delivery.DispatchAsync(identity, crashId, default)).Stage == OperationStage.Unknown);
                var selection = new AssistantArtifactSelection(artifacts, reopened, _ => true);
                var recoveredList = await selection.ListAsync(identity, default, query: "selected-local");
                Check("file_status_history_survives_reopen", recoveredList.Sends[selectedId] is { Requests: 2, LatestStage: OperationStage.Accepted, UnknownResults: 1 });
            }
            File.WriteAllText(Path.Combine(root, "checks.json"), JsonSerializer.Serialize(checks)); exit(0);
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(root, "failure.txt"), error.ToString()); exit(1); }
        return true;
    }
}
