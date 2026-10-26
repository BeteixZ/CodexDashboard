using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Assistant.Core;
using Codex.Migration;

namespace CodexDashboard;

// New synthetic DPAPI identities and explicit temporary data only. This does not implement
// production receiver exclusion and must never be used as a production migration guard.
internal static class MigrationHostIntegrationTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--migration-host-test");
        if (index < 0) return false;
        try
        {
            if (args.Length != 2 || index != 0) throw new ArgumentException("Exact synthetic test arguments required.");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); exit(1); }
        return true;
    }
    private static async Task RunAsync(string directory)
    {
        if (Application.Current?.Dispatcher.CheckAccess() != true || !Path.IsPathFullyQualified(directory)
            || directory.Any(char.IsControl) || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("New absolute synthetic WPF test directory required.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Redirected test directory rejected.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Save() => File.WriteAllText(Path.Combine(directory, "migration-host-checks.json"),
            JsonSerializer.Serialize(new { count = checks.Count, passed = checks.Values.All(v => v), checks,
                syntheticOnly = true, productionCredentialsRead = false, realNetworkConnections = 0,
                realTaskOperations = 0, productionMigrationGuardImplemented = false }, Settings.Json));
        void Check(string name, bool value) { checks.Add(name, value); Save(); if (!value) throw new InvalidOperationException(name); }
        var preview = new MigrationPreviewModel();
        Check("preview requires explicit directories and has no default user profile selection",
            !preview.Evaluate(new("", "", "")).Valid);
        var rollback = new Scenario(Path.Combine(directory, "before-activation"));
        var sourceBefore = Hashes(rollback.Request.SourceDirectory);
        using (var settingsLock = new FileStream(rollback.Source("settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        using (var credentialLock = new FileStream(rollback.Source("chatgpt.credentials"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var state = preview.Evaluate(new(rollback.Request.SourceDirectory, rollback.Request.TargetDirectory,
                rollback.Request.TransactionDirectory));
            Check("actual preview succeeds with settings and credential payloads unreadable",
                state.Valid && state.Plan?.RelativeFiles.Count == 5 && state.Categories.Sum(c => c.Count) == 5
                && !Directory.Exists(rollback.Request.TargetDirectory));
        }
        var window = new MigrationPreviewWindow();
        try
        {
            window.SetDirectories(rollback.Request.SourceDirectory, rollback.Request.TargetDirectory, rollback.Request.TransactionDirectory);
            window.Show(); window.Evaluate();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Capture(window, Path.Combine(directory, "migration-preview.png"));
            Check("preview window exposes inspection and close actions without an execution action",
                window.Preview?.Valid == true && window.CheckButton.IsVisible && window.CategoriesList.Items.Count == 5);
            var content = (FrameworkElement)window.Content;
            var buttonBounds = window.CheckButton.TransformToAncestor(content).TransformBounds(
                new Rect(0, 0, window.CheckButton.ActualWidth, window.CheckButton.ActualHeight));
            Check("preview action remains inside the visible content bounds",
                buttonBounds.Left >= 0 && buttonBounds.Top >= 0
                && buttonBounds.Right <= content.ActualWidth && buttonBounds.Bottom <= content.ActualHeight);
        }
        finally { window.Close(); }
        Check("metadata preview preserves source bytes and creates no target or transaction",
            Same(sourceBefore, Hashes(rollback.Request.SourceDirectory))
            && !Directory.Exists(rollback.Request.TargetDirectory) && !Directory.Exists(rollback.Request.TransactionDirectory));
        var denied = await new MigrationEngine().MigrateAsync(rollback.Request);
        Check("product default guard still denies migration", denied.Code == "receiver_guard_required" && !denied.Succeeded);
        var engine = new MigrationEngine(new SyntheticGuard(rollback.Request));
        Check("synthetic guarded migration completes", (await engine.MigrateAsync(rollback.Request)).Succeeded);
        Check("preactivation inspection cannot grant rollback execution",
            engine.Inspect(rollback.Request).Rollback == RollbackAssessment.NeedsReceiverGuardAndIntegrityCheck);
        Check("synthetic preactivation rollback restores exact source bytes",
            (await engine.RollbackAsync(rollback.Request)).Succeeded && Same(sourceBefore, Hashes(rollback.Request.SourceDirectory)));
        bool blocked = false;
        try { using var unexpected = MigrationStartup.AcquireRuntimeLease(rollback.Request.TargetDirectory); }
        catch (InvalidOperationException) { blocked = true; }
        Check("rolled-back target refuses the actual runtime startup lease", blocked);

        var active = new Scenario(Path.Combine(directory, "after-activation"));
        var activeEngine = new MigrationEngine(new SyntheticGuard(active.Request));
        Check("second isolated migration completes", (await activeEngine.MigrateAsync(active.Request)).Succeeded);
        var old = JsonSerializer.Deserialize<Settings>(File.ReadAllText(active.Source("settings.json")), Settings.Json)!;
        Check("old reader sees no binding push token or enabled assistant after handoff",
            old.WeChat.ReadBinding() == null && old.WeChat.ReadPushPlusToken() == ""
            && !old.WeChat.ClawBotEnabled && !old.WeChat.PushPlusEnabled && !old.ChatGpt.Enabled
            && !File.Exists(active.Source("chatgpt.credentials")));
        var oldHttp = new FakeHttp();
        await using (var oldChannel = new WeChatService(old, EmptySnapshot, () => throw new InvalidOperationException("Old source must not save."),
            oldHttp, receiveMessages: true))
        {
            await oldChannel.ConfigureAsync();
            Check("current legacy-compatible channel with scrubbed old settings starts no receive request",
                oldHttp.Calls == 0 && oldChannel.ConnectionState == Codex.Protocol.ConnectionState.NotConfigured);
        }
        await ActivateAndAddDataAsync(active, Check);
        var after = Hashes(active.Request.TargetDirectory);
        var inspection = activeEngine.Inspect(active.Request);
        Check("preview identifies activated target as requiring reconciliation",
            inspection.ActivationRecorded && inspection.Rollback == RollbackAssessment.RequiresReconciliation);
        var refused = await activeEngine.RollbackAsync(active.Request);
        Check("rollback after actual synthetic host activation and new input is rejected without overwriting new records",
            refused.Code == "rollback_requires_reconciliation_after_activation"
            && Same(after, Hashes(active.Request.TargetDirectory)));
        Check("repeated completed migration preserves new host records",
            (await activeEngine.MigrateAsync(active.Request)).Succeeded && Same(after, Hashes(active.Request.TargetDirectory)));
        Save();
    }

    private static async Task ActivateAndAddDataAsync(Scenario scenario, Action<string, bool> check)
    {
        Settings.SetDataDirectory(scenario.Request.TargetDirectory);
        using var runtimeLease = MigrationStartup.AcquireRuntimeLease(Settings.DirectoryPath);
        var settings = Settings.Load();
        var binding = settings.WeChat.ReadBinding()!;
        check("actual Settings.Load preserves stable account bot user cursor seen IDs and send dedup",
            settings.ChatGpt.ActiveAccount == scenario.Account && binding.BotId == scenario.Binding.BotId
            && binding.UserId == scenario.Binding.UserId && binding.Cursor == scenario.Binding.Cursor
            && binding.SeenMessages.SequenceEqual(scenario.Binding.SeenMessages)
            && settings.WeChat.RecentAttempts.SequenceEqual(scenario.Settings.WeChat.RecentAttempts));
        var authHttp = new FakeHttp(reject: true);
        using var auth = new ChatGptAuth(Path.Combine(Settings.DirectoryPath, "chatgpt.credentials"), authHttp);
        var vault = ChatGptVault.Load(Path.Combine(Settings.DirectoryPath, "chatgpt.credentials"));
        check("actual protected vault retains stable host and client identities", vault.HostId == scenario.VaultHost
            && auth.Accounts.Single().ClientId == scenario.Account && auth.Accounts.Single().Subject == "synthetic-migration-subject");
        var memory = new AssistantMemoryStore(Path.Combine(Settings.DirectoryPath, "assistant"), () => scenario.MemoryIdentity);
        memory.Initialize();
        check("actual protected memory and rules retain their stable profile",
            memory.Profile == scenario.MemoryProfile && memory.Entries.Single().Text == "合成迁移偏好：先说明事实"
            && memory.ReadRules() == "# 合成迁移规则\n保持自然回复。\n");
        var modelHttp = new FakeHttp(reject: true);
        using var model = new ChatGptAssistant(settings, settings.Save, auth, modelHttp, memory);
        var transport = new NoTransport();
        var gateway = new CodexGateway(settings, EmptySnapshot, transport);
        await using var queue = new TaskControlQueue(settings, gateway, Path.Combine(Settings.DirectoryPath, "task-control.state"));
        check("actual queue preserves IDs and converts interrupted sending only to unconfirmed",
            queue.Entries.Select(e => e.Id).Order().SequenceEqual(scenario.EntryIds.Order())
            && queue.Entries.Count(e => e.State == "unconfirmed") == 2 && queue.Entries.Count(e => e.State == "queued") == 1);
        var http = new FakeHttp();
        var channel = new WeChatService(settings, EmptySnapshot, settings.Save, http, receiveMessages: false,
            assistant: model, requireDurableAssistant: true);
        var contextProvider = new AssistantModelContextProvider(settings, model.Memory);
        var ledger = await DurableConversationLedger.OpenAsync(
            new FileJournalStorage(Path.Combine(Settings.DirectoryPath, "conversations.state")), new WindowsStateProtector(),
            options: new() { RequireOutputDependencies = true }, dependencyValidator: contextProvider);
        DurableAssistantRuntime? runtime = null;
        try
        {
            runtime = new DurableAssistantRuntime(settings, model, queue, gateway, channel,
                Dispatcher.CurrentDispatcher, ledger, EmptySnapshot, modelContext: contextProvider);
            channel.DurableRuntime = runtime;
            await channel.ConfigureAsync();
            await queue.PumpAsync(EmptySnapshot(), default);
            var persisted = JsonSerializer.Deserialize<ControlJournal>(LocalSecret.Unprotect(
                File.ReadAllText(Path.Combine(Settings.DirectoryPath, "task-control.state"))), Settings.Json)!;
            var all = persisted.Entries.Concat(persisted.History.SelectMany(h => h.Entries)).ToArray();
            check("actual host archives old owner queue without replaying or losing prior IDs",
                all.Select(e => e.Id).Order().SequenceEqual(scenario.EntryIds.Order()) && transport.Calls == 0
                && all.Count(e => e.State == "unconfirmed") == 2 && all.Any(e => e.State == "suspended_identity_changed"));
            await channel.ProcessUpdatesAsync(Batch(binding, scenario.DuplicateId, "继续执行旧任务"), default);
            var state = await ledger.ReadConversationAsync(runtime.Identity!);
            check("migrated seen message is not reaccepted or dispatched by the actual channel",
                state.Inbox.Length == 0 && transport.Calls == 0 && http.Calls == 0 && modelHttp.Calls == 0 && authHttp.Calls == 0);
            await channel.ProcessUpdatesAsync(Batch(binding, "new-synthetic-read", "状态"), default);
            for (int i = 0; i < 250; i++)
            {
                state = await ledger.ReadConversationAsync(runtime.Identity!);
                if (state.Inbox.Length == 1 && state.Outbox.Length > 0 && state.Outbox.All(o => o.Stage == Assistant.Core.OutboxStage.Accepted)) break;
                await Task.Delay(20);
            }
            check("actual migrated host durably handles a new readonly input using only fake delivery",
                state.Inbox.Length == 1 && state.Outbox.Length > 0 && state.Outbox.All(o => o.Stage == Assistant.Core.OutboxStage.Accepted)
                && state.Operations.Length == 0 && transport.Calls == 0 && http.Calls > 0 && modelHttp.Calls == 0 && authHttp.Calls == 0);
        }
        finally
        {
            await channel.DisposeAsync(); await channel.WhenStopped;
            if (runtime != null) await runtime.DisposeAsync();
            else await ledger.DisposeAsync();
        }
    }
    private static WeChatSnapshot EmptySnapshot() => new([], DateTimeOffset.UtcNow, []);
    private static JsonElement Batch(ClawBinding binding, string id, string text) => JsonSerializer.SerializeToElement(new
    {
        ret = 0, msgs = new[] { new { from_user_id = binding.UserId, to_user_id = binding.BotId, message_type = 1,
            message_id = id, create_time_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            context_token = "SYNTHETIC_NEW_CONTEXT", item_list = new[] { new { type = 1, text_item = new { text } } } } }
    });
    private static Dictionary<string, string> Hashes(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Where(p => Path.GetFileName(p) != ".codex-migration.lock").ToDictionary(p => Path.GetRelativePath(directory, p),
            p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))), StringComparer.Ordinal);
    private static bool Same(Dictionary<string, string> first, Dictionary<string, string> second)
        => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out string? hash) && hash == pair.Value);
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle(window.Background, null, bounds);
            context.DrawRectangle(new VisualBrush(visual), null, bounds);
        }
        bitmap.Render(drawing);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }
    private sealed class FakeHttp(bool reject = false) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref Calls);
            if (reject) throw new InvalidOperationException("Synthetic auth/model network was not expected.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"ret\":0}", Encoding.UTF8, "application/json") });
        }
    }
    private sealed class NoTransport : ITaskTransport
    {
        internal int Calls;
        public Task<TaskDelivery> DeliverAsync(TaskRecord task, string text, string messageId, CancellationToken ct)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Migration must not replay a task."); }
    }
    private sealed class SyntheticGuard(MigrationRequest onlyRequest) : ILegacyReceiverGuard
    {
        public ValueTask<IReceiverExclusionLease?> TryAcquireAsync(MigrationRequest request, MigrationDirection direction, CancellationToken cancellation)
            => ValueTask.FromResult<IReceiverExclusionLease?>(request == onlyRequest ? new SyntheticLease() : null);
        private sealed class SyntheticLease : IReceiverExclusionLease
        {
            public bool LegacyStoppedAndWritesBlocked => true;
            public bool AssistantStoppedAndWritesBlocked => true;
            public bool CredentialScopeCompatible => true;
            public ValueTask<bool> RevalidateAsync(CancellationToken cancellation) => ValueTask.FromResult(true);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class Scenario
    {
        internal readonly MigrationRequest Request;
        internal readonly Settings Settings;
        internal readonly ClawBinding Binding;
        internal readonly string Account = "synthetic-migration-client", VaultHost = "urn:uuid:" + Guid.NewGuid();
        internal readonly string DuplicateId = "already-processed-synthetic-command";
        internal readonly string[] EntryIds;
        internal string MemoryIdentity => Account + "\n" + Binding.BaseUrl + "\n" + Binding.BotId + "\n" + Binding.UserId;
        internal string MemoryProfile => WeChatText.Hash(MemoryIdentity).ToLowerInvariant();
        internal Scenario(string root)
        {
            Request = new(Path.Combine(root, "source"), Path.Combine(root, "target"), Path.Combine(root, "transaction"));
            Directory.CreateDirectory(Request.SourceDirectory);
            Binding = new() { BotId = "synthetic-migration-" + Guid.NewGuid().ToString("N"), UserId = "synthetic-migration-user",
                Token = "SYNTHETIC_MIGRATION_TOKEN", Cursor = "SYNTHETIC_MIGRATION_CURSOR",
                ContextToken = "SYNTHETIC_CONTEXT", SeenMessages = [WeChatText.Hash(DuplicateId)] };
            Settings = new() { Version = 2, PythonPath = "synthetic-unused-python", CodexHome = Path.Combine(root, "unused-home"),
                LocalEnabled = false, RemoteHosts = [], ChatGpt = new() { Enabled = true, TaskControlEnabled = true,
                    ActiveAccount = Account, AutoMemoryEnabled = false },
                WeChat = new() { ClawBotEnabled = true, ProtectedPushPlusToken = LocalSecret.Protect("SYNTHETIC_PUSH_TOKEN"),
                    RecentAttempts = [WeChatText.Hash("synthetic-prior-notice")] } };
            Settings.WeChat.WriteBinding(Binding);
            File.WriteAllText(Source("settings.json"), JsonSerializer.Serialize(Settings, CodexDashboard.AssistantSettings.Json));
            new ChatGptVault { HostId = VaultHost, Accounts = [new() { ClientId = Account, Subject = "synthetic-migration-subject",
                AccessToken = "SYNTHETIC_MODEL_TOKEN", RefreshToken = "SYNTHETIC_REFRESH_TOKEN",
                Scope = ChatGptAuth.Scopes, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) }] }.Save(Source("chatgpt.credentials"));
            string owner = WeChatText.Hash("synthetic-old-owner");
            var entries = new[] { "sending", "unconfirmed", "queued" }.Select((state, i) => new ControlEntry
            {
                Id = WeChatText.Hash(owner + "\ncommand-" + i), TaskKey = "本机:synthetic-task-" + i,
                State = state, Instruction = "合成旧指令", CreatedAt = DateTimeOffset.UtcNow, TurnId = ""
            }).ToList();
            EntryIds = entries.Select(e => e.Id).ToArray();
            File.WriteAllText(Source("task-control.state"), LocalSecret.Protect(JsonSerializer.Serialize(
                new ControlJournal { Owner = owner, Entries = entries }, CodexDashboard.AssistantSettings.Json)));
            var memory = new AssistantMemoryStore(Source("assistant"), () => MemoryIdentity);
            memory.Initialize(); memory.Add("合成迁移偏好：先说明事实"); memory.SaveRules("# 合成迁移规则\n保持自然回复。\n");
        }
        internal string Source(string relative) => Path.Combine(Request.SourceDirectory, relative);
    }
}
