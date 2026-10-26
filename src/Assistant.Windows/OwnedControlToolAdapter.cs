using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;
using Codex.Control;
using OwnedReceipt = Codex.Control.ControlReceipt;

namespace CodexDashboard;

/// <summary>
/// Deterministic current-message authority and exact durable dispatch binding. The model can select
/// configured identifiers but cannot supply configuration, executable, evidence or ownership.
/// An unrecognized natural-language request needs confirmation; it never grants broad autonomy.
/// </summary>
internal sealed class OwnedControlToolAdapter(OwnedControlRuntime runtime) : IToolExecutor, IUserAuthorizationPolicy
{
    private static readonly ImmutableDictionary<string, string> Methods = new Dictionary<string, string>
    {
        ["create_task"] = "thread/start", ["resume_owned_task"] = "thread/resume", ["continue_owned_task"] = "turn/start", ["steer_turn"] = "turn/steer",
        ["interrupt_turn"] = "turn/interrupt", ["rename_owned_task"] = "thread/name/set",
        ["archive_owned_task"] = "thread/archive", ["unarchive_owned_task"] = "thread/unarchive",
        ["respond_approval"] = "item/commandExecution/requestApproval"
    }.ToImmutableDictionary(StringComparer.Ordinal);
    private static readonly ToolCatalog Tools = new(Descriptors());
    internal static IReadOnlyList<ToolDescriptor> Descriptors()
    {
        var revision = new ToolSchema { Type = ToolValueType.Integer, Minimum = 1, Maximum = 9007199254740991 };
        var page = new ToolSchema { Type = ToolValueType.Integer, Minimum = 1, Maximum = 1024 };
        var snapshot = ToolSchema.String(32);
        ToolDescriptor D(string name, string description, bool effects, ToolSchema schema) =>
            new(name, description, schema, effects, "owned." + name);
        ToolSchema Target(params (string, ToolSchema, bool)[] extra) => ToolSchema.Object(
            [("configurationId", ToolSchema.String(64), true), ("threadId", ToolSchema.String(), true), .. extra]);
        ToolSchema Write(params (string, ToolSchema, bool)[] extra) => Target([("revision", revision, true), .. extra]);
        return
        [
            D("list_owned_tasks", "查询可新建任务的预配置范围（configurations，含configurationId和Label），以及助手创建的任务。用户要求新建任务时先用此工具选择配置；即使tasks为空仍可能有可用配置。不启动服务，后续页须带原snapshotId；仅创建来源不证明当前状态。", false,
                ToolSchema.Object(("page", page, false), ("pageSize", new ToolSchema { Type = ToolValueType.Integer, Minimum = 1, Maximum = 8 }, false),
                    ("snapshotId", snapshot, false))),
            D("read_owned_task", "读取本地创建事实或已加载任务；refresh=true仅在已连接或宿主明确只读配置下核对原服务。", false,
                Target(("refresh", new ToolSchema { Type = ToolValueType.Boolean }, false))),
            D("read_owned_report", "读取指定轮次最终答复及安全成果摘要，不含分析、命令原文或环境。后续页须带原snapshotId；不宣称独立验收。", false,
                Target(("turnId", ToolSchema.String(), true), ("page", page, false),
                    ("pageSize", new ToolSchema { Type = ToolValueType.Integer, Minimum = 256, Maximum = 600 }, false), ("snapshotId", snapshot, false))),
            D("read_operations", "查询当前绑定操作状态。后续页携带原snapshotId；快照最多保留最近256条，更早记录按operationId查询。Unknown不重投。", false,
                ToolSchema.Object(("operationId", ToolSchema.String(36), false), ("page", page, false),
                    ("pageSize", new ToolSchema { Type = ToolValueType.Integer, Minimum = 1, Maximum = 8 }, false), ("snapshotId", snapshot, false))),
            D("create_task", "按用户本条明确选择的预配置范围创建空任务；不会自动开始一轮工作。", true,
                ToolSchema.Object(("configurationId", ToolSchema.String(64), true))),
            D("continue_owned_task", "把当前用户的完整原文派发给已拥有且空闲的任务；需最新revision。例如‘继续 编号：只回复OK，不要使用工具’仍应调用本工具，后半句约束目标任务内部执行。text逐字保留完整原文，不由助手代答。", true,
                Write(("text", ToolSchema.String(16384), true))),
            D("resume_owned_task", "继续工具暂时不可用时，用本工具完成重启恢复。用户明确继续指定编号即可授权恢复：先list_owned_tasks定位配置，再read_owned_task(refresh=true)，核对空闲状态和revision后调用本工具。成功后continue_owned_task会出现在下一轮工具列表；重新读取revision再派发原文。恢复本身不启动工作。", true, Write()),
            D("steer_turn", "按当前明确请求纠偏正在运行的指定轮次；受理不代表已生效。", true,
                Write(("expectedTurnId", ToolSchema.String(), true), ("text", ToolSchema.String(16384), true))),
            D("interrupt_turn", "按当前明确请求中断精确轮次；受理不等于已经中断。", true,
                Write(("turnId", ToolSchema.String(), true))),
            D("rename_owned_task", "按当前明确要求重命名指定自建任务。", true, Write(("name", ToolSchema.String(256), true))),
            D("archive_owned_task", "按当前明确要求归档指定空闲自建任务。", true, Write()),
            D("unarchive_owned_task", "按当前明确要求恢复指定自建任务的归档状态。", true, Write()),
            D("respond_approval", "只回应当前明确指向且仍有效的审批；必须完整审批编号或可信通知引用。", true,
                Write(("turnId", ToolSchema.String(), true), ("requestToken", ToolSchema.String(64), true),
                    ("decision", ToolSchema.String(32) with { AllowedStrings = ["accept", "acceptForSession", "decline", "cancel"] }, true)))
        ];
    }
    internal static ToolCatalog Catalog() => Tools;
    internal static bool Handles(string tool) => Tools.TryGet(tool, out _);
    public ToolCapability GetCapability(string capability)
    {
        string name = capability.StartsWith("owned.", StringComparison.Ordinal) ? capability[6..] : capability;
        if (!Handles(name) || !runtime.Active) return new(false, "自建任务工具未启用或绑定已失效。");
        if (name is "list_owned_tasks" or "read_operations") return new(true);
        if (name is "read_owned_task" or "read_owned_report")
            return new(runtime.Tasks().Count > 0 || runtime.HasLocalProvenance
                || runtime.Profiles.Any(p => p.Mode == OwnedProfileMode.ReadOnly), "尚无本地创建记录或可读取的自建任务；刷新仍须通过连接门控。");
        return new(runtime.Profiles.Any(p => runtime.WriteFailure(p.Id, Methods[name]) == null
            || name == "respond_approval" && runtime.WriteFailure(p.Id, "item/fileChange/requestApproval") == null),
            "自建控制没有匹配的运行验收记录，保持关闭。");
    }

    public async ValueTask<UserAuthorizationDecision> AuthorizeAsync(UserAuthorizationRequest request, CancellationToken ct)
        => await AuthorizeCoreAsync(request, ct, null).ConfigureAwait(false);
    private async ValueTask<UserAuthorizationDecision> AuthorizeCoreAsync(UserAuthorizationRequest request, CancellationToken ct,
        WorkspaceReservation? reservation, Guid? verifiedOperationId = null)
    {
        if (!Tools.TryGet(request.Call.Name, out var definition) || !definition.HasSideEffects
            || !definition.Parameters.TryValidate(request.Call.ArgumentsJson, out _, out _))
            return Deny("工具参数不完整或不受支持。");
        await runtime.PrepareWorkspaceAsync(ct).ConfigureAwait(false);
        var snapshot = await runtime.Ledger.ReadConversationAsync(runtime.Identity, ct).ConfigureAwait(false);
        var current = snapshot.Inbox.SingleOrDefault(m => m.SourceMessageId == request.CurrentMessage.SourceMessageId);
        if (!runtime.Active || request.CurrentMessage.Identity != runtime.Identity || current == null
            || current.Trust != MessageTrust.AuthenticatedUser || current.Revision != snapshot.Revision
            || request.CurrentMessage.Body != current.Body || request.CurrentMessage.Revision != current.Revision
            || request.CurrentMessage.Task != current.Task || request.CurrentMessage.ReferencedEventId != current.ReferencedEventId)
            return Deny("只有本条经过身份核对的当前消息可以授权。");
        using var document = JsonDocument.Parse(request.Call.ArgumentsJson);
        var args = document.RootElement;
        string configurationId = Text(args, "configurationId");
        var profile = runtime.Profile(configurationId);
        if (profile == null || runtime.WriteFailure(configurationId, MethodFor(request.Call.Name, args)) is { })
            return Deny("所选控制范围尚未配置或未通过对应运行验收。");
        string body = current.Body.Trim();
        if (body.Length is 0 or > 16384 || body.Contains("```", StringComparison.Ordinal)
            || Regex.IsMatch(body, @"^(?:请)?(?:不要|别|先别|暂不|引用|报告|日志|示例|翻译|解释|如何|怎么|能否|是否)")
            || Regex.IsMatch(body, @"如果|假如|除非|通过后|完成后|结束后|(?<!\S)[>“""]")
            || Regex.IsMatch(body, @"(?:不要|不许|禁止|不必|无需|暂不).{0,24}(?:创建|新建|继续|运行|执行|纠偏|调整|中断|停止|归档|重命名|批准)"))
            return Deny("需要直接明确的当前执行要求；引用、条件或报告内容不能授权。");
        if (ConflictingProfile(body, profile)) return Deny("当前消息指定了不同或多个配置范围。");
        if (request.Call.Name == "create_task")
        {
            if (!Regex.IsMatch(body, @"^(?:请|帮我|麻烦)?(?:在\s*)?(?:新建|创建)\b|^(?:请|帮我|麻烦)?(?:新建|创建)")
                || !NamesProfile(body, profile))
                return Deny("新建前需要明确选择已配置的主机、项目、模型及权限范围。");
            var creationAdmission = runtime.CheckCreateWorkspace(profile.Id, reservation);
            if (!creationAdmission.Allowed) return Deny(AdmissionMessage(creationAdmission.Code));
            return Allow("owned-create/" + profile.Id, current.Body);
        }
        var target = runtime.FindTask(configurationId, Text(args, "threadId"));
        if (request.Call.Name == "continue_owned_task" && target != null
            && snapshot.Operations.Any(o => o.Id != verifiedOperationId && o.SourceMessageId == current.SourceMessageId
                && o.Action == "continue_owned_task" && o.Task?.ThreadId == target.Handle.Identity.ThreadId
                && o.Task.HostId == target.Handle.Identity.HostId && o.Task.ProfileId == target.Handle.Identity.ProfileId
                && o.Stage != OperationStage.CancelledBeforeDispatch))
            return Deny("本条消息已有这个任务的派发记录，不再启动新一轮。请用read_operations或read_owned_report查询原操作和结果，不改revision重复派发。");
        if (target == null || (request.Call.Name == "unarchive_owned_task" ? false : request.Call.Name == "resume_owned_task"
                ? target.Handle.Access != OwnedTaskAccess.RestoredReadOnly
                : target.Handle.Access is not (OwnedTaskAccess.CreatedThisSession or OwnedTaskAccess.ResumedThisSession))
            || target.Snapshot.Revision != args.GetProperty("revision").GetInt64())
            return Deny("任务状态已变化，先刷新任务。重启或断线后的任务需明确恢复控制，才能继续工作。");
        var task = new TaskReference(target.Handle.Identity.HostId, target.Handle.Identity.ThreadId,
            ProfileId: target.Handle.Identity.ProfileId);
        if (request.Call.Name == "respond_approval")
            return AuthorizeApproval(current, args, target, task, body);
        if (!NamesTarget(current, body, target, snapshot.Context, request.Call.Name)) return Deny("当前消息没有唯一指向这个自建任务。");
        if (target.Handle.Access == OwnedTaskAccess.RestoredReadOnly
            && request.Call.Name is "resume_owned_task" or "unarchive_owned_task"
            && snapshot.Operations.Any(o => o.Id != verifiedOperationId && o.Task?.ThreadId == target.Handle.Identity.ThreadId
                && o.Task.HostId == target.Handle.Identity.HostId && o.Task.ProfileId == target.Handle.Identity.ProfileId
                && (o.Stage is OperationStage.Unknown or OperationStage.Dispatching
                    || o.Action is "continue_owned_task" or "steer_turn" or "respond_approval"
                        && o.Stage is OperationStage.Accepted or OperationStage.Running)))
            return Deny("该任务有未核清的操作，先查询工作记录，不恢复或重发。");
        switch (request.Call.Name)
        {
            case "resume_owned_task":
                if (!Regex.IsMatch(body, @"^(?:请|帮我|麻烦)?(?:恢复|继续|接续|运行|执行)")
                    || !(Contains(body, target.Handle.Identity.ThreadId) || Regex.IsMatch(body, @"^(?:请)?(?:恢复当前任务|继续当前任务)(?:控制)?(?:\s*[:：]\s*\S|[。]?$)"))
                    || target.Snapshot.State is not (OwnedTaskState.Idle or OwnedTaskState.Completed or OwnedTaskState.Failed or OwnedTaskState.Interrupted))
                    return Deny("恢复控制需要明确任务编号和已核对的空闲状态。");
                break;
            case "continue_owned_task":
                if (!(Regex.IsMatch(body, @"^(?:请|帮我|麻烦)?(?:继续|接续|运行|执行|修复|实现|测试)")
                    || (current.ReferencedEventId != null && QuotedTaskContinuation.IsRequest(body)))
                    || Text(args, "text") != current.Body || target.Snapshot.State is not
                    (OwnedTaskState.Idle or OwnedTaskState.Completed or OwnedTaskState.Failed or OwnedTaskState.Interrupted))
                    return Deny("需要本条原文工作指令，以及已经观察为空闲的原任务。");
                var continuationAdmission = runtime.CheckWorkingDirectory(target, reservation);
                if (!continuationAdmission.Allowed) return Deny(AdmissionMessage(continuationAdmission.Code));
                break;
            case "steer_turn":
                if (!Regex.IsMatch(body, @"^(?:请|帮我|麻烦)?(?:纠偏|调整|补充|改为|改成)")
                    || Text(args, "text") != current.Body || !ActiveTurn(target, Text(args, "expectedTurnId"))
                    || !runtime.CanAmendWorkspace(target, Text(args, "expectedTurnId")))
                    return Deny("需要当前明确的纠偏原文及准确的运行轮次。");
                task = task with { TurnId = Text(args, "expectedTurnId") };
                break;
            case "interrupt_turn":
                if ((!Regex.IsMatch(body, @"^(?:请|帮我|麻烦)?(?:中断|停止)\s*")
                    && body.TrimEnd('。', '！', '!', '.', ' ') != "先停一下") || !ActiveTurn(target, Text(args, "turnId")))
                    return Deny("需要明确中断这个运行轮次。");
                task = task with { TurnId = Text(args, "turnId") };
                break;
            case "rename_owned_task":
                if (!Regex.IsMatch(body, @"^(?:请|帮我|麻烦)?重命名|^(?:请|帮我|麻烦)?把.+(?:重命名|改名)为")
                    || !body.EndsWith("为" + Text(args, "name"), StringComparison.Ordinal)
                        && !body.EndsWith("为 " + Text(args, "name"), StringComparison.Ordinal))
                    return Deny("需要明确原任务和逐字一致的新名称。");
                break;
            case "archive_owned_task":
                if (!Regex.IsMatch(body, @"^(?:请|帮我|麻烦)?归档")) return Deny("需要当前明确归档要求。");
                break;
            case "unarchive_owned_task":
                if (!Regex.IsMatch(body, @"^(?:请|帮我|麻烦)?(?:取消归档|恢复归档)")) return Deny("需要当前明确恢复归档要求。");
                if (target.Handle.Access == OwnedTaskAccess.RestoredReadOnly && !Contains(body, target.Handle.Identity.ThreadId))
                    return Deny("重启或断线后取消归档，请写明原任务的完整编号。");
                break;
        }
        if (request.Call.Name is "rename_owned_task" or "archive_owned_task" or "unarchive_owned_task"
            && target.Snapshot.State is OwnedTaskState.Accepted or OwnedTaskState.Running or OwnedTaskState.WaitingApproval or OwnedTaskState.Unknown)
            return Deny("任务未观察为空闲，不执行元数据变更。");
        return Allow(target.Handle.Identity.Key, current.Body, task);
    }

    private UserAuthorizationDecision AuthorizeApproval(InboxMessage current, JsonElement args, OwnedRuntimeTask target,
        TaskReference task, string body)
    {
        var session = runtime.LoadedSession(target.ConfigurationId)!;
        var approval = session.Client.PendingApprovals().SingleOrDefault(a => a.Token == Text(args, "requestToken"));
        if (approval == null || approval.Task != target.Handle.Identity || approval.TurnId != Text(args, "turnId")
            || !runtime.IsFocused(approval) || !ActiveTurn(target, approval.TurnId)
            || runtime.WriteFailure(target.ConfigurationId, approval.Method) != null)
            return Deny("审批已经失效、焦点改变或轮次不一致。");
        var decision = Decision(Text(args, "decision"));
        if (!approval.Decisions.Contains(decision)) return Deny("该审批不允许这个决定。");
        if (decision is ApprovalDecision.Accept or ApprovalDecision.AcceptForSession
            && !runtime.CanAmendWorkspace(target, approval.TurnId)) return Deny("工作目录仍有冲突或结果待确认的写操作，尚未批准。");
        string[] words = decision switch
        {
            ApprovalDecision.Accept => ["批准", "同意", "批"],
            ApprovalDecision.AcceptForSession => ["本次会话批准", "本次会话同意"],
            ApprovalDecision.Decline => ["拒绝", "不批准"],
            _ => ["取消审批"]
        };
        string answer = body.TrimEnd('。', '！', '!', '.', ' ');
        bool exactReference = words.Any(word => answer == word + " " + ApprovalReference(target.ConfigurationId, approval));
        bool trustedReply = current.ReferencedEventId is { } eventId
            && runtime.Presented(eventId) is { } shown && shown.Identity == current.Identity
            && shown.ConfigurationId == target.ConfigurationId && shown.Request.Token == approval.Token
            && shown.Request.RequestId.GetRawText() == approval.RequestId.GetRawText()
            && shown.Request.ConnectionEpoch == approval.ConnectionEpoch && shown.FocusRevision == approval.FocusRevision
            && words.Contains(answer, StringComparer.Ordinal);
        if (!exactReference && !trustedReply) return Deny("需要完整审批编号或可验证的通知引用；不能猜测最近审批。");
        return Allow(target.Handle.Identity.Key, current.Body, task with { TurnId = approval.TurnId });
    }
    internal static string ApprovalReference(string configurationId, PendingApproval approval)
        => "approval/" + configurationId + "/" + approval.Token + "/"
            + (approval.RequestId.ValueKind == JsonValueKind.String ? "s/" + Uri.EscapeDataString(approval.RequestId.GetString()!)
                : "n/" + Uri.EscapeDataString(approval.RequestId.GetRawText()));

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken ct)
    {
        if (!Tools.TryGet(call.Name, out var descriptor)
            || !descriptor.Parameters.TryValidate(call.ArgumentsJson, out string canonical, out _))
            return Needs("invalid_or_unconfigured_arguments");
        using var scope = runtime.Enter();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, runtime.StopToken);
        using var document = JsonDocument.Parse(canonical);
        var args = document.RootElement; ct = linked.Token;
        var saved = await runtime.Ledger.ReadConversationAsync(runtime.Identity, ct).ConfigureAwait(false);
        var current = saved.Inbox.SingleOrDefault(m => m.SourceMessageId == context.SourceMessageId);
        if (!runtime.Active || context.Identity != runtime.Identity || saved.IsReadOnly || current == null
            || current.Revision != context.Revision)
            return Needs("current_binding_or_message_changed");
        if (context.InputWorkId != null || context.Revision != saved.Revision)
        {
            var invocation = saved.ToolInvocations.SingleOrDefault(i => i.Id == context.InvocationId);
            if (descriptor.HasSideEffects || context.OperationId != null || context.IdempotencyKey != null
                || invocation == null || invocation.ToolName != call.Name || invocation.CallId != call.Id
                || invocation.ArgumentsJson != canonical
                || !await runtime.Ledger.VerifyReadOnlyWorkExecutionAsync(context, ct).ConfigureAwait(false))
                return Needs("current_read_only_work_lease_required");
            return await ReadAsync(context, call.Name, args, ct).ConfigureAwait(false);
        }
        if (!descriptor.HasSideEffects)
        {
            if (context.OperationId != null || context.IdempotencyKey != null) return Needs("read_only_operation_context_required");
            return await ReadAsync(context, call.Name, args, ct).ConfigureAwait(false);
        }
        var verification = await VerifiedOperationAsync(context, call with { ArgumentsJson = canonical }, descriptor, ct).ConfigureAwait(false);
        OperationRecord? operation = verification.Operation;
        if (operation == null) return new(ToolExecutionState.Rejected,
            JsonSerializer.SerializeToElement(new { code = "durable_current_authorization_required", dispatched = false, automaticRetry = false }),
            verification.Reason ?? "需要核对当前请求和准确目标后再执行。");
        string configurationId = Text(args, "configurationId");
        WorkspaceReservation? workspaceReservation = null;
        if (call.Name is "create_task" or "continue_owned_task")
        {
            var admission = runtime.ReserveWorkspace(configurationId, operation.Id,
                call.Name == "create_task" ? null : runtime.FindTask(configurationId, Text(args, "threadId")));
            if (!admission.Allowed) return NotDispatched(admission.Code) with { TargetKey = operation.TargetKey, Task = operation.Task };
            workspaceReservation = admission.Reservation;
        }
        try { runtime.RegisterDispatch(operation); }
        catch (InvalidOperationException) { workspaceReservation?.NotDispatched(); return Needs("operation_already_attempted_or_binding_changed"); }
        bool mutationInvoked = false;
        try
        {
            var session = await runtime.EnsureSessionAsync(configurationId, MethodFor(call.Name, args), false, ct).ConfigureAwait(false);
            // Initializing a process can take time. Recheck current revision, exact authorization,
            // expiry and current task facts immediately before a task mutation.
            var recheck = await VerifiedOperationAsync(context, call with { ArgumentsJson = canonical }, descriptor, ct, workspaceReservation).ConfigureAwait(false);
            if (recheck.Operation == null)
            {
                workspaceReservation?.NotDispatched();
                return Needs("authorization_or_target_changed_before_control") with
                { Reason = recheck.Reason ?? "请求或任务状态在连接期间发生变化，这次没有派发。请查看工作记录核对当前状态。" };
            }
            ct.ThrowIfCancellationRequested();
            OwnedReceipt receipt;
            if (call.Name == "create_task")
            {
                mutationInvoked = true;
                var created = await session.Client.CreateAsync(operation.Id, session.Profile.Creation, ct).ConfigureAwait(false);
                receipt = created.Receipt;
                if (created.Task != null)
                {
                    runtime.Adopt(session, created.Task);
                    runtime.SetFocus(configurationId, created.Task.Identity.ThreadId);
                }
                if (created.Task != null && receipt.State == ControlState.Accepted) workspaceReservation?.NotDispatched();
                else workspaceReservation?.Unknown();
            }
            else
            {
                var target = runtime.FindTask(configurationId, Text(args, "threadId"))!;
                long revision = args.GetProperty("revision").GetInt64();
                switch (call.Name)
                {
                    case "resume_owned_task":
                        mutationInvoked = true;
                        receipt = await session.Client.ResumeAsync(operation.Id, target.Handle, revision, ct).ConfigureAwait(false);
                        break;
                    case "continue_owned_task":
                        using (runtime.ReserveDirectory(target))
                        {
                            runtime.SetFocus(configurationId, target.Handle.Identity.ThreadId);
                            mutationInvoked = true;
                            receipt = await session.Client.StartTurnAsync(operation.Id, target.Handle, revision, Text(args, "text"), ct).ConfigureAwait(false);
                        }
                        if (receipt.State is ControlState.Accepted or ControlState.Running or ControlState.Completed)
                            workspaceReservation?.Accepted(target.Handle.Identity, receipt.TurnId);
                        else if (receipt.State == ControlState.CancelledBeforeDispatch) workspaceReservation?.NotDispatched();
                        else workspaceReservation?.Unknown();
                        break;
                    case "steer_turn":
                        receipt = await session.Client.SteerAsync(operation.Id, target.Handle, revision, Text(args, "expectedTurnId"), Text(args, "text"), ct).ConfigureAwait(false); break;
                    case "interrupt_turn":
                        receipt = await session.Client.InterruptAsync(operation.Id, target.Handle, revision, Text(args, "turnId"), ct).ConfigureAwait(false); break;
                    case "rename_owned_task":
                        receipt = await session.Client.RenameAsync(operation.Id, target.Handle, revision, Text(args, "name"), ct).ConfigureAwait(false); break;
                    case "archive_owned_task":
                        receipt = await session.Client.ArchiveAsync(operation.Id, target.Handle, revision, ct).ConfigureAwait(false); break;
                    case "unarchive_owned_task":
                        receipt = await session.Client.UnarchiveAsync(operation.Id, target.Handle, revision, ct).ConfigureAwait(false); break;
                    case "respond_approval":
                        var approval = session.Client.PendingApprovals().Single(a => a.Token == Text(args, "requestToken"));
                        receipt = await session.Client.RespondApprovalAsync(operation.Id, approval.Token, target.Handle.Identity,
                            Text(args, "turnId"), approval.ProjectId, approval.FocusRevision, Decision(Text(args, "decision")), ct).ConfigureAwait(false); break;
                    default: return Needs("unsupported_owned_action");
                }
            }
            // Preserve the immutable authorized target. New thread/turn IDs are separate evidence.
            var observed = new OperationObservation(OwnedControlRuntime.Stage(receipt.State),
                JsonSerializer.Serialize(new { source = "owned_direct_response", receipt.Code, phase = receipt.State.ToString(),
                    observedTask = receipt.Task, observedTurnId = receipt.TurnId }), operation.TargetKey, operation.Task);
            var latest = (await runtime.Ledger.ReadConversationAsync(runtime.Identity, CancellationToken.None).ConfigureAwait(false))
                .Operations.Single(o => o.Id == operation.Id);
            if (latest.Stage != OperationStage.Unknown || observed.Stage == OperationStage.Unknown)
                await runtime.Ledger.RecordOperationObservationAsync(runtime.Identity, operation.Id, observed, CancellationToken.None).ConfigureAwait(false);
            bool selectedCurrentTask = false;
            if (call.Name == "create_task" && receipt.State == ControlState.Accepted && receipt.Task != null && runtime.Active)
            {
                var source = (await runtime.Ledger.ReadConversationAsync(runtime.Identity, CancellationToken.None).ConfigureAwait(false))
                    .Inbox.Single(m => m.SourceMessageId == context.SourceMessageId);
                try
                {
                    await runtime.Ledger.SetWorkFocusAsync(runtime.Identity, context.SourceMessageId,
                        new TaskReference(receipt.Task.HostId, receipt.Task.ThreadId, ProfileId: receipt.Task.ProfileId),
                        source.Body[..Math.Min(source.Body.Length, 4096)], CancellationToken.None).ConfigureAwait(false);
                    selectedCurrentTask = true;
                }
                catch (LedgerException error) when (error.Code == LedgerErrors.StaleRevision)
                {
                    // A later user message owns focus; the accepted creation receipt remains valid.
                }
            }
            return new(Map(receipt.State), JsonSerializer.SerializeToElement(new
            {
                operationId = operation.Id, phase = receipt.State.ToString(), receipt.Code, selectedCurrentTask,
                observedTask = receipt.Task, observedTurnId = receipt.TurnId,
                originalAuthorizationTarget = operation.TargetKey, originalAuthorizationTask = operation.Task,
                newlyBoundTaskEvidence = call.Name == "create_task" && receipt.Task != null ? new { createOperationId = operation.Id, task = receipt.Task } : null,
                independentlyVerified = false, automaticRetry = false
            }), TargetKey: operation.TargetKey, Task: operation.Task);
        }
        catch
        {
            if (mutationInvoked) workspaceReservation?.Unknown();
            else workspaceReservation?.NotDispatched();
            return new(ToolExecutionState.Unknown, JsonSerializer.SerializeToElement(new
            { code = "owned_outcome_unknown_needs_reconciliation", operationId = operation.Id, automaticRetry = false }),
                TargetKey: operation.TargetKey, Task: operation.Task);
        }
    }

    private async Task<(OperationRecord? Operation, string? Reason)> VerifiedOperationAsync(ToolExecutionContext context, ModelToolCall call,
        ToolDescriptor descriptor, CancellationToken ct, WorkspaceReservation? reservation = null)
    {
        var snapshot = await runtime.Ledger.ReadConversationAsync(runtime.Identity, ct).ConfigureAwait(false);
        var current = snapshot.Inbox.SingleOrDefault(m => m.SourceMessageId == context.SourceMessageId);
        var operation = snapshot.Operations.SingleOrDefault(o => o.Id == context.OperationId);
        var invocation = snapshot.ToolInvocations.SingleOrDefault(i => i.Id == context.InvocationId);
        if (!runtime.Active || snapshot.IsReadOnly || current == null || operation == null || invocation == null
            || snapshot.Revision != context.Revision || current.Trust != MessageTrust.AuthenticatedUser
            || operation.Stage != OperationStage.Dispatching || operation.Identity != context.Identity
            || operation.SourceMessageId != context.SourceMessageId || operation.Revision != context.Revision
            || operation.Action != call.Name || operation.ArgumentsJson != call.ArgumentsJson || operation.IdempotencyKey != context.IdempotencyKey
            || invocation.Stage != ToolInvocationStage.Executing || invocation.OperationId != operation.Id
            || invocation.CallId != call.Id || invocation.ToolName != call.Name || invocation.ArgumentsJson != call.ArgumentsJson
            || invocation.SourceMessageId != context.SourceMessageId || invocation.Revision != context.Revision)
            return (null, null);
        var authorization = snapshot.Authorizations.SingleOrDefault(a => a.Id == operation.AuthorizationId);
        if (authorization == null || authorization.Identity != operation.Identity || authorization.SourceMessageId != operation.SourceMessageId
            || authorization.Action != operation.Action || authorization.ArgumentsJson != operation.ArgumentsJson
            || authorization.TargetKey != operation.TargetKey || authorization.Task != operation.Task
            || !current.Body.Contains(authorization.SourceExcerpt, StringComparison.Ordinal)) return (null, null);
        var decision = await AuthorizeCoreAsync(new(current, call, descriptor), ct, reservation, operation.Id).ConfigureAwait(false);
        return decision.Allowed && decision.TargetKey == operation.TargetKey && decision.Task == operation.Task
            ? (operation, null) : (null, decision.Reason);
    }
    private async Task<ToolExecutionResult> ReadAsync(ToolExecutionContext context, string name, JsonElement args, CancellationToken ct)
    {
        if (context.OperationId != null) return Needs("read_cannot_attach_mutation_operation");
        if (name == "read_operations")
        {
            string? id = args.TryGetProperty("operationId", out var requested) ? requested.GetString() : null;
            if (TryCachedPage(args, name, id ?? "", null, out var cached)) return cached!;
            var ledger = await runtime.Ledger.ReadConversationAsync(runtime.Identity, ct).ConfigureAwait(false);
            var matches = ledger.Operations.Where(o => id == null || o.Id.ToString("D") == id)
                .OrderByDescending(o => o.UpdatedAt).ThenBy(o => o.Id).ToArray();
            int size = PageSize(args, 8), retained = Math.Min(matches.Length, 256);
            int pages = Math.Max(1, (retained + size - 1) / size);
            var contents = Enumerable.Range(0, pages).Select(index => ToolResultJson.Element(new
            {
                source = "durable_operation_ledger", totalOperations = matches.Length, retainedOperations = retained,
                sourceTruncated = retained < matches.Length,
                operations = matches.Take(retained).Skip(index * size).Take(size)
                    .Select(o => new { operationId = o.Id, o.Action, o.TargetKey, o.Task, phase = o.Stage.ToString(),
                        o.UpdatedAt, receipt = IsMemoryAction(o.Action) ? null : Limit(o.Receipt, 800),
                        detailsOmitted = IsMemoryAction(o.Action), automaticRetry = false })
            }));
            return PageResult(runtime.SaveQuerySnapshot(name, id ?? "", null, size, contents), 1);
        }
        if (name == "list_owned_tasks") return await ReadListPageAsync(args, ct).ConfigureAwait(false);
        string profile = Text(args, "configurationId");
        var target = runtime.FindTask(profile, Text(args, "threadId"));
        bool refresh = name == "read_owned_report" || args.TryGetProperty("refresh", out var value) && value.GetBoolean();
        try
        {
            string? requestedTurn = name == "read_owned_report" ? Text(args, "turnId") : null;
            if (name == "read_owned_report" && TryCachedPage(args, name, profile + "/" + Text(args, "threadId"), requestedTurn, out var cached))
                return cached!;
            var catalogue = target == null ? await runtime.ReadCatalogueAsync(ct).ConfigureAwait(false) : null;
            var fact = catalogue?.Tasks.SingleOrDefault(t => t.ConfigurationId == profile && t.Fact.Task.ThreadId == Text(args, "threadId"));
            if (target == null && fact == null) return Needs("owned_task_not_in_verified_catalogue");
            if (!refresh && target == null)
                return Result(ToolExecutionState.ReadOnlySuccess, new { task = PublicFact(fact!), refreshed = false,
                    approvals = Array.Empty<object>(), freshness = "creation_only_not_current_task_state", startedProcess = false });
            var session = runtime.LoadedSession(profile);
            if (refresh) session = await runtime.EnsureSessionAsync(profile, "thread/read", true, ct).ConfigureAwait(false);
            target ??= runtime.FindTask(profile, Text(args, "threadId"));
            if (target == null || session == null) return Needs("owned_task_not_loaded");
            if (name == "read_owned_report")
            {
                var report = await session.Client.ReadReportAsync(target.Handle, Text(args, "turnId"), ct).ConfigureAwait(false);
                runtime.Observed(session, target.Handle);
                return ReportPages(args, profile, report);
            }
            if (refresh)
            {
                await session.Client.ReconcileAsync(target.Handle, ct).ConfigureAwait(false);
                runtime.Observed(session, target.Handle);
            }
            return Result(ToolExecutionState.ReadOnlySuccess, new
            {
                task = PublicTask(runtime.FindTask(profile, target.Handle.Identity.ThreadId)!),
                refreshed = refresh, approvals = session.Client.PendingApprovals().Where(a => a.Task == target.Handle.Identity).Take(4)
                    .Select(a => new { reference = ApprovalReference(profile, a), requestToken = a.Token, requestId = a.RequestId,
                        a.ConnectionEpoch, a.Task, a.TurnId, a.ProjectId, cwd = Limit(a.Cwd, 512), a.ItemId, a.Method, summary = Limit(a.Summary, 800),
                        decisions = a.Decisions.Select(d => d.ToString()), a.ExpiresAt, a.FocusRevision,
                        currentlyFocused = runtime.IsFocused(a) })
            });
        }
        catch { return Result(ToolExecutionState.Rejected, new { code = "owned_read_unavailable", preservedLastObservation = target != null }); }
    }
    private static bool IsMemoryAction(string action) =>
        action is "remember" or "update_memory" or "forget" or "learn_preference";

    private async Task<ToolExecutionResult> ReadListPageAsync(JsonElement args, CancellationToken ct)
    {
        if (TryCachedPage(args, "list_owned_tasks", "", null, out var cached)) return cached!;
        var catalogue = await runtime.ReadCatalogueAsync(ct).ConfigureAwait(false);
        var live = runtime.Tasks();
        var tasks = live.Select(t => (Key: t.ConfigurationId + "/" + t.Handle.Identity.Key, Value: PublicTask(t)))
            .Concat(catalogue.Tasks.Where(f => !live.Any(t => t.ConfigurationId == f.ConfigurationId && t.Handle.Identity == f.Fact.Task))
                .Select(f => (Key: f.ConfigurationId + "/" + f.Fact.Task.Key, Value: PublicFact(f))))
            .OrderBy(t => t.Key, StringComparer.Ordinal).ToArray();
        var configurations = runtime.Profiles.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => new
        {
            configurationId = p.Id, p.Label, endpoint = p.Configuration.Connection.Endpoint,
            p.Creation.ProjectId, cwd = Limit(p.Creation.Cwd, 512), p.Creation.Model, p.Creation.ApprovalPolicy,
            p.Creation.Sandbox, mode = p.Mode.ToString(), controlEnabled = runtime.WriteFailure(p.Id, "thread/start") == null
        }).ToArray();
        int size = PageSize(args, 8), retained = Math.Min(tasks.Length, 256);
        int pages = Math.Max(1, (Math.Max(configurations.Length, retained) + size - 1) / size);
        var contents = Enumerable.Range(0, pages).Select(index => JsonSerializer.SerializeToElement(new
        {
            configurations = configurations.Skip(index * size).Take(size),
            tasks = tasks.Take(retained).Skip(index * size).Take(size).Select(t => t.Value),
            freshness = "creation_facts_or_last_observation_only", startedProcess = false,
            totalConfigurations = configurations.Length, totalTasks = tasks.Length, retainedTasks = retained,
            unavailableConfigurations = catalogue.UnavailableConfigurations, completeCatalogue = catalogue.UnavailableConfigurations.Count == 0,
            sourceTruncated = retained < tasks.Length, truncated = retained < tasks.Length || pages > 1
        }));
        return PageResult(runtime.SaveQuerySnapshot("list_owned_tasks", "", null, size, contents), 1);
    }

    private bool TryCachedPage(JsonElement args, string query, string target, string? turn, out ToolExecutionResult? result)
    {
        int page = args.TryGetProperty("page", out var requested) ? requested.GetInt32() : 1;
        if (!args.TryGetProperty("snapshotId", out var id))
        {
            result = page == 1 ? null : Needs("owned_snapshot_id_required_for_next_page");
            return result != null;
        }
        var snapshot = runtime.FindQuerySnapshot(id.GetString()!, query, target, turn,
            args.TryGetProperty("pageSize", out var size) ? size.GetInt32() : null);
        result = snapshot == null ? Needs("owned_snapshot_missing_expired_or_scope_changed")
            : page > snapshot.Pages.Length ? Needs("owned_snapshot_page_out_of_range") : PageResult(snapshot, page);
        return true;
    }

    private ToolExecutionResult ReportPages(JsonElement args, string configurationId, OwnedReport report)
    {
        const int maximumText = 128 * 1024, maximumItems = 256;
        int size = PageSize(args, 600);
        var text = TextChunks(report.FinalText, size, maximumText);
        // Whitelist structural evidence. Raw command/output/environment/diff and arbitrary JSON
        // can contain secrets or analysis and are never returned as report item summaries.
        var commands = report.Commands.Take(maximumItems).Select(item => new
        {
            id = SafeText(item, "id", 128), status = SafeText(item, "status", 32),
            exitCode = item.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number
                && exit.TryGetInt32(out int code) ? (int?)code : null,
            truncated = FieldTruncated(item, "id", 128) || FieldTruncated(item, "status", 32)
        }).ToArray();
        var changes = report.FileChanges.SelectMany(item =>
            item.TryGetProperty("changes", out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(change => new { itemId = SafeText(item, "id", 128),
                    status = SafeText(item, "status", 32), path = SafeText(change, "path", 512),
                    kind = change.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.Object
                        ? SafeText(kind, "type", 32) : SafeText(change, "kind", 32),
                    truncated = FieldTruncated(item, "id", 128) || FieldTruncated(item, "status", 32)
                        || FieldTruncated(change, "path", 512) || FieldTruncated(change, "kind", 32)
                        || change.TryGetProperty("kind", out var kindObject) && FieldTruncated(kindObject, "type", 32) })
                : []).Take(maximumItems + 1).ToArray();
        int pages = Math.Max(text.Count, (Math.Max(commands.Length, Math.Min(changes.Length, maximumItems)) + 3) / 4);
        bool limited = report.FinalText.Length > maximumText || report.Commands.Length > maximumItems || changes.Length > maximumItems
            || commands.Any(c => c.truncated) || changes.Any(c => c.truncated);
        var contents = Enumerable.Range(0, pages).Select(index => JsonSerializer.SerializeToElement(new
        {
            task = report.Task, report.TurnId, finalText = index < text.Count ? text[index] : "",
            commandItems = commands.Skip(index * 4).Take(4), fileChanges = changes.Take(maximumItems).Skip(index * 4).Take(4),
            report.EvidenceSource, report.IndependentlyVerified, reportSelfClaim = true,
            totalFinalTextCharacters = report.FinalText.Length, retainedFinalTextCharacters = text.Sum(t => t.Length),
            totalCommandItems = report.Commands.Length, retainedCommandItems = commands.Length,
            fileChangeCountIsLowerBound = changes.Length > maximumItems, retainedFileChanges = Math.Min(changes.Length, maximumItems),
            excludedFields = new[] { "analysis", "command", "aggregatedOutput", "environment", "diff", "rawItem" },
            summaryFieldLimits = new { id = 128, path = 512, statusAndKind = 32 },
            sourceTruncated = limited, truncated = limited || pages > 1
        }));
        return PageResult(runtime.SaveQuerySnapshot("read_owned_report", configurationId + "/" + report.Task.ThreadId, report.TurnId, size, contents), 1);
    }

    private static List<string> TextChunks(string value, int size, int maximum)
    {
        var result = new List<string>();
        int end = Math.Min(maximum, value.Length);
        if (end < value.Length && end > 0 && char.IsHighSurrogate(value[end - 1])) end--;
        for (int offset = 0; offset < end;)
        {
            int count = Math.Min(size, end - offset);
            if (offset + count < end && char.IsHighSurrogate(value[offset + count - 1])) count--;
            result.Add(value.Substring(offset, count)); offset += count;
        }
        if (result.Count == 0) result.Add("");
        return result;
    }
    private static int PageSize(JsonElement args, int fallback)
        => args.TryGetProperty("pageSize", out var size) ? size.GetInt32() : fallback;
    private static string? SafeText(JsonElement item, string field, int maximum)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? Limit(value.GetString(), maximum) : null;
    private static bool FieldTruncated(JsonElement item, string field, int maximum)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString()!.Length > maximum;
    private static ToolExecutionResult PageResult(OwnedQuerySnapshot snapshot, int number)
    {
        var facts = snapshot.Pages[number - 1].EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
        facts["page"] = new { number, size = snapshot.PageSize, totalPages = snapshot.Pages.Length,
            nextPage = number < snapshot.Pages.Length ? (int?)(number + 1) : null, snapshotId = snapshot.Id,
            snapshot.CapturedAt, snapshot.ExpiresAt, consistentSnapshot = true, currentStateMayHaveChanged = true };
        return Result(ToolExecutionState.ReadOnlySuccess, facts);
    }
    private bool NamesTarget(InboxMessage message, string text, OwnedRuntimeTask target,
        ConversationContext? context, string action)
    {
        if (message.ReferencedEventId != null && QuotedTaskContinuation.IsRequest(text))
            return action == "continue_owned_task" && message.Task is { } quoted
                && SameCanonical(quoted, target)
                && (quoted.TurnId == null || quoted.TurnId == target.Snapshot.CurrentTurnId);
        var named = runtime.Tasks().Where(t => Contains(text, t.Handle.Identity.ThreadId)).ToArray();
        if (named.Length > 0) return named.Length == 1 && named[0].Handle == target.Handle;
        if (message.Task is { } reference && reference.HostId == target.Handle.Identity.HostId
            && reference.ThreadId == target.Handle.Identity.ThreadId && reference.ProfileId == target.Handle.Identity.ProfileId
            && (reference.TurnId == null || reference.TurnId == target.Snapshot.CurrentTurnId)) return true;
        // This narrow language branch uses independently persisted user-selected context.
        // Never rewrite Inbox.Task or infer a selection from an assistant reply/notification.
        string phrase = text.TrimEnd('。', '！', '!', '.', ' ');
        bool explicitCurrent = phrase.Contains("当前任务", StringComparison.Ordinal);
        bool exactRequest = action switch
        {
            "interrupt_turn" => phrase is "先停一下" or "停止运行" or "中断当前任务" or "停止当前任务",
            "continue_owned_task" => Regex.IsMatch(phrase, @"^(?:请)?继续当前任务\s*[:：]\s*\S") || phrase is "继续" or "继续运行" or "继续测试" or "继续修复" or "继续实现" or "继续执行"
                or "继续当前任务" or "接续当前任务" or "运行当前任务" or "执行当前任务",
            "resume_owned_task" => Regex.IsMatch(phrase, @"^(?:请)?(?:恢复当前任务|继续当前任务)(?:控制)?(?:\s*[:：]\s*\S|$)"),
            _ => false
        };
        if (!exactRequest || context?.Identity != message.Identity || context.Focus is not { } focus
            || !SameCanonical(focus.Task, target) || context.PendingSelection != null
            || context.PendingApproval is { } pending && !(action == "interrupt_turn" && explicitCurrent && SameCanonical(pending.Task, target)))
            return false;
        // "Current task" explicitly retains the selected target despite a notification.
        // Generic short replies must not silently pick between the focus and another/unknown notice.
        return explicitCurrent || context.LastNotification == null
            || context.LastNotification.Task is { } notified && SameCanonical(notified, target);
    }
    private static bool SameCanonical(TaskReference reference, OwnedRuntimeTask target)
        => reference.HostId == target.Handle.Identity.HostId && reference.ThreadId == target.Handle.Identity.ThreadId
            && reference.ProfileId == target.Handle.Identity.ProfileId;
    private bool ConflictingProfile(string text, ConfiguredOwnedProfile selected)
    {
        string host = selected.Configuration.Connection.Endpoint.HostId;
        return runtime.Profiles.Any(p => p.Id != selected.Id && NamesProfile(text, p))
            || Regex.Matches(text, @"remote-ssh-discovered:[A-Za-z0-9][A-Za-z0-9._-]*").Any(m => m.Value != host)
            || text.Contains("远程", StringComparison.Ordinal) && host == "local"
            || text.Contains("本机", StringComparison.Ordinal) && host != "local";
    }
    private string MethodFor(string name, JsonElement args)
    {
        if (name != "respond_approval") return Methods[name];
        return runtime.LoadedSession(Text(args, "configurationId"))?.Client.PendingApprovals()
            .SingleOrDefault(a => a.Token == Text(args, "requestToken"))?.Method ?? Methods[name];
    }
    private static bool NamesProfile(string text, ConfiguredOwnedProfile profile) => Contains(text, profile.Id) || Contains(text, profile.Label);
    private static bool Contains(string text, string value) => value.Length > 0
        && Regex.IsMatch(text, @"(?<![A-Za-z0-9_-])" + Regex.Escape(value) + @"(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant);
    private static bool ActiveTurn(OwnedRuntimeTask task, string turn)
        => task.Snapshot.CurrentTurnId == turn && task.Snapshot.State is OwnedTaskState.Running or OwnedTaskState.WaitingApproval;
    private static UserAuthorizationDecision Deny(string reason) => new(false, Reason: reason);
    private static UserAuthorizationDecision Allow(string target, string excerpt, TaskReference? task = null) => new(true, target, excerpt, task);
    private static string AdmissionMessage(string code) => code == "workspace_global_capacity"
        ? "助手同时执行的额度已满，结果待核对的任务仍占额度。这次没有派发或自动排队，请稍后再次明确要求。"
        : code == "workspace_busy_or_unknown"
        ? "这次未派发，目录可能有运行任务或过期状态。先list_owned_tasks，再read_owned_task(refresh=true)核对同目录任务；全部空闲且无Unknown操作时可按本条要求再次调用。仍有占用或未知结果时停止，不重发未知操作。"
        : "工作目录仍被占用或尚未完成范围核对。这次没有派发，请核对后再次明确要求。";
    private static ToolExecutionResult NotDispatched(string code) => new(ToolExecutionState.Rejected,
        JsonSerializer.SerializeToElement(new { code, needsConfirmation = true, dispatched = false, automaticRetry = false }), AdmissionMessage(code));
    private static ToolExecutionResult Needs(string reason) => new(ToolExecutionState.Rejected,
        JsonSerializer.SerializeToElement(new { code = "needs_confirmation", reason, executed = false }), "需要核对当前请求和准确目标后再执行。");
    private static ToolExecutionResult Result(ToolExecutionState state, object facts) => new(state, JsonSerializer.SerializeToElement(facts));
    private static string Text(JsonElement args, string key) => args.GetProperty(key).GetString()!;
    private static string? Limit(string? value, int maximum) => value?.Length > maximum ? value[..maximum] : value;
    private static object PublicTask(OwnedRuntimeTask task) => new { configurationId = task.ConfigurationId,
        identity = task.Handle.Identity, task.Snapshot.ProjectId, cwd = Limit(task.Snapshot.Cwd, 512), task.Snapshot.Model,
        task.Snapshot.ApprovalPolicy, task.Snapshot.Sandbox, task.Snapshot.CurrentTurnId,
        state = task.Snapshot.State.ToString(), task.Snapshot.Revision, access = task.Handle.Access.ToString(),
        task.Handle.Creation, task.ObservedAt, task.EvidenceSource, independentlyVerified = false };
    private static object PublicFact(OwnedCatalogueTask task) => new { configurationId = task.ConfigurationId,
        identity = task.Fact.Task, task.Fact.ProjectId, cwd = Limit(task.Fact.Cwd, 512), task.Fact.Model,
        task.Fact.ApprovalPolicy, task.Fact.Sandbox, CurrentTurnId = (string?)null, state = "Unknown",
        Revision = (long?)null, access = "CreationEvidenceOnly", task.Fact.Creation, ObservedAt = (DateTimeOffset?)null,
        EvidenceSource = "protected_owned_provenance_creation_only", independentlyVerified = false, controlEnabled = false };
    private static ApprovalDecision Decision(string decision) => decision switch
    {
        "accept" => ApprovalDecision.Accept, "acceptForSession" => ApprovalDecision.AcceptForSession,
        "decline" => ApprovalDecision.Decline, "cancel" => ApprovalDecision.Cancel, _ => throw new ArgumentException("invalid_decision")
    };
    private static ToolExecutionState Map(ControlState state) => state switch
    {
        ControlState.Accepted or ControlState.Interrupted => ToolExecutionState.Accepted,
        ControlState.Running => ToolExecutionState.Running, ControlState.Completed => ToolExecutionState.Completed,
        ControlState.Failed or ControlState.CancelledBeforeDispatch => ToolExecutionState.Rejected, _ => ToolExecutionState.Unknown
    };
}
