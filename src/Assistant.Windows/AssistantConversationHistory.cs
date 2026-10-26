using System;
using System.Collections.Generic;
using System.Linq;
using Assistant.Core;

namespace CodexDashboard;

internal sealed record AssistantConversationRow(
    string Id, string Speaker, string State, string Body, string Target,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string TimeLabel)
{
    public string Time => CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string Preview
    {
        get
        {
            string value = Body.Replace("\r", " ").Replace("\n", " ");
            if (value.Length <= 160) return value;
            int length = char.IsHighSurrogate(value[159]) ? 159 : 160;
            return value[..length] + "…";
        }
    }
    internal string Detail => Body + "\n\n" + Speaker + " · " + State + "\n"
        + TimeLabel + "：" + CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") + "\n"
        + "记录更新：" + UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") + "\n"
        + "关联：" + Target;
}

internal static class AssistantConversationHistory
{
    internal const int Limit = 200;
    internal static IReadOnlyList<AssistantConversationRow> Read(ConversationSnapshot saved,
        Func<OutboxMessage, string> messageState)
        => ReadPage(saved, messageState, DateTimeOffset.UtcNow).Rows;

    internal static AssistantConversationPage ReadPage(ConversationSnapshot saved,
        Func<OutboxMessage, string> messageState, DateTimeOffset readAt,
        AssistantConversationCursor? before = null, ConversationIdentity? sessionIdentity = null, int limit = Limit, string query = "")
    {
        query = query.Trim();
        var identity = sessionIdentity ?? saved.Identity;
        if (identity != saved.Identity || before != null && (before.Identity != identity
            || before.Query != query || !Guid.TryParseExact(before.BeforeId, "N", out _)))
            throw new InvalidOperationException("history_cursor_identity_or_format_invalid");
        // Sort receipt keys before projecting bodies and states. At most one page
        // of UI rows is materialized; no growing collection of WPF rows is kept.
        var ordered = saved.Inbox.Select(item => new HistoryKey(item.Id, item.ReceivedAt, item, null))
            .Concat(saved.Outbox.Select(item => new HistoryKey(item.Id, item.CreatedAt, null, item)))
            .OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id.ToString("N"), StringComparer.Ordinal)
            .ToArray();
        if (query.Length != 0) ordered = ordered.Where(item =>
        {
            var row = item.Incoming != null ? Incoming(item.Incoming) : Outgoing(item.Outgoing!);
            return new[] { row.Id, row.Body, row.Target, row.Speaker, row.State }
                .Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        int offset = 0;
        if (before != null)
        {
            int anchor = Array.FindIndex(ordered, item => item.Id.ToString("N") == before.BeforeId
                && item.CreatedAt == before.BeforeCreatedAt);
            if (anchor < 0) throw new InvalidOperationException("history_cursor_anchor_unavailable");
            offset = anchor + 1;
        }
        static string Target(TaskReference? task) => task == null ? "当前对话"
            : task.HostId + " / " + task.ThreadId + (task.TurnId == null ? "" : " / " + task.TurnId);
        // Inbox/Outbox are the immutable receipt identities. Tool observations
        // and notification preparation events are not additional chat messages.
        AssistantConversationRow Incoming(InboxMessage item) => new(item.Id.ToString("N"),
                item.Trust == MessageTrust.AuthenticatedUser ? "你" : "收到的内容",
                item.Stage switch
                {
                    InboxStage.Received => "已持久接收",
                    InboxStage.Grouped => "已与补充内容合并",
                    InboxStage.Processing => "正在处理",
                    InboxStage.Prepared => "回复已准备",
                    InboxStage.HandedToOutbox => "回复已交给发送记录",
                    InboxStage.Cancelled => "处理已取消",
                    InboxStage.Superseded => "已有新的要求，以最新要求为准",
                    _ => "处理未完成"
                }, item.Body, Target(item.Task), item.ReceivedAt, item.ReceivedAt, "接收时间");
        AssistantConversationRow Outgoing(OutboxMessage item) => new(item.Id.ToString("N"),
                item.Kind switch { OutboxKind.Notification => "助手通知", OutboxKind.DeferredReply => "助手补答", _ => "助手回复" },
                messageState(item), item.Body,
                Target(item.NotificationEventId is { } eventId
                    ? saved.Events.FirstOrDefault(entry => entry.Id == eventId)?.Task
                    : item.GenerationId is { } generationId
                        ? saved.Generations.FirstOrDefault(entry => entry.Id == generationId)?.Task : null),
                item.CreatedAt, item.UpdatedAt, "准备时间");
        var selected = ordered.Skip(offset).Take(limit).ToArray();
        var rows = selected.Select(item => item.Incoming != null ? Incoming(item.Incoming) : Outgoing(item.Outgoing!)).ToArray();
        var older = offset + rows.Length < ordered.Length && selected.Length > 0
            ? new AssistantConversationCursor(identity, selected[^1].CreatedAt, selected[^1].Id.ToString("N"), query) : null;
        return new(identity, readAt, rows, ordered.Length, rows.Length == 0 ? 0 : offset + 1,
            rows.Length == 0 ? 0 : offset + rows.Length, older, query);
    }

    private sealed record HistoryKey(Guid Id, DateTimeOffset CreatedAt, InboxMessage? Incoming, OutboxMessage? Outgoing);
}

// Desktop-only read positions. These are not persisted and convey no task or
// approval authority. Every page is read independently and replaces the prior page.
internal sealed record AssistantConversationCursor(ConversationIdentity Identity, DateTimeOffset BeforeCreatedAt, string BeforeId, string Query = "");
internal sealed record AssistantConversationPage(ConversationIdentity? Identity, DateTimeOffset ReadAt,
    IReadOnlyList<AssistantConversationRow> Rows, int TotalCount, int StartIndex, int EndIndex,
    AssistantConversationCursor? OlderCursor, string Query = "");
