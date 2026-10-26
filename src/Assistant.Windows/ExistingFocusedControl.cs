using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assistant.Core;

namespace CodexDashboard;

internal sealed partial class ExistingControlRuntime
{
    internal static bool IsFocusedStop(string text)
        => text.Trim().TrimEnd('。', '！', '!', '.', ' ') is "先停一下" or "停止运行" or "中断当前任务" or "停止当前任务";
    internal static bool IsFocusedSteer(string text) => text.TrimStart().StartsWith("纠偏当前任务", StringComparison.Ordinal);

    internal ModelToolCall? FocusedCommand(string text, WorkFocus? focus)
    {
        bool steer = IsFocusedSteer(text);
        var instruction = steer ? Regex.Match(text, @"\A\s*纠偏当前任务\s*[:：](?<text>[\s\S]*)\z", RegexOptions.CultureInvariant) : null;
        if (steer && (instruction?.Success != true || string.IsNullOrWhiteSpace(instruction.Groups["text"].Value))) return null;
        if ((!steer && !IsFocusedStop(text)) || focus == null || readFocusedTask?.Invoke(focus.Task) is not { } task
            || task.Identity.HostId != focus.Task.HostId || task.Identity.ThreadId != focus.Task.ThreadId
            || task.Identity.ProfileId != focus.Task.ProfileId || !task.Fresh
            || task.Record.Status is not (TaskStatusNames.Active or TaskStatusNames.Waiting)
            || string.IsNullOrWhiteSpace(task.Record.TurnId)) return null;
        var matches = profiles.Values.Where(p => p.Configuration.Connection.Endpoint == task.Identity.Endpoint
            && p.Configuration.Directories.Contains(task.Record.Cwd)).ToArray();
        if (matches.Length != 1 || WriteFailure(matches[0], steer ? "turn/steer" : "turn/interrupt") != null) return null;
        return steer ? new("existing-focused", "steer_existing_turn", JsonSerializer.Serialize(new
            { configurationId = matches[0].Id, threadId = task.Identity.ThreadId, turnId = task.Record.TurnId, text = instruction!.Groups["text"].Value }))
            : ParseCommand("中断已有 " + matches[0].Id + "/" + task.Identity.ThreadId + "/" + task.Record.TurnId);
    }
}
