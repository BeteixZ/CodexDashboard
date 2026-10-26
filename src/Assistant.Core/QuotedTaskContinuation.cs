namespace Assistant.Core;

// Only the new authenticated message supplies intent; quoted prose never does.
public static class QuotedTaskContinuation
{
    public static bool IsRequest(string text) => text.Trim().TrimEnd('。', '！', '!', '.') is
        "这个继续" or "继续这个" or "继续这个任务" or "接着这个任务" or "继续";
    public static bool SameTarget(TaskReference? left, TaskReference? right) => left != null && right != null
        && left.HostId == right.HostId && left.ThreadId == right.ThreadId && left.ProfileId == right.ProfileId;
}
