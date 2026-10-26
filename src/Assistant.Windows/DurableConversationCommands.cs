using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal sealed partial class DurableAssistantRuntime
{
    private static bool IsConversationCommand(string text)
        => text is "新对话" or "当前任务" or "任务列表" or "选择任务"
            || text.StartsWith("切换到 ", StringComparison.Ordinal)
            || text.StartsWith("选择任务 ", StringComparison.Ordinal)
            || Regex.IsMatch(text, @"^(?:选择\s*)?[0-9]{1,3}$", RegexOptions.CultureInvariant);

    private async Task<DurableFastReply?> HandleConversationCommandAsync(DurableFastContext command, CancellationToken ct)
    {
        var message = command.Message;
        string text = message.Body.Trim();
        if (!IsConversationCommand(text)) return null;
        if (!command.StillCurrent()) throw new OperationCanceledException(ct);
        if (text == "新对话")
        {
            OwnedRuntime?.ClearFocus();
            await ledger.BeginNewDialogueAsync(message.Identity, message.SourceMessageId, ct);
            return new(["好，从这里重新聊。长期记忆和工作记录还在；已经交给任务的工作没有被中断。"]);
        }
        var context = await ledger.ReadConversationContextAsync(message.Identity, ct);
        if (text == "当前任务")
        {
            if (context.Focus is not { } focus) return new(["还没有选定当前任务。可以发“任务列表”来选。"]);
            var target = (await FocusChoicesAsync(ct)).Choices.SingleOrDefault(choice => SameTask(choice.Task, focus.Task));
            return new([target == null
                ? "当前任务暂时不在采集记录里，我保留了原指向，没有改选其他任务。"
                : $"当前说的是 {target.Label}。查询其他任务或收到通知不会自动切换它。" + ExistingTargetCommands(target)]);
        }
        if (Regex.IsMatch(text, @"^(?:选择\s*)?[0-9]{1,3}$", RegexOptions.CultureInvariant))
        {
            if (context.PendingSelection is not { } selection)
                return new(["现在没有待选择的任务列表。可以先发“任务列表”。"]);
            string number = Regex.Match(text, @"[0-9]+", RegexOptions.CultureInvariant).Value;
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || index < 1 || index > selection.Candidates.Length)
                return new(["这个编号不在刚才的列表里，请使用列表中的编号。"]);
            if (selection.ExpiresAt <= DateTimeOffset.UtcNow)
                return new(["刚才的任务列表已经过期，请重新发“任务列表”。"]);
            var chosen = selection.Candidates[index - 1];
            var target = (await FocusChoicesAsync(ct)).Choices.SingleOrDefault(choice => SameTask(choice.Task, chosen.Task));
            if (target == null)
                return new(["刚才那项任务已经不在当前记录中，没有改选同名任务。请重新查看任务列表。"]);
            try
            {
                await ledger.SelectTaskCandidateAsync(message.Identity, message.SourceMessageId,
                    selection.Id, index, text, ct);
                ApplyOwnedFocus(target);
            }
            catch (LedgerException ex) when (ex.Code == LedgerErrors.InvalidTransition)
            { return new(["刚才的选择已经失效，请重新查看任务列表。"]); }
            return new([$"已经选定 {target.Label}。这里只切换了交流目标，还没有发送工作指令。"]);
        }
        var (tasks, conflictCount) = await FocusChoicesAsync(ct);
        if (text is "任务列表" or "选择任务")
        {
            if (tasks.Length == 0) return new([conflictCount > 0
                ? "任务来源存在身份冲突，暂时不能从这些记录里选择。需要先核对采集来源。"
                : "当前还没有读到任务。"]);
            var choices = tasks.Take(20).Select(t => new TaskSelectionCandidate(
                t.Task, t.Label)).ToArray();
            await ledger.OpenTaskSelectionAsync(message.Identity, message.SourceMessageId,
                TaskSelectionPurpose.Focus, choices, TimeSpan.FromMinutes(10), ct);
            return new([string.Join("\n", choices.Select((choice, index) => $"{index + 1}. {choice.Label}"))
                + (tasks.Length > choices.Length ? "\n这里只列出前 20 项；也可以说“切换到 完整任务名称”。" : "")
                + (conflictCount > 0 ? "\n另有来源冲突的任务未列入可选项，需要先核对。" : "")
                + "\n回复编号可选定交流目标；选择本身不会执行工作。"]);
        }
        string query = text.StartsWith("切换到 ", StringComparison.Ordinal) ? text[4..].Trim() : text[5..].Trim();
        if (query.Length == 0) return new(["请带上任务的完整名称或编号。"]);
        var matches = tasks.Where(t => t.Aliases
            .Any(value => string.Equals(value, query, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length == 0) return new([conflictCount > 0
            ? "没有找到可以唯一确认的目标，部分采集记录存在身份冲突。请先核对任务列表和来源。"
            : "没有找到完全对应的任务。可以发“任务列表”查看，再选定原任务。"]);
        if (matches.Length == 1)
        {
            var target = matches[0];
            await ledger.SetWorkFocusAsync(message.Identity, message.SourceMessageId, target.Task, text, ct);
            ApplyOwnedFocus(target);
            return new([$"已经切换到 {target.Label}。还没有向任务发送新要求。"]);
        }
        var candidates = matches.Take(20).Select(t => new TaskSelectionCandidate(
            t.Task, t.Label)).ToArray();
        await ledger.OpenTaskSelectionAsync(message.Identity, message.SourceMessageId,
            TaskSelectionPurpose.Focus, candidates, TimeSpan.FromMinutes(10), ct);
        return new(["有几项任务符合这个名称，请选一个：\n"
            + string.Join("\n", candidates.Select((choice, index) => $"{index + 1}. {choice.Label}"))]);
    }

    private sealed record FocusChoice(TaskReference Task, string Label, string[] Aliases,
        string? OwnedConfigurationId = null);
    private async Task<(FocusChoice[] Choices, int Conflicts)> FocusChoicesAsync(CancellationToken ct)
    {
        var choices = new List<FocusChoice>();
        choices.AddRange(gateway.ListTasks().Select(task => new FocusChoice(
            new(task.Identity.HostId, task.Identity.ThreadId, task.Record.TurnId, task.Identity.ProfileId),
            Label(task), [task.Record.Title, task.Record.Id, task.Record.Project,
                TaskControlAssistant.Token(task.Record), task.Identity.HostId + "/" + task.Identity.ThreadId])));
        var owned = OwnedRuntime;
        var loaded = owned?.Tasks() ?? [];
        choices.AddRange(loaded.Select(task => new FocusChoice(
            new(task.Handle.Identity.HostId, task.Handle.Identity.ThreadId, task.Snapshot.CurrentTurnId, task.Handle.Identity.ProfileId),
            SingleLine(task.Snapshot.ProjectId, 70) + " · 自建 · "
                + SingleLine(task.Handle.Identity.HostId, 80) + " · " + task.Handle.Identity.ThreadId[..8],
            [task.Handle.Identity.ThreadId, task.ConfigurationId + "/" + task.Handle.Identity.ThreadId,
                task.Snapshot.ProjectId, task.Handle.Identity.HostId + "/" + task.Handle.Identity.ThreadId],
            task.ConfigurationId)));
        if (owned != null)
        {
            var catalogue = await owned.ReadCatalogueAsync(ct);
            choices.AddRange(catalogue.Tasks.Where(item => !loaded.Any(t => t.Handle.Identity == item.Fact.Task)).Select(item => new FocusChoice(
                new(item.Fact.Task.HostId, item.Fact.Task.ThreadId, ProfileId: item.Fact.Task.ProfileId),
                SingleLine(item.Fact.ProjectId, 70) + " · 自建 · 待刷新 · " + item.Fact.Task.ThreadId[..8],
                [item.Fact.Task.ThreadId, item.ConfigurationId + "/" + item.Fact.Task.ThreadId, item.Fact.ProjectId], item.ConfigurationId)));
        }
        var groups = choices.GroupBy(choice => (choice.Task.HostId, choice.Task.ThreadId, choice.Task.ProfileId)).ToArray();
        return (groups.Where(group => group.Count() == 1).Select(group => group.Single()).ToArray(), groups.Count(group => group.Count() > 1));
    }
    private void ApplyOwnedFocus(FocusChoice choice)
    {
        if (choice.OwnedConfigurationId == null) OwnedRuntime?.ClearFocus();
        else if (OwnedRuntime?.FindTask(choice.OwnedConfigurationId, choice.Task.ThreadId) != null)
            OwnedRuntime.SetFocus(choice.OwnedConfigurationId, choice.Task.ThreadId);
    }
    private string ExistingTargetCommands(FocusChoice choice)
    {
        if (choice.OwnedConfigurationId != null || ExistingRuntime is not { } runtime) return "";
        var read = gateway.ReadTask(new(choice.Task.HostId, choice.Task.ThreadId, choice.Task.ProfileId));
        return read.Value is { } task ? runtime.TargetCommands(task) : "\n当前采集来源无法唯一核对控制目标。";
    }
    private static bool SameTask(TaskReference left, TaskReference right)
        => left.HostId == right.HostId && left.ThreadId == right.ThreadId && left.ProfileId == right.ProfileId;

    private static string Label(GatewayTask task)
        => SingleLine(task.Record.Title, 100) + " · " + SingleLine(task.Record.Host, 45)
            + " · " + SingleLine(task.Record.ShortStatus, 35) + " [" + TaskControlAssistant.Token(task.Record) + "]";
    private static string SingleLine(string value, int limit)
        => Regex.Replace(new string(WeChatText.Clean(value, limit).Select(c => char.IsControl(c) ? ' ' : c).ToArray()),
            @"\s+", " ").Trim();
}
