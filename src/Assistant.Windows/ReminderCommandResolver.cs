using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace CodexDashboard;

internal enum ReminderCommandStatus { None, Ready, NeedsClarification, Rejected }

// Describes one notification to the user, never permission to execute its text.
// Keep ResolvedAtUtc and the chosen instant with the source: retrying a relative
// request against a new clock must not move an already-created reminder.
internal sealed record ReminderCommandResolution(ReminderCommandStatus Status, string SourceText,
    string Text, DateTimeOffset ResolvedAtUtc, DateTimeOffset? DueAtUtc, string TimeZoneId,
    DateTimeOffset? DueLocal, string TimeExpression, string Message)
{
    internal bool IsReady => Status == ReminderCommandStatus.Ready;
    internal string LocalTime => DueLocal?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) ?? "";
    internal string LocalDisplay => DueLocal is { } due
        ? due.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + " [" + TimeZoneId + "]" : "";
}

internal static class ReminderCommandResolver
{
    private const string Prefix = @"(?:(?:请帮我|帮我|请)\s*)?";
    private const string Number = @"(?:[0-9]{1,9}|[零〇一二两三四五六七八九十]{1,3})";
    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private static readonly Regex Candidate = new(@"\A" + Prefix
        + @"(?:[0-9零〇一二两三四五六七八九十半]|今天|明天|明早|明晚|今晚|后天|大后天|明日|每|循环|反复|定时|提醒我)", Options);
    private static readonly Regex Command = new(@"\A" + Prefix + @"(?<when>.+?)\s*提醒我[，,:：\s]*(?<text>[\s\S]*)\z", Options);
    private static readonly Regex Relative = new(@"\A(?<amount>" + Number + @")\s*(?<unit>分钟|小时|天)\s*后\z", Options);
    private static readonly Regex Day = new(@"\A(?<day>今天|明天|明日|后天|大后天|明早|今晚|明晚)\s*(?<clock>[\s\S]+)\z", Options);
    private static readonly Regex Date = new(@"\A(?<year>[0-9]{4})(?:-(?<month>[0-9]{1,2})-(?<day>[0-9]{1,2})|/(?<month>[0-9]{1,2})/(?<day>[0-9]{1,2})|年(?<month>[0-9]{1,2})月(?<day>[0-9]{1,2})日)\s+(?<clock>[\s\S]+)\z", Options);
    private static readonly Regex ChineseDate = new(@"\A(?<year>[0-9]{4})年(?<month>[0-9]{1,2})月(?<day>[0-9]{1,2})日\s*(?<clock>[\s\S]+)\z", Options);
    private static readonly Regex Clock = new(@"\A(?<period>早上|上午|中午|下午|晚上)?\s*(?<hour>" + Number
        + @")(?:(?:[:：])(?<minute>[0-9]{2})|点(?:(?<half>半)|(?<minute>" + Number + @")(?:分)?)?)\z", Options);
    private static readonly Regex Recurring = new(@"\A" + Prefix + @"(?:每|循环|反复)|(?:重复|循环|反复|定期|一直)提醒|(?:每隔|每[天周月年小时分钟]).{0,12}(?:提醒|通知)|提醒我.{0,12}[0-9一二两三四五六七八九十]+次", Options);
    private static readonly Regex Conditions = new(@"如果|假如|假设|假定|除非|否则|前提|条件是|(?:通过|完成|结束|确认|同意|批准)后(?:再|才)|直到", Options);
    private static readonly Regex Questions = new(@"[?？]|可以吗|行吗|好吗|能否|能不能|可不可以|是否|是什么意思|什么意思|怎么理解|如何理解|吗[。.!！]*\z", Options);
    private static readonly Regex ExtraAction = new(@"(?:[，,;；。\r\n]|然后|并且|同时|顺便|并)\s*(?:(?:再|请|帮我|自动|立即|接着)\s*)*(?:执行|运行|启动|停止|中断|批准|删除|发送|发布|继续|创建|取消|修改|更新|通知|提醒|打开|重启|关闭|保存)", Options);
    private static readonly Regex Withdrawal = new(@"(?:[，,;；。\r\n]|但是|不过|可是|并且|而且)\s*(?:(?:现在|请|先|暂时|暂|还是|但|这次)\s*)*(?:(?:不要|别|不许|禁止|取消|暂不).{0,12}(?:提醒|安排|保存|执行|操作)|算了|作罢)", Options);

    internal static ReminderCommandResolution Resolve(string source, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeZone);
        DateTimeOffset resolvedAt = now.ToUniversalTime();
        ReminderCommandResolution Result(ReminderCommandStatus status, string message = "", string text = "",
            DateTimeOffset? due = null, string expression = "") => new(status, source, text, resolvedAt, due,
                timeZone.Id, due is { } utc ? TimeZoneInfo.ConvertTime(utc, timeZone) : null, expression, message);
        string command = source.Trim();
        if (!Candidate.IsMatch(command) || !command.Contains("提醒我", StringComparison.Ordinal))
            return Result(ReminderCommandStatus.None);
        if (source.Length > 4000 || source.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            return Result(ReminderCommandStatus.Rejected, "提醒请求过长或包含无法处理的字符，请重新明确一次提醒。");
        if (!TryUnquoted(command, out string visible) || command.Contains('`')
            || Regex.IsMatch(command, @"(?m)^\s*>", Options))
            return Result(ReminderCommandStatus.Rejected, "引用或代码的边界不明确，请直接说明一次提醒的时间与内容。");
        if (Recurring.IsMatch(visible))
            return Result(ReminderCommandStatus.NeedsClarification, "这里仅解析一次性提醒；请明确某一天的具体时间，不会把循环要求安排成一次提醒。");
        if (Conditions.IsMatch(visible) || Questions.IsMatch(visible) || ExtraAction.IsMatch(visible)
            || Withdrawal.IsMatch(visible) || Regex.Matches(visible, "提醒我", Options).Count != 1)
            return Result(ReminderCommandStatus.NeedsClarification, "这句话包含条件、疑问、撤回或多项操作，请单独明确一次提醒；提醒内容不会自动作为任务执行。");
        var match = Command.Match(command);
        if (!match.Success)
            return Result(ReminderCommandStatus.NeedsClarification, "请按“10分钟后提醒我看结果”或“明早9点提醒我看结果”说明时间与内容。");
        string expression = match.Groups["when"].Value.Trim();
        string content = match.Groups["text"].Value.Trim();
        if (content.Length is < 1 or > 1200 || content.All(char.IsPunctuation))
            return Result(ReminderCommandStatus.NeedsClarification, "请提供 1–1200 字符的完整提醒内容。", expression: expression);
        var relative = Relative.Match(expression);
        if (relative.Success || expression == "半小时后")
        {
            if (relative.Success && (!TryNumber(relative.Groups["amount"].Value, out int amount) || amount == 0))
                return Result(ReminderCommandStatus.NeedsClarification, "相对时间需要是大于零的完整时长。", expression: expression);
            double minutes = expression == "半小时后" ? 30
                : NumberValue(relative.Groups["amount"].Value) * (relative.Groups["unit"].Value switch { "小时" => 60d, "天" => 1440d, _ => 1d });
            try
            {
                // Duration is elapsed time. The host's localTime + zone contract
                // still cannot persist which repeated wall-clock occurrence was
                // meant, so fail closed until it also carries an explicit offset.
                var due = resolvedAt.AddMinutes(minutes);
                long fractionalTicks = due.Ticks % TimeSpan.TicksPerSecond;
                if (fractionalTicks != 0) due = due.AddTicks(TimeSpan.TicksPerSecond - fractionalTicks);
                if (timeZone.IsAmbiguousTime(TimeZoneInfo.ConvertTime(due, timeZone).DateTime))
                    return Result(ReminderCommandStatus.NeedsClarification, "相对时间落在夏令时重复的当地时刻；请换一个时间，以免安排到另一遍钟点。", expression: expression);
                return Result(ReminderCommandStatus.Ready, text: content, due: due, expression: expression);
            }
            catch (ArgumentOutOfRangeException)
            { return Result(ReminderCommandStatus.NeedsClarification, "时间超出可表示范围，请提供具体日期与时间。", expression: expression); }
        }
        DateTime localNow = TimeZoneInfo.ConvertTime(resolvedAt, timeZone).DateTime;
        DateTime date;
        string clockText, impliedPeriod = "";
        var day = Day.Match(expression);
        if (day.Success)
        {
            string label = day.Groups["day"].Value;
            try { date = localNow.Date.AddDays(label switch { "明天" or "明日" or "明早" or "明晚" => 1, "后天" => 2, "大后天" => 3, _ => 0 }); }
            catch (ArgumentOutOfRangeException)
            { return Result(ReminderCommandStatus.NeedsClarification, "日期超出可表示范围。", expression: expression); }
            clockText = day.Groups["clock"].Value.Trim();
            impliedPeriod = label == "明早" ? "早上" : label is "今晚" or "明晚" ? "晚上" : "";
        }
        else
        {
            var calendar = Date.Match(expression);
            if (!calendar.Success) calendar = ChineseDate.Match(expression);
            if (!calendar.Success)
                return Result(ReminderCommandStatus.NeedsClarification,
                    "请写明年份、日期和时间（例如“2026-10-22 09:00”），或使用“今天/明天/后天 09:00”；不会猜年份或自动顺延。", expression: expression);
            try
            {
                date = new DateTime(int.Parse(calendar.Groups["year"].Value, CultureInfo.InvariantCulture),
                    int.Parse(calendar.Groups["month"].Value, CultureInfo.InvariantCulture),
                    int.Parse(calendar.Groups["day"].Value, CultureInfo.InvariantCulture));
            }
            catch (ArgumentOutOfRangeException)
            { return Result(ReminderCommandStatus.NeedsClarification, "这个日期不存在，请核对年月日。", expression: expression); }
            clockText = calendar.Groups["clock"].Value.Trim();
        }
        if (!TryClock(clockText, impliedPeriod, out int hour, out int minute))
            return Result(ReminderCommandStatus.NeedsClarification, "请明确使用 00:00–23:59，或“上午9点/下午3点半”；时段与钟点不能冲突。", expression: expression);
        var local = DateTime.SpecifyKind(date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
            return Result(ReminderCommandStatus.NeedsClarification, "该时区因夏令时切换不存在这个当地时间，请换一个时间。", expression: expression);
        if (timeZone.IsAmbiguousTime(local))
            return Result(ReminderCommandStatus.NeedsClarification, "该时区因夏令时切换会出现两次这个当地时间，请改用明确的相对时长或另一个时间。", expression: expression);
        DateTimeOffset instant;
        try { instant = new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToUniversalTime(); }
        catch (ArgumentException)
        { return Result(ReminderCommandStatus.NeedsClarification, "无法把该当地时间转换为唯一时刻，请重新明确时间。", expression: expression); }
        if (instant <= resolvedAt)
            return Result(ReminderCommandStatus.NeedsClarification, "这个时间已经过去或就是现在，尚未安排；请明确新的未来时间。", expression: expression);
        return Result(ReminderCommandStatus.Ready, text: content, due: instant, expression: expression);
    }
    private static bool TryClock(string source, string impliedPeriod, out int hour, out int minute)
    {
        hour = minute = 0;
        var match = Clock.Match(source);
        if (!match.Success || !TryNumber(match.Groups["hour"].Value, out hour)) return false;
        string period = match.Groups["period"].Value;
        if (impliedPeriod.Length > 0)
        {
            if (period.Length > 0 && period != impliedPeriod && !(impliedPeriod == "早上" && period == "上午")) return false;
            period = impliedPeriod;
        }
        if (match.Groups["half"].Success) minute = 30;
        else if (match.Groups["minute"].Success && !TryNumber(match.Groups["minute"].Value, out minute)) return false;
        if (minute > 59 || hour > 23) return false;
        if (period is "早上" or "上午") return hour is >= 1 and <= 11;
        if (period == "中午") return hour is 11 or 12 or 13;
        if (period is "下午" or "晚上")
        {
            if (hour is >= 1 and <= 11) hour += 12;
            else if (hour == 12) return period == "下午"; // 晚上12点 needs an explicit date.
            else return false;
        }
        return true;
    }
    private static int NumberValue(string value) { _ = TryNumber(value, out int number); return number; }
    private static bool TryNumber(string value, out int number)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number)) return true;
        int Digit(char c) => c switch { '零' or '〇' => 0, '一' => 1, '二' or '两' => 2, '三' => 3, '四' => 4,
            '五' => 5, '六' => 6, '七' => 7, '八' => 8, '九' => 9, _ => -1 };
        if (value.Length == 1 && Digit(value[0]) >= 0) { number = Digit(value[0]); return true; }
        int ten = value.IndexOf('十');
        if (ten is 0 or 1 && value.Length is >= 1 and <= 3 && value.LastIndexOf('十') == ten)
        {
            int tens = ten == 0 ? 1 : Digit(value[0]);
            int units = ten == value.Length - 1 ? 0 : value.Length == ten + 2 ? Digit(value[^1]) : -1;
            if (tens is >= 1 and <= 9 && units >= 0) { number = tens * 10 + units; return true; }
        }
        number = 0; return false;
    }
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
        visible = result.ToString(); return closing.Count == 0;
    }
}
