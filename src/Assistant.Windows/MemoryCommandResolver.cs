using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexDashboard;

internal enum MemoryCommandStatus { None, Ready, NeedsClarification, Rejected }

// A syntactic resolution, never an authority grant. The host must still verify
// the current authenticated source, binding/revision and durable operation.
internal sealed record MemoryCommandResolution(
    MemoryCommandStatus Status, string SourceText, string Action, string MemoryId, string Text,
    AssistantMemoryEntry? TargetEntry, ImmutableArray<string> CandidateIds, string Message)
{
    internal bool IsReady => Status == MemoryCommandStatus.Ready;

    // Re-resolve immediately before execution. A stable ID alone is insufficient:
    // editing/recreating its entry, or changing the source, invalidates this match.
    internal bool SameRequestAs(MemoryCommandResolution current) => IsReady && current.IsReady
        && SourceText == current.SourceText && Action == current.Action
        && MemoryId == current.MemoryId && Text == current.Text && TargetEntry == current.TargetEntry;
}

internal static class MemoryCommandResolver
{
    private const int MaximumSourceCharacters = 4000, MaximumMemoryCharacters = 1200;
    private const string Prefix = @"(?:(?:请帮我|帮我|请)\s*)?";
    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private static readonly Regex Add = new(@"\A" + Prefix + @"记住[，,:：\s]*(?<body>[\s\S]*)\z", Options);
    private static readonly Regex Update = new(@"\A" + Prefix
        + @"(?:修改|更新)记忆\s+(?<selector>\S+)\s+(?:(?:改为|改成)\s+)?(?<body>[\s\S]+)\z", Options);
    private static readonly Regex NaturalUpdate = new(@"\A" + Prefix
        + @"把记忆\s*(?<selector>.+?)\s*(?:修改为|更新为|改为|改成)\s*(?<body>[\s\S]+)\z", Options);
    private static readonly Regex Delete = new(@"\A" + Prefix
        + @"(?:删除|忘掉|忘记)(?:那条|这条)?记忆[，,:：\s]*(?<selector>[\s\S]*)\z", Options);
    private static readonly Regex NaturalDelete = new(@"\A" + Prefix
        + @"(?:删除|忘掉|忘记)(?:关于|有关)?(?<selector>.+?)的(?:记忆|偏好)\z", Options);
    private static readonly Regex FullId = new(@"\A[0-9A-Fa-f]{8}\z", Options);
    private static readonly Regex AllHex = new(@"\A[0-9A-Fa-f]+\z", Options);
    private static readonly Regex StartsMemoryAction = new(@"\A" + Prefix
        + @"(?:记住|(?:修改|更新|删除|忘掉|忘记)(?:那条|这条)?记忆|把记忆|(?:删除|忘掉|忘记)(?:关于|有关)|(?:忘掉|忘记)刚才那条)", Options);
    private static readonly Regex StartsNaturalDelete = new(@"\A" + Prefix + @"(?:删除|忘掉|忘记)", Options);
    private static readonly Regex Conditional = new(@"如果|假如|假设|假定|除非|否则|前提|条件是|(?:通过|完成|结束|确认|同意|批准)后(?:再|才)|等.{0,20}再(?:记住|保存|删除|修改|更新|忘记|忘掉)", Options);
    private static readonly Regex Question = new(@"[?？]|(?:可以吗|行吗|好吗|对吗|了吗|是否|能否|能不能|可不可以|是什么意思|什么意思|怎么理解|如何理解|会怎么回答)|吗[。.!！]*\z", Options);
    private static readonly Regex AdditionalAction = new(@"(?:[，,;；。\r\n]|\bthen\b)\s*(?:(?:然后|并且|同时|接着|再|也)\s*)*"
        + Prefix + @"(?:记住|(?:修改|更新|删除|忘掉|忘记)(?:那条|这条)?记忆|把记忆|(?:删除|忘掉|忘记)(?:关于|有关))", Options | RegexOptions.IgnoreCase);
    private static readonly Regex Withdrawal = new(@"(?:[，,;；。\r\n]|但是|不过|可是|而且|并且|然后)\s*(?:(?:现在|目前|请|先|暂时|暂|还是|但|但是|不过|这次|本次)\s*)*(?:(?:不要|别|不许|禁止|无需|不用|取消|暂不).{0,12}(?:记住|保存|修改|更新|删除|忘记|忘掉|执行|操作)|算了|作罢|先等等)", Options);

    internal static MemoryCommandResolution Resolve(string source, IReadOnlyList<AssistantMemoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entries);
        MemoryCommandResolution Result(MemoryCommandStatus status, string message = "", string action = "",
            string id = "", string text = "", AssistantMemoryEntry? entry = null, IEnumerable<string>? candidates = null)
            => new(status, source, action, id, text, entry,
                candidates == null ? ImmutableArray<string>.Empty : candidates.ToImmutableArray(), message);

        string command = source.Trim();
        string deletionCommand = command.TrimEnd('。', '！', '!', '.');
        // This resolver also identifies the local fast path. An unrelated long
        // message must remain ordinary chat, without reading the memory store.
        bool candidate = StartsMemoryAction.IsMatch(command) || StartsNaturalDelete.IsMatch(command)
            && (deletionCommand.EndsWith("的记忆", StringComparison.Ordinal)
                || deletionCommand.EndsWith("的偏好", StringComparison.Ordinal));
        if (!candidate) return Result(MemoryCommandStatus.None);
        if (source.Length > MaximumSourceCharacters
            || source.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            return Result(MemoryCommandStatus.Rejected, "原消息过长或包含无法处理的字符，请重新明确一条记忆操作。");
        var add = Add.Match(command);
        var update = NaturalUpdate.Match(command);
        if (!update.Success) update = Update.Match(command);
        var delete = Delete.Match(deletionCommand);
        if (!delete.Success) delete = NaturalDelete.Match(deletionCommand);
        if (!add.Success && !update.Success && !delete.Success && !StartsMemoryAction.IsMatch(command))
            return Result(MemoryCommandStatus.None);
        if (!TryUnquoted(command, out string visible) || command.Contains('`')
            || Regex.IsMatch(command, @"(?m)^\s*>", Options))
            return Result(MemoryCommandStatus.Rejected, "引用或代码的边界不明确，没有从中提取记忆操作。");
        if (Conditional.IsMatch(visible) || Question.IsMatch(visible)
            || AdditionalAction.IsMatch(visible) || Withdrawal.IsMatch(visible))
            return Result(MemoryCommandStatus.Rejected, "这句话包含条件、疑问、撤回或多项操作，请重新明确一条记忆操作。");

        // Snapshot corruption cannot make an ID/keyword appear uniquely resolved.
        if (entries.Any(entry => entry == null || entry.Id == null || !FullId.IsMatch(entry.Id)
                || string.IsNullOrWhiteSpace(entry.Text) && !(entry.Text == "" && entry.Metadata?.InvalidatedAtUtc != null))
            || entries.Select(entry => entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            return Result(MemoryCommandStatus.Rejected, "当前记忆列表无法唯一核对，未选择或修改条目。");

        if (add.Success)
        {
            string body = add.Groups["body"].Value.Trim();
            if (body.Length == 0 || body.All(char.IsPunctuation)
                || Regex.IsMatch(body, @"\A(?:了?吗|了吗|什么|怎么|如何)", Options))
                return Result(MemoryCommandStatus.NeedsClarification, "请完整说明要保存的内容，例如“记住：以后汇报先说结论”。", "remember");
            if (body.Length > MaximumMemoryCharacters)
                return Result(MemoryCommandStatus.Rejected, "每条记忆最多 1200 字符，请缩短后重新发送。", "remember");
            return Result(MemoryCommandStatus.Ready, action: "remember", text: body);
        }
        string action = update.Success || Regex.IsMatch(command, @"\A" + Prefix + @"(?:(?:修改|更新)记忆|把记忆)", Options)
            ? "update_memory" : "forget";
        if (!update.Success && !delete.Success)
            return Result(MemoryCommandStatus.NeedsClarification,
                "请使用“删除记忆 完整编号”或“修改记忆 完整编号 新内容”。这里不根据“刚才那条”或数字选择执行。", action);
        string replacement = update.Success ? update.Groups["body"].Value.Trim() : "";
        if (update.Success && (replacement.Length == 0 || replacement.All(char.IsPunctuation)
                || replacement.Length > MaximumMemoryCharacters))
            return Result(MemoryCommandStatus.Rejected, "修改需要提供 1–1200 字符的完整新内容。", action);
        string selector = (update.Success ? update : delete).Groups["selector"].Value.Trim();
        selector = UnwrapSelector(selector);
        bool fullId = FullId.IsMatch(selector);
        if (selector.Length < 2 || !fullId && (Regex.IsMatch(selector,
            @"\A(?:全部|所有|全体|所有记忆|全部记忆|刚才那条|刚才|那条|这条|上一条|上一个|它|这些|第?[一二三四五六七八九十0-9]+条?)\z", Options)
            || AllHex.IsMatch(selector)))
            return Result(MemoryCommandStatus.NeedsClarification,
                "请提供完整记忆编号或明确关键词；不按数字、短编号或“全部”执行。", action);
        var matches = fullId
            ? entries.Where(entry => entry.Id.Equals(selector, StringComparison.OrdinalIgnoreCase)).ToArray()
            : entries.Where(entry => entry.Text.Contains(selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
            return Result(MemoryCommandStatus.NeedsClarification,
                "当前记忆中没有匹配条目，请先发送“查看记忆”核对完整编号。", action);
        if (matches.Length > 1)
            return Result(MemoryCommandStatus.NeedsClarification,
                "匹配了多条记忆，尚未执行。请使用其中一个完整编号重新发送“删除记忆 编号”或“修改记忆 编号 新内容”。",
                action, candidates: matches.Select(entry => entry.Id));
        var target = matches[0];
        return Result(MemoryCommandStatus.Ready, action: action, id: target.Id,
            text: replacement, entry: target);
    }

    // Quotes stay in the exact saved text. Masking is used only for guards, never
    // to select a substring or manufacture a different executable source.
    private static bool TryUnquoted(string source, out string visible)
    {
        var result = new StringBuilder(source.Length);
        var closing = new Stack<char>();
        for (int index = 0; index < source.Length; index++)
        {
            char value = source[index];
            bool apostrophe = value == '\'' && index > 0 && index + 1 < source.Length
                && char.IsLetterOrDigit(source[index - 1]) && char.IsLetterOrDigit(source[index + 1]);
            if (!apostrophe && closing.Count > 0 && value == closing.Peek())
            { closing.Pop(); result.Append(' '); continue; }
            char end = value switch { '“' => '”', '‘' => '’', '「' => '」', '『' => '』', '"' => '"', '\'' when !apostrophe => '\'', _ => '\0' };
            if (end != '\0') { closing.Push(end); result.Append(' '); continue; }
            if (value is '”' or '’' or '」' or '』') { visible = ""; return false; }
            result.Append(closing.Count == 0 ? value : ' ');
        }
        visible = result.ToString();
        return closing.Count == 0;
    }

    private static string UnwrapSelector(string selector)
    {
        if (selector.Length < 2) return selector;
        return (selector[0], selector[^1]) is ('“', '”') or ('‘', '’') or ('「', '」')
            or ('『', '』') or ('"', '"') or ('\'', '\'') ? selector[1..^1].Trim() : selector;
    }
}
