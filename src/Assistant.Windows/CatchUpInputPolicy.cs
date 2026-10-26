using System.Text.RegularExpressions;

namespace CodexDashboard;

/// <summary>
/// Presentation scheduling only. This cannot authorize a tool or resume a task.
/// Ambiguous restrictions postpone historical notices while current input is handled.
/// </summary>
internal static class CatchUpInputPolicy
{
    private static readonly Regex Restrictions = new(
        @"取消|暂停|停止|停下|停掉|停一|别|不要|不用|无需|不必|不许|禁止|不再|不发|不想|不看|不听|忽略|算了|免了|等一|等等|稍后|晚点|暂缓|暂时|先|只|新对话|清空|忘|删|修改|改成|改为|纠正|中断|审批|批准|拒绝|同意|授权|关闭|禁用|静音|安静|切换|继续|运行|执行|记住"
        + @"|\b(?:cancel|pause|stop|hold|wait|later|ignore|forget|delete|remove|edit|change|correct|interrupt|approve|reject|deny|allow|authorize|disable|mute|quiet|only|before|resume|run|execute|continue|switch)\b"
        + @"|\b(?:don['’]?t|do\s+not|no\s+more|new\s+(?:chat|conversation)|clear\s+(?:chat|history))\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        System.TimeSpan.FromMilliseconds(100));

    internal static bool AllowsHistory(string text, bool hasFreshChannelContext)
    {
        if (!hasFreshChannelContext || string.IsNullOrWhiteSpace(text) || text.Length > 4000)
            return false;
        text = text.Trim();
        if (CommitmentDetails.IsCandidate(text)
            || text.StartsWith("[无法处理的微信消息]", System.StringComparison.Ordinal))
            return false;
        try { return !Restrictions.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
