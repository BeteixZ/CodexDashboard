using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CodexDashboard;

internal static class WeChatReportSmokeTests
{
    private static TaskRecord Report(string id = "one", string text = "FINAL_BODY", double completed = 1050) => new()
    {
        Id = id, TurnId = "turn-" + id, Host = "本机", Project = "示例项目", Title = "报告任务 " + id,
        Status = TaskStatusNames.Completed, CompletedAt = completed, LastActivity = completed,
        FinalReport = text, FinalReportTurnId = "turn-" + id, FinalReportSource = "本轮最终回复", OneLineSummary = "结果已验证；仍有一项人工验收。"
    };
    public static void Run(Dictionary<string, bool> checks)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1100);
        WeChatSnapshot Snapshot(params TaskRecord[] tasks) => new(tasks, now, Array.Empty<HostSnapshot>());
        var task = Report(); var store = new WeChatReports(); store.Capture(new[] { task }); string id = WeChatReports.IdFor(task);
        string report = WeChatText.Reply("报告", Snapshot(task), false, now, store);
        checks["reportReturnsExplicitFinalWithTurnAndSource"] = report.Contains("FINAL_BODY") && report.Contains("turn-one") && report.Contains("来自本轮最终回复");
        string introduction = report[..report.IndexOf("\n\n", StringComparison.Ordinal)];
        checks["requestedReportHasCompactNaturalLocationWithoutMetadataColumns"] = introduction.Split('\n').Length == 2
            && introduction.StartsWith("这是“报告任务 one”在 本机 / 示例项目 的本轮原文", StringComparison.Ordinal)
            && introduction.Contains("报告 " + id + "，第 1/1 页") && introduction.Contains("对应轮次 turn-one")
            && introduction.Contains("记录为" + task.StatusLabel) && introduction.Contains("结束于 ")
            && !new[] { "\n状态：", "\n时间：", "\n来源：", "\n轮次：", "\n结束：" }.Any(introduction.Contains);
        checks["reportQueryCanBeDisabled"] = !WeChatText.Reply("报告", Snapshot(task), false, now, store, false).Contains("FINAL_BODY");
        checks["reportQueryByKeyword"] = WeChatText.Reply("报告 示例项目", Snapshot(task), false, now, store).Contains("FINAL_BODY");
        var notice = WeChatText.Notification(task, false, now, true);
        checks["completionHasOnlyOverviewAndKeepsReportAssociation"] = notice.Body == task.OverviewText && notice.Title == "" && notice.Completion?.ReportId == id && !notice.Body.Contains("FINAL_BODY");
        string longText = "BEGIN\n" + new string('段', 2100) + "\nEND";
        var longTask = Report("long", longText); string longId = WeChatReports.IdFor(longTask); store.Capture(new[] { longTask });
        string first = store.Reply("报告 " + longId, Snapshot(longTask), now);
        string last = store.Reply("报告 " + longId + " 3", Snapshot(longTask), now);
        checks["longReportPaginatesOnRequest"] = first.Contains("第 1/3 页") && first.Contains("下一页：报告 " + longId + " 2") && !first.Contains("END") && last.Contains("END");
        checks["invalidReportPageRejected"] = store.Reply("报告 " + longId + " 99", Snapshot(longTask), now).Contains("共有 3 页") && store.Reply("报告 " + longId + " 0", Snapshot(longTask), now).Contains("正整数");
        string unicode = new string('A', 899) + "😀\n\t代码缩进\n" + string.Concat(Enumerable.Repeat("😀", 1000));
        var pages = WeChatReports.Pages(unicode);
        checks["reportPaginationPreservesAllTextAndUnicode"] = string.Concat(pages) == unicode && pages.All(WellFormed) && pages.All(p => p.Length <= 900);
        string exact = "  原文前置空格\n\n" + unicode + "\n尾行\t  ";
        var exactTask = Report("exact", exact); var exactStore = new WeChatReports(); exactStore.Capture(new[] { exactTask });
        var expectedPages = WeChatReports.Pages(exact); var actualPages = new List<string>(); bool exactPages = true;
        for (int page = 1; page <= expectedPages.Count; page++)
        {
            string reply = exactStore.Reply("报告 " + WeChatReports.IdFor(exactTask) + " " + page, Snapshot(exactTask), now);
            int start = reply.IndexOf("\n\n", StringComparison.Ordinal) + 2;
            string actual = reply.Substring(start, expectedPages[page - 1].Length);
            exactPages &= start >= 2 && actual == expectedPages[page - 1] && reply[start + actual.Length] == '\n';
            actualPages.Add(actual);
        }
        checks["naturalReportHeaderPreservesEveryOriginalPageCharacter"] = exactPages && string.Concat(actualPages) == exact;
        task.Status = TaskStatusNames.Active; task.TurnId = "next-turn"; task.FinalReport = "SHOULD_NOT_REPLACE";
        string cached = store.Reply("报告 " + id, Snapshot(task), now);
        checks["reportSnapshotSurvivesNewTurn"] = cached.Contains("FINAL_BODY") && cached.Contains("turn-one") && !cached.Contains("SHOULD_NOT_REPLACE");
        var missing = Report("newest", "", 1099);
        checks["missingLatestReportDoesNotReturnOlderTask"] = store.Reply("报告", Snapshot(missing), now).Contains("尚未读取到这一轮") && !store.Reply("报告", Snapshot(missing), now).Contains("FINAL_BODY");
        var ambiguousStore = new WeChatReports(); var a = Report("a"); var b = Report("b"); ambiguousStore.Capture(new[] { a, b });
        string ambiguous = ambiguousStore.Reply("报告 示例项目", Snapshot(a, b), now);
        checks["ambiguousReportsRequireSelection"] = ambiguous.Contains(WeChatReports.IdFor(a)) && ambiguous.Contains(WeChatReports.IdFor(b)) && !ambiguous.Contains("FINAL_BODY");
        var wrong = Report("wrong", "WRONG_TURN"); wrong.FinalReportTurnId = "another-turn";
        checks["reportStoreRejectsMismatchedTurn"] = !new WeChatReports().Reply("报告", Snapshot(wrong), now).Contains("WRONG_TURN");
        var truncated = Report("truncated"); truncated.FinalReportTruncated = true;
        checks["truncatedReportClearlyLabelled"] = new WeChatReports().Reply("报告", Snapshot(truncated), now).Contains("并非全文");
        var oldData = new WeChatSnapshot(Array.Empty<TaskRecord>(), now.AddMinutes(-5), Array.Empty<HostSnapshot>());
        checks["cachedReportLabelsStaleCollection"] = store.Reply("报告 " + id, oldData, now).Contains("以下为此前读取的报告");
        var bounded = new WeChatReports(); var many = Enumerable.Range(0, 140).Select(i => Report("bound-" + i, "REPORT_BODY", 1000 + i)).ToArray(); bounded.Capture(many);
        checks["reportCacheCountBounded"] = bounded.Reply("报告 " + WeChatReports.IdFor(many[0]), Snapshot(), now).Contains("未找到") && bounded.Reply("报告 " + WeChatReports.IdFor(many[^1]), Snapshot(), now).Contains("REPORT_BODY");
        var charBounded = new WeChatReports(); var large = Enumerable.Range(0, 20).Select(i => Report("large-" + i, new string('x', 64000), 1000 + i)).ToArray(); charBounded.Capture(large);
        checks["reportCacheCharacterBudgetBounded"] = charBounded.Reply("报告 " + WeChatReports.IdFor(large[0]), Snapshot(), now).Contains("未找到");
        var budget = Report(new string('轮', 70), new string('字', 2000)); budget.Host = new string('机', 100); budget.Project = new string('项', 100); budget.Title = new string('题', 200);
        string payload = new WeChatReports().Reply("报告", Snapshot(budget), now);
        checks["reportPageHasConservativeWireBudget"] = payload.Length < 3500 && Encoding.UTF8.GetByteCount(payload) < 4096;
    }
    private static bool WellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsLowSurrogate(text[i])) return false;
            if (char.IsHighSurrogate(text[i]) && (++i == text.Length || !char.IsLowSurrogate(text[i]))) return false;
        }
        return true;
    }
}
