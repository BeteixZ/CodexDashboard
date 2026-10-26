using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Assistant.Core;

namespace CodexDashboard;

public sealed class WeChatOptions
{
    public bool ClawBotEnabled { get; set; }
    public bool PushPlusEnabled { get; set; }
    public bool NotifyCompleted { get; set; } = true;
    public bool NotifyFailed { get; set; } = true;
    public bool NotifyWaiting { get; set; } = true;
    public bool IncludeSummary { get; set; }
    public bool ReportQueriesEnabled { get; set; } = true;
    public string ProtectedPushPlusToken { get; set; } = "";
    // A public route identity, never a token or a token-derived fingerprint.
    // Legacy settings remain empty until the host explicitly initializes and persists it.
    public Guid PushPlusWakeRouteId { get; set; }
    public string? PushPlusWakeQuietStart { get; set; }
    public string? PushPlusWakeQuietEnd { get; set; }
    public string? PushPlusWakeTimeZoneId { get; set; }
    public string ProtectedBinding { get; set; } = "";
    private readonly object pushTokenGate = new();
    // Bounded hashes only: no task titles or message bodies are persisted here.
    public List<string> RecentAttempts { get; set; } = new();
    public ClawBinding? ReadBinding() => string.IsNullOrEmpty(ProtectedBinding) ? null : JsonSerializer.Deserialize<ClawBinding>(LocalSecret.Unprotect(ProtectedBinding), Settings.Json);
    public void WriteBinding(ClawBinding binding) => ProtectedBinding = LocalSecret.Protect(JsonSerializer.Serialize(binding, Settings.Json));
    public string ReadPushPlusToken() => ProtectedPushPlusToken == null
        ? throw new FormatException("已保存的 pushplus 令牌字段无效。") : LocalSecret.Unprotect(ProtectedPushPlusToken);
    public bool EnsureWakeRouteId()
    {
        lock (pushTokenGate)
        {
            if (PushPlusWakeRouteId != Guid.Empty) return false;
            PushPlusWakeRouteId = Guid.NewGuid();
            return true;
        }
    }
    public bool WritePushPlusToken(string token)
    {
        string candidate = ValidatePushToken(token, allowEmpty: true);
        lock (pushTokenGate)
        {
            // An unreadable prior value cannot establish equality. Normal saves
            // must not silently replace it or rotate its durable route identity.
            if (string.Equals(ReadPushPlusToken(), candidate, StringComparison.Ordinal)) return false;
            string encrypted = LocalSecret.Protect(candidate);
            Guid route = Guid.NewGuid();
            ProtectedPushPlusToken = encrypted;
            PushPlusWakeRouteId = route;
            return true;
        }
    }
    public void ReplaceUnreadablePushPlusToken(string token)
    {
        string candidate = ValidatePushToken(token, allowEmpty: false);
        lock (pushTokenGate)
        {
            bool unreadable = false;
            try { _ = ReadPushPlusToken(); }
            catch (Exception error) when (error is FormatException or CryptographicException) { unreadable = true; }
            if (!unreadable) throw new InvalidOperationException("令牌可以读取，请使用普通保存；未执行不可读令牌替换。");
            string encrypted = LocalSecret.Protect(candidate);
            Guid route = Guid.NewGuid();
            ProtectedPushPlusToken = encrypted;
            PushPlusWakeRouteId = route;
        }
    }
    private static string ValidatePushToken(string token, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(token);
        string result = token.Trim();
        if ((!allowEmpty && result.Length == 0) || result.Length > 512 || result.Any(char.IsControl))
            throw new ArgumentException("请填写有效的 pushplus 令牌；替换无法读取的令牌时不能为空。");
        return result;
    }
    public bool TrySetWakeQuietHours(string? start, string? end, string? timeZoneId, out string? error)
    {
        if (!TryValidateWakeQuietHours(start, end, timeZoneId, out var quiet, out error)) return false;
        PushPlusWakeQuietStart = quiet?.Start;
        PushPlusWakeQuietEnd = quiet?.End;
        PushPlusWakeTimeZoneId = quiet?.TimeZoneId;
        return true;
    }
    public bool TryReadWakeQuietHours(out PushPlusWakeQuietHours? quiet, out string? error)
        => TryValidateWakeQuietHours(PushPlusWakeQuietStart, PushPlusWakeQuietEnd, PushPlusWakeTimeZoneId, out quiet, out error);
    private static bool TryValidateWakeQuietHours(string? start, string? end, string? timeZoneId,
        out PushPlusWakeQuietHours? quiet, out string? error)
    {
        quiet = null; error = null;
        if (string.IsNullOrEmpty(start) && string.IsNullOrEmpty(end) && string.IsNullOrEmpty(timeZoneId)) return true;
        if (start == null || end == null || timeZoneId == null || start.Length != 5 || end.Length != 5
            || start.Any(char.IsControl) || end.Any(char.IsControl) || timeZoneId.Length is < 1 or > 128
            || timeZoneId != timeZoneId.Trim() || timeZoneId.Any(char.IsControl)
            || !TimeOnly.TryParseExact(start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || !TimeOnly.TryParseExact(end, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until)
            || from == until)
        { error = "短唤醒安静时段需同时填写两个不同的有效时间（HH:mm）及有效时区；关闭时须一起清除。"; return false; }
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (Exception failure) when (failure is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { error = "短唤醒安静时段的时区无法识别；请修正设置，尚未按无安静时段处理。"; return false; }
        quiet = new(start, end, timeZoneId);
        return true;
    }
    public bool Wants(string status) => status switch
    {
        TaskStatusNames.Completed => NotifyCompleted,
        TaskStatusNames.Failed => NotifyFailed,
        TaskStatusNames.Waiting => NotifyWaiting,
        _ => false
    };
}

public sealed record PushPlusWakeQuietHours(string Start, string End, string TimeZoneId);

public sealed class ClawBinding
{
    public string Token { get; set; } = "";
    public string BotId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string BaseUrl { get; set; } = WeChatApi.DefaultBaseUrl;
    public string ContextToken { get; set; } = "";
    public string Cursor { get; set; } = "";
    public long LastContextMessageAt { get; set; }
    public List<string> SeenMessages { get; set; } = new();
}

// Windows DPAPI binds credentials to the current Windows user. No added secret file or key.
internal static class LocalSecret
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static string Protect(string value) => value.Length == 0 ? "" : Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value) => value.Length == 0 ? "" : Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, "CodexDashboard WeChat", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("无法读取微信凭据，请在当前 Windows 用户下重新绑定或填写令牌。");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (int i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

internal sealed record WeChatSnapshot(IReadOnlyList<TaskRecord> Tasks, DateTimeOffset? ReadAt, IReadOnlyList<HostSnapshot> Hosts);
internal sealed record CompletionNotice(string TaskKey, string TurnId, string ReportId, WeChatReport? Report);
internal sealed record WeChatNotification(string Key, string Title, string Body, DateTimeOffset CreatedAt,
    CompletionNotice? Completion = null, TaskReference? Task = null, string? NaturalBody = null);
internal sealed record WeChatReceipt(string Key, string Channel, bool? Accepted, string Detail);

internal static class WeChatText
{
    public static bool IsQuery(string text) => text is "帮助" or "help" or "开始" or "状态" or "概览" or "status" or "运行中" or "运行" or "需要关注" or "等待输入" or "最近完成" or "完成" or "报告" or "报告列表"
        || text.StartsWith("查看 ", StringComparison.Ordinal) || text.StartsWith("报告 ", StringComparison.Ordinal);
    public const string Help = "Codex 状态查询：\n状态：任务概览\n运行中：正在运行的任务\n需要关注：等待输入或失败\n最近完成：最近 24 小时本轮结束\n查看 关键词：按项目或任务名称查询\n报告：最近一轮最终回复\n报告 关键词：选择某任务报告\n报告列表：查看可用报告编号\n报告 编号 2：查看第 2 页\n帮助：显示指令\n以上指令只查询；本轮结束不表示整个项目完成。";
    public static string Clean(string? text, int max) => TextSafety.Clean(text, max);
    public static string Hash(string value) => TextSafety.Hash(value);
    public static WeChatNotification Notification(TaskRecord task, bool summary, DateTimeOffset now, bool reportsEnabled = false)
    {
        string turn = task.TurnId.Length > 0 ? task.TurnId : task.StartedAt.ToString("R", CultureInfo.InvariantCulture);
        string key = Hash(task.Key + "\n" + turn + "\n" + task.Status);
        var identity = CodexGateway.Identity(task);
        var reference = new TaskReference(identity.HostId, identity.ThreadId, task.TurnId, identity.ProfileId);
        string natural = task.Status switch
        {
            TaskStatusNames.Completed => $"“{Clean(task.Title, 100)}”这一轮结束了。",
            TaskStatusNames.Failed => $"“{Clean(task.Title, 100)}”运行失败了。可以查看进展，核对失败原因。",
            TaskStatusNames.Waiting => $"“{Clean(task.Title, 100)}”正在等待处理。需要在 Codex 中查看审批或补充输入。",
            _ => $"“{Clean(task.Title, 100)}”的状态是{task.StatusLabel}。"
        };
        if (task.Status == TaskStatusNames.Completed)
        {
            string content = summary || reportsEnabled ? Clean(task.OverviewText, 240) : "这一轮的处理已结束。";
            if (string.IsNullOrWhiteSpace(content)) content = "这一轮的处理已结束。";
            return new(key, "", content, now, new(task.Key, task.TurnId, WeChatReports.IdFor(task), WeChatReports.FromTask(task)), reference, natural);
        }
        string title = $"Codex · {task.ShortStatus} · {Clean(task.Project, 35)}";
        string header = $"{Clean(task.Host, 60)} / {Clean(task.Project, 60)}\n{Clean(task.Title, 150)}\n状态：{task.StatusLabel}\n时间：{now.ToLocalTime():MM-dd HH:mm:ss}";
        string body = header;
        if (summary) body += "\n进展：" + Clean(task.OverviewText, 240);
        return new(key, title, body, now, Task: reference, NaturalBody: natural);
    }
    public static string Reply(string command, WeChatSnapshot snapshot, bool summary, DateTimeOffset now, WeChatReports? reports = null, bool reportsEnabled = true)
    {
        command = command.Trim().TrimStart('/').Trim();
        if (command is "帮助" or "help" or "开始" or "你好") return Help;
        if (command is "报告" or "报告列表" || command.StartsWith("报告 ", StringComparison.Ordinal))
            return reportsEnabled ? (reports ?? new WeChatReports()).Reply(command, snapshot, now) : "报告查询已关闭；可在设置 → 微信通知中启用“完成时发送摘要，并允许查询本轮最终回复”。";
        string freshness = snapshot.ReadAt is DateTimeOffset read
            ? $"最近读取：{read.ToLocalTime():MM-dd HH:mm:ss}" + (now - read > TimeSpan.FromMinutes(2) ? "（数据已过期）" : "") : "尚未读取任务数据";
        string offline = string.Join("、", snapshot.Hosts.Where(h => h.Error != null).Select(h => Clean(h.Name, 40)));
        if (offline.Length > 0) freshness += "\n连接异常：" + offline + "（以下含上次数据）";
        if (command is "状态" or "概览" or "status")
            return $"Codex 状态\n运行 {snapshot.Tasks.Count(t => t.EffectiveStatus == TaskStatusNames.Active)} · 等待 {snapshot.Tasks.Count(t => t.EffectiveStatus == TaskStatusNames.Waiting)} · 失败 {snapshot.Tasks.Count(t => t.EffectiveStatus == TaskStatusNames.Failed)}\n{freshness}\n发送“帮助”查看查询指令。";
        IEnumerable<TaskRecord> tasks;
        if (command is "运行中" or "运行") tasks = snapshot.Tasks.Where(t => t.EffectiveStatus == TaskStatusNames.Active);
        else if (command == "需要关注") tasks = snapshot.Tasks.Where(t => t.Attention && now.ToUnixTimeSeconds() - t.LastActivity < 86400);
        else if (command == "等待输入") tasks = snapshot.Tasks.Where(t => t.EffectiveStatus == TaskStatusNames.Waiting);
        else if (command is "最近完成" or "完成") tasks = snapshot.Tasks.Where(t => t.EffectiveStatus == TaskStatusNames.Completed && t.CompletedAt > 0 && now.ToUnixTimeSeconds() - t.CompletedAt < 86400);
        else if (command.StartsWith("查看 ", StringComparison.Ordinal) && command.Length > 3)
        {
            string query = command[3..].Trim();
            tasks = snapshot.Tasks.Where(t => (t.Title + "\n" + t.Project + "\n" + t.Host).Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        else return Help;
        var ordered = tasks.OrderBy(t => t.Priority).ThenByDescending(t => t.LastActivity).ToList();
        var lines = ordered.Take(8).Select(t => $"• {Clean(t.Host, 35)}/{Clean(t.Project, 40)} · {t.ShortStatus}\n  {Clean(t.Title, 95)}" + (summary ? "\n  " + Clean(t.OverviewText, 160) : ""));
        return Clean($"{command}\n{freshness}\n" + (ordered.Count == 0 ? "暂无符合条件的任务。" : string.Join("\n", lines)) + (ordered.Count > 8 ? $"\n共 {ordered.Count} 项，显示前 8 项；可用“查看 关键词”缩小范围。" : "") + "\n依据会话记录；本轮结束不代表整个项目完成。", 3000);
    }
}
