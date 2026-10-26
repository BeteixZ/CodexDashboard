using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;
internal sealed record SelectedArtifact(string Id, string Name, int Length, string Sha256, TaskReference? Task = null);
internal sealed record ArtifactSendSummary(int Requests, OperationStage? LatestStage, Guid? LatestOperationId, int UnknownResults);
internal sealed record ArtifactPage(SelectedArtifact[] Items, int Page, int TotalPages, int Total, IReadOnlyDictionary<string, ArtifactSendSummary> Sends);
internal sealed class AssistantArtifactSelection(AssistantArtifactStore store, DurableConversationLedger ledger,
    Func<ConversationIdentity, bool> currentBinding, Func<TaskReference, bool>? taskCurrent = null)
{
    internal async Task RememberChoicesAsync(ConversationIdentity identity, SelectedArtifact[] items, CancellationToken ct)
    {
        if (!currentBinding(identity)) throw new OperationCanceledException(ct);
        await ledger.AppendContextAsync(identity, ConversationSource.UntrustedContent,
            JsonSerializer.Serialize(new { kind = "file_send_choices", ids = items.Select(a => a.Id).ToArray(), authorization = false }),
            DateTimeOffset.UtcNow, cancellationToken: ct);
    }
    internal async Task<string?> ResolveChoiceAsync(ConversationIdentity identity, string choice, CancellationToken ct)
    {
        if (!currentBinding(identity) || !int.TryParse(choice, out int number) || number is < 1 or > 5) return null;
        var saved = await ledger.ReadConversationAsync(identity, ct);
        foreach (var entry in saved.Events.Where(e => e.Source == ConversationSource.UntrustedContent && e.SourceMessageId == null && e.RelatedOutboxId == null).Reverse())
        {
            try
            {
                using var json = JsonDocument.Parse(entry.Body);
                if (!json.RootElement.TryGetProperty("kind", out var kind) || kind.GetString() != "file_send_choices") continue;
                var ids = json.RootElement.GetProperty("ids");
                return number <= ids.GetArrayLength() ? ids[number - 1].GetString() : null;
            }
            catch (JsonException) { }
        }
        return null;
    }
    internal async Task<ArtifactPage> ListAsync(ConversationIdentity identity, CancellationToken ct, int page = 1, string? query = null)
    {
        if (!currentBinding(identity)) throw new OperationCanceledException(ct);
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        var saved = await ledger.ReadConversationAsync(identity, ct);
        var result = new List<SelectedArtifact>();
        foreach (var entry in saved.Events.Where(e => e.Source == ConversationSource.UntrustedContent && e.SourceMessageId == null && e.RelatedOutboxId == null).Reverse())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var json = JsonDocument.Parse(entry.Body);
                if (!json.RootElement.TryGetProperty("kind", out var kind) || kind.GetString() != "selected_file_artifact") continue;
                var item = json.RootElement.GetProperty("artifact").Deserialize<SelectedArtifact>();
                if (item == null || !string.IsNullOrEmpty(query) && !item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var actual = store.Load(identity, item.Id);
                if (actual.ContentHash == item.Sha256 && actual.DisplayName == item.Name && actual.Content.Length == item.Length && actual.Task == item.Task) result.Add(item);
            }
            catch (Exception error) when (error is JsonException or IOException or WeChatMediaException) { }

        }
        if (!currentBinding(identity)) throw new OperationCanceledException(ct);
        int pages = Math.Max(1, (result.Count + 4) / 5);
        var items = result.Skip((page - 1) * 5).Take(5).ToArray();
        var sends = items.ToDictionary(item => item.Id, item =>
        {
            var operations = saved.Operations.Where(o => o.Action == "send_file" && o.TargetKey == item.Id).OrderByDescending(o => o.CreatedAt).ToArray();
            return new ArtifactSendSummary(operations.Length, operations.FirstOrDefault()?.Stage,
                operations.FirstOrDefault()?.Id, operations.Count(o => o.Stage == OperationStage.Unknown));
        });
        return new(items, page, pages, result.Count, sends);
    }
    internal async Task<SelectedArtifact> ImportAsync(ConversationIdentity identity, string path, CancellationToken ct, TaskReference? task = null)
    {
        if (!currentBinding(identity)) throw new InvalidOperationException("当前微信绑定已经变化，请重新选择文件。");
        if (task != null && taskCurrent?.Invoke(task) != true) throw new InvalidOperationException("来源任务已变化，请重新选择。");
        byte[] bytes;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            if (input.Length > AssistantAttachmentStore.MaximumBytes) throw new InvalidOperationException("当前支持不超过 8 MiB 的文件。");
            bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, ct);
        }
        ct.ThrowIfCancellationRequested();
        if (!currentBinding(identity)) throw new InvalidOperationException("当前微信绑定已经变化，文件没有加入当前会话。");
        if (task != null && taskCurrent?.Invoke(task) != true) throw new InvalidOperationException("来源任务已变化，请重新选择。");
        string name = Path.GetFileName(path);
        string id = store.Save(identity, name, bytes, task);
        var saved = store.Load(identity, id);
        var selected = new SelectedArtifact(id, name, bytes.Length, saved.ContentHash, task);
        await ledger.AppendContextAsync(identity, ConversationSource.UntrustedContent,
            JsonSerializer.Serialize(new { kind = "selected_file_artifact", artifact = selected, authorization = false }),
            DateTimeOffset.UtcNow, task: task, cancellationToken: ct);
        return selected;
    }
}
