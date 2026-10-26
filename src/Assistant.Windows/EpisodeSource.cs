using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Assistant.Core;

namespace CodexDashboard;

internal sealed record EpisodeSource(ConversationEvent Event, ImmutableArray<CommittedCatchUpMember> Members = default)
{
    internal Guid Id => Event.Id;
    internal bool IsCatchUp => !Members.IsDefaultOrEmpty;
    internal IEnumerable<ConversationEvent> DependencyEvents => IsCatchUp ? Members.Select(m => m.Source.Event) : [Event];
    internal EpisodeEventReference Reference => new(Event.Identity, Event.Id, WeChatText.Hash(IsCatchUp
        ? JsonSerializer.Serialize(new { Event, Members }, Settings.Json) : JsonSerializer.Serialize(Event, Settings.Json)));
    public static implicit operator EpisodeSource?(ConversationEvent? source) => source == null ? null : new(source);
    internal object[] FragmentDetails() => IsCatchUp ? Members.Select(m => (object)new
    {
        eventId = m.Source.Event.Id, source = EpisodeMemoryEditorWindow.SourceLabel(m.Source.Event.Source),
        task = m.Source.Event.Task, recordedAt = m.Source.Event.RecordedAt,
        ranges = m.Spans.Select(s => new { outboxId = s.OutboxId, start = s.Start, length = s.Length, bodyHash = s.BodyHash })
    }).ToArray() : [];
    internal string DescribeFragments() => !IsCatchUp ? "" : "\n\n补报片段来源（仅展示所选内容，不代表完整原报告）：\n"
        + string.Join("\n\n", Members.Select(m => $"{EpisodeMemoryEditorWindow.SourceLabel(m.Source.Event.Source)} · 事件 {m.Source.Event.Id}\n"
            + (m.Source.Event.Task is { } task ? $"主机：{task.HostId} · 任务：{task.ThreadId} · 轮次：{task.TurnId ?? "未记录"}\n" : "")
            + string.Join("\n", m.Spans.Select(s => $"范围 {s.Start}–{s.Start + s.Length}（不含末尾），原分段 {s.OutboxId}\n{s.Text}"))));
}
