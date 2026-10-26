using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Assistant.Core;

namespace CodexDashboard;

internal sealed partial class DurableAssistantRuntime : IAsyncDisposable
{
    private readonly Settings settings;
    private readonly AssistantArtifactSelection? artifactSelection;
    internal bool CanSelectArtifact => artifactSelection != null && Identity != null;
    internal Task<SelectedArtifact> ImportArtifactAsync(ConversationIdentity expected, string path, CancellationToken ct, TaskReference? task = null)
        => artifactSelection?.ImportAsync(expected, path, ct, task) ?? throw new InvalidOperationException("文件产物试验尚未开启。");
    private readonly ChatGptAssistant model;
    private readonly TaskControlQueue queue;
    private readonly ICodexGateway gateway;
    private readonly WeChatService channel;
    private readonly Dispatcher dispatcher;
    private readonly DurableConversationLedger ledger;
    private readonly AssistantToolExecutor executor;
    private readonly RuntimeToolRouter tools;
    private readonly DurableMessageCoordinator coordinator;
    private readonly AssistantModelContextProvider modelContext;
    private ConversationIdentity? identity;
    internal string? LastError { get; private set; }
    internal ConversationIdentity? Identity => Volatile.Read(ref identity);

    internal DurableAssistantRuntime(Settings settings, ChatGptAssistant model, TaskControlQueue queue,
        ICodexGateway gateway, WeChatService channel, Dispatcher dispatcher, DurableConversationLedger ledger,
        Func<WeChatSnapshot> snapshot, IToolModel? toolModel = null, DurableMessageOptions? coordinatorOptions = null,
        OwnedHostOptions? ownedOptions = null, IClock? clock = null, AssistantModelContextProvider? modelContext = null,
        ExistingHostOptions? existingOptions = null, AssistantInboundAttachments? attachments = null, AssistantArtifactStore? artifacts = null,
        Func<string, AssistantImageInput, CancellationToken, Task<string>>? imageAnalyzer = null)
    {
        this.settings = settings; this.model = model; this.queue = queue; this.gateway = gateway;
        this.attachments = attachments; this.artifacts = artifacts;
        this.imageAnalyzer = imageAnalyzer;
        imageAnalysisEnabled = imageAnalyzer != null || settings.ActiveMedia.Images;
        artifactSelection = artifacts == null ? null : new(artifacts, ledger, IsCurrentBinding, IsArtifactTaskCurrent);
        this.channel = channel; this.dispatcher = dispatcher; this.ledger = ledger;
        if (gateway is CodexGateway concreteGateway)
            concreteGateway.ExistingControlCapability = (task, cwd, capability) => ExistingRuntime?.Capability(task, cwd, capability);
        this.modelContext = modelContext ?? new(settings, model.Memory, clock);
        this.modelContext.BindLedger(ledger);
        var projectResolver = new ProjectMemoryResolver(gateway, () => OwnedRuntime,
            clock: () => clock?.UtcNow ?? DateTimeOffset.UtcNow,
            remoteConfigured: host => settings.RemoteHosts.Contains(HostIdentity.DisplayName(host), StringComparer.Ordinal));
        this.modelContext.BindProjectResolver(projectResolver);
        model.ProjectMemoryResolver = projectResolver;
        model.ProjectMemoryTargetProvider = this.modelContext.GetCurrentProjectAsync;
        model.EpisodeMemoryProvider = this.modelContext.CaptureEpisodeEditorAsync;
        model.ProjectEpisodeMemoryProvider = this.modelContext.CaptureProjectEpisodeEditorAsync;
        model.GenerationPersonalizationProvider = this.modelContext.GetPersonalization;
        this.ownedOptions = ownedOptions;
        this.existingOptions = existingOptions;
        commitmentClock = () => clock?.UtcNow ?? DateTimeOffset.UtcNow;
        var automaticPreferences = new AutomaticPreferenceTools(settings, model.Memory, ledger,
            active => active == Identity && CurrentBindingMatches(active), () => model.ReplyRevision, model.SetAutoMemoryEnabled);
        executor = new(settings, gateway, queue, model.Memory, snapshot,
            (identity, ct) => ledger.ReadConversationContextAsync(identity, ct), automaticPreferences,
            new ManualMemoryTools(settings, model.Memory, ledger, IsCurrentBinding),
            new ConversationHistoryTools(ledger, this.modelContext, settings),
            attachments?.Enabled == true ? ReadAttachmentForModelAsync : null,
            attachments?.Enabled == true ? ListAttachmentsForModelAsync : null,
            attachments?.Enabled == true && imageAnalysisEnabled ? AnalyzeAttachmentImageAsync : null,
            artifactSelection != null ? ListPreparedFilesForModelAsync : null);
        commitments = new(settings, ledger, gateway, queue, () => OwnedRuntime, IsCurrentBinding, clock: commitmentClock);
        dependencies = new(ledger, () => OwnedRuntime, IsCurrentBinding, DependencyControlEnabled, commitmentClock);
        tools = new(executor, commitments, dependencies, () => Volatile.Read(ref ownedAdapter), () => ExistingRuntime, ledger,
            () => settings.ChatGpt.TaskControlEnabled && settings.WeChat.ClawBotEnabled
                && Volatile.Read(ref ownedDeliveryFault) == 0, IsCurrentBinding);
        var ui = new UiAdapters(dispatcher, new TypingToolModel(automaticPreferences.WrapModel(toolModel ?? model), channel.StartTyping), tools, tools,
            () => settings.ChatGpt.Enabled, IsCurrentBinding);
        var segmenter = new WeChatSegmenter();
        var loop = new ToolAgentLoop(ledger, ui, AssistantToolExecutor.Catalog(includeOwned: true), ui, ui,
            new ToolLoopOptions { ToolTimeout = TimeSpan.FromSeconds(75) }, segmenter, contextProvider: this.modelContext);
        coordinator = new(ledger, loop, () => Identity,
            (request, ct) => OnUi(() => channel.SendDurableAsync(request, ct), ct),
            clock: clock,
            readonlyFastHandler: (message, ct) => OnUi(() => ReadonlyFastAsync(message, ct), ct),
            trustedFastHandler: (context, ct) => OnUi(() => TrustedFastAsync(context, ct), ct),
            replySegmenter: segmenter,
            options: coordinatorOptions ?? new()
            {
                MergeWindow = TimeSpan.FromMilliseconds(settings.InputMergeWindowMilliseconds),
                DeliveryTimeout = TimeSpan.FromSeconds(40), CanGroup = IsExplicitSupplement,
                ReplacesPendingReply = IsExplicitCorrection,
                FailureReplyFactory = result => DurableMessageCoordinator.FailureReply(result)
            },
            onError: error => LastError = error.Code,
            requiredDecisionValidator: RequiredDecisionStillCurrent);
    }
    internal static async Task<DurableAssistantRuntime> OpenAsync(Settings settings, ChatGptAssistant model,
        TaskControlQueue queue, ICodexGateway gateway, WeChatService channel, Dispatcher dispatcher,
        Func<WeChatSnapshot> snapshot, WorkspaceWriteCoordinator? workspace = null)
    {
        var contextProvider = new AssistantModelContextProvider(settings, model.Memory);
        var ledger = await AssistantLedgerStorage.OpenAsync(Settings.DirectoryPath, settings.HistoryArchiveEnabled, contextProvider);
        try
        {
            var configuration = OwnedControlConfiguration.Load(Settings.DirectoryPath,
                Path.Combine(Settings.DirectoryPath, "owned-acceptance"));
            var existingConfiguration = ExistingControlConfiguration.Load(Settings.DirectoryPath,
                Path.Combine(Settings.DirectoryPath, "existing-acceptance"), DateTimeOffset.UtcNow);
            var runtime = new DurableAssistantRuntime(settings, model, queue, gateway, channel, dispatcher, ledger, snapshot,
                ownedOptions: new(Path.Combine(Settings.DirectoryPath, "owned-state"), configuration.Profiles, Workspace: workspace),
                modelContext: contextProvider, existingOptions: new(existingConfiguration.Profiles),
                attachments: new AssistantInboundAttachments(Settings.DirectoryPath,
                    enableCapture: settings.ActiveMedia.Attachments),
                artifacts: settings.ActiveMedia.Files ? new AssistantArtifactStore(Settings.DirectoryPath) : null);
            if (configuration.Problems.Length > 0 || existingConfiguration.Problems.Length > 0)
                runtime.LastError = string.Join(",", configuration.Problems.Select(problem => problem.Code).Concat(existingConfiguration.Problems).Distinct());
            return runtime;
        }
        catch
        {
            await ledger.DisposeAsync();
            throw;
        }
    }
    internal static ConversationIdentity BindingIdentity(ClawBinding binding, string account)
        => new(WeChatText.Hash(binding.UserId), WeChatText.Hash(binding.BaseUrl + "\n" + binding.BotId + "\n" + account), "main");
    internal bool MatchesBinding(ClawBinding binding)
        => Identity == BindingIdentity(binding, settings.ChatGpt.ActiveAccount);
    private bool CurrentBindingMatches(ConversationIdentity expected)
    {
        try { return settings.WeChat.ReadBinding() is { } binding && BindingIdentity(binding, settings.ChatGpt.ActiveAccount) == expected; }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or FormatException or JsonException)
        { return false; }
    }
    internal bool IsCurrentBinding(ConversationIdentity expected) => Identity == expected && CurrentBindingMatches(expected);
    internal async Task ActivateAsync(ClawBinding binding, CancellationToken ct)
    {
        await StopAttachmentReadsAsync();
        attachmentStop.Dispose(); attachmentStop = new CancellationTokenSource();
        commitmentDetailPages.Clear();
        await StopWakeSessionAsync();
        await StopCommitmentSessionAsync();
        await StopOwnedSessionAsync();
        await StopExistingSessionAsync();
        var next = BindingIdentity(binding, settings.ChatGpt.ActiveAccount);
        Volatile.Write(ref identity, next);
        // Token rotation does not change control ownership.
        queue.SetOwner(WeChatText.Hash(next.UserId + "\n" + next.BindingId + "\n" + next.ConversationId));
        await ActivateOwnedSessionAsync(next, ct);
        ActivateExistingSession(next);
        await coordinator.ActivateSessionAsync(next, ct);
        StartCommitmentSession(next);
        StartWakeSession(next);
    }
    internal async Task<ReceiveResult> AcceptAsync(ClawBinding binding, string sourceId, string text,
        DateTimeOffset occurredAt, CancellationToken ct, bool hasFreshChannelContext = false, bool hasUnresolvedReference = false,
        string? referencedMessageId = null)
    {
        var expected = BindingIdentity(binding, settings.ChatGpt.ActiveAccount);
        if (!IsCurrentBinding(expected)) throw new InvalidOperationException("微信绑定正在切换，消息尚未持久接收。");
        queue.ObserveAuthenticatedInput(sourceId, IsKnownReadonlyInput(text));
        var catchUp = new CatchUpInputOptions(CatchUpInputPolicy.AllowsHistory(text, hasFreshChannelContext)
            ? CatchUpDisposition.AllowHistory : CatchUpDisposition.HoldHistory,
            hasFreshChannelContext, TimeZoneInfo.Local.Id);
        Guid? referencedEventId = null;
        if (hasUnresolvedReference && referencedMessageId != null)
        {
            var saved = await ledger.ReadConversationAsync(expected, ct);
            var prior = saved.Inbox.SingleOrDefault(m => m.SourceMessageId == sourceId);
            if (prior != null)
            {
                referencedEventId = prior.ReferencedEventId;
                hasUnresolvedReference = prior.HasUnresolvedReference;
            }
            else
            {
                var matches = saved.Outbox.Where(o => o.Stage is OutboxStage.Accepted or OutboxStage.Unknown
                    && WeChatReferenceReceipt.Matches(o.ChannelReceipt, referencedMessageId)).ToArray();
                if (matches.Length == 1)
                {
                    try
                    {
                        referencedEventId = (await ledger.ResolveSentReferenceAsync(expected, matches[0].Id, ct)).Id;
                        hasUnresolvedReference = false;
                    }
                    catch (LedgerException e) when (e.Code is LedgerErrors.DependencyInvalidated or LedgerErrors.DependencyUnavailable) { }
                }
            }
        }
        return await coordinator.AcceptAsync(expected, sourceId, text, occurredAt, ct, catchUp, hasUnresolvedReference, referencedEventId);
    }
    private static bool TryWorkListPage(string text, out int page)
    {
        page = 1;
        var match = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"\A工作列表(?:\s+(?<page>[1-9][0-9]{0,5}))?\z");
        if (!match.Success) return false;
        if (match.Groups["page"].Success) page = int.Parse(match.Groups["page"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }
    internal static bool IsKnownReadonlyInput(string text)
        => text.Trim() is "帮助" or "help" or "开始" or "状态" or "概览" or "status"
            or "运行中" or "运行" or "需要关注" or "等待输入" or "最近完成" or "完成"
            or "报告" or "报告列表" or "任务列表" or "当前任务" or "工作列表" or "AI状态" or "ai状态"
            or "查看记忆" or "查看规则" or "自动记忆状态" or "你还记着哪些事" or "你还记着哪些事？"
            or "文件帮助" or "附件帮助" or "控制能力" or "能力状态" or "渠道能力" or "媒体能力" or "已有控制" or "刷新项目来源"
            or "提醒列表" or "订阅列表" or "事项列表" or "自动化列表" or "提醒状态" or "唤醒记录" or "唤醒状态"
            or "附件列表" or "待发送文件" or "已准备文件" or "补报" or "补报记录" or "补报状态" or "引用任务" or "这条属于哪个任务" or "这条属于哪个任务？"
            || TryWorkListPage(text, out _)
            || CommitmentToolAdapter.TryListPage(text, out _)
            || CommitmentToolAdapter.TryReminderSearch(text, out _, out _)
            || DependentWorkToolAdapter.TryListPage(text.Trim(), out _)
            || TryParsePreparedFiles(text.Trim(), out _, out _)
            || System.Text.RegularExpressions.Regex.IsMatch(text.Trim(), @"^报告续页 [0-9a-f]{32} [1-9][0-9]{0,3}$")
            || text.Trim() is "报告下一页" or "报告续页"
            || System.Text.RegularExpressions.Regex.IsMatch(text.Trim(), @"^报告第[1-9][0-9]{0,3}页$")
            || TryParseAttachmentRead(text.Trim(), out _, out _)
            || IsOperationQuery(text.Trim()) || text.TrimStart().StartsWith("核对操作", StringComparison.Ordinal)
            || IsReadonlyMemoryCommand(text.Trim()) || CommitmentDetails.TryParse(text, out _)
            || (WeChatText.IsQuery(text.Trim())
                && !System.Text.RegularExpressions.Regex.IsMatch(text,
                    @"不要|不许|禁止|别|先停|停止|暂停|暂缓|等等|撤回|只(?:分析|读|看)|取消|修改|修复|继续|执行|切换|改成|改为|补充|[,，;\r\n；。]",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant));
    internal async Task StopSessionAsync()
    {
        commitmentDetailPages.Clear();
        Volatile.Write(ref identity, null);
        var attachmentDrain = StopAttachmentReadsAsync();
        var wakeStop = StopWakeSessionAsync();
        var commitmentStop = StopCommitmentSessionAsync();
        var ownedStop = StopOwnedSessionAsync();
        var existingStop = StopExistingSessionAsync();
        await coordinator.DrainSessionAsync();
        // A timeout is not proof that an adapter stopped. The caller retains its
        // receiver lease until this real drain completes.
        await coordinator.WaitForSessionDrainAsync();
        await Task.WhenAll(ownedStop, existingStop, commitmentStop, wakeStop, attachmentDrain);
    }
    internal async Task<bool> NotificationKnownAsync(string key, CancellationToken ct)
    {
        var active = Identity;
        if (active == null) return false;
        return (await ledger.ReadConversationAsync(active, ct)).Events.Any(e => e.ExternalEventKey == key);
    }
    internal Task<NotificationEnqueueResult> EnqueueNotificationAsync(string key, string body,
        TaskReference? task, CancellationToken ct, bool isDefaultTaskNotification = false)
        => coordinator.EnqueueNotificationAsync(Identity ?? throw new InvalidOperationException("微信绑定已停止。"),
            key, body, task, cancellationToken: ct, isDefaultTaskNotification: isDefaultTaskNotification);
    internal async Task CaptureNotificationAsync(WeChatNotification message, CancellationToken ct)
    {
        var active = Identity ?? throw new InvalidOperationException("微信绑定尚未激活。");
        string key = "notice/" + message.Key;
        var snapshot = await ledger.ReadConversationAsync(active, ct);
        var existing = snapshot.NotificationDrafts.SingleOrDefault(d => d.EventKey == key);
        if (existing != null)
        {
            var captured = JsonSerializer.Deserialize<WeChatNotification>(existing.PayloadJson, Settings.Json)
                ?? throw new IOException("原通知草稿无法读取。");
            // Observing the same transition again after a storage failure may
            // occur later. Preserve its first observation time; compare all
            // task and public content fields through the ledger's strict key.
            message = message with { CreatedAt = captured.CreatedAt };
        }
        else if (snapshot.Events.Any(e => e.ExternalEventKey == key))
            return; // Previously finalized v1 notification has no draft to prepare.
        message = message with { Body = message.NaturalBody ?? message.Body };
        // Only public report evidence is retained. Bound large reports before
        // serializing a durable preparation job; never include channel tokens.
        if (message.Completion?.Report is { } report && report.Text.Length > 16000)
            message = message with { Completion = message.Completion with
            { Report = report with { Text = WeChatText.Clean(report.Text, 16000), Truncated = true } } };
        try
        {
            await ledger.CaptureNotificationAsync(active, key, message.NaturalBody ?? message.Body,
                JsonSerializer.Serialize(message, Settings.Json), message.Task,
                maximumPreparationAttempts: 2, cancellationToken: ct, isDefaultTaskNotification: true);
        }
        catch (LedgerException error) when (error.Code == "notification_suppressed")
        { /* An explicit subscription governs this exact task and turn. */ }
    }
    internal async Task<WeChatNotification[]> PendingNotificationsAsync(CancellationToken ct)
    {
        var active = Identity;
        if (active == null) return [];
        var drafts = await ledger.GetPendingNotificationDraftsAsync(active, ct);
        return drafts.Where(draft => draft.WorkRunId == null).Select(draft => JsonSerializer.Deserialize<WeChatNotification>(draft.PayloadJson, Settings.Json)
            ?? throw new IOException("通知草稿无法读取；原记录保持不变。")).ToArray();
    }
    internal async Task<string?> PrepareNotificationAsync(string messageKey,
        Func<WeChatNotification, CancellationToken, Task<PreparedCompletion>> prepare, CancellationToken ct)
    {
        var active = Identity ?? throw new OperationCanceledException(ct);
        var draft = (await ledger.GetPendingNotificationDraftsAsync(active, ct))
            .SingleOrDefault(d => d.EventKey == "notice/" + messageKey);
        if (draft == null) return null;
        var message = JsonSerializer.Deserialize<WeChatNotification>(draft.PayloadJson, Settings.Json)
            ?? throw new IOException("通知草稿无法读取；原记录保持不变。");
        if (message.Task != draft.Task || draft.EventKey != "notice/" + message.Key)
            throw new IOException("通知草稿的任务关联不一致，未发送。");
        string fallback = message.NaturalBody ?? draft.ContextBody;
        if (draft.PreparationAttempts >= draft.MaximumPreparationAttempts || message.Completion == null)
        {
            await ledger.FinalizeNotificationFallbackAsync(active, draft.Id, fallback,
                WeChatReports.Pages(fallback), ct);
            coordinator.NotifyDeliveryAvailable(active);
            return fallback;
        }
        var claim = await ledger.BeginNotificationPreparationAsync(active, draft.Id, ct);
        try
        {
            var prepared = await prepare(message, ct);
            ct.ThrowIfCancellationRequested();
            if (Identity != active) throw new OperationCanceledException(ct);
            await ledger.FinalizeNotificationAsync(claim, prepared.Text, WeChatReports.Pages(prepared.Text), ct,
                outputDependencies: prepared.Dependencies);
            coordinator.NotifyDeliveryAvailable(active);
            return prepared.Text;
        }
        finally
        {
            // The ledger treats an already finalized claim idempotently. A
            // cancelled read-only preparation releases its claim, not its budget.
            await ledger.EndNotificationPreparationAsync(claim, CancellationToken.None);
        }
    }
    internal async Task EnqueueControlReceiptAsync(ControlReceipt receipt, CancellationToken ct)
    {
        var active = Identity;
        if (active == null) return;
        string expectedOwner = WeChatText.Hash(active.UserId + "\n" + active.BindingId + "\n" + active.ConversationId);
        bool StillCurrent() => IsCurrentBinding(active) && (receipt.QueueScope != null
            ? queue.IsCurrentReceipt(receipt, expectedOwner) : queue.IsCurrentAttention(receipt, expectedOwner));
        if (!StillCurrent()) return;
        var captured = receipt.QueueScope;
        string? body = captured == null ? receipt.Body : captured.State switch
        {
            "submitted" => "排队的指令已送进原任务。还没有确认它开始运行。",
            "expired" => "这条指令等了超过一天，已停止自动投递。",
            "failed" or "rejected" => "这条指令未能投递到原任务，可以查看工作列表核对。",
            "unconfirmed" => "这次投递的结果还不能确认，我不会自动重发。",
            "held_constraints_changed" => "后来收到了新的要求，原来排队的指令还没投递。请按最新要求重新说明要做什么。",
            "held_target_changed" => "原任务的身份、目录或轮次已经变化，排队的指令还没投递。请核对目标后重新说明要求。",
            _ => null
        };
        if (body == null) return;
        if (captured != null)
        {
            var saved = await ledger.ReadConversationAsync(active, ct);
            if (!StillCurrent()) return;
            var operation = saved.Operations.FirstOrDefault(o => o.Action == "send_to_task"
                && queue.EntryForSource(o.Id.ToString("N"))?.Id == receipt.Id);
            if (operation != null)
            {
                var stage = captured.State switch
                {
                    "submitted" => OperationStage.Accepted,
                    "unconfirmed" => OperationStage.Unknown,
                    "failed" or "rejected" or "expired" => OperationStage.Failed,
                    _ => (OperationStage?)null
                };
                if (stage != null)
                    await ledger.RecordOperationObservationAsync(active, operation.Id,
                        new OperationObservation(stage.Value, JsonSerializer.Serialize(new
                        {
                            queueId = receipt.Id, phase = captured.State, observedTurnId = captured.TurnId,
                            codexAccepted = captured.State == "submitted", runningObserved = false,
                            completionObserved = false, detail = captured.Detail
                        }), operation.TargetKey, operation.Task), ct);
            }
        }
        if (!StillCurrent()) return;
        await coordinator.EnqueueNotificationAsync(active, "control/" + receipt.Id, body, receipt.Task,
            ConversationSource.ToolResult, ct);
    }
    private async Task<DurableFastReply?> ReadonlyFastAsync(InboxMessage message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsCurrentBinding(message.Identity)) throw new OperationCanceledException("微信绑定已经变化。", ct);
        if (message.HasUnresolvedReference && attachments != null && message.Body.StartsWith(AssistantInboundAttachments.Marker, StringComparison.Ordinal))
        {
            string attachmentId = message.Body[AssistantInboundAttachments.Marker.Length..].Split('\n')[0];
            var descriptor = attachments.Descriptor(message.Identity, attachmentId);
            string receipt = descriptor.ItemType == 2 ? "图片收到了。" : "文件收到了。";
            string next = descriptor.ItemType == 2 && !imageAnalysisEnabled ? "这个测试版尚未开启图片分析。" : settings.ChatGpt.Enabled
                ? descriptor.ItemType == 2 ? "想看哪里，直接告诉我，例如“分析刚收到的图片”。" : "可以直接说“读取刚收到的文件”，或告诉我要找什么。"
                : descriptor.ItemType == 2 ? "开启模型问答后，可以直接问我图里的内容。" : "查看文字：读取附件 " + attachmentId;
            return new([receipt + next + "\n编号：" + attachmentId]);
        }
        if (message.HasUnresolvedReference)
            return new(["这条引用暂时无法对应到原消息。请直接写明任务名称和要做的事；这次没有执行操作。"]);
        string text = message.Body.Trim();
        if (TryParsePreparedFiles(text, out int artifactPage, out string? artifactQuery)) return await PreparedFilesAsync(message, artifactPage, artifactQuery, ct);
        if (text == "附件列表") return await AttachmentListReplyAsync(message, ct);
        if (text.StartsWith("读取附件 ", StringComparison.Ordinal))
        {
            if (!TryParseAttachmentRead(text, out string attachmentId, out int offset))
                return new(["请使用“读取附件 编号”，或直接复制上一条回复中的继续读取命令。"]);
            return await StartAttachmentReadAsync(message, attachmentId, ct, offset);
        }
        if (message.ReferencedEventId is { } quoteId)
        {
            var quote = (await ledger.ReadConversationAsync(message.Identity, ct)).Events.SingleOrDefault(e => e.Id == quoteId);
            if (quote?.SourceMessageId?.StartsWith("channel-reference/", StringComparison.Ordinal) == true)
            {
                if (text is "引用任务" or "这条属于哪个任务" or "这条属于哪个任务？")
                    return new([quote.Task is { } task
                        ? "这条消息关联的是主机 " + task.HostId + " 上的任务 " + task.ThreadId
                            + (task.TurnId == null ? "；原记录没有具体轮次。" : "，轮次 " + task.TurnId + "。")
                            + (task.ProfileId == null ? "" : "配置：" + task.ProfileId + "。")
                        : "这条消息没有关联具体任务，不能据此确定要操作哪个任务。"], quote.OutputDependencies);
                if (QuotedTaskContinuation.IsRequest(text))
                {
                    if (quote.Task == null) return new(["这条消息没有关联具体任务，请写明要继续的任务。"]);
                    if (!settings.ChatGpt.Enabled) return new(["已找到引用的任务，但模型尚未开启，目前不能解析接续请求。"]);
                    return null; // The ordinary tool loop retains the new user's exact text and checks task capability.
                }
                if (IsTrustedFastCandidate(text))
                    return new(["已找到你引用的消息。要执行操作，请另发一条文字，写明任务和要求；引用目前用于查看和问答。"]);
                if (text is "引用测试" or "查看引用" || !settings.ChatGpt.Enabled)
                    return new([.. WeChatReports.Pages("你引用的是这条消息：\n" + quote.Body
                        + (text is "引用测试" or "查看引用" ? "" : "\n\n模型问答尚未开启，目前可以定位和查看原文。"))], quote.OutputDependencies);
            }
        }
        var memoryCapture = IsReadonlyMemoryCommand(text)
            ? model.Memory.CaptureForGeneration(true, commitmentClock().ToUniversalTime()) : null;
        string? reply = null;
        if (text is "引用任务" or "这条属于哪个任务" or "这条属于哪个任务？") reply = "请引用要查询的那条助手消息，再发送“引用任务”。";
        else if (text.Length == 0) reply = "这条消息没有可读取的文字。";
        else if (text.StartsWith("[无法处理的微信消息] 含未支持的附件或消息项。", StringComparison.Ordinal))
            reply = "这条消息包含暂未支持的附件或消息项。请把要做的事和准确目标单独发成文字；这次没有执行工作指令。";
        else if (text.Length > 4000 || text.StartsWith("[无法处理的微信消息]", StringComparison.Ordinal))
            reply = "这条消息暂时无法完整处理，尚未作为工作指令执行。请缩短文字后再发。";
        else if (text is "文件帮助" or "附件帮助") reply = FileHelp();
        else if (text is "帮助" or "help" or "开始")
            reply = channel.ReadOnlyReply(text) + "\n\n也可以说“十分钟后提醒我看结果”或“当前任务失败才叫我”。"
                + "发“事项列表”查看已保存的安排；发“事项详情 完整编号”查看完整内容和处理记录。暂停、恢复、取消或删除时写完整事项编号。"
                + "\n例如“设置事项 完整编号 安静时段 22:00-08:00”。提醒只在助手运行时检查，发送仍受渠道可用性影响。"
                + "\n发“文件帮助”查看附件读取、图片分析和文件发送用法；发“渠道能力”查看可用范围；发“记忆来源 完整编号”核对一条记忆的来源。"
                + "\n发“唤醒记录”查看短提示的实际回执；发“补报记录”查看正文补报。短提示受理不表示正文已补报或手机已收到。";
        else if (text.StartsWith("核对操作", StringComparison.Ordinal)) reply = ExistingControlRuntime.TryParseReconcile(text, out var operationId)
            ? ExistingRuntime == null ? "已有任务只读核对尚未配置。" : await ExistingRuntime.ReconcileOperationAsync(message, operationId, ct)
            : "请发送“核对操作 完整操作编号”。这项查询不会重发控制请求。";
        else if (text == "刷新项目来源") reply = await RefreshOwnedProjectSourceAsync(message, ct);
        else if (WeChatText.IsQuery(text)) reply = channel.ReadOnlyReply(text);
        else if (text is "AI状态" or "ai状态") reply = model.Status;
        else if (text is "控制能力" or "能力状态") reply = ControlCapabilitySummary();
        else if (text == "已有控制") reply = ExistingCapabilitySummary();
        else if (text is "渠道能力" or "媒体能力") reply = ChannelCapabilitySummary();
        else if (text == "提醒状态") reply = await ReadCommitmentBudgetAsync(message.Identity, ct);
        else if (text is "唤醒记录" or "唤醒状态") reply = await ReadWakeStatusAsync(message.Identity, ct);
        else if (text is "补报" or "补报记录" or "补报状态") reply = await ReadCatchUpStatusAsync(message.Identity, ct);
        else if (MemorySourceId(text) is { } memoryId)
        {
            if (!settings.ChatGpt.PersonalizationEnabled) reply = "规则与长期记忆尚未启用，暂不读取记忆来源。";
            else
            {
                var source = model.Memory.SourceDetails(memoryId);
                if (!IsCurrentBinding(message.Identity)) throw new OperationCanceledException("微信绑定已经变化。", ct);
                reply = source == null ? "当前记忆中没有这个编号，请发“查看记忆”核对。"
                    : $"记忆 {source.MemoryId}\n{source.Detail}";
            }
        }
        else if (IsOperationQuery(text)) reply = await ReadOperationsFastAsync(message, ct);
        else if (DependentWorkToolAdapter.TryListPage(text, out int dependencyPage)) reply = await dependencies.FormatListAsync(message.Identity, ct, dependencyPage);
        else if (CommitmentDetails.IsCandidate(text)) reply = await ReadCommitmentDetailsAsync(message.Identity, text, ct);
        else if (CommitmentToolAdapter.TryReminderSearch(text, out string reminderQuery, out int reminderPage))
            reply = await commitments.FormatReminderSearchAsync(message.Identity, reminderQuery, reminderPage, ct);
        else if (CommitmentToolAdapter.TryListPage(text, out int commitmentPage))
        {
            reply = await commitments.FormatListAsync(message.Identity, ct, commitmentPage);
            if (text.StartsWith("你还记着", StringComparison.Ordinal) && !(await ledger.GetDependentWorkAsync(message.Identity, ct)).WorkItems.IsEmpty)
                reply += "\n\n" + await dependencies.FormatListAsync(message.Identity, ct);
        }
        else if (TryWorkListPage(text, out int workPage))
        {
            var entries = queue.Entries.Where(e => TaskControlQueue.IsPending(e) || e.State is "sending" or "unconfirmed").ToArray();
            var saved = await ledger.ReadConversationAsync(message.Identity, ct);
            var work = saved.InputWork.Where(w => w.Stage is InputWorkStage.Queued or InputWorkStage.Reading
                or InputWorkStage.NeedsConfirmation or InputWorkStage.Failed or InputWorkStage.Unknown).ToArray();
            var lines = entries.Select(e => $"[{e.Id}] {QueueState(e.State)}：{SingleLine(e.Instruction, 100)}"
                + (string.IsNullOrWhiteSpace(e.Detail) ? "" : " · " + SingleLine(e.Detail, 120))).ToList();
            lines.AddRange(work.Select(w =>
            {
                var original = saved.Inbox.Single(i => i.SourceMessageId == w.SourceMessageId);
                string state = w.Stage switch
                {
                    InputWorkStage.Queued => "以前的问题等待补处理",
                    InputWorkStage.Reading => "正在补做只读查询",
                    InputWorkStage.NeedsConfirmation => "旧请求需要按最新要求重新明确，尚未执行",
                    InputWorkStage.Unknown => "答复发送结果待核对",
                    _ => "以前的输入处理未完成"
                };
                return $"[{w.Id.ToString("N")}] {state}：{SingleLine(original.Body, 100)}";
            }));
            lines.AddRange(saved.Operations.Where(operation => (OwnedControlToolAdapter.Handles(operation.Action) || ExistingControlRuntime.Handles(operation.Action))
                && operation.Stage is OperationStage.Queued or OperationStage.Dispatching or OperationStage.Accepted
                    or OperationStage.Running or OperationStage.Unknown)
                .OrderByDescending(operation => operation.CreatedAt).ThenBy(operation => operation.Id).Select(operation => OperationLine(operation) + " · 完整编号 " + operation.Id.ToString("N")));
            int total = lines.Count;
            int pages = Math.Max(1, (total + 11) / 12);
            reply = workPage > pages ? "只有 " + pages + " 页，请发“工作列表 1”重新查看。"
                : total == 0 ? "目前没有排队、挂起或结果待核对的指令。"
                : "第 " + workPage + "/" + pages + " 页，共 " + total + " 条指令。\n"
                    + string.Join("\n", lines.Skip((workPage - 1) * 12).Take(12))
                    + (workPage < pages ? "\n下一页：工作列表 " + (workPage + 1) : "");
            if ((await ledger.GetCommitmentsAsync(message.Identity, ct)).WorkItems.Length > 0)
                reply += "\n提醒与订阅：发“事项列表”查看。";
            if (!(await ledger.GetDependentWorkAsync(message.Identity, ct)).WorkItems.IsEmpty)
                reply += "\n依赖工作：发“依赖列表”查看。";
        }
        else if (IsReadonlyMemoryCommand(text))
        {
            // Project management rows contain IDs and scope labels only. This
            // personal-only read must never carry project prose into later history.
            if (model.TryMemoryCommand(text, out var memoryReply)) reply = memoryReply;
        }
        else if (text is "先停一下" or "停止运行" && (OwnedRuntime == null || OwnedRuntime.Profiles.Count == 0)
            && (ExistingRuntime == null || ExistingRuntime.Profiles.Count == 0))
            reply = "当前接入的原任务暂时不能从这里中断。可以发“取消接续”撤回尚未投递的指令；运行中的任务需要在 Codex 中停止。";
        else if (!settings.ChatGpt.Enabled && !IsTrustedFastCandidate(text))
            reply = "模型问答尚未启用。状态、报告、工作列表和明确的取消请求仍可处理。";
        if (reply == null) return null;
        if (memoryCapture != null && !model.Memory.IsGenerationPersonalizationCurrent(memoryCapture,
                true, commitmentClock().ToUniversalTime()))
            throw new OperationCanceledException("读取期间记忆已变化，没有发送旧内容。", ct);
        return new DurableFastReply([reply], memoryCapture == null ? OutputDependencies.Independent
            : AssistantModelContextProvider.ExplicitReadDependencies(memoryCapture));
    }
    private static bool IsTrustedFastCandidate(string text)
        => text.StartsWith("发送文件 ", StringComparison.Ordinal) || IsConversationCommand(text) || IsOwnedFastCandidate(text) || IsExistingFastCandidate(text) || text is "开启自动记忆" or "关闭自动记忆"
            || CommitmentToolAdapter.IsCandidate(text)
            || DependentWorkToolAdapter.IsCandidate(text)
            || text == "取消接续" || text.StartsWith("取消", StringComparison.Ordinal)
            || MemoryCommandResolver.Resolve(text, Array.Empty<AssistantMemoryEntry>()).Status != MemoryCommandStatus.None;
    private static bool IsReadonlyMemoryCommand(string text)
        => text is "自动记忆状态" or "记忆帮助" or "你记住了什么" or "你记住了什么？" or "你记住了什么?"
            || MemorySourceId(text) != null
            || System.Text.RegularExpressions.Regex.IsMatch(text,
                @"^(?:(?:查看记忆|记忆列表|长期记忆|我的记忆|记忆|查看规则|查看\s*AGENTS\.md|规则)(?:\s+\d{1,3})?)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static string? MemorySourceId(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"\A记忆来源\s+([0-9A-Fa-f]{8})\z",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }
    private string ChannelCapabilitySummary() => AssistantChannelCapabilities.Describe(
        settings.WeChat.ClawBotEnabled, settings.WeChat.PushPlusEnabled, settings.ChatGpt.Enabled, channel.ConnectionState, settings.ActiveMedia);
    private async Task<DurableFastReply?> TrustedFastAsync(DurableFastContext context, CancellationToken ct)
    {
        if (!IsCurrentBinding(context.Message.Identity) || !context.StillCurrent())
            throw new OperationCanceledException("当前消息或绑定已经变化。", ct);
        string text = context.Message.Body.Trim();
        if (await SendFileCommandAsync(context, ct) is { } fileReply) return fileReply;
        if (await HandleConversationCommandAsync(context, ct) is { } conversationReply) return conversationReply;
        var dependency = await dependencies.ResolveAsync(context.Message, ct);
        if (dependency.Candidate)
            return dependency.Call is { } dependencyCall ? await ExecuteLocalFastAsync(context, dependencyCall, ct)
                : new([dependency.Reply ?? "依赖工作尚未登记，请明确前置条件和目标。"]);
        if (await HandleExistingFastAsync(context, ct) is { } existingReply) return existingReply;
        if (await HandleOwnedFastAsync(context, ct) is { } ownedReply) return ownedReply;
        if (text == "取消刚才那条")
            return new(["请明确要取消哪一项。发“工作列表”查看待投递指令，再说“取消 编号”；提醒或订阅请先查看“事项列表”。"]);
        var commitment = await commitments.ResolveAsync(context.Message, ct);
        if (commitment.Candidate)
            return commitment.Call is { } commitmentCall
                ? await ExecuteLocalFastAsync(context, commitmentCall, ct)
                : new([commitment.Reply ?? "这项安排还需要明确目标和条件，尚未保存。"]);
        ModelToolCall? call = null;
        if (text is "开启自动记忆" or "关闭自动记忆")
            call = Call("set_auto_memory", new { enabled = text == "开启自动记忆" });
        else if (text == "取消接续")
        {
            var pending = queue.Entries.Where(TaskControlQueue.IsPending).ToArray();
            if (pending.Length == 0) return new(["目前没有待投递的指令；运行中的任务没有被中断。"]);
            if (pending.Length > 1) return new(["有多条待投递指令，请发“工作列表”查看编号，再说“取消 编号”。"]);
            call = Call("cancel_queued", new { operationId = pending[0].Id });
        }
        else if (text.StartsWith("取消", StringComparison.Ordinal))
        {
            var candidates = queue.Entries.Where(e => TaskControlQueue.IsPending(e)
                && (text.Contains(e.Id, StringComparison.OrdinalIgnoreCase) || text.Contains(e.Id[..8], StringComparison.OrdinalIgnoreCase))).ToArray();
            if (candidates.Length == 0)
                return new(["没有找到与这句话对应的待投递指令，这次没有取消任何操作。发“工作列表”查看编号，再发“取消 编号”；提醒或订阅请发“事项列表”。"]);
            if (candidates.Length > 1)
                return new(["这句话对应多条待投递指令，请从“工作列表”复制一条完整编号，再发“取消 编号”。这次没有取消任何操作。"]);
            call = Call("cancel_queued", new { operationId = candidates[0].Id });
        }
        else
        {
            if (MemoryCommandResolver.Resolve(context.Message.Body, Array.Empty<AssistantMemoryEntry>()).Status == MemoryCommandStatus.None)
                return null;
            if (!settings.ChatGpt.PersonalizationEnabled) return new(["规则与长期记忆尚未启用，这次没有修改记录。"]);
            var resolved = MemoryCommandResolver.Resolve(context.Message.Body, model.Memory.Entries);
            if (resolved.Status is MemoryCommandStatus.NeedsClarification or MemoryCommandStatus.Rejected)
                return new([resolved.Message + (resolved.CandidateIds.IsEmpty ? "" : "\n编号：" + string.Join("、", resolved.CandidateIds))]);
            if (resolved.IsReady)
                call = resolved.Action switch
                {
                    "remember" => Call("remember", new { text = resolved.Text, scope = "personal" }),
                    "forget" => Call("forget", new { memoryId = resolved.MemoryId }),
                    "update_memory" => Call("update_memory", new { memoryId = resolved.MemoryId, text = resolved.Text }),
                    _ => null
                };
        }
        if (call == null) return null;
        return await ExecuteLocalFastAsync(context, call, ct);
    }
    private async Task<DurableFastReply> ExecuteLocalFastAsync(DurableFastContext context, ModelToolCall call,
        CancellationToken ct)
    {
        if (!IsCurrentBinding(context.Message.Identity) || !context.StillCurrent())
            throw new OperationCanceledException("当前消息或绑定已经变化。", ct);
        var catalog = AssistantToolExecutor.Catalog();
        if (!catalog.TryGet(call.Name, out var descriptor) || !descriptor.Parameters.TryValidate(call.ArgumentsJson, out var canonical, out _))
            return new(["参数还不完整，这次没有修改记录或执行控制。"]);
        call = call with { ArgumentsJson = canonical };
        var permission = await tools.AuthorizeAsync(new(context.Message, call, descriptor), ct);
        if (!permission.Allowed) return new([permission.Reason ?? "这句话还不能确定为执行请求，这次没有修改记录或执行控制。"]);
        if (!tools.GetCapability(descriptor.Capability).Available) return new(["该功能尚未启用，这次没有修改记录或执行控制。"]);
        if (!context.StillCurrent()) throw new OperationCanceledException(ct);
        string key = WeChatText.Hash(context.Message.SourceMessageId + "\n" + call.Name + "\n" + call.ArgumentsJson);
        var saved = await ledger.ReadConversationAsync(context.Message.Identity, ct);
        var operation = saved.Operations.SingleOrDefault(o => o.IdempotencyKey == key);
        if (operation != null)
            return new(["这条请求已有操作记录，我没有重复执行。可以发“工作列表”核对。"]);
        bool memoryMutation = ManualMemoryTools.Handles(call.Name);
        var lease = await ledger.BeginGenerationAsync(context.Message.Identity,
            [context.Message.SourceMessageId], context.Message.Task, ct,
            outputDependencies: memoryMutation ? null : OutputDependencies.Independent);
        ToolInvocationRecord? preparedInvocation = null;
        bool dispatchStarted = false;
        try
        {
            GenerationContext? generationContext = memoryMutation ? null : await ledger.GetGenerationContextAsync(lease, ct);
            if (memoryMutation)
                generationContext = await ledger.BindGenerationContextAsync(lease, await modelContext.CaptureAsync(lease, ct), ct);
            var invocation = await ledger.RegisterToolInvocationAsync(lease, context.Message.SourceMessageId,
                call.Id, call.Name, canonical, true, ct);
            preparedInvocation = invocation;
            var grant = await ledger.AuthorizeCurrentUserRequestAsync(context.Message.Identity, context.Message.SourceMessageId,
                call.Name, permission.TargetKey!, canonical, permission.SourceExcerpt!, permission.Task, ct);
            operation = await ledger.QueueOperationAsync(context.Message.Identity, grant.Id, key, ct);
            await ledger.BeginToolInvocationAsync(lease, invocation.Id, operation.Id, ct);
            dispatchStarted = true;
            ToolExecutionResult result;
            try
            {
                result = !IsCurrentBinding(context.Message.Identity) || !context.StillCurrent() || ct.IsCancellationRequested
                    ? new(ToolExecutionState.Rejected, JsonSerializer.SerializeToElement(new { code = "local_fast_source_changed", dispatched = false }))
                    : await tools.ExecuteAsync(new(context.Message.Identity, context.Message.SourceMessageId,
                        context.Message.Revision, invocation.Id, operation.Id, key, GenerationContext: generationContext), call, ct);
            }
            catch (Exception)
            {
                result = new(ToolExecutionState.Unknown,
                    JsonSerializer.SerializeToElement(new { code = "local_fast_outcome_unknown", automaticRetry = false }));
            }
            var state = result.State == ToolExecutionState.Completed ? OperationStage.Completed
                : result.State == ToolExecutionState.Rejected ? OperationStage.Failed : OperationStage.Unknown;
            await ledger.FinishToolInvocationAsync(context.Message.Identity, invocation.Id,
                state == OperationStage.Unknown ? ToolInvocationStage.Unknown : ToolInvocationStage.Completed,
                result.Facts.GetRawText(), state, CancellationToken.None,
                new(state, result.Facts.GetRawText(), result.TargetKey ?? operation.TargetKey, result.Task ?? operation.Task));
            if (state == OperationStage.Completed && result.ContextTransition is { } transition)
            {
                if (generationContext == null
                    || !await modelContext.AcceptTransitionAsync(context.Message.Identity, generationContext, transition, ct))
                    throw new OperationCanceledException("操作已经记账，但记忆随后变化；旧上下文不再继续使用。", ct);
                await ledger.AdvanceGenerationContextAsync(lease, transition, ct);
            }
            if (state == OperationStage.Unknown)
                return new(["这次操作的结果还不能确认，我不会自动重试。"]);
            if (state != OperationStage.Completed)
                return new(["这次操作没有确认成功。请查看原记录后再决定。"]);
            if (CommitmentToolAdapter.Handles(call.Name))
                return new([CommitmentToolAdapter.DescribeReceipt(call, result)]);
            if (DependentWorkToolAdapter.Handles(call.Name))
                return new([DependentWorkToolAdapter.DescribeReceipt(result)]);
            return call.Name switch
            {
                "cancel_queued" => new(["这条待投递指令已取消。运行中的任务没有被中断。"]),
                "remember" => new(["记住了。编号 " + result.Facts.GetProperty("memoryId").GetString() + "，之后可以查看、修改或删除。"]),
                "forget" => new(["这条长期记忆已删除。历史对话和独立备份仍各自保留。"]),
                "set_auto_memory" => new([settings.ChatGpt.AutoMemoryEnabled
                    ? "自动记忆已开启。只保存当前消息中明确的长期偏好；可以随时查看或删除。"
                    : "自动记忆已关闭。已有记忆仍保留，可以单独查看或删除。"]),
                _ => new(["这条记忆已更新。"])
            };
        }
        catch (LedgerException error) when (!dispatchStarted
            && error.Code is LedgerErrors.DependencyInvalidated or LedgerErrors.DependencyUnavailable)
        {
            return new(["用于这次请求的记忆已经变化或暂时无法读取，这次没有执行修改。"]);
        }
        finally
        {
            try
            {
                if (!dispatchStarted && preparedInvocation != null)
                    await ledger.AbandonPreparedToolInvocationAsync(lease, preparedInvocation.Id, operation?.Id,
                        "local_fast_not_started", CancellationToken.None);
            }
            finally
            {
                modelContext.Release(lease);
                await ledger.EndGenerationAttemptAsync(lease, CancellationToken.None);
            }
        }
    }
    private static ModelToolCall Call(string name, object args) => new(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(args));
    internal void ApplyInputMergeWindow(int milliseconds)
    {
        if (milliseconds is < 0 or > 3000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        coordinator.SetMergeWindow(TimeSpan.FromMilliseconds(milliseconds));
    }
    internal static bool CanGroup(InboxMessage earlier, InboxMessage current)
    {
        return current.ReceivedAt >= earlier.ReceivedAt
            && current.ReceivedAt - earlier.ReceivedAt <= TimeSpan.FromSeconds(1)
            && IsExplicitSupplement(earlier, current);
    }
    private static bool IsExplicitSupplement(InboxMessage earlier, InboxMessage current)
    {
        if (earlier.Task != current.Task || earlier.ReferencedEventId != current.ReferencedEventId
            || earlier.HasUnresolvedReference || current.HasUnresolvedReference) return false;
        // Only clearly marked additions are combined. A separate subject remains
        // a separate inbox record and visible history, never a concatenated command.
        return new[] { "还有", "另外补充", "补充", "不要", "先别", "只", "而且" }.Any(current.Body.TrimStart().StartsWith);
    }
    internal static bool IsExplicitCorrection(InboxMessage earlier, InboxMessage current)
    {
        if (earlier.Task != current.Task || earlier.ReferencedEventId != current.ReferencedEventId
            || earlier.HasUnresolvedReference || current.HasUnresolvedReference) return false;
        var text = current.Body.TrimStart();
        return (text.StartsWith("不对，") || text.StartsWith("不对,") || text.StartsWith("刚才说错了"))
            && (text.Contains("改成") || text.Contains("改为"));
    }
    private static string QueueState(string state) => state switch
    {
        "queued" => "尚未投递", "held_constraints_changed" => "等待核对最新要求",
        "held_target_changed" => "等待核对任务变化",
        "sending" => "正在投递", _ => "结果待核对"
    };
    private Task<T> OnUi<T>(Func<Task<T>> action, CancellationToken ct)
        => dispatcher.CheckAccess() ? action() : dispatcher.InvokeAsync(action, DispatcherPriority.Normal, ct).Task.Unwrap();
    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync(); await coordinator.DisposeAsync(); await ledger.DisposeAsync();
    }
    private sealed class WeChatSegmenter : IReplySegmenter
    {
        public ImmutableArray<string> Split(ImmutableArray<string> replies)
            => replies.SelectMany(WeChatReports.Pages).ToImmutableArray();
    }
    private sealed class UiAdapters(Dispatcher dispatcher, IToolModel model, IToolExecutor executor, IUserAuthorizationPolicy policy,
        Func<bool> modelEnabled, Func<ConversationIdentity, bool> isCurrentBinding)
        : IToolModel, IToolExecutor, IUserAuthorizationPolicy
    {
        public Task<ToolModelResponse> GenerateAsync(ToolModelRequest request, CancellationToken ct)
            => dispatcher.InvokeAsync(() => modelEnabled() && isCurrentBinding(request.CurrentMessage.Identity) ? model.GenerateAsync(request, ct)
                : Task.FromException<ToolModelResponse>(new InvalidOperationException("assistant_model_disabled")),
                DispatcherPriority.Normal, ct).Task.Unwrap();
        public ToolCapability GetCapability(string capability) => executor.GetCapability(capability);
        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, ModelToolCall call, CancellationToken ct)
            => dispatcher.InvokeAsync(() => executor.ExecuteAsync(context, call, ct), DispatcherPriority.Normal, ct).Task.Unwrap();
        public ValueTask<UserAuthorizationDecision> AuthorizeAsync(UserAuthorizationRequest request, CancellationToken ct)
            => new(dispatcher.InvokeAsync(() => policy.AuthorizeAsync(request, ct).AsTask(), DispatcherPriority.Normal, ct).Task.Unwrap());
    }
}
