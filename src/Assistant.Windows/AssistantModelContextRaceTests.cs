using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// Deterministic expiry interleavings in the actual host provider. There is no model or transport.
internal static class AssistantModelContextRaceTests
{
    private const string OldBody = "OLD_DEFERRED_MEMORY_SOURCE：这条旧问题仍含已到期资料。";
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--model-context-race-test");
        if (index < 0) return false;
        try
        {
            if (index != 0 || args.Length != 2) throw new ArgumentException("Explicit synthetic directory required.");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl)
            || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("New absolute test directory required.");
        directory = Path.GetFullPath(directory);
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(directory)!); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Redirected output rejected.");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "model-context-race-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, passed = checks.Values.All(v => v), checks,
                actualCodexProcesses = 0, actualNetworkRequests = 0, actualModelCalls = 0, productionDataRead = false,
                runtime = "actual model context provider, scripted clock and synthetic protected ledger"
            }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        await CaptureBoundaryAsync(Path.Combine(directory, "capture-boundary"), Check);
        await FilterBoundaryAsync(Path.Combine(directory, "filter-boundary"), Check);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
    private sealed class ScriptClock : IClock
    {
        private readonly Queue<DateTimeOffset> sequence = new();
        private DateTimeOffset final;
        internal readonly List<DateTimeOffset> Reads = new();
        internal void Set(params DateTimeOffset[] values)
        {
            if (values.Length == 0) throw new ArgumentException("A clock script is required.");
            sequence.Clear(); Reads.Clear();
            foreach (var value in values) sequence.Enqueue(value);
            final = values[^1];
        }
        public DateTimeOffset UtcNow
        {
            get { var value = sequence.Count > 0 ? sequence.Dequeue() : final; Reads.Add(value); return value; }
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Settings Settings = new();
        internal readonly ScriptClock Clock = new();
        internal readonly FixedClock LedgerClock;
        internal readonly AssistantMemoryStore Memory;
        internal readonly AssistantModelContextProvider Provider;
        internal readonly ConversationIdentity Identity;
        internal readonly DateTimeOffset Before, Expires, After;
        internal DurableConversationLedger Ledger = null!;
        internal GenerationLease Lease = null!;
        internal InboxMessage Original = null!;
        internal Fixture(string directory, bool personalize)
        {
            Directory.CreateDirectory(directory);
            var now = DateTimeOffset.UtcNow;
            Before = now.AddMinutes(30); Expires = now.AddHours(1); After = Expires.AddSeconds(1);
            LedgerClock = new(Before);
            Settings.ChatGpt.ActiveAccount = "context-race-account";
            Settings.ChatGpt.PersonalizationEnabled = personalize;
            var binding = new ClawBinding { Token = "SYNTHETIC_TOKEN", BaseUrl = "https://synthetic.invalid", UserId = "race-user", BotId = "race-bot", ContextToken = "SYNTHETIC_CONTEXT" };
            Settings.WeChat.WriteBinding(binding);
            Identity = DurableAssistantRuntime.BindingIdentity(binding, Settings.ChatGpt.ActiveAccount);
            Memory = new AssistantMemoryStore(Path.Combine(directory, "synthetic-memory"), () => "context-race-profile");
            var entry = Memory.Add("UNKNOWN_PROVENANCE_MEMORY");
            Memory.UpdateMetadata(entry.Id, entry.Metadata! with { ExpiresAtUtc = Expires }, Memory.Capture().Stamp);
            Clock.Set(Before);
            Provider = new(Settings, Memory, Clock);
        }
        internal static async Task<Fixture> CreateAsync(string directory, bool personalize)
        {
            var f = new Fixture(directory, personalize);
            f.Ledger = await DurableConversationLedger.OpenAsync(new FileJournalStorage(Path.Combine(directory, "synthetic-dialogue.state")),
                new WindowsStateProtector(), f.LedgerClock, new() { RequireOutputDependencies = true }, dependencyValidator: f.Provider);
            f.Provider.BindLedger(f.Ledger);
            f.Original = (await f.Ledger.ReceiveAsync(f.Identity, "old-source", OldBody, MessageTrust.AuthenticatedUser, f.Before)).Message;
            f.LedgerClock.UtcNow = f.Before.AddMinutes(1);
            await f.Ledger.ReceiveAsync(f.Identity, "new-source", "状态", MessageTrust.AuthenticatedUser, f.LedgerClock.UtcNow);
            var work = (await f.Ledger.QueueAvailableInputWorkAsync(f.Identity)).Single();
            f.Lease = await f.Ledger.BeginReadOnlyWorkAsync(f.Identity, work.Id);
            return f;
        }
        public async ValueTask DisposeAsync()
        {
            Provider.Release(Lease);
            await Ledger.DisposeAsync();
        }
    }

    private static async Task CaptureBoundaryAsync(string directory, Action<string, bool> check)
    {
        await using var f = await Fixture.CreateAsync(directory, personalize: true);
        var initial = f.Memory.CaptureHistoryFilter(f.Before);
        check("unknown_provenance_fixture_admits_original_input_before_expiry", initial.UnknownSourceHistoryCutoffUtc < f.Original.ReceivedAt
            && f.Memory.CaptureForGeneration(true, f.Before).Content!.Context.Contains("UNKNOWN_PROVENANCE_MEMORY", StringComparison.Ordinal));
        f.Clock.Set(f.Before, f.After);
        bool rejected = await RejectAsync(async () => await f.Provider.CaptureAsync(f.Lease, default));
        check("capture_rejects_expiry_between_history_and_actual_prompt_snapshot", rejected && f.Clock.Reads.Count >= 3
            && f.Clock.Reads[0] == f.Before && f.Clock.Reads[1] == f.After);
        var saved = await f.Ledger.ReadConversationAsync(f.Identity);
        var after = AssistantMemoryDependencies.ToOutputDependencies(f.Memory.CaptureForGeneration(true, f.After));
        bool cached = true;
        try { f.Provider.GetPersonalization(new(f.Lease.Id, 0, after)); }
        catch (ChatGptException) { cached = false; }
        check("rejected_capture_never_binds_or_caches_a_new_manifest_for_old_input", !cached
            && saved.Generations.Single(g => g.Id == f.Lease.Id).GenerationContext == null
            && saved.Outbox.IsEmpty && saved.Operations.IsEmpty && saved.Inbox.Single(i => i.Id == f.Original.Id).Body == OldBody);
    }

    private static async Task FilterBoundaryAsync(string directory, Action<string, bool> check)
    {
        // Disabled personalization deliberately leaves the generation's policy-only
        // manifest valid at expiry, isolating the second historical-input guard.
        await using var f = await Fixture.CreateAsync(directory, personalize: false);
        var dependencies = await f.Provider.CaptureAsync(f.Lease, default);
        var context = await f.Ledger.BindGenerationContextAsync(f.Lease, dependencies);
        check("deferred_disabled_generation_has_a_real_bound_policy_only_context", context.Dependencies.Mode == OutputDependencyMode.Bound
            && context.Dependencies.Contexts.Length == 1 && f.Provider.Validate(f.Identity, dependencies).Status == OutputDependencyStatus.Valid);
        f.Clock.Set(f.After);
        check("expiry_can_revoke_historical_source_without_invalidating_disabled_policy", f.Provider.Validate(f.Identity, dependencies).Status == OutputDependencyStatus.Valid
            && f.Memory.CaptureHistoryFilter(f.After).UnknownSourceHistoryCutoffUtc == f.Expires);
        var saved = await f.Ledger.ReadConversationAsync(f.Identity);
        check("filter_rechecks_deferred_current_message_not_only_history_events", await RejectAsync(async () =>
            await f.Provider.FilterAsync(f.Identity, context, saved.Events, [], default))
            && saved.Inbox.Single(i => i.Id == f.Original.Id).Body == OldBody);

        await f.Ledger.EndGenerationAttemptAsync(f.Lease);
        f.Provider.Release(f.Lease);
        f.LedgerClock.UtcNow = f.After.AddMinutes(1);
        var fresh = (await f.Ledger.ReceiveAsync(f.Identity, "fresh-same-text", OldBody, MessageTrust.AuthenticatedUser, f.LedgerClock.UtcNow)).Message;
        f.Lease = await f.Ledger.BeginGenerationAsync(f.Identity, [fresh.SourceMessageId]);
        f.Clock.Set(f.LedgerClock.UtcNow);
        var freshDependencies = await f.Provider.CaptureAsync(f.Lease, default);
        var freshContext = await f.Ledger.BindGenerationContextAsync(f.Lease, freshDependencies);
        saved = await f.Ledger.ReadConversationAsync(f.Identity);
        var selection = await f.Provider.FilterAsync(f.Identity, freshContext, saved.Events, [], default);
        check("fresh_authenticated_same_text_remains_current_without_keyword_censorship", selection.Context.Any(e => e.Source == ConversationSource.TrustedUser
            && e.SourceMessageId == fresh.SourceMessageId && e.Body == OldBody)
            && !selection.Context.Any(e => e.SourceMessageId == f.Original.SourceMessageId));
        check("provider_race_checks_preserve_audit_without_operations_or_delivery", saved.Operations.IsEmpty && saved.Outbox.IsEmpty
            && saved.Inbox.Any(i => i.Id == f.Original.Id && i.Body == OldBody)
            && f.Memory.Entries.Single().Text == "UNKNOWN_PROVENANCE_MEMORY");
    }
    private static async Task<bool> RejectAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (ChatGptException) { return true; }
    }
}
