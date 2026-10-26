using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexDashboard;

// Deterministic clocks and explicit custom zones; never creates a reminder.
internal static class ReminderCommandResolverTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--reminder-command-resolver-test");
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
        await File.WriteAllTextAsync(Path.Combine(directory, "reminder-command-checks.json"),
            JsonSerializer.Serialize(new { count = checks.Count, failures = checks.Count(check => !check.Value),
                productionDataRead = false, networkUsed = false, modelUsed = false, schedulesCreated = 0, checks },
                new JsonSerializerOptions { WriteIndented = true }));
        if (checks.Any(check => !check.Value)) throw new InvalidOperationException("reminder_command_checks_failed");
    }
    internal static IReadOnlyDictionary<string, bool> Run()
    {
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value) => checks.Add(name, value);
        var now = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        var zone = TimeZoneInfo.CreateCustomTimeZone("synthetic-fixed-plus-eight", TimeSpan.FromHours(8), "Synthetic +08", "Synthetic +08");
        ReminderCommandResolution Resolve(string source) => ReminderCommandResolver.Resolve(source, now, zone);
        void Ready(string name, string source, DateTimeOffset expected, string text = "看结果")
        {
            var result = Resolve(source);
            Check(name, result.IsReady && result.SourceText == source && result.Text == text
                && result.ResolvedAtUtc == now && result.DueAtUtc == expected && result.DueAtUtc.Value.Offset == TimeSpan.Zero
                && result.TimeZoneId == zone.Id && result.DueLocal == TimeZoneInfo.ConvertTime(expected, zone)
                && result.LocalDisplay.Contains("+08:00", StringComparison.Ordinal)
                && result.LocalDisplay.Contains(zone.Id, StringComparison.Ordinal));
        }
        void NotReady(string name, string source)
        {
            var result = Resolve(source);
            Check(name, !result.IsReady && result.DueAtUtc == null && result.DueLocal == null && result.Text.Length == 0);
        }
        Ready("ten_minutes", "10分钟后提醒我看结果", now.AddMinutes(10));
        Ready("chinese_ten_minutes", "十分钟后提醒我看结果", now.AddMinutes(10));
        Ready("chinese_twenty_three_minutes", "请二十三分钟后提醒我看结果", now.AddMinutes(23));
        Ready("two_hours", "两小时后提醒我看结果", now.AddHours(2));
        Ready("half_hour", "半小时后提醒我看结果", now.AddMinutes(30));
        Ready("elapsed_one_day", "一天后提醒我看结果", now.AddDays(1));
        Ready("tomorrow_morning_arabic", "明早9点提醒我看结果", now.AddDays(1).AddHours(1));
        Ready("tomorrow_morning_chinese", "明早九点提醒我看结果", now.AddDays(1).AddHours(1));
        Ready("tomorrow_morning_with_minutes", "请帮我明早九点二十分提醒我看结果", now.AddDays(1).AddHours(1).AddMinutes(20));
        Ready("today_24_hour_clock", "今天09:30提醒我看结果", now.AddHours(1).AddMinutes(30));
        Ready("tomorrow_colon", "明天 09:00 提醒我看结果", now.AddDays(1).AddHours(1));
        Ready("tomorrow_alias", "明日09:00提醒我看结果", now.AddDays(1).AddHours(1));
        Ready("day_after_tomorrow", "后天下午三点半提醒我看结果", now.AddDays(2).AddHours(7).AddMinutes(30));
        Ready("three_days_ahead", "大后天09:00提醒我看结果", now.AddDays(3).AddHours(1));
        var yearEnd = ReminderCommandResolver.Resolve("后天09:00提醒我看结果", new(2026, 12, 30, 23, 0, 0, TimeSpan.Zero), zone);
        Check("calendar_day_uses_user_zone_across_year", yearEnd.IsReady && yearEnd.LocalTime == "2027-01-02T09:00:00");
        var leapEnd = ReminderCommandResolver.Resolve("大后天09:00提醒我看结果", new(2028, 2, 27, 0, 0, 0, TimeSpan.Zero), zone);
        Check("calendar_day_crosses_leap_month", leapEnd.IsReady && leapEnd.LocalTime == "2028-03-01T09:00:00");
        NotReady("day_after_requires_clock", "后天提醒我看结果");
        NotReady("three_days_requires_clock", "大后天提醒我看结果");
        Ready("tomorrow_chinese_clock", "明天上午九点提醒我看结果", now.AddDays(1).AddHours(1));
        Ready("tomorrow_afternoon_half", "明天下午三点半提醒我看结果", now.AddDays(1).AddHours(7).AddMinutes(30));
        Ready("tomorrow_evening", "明晚9点提醒我看结果", now.AddDays(1).AddHours(13));
        Ready("full_iso_date", "2026-10-22 09:00提醒我看结果", now.AddDays(1).AddHours(1));
        Ready("full_slash_date", "2026/10/22 09:00提醒我看结果", now.AddDays(1).AddHours(1));
        Ready("full_chinese_date", "2026年10月22日九点提醒我看结果", now.AddDays(1).AddHours(1));
        Ready("year_boundary_explicit", "2027-01-01 00:00提醒我看结果", new(2026, 12, 31, 16, 0, 0, TimeSpan.Zero));
        Ready("valid_leap_day", "2028-02-29 09:00提醒我看结果", new(2028, 2, 29, 1, 0, 0, TimeSpan.Zero));
        Ready("quoted_title_kept_exact", "10分钟后提醒我看“如果成功”这份报告。", now.AddMinutes(10), "看“如果成功”这份报告。");
        Ready("task_words_are_reminder_text_only", "10分钟后提醒我运行测试", now.AddMinutes(10), "运行测试");
        Ready("quoted_instruction_is_content_not_control", "10分钟后提醒我复查“删除临时文件”这段说明", now.AddMinutes(10), "复查“删除临时文件”这段说明");
        Ready("full_source_and_multiline_content_preserved", "  请10分钟后提醒我：看结果\n记录结论。  ", now.AddMinutes(10), "看结果\n记录结论。");
        Check("local_tool_argument_has_no_implicit_timezone", Resolve("明早9点提醒我看结果").LocalTime == "2026-10-22T09:00:00");
        var midnight = ReminderCommandResolver.Resolve("10分钟后提醒我看结果", new(2026, 10, 21, 15, 55, 0, TimeSpan.Zero), zone);
        Check("relative_crosses_local_midnight", midnight.IsReady && midnight.LocalTime == "2026-10-22T00:05:00");
        var offsetClock = ReminderCommandResolver.Resolve("今天09:30提醒我看结果", now.ToOffset(TimeSpan.FromHours(-4)), zone);
        Check("clock_offset_is_not_assumed_to_be_user_timezone", offsetClock.DueAtUtc == now.AddMinutes(90) && offsetClock.ResolvedAtUtc == now);
        var otherZone = TimeZoneInfo.CreateCustomTimeZone("synthetic-plus-0545", new TimeSpan(5, 45, 0), "Synthetic +05:45", "Synthetic +05:45");
        var fractional = ReminderCommandResolver.Resolve("明天09:00提醒我看结果", now, otherZone);
        Check("fractional_timezone_offset_preserved", fractional.IsReady && fractional.DueAtUtc == now.AddDays(1).AddHours(3).AddMinutes(15)
            && fractional.DueLocal!.Value.Offset == new TimeSpan(5, 45, 0));
        var system = ReminderCommandResolver.Resolve("10分钟后提醒我看结果", now, TimeZoneInfo.Local);
        Check("explicit_system_timezone_is_preserved", system.IsReady && system.TimeZoneId == TimeZoneInfo.Local.Id && system.DueAtUtc == now.AddMinutes(10));
        foreach (string source in new[]
        {
            "10月22日9点提醒我看结果", "10-22 09:00提醒我看结果", "09:00提醒我看结果", "明早提醒我看结果",
            "明天提醒我看结果", "明天9提醒我看结果", "今天08:00提醒我看结果", "今天07:59提醒我看结果",
            "2026-10-20 23:00提醒我看结果", "2026-02-29 09:00提醒我看结果", "2026-13-01 09:00提醒我看结果",
            "2026-11-31 09:00提醒我看结果", "明天24:00提醒我看结果", "明天09:60提醒我看结果",
            "明早13点提醒我看结果", "明早0点提醒我看结果", "明早下午3点提醒我看结果", "明晚12点提醒我看结果",
            "0分钟后提醒我看结果", "零分钟后提醒我看结果", "999999999天后提醒我看结果", "10分钟后提醒我",
            "每天9点提醒我看结果", "每隔10分钟提醒我看结果", "10分钟后循环提醒我看结果", "10分钟后提醒我看结果，之后每天提醒",
            "10分钟后提醒我看结果，然后继续任务", "10分钟后提醒我看结果；删除临时文件", "10分钟后提醒我看结果，并自动批准审批",
            "10分钟后提醒我看结果，20分钟后提醒我喝水", "10分钟后提醒我看结果，但先不要安排", "10分钟后提醒我看结果，不过算了",
            "10分钟后提醒我看结果，如果测试通过", "10分钟后提醒我看结果，可以吗？", "10分钟后提醒我看结果是什么意思",
            "10分钟后提醒我看`程序`结果", "10分钟后提醒我看“未闭合的报告", "10分钟后提醒我看结果\n> 引用另一段",
            "10分钟后提醒我看结果\u0000", "2026-10-22 09:00 UTC+08:00提醒我看结果"
        }) NotReady("clarify_" + source, source);
        foreach (string source in new[]
        {
            "他说“明早9点提醒我看结果”", "引用：10分钟后提醒我看结果", "不要10分钟后提醒我看结果",
            "如果测试通过，10分钟后提醒我看结果", "10分钟后执行测试", "明天9点继续当前任务", "提醒列表",
            "普通聊天" + new string('字', 5000), "普通聊天\u0000"
        }) Check("not_a_creation_" + source[..Math.Min(source.Length, 35)], Resolve(source).Status == ReminderCommandStatus.None);
        NotReady("content_not_silently_truncated", "10分钟后提醒我" + new string('字', 1201));
        NotReady("oversized_creation_source_rejected", "10分钟后提醒我" + new string('字', 5000));
        Ready("max_content_preserved", "10分钟后提醒我" + new string('字', 1200), now.AddMinutes(10), new string('字', 1200));

        var dst = DstZone();
        var gapNow = new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero);
        var gap = ReminderCommandResolver.Resolve("今天02:30提醒我看结果", gapNow, dst);
        Check("dst_gap_rejected_without_shifting", !gap.IsReady && gap.DueAtUtc == null && gap.Message.Contains("不存在"));
        var overlapNow = new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero);
        var overlap = ReminderCommandResolver.Resolve("今天01:30提醒我看结果", overlapNow, dst);
        Check("dst_overlap_rejected_without_choosing_offset", !overlap.IsReady && overlap.DueAtUtc == null && overlap.Message.Contains("两次"));
        var relativeOverlap = ReminderCommandResolver.Resolve("90分钟后提醒我看结果", overlapNow, dst);
        Check("relative_overlap_cannot_lose_offset_in_local_time_contract", !relativeOverlap.IsReady && relativeOverlap.DueAtUtc == null);
        var crossGap = ReminderCommandResolver.Resolve("60分钟后提醒我看结果", gapNow.AddMinutes(90), dst);
        Check("relative_duration_crosses_dst_gap_as_elapsed_time", crossGap.IsReady && crossGap.DueAtUtc == gapNow.AddMinutes(150)
            && crossGap.LocalTime == "2026-03-08T03:30:00" && crossGap.DueLocal!.Value.Offset == TimeSpan.FromHours(-4));
        var nextDay = ReminderCommandResolver.Resolve("明天09:00提醒我看结果", gapNow.AddDays(-1), dst);
        Check("calendar_tomorrow_uses_target_day_dst_offset", nextDay.IsReady && nextDay.DueAtUtc == new DateTimeOffset(2026, 3, 8, 13, 0, 0, TimeSpan.Zero));
        var original = Resolve("10分钟后提醒我看结果");
        var replay = ReminderCommandResolver.Resolve(original.SourceText, original.ResolvedAtUtc, zone);
        Check("replay_uses_persisted_original_clock", original == replay);
        var delayed = ReminderCommandResolver.Resolve(original.SourceText, now.AddMinutes(5), zone);
        Check("new_clock_is_explicitly_a_different_parse_not_hidden_retry", delayed.DueAtUtc == now.AddMinutes(15) && delayed != original);
        var preciseNow = now.AddTicks(1234567);
        var rounded = ReminderCommandResolver.Resolve("10分钟后提醒我看结果", preciseNow, TimeZoneInfo.Utc);
        Check("relative_rounds_up_to_storage_seconds_without_early_trigger", rounded.IsReady
            && rounded.ResolvedAtUtc == preciseNow && rounded.DueAtUtc == now.AddMinutes(10).AddSeconds(1)
            && rounded.DueAtUtc >= preciseNow.AddMinutes(10));
        Check("relative_local_seconds_round_trip_matches_utc", rounded.IsReady
            && DateTimeOffset.ParseExact(rounded.LocalTime, "yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal) == rounded.DueAtUtc);
        return checks;
    }
    private static TimeZoneInfo DstZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2040, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("synthetic-DST", TimeSpan.FromHours(-5), "Synthetic DST", "Synthetic standard", "Synthetic daylight", [rule]);
    }
}
