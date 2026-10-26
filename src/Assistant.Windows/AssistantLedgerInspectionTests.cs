using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;
internal static class AssistantLedgerInspectionTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--ledger-inspection-test", var root]) return false;
        Directory.CreateDirectory(root); var checks = new Dictionary<string, bool>();
        void Check(string key, bool value) { checks.Add(key, value); if (!value) throw new Exception(key); }
        try
        {
            foreach (bool archived in new[] { false, true })
            {
                string source = Path.Combine(root, archived ? "archived" : "legacy"); Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source, ".codex-migration.lock"), "");
                File.WriteAllText(Path.Combine(source, "settings.json"), "{\"version\":1,\"weChat\":{\"clawBotEnabled\":true},\"chatGpt\":{\"enabled\":true}}");
                var identity = new ConversationIdentity("PRIVATE_USER", "PRIVATE_BINDING", "PRIVATE_CONVERSATION");
                var memory = new AssistantMemoryStore(Path.Combine(source, "assistant"), () => "PRIVATE_MEMORY_IDENTITY");
                var removed = memory.Add("PRIVATE_REMOVED_MEMORY"); memory.Delete(removed.Id);
                memory.Add("PRIVATE_RETAINED_MEMORY");
                await using (var ledger = await AssistantLedgerStorage.OpenAsync(source, archived))
                {
                    await ledger.ReceiveAsync(identity, "PRIVATE_MESSAGE", "PRIVATE_BODY", MessageTrust.AuthenticatedUser, DateTimeOffset.UtcNow);
                    var authorization = await ledger.AuthorizeCurrentUserRequestAsync(identity, "PRIVATE_MESSAGE", "synthetic_action", "synthetic_target", "{}", "PRIVATE_BODY");
                    var operation = await ledger.QueueOperationAsync(identity, authorization.Id, "synthetic-idempotency");
                    await ledger.BeginOperationDispatchAsync(identity, operation.Id);
                    await ledger.RecordOperationObservationAsync(identity, operation.Id, OperationStage.Unknown, "SYNTHETIC_LOST_RECEIPT");
                    await ledger.BeginGenerationAsync(identity, ["PRIVATE_MESSAGE"]);
                    var history = await ledger.AppendContextAsync(identity, ConversationSource.Report, "PRIVATE_REPORT", DateTimeOffset.UtcNow);
                    memory.EpisodeSourceReader = (owner, id) => ledger.ReadCommittedEvent(owner, id);
                    memory.SaveEpisodeFromEditor(memory.CaptureEpisodeEditor([history]), "PRIVATE_SUMMARY", [EpisodeEventReference.From(history)]);
                }
                var before = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                    .ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
                string report = Path.Combine(root, $"report-{archived}.json");
                Check($"{archived}: command success", await AssistantLedgerInspection.RunAsync(["--inspect-ledger", source, report]) == 0);
                string text = File.ReadAllText(report); using var json = JsonDocument.Parse(text);
                Check($"{archived}: no private content", !text.Contains("PRIVATE_"));
                var memoryReport = json.RootElement.GetProperty("memories")[0];
                Check($"{archived}: memory counts and revocation retained", memoryReport.GetProperty("entryCount").GetInt32() == 2
                    && memoryReport.GetProperty("revocationCount").GetInt32() == 1
                    && !text.Contains(memory.Profile));
                Check($"{archived}: event reference fingerprint matches", memoryReport.GetProperty("eventReferenceStates").GetProperty("fingerprint_matches").GetInt32() == 1);
                Check($"{archived}: truthful counts and no permission", json.RootElement.GetProperty("conversations")[0].GetProperty("inboxCount").GetInt32() == 1
                    && !json.RootElement.GetProperty("restoreAuthorized").GetBoolean());
                Check($"{archived}: files unchanged", before.All(x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x.Key))) == x.Value)
                    && Directory.GetFiles(source, "*", SearchOption.AllDirectories).Length == before.Count);
                await using (var inspect = await AssistantLedgerStorage.OpenAsync(source, false, inspection: true))
                    Check($"{archived}: original processing preserved", (await inspect.ReadConversationAsync(identity)).Generations.Single().Stage == GenerationStage.Processing);
                Check($"{archived}: report not overwritten", await AssistantLedgerInspection.RunAsync(["--inspect-ledger", source, report]) == 1
                    && File.ReadAllText(report) == text);
                using (var lease = new FileStream(Path.Combine(source, ".codex-migration.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    Check($"{archived}: running writer excluded", await AssistantLedgerInspection.RunAsync(["--inspect-ledger", source, report + ".blocked"]) == 1);
                string protectedMemory = File.ReadAllText(memory.DataPath!);
                var document = AssistantMemoryStore.ReadPersistedDocument(memory.DataPath!, memory.Profile);
                int summaryIndex = document.Entries.FindIndex(entry => entry.Metadata?.Events.IsEmpty == false);
                var summary = document.Entries[summaryIndex]; var reference = summary.Metadata!.Events.Single();
                foreach (bool missing in new[] { false, true })
                {
                    var altered = missing ? reference with { EventId = Guid.NewGuid() } : reference with { Fingerprint = new string('0', 64) };
                    document.Entries[summaryIndex] = summary with { Metadata = summary.Metadata with { EventSources = [altered] } };
                    File.WriteAllText(memory.DataPath!, LocalSecret.Protect(JsonSerializer.Serialize(document, Settings.Json)));
                    string changedReport = report + (missing ? ".missing" : ".different");
                    Check($"{archived}: altered reference report {missing}", await AssistantLedgerInspection.RunAsync(["--inspect-ledger", source, changedReport]) == 0);
                    using var changedJson = JsonDocument.Parse(File.ReadAllText(changedReport));
                    Check($"{archived}: altered reference distinguished {missing}", changedJson.RootElement.GetProperty("memories")[0]
                        .GetProperty("eventReferenceStates").GetProperty(missing ? "missing_or_unavailable" : "fingerprint_differs").GetInt32() == 1);
                }
                File.WriteAllText(memory.DataPath!, "synthetic-invalid-protected-document");
                string invalidReport = report + ".invalid-memory";
                Check($"{archived}: malformed memory rejects report", await AssistantLedgerInspection.RunAsync(["--inspect-ledger", source, invalidReport]) == 1
                    && !File.Exists(invalidReport) && File.ReadAllText(memory.DataPath!) == "synthetic-invalid-protected-document");
                File.WriteAllText(memory.DataPath!, protectedMemory);
                bool reviewDenied = false;
                try { await AssistantLedgerInspection.ReadAsync(source, requireHold: true); }
                catch (InvalidOperationException) { reviewDenied = true; }
                Check($"{archived}: review requires paused copy", reviewDenied);
                Codex.Migration.MigrationStartup.HoldForRecovery(source);
                string holdPath = Path.Combine(source, Codex.Migration.MigrationStartup.RecoveryHoldName);
                var hold = File.ReadAllBytes(holdPath);
                Codex.Migration.MigrationStartup.HoldForRecovery(source);
                Check($"{archived}: repeated hold preserves marker", hold.SequenceEqual(File.ReadAllBytes(holdPath)));
                bool stopped = false;
                try { using var denied = Codex.Migration.MigrationStartup.AcquireRuntimeLease(source); }
                catch (InvalidOperationException) { stopped = true; }
                Check($"{archived}: held startup rejected", stopped && !Codex.Migration.MigrationStartup.CanStart(source));
                string heldReport = report + ".held";
                Check($"{archived}: inspection available during hold", await AssistantLedgerInspection.RunAsync(["--inspect-ledger", source, heldReport]) == 0);
                using var heldJson = JsonDocument.Parse(File.ReadAllText(heldReport));
                Check($"{archived}: current saved report verifies without startup", await AssistantLedgerInspection.RunAsync(
                    ["--verify-recovery-report", source, heldReport]) == 0 && !Codex.Migration.MigrationStartup.CanStart(source));
                Check($"{archived}: hold reported without resume permission", heldJson.RootElement.GetProperty("recoveryHoldPresent").GetBoolean()
                    && !heldJson.RootElement.GetProperty("restoreAuthorized").GetBoolean());
                var heldFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                    .ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
                var reviewReport = await AssistantLedgerInspection.ReadAsync(source, requireHold: true);
                Check($"{archived}: unchanged snapshot has stable fingerprint",
                    reviewReport.GetProperty("snapshotFingerprint").GetProperty("Sha256").GetString()
                    == heldJson.RootElement.GetProperty("snapshotFingerprint").GetProperty("Sha256").GetString());
                string reviewText = RecoveryReviewWindow.Describe(reviewReport);
                var reviewWindow = new RecoveryReviewWindow(reviewReport);
                reviewWindow.Close();
                Check($"{archived}: review shows counts and limitations without private text", reviewText.Contains("收件 1")
                    && reviewText.Contains("撤销 1") && reviewText.Contains("不提供启动许可") && !reviewText.Contains("PRIVATE_"));
                Check($"{archived}: closing review preserves held data", heldFiles.All(x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x.Key))) == x.Value)
                    && Directory.GetFiles(source, "*", SearchOption.AllDirectories).Length == heldFiles.Count
                    && !Codex.Migration.MigrationStartup.CanStart(source));
                await using (var inspection = await AssistantLedgerStorage.OpenAsync(source, false, inspection: true))
                {
                    var snapshot = await inspection.ReadConversationAsync(identity);
                    var now = DateTimeOffset.UtcNow;
                    var reminder = new TrackedWorkItem(Guid.NewGuid(), identity, TrackedWorkKind.Reminder,
                        "PRIVATE_SOURCE", Guid.NewGuid(), 1, TrackedWorkStage.Paused, "PRIVATE_GOAL", null, now, now, null, new());
                    var wake = new ChannelWakeRecord(Guid.NewGuid(), identity, 1, Guid.NewGuid(), [], ChannelWakeStage.Unknown, now, now,
                        Evidence: "PRIVATE_EVIDENCE", PlatformReceipt: "PRIVATE_RECEIPT");
                    var pending = RecoveryPendingWork.Read(snapshot with {
                        WorkItems = [reminder, reminder with { Id = Guid.NewGuid(), Stage = TrackedWorkStage.Completed }],
                        Schedules = [new(reminder.Id, "PRIVATE_LOCAL_TIME", "PRIVATE_ZONE", now, now.AddMinutes(5), MissedTriggerPolicy.NeedsDecision)],
                        ChannelWakes = [wake] });
                    Check($"{archived}: pending inventory preserves pause unknown and missed policy", pending.Length == 3
                        && pending.Single(x => x.Kind == "Generation").Stage == "Processing"
                        && pending.Single(x => x.Id == reminder.Id) is { Stage: "Paused", MissedPolicy: "NeedsDecision" }
                        && pending.Single(x => x.Id == wake.Id).Stage == "Unknown"
                        && !JsonSerializer.Serialize(pending).Contains("PRIVATE_"));
                    Check($"{archived}: report contains background inventory", reviewReport.GetProperty("version").GetInt32() == 6
                        && reviewReport.GetProperty("conversations")[0].GetProperty("pendingWork").ValueKind == JsonValueKind.Array);
                }
                var identical = RecoveryComparison.Compare(reviewReport, reviewReport);
                var pendingRequest = new Codex.Migration.MigrationRequest(source, source + "-pending-target", source + "-pending-transaction");
                var pendingHandoff = await ReviewedRecoveryHandoff.ExecuteAsync(pendingRequest,
                    reviewReport.GetProperty("snapshotFingerprint").GetProperty("Sha256").GetString()!, new SyntheticReceiverGuard());
                Check($"{archived}: reviewed pending work is preserved without activation", pendingHandoff.Succeeded && !pendingHandoff.ActivationAllowed
                    && File.ReadAllBytes(Path.Combine(source, "conversations.state")).SequenceEqual(File.ReadAllBytes(Path.Combine(pendingRequest.TargetDirectory, "conversations.state"))));
                await using (var copied = await AssistantLedgerStorage.OpenAsync(pendingRequest.TargetDirectory, false, inspection: true))
                {
                    var preserved = await copied.ReadConversationAsync(identity);
                    Check($"{archived}: copied processing is not reset or replayed", preserved.Generations.Single().Stage == GenerationStage.Processing);
                    Check($"{archived}: unknown operation retains idempotency and unknown status", preserved.Operations.Single().Stage == OperationStage.Unknown
                        && preserved.Operations.Single().IdempotencyKey == "synthetic-idempotency");
                }
                Check($"{archived}: identical records still do not authorize restore", identical.GetProperty("recordsIdentical").GetBoolean()
                    && !identical.GetProperty("restoreAuthorized").GetBoolean());
                var changedReportNode = System.Text.Json.Nodes.JsonNode.Parse(reviewReport.GetRawText())!;
                var recordNodes = changedReportNode["reconciliationRecords"]!.AsArray();
                recordNodes[0]!["Digest"] = new string('0', 64);
                recordNodes.RemoveAt(recordNodes.Count - 1);
                using var changedRecordReport = JsonDocument.Parse(changedReportNode.ToJsonString());
                var comparison = RecoveryComparison.Compare(reviewReport, changedRecordReport.RootElement);
                Check($"{archived}: comparison detects missing memory and changed records despite unchanged counts",
                    !comparison.GetProperty("recordsIdentical").GetBoolean()
                    && comparison.GetProperty("differences").EnumerateArray().Any(x => x.GetProperty("Changed").GetInt32() > 0)
                    && comparison.GetProperty("differences").EnumerateArray().Any(x => x.GetProperty("MissingFromCandidate").GetInt32() > 0)
                    && !comparison.GetRawText().Contains("PRIVATE_"));
                string comparisonPath = report + ".comparison";
                Check($"{archived}: comparison command is offline and keeps hold", await AssistantLedgerInspection.RunAsync(
                    ["--compare-recovery-reports", heldReport, heldReport, comparisonPath]) == 0
                    && !Codex.Migration.MigrationStartup.CanStart(source));
                string extraFile = Path.Combine(source, "PRIVATE_SYNTHETIC_SIDECAR");
                File.WriteAllText(extraFile, "PRIVATE_OPAQUE_BYTES");
                var changedSnapshot = await AssistantLedgerInspection.ReadAsync(source, requireHold: true);
                Check($"{archived}: stale saved report rejects without startup", await AssistantLedgerInspection.RunAsync(
                    ["--verify-recovery-report", source, heldReport]) == 3 && !Codex.Migration.MigrationStartup.CanStart(source));
                Check($"{archived}: sidecar change changes fingerprint without exposing content",
                    changedSnapshot.GetProperty("snapshotFingerprint").GetProperty("Sha256").GetString()
                    != reviewReport.GetProperty("snapshotFingerprint").GetProperty("Sha256").GetString()
                    && !changedSnapshot.GetRawText().Contains("PRIVATE_"));
            }
            string handoffSource = Path.Combine(root, "handoff-source"); Directory.CreateDirectory(handoffSource);
            File.WriteAllText(Path.Combine(handoffSource, ".codex-migration.lock"), "");
            File.WriteAllText(Path.Combine(handoffSource, "settings.json"), "{\"version\":1,\"weChat\":{\"clawBotEnabled\":true,\"protectedBinding\":\"SYNTHETIC_BINDING\"},\"chatGpt\":{\"enabled\":true,\"taskControlEnabled\":true}}");
            var handoffIdentity = new ConversationIdentity("synthetic", "handoff", "conversation");
            await using (var ledger = await AssistantLedgerStorage.OpenAsync(handoffSource, true))
                await ledger.AppendContextAsync(handoffIdentity, ConversationSource.Report, "SYNTHETIC_NEWEST_RESULT", DateTimeOffset.UtcNow);
            Codex.Migration.MigrationStartup.HoldForRecovery(handoffSource);
            var handoffReport = await AssistantLedgerInspection.ReadAsync(handoffSource, true);
            string fingerprint = handoffReport.GetProperty("snapshotFingerprint").GetProperty("Sha256").GetString()!;
            var request = new Codex.Migration.MigrationRequest(handoffSource, Path.Combine(root, "handoff-target"), Path.Combine(root, "handoff-transaction"));
            Check("review alone cannot bypass receiver exclusion", !(await ReviewedRecoveryHandoff.ExecuteAsync(request, fingerprint)).Succeeded
                && !Directory.Exists(request.TargetDirectory));
            Check("stale review cannot activate recovery", !(await ReviewedRecoveryHandoff.ExecuteAsync(request, new string('0', 64), new SyntheticReceiverGuard())).Succeeded
                && !Directory.Exists(request.TargetDirectory));
            var handoff = await ReviewedRecoveryHandoff.ExecuteAsync(request, fingerprint, new SyntheticReceiverGuard());
            Check("reviewed quiescent handoff publishes latest state to new directory", handoff.Succeeded && !handoff.ActivationAllowed
                && Codex.Migration.MigrationStartup.CanStart(request.TargetDirectory) && !Codex.Migration.MigrationStartup.CanStart(handoffSource));
            using (var targetSettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(request.TargetDirectory, "settings.json"))))
                Check("recovery opens with channels model and control off while retaining binding", !targetSettings.RootElement.GetProperty("weChat").GetProperty("clawBotEnabled").GetBoolean()
                    && !targetSettings.RootElement.GetProperty("chatGpt").GetProperty("enabled").GetBoolean()
                    && !targetSettings.RootElement.GetProperty("chatGpt").GetProperty("taskControlEnabled").GetBoolean()
                    && targetSettings.RootElement.GetProperty("weChat").GetProperty("protectedBinding").GetString() == "SYNTHETIC_BINDING");
            await using (var ledger = await AssistantLedgerStorage.OpenAsync(request.TargetDirectory, false, inspection: true))
                Check("handoff retains newest committed report without replay", (await ledger.ReadConversationAsync(handoffIdentity)).Events.Any(x => x.Body == "SYNTHETIC_NEWEST_RESULT"));
            File.WriteAllText(Path.Combine(root, "inspection-checks.json"), JsonSerializer.Serialize(checks)); exit(0);
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(root, "failure.txt"), error.ToString()); exit(1); }
        return true;
    }
    private sealed class SyntheticReceiverGuard : Codex.Migration.ILegacyReceiverGuard
    {
        public ValueTask<Codex.Migration.IReceiverExclusionLease?> TryAcquireAsync(Codex.Migration.MigrationRequest request,
            Codex.Migration.MigrationDirection direction, System.Threading.CancellationToken cancellation)
            => ValueTask.FromResult<Codex.Migration.IReceiverExclusionLease?>(new Lease());
        private sealed class Lease : Codex.Migration.IReceiverExclusionLease
        {
            public bool LegacyStoppedAndWritesBlocked => true;
            public bool AssistantStoppedAndWritesBlocked => true;
            public bool CredentialScopeCompatible => true;
            public ValueTask<bool> RevalidateAsync(System.Threading.CancellationToken cancellation) => ValueTask.FromResult(true);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
