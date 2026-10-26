using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal sealed record CommitmentListPage(string Text, int Page, int Total, int? NextPage, bool Available = true);

internal sealed record CommitmentCommandResolution(bool Candidate, ModelToolCall? Call, string? Reply);

// Creates durable notification/read-only commitments. No path invokes a task
// transport, reconnects an owned server, approves a request or executes text.
internal sealed class CommitmentToolAdapter : IToolExecutor, IUserAuthorizationPolicy
{
    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private const string Prefix = @"(?:(?:请帮我|帮我|请)\s*)?";
    private const string Number = @"(?:[0-9]{1,9}|[零〇一二两三四五六七八九十]{1,3})";
    private static readonly string[] ListAliases = ["提醒列表", "订阅列表", "事项列表", "自动化列表", "你还记着哪些事"];
    private static readonly Regex ReminderLead = new(@"\A" + Prefix + @"(?:[0-9零〇一二两三四五六七八九十半]|今天|明天|明早|明晚|今晚|后天|大后天|明日|每|循环|反复|定时|提醒我)", Options);
    private static readonly Regex ChangeLead = new(@"\A" + Prefix + @"(?:暂停|恢复|取消|删除|修改|更新)(?:提醒|订阅|事项|自动化)", Options);
    private static readonly Regex Change = new(@"\A" + Prefix + @"(?<action>暂停|恢复|取消|删除)(?<kind>提醒|订阅|事项|自动化)\s+(?<id>[0-9A-Fa-f-]{32,36})[。!！]?\z", Options);
    private static readonly Regex Update = new(@"\A" + Prefix + @"(?:修改|更新)(?<kind>提醒|事项)\s+(?<id>[0-9A-Fa-f-]{32,36})\s+(?:(?:改为|改成)\s*)?(?<request>[\s\S]+)\z", Options);
    private static readonly Regex NamedChange = new(@"\A" + Prefix + @"(?<action>暂停|恢复|取消|删除)提醒\s+(?<name>[^\r\n]+?)\z", Options);
    private static readonly Regex NamedUpdate = new(@"\A" + Prefix + @"(?:修改|更新)提醒\s+(?<name>[^\r\n]+?)\s+(?:改为|改成)\s+(?<request>[^\r\n]+)\z", Options);
    private static readonly Regex PolicyLead = new(@"\A" + Prefix + @"(?:设置|清除)(?:提醒|订阅|事项)", Options);
    private static readonly Regex PolicyCommand = new(@"\A" + Prefix + @"(?<action>设置|清除)(?<kind>提醒|订阅|事项)\s+(?<id>[0-9A-Fa-f-]{32,36})\s+(?<rule>安静时段(?:\s+(?<start>[0-9]{2}:[0-9]{2})-(?<end>[0-9]{2}:[0-9]{2}))?|错过时跳过|恢复后提醒一次|需要我决定|每24小时最多(?<limit>[0-9]{1,3})次)[。!！]?\z", Options);
    private static readonly Regex VagueQuiet = new(@"\A" + Prefix + @"(?:晚上|夜里|夜间|今晚)(?:别打扰|不要打扰|别提醒我|别发消息|不要发消息)[。!！]?\z", Options);
    private static readonly Regex Subscription = new(@"\A" + Prefix + @"(?<target>.*?)(?<condition>跑完告诉我|完成后告诉我|失败才叫我|失败时叫我|需要决定时叫我|需要我决定时叫我|(?<amount>" + Number + @")\s*(?<unit>分钟|小时)没动静(?:就)?检查一下)[。!！]?\z", Options);
    private static readonly Regex UnsafeSubscription = new(@"[“”‘’「」『』""'`?？\r\n]|如果|假如|除非|否则|前提|引用|报告|示例|解释|不要|不许|禁止|先别|暂停|取消|然后|并且|顺便|每天|每周|每隔|循环|反复|是否|能否", Options);
    internal static ToolDescriptor ListDescriptor() => new("list_commitments", "分页读取当前绑定保存的提醒、订阅与原任务接续记录；按nextPage继续读取。",
        ToolSchema.Object(("page", new() { Type = ToolValueType.Integer, Minimum = 1, Maximum = 999999 }, false)), false, "list_commitments");
    private static readonly ToolCatalog Tools = new(CommitmentTools.Descriptors().Add(ListDescriptor()));
    private readonly Settings settings;
    private readonly DurableConversationLedger ledger;
    private readonly ICodexGateway gateway;
    private readonly TaskControlQueue queue;
    private readonly Func<OwnedControlRuntime?> owned;
    private readonly Func<ConversationIdentity, bool> currentBinding;
    private readonly Func<TimeZoneInfo> zone;
    private readonly Func<DateTimeOffset> clock;

    internal CommitmentToolAdapter(Settings settings, DurableConversationLedger ledger, ICodexGateway gateway,
        TaskControlQueue queue, Func<OwnedControlRuntime?> owned, Func<ConversationIdentity, bool> currentBinding,
        Func<TimeZoneInfo>? zone = null, Func<DateTimeOffset>? clock = null)
    {
        this.settings = settings; this.ledger = ledger; this.gateway = gateway; this.queue = queue;
        this.owned = owned; this.currentBinding = currentBinding;
        this.zone = zone ?? (() => TimeZoneInfo.Local); this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }
    internal static bool Handles(string name) => CommitmentTools.Handles(name) || name == "list_commitments";
    internal static bool IsCandidate(string text)
    {
        string command = text.Trim();
        return TryListPage(command, out _) || TryReminderSearch(command, out _, out _) || ChangeLead.IsMatch(command) || PolicyLead.IsMatch(command) || VagueQuiet.IsMatch(command)
            || Subscription.IsMatch(command)
            || ReminderLead.IsMatch(command) && command.Contains("提醒我", StringComparison.Ordinal);
    }
    public ToolCapability GetCapability(string capability) => capability switch
    {
        "schedule" or "change_commitment" or "list_commitments" => new(true),
        "subscribe" => new(settings.ChatGpt.IncludeTaskContext, "任务上下文已关闭，尚未创建订阅。"),
        _ => new(false, "不支持此功能。")
    };
    internal async Task<CommitmentCommandResolution> ResolveAsync(InboxMessage proposed, CancellationToken ct = default)
    {
        if (!IsCandidate(proposed.Body)) return new(false, null, null);
        var snapshot = await ledger.ReadConversationAsync(proposed.Identity, ct);
        var source = Current(snapshot, proposed.SourceMessageId, proposed.Revision);
        if (source == null || source with { Stage = proposed.Stage, GenerationId = proposed.GenerationId } != proposed)
            return Clarify("当前消息或微信绑定已经变化，未安排提醒或订阅。");
        if (TryListPage(source.Body, out int page))
            return new(true, null, await FormatListAsync(source.Identity, ct, page));
        if (TryReminderSearch(source.Body, out string query, out int searchPage))
            return new(true, null, await FormatReminderSearchAsync(source.Identity, query, searchPage, ct));
        if (snapshot.IsReadOnly) return Clarify("持久记录需要恢复，尚未安排或修改事项。");
        return ResolveCurrent(source, snapshot);
    }
    private CommitmentCommandResolution ResolveCurrent(InboxMessage source, ConversationSnapshot snapshot)
    {
        string text = source.Body.Trim();
        if (VagueQuiet.IsMatch(text))
            return Clarify("请明确要设置哪一项及起止时间，例如“设置事项 完整编号 安静时段 22:00-08:00”。尚未更改全局或任何事项规则。");
        if (PolicyLead.IsMatch(text)) return ResolvePolicy(source, snapshot, text);
        var update = Update.Match(text);
        var change = Change.Match(text);
        if (!update.Success && !change.Success)
        {
            var namedUpdate = NamedUpdate.Match(text);
            var named = namedUpdate.Success ? namedUpdate : NamedChange.Match(text);
            if (named.Success)
            {
                string name = named.Groups["name"].Value.Trim();
                var matches = snapshot.WorkItems.Where(work => work.Kind == TrackedWorkKind.Reminder
                    && work.Stage is not (TrackedWorkStage.Completed or TrackedWorkStage.Cancelled or TrackedWorkStage.Expired)
                    && string.Equals(work.Goal, name, StringComparison.Ordinal)).ToArray();
                if (matches.Length == 0) return Clarify("没有找到内容为“" + name + "”的未结束提醒。发“提醒列表”核对内容或编号。");
                if (matches.Length > 1) return Clarify("有多条“" + name + "”提醒，请用编号指定：\n"
                    + string.Join("\n", matches.Take(8).Select(work => work.Id.ToString("D") + "  " + Stage(work)))
                    + (matches.Length > 8 ? "\n更多记录请发“提醒列表”。" : ""));
                string command = namedUpdate.Success
                    ? "修改提醒 " + matches[0].Id.ToString("D") + " " + namedUpdate.Groups["request"].Value
                    : named.Groups["action"].Value + "提醒 " + matches[0].Id.ToString("D");
                update = Update.Match(command);
                change = Change.Match(command);
            }
        }
        if (ChangeLead.IsMatch(text))
        {
            var match = update.Success ? update : change;
            if (!match.Success || text.Any(char.IsControl) || !TryId(match.Groups["id"].Value, out var id))
                return Clarify("提醒可以写完整内容或编号，例如“取消提醒 喝水”“修改提醒 喝水 改为 明早9点提醒我喝水”。订阅请使用完整编号；不按数字选择。");
            var work = snapshot.WorkItems.SingleOrDefault(w => w.Id == id);
            if (work == null) return Clarify("当前绑定没有这条事项，请先查看“事项列表”核对完整编号。");
            if (!KindMatches(match.Groups["kind"].Value, work)) return Clarify("编号对应的事项类型与指令不符，请查看事项列表后重新明确。");
            string action = update.Success ? "update" : change.Groups["action"].Value switch { "暂停" => "pause", "恢复" => "resume", _ => "cancel" };
            if (action == "pause" && work.Stage is not (TrackedWorkStage.Active or TrackedWorkStage.Triggered)
                || action == "resume" && work.Stage != TrackedWorkStage.Paused
                || (action is "update" or "cancel") && work.Stage is (TrackedWorkStage.Completed or TrackedWorkStage.Cancelled or TrackedWorkStage.Expired))
                return Clarify("这条事项当前为“" + Stage(work) + "”，不能按这条指令改变；历史记录仍保留。");
            if (!update.Success) return Proposal("change_commitment", new { workItemId = id.ToString("D"), expectedVersion = work.Version, action });
            var schedule = snapshot.Schedules.SingleOrDefault(s => s.WorkItemId == id);
            if (work.Kind != TrackedWorkKind.Reminder || schedule == null || schedule.FiredRunId != null)
                return Clarify("只可修改尚未触发的一次性提醒；已触发事项请先核对记录，再明确取消或新建。");
            var reminder = ReminderCommandResolver.Resolve(update.Groups["request"].Value, source.ReceivedAt, zone());
            if (!reminder.IsReady) return Clarify(reminder.Message.Length > 0 ? reminder.Message : "请提供新的完整提醒表达。");
            return Proposal("change_commitment", new { workItemId = id.ToString("D"), expectedVersion = work.Version,
                action, text = reminder.Text, localTime = reminder.LocalTime, timeZoneId = reminder.TimeZoneId,
                missedPolicy = PolicyName(schedule.MissedPolicy) });
        }
        var subscription = Subscription.Match(text);
        if (subscription.Success)
        {
            if (!settings.ChatGpt.IncludeTaskContext) return Clarify("任务上下文已关闭，尚未创建订阅。");
            if (text.Length > 4000 || UnsafeSubscription.IsMatch(text) || text.Any(char.IsControl))
                return Clarify("请直接说明一项订阅，不从引用、条件或多项操作中提取授权。");
            string condition = subscription.Groups["condition"].Value;
            string kind = condition.Contains("没动静", StringComparison.Ordinal) ? "noActivity"
                : condition.StartsWith("失败", StringComparison.Ordinal) ? "failed"
                : condition.StartsWith("需要", StringComparison.Ordinal) ? "needsDecision" : "completed";
            int? inactivitySeconds = null;
            if (kind == "noActivity")
            {
                var duration = ReminderCommandResolver.Resolve(subscription.Groups["amount"].Value + subscription.Groups["unit"].Value
                    + "后提醒我检查", DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc);
                double seconds = duration.DueAtUtc is { } due ? (due - duration.ResolvedAtUtc).TotalSeconds : 0;
                if (seconds is < 1 or > 604800) return Clarify("无活动检查间隔需在 1 秒至 7 天之间，请重新明确时长。");
                inactivitySeconds = (int)seconds;
            }
            var target = ResolveTarget(source, snapshot.Context, subscription.Groups["target"].Value.Trim(), out string reason);
            if (target == null) return Clarify(reason);
            var args = new Dictionary<string, object?> { ["hostId"] = target.HostId, ["threadId"] = target.ThreadId,
                ["turnId"] = target.TurnId, ["condition"] = kind };
            if (target.ProfileId != null) args["profileId"] = target.ProfileId;
            if (inactivitySeconds != null) args["inactivitySeconds"] = inactivitySeconds.Value;
            return Proposal("subscribe", args);
        }
        var parsed = ReminderCommandResolver.Resolve(source.Body, source.ReceivedAt, zone());
        if (!parsed.IsReady) return Clarify(parsed.Message.Length > 0 ? parsed.Message : "请明确一次性提醒的时间与内容。");
        return Proposal("schedule", new { text = parsed.Text, localTime = parsed.LocalTime, timeZoneId = parsed.TimeZoneId, missedPolicy = "notifyOnce" });
    }
    private CommitmentCommandResolution ResolvePolicy(InboxMessage source, ConversationSnapshot snapshot, string text)
    {
        var match = PolicyCommand.Match(text);
        if (!match.Success || text.Any(char.IsControl) || !TryId(match.Groups["id"].Value, out var id))
            return Clarify("请使用完整事项编号和一项明确规则：安静时段 22:00-08:00、每24小时最多3次，或提醒的错过时间处理；未更改规则。");
        var work = snapshot.WorkItems.SingleOrDefault(w => w.Id == id);
        if (work == null || !KindMatches(match.Groups["kind"].Value, work))
            return Clarify("当前绑定没有这项匹配类型的记录，请先查看事项列表核对完整编号。");
        if (work.Stage is not (TrackedWorkStage.Active or TrackedWorkStage.Paused or TrackedWorkStage.Triggered))
            return Clarify("这条事项当前为“" + Stage(work) + "”，未更改规则；历史记录仍保留。");
        string action = match.Groups["action"].Value, rule = match.Groups["rule"].Value;
        var policy = work.Policy;
        if (rule.StartsWith("安静时段", StringComparison.Ordinal))
        {
            if (action == "清除")
            {
                if (match.Groups["start"].Success) return Clarify("清除安静时段不同时设置新时间，请只写“清除事项 完整编号 安静时段”。");
                policy = policy with { QuietStart = null, QuietEnd = null, TimeZoneId = null };
            }
            else
            {
                if (!TimeOnly.TryParseExact(match.Groups["start"].Value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                    || !TimeOnly.TryParseExact(match.Groups["end"].Value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || start == end)
                    return Clarify("安静时段需要不同的有效起止时间，格式为 HH:mm-HH:mm；可跨午夜。");
                policy = policy with { QuietStart = start.ToString("HH:mm", CultureInfo.InvariantCulture),
                    QuietEnd = end.ToString("HH:mm", CultureInfo.InvariantCulture), TimeZoneId = zone().Id };
            }
        }
        else if (match.Groups["limit"].Success)
        {
            if (action != "设置" || !int.TryParse(match.Groups["limit"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
                || limit is < 1 or > 100) return Clarify("每项每24小时次数需在1至100之间；仍受当前绑定的共享通知预算限制。");
            policy = policy with { MaximumNotificationsPer24Hours = limit };
        }
        else
        {
            var schedule = snapshot.Schedules.SingleOrDefault(s => s.WorkItemId == id);
            if (action != "设置" || match.Groups["kind"].Value != "提醒" || work.Kind != TrackedWorkKind.Reminder
                || schedule == null || schedule.FiredRunId != null || schedule.DueAtUtc <= source.ReceivedAt)
                return Clarify("只能设置尚未触发、尚未到期提醒的错过时间处理；未改动其他事项。");
            string missed = rule switch { "错过时跳过" => "skip", "恢复后提醒一次" => "notifyOnce", _ => "needsDecision" };
            return Proposal("change_commitment", new { workItemId = id.ToString("D"), expectedVersion = work.Version,
                action = "update", missedPolicy = missed });
        }
        return Proposal("change_commitment", new { workItemId = id.ToString("D"), expectedVersion = work.Version,
            action = "update", policy = PolicyArguments(policy) });
    }
    private static Dictionary<string, object> PolicyArguments(CommitmentPolicy policy)
    {
        var args = new Dictionary<string, object>
        {
            ["minimumIntervalSeconds"] = policy.MinimumIntervalSeconds,
            ["maximumNotificationsPer24Hours"] = policy.MaximumNotificationsPer24Hours,
            ["maximumCheckAttempts"] = policy.MaximumCheckAttempts,
            ["maximumModelPreparations"] = policy.MaximumModelPreparations,
            ["maximumDeliveryAttempts"] = policy.MaximumDeliveryAttempts
        };
        if (policy.QuietStart != null) args["quietStart"] = policy.QuietStart;
        if (policy.QuietEnd != null) args["quietEnd"] = policy.QuietEnd;
        if (policy.TimeZoneId != null) args["timeZoneId"] = policy.TimeZoneId;
        return args;
    }
    private static bool KindMatches(string kind, TrackedWorkItem work) => kind is "事项" or "自动化"
        || kind == "提醒" && work.Kind == TrackedWorkKind.Reminder || kind == "订阅" && work.Kind == TrackedWorkKind.Subscription;
    private TaskReference? ResolveTarget(InboxMessage source, ConversationContext? context, string target, out string reason)
    {
        reason = "没有唯一且仍活跃的任务轮次。请先选择任务，或明确主机与任务完整编号；不会借用历史报告或已结束轮次。";
        var tasks = ObservedTasks(source.Identity);
        bool explicitFocus = target is "当前任务" or "当前这个任务";
        bool implicitFocus = target is "" or "这个" or "这个任务" or "它" or "这项任务";
        TaskReference? reference = null;
        if (explicitFocus || implicitFocus)
        {
            if (!explicitFocus && source.Task != null) reference = source.Task;
            else
            {
                if (context?.Focus == null || context.PendingSelection != null) return null;
                reference = context.Focus.Task;
                if (!explicitFocus && context.LastNotification != null
                    && (context.LastNotification.Task == null || !SameIdentity(reference, context.LastNotification.Task)
                        || context.LastNotification.Task.TurnId != reference.TurnId))
                { reason = "当前交流目标与最近通知不一致，请明确说“当前任务”或提供主机和任务完整编号。"; return null; }
            }
        }
        var matches = reference != null ? tasks.Where(t => SameIdentity(t.Task, reference)
                && (reference.TurnId == null || t.Task.TurnId == reference.TurnId)).ToArray()
            : tasks.Where(t => t.Aliases.Contains(target, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || !matches[0].Fresh || !matches[0].Active || string.IsNullOrWhiteSpace(matches[0].Task.TurnId)) return null;
        return matches[0].Task;
    }
    private sealed record ObservedTask(TaskReference Task, string[] Aliases, bool Fresh, bool Active);
    private ObservedTask[] ObservedTasks(ConversationIdentity identity)
    {
        var result = new List<ObservedTask>();
        DateTimeOffset now = clock();
        bool Fresh(DateTimeOffset? at) => at is { } value && value <= now.AddSeconds(30) && now - value <= TimeSpan.FromMinutes(2);
        foreach (var task in gateway.ListTasks())
        {
            var id = task.Identity;
            result.Add(new(new(id.HostId, id.ThreadId, task.Record.TurnId, id.ProfileId),
                Aliases([task.Record.Title, task.Record.Project, id.ThreadId], [task.Record.Host, id.HostId]),
                task.Fresh && Fresh(task.ObservedAt) && !task.Record.Disconnected,
                task.Record.Status is TaskStatusNames.Active or TaskStatusNames.Waiting));
        }
        var runtime = owned();
        if (runtime is { Active: true } && runtime.Identity == identity)
            foreach (var task in runtime.Tasks())
            {
                var id = task.Handle.Identity;
                result.Add(new(new(id.HostId, id.ThreadId, task.Snapshot.CurrentTurnId, id.ProfileId),
                    Aliases([id.ThreadId, task.Snapshot.ProjectId], [id.HostId, task.ConfigurationId, runtime.Profile(task.ConfigurationId)?.Label ?? ""]),
                    runtime.Failure == null && task.Handle.Access == OwnedTaskAccess.CreatedThisSession
                        && task.EvidenceSource == "owned_app_server_last_observation" && Fresh(task.ObservedAt),
                    task.Snapshot.State is OwnedTaskState.Running or OwnedTaskState.WaitingApproval));
            }
        return result.ToArray();
    }
    private static string[] Aliases(string[] labels, string[] hosts)
    {
        var valid = labels.Where(label => label.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return valid.Concat(hosts.Where(host => host.Length > 0).SelectMany(host => valid.SelectMany(label =>
            new[] { host + " " + label, host + "/" + label, host + "的" + label }))).ToArray();
    }
    public async ValueTask<UserAuthorizationDecision> AuthorizeAsync(UserAuthorizationRequest request, CancellationToken ct)
    {
        if (!CommitmentTools.Handles(request.Call.Name) || !GetCapability(request.Call.Name).Available
            || !Canonical(request.Call, out string canonical)) return Denied("不支持此操作或参数不完整。");
        var resolved = await ResolveAsync(request.CurrentMessage, ct);
        if (resolved.Call == null || resolved.Call.Name != request.Call.Name || resolved.Call.ArgumentsJson != canonical)
            return Denied(resolved.Reply ?? "工具参数与当前完整原文不一致，未安排事项。");
        var snapshot = await ledger.ReadConversationAsync(request.CurrentMessage.Identity, ct);
        if (Current(snapshot, request.CurrentMessage.SourceMessageId, request.CurrentMessage.Revision) == null || snapshot.IsReadOnly)
            return Denied("当前消息或绑定已变化。");
        return new(true, CommitmentTools.TargetKey(request.Call.Name, canonical), request.CurrentMessage.Body,
            TaskForCall(resolved.Call, snapshot));
    }
    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Handles(call.Name) || !GetCapability(call.Name).Available || !Canonical(call, out string canonical)
            || !currentBinding(context.Identity)) return Rejected("commitment_unavailable_or_invalid");
        var snapshot = await ledger.ReadConversationAsync(context.Identity, ct);
        var invocation = snapshot.ToolInvocations.SingleOrDefault(i => i.Id == context.InvocationId);
        if (invocation == null || invocation.CallId != call.Id || invocation.ToolName != call.Name
            || invocation.ArgumentsJson != canonical || invocation.Identity != context.Identity
            || invocation.SourceMessageId != context.SourceMessageId || invocation.Revision != context.Revision)
            return Rejected("commitment_invocation_mismatch");
        if (call.Name == "list_commitments")
        {
            if (invocation.HasSideEffects || invocation.Stage != ToolInvocationStage.Executing
                || context.OperationId != null || context.IdempotencyKey != null)
                return Rejected("read_only_commitment_context_required");
            if (context.InputWorkId != null || context.Revision != snapshot.Revision)
            {
                if (!await ledger.VerifyReadOnlyWorkExecutionAsync(context, ct)) return Rejected("current_read_only_work_required");
            }
            else
            {
                var listSource = Current(snapshot, context.SourceMessageId, context.Revision);
                var generation = snapshot.Generations.SingleOrDefault(g => g.Id == invocation.GenerationId);
                if (listSource == null || context.ReadAtRevision != null || generation == null
                    || generation.Stage != GenerationStage.Processing || generation.InputWorkId != null
                    || generation.Revision != context.Revision || !generation.InboxIds.Contains(listSource.Id))
                    return Rejected("commitment_source_changed");
            }
            using var args = JsonDocument.Parse(canonical);
            int page = args.RootElement.TryGetProperty("page", out var requestedPage) ? requestedPage.GetInt32() : 1;
            var result = await ReadListAsync(context.Identity, ct, page);
            return new(ToolExecutionState.ReadOnlySuccess, ToolResultJson.Element(new
            { text = result.Text, page = result.Page, total = result.Total, nextPage = result.NextPage,
                actualRecords = result.Available, taskExecutionAllowed = false }));
        }
        var source = Current(snapshot, context.SourceMessageId, context.Revision);
        var operation = snapshot.Operations.SingleOrDefault(o => o.Id == context.OperationId);
        var grant = operation == null ? null : snapshot.Authorizations.SingleOrDefault(a => a.Id == operation.AuthorizationId);
        if (snapshot.IsReadOnly || source == null || operation == null || grant == null
            || context.InputWorkId != null || context.ReadAtRevision != null || !invocation.HasSideEffects
            || invocation.Stage != ToolInvocationStage.Executing || invocation.OperationId != operation.Id
            || operation.Stage != OperationStage.Dispatching || operation.Action != call.Name
            || operation.ArgumentsJson != canonical || operation.TargetKey != CommitmentTools.TargetKey(call.Name, canonical)
            || operation.SourceMessageId != source.SourceMessageId || operation.Revision != context.Revision
            || operation.IdempotencyKey != context.IdempotencyKey || grant.SourceExcerpt != source.Body
            || grant.SourceMessageId != source.SourceMessageId || grant.Revision != context.Revision
            || grant.Action != call.Name || grant.ArgumentsJson != canonical || grant.TargetKey != operation.TargetKey)
            return Rejected("durable_commitment_authorization_required");
        var resolved = ResolveCurrent(source, snapshot);
        if (resolved.Call?.Name != call.Name || resolved.Call.ArgumentsJson != canonical
            || TaskForCall(resolved.Call, snapshot) != operation.Task || operation.Task != grant.Task)
            return Rejected("commitment_target_changed");
        ct.ThrowIfCancellationRequested();
        if (!currentBinding(context.Identity)) return Rejected("commitment_binding_changed");
        try { return await ledger.ApplyCommitmentToolAsync(context, ct); }
        catch (LedgerException error) { return Rejected(error.Code); }
    }
    internal static bool TryListPage(string input, out int page)
    {
        page = 1;
        var match = Regex.Match(input.Trim().TrimEnd('？', '?'), @"\A(?:" + string.Join("|", ListAliases) + @")(?:\s+(?<page>[1-9][0-9]{0,5}))?\z", Options);
        if (!match.Success) return false;
        if (match.Groups["page"].Success) page = int.Parse(match.Groups["page"].Value, CultureInfo.InvariantCulture);
        return true;
    }
    internal async Task<string> FormatListAsync(ConversationIdentity identity, CancellationToken ct = default, int page = 1)
        => (await ReadListAsync(identity, ct, page)).Text;
    internal static bool TryReminderSearch(string input, out string query, out int page)
    {
        var match = Regex.Match(input.Trim(), @"\A查提醒\s+(?<query>[^\r\n]{1,200}?)(?:\s+第(?<page>[1-9][0-9]{0,5})页)?\z", Options);
        query = match.Success ? match.Groups["query"].Value.Trim() : "";
        page = match.Groups["page"].Success ? int.Parse(match.Groups["page"].Value, CultureInfo.InvariantCulture) : 1;
        return match.Success && query.Length > 0;
    }
    internal async Task<string> FormatReminderSearchAsync(ConversationIdentity identity, string query, int page, CancellationToken ct)
        => (await ReadListAsync(identity, ct, page, query)).Text;
    private async Task<CommitmentListPage> ReadListAsync(ConversationIdentity identity, CancellationToken ct, int page, string? reminderQuery = null)
    {
        if (!currentBinding(identity)) return new("当前微信绑定已变化，没有读取其他绑定的事项。", page, 0, null, false);
        var saved = await ledger.GetCommitmentsAsync(identity, ct);
        var budget = await ledger.GetCommitmentBudgetStatusAsync(identity, ct);
        if (!currentBinding(identity)) return new("当前微信绑定已变化，没有读取其他绑定的事项。", page, 0, null, false);
        var works = saved.WorkItems.Where(work => reminderQuery == null || work.Kind == TrackedWorkKind.Reminder
            && work.Goal.Contains(reminderQuery, StringComparison.OrdinalIgnoreCase)).ToArray();
        var pending = queue.Entries.Where(e => reminderQuery == null && (TaskControlQueue.IsPending(e) || e.State is "sending" or "unconfirmed")).ToArray();
        string pageCommand = reminderQuery == null ? "事项列表 " : "查提醒 " + reminderQuery + " 第";
        string pageSuffix = reminderQuery == null ? "" : "页";
        int total = works.Length + pending.Length;
        if (total == 0) return new(reminderQuery == null ? "当前没有已保存的提醒、订阅或待接续指令。" : "没有找到包含“" + reminderQuery + "”的提醒。", 1, 0, null);
        int pages = (total + 7) / 8;
        if (page > pages) return new("只有 " + pages + " 页，请发“" + pageCommand + "1" + pageSuffix + "”重新查看。", page, total, null);
        int start = (page - 1) * 8;
        var entries = new List<string>();
        var text = new StringBuilder();
        foreach (var work in works.OrderByDescending(w => w.UpdatedAt).ThenBy(w => w.Id).Skip(start).Take(8))
        {
            text.Clear();
            var schedule = saved.Schedules.SingleOrDefault(s => s.WorkItemId == work.Id);
            string due = schedule == null ? "" : " · " + schedule.LocalTime.Replace('T', ' ') + " [" + schedule.TimeZoneId + "]";
            text.Append('[').Append(work.Id.ToString("D")).Append("] ").Append(work.Kind == TrackedWorkKind.Reminder ? "提醒" : "订阅")
                .Append(" · ").Append(Stage(work)).Append(due).Append('\n').Append(Clean(work.Goal, 180)).Append('\n');
            text.Append("规则：").Append(PolicySummary(work, schedule)).Append('\n');
            if (work.Task != null) text.Append("轮次：").Append(work.Task.HostId).Append(" / ").Append(work.Task.ThreadId).Append(" / ").Append(work.Task.TurnId).Append('\n');
            entries.Add(text.ToString());
        }
        foreach (var entry in pending.Skip(Math.Max(0, start - works.Length)).Take(8 - entries.Count))
        {
            text.Clear();
            text.Append('[').Append(entry.Id).Append("] 原任务接续 · ").Append(entry.State switch
            { "sending" => "投递中", "unconfirmed" => "结果未知，不自动重发", "queued" => "等待投递", _ => "等待核对" })
                .Append('\n').Append(Clean(entry.Detail, 160)).Append('\n');
            entries.Add(text.ToString());
        }
        text.Clear().Append("第 ").Append(page).Append('/').Append(pages).Append(" 页，共 ").Append(total).Append(" 项。\n");
        foreach (var entry in entries) text.Append(entry);
        int? next = page < pages ? page + 1 : null;
        if (next != null) text.Append("下一页：").Append(pageCommand).Append(next).Append(pageSuffix).Append('\n');
        var daily = budget.Windows.Single(w => w.Kind == CommitmentBudgetKind.Notification && w.Window == TimeSpan.FromDays(1));
        text.Append("同一绑定的提醒与订阅通知还受共享预算限制：滚动24小时最多").Append(daily.Limit).Append("次。\n");
        text.Append("提醒可按完整内容暂停、恢复或取消，例如“取消提醒 喝水”；同名多条时请指定编号。订阅和规则设置请写事项完整编号；删除保留历史记录。接续记录另用“取消接续”。接口受理不等于手机已收到。");
        return currentBinding(identity) ? new(text.ToString(), page, total, next) : new("当前微信绑定已变化，没有读取其他绑定的事项。", page, 0, null, false);
    }
    internal static string DescribeReceipt(ModelToolCall call, ToolExecutionResult result)
    {
        if (result.State != ToolExecutionState.Completed) return result.State == ToolExecutionState.Unknown
            ? "这次操作结果还不能确认，我不会自动重试。" : "这次未确认安排成功，请先查看事项列表。";
        var facts = result.Facts;
        string id = facts.TryGetProperty("workItemId", out var item) ? item.GetString() ?? "" : "";
        if (call.Name == "change_commitment")
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            string action = document.RootElement.GetProperty("action").GetString()!;
            if (action == "pause") return "已暂停这项提醒或订阅。编号 " + id + "。任务没有被中断，已发送消息不会撤回。";
            if (action == "cancel") return "已取消这项提醒或订阅。编号 " + id + "。历史记录保留，没有物理删除。任务没有被中断，已发送或结果未知的消息仍保留原状态。";
            if (action == "resume") return "已恢复这项提醒或订阅。编号 " + id + "。只会通知或做只读检查。";
            if (action == "update")
            {
                var args = document.RootElement;
                if (args.TryGetProperty("policy", out _) && facts.TryGetProperty("policy", out var savedPolicy))
                {
                    var policy = JsonSerializer.Deserialize<CommitmentPolicy>(savedPolicy.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (policy != null) return "已更新这项事项的通知规则。编号 " + id + "。当前规则：" + PolicySummary(policy)
                        + "。仍受当前绑定的共享预算限制，不代表一定能够发送。";
                }
                if (args.TryGetProperty("missedPolicy", out var missed) && !args.TryGetProperty("localTime", out _))
                    return "已更新这项提醒的错过时间处理：" + MissedSummary(missed.GetString()!) + "。编号 " + id + "。原提醒时间和内容保留。";
                if (facts.TryGetProperty("localTime", out var updatedLocal) && updatedLocal.ValueKind == JsonValueKind.String)
                    return "提醒已改到 " + ReminderTime(updatedLocal.GetString()!, facts.GetProperty("timeZoneId").GetString()!)
                        + "。\n编号：" + id;
                return "事项修改已保存。编号 " + id + "。请查看事项列表核对实际规则。";
            }
        }
        if (facts.TryGetProperty("localTime", out var local) && local.ValueKind == JsonValueKind.String)
            return "安排好了，" + ReminderTime(local.GetString()!, facts.GetProperty("timeZoneId").GetString()!)
                + " 提醒你。记得让电脑上的助手保持运行；暂时发不出会留待补报。\n编号：" + id;
        return "订阅好了，会跟进刚核对的这一轮任务，只通知或查询，不会替你继续或中断任务。"
            + "助手需要保持运行；暂时发不出会留待补报。\n编号：" + id;
    }
    private static string ReminderTime(string localTime, string timeZoneId)
    {
        var time = DateTime.Parse(localTime, CultureInfo.InvariantCulture, DateTimeStyles.None);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var offset = zone.GetUtcOffset(time);
        return time.ToString("yyyy年MM月dd日 HH:mm:ss", CultureInfo.InvariantCulture)
            + "（UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture) + "）";
    }
    private InboxMessage? Current(ConversationSnapshot snapshot, string source, long revision)
        => !currentBinding(snapshot.Identity) || snapshot.Revision != revision ? null
            : snapshot.Inbox.SingleOrDefault(i => i.SourceMessageId == source && i.Revision == revision && i.Trust == MessageTrust.AuthenticatedUser);
    private static TaskReference? TaskForCall(ModelToolCall call, ConversationSnapshot snapshot)
    {
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var args = document.RootElement;
        return call.Name == "subscribe" ? new(args.GetProperty("hostId").GetString()!, args.GetProperty("threadId").GetString()!,
                args.GetProperty("turnId").GetString(), args.TryGetProperty("profileId", out var profile) ? profile.GetString() : null)
            : call.Name == "change_commitment" && Guid.TryParse(args.GetProperty("workItemId").GetString(), out var id)
                ? snapshot.WorkItems.SingleOrDefault(w => w.Id == id)?.Task : null;
    }
    private static bool Canonical(ModelToolCall call, out string canonical)
    { canonical = ""; return Tools.TryGet(call.Name, out var descriptor) && descriptor.Parameters.TryValidate(call.ArgumentsJson, out canonical, out _); }
    private static CommitmentCommandResolution Proposal(string name, object args)
    {
        var call = new ModelToolCall(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(args));
        return Canonical(call, out var canonical) ? new(true, call with { ArgumentsJson = canonical }, null)
            : Clarify("参数超出范围或提醒内容超过 1000 字符，请缩短后重新明确。");
    }
    private static CommitmentCommandResolution Clarify(string reply) => new(true, null, reply);
    private static bool TryId(string value, out Guid id) => Guid.TryParseExact(value, "D", out id) || Guid.TryParseExact(value, "N", out id);
    private static bool SameIdentity(TaskReference a, TaskReference b) => a.HostId == b.HostId && a.ThreadId == b.ThreadId && a.ProfileId == b.ProfileId;
    private static string PolicyName(MissedTriggerPolicy policy) => policy switch { MissedTriggerPolicy.Skip => "skip", MissedTriggerPolicy.NeedsDecision => "needsDecision", _ => "notifyOnce" };
    private static string MissedSummary(string policy) => policy switch { "skip" => "错过时跳过", "needsDecision" => "错过时等待你决定", _ => "恢复后提醒一次" };
    internal static string PolicySummary(TrackedWorkItem work, ReminderSchedule? schedule)
        => PolicySummary(work.Policy) + (schedule == null ? "" : "；错过时间：" + MissedSummary(PolicyName(schedule.MissedPolicy)));
    private static string PolicySummary(CommitmentPolicy policy) => (policy.QuietStart == null ? "未设置安静时段"
        : "安静时段 " + policy.QuietStart + "-" + policy.QuietEnd + " [" + policy.TimeZoneId + "]")
        + "；每24小时最多" + policy.MaximumNotificationsPer24Hours + "次";
    private static string Stage(TrackedWorkItem work) => work.Stage switch { TrackedWorkStage.Active => "生效中", TrackedWorkStage.Triggered => "已触发，等待通知", TrackedWorkStage.Paused => "已暂停", TrackedWorkStage.Completed => work.Kind == TrackedWorkKind.Reminder ? "提醒流程已结束" : "订阅流程已结束", TrackedWorkStage.Cancelled => "已取消", TrackedWorkStage.Expired => "已过期", _ => "需要决定" };
    private static string Clean(string text, int maximum) => new string(text.Select(c => char.IsControl(c) ? ' ' : c).Take(maximum).ToArray());
    private static UserAuthorizationDecision Denied(string reason) => new(false, Reason: reason);
    private static ToolExecutionResult Rejected(string code) => new(ToolExecutionState.Rejected, JsonSerializer.SerializeToElement(new { code, saved = false, taskExecutionAllowed = false }));
}
