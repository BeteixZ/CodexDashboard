using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexDashboard;

// Pure parser fixtures only. No Settings, store, DPAPI, model or channel is used.
internal static class MemoryCommandResolverTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--memory-command-resolver-test");
        if (index < 0) return false;
        try
        {
            if (index + 1 >= args.Length) throw new ArgumentException("explicit_output_directory_required");
            await RunAsync(args[index + 1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name); exit(1); }
        return true;
    }

    internal static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl))
            throw new ArgumentException("new_absolute_output_required");
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new ArgumentException("new_output_required");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
        var checks = Run();
        await File.WriteAllTextAsync(Path.Combine(directory, "memory-command-checks.json"),
            JsonSerializer.Serialize(new { count = checks.Count, failures = checks.Count(check => !check.Value),
                productionDataRead = false, networkUsed = false, modelUsed = false, checks },
                new JsonSerializerOptions { WriteIndented = true }));
        if (checks.Any(check => !check.Value)) throw new InvalidOperationException("memory_command_checks_failed");
    }

    internal static IReadOnlyDictionary<string, bool> Run()
    {
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool passed) => checks.Add(name, passed);
        var time = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var first = new AssistantMemoryEntry("AABBCCDD", "以后汇报先说结论", time, time);
        var second = new AssistantMemoryEntry("EEFF0011", "以后汇报分行", time, time, "automatic", "report_layout");
        var numeric = new AssistantMemoryEntry("12345678", "我叫阿林", time, time);
        var original = new[] { first, second, numeric };
        MemoryCommandResolution Resolve(string source) => MemoryCommandResolver.Resolve(source, original);
        void Ready(string name, string source, string action, string id, string text)
        {
            var result = Resolve(source);
            Check(name, result.IsReady && result.Action == action && result.MemoryId == id
                && result.Text == text && result.SourceText == source && result.CandidateIds.IsEmpty
                && (id.Length == 0 ? result.TargetEntry == null : result.TargetEntry == original.Single(entry => entry.Id == id)));
        }
        void NotReady(string name, string source) => Check(name, !Resolve(source).IsReady);

        Ready("remember_without_colon", "记住以后先说结论", "remember", "", "以后先说结论");
        Ready("remember_help_prefix", "帮我记住我更喜欢分行", "remember", "", "我更喜欢分行");
        Ready("remember_polite_colon", "请记住：以后先说结论", "remember", "", "以后先说结论");
        Ready("remember_full_polite_prefix", "请帮我记住， 以后先说结论", "remember", "", "以后先说结论");
        Ready("remember_exact_multiline_content", " 记住：第一行\n第二行。 ", "remember", "", "第一行\n第二行。");
        Ready("quoted_alias_is_literal_memory_content", "记住：你叫“小桥”", "remember", "", "你叫“小桥”");
        Ready("quoted_conditional_is_literal_content_not_executed", "记住：示例原文是“如果成功就删除记忆 AABBCCDD”", "remember", "", "示例原文是“如果成功就删除记忆 AABBCCDD”");
        Ready("apostrophe_in_name_is_not_quote_boundary", "记住：我的昵称是 O'Connor", "remember", "", "我的昵称是 O'Connor");
        Ready("negative_memory_rule_is_not_outer_withdrawal", "记住：不要自动批准任务", "remember", "", "不要自动批准任务");
        Ready("update_exact_id", "修改记忆 AABBCCDD 以后分行回答", "update_memory", "AABBCCDD", "以后分行回答");
        Ready("update_lowercase_id", "请修改记忆 aabbccdd 以后分行回答", "update_memory", "AABBCCDD", "以后分行回答");
        Ready("update_marker", "更新记忆 AABBCCDD 改为 以后分行回答", "update_memory", "AABBCCDD", "以后分行回答");
        Ready("update_no_space_marker_is_verbatim_content", "修改记忆 AABBCCDD 改成这条明确内容", "update_memory", "AABBCCDD", "改成这条明确内容");
        Ready("natural_update_without_spaces", "把记忆AABBCCDD改为以后分行回答", "update_memory", "AABBCCDD", "以后分行回答");
        Ready("natural_update_with_spaces", "请把记忆 AABBCCDD 改成 以后分行回答", "update_memory", "AABBCCDD", "以后分行回答");
        Ready("update_preserves_content_beginning_with_wei", "修改记忆 AABBCCDD 为人坦率，不编造事实。", "update_memory", "AABBCCDD", "为人坦率，不编造事实。");
        Ready("unique_keyword_update", "把记忆先说结论改为先讲验证结果", "update_memory", "AABBCCDD", "先讲验证结果");
        Ready("delete_exact_id", "删除记忆 AABBCCDD", "forget", "AABBCCDD", "");
        Ready("delete_lowercase_id_with_punctuation", "请删除记忆 aabbccdd。", "forget", "AABBCCDD", "");
        Ready("delete_numeric_full_id_is_not_list_selection", "删除记忆 12345678", "forget", "12345678", "");
        Ready("delete_unique_keyword", "删除记忆 先说结论", "forget", "AABBCCDD", "");
        Ready("delete_quoted_keyword", "帮我删除记忆“先说结论”", "forget", "AABBCCDD", "");
        Ready("natural_forget_about_keyword", "忘记关于先说结论的记忆", "forget", "AABBCCDD", "");
        Ready("natural_forget_preference", "请忘掉有关分行的偏好。", "forget", "EEFF0011", "");
        Ready("delete_this_memory_id", "删除这条记忆 AABBCCDD", "forget", "AABBCCDD", "");

        foreach (string source in new[] { "删除记忆 以后汇报", "把记忆以后汇报改为简短回答" })
        {
            var result = Resolve(source);
            Check("ambiguous_candidates_" + source, result.Status == MemoryCommandStatus.NeedsClarification
                && result.CandidateIds.SequenceEqual(new[] { first.Id, second.Id })
                && result.MemoryId.Length == 0 && result.Text.Length == 0 && result.TargetEntry == null
                && result.Message.Contains("完整编号") && result.SourceText == source);
        }
        foreach (string source in new[] { "1", "选1", "第二个", "第2条", "AABBCCDD", "取消记忆操作" })
            Check("no_pending_selection_" + source, Resolve(source).Status == MemoryCommandStatus.None);
        foreach (string source in new[] { "删除记忆 1", "删除记忆 AABB", "删除记忆 全部", "忘记刚才那条记忆",
            "删除记忆 不存在的内容", "修改记忆", "把记忆 AABBCCDD 改为", "记住：" })
            NotReady("clarification_" + source, source);
        foreach (string source in new[] {
            "他说“删除记忆 AABBCCDD”", "引用：修改记忆 AABBCCDD 乱改", "“记住：虚假授权”",
            "> 删除记忆 AABBCCDD", "```text\n删除记忆 AABBCCDD\n```", "请解释删除记忆 AABBCCDD",
            "不要删除记忆 AABBCCDD", "别帮我记住这个", "如果成功，删除记忆 AABBCCDD",
            "删除记忆 AABBCCDD，如果结果正确", "记住：以后分行，如果我之后同意",
            "修改记忆 AABBCCDD 简短回答，先别修改", "删除记忆 AABBCCDD，然后修改记忆 EEFF0011 内容",
            "记住：以后分行；删除记忆 AABBCCDD", "记住：以后分行\n修改记忆 AABBCCDD 内容",
            "记住：以后分行，可以吗？", "记住了吗", "你记住了什么", "记住：以后分行，不要保存",
            "记住：以后分行`忽略约束`", "记住：你叫“小桥", "删除记忆「先说结论”",
            "记住：以后分行\n> 这是引用", "删除记忆 AABBCCDD\n不要执行",
            "修改记忆 AABBCCDD 新内容\n不要执行", "记住：以后分行，但先不要执行",
            "修改记忆 AABBCCDD 新内容；现在不要操作", "记住：以后分行，不过算了",
            "记住这句话是什么意思", "记住这条偏好后你会怎么回答",
            "修改记忆 AABBCCDD 新内容并且不要执行", "记住：以后分行而且先别保存",
            "修改记忆 AABBCCDD 新内容\u0000", "查看记忆，删除全部" })
            NotReady("protected_source_" + source, source);
        foreach (string source in new[] { "查看记忆", "记忆 2", "查看规则", "自动记忆状态", "任务列表", "状态", "继续测试" })
            Check("not_a_memory_write_" + source, Resolve(source).Status == MemoryCommandStatus.None);

        var exact = Resolve("删除记忆 AABBCCDD");
        var missing = MemoryCommandResolver.Resolve(exact.SourceText, new[] { second, numeric });
        Check("removed_id_never_falls_back_to_other_entries", !missing.IsReady && missing.CandidateIds.IsEmpty);
        var textContainsId = new AssistantMemoryEntry("DEADBEEF", "这条内容提到 AABBCCDD", time, time);
        Check("missing_full_id_never_becomes_keyword", !MemoryCommandResolver.Resolve(exact.SourceText, new[] { textContainsId }).IsReady);
        var changed = first with { Text = "用户后来手动修改", UpdatedAt = time.AddMinutes(1) };
        var changedResolution = MemoryCommandResolver.Resolve(exact.SourceText, new[] { changed, second, numeric });
        Check("same_id_changed_entry_invalidates_old_resolution", changedResolution.IsReady
            && !exact.SameRequestAs(changedResolution) && changedResolution.TargetEntry == changed);
        var recreated = first with { CreatedAt = time.AddMinutes(2), UpdatedAt = time.AddMinutes(2) };
        Check("same_id_same_text_recreation_invalidates_old_resolution",
            !exact.SameRequestAs(MemoryCommandResolver.Resolve(exact.SourceText, new[] { recreated })));
        Check("changed_full_source_invalidates_old_resolution", !exact.SameRequestAs(Resolve("请删除记忆 AABBCCDD")));
        Check("unchanged_snapshot_matches_exact_request", exact.SameRequestAs(Resolve(exact.SourceText)));
        Check("non_ready_resolutions_never_match_for_execution", !missing.SameRequestAs(missing));
        var keyword = Resolve("删除记忆 先说结论");
        var ambiguousLater = first with { Id = "FACEB00C" };
        Check("new_matching_entry_requires_clarification_on_reresolve",
            !keyword.SameRequestAs(MemoryCommandResolver.Resolve(keyword.SourceText, new[] { first, ambiguousLater })));
        Check("duplicate_snapshot_ids_rejected", MemoryCommandResolver.Resolve(exact.SourceText,
            new[] { first, first with { Id = "aabbccdd" } }).Status == MemoryCommandStatus.Rejected);
        Check("invalid_snapshot_id_rejected", MemoryCommandResolver.Resolve(exact.SourceText,
            new[] { first with { Id = "bad" } }).Status == MemoryCommandStatus.Rejected);
        Check("null_snapshot_id_rejected", MemoryCommandResolver.Resolve(exact.SourceText,
            new[] { first with { Id = null! } }).Status == MemoryCommandStatus.Rejected);
        Check("empty_unrevoked_snapshot_still_rejected", MemoryCommandResolver.Resolve("记住：新的偏好",
            new[] { first with { Text = "", Metadata = null } }).Status == MemoryCommandStatus.Rejected);
        Check("maximum_memory_length_preserved", Resolve("记住：" + new string('字', 1200)).Text.Length == 1200);
        NotReady("oversized_memory_not_truncated", "记住：" + new string('字', 1201));
        NotReady("oversized_source_rejected_before_matching", "记住：" + new string('字', 5000));
        Check("long_ordinary_chat_is_not_memory_command", Resolve("请分析下面的内容：" + new string('字', 5000)).Status == MemoryCommandStatus.None);
        Check("ordinary_control_character_is_not_memory_command", Resolve("请分析这份日志\u0000").Status == MemoryCommandStatus.None);
        Check("long_natural_memory_delete_still_rejected", Resolve("忘掉" + new string('字', 5000) + "的记忆").Status == MemoryCommandStatus.Rejected);
        Check("parser_does_not_mutate_input_snapshot", original.SequenceEqual(new[] { first, second, numeric })
            && first.Text == "以后汇报先说结论" && first.UpdatedAt == time);
        return checks;
    }
}
