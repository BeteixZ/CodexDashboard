using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;

namespace CodexDashboard;

internal sealed class DependentWorkToolAdapter(DurableConversationLedger ledger, Func<OwnedControlRuntime?> owned,
    Func<ConversationIdentity, bool> currentBinding, Func<bool> enabled, Func<DateTimeOffset> clock)
    : IToolExecutor, IUserAuthorizationPolicy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly Regex Schedule = new(@"\A(?:请)?等\s*(?<parents>.+?)\s*(?:的)?本轮(?:都)?完成后[，,]\s*向\s*(?<target>.+?)\s*发送[：:](?<text>[\s\S]+)\z", RegexOptions.CultureInvariant);
    private static readonly Regex Chain = new(@"\A(?:请)?等依赖\s+(?<parents>.+?)\s*(?:都)?(?:完成|测试命令「(?<command>[^「」\r\n]+)」成功)后[，,]\s*向\s*(?<target>.+?)\s*发送[：:](?<text>[\s\S]+)\z", RegexOptions.CultureInvariant);
    private static readonly Regex TestCommand = new(@"\A(?:请)?等\s*(?<parents>.+?)\s*本轮测试命令「(?<command>[^「」\r\n]+)」成功后[，,]\s*向\s*(?<target>.+?)\s*发送[：:](?<text>[\s\S]+)\z", RegexOptions.CultureInvariant);
    private static readonly Regex ContinueAfter = new(@"\A(?:请)?等依赖\s+(?<id>[0-9a-fA-F-]{36})\s*(?:完成|测试命令「(?<command>[^「」\r\n]+)」成功)后[，,]\s*在同一任务继续[：:](?<text>[\s\S]+)\z", RegexOptions.CultureInvariant);
    private static readonly Regex Manage = new(@"\A(?<action>暂停|恢复|取消|修改)依赖\s+(?<id>[0-9a-fA-F-]{36})(?:[：:](?<text>[\s\S]+))?\z", RegexOptions.CultureInvariant);
    private static readonly Regex EditDeadline = new(@"\A修改依赖\s+(?<id>[0-9a-fA-F-]{36})\s+截止[：:](?<deadline>[^；;\r\n]+)\z", RegexOptions.CultureInvariant);
    private static readonly Regex EditQuiet = new(@"\A修改依赖\s+(?<id>[0-9a-fA-F-]{36})\s+安静时段[：:](?<quiet>[^\r\n]+)\z", RegexOptions.CultureInvariant);
    private const string CommandEdit = @"测试命令[：:]\s*(?:「(?<command>[^「」\r\n]+)」|(?<clearCommand>无))";
    private const string DeadlineEdit = @"(?:[；;]\s*截止[：:](?<deadline>[^；;\r\n]+))?\z";
    private static readonly Regex EditRequirements = new(@"\A修改依赖\s+(?<id>[0-9a-fA-F-]{36})\s+前置[：:](?<kind>任务|依赖)\s+(?<parents>[^；;\r\n]+)(?:[；;]\s*" + CommandEdit + ")?" + DeadlineEdit, RegexOptions.CultureInvariant);
    private static readonly Regex EditTestCommand = new(@"\A修改依赖\s+(?<id>[0-9a-fA-F-]{36})\s+" + CommandEdit + DeadlineEdit, RegexOptions.CultureInvariant);
    internal static bool Handles(string name) => DependentWorkTools.Handles(name) || name == "list_dependencies";
    internal static bool IsCandidate(string text) => TryListPage(text, out _) || Manage.IsMatch(text) || Schedule.IsMatch(text)
        || Regex.IsMatch(text, @"\A(?:暂停|恢复|取消|修改)依赖|\A(?:请)?等.+(?:完成|通过|成功)后", RegexOptions.CultureInvariant);
    internal static ImmutableArray<ToolDescriptor> Descriptors() => DependentWorkTools.Descriptors().Add(
        new("list_dependencies", "分页读取当前绑定真实登记的依赖工作和实际状态，受理不等于完成。",
            ToolSchema.Object(("page", new() { Type = ToolValueType.Integer, Minimum = 1, Maximum = 64 }, false)), false, "list_dependencies"));
    public ToolCapability GetCapability(string name) => name is "list_dependencies" or "change_dependency" ? new(true)
        : new(name == "schedule_dependency" && enabled() && owned() is { Active: true } runtime
            && runtime.Tasks().Any(runtime.CanScheduleDependency), "当前没有支持可靠依赖派发的已连接自建任务。");

    internal async Task<CommitmentCommandResolution> ResolveAsync(InboxMessage proposed, CancellationToken ct)
    {
        string text = proposed.Body.Trim();
        if (!IsCandidate(text)) return new(false, null, null);
        var conversation = await ledger.ReadConversationAsync(proposed.Identity, ct);
        var source = conversation.Inbox.SingleOrDefault(i => i.SourceMessageId == proposed.SourceMessageId);
        if (source == null || source.Body != proposed.Body || source.Revision != proposed.Revision
            || source.Revision != conversation.Revision || source.Trust != MessageTrust.AuthenticatedUser || source.HasUnresolvedReference
            || conversation.IsReadOnly || !currentBinding(source.Identity)) return Clarify("当前请求或引用还未核实，依赖工作没有改变。");
        if (TryListPage(text, out int page)) return new(true, null, await FormatListAsync(source.Identity, ct, page));
        var saved = conversation.DependentWork!;
        var quiet = EditQuiet.Match(text);
        if (quiet.Success)
        {
            var work = Guid.TryParseExact(quiet.Groups["id"].Value, "D", out var quietId) ? saved.WorkItems.SingleOrDefault(w => w.Id == quietId) : null;
            if (work == null) return Clarify("请使用当前绑定依赖列表中的完整工作编号。");
            var attempt = saved.Attempts.SingleOrDefault(a => a.Id == work.AttemptId);
            if (work.Stage is not (DependentWorkStage.Waiting or DependentWorkStage.Paused or DependentWorkStage.NeedsDecision)
                || work.DeadlineUtc <= clock() || attempt?.Stage is DependentDispatchStage.Accepted or DependentDispatchStage.Dispatching or DependentDispatchStage.Unknown)
                return Clarify("请在尚未派发且等待期限未过时调整安静时段；已到期的安排可先明确续期，或取消原安排。");
            string value = quiet.Groups["quiet"].Value.Trim();
            var policy = work.NotificationPolicy ?? new();
            if (value == "无") policy = policy with { QuietStart = null, QuietEnd = null, TimeZoneId = null };
            else
            {
                var times = value.Split('-');
                if (times.Length != 2 || !TimeOnly.TryParseExact(times[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
                    || !TimeOnly.TryParseExact(times[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until) || from == until)
                    return Clarify("安静时段请写成 22:00-08:00，按这台电脑的时区保存；写“无”可清除。");
                policy = policy with { QuietStart = times[0], QuietEnd = times[1], TimeZoneId = TimeZoneInfo.Local.Id };
            }
            return Proposal("change_dependency", new DependentWorkChange(work.Id, work.Version, "update", NotificationPolicy: policy));
        }
        var edit = EditRequirements.Match(text);
        if (!edit.Success) edit = EditTestCommand.Match(text);
        if (!edit.Success) edit = EditDeadline.Match(text);
        if (edit.Success)
        {
            if (!Guid.TryParseExact(edit.Groups["id"].Value, "D", out var editId)) return Clarify("请使用依赖列表中的完整工作编号。");
            var work = saved.WorkItems.SingleOrDefault(w => w.Id == editId);
            if (work == null) return Clarify("当前绑定没有这条依赖工作，请先查看依赖列表。");
            var attempt = saved.Attempts.SingleOrDefault(a => a.Id == work.AttemptId);
            if (work.Stage is not (DependentWorkStage.Waiting or DependentWorkStage.Paused or DependentWorkStage.NeedsDecision or DependentWorkStage.Expired)
                || attempt?.Stage is DependentDispatchStage.Accepted or DependentDispatchStage.Dispatching or DependentDispatchStage.Unknown)
                return Clarify("这项工作已经派发、结果未知或已结束，不能改前置、测试条件和截止。请查看原记录；修改不会撤回已发送的指令。");
            string? deadline = null;
            if (edit.Groups["deadline"].Success)
            {
                if (!DateTimeOffset.TryParseExact(edit.Groups["deadline"].Value.Trim(),
                    ["yyyy-MM-dd HH:mm zzz", "yyyy-MM-dd HH:mm:ss zzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss'Z'", "O"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) || parsed <= clock())
                    return Clarify("截止需要带时区的未来时间，例如 " + DeadlineExample() + "。没有修改这项安排。");
                deadline = parsed.ToUniversalTime().ToString("O");
            }
            if (deadline == null && work.DeadlineUtc <= clock())
                return Clarify("原截止时间已过。请同时明确新的截止时间，例如：修改依赖 完整编号 前置：任务 完整任务编号；截止：" + DeadlineExample() + "。");
            ImmutableArray<DependencyRequirement>? requirements = null;
            if (edit.Groups["parents"].Success)
            {
                if (!edit.Groups["command"].Success && !edit.Groups["clearCommand"].Success && work.Requirements.Any(r => r.CommandSuccess != null))
                    return Clarify("原安排含测试命令条件。更换前置时请同时写“；测试命令：「原样命令」”或“；测试命令：无”，明确保留的条件。");
                var parsed = ResolveRequirements(edit.Groups["parents"].Value, edit.Groups["kind"].Value == "依赖", conversation, owned());
                if (parsed.Error != null) return Clarify(parsed.Error);
                requirements = parsed.Requirements;
                if (work.ContinueAfterWorkId is { } continuationId && !requirements.Value.Any(r => r.WorkItemId == continuationId))
                    return Clarify("同一任务接续必须保留原来指定的前置工作；可明确更新它的版本。若要换接续起点，请重新登记安排。");
            }
            if (edit.Groups["command"].Success || edit.Groups["clearCommand"].Success)
            {
                var updated = ApplyCommand(requirements ?? work.Requirements,
                    edit.Groups["command"].Success ? edit.Groups["command"].Value : null, conversation, owned());
                if (updated.Error != null) return Clarify(updated.Error);
                requirements = updated.Requirements;
            }
            return Proposal("change_dependency", new DependentWorkChange(work.Id, work.Version, "update", DeadlineUtc: deadline, Requirements: requirements));
        }
        var change = Manage.Match(text);
        if (change.Success && Guid.TryParse(change.Groups["id"].Value, out var id))
        {
            var work = saved.WorkItems.SingleOrDefault(w => w.Id == id);
            if (work == null) return Clarify("当前绑定没有这条依赖工作，请先查看依赖列表。");
            string action = change.Groups["action"].Value switch { "暂停" => "pause", "恢复" => "resume", "取消" => "cancel", _ => "update" };
            string? instructions = change.Groups["text"].Success ? change.Groups["text"].Value.Trim() : null;
            if (action == "update" ? string.IsNullOrWhiteSpace(instructions) : instructions != null)
                return Clarify("修改时请在编号后用冒号写出新指令；暂停、恢复或取消只需完整编号。");
            return Proposal("change_dependency", new DependentWorkChange(work.Id, work.Version, action, instructions));
        }
        if (text.StartsWith("修改依赖", StringComparison.Ordinal))
            return Clarify("请明确修改内容：修改依赖 完整编号：新指令；修改依赖 完整编号 截止：带时区的未来时间；修改依赖 完整编号 前置：任务或依赖 完整编号；或修改依赖 完整编号 测试命令：「原样命令」。写“测试命令：无”可移除命令条件。");
        var chain = Chain.Match(text);
        var continuation = ContinueAfter.Match(text);
        var testCommand = TestCommand.Match(text);
        var match = continuation.Success ? continuation : testCommand.Success ? testCommand : chain.Success ? chain : Schedule.Match(text);
        if (!match.Success) return Clarify("请明确前置和后续指令，例如：等 A 本轮完成后，向 B 发送：后续指令；或等 A 本轮测试命令「dotnet test」成功后，向 B 发送：后续指令。测试条件核对指定命令的实际退出码，报告文字不作证明。");
        if (source.ReceivedAt.AddHours(24) <= clock()) return Clarify("这条请求的 24 小时等待期限已过，尚未登记。请重新安排。");
        var runtime = owned();
        if (!enabled() || runtime is not { Active: true } || runtime.Identity != source.Identity)
            return Clarify("自建任务控制未开启或未连接，尚未登记依赖工作。");
        if (continuation.Success)
        {
            var parent = Guid.TryParseExact(continuation.Groups["id"].Value, "D", out var parentId)
                ? saved.WorkItems.SingleOrDefault(w => w.Id == parentId) : null;
            if (parent == null) return Clarify("请使用当前绑定依赖列表中的完整工作编号。");
            var task = runtime.DependencyTarget(parent.Target);
            if (task == null || !runtime.CanScheduleDependency(task)
                || OwnedVerification.ConfigurationDigest(runtime.Profile(task.ConfigurationId)!.Configuration) != parent.BindingFingerprint)
                return Clarify("前置工作的原任务或配置当前无法核对，尚未登记同一任务接续。");
            ImmutableArray<DependencyRequirement> requirements = [new(WorkItemId: parent.Id, WorkVersion: parent.Version)];
            if (continuation.Groups["command"].Success)
            {
                var updated = ApplyCommand(requirements, continuation.Groups["command"].Value, conversation, runtime);
                if (updated.Error != null) return Clarify(updated.Error);
                requirements = updated.Requirements;
            }
            return Proposal("schedule_dependency", new DependentWorkSpecification(parent.Target, parent.BindingFingerprint,
                continuation.Groups["text"].Value.Trim(), requirements,
                source.ReceivedAt.AddHours(24).ToUniversalTime().ToString("O"), parent.Id, NotificationPolicy: new()));
        }
        var target = ResolveTask(match.Groups["target"].Value, conversation, runtime);
        if (target == null) return Clarify("目标需要唯一的、已连接且有轮次的自建任务，请使用完整任务编号。");
        var conditions = ResolveRequirements(match.Groups["parents"].Value, chain.Success, conversation, runtime);
        if (conditions.Error != null) return Clarify(conditions.Error);
        if (match.Groups["command"].Success)
        {
            conditions = ApplyCommand(conditions.Requirements, match.Groups["command"].Value, conversation, runtime);
            if (conditions.Error != null) return Clarify(conditions.Error);
        }
        return Proposal("schedule_dependency", new DependentWorkSpecification(Reference(target),
            OwnedVerification.ConfigurationDigest(runtime.Profile(target.ConfigurationId)!.Configuration), match.Groups["text"].Value.Trim(),
            conditions.Requirements, source.ReceivedAt.AddHours(24).ToUniversalTime().ToString("O"), NotificationPolicy: new()));
    }

    private string DeadlineExample() => clock().AddDays(1).ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);

    private static (ImmutableArray<DependencyRequirement> Requirements, string? Error) ApplyCommand(
        ImmutableArray<DependencyRequirement> requirements, string? command, ConversationSnapshot conversation, OwnedControlRuntime? runtime)
    {
        if (command == null) return (requirements.Select(r => r with { CommandSuccess = null }).ToImmutableArray(), null);
        var changed = ImmutableArray.CreateBuilder<DependencyRequirement>();
        foreach (var requirement in requirements)
        {
            string? directory = requirement.CommandSuccess?.WorkingDirectory;
            if (directory == null)
            {
                var parent = requirement.WorkItemId is { } id ? conversation.DependentWork!.WorkItems.Single(w => w.Id == id) : null;
                var task = runtime?.DependencyTarget(requirement.Task ?? parent!.Target);
                if (runtime is not { Active: true } || runtime.Identity != conversation.Identity || task == null || !runtime.CanScheduleDependency(task)
                    || OwnedVerification.ConfigurationDigest(runtime.Profile(task.ConfigurationId)!.Configuration) != (requirement.BindingFingerprint ?? parent!.BindingFingerprint))
                    return ([], "前置任务的原配置和项目目录当前无法核对，未保存测试命令条件。请先核对连接。");
                directory = task.Snapshot.Cwd;
            }
            changed.Add(requirement with { CommandSuccess = new(command, directory) });
        }
        return (changed.ToImmutable(), null);
    }

    private static TaskReference Reference(OwnedRuntimeTask task) => new(task.Handle.Identity.HostId, task.Handle.Identity.ThreadId,
        task.Snapshot.CurrentTurnId, task.Handle.Identity.ProfileId);
    private static OwnedRuntimeTask? ResolveTask(string token, ConversationSnapshot conversation, OwnedControlRuntime runtime)
    {
        token = token.Trim();
        var focus = token == "当前任务" && conversation.Context?.PendingSelection == null ? conversation.Context?.Focus?.Task : null;
        var matches = runtime.Tasks().Where(t => runtime.CanScheduleDependency(t) && (focus != null
            ? t.Handle.Identity.HostId == focus.HostId && t.Handle.Identity.ThreadId == focus.ThreadId && t.Handle.Identity.ProfileId == focus.ProfileId
                && (focus.TurnId == null || focus.TurnId == t.Snapshot.CurrentTurnId)
            : token == t.Handle.Identity.ThreadId || token == t.ConfigurationId + "/" + t.Handle.Identity.ThreadId || token == t.Snapshot.ProjectId)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private static (ImmutableArray<DependencyRequirement> Requirements, string? Error) ResolveRequirements(
        string text, bool workReferences, ConversationSnapshot conversation, OwnedControlRuntime? runtime)
    {
        var tokens = text.Split('、', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 1 or > 8) return ([], "前置需要一至八项，多个前置用顿号分开。");
        if (workReferences)
        {
            var parents = tokens.Select(token => Guid.TryParseExact(token, "D", out var id)
                ? conversation.DependentWork!.WorkItems.SingleOrDefault(w => w.Id == id) : null).ToArray();
            return parents.Any(p => p == null) ? ([], "请使用当前绑定依赖列表中的完整工作编号；多个编号用顿号分开。")
                : (parents.Select(p => new DependencyRequirement(WorkItemId: p!.Id, WorkVersion: p.Version)).Distinct().ToImmutableArray(), null);
        }
        if (runtime is not { Active: true } || runtime.Identity != conversation.Identity) return ([], "任务前置需要当前绑定已连接的自建任务，请先核对连接。");
        var tasks = tokens.Select(t => ResolveTask(t, conversation, runtime)).ToArray();
        return tasks.Any(t => t == null) ? ([], "前置需要唯一的、已连接且有轮次的自建任务。请用完整任务编号。")
            : (tasks.Select(t => new DependencyRequirement(Reference(t!), OwnedVerification.ConfigurationDigest(runtime.Profile(t!.ConfigurationId)!.Configuration)))
                .Distinct().ToImmutableArray(), null);
    }

    public async ValueTask<UserAuthorizationDecision> AuthorizeAsync(UserAuthorizationRequest request, CancellationToken ct)
    {
        if (!DependentWorkTools.Handles(request.Call.Name) || !GetCapability(request.Call.Name).Available || !Canonical(request.Call, out var canonical)) return new(false);
        var resolved = await ResolveAsync(request.CurrentMessage, ct);
        if (resolved.Call?.Name != request.Call.Name || resolved.Call.ArgumentsJson != canonical) return new(false, Reason: resolved.Reply ?? "参数与当前明确请求不符。");
        using var document = JsonDocument.Parse(canonical);
        TaskReference? target = request.Call.Name == "schedule_dependency"
            ? document.RootElement.GetProperty("target").Deserialize<TaskReference>(Json)
            : (await ledger.GetDependentWorkAsync(request.CurrentMessage.Identity, ct)).WorkItems
                .SingleOrDefault(w => w.Id == document.RootElement.GetProperty("workItemId").GetGuid())?.Target;
        return new(true, DependentWorkTools.TargetKey(request.Call.Name, canonical), request.CurrentMessage.Body, target);
    }
    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken ct)
    {
        ToolExecutionResult Rejected() => new(ToolExecutionState.Rejected, JsonSerializer.SerializeToElement(new { code = "dependency_request_changed", dispatched = false }));
        if (!currentBinding(context.Identity) || !Canonical(call, out var canonical)) return Rejected();
        var saved = await ledger.ReadConversationAsync(context.Identity, ct);
        var invocation = saved.ToolInvocations.SingleOrDefault(i => i.Id == context.InvocationId);
        if (invocation == null || invocation.ToolName != call.Name || invocation.CallId != call.Id || invocation.ArgumentsJson != canonical
            || invocation.SourceMessageId != context.SourceMessageId || invocation.Revision != context.Revision) return Rejected();
        if (call.Name == "list_dependencies")
        {
            if (context.OperationId != null || context.IdempotencyKey != null || invocation.Stage != ToolInvocationStage.Executing
                || (context.InputWorkId != null || context.Revision != saved.Revision)
                    && !await ledger.VerifyReadOnlyWorkExecutionAsync(context, ct)) return Rejected();
            using var arguments = JsonDocument.Parse(canonical);
            int page = arguments.RootElement.TryGetProperty("page", out var field) ? field.GetInt32() : 1;
            return new(ToolExecutionState.ReadOnlySuccess, JsonSerializer.SerializeToElement(new { text = await FormatListAsync(context.Identity, ct, page) }));
        }
        var source = saved.Inbox.SingleOrDefault(i => i.SourceMessageId == context.SourceMessageId);
        if (source == null || !(await AuthorizeAsync(new(source, call, Descriptors().Single(d => d.Name == call.Name)), ct)).Allowed) return Rejected();
        try { return await ledger.ApplyDependentWorkToolAsync(context, ct); }
        catch (Exception error) when (error is ArgumentException or LedgerException) { return Rejected(); }
    }

    internal static bool TryListPage(string text, out int page)
    {
        page = 1;
        var match = Regex.Match(text, @"\A依赖列表(?:\s+([1-9][0-9]?))?\z", RegexOptions.CultureInvariant);
        return match.Success && (!match.Groups[1].Success || int.TryParse(match.Groups[1].Value, out page) && page <= 64);
    }
    internal async Task<string> FormatListAsync(ConversationIdentity identity, CancellationToken ct, int page = 1)
    {
        if (!currentBinding(identity)) return "当前绑定已经变化。";
        var saved = await ledger.GetDependentWorkAsync(identity, ct);
        if (saved.WorkItems.IsEmpty) return "目前没有登记依赖工作。";
        int pages = (saved.WorkItems.Length + 5) / 6;
        if (page > pages) return $"依赖工作共有 {pages} 页，请发“依赖列表 页码”查询。";
        return $"依赖工作共 {saved.WorkItems.Length} 项，第 {page}/{pages} 页。\n\n"
            + string.Join("\n\n", saved.WorkItems.OrderByDescending(w => w.CreatedAt).ThenBy(w => w.Id).Skip((page - 1) * 6).Take(6).Select(w =>
            $"{w.Id:D} · {Stage(w.Stage)}\n目标 {w.Target.ThreadId}，前置 {w.Requirements.Length} 项\n{w.Instructions}\n截止 {w.DeadlineUtc.ToLocalTime():yyyy-MM-dd HH:mm zzz}"
            + (w.ContinueAfterWorkId is { } parent ? $"\n接续依赖 {parent:D} 实际完成的轮次" : "")
            + "\n" + NotificationSummary(w.NotificationPolicy)
            + "\n前置：" + string.Join("；", w.Requirements.Select(RequirementSummary))
            + (w.ResultTask == null ? "" : "\n实际轮次 " + w.ResultTask.TurnId)))
            + (page < pages ? $"\n\n下一页：依赖列表 {page + 1}" : "");
    }
    internal static string DescribeReceipt(ToolExecutionResult result)
    {
        var facts = result.Facts;
        return "已保存依赖工作。编号 " + facts.GetProperty("workItemId").GetGuid().ToString("D")
            + "，第 " + facts.GetProperty("version").GetInt64() + " 版，状态：" + Stage(Enum.Parse<DependentWorkStage>(facts.GetProperty("stage").GetString()!))
            + "。截止 " + facts.GetProperty("deadlineUtc").GetDateTimeOffset().ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz")
            + "。前置：" + string.Join("；", facts.GetProperty("requirements").Deserialize<ImmutableArray<DependencyRequirement>>(Json).Select(RequirementSummary))
            + (facts.TryGetProperty("continueAfterWorkId", out var continuation) && continuation.ValueKind == JsonValueKind.String
                ? "。接续起点是这项前置工作实际完成的轮次，不会跟随任意新轮次" : "")
            + "。" + NotificationSummary(facts.TryGetProperty("notificationPolicy", out var policy) && policy.ValueKind == JsonValueKind.Object ? policy.Deserialize<CommitmentPolicy>(Json) : null)
            + "。登记不表示后续指令已发送；前置工作按登记版本等待，版本变化需重新安排。暂停或取消也不会中断已经开始的任务。";
    }
    internal static string NotificationSummary(CommitmentPolicy? policy) => policy == null ? "旧安排未开启专门提醒；可在派发前明确设置安静时段或设为“无”以开启"
        : "失败、到期或需要重新决定时提醒一次；"
        + (policy?.QuietStart is { } start ? $"安静时段 {start}-{policy.QuietEnd}（{policy.TimeZoneId}）" : "未设置安静时段")
        + "；与提醒和订阅共用通知预算";
    internal static string RequirementSummary(DependencyRequirement r) => (r.WorkItemId is { } id
        ? $"依赖 {id:D} 第 {r.WorkVersion} 版完成" : $"任务 {r.Task!.ThreadId} / 轮次 {r.Task.TurnId} 完成")
        + (r.CommandSuccess is { } command ? $"，且目录 {command.WorkingDirectory} 中命令「{command.Command}」的最后一次执行已结束、退出码为 0" : "");
    internal static string Stage(DependentWorkStage stage) => stage switch
    { DependentWorkStage.Waiting => "等待前置条件", DependentWorkStage.Dispatching => "正在派发", DependentWorkStage.Running => "已受理，等待完成",
        DependentWorkStage.Completed => "已完成", DependentWorkStage.Failed => "执行失败", DependentWorkStage.Paused => "已暂停",
        DependentWorkStage.Cancelled => "已取消", DependentWorkStage.Expired => "已到期", DependentWorkStage.NeedsDecision => "需要重新决定", _ => "结果未知，不重发" };
    private static bool Canonical(ModelToolCall call, out string canonical)
    {
        canonical = ""; var descriptor = Descriptors().SingleOrDefault(d => d.Name == call.Name);
        return descriptor != null && descriptor.Parameters.TryValidate(call.ArgumentsJson, out canonical, out _);
    }
    private static CommitmentCommandResolution Proposal(string name, object value)
    {
        var call = new ModelToolCall(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(value, Json));
        return Canonical(call, out var canonical) ? new(true, call with { ArgumentsJson = canonical }, null) : Clarify("指令为空或超过 4000 字，请重新明确。");
    }
    private static CommitmentCommandResolution Clarify(string message) => new(true, null, message);
}
