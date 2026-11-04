using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexDashboard;

// Read-only calendar interpretations. They neither rewrite messages nor schedule work.
internal static class RelativeDateContext
{
    internal sealed record Hint(string Expression, string Date, string AnchorDate, string TimeZone, string Basis);
    internal static Hint[] Read(string text, DateTimeOffset occurredAt, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var anchor = TimeZoneInfo.ConvertTime(occurredAt, zone).Date;
        var monday = anchor.AddDays(-(((int)anchor.DayOfWeek + 6) % 7));
        return Regex.Matches(text, @"(?<![上下本这])(?<week>上上|下下|上|下|本|这)(?:周|星期)(?<day>[一二三四五六日天])(?![一二三四五六日天])|(?<![大前后])(?<relative>大前天|大后天|前天|昨天|今天|明天|后天)")
            .Cast<Match>().Select(match =>
            {
                bool relative = match.Groups["relative"].Success;
                int offset = match.Groups["relative"].Value switch { "大前天" => -3, "前天" => -2, "昨天" => -1, "明天" => 1, "后天" => 2, "大后天" => 3, _ => 0 };
                int week = match.Groups["week"].Value switch { "上上" => -2, "下下" => 2, "上" => -1, "下" => 1, _ => 0 };
                int day = "一二三四五六日".IndexOf(match.Groups["day"].Value.Replace('天', '日'), StringComparison.Ordinal);
                var date = relative ? anchor.AddDays(offset) : monday.AddDays(7 * week + day);
                return new Hint(match.Value, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    anchor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), zone.Id,
                    (relative ? "按原消息时间计算" : "按原消息时间及周一开始的一周计算")
                    + "；仅解释原文中的日期表达，不证明安排或授权操作");
            }).Distinct().ToArray();
    }
}
