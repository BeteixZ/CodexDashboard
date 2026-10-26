using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
namespace CodexDashboard;
internal static class AssistantTestDispatch
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (await ImageAttachmentRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await AssistantImageTests.TryRunAsync(args, exit)) return true;
        if (await AttachmentRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await AssistantAttachmentTests.TryRunAsync(args, exit)) return true;
        if (await FileChannelRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await AssistantFileDeliveryTests.TryRunAsync(args, exit)) return true;
        if (await WeChatFileTests.TryRunAsync(args, exit)) return true;
        if (await WeChatMediaTests.TryRunAsync(args, exit)) return true;
        if (await WeChatTypingTests.TryRunAsync(args, exit)) return true;
        if (await WeChatFieldProbeTests.TryRunAsync(args, exit)) return true;
        if (await AssistantLedgerInspectionTests.TryRunAsync(args, exit)) return true;
        if (await AssistantLedgerStorageTests.TryRunAsync(args, exit)) return true;
        if (await OwnedProjectMemoryTests.TryRunAsync(args, exit)) return true;
        if (await ExistingReconciliationTests.TryRunAsync(args, exit)) return true;
        if (await ExistingControlRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await SharedExistingControlTests.TryRunAsync(args, exit)) return true;
        if (await DependencyCommandChainTests.TryRunAsync(args, exit)) return true;
        if (await DependencyCommandTests.TryRunAsync(args, exit)) return true;
        if (await DependencyRecoveryTests.TryRunAsync(args, exit)) return true;
        if (await DependencyNotificationTests.TryRunAsync(args, exit)) return true;
        if (await DependencyContinuationTests.TryRunAsync(args, exit)) return true;
        if (await DependencyEditTests.TryRunAsync(args, exit)) return true;
        if (await DependencyChainTests.TryRunAsync(args, exit)) return true;
        if (await DependentWorkRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await RemoteProjectMemoryTests.TryRunAsync(args, exit)) return true;
        if (await EpisodeProjectTests.TryRunAsync(args, exit)) return true;
        if (await EpisodeCatchUpTests.TryRunAsync(args, exit)) return true;
        if (await EpisodeSourceTests.TryRunAsync(args, exit)) return true;
        if (await MemoryViewRevocationTests.TryRunAsync(args, exit)) return true;
        if (await EpisodeDraftTests.TryRunAsync(args, exit)) return true;
        if (await EpisodeMemoryTests.TryRunAsync(args, exit)) return true;
        if (await ConversationHistoryRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await InputSettingsLifecycleTests.TryRunAsync(args, exit)) return true;
        if (await MemoryEditorConflictTests.TryRunAsync(args, exit)) return true;
        if (await LateReceiptRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await HistoryPaginationRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await HistoryPaginationWindowTests.TryRunAsync(args, exit)) return true;
        if (await ModelSettingsTransactionTests.TryRunAsync(args, exit)) return true;
        if (await WeChatBindingLifecycleTests.TryRunAsync(args, exit)) return true;
        if (await BindingSessionRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await QuotedMessageRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await OwnedObservationOrderRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await ReceiverLeaseTests.TryRunAsync(args, exit)) return true;
        if (await TaskCoordinatorTests.TryRunAsync(args, exit)) return true;
        if (await CodexGatewayTests.TryRunAsync(args, exit)) return true;
        if (await AgentAdapterTests.TryRunAsync(args, exit)) return true;
        if (await ModelWindowLifecycleTests.TryRunAsync(args, exit)) return true;
        if (await AssistantToolExecutorTests.TryRunAsync(args, exit)) return true;
        if (await AutomaticPreferenceToolTests.TryRunAsync(args, exit)) return true;
        if (await AssistantRefreshLifecycleTests.TryRunAsync(args, exit)) return true;
        if (await MemoryCommandResolverTests.TryRunAsync(args, exit)) return true;
        if (await ManualMemoryToolsTests.TryRunAsync(args, exit)) return true;
        if (await MemoryProvenanceTests.TryRunAsync(args, exit)) return true;
        if (await MemoryLayerTests.TryRunAsync(args, exit)) return true;
        if (await SummaryMemoryTests.TryRunAsync(args, exit)) return true;
        if (await MemoryRevocationTests.TryRunAsync(args, exit)) return true;
        if (await GenerationCaptureTests.TryRunAsync(args, exit)) return true;
        if (await AssistantModelContextRaceTests.TryRunAsync(args, exit)) return true;
        if (await MemoryOutputRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await MemorySourceRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await ReminderCommandResolverTests.TryRunAsync(args, exit)) return true;
        if (await MigrationHostIntegrationTests.TryRunAsync(args, exit)) return true;
        if (await CommitmentRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await CommitmentDetailsRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await OperationDetailsTests.TryRunAsync(args, exit)) return true;
        if (await OperationDetailsRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await OperationDetailWindowTests.TryRunAsync(args, exit)) return true;
        if (await CommitmentDetailWindowTests.TryRunAsync(args, exit)) return true;
        if (await CommitmentMissedRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await CommitmentToolAdapterTests.TryRunAsync(args, exit)) return true;
        if (await CommitmentPolicyCommandTests.TryRunAsync(args, exit)) return true;
        if (await CommitmentPolicyRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await PushPlusWakeSettingsTests.TryRunAsync(args, exit)) return true;
        if (await ChannelWakeRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await CatchUpRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await CatchUpRangeRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await RequiredDecisionRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await WakeDeliveryGuardTests.TryRunAsync(args, exit)) return true;
        if (await CommitmentChannelRaceTests.TryRunAsync(args, exit)) return true;
        if (await OwnedCommitmentRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await CommitmentBudgetRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await ApprovalNotificationMergeRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await DurableRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await DurableInputBurstRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await InputMergeBoundaryTests.TryRunAsync(args, exit)) return true;
        if (await RuntimeCrashTests.TryRunAsync(args, exit)) return true;
        if (await QueueHistoryTests.TryRunAsync(args, exit)) return true;
        if (await QueueFenceTests.TryRunAsync(args, exit)) return true;
        if (await NotificationRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await OwnedControlAdapterTests.TryRunAsync(args, exit)) return true;
        if (await OwnedControlConfigurationTests.TryRunAsync(args, exit)) return true;
        if (await OwnedHostRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await OwnedTurnAmendRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await WorkspaceHistoryTests.TryRunAsync(args, exit)) return true;
        if (await AssistantHealthTests.TryRunAsync(args, exit)) return true;
        if (await AssistantHealthExportTests.TryRunAsync(args, exit)) return true;
        if (await ControlReceiptRuntimeTests.TryRunAsync(args, exit)) return true;
        if (await ProjectMemoryTests.TryRunAsync(args, exit)) return true;
        if (await ProjectMemoryEditorTests.TryRunAsync(args, exit)) return true;
        if (await ProjectMemoryRuntimeTests.TryRunAsync(args, exit)) return true;
        if (args.Contains("--assistant-network-diagnostic"))
        {
            int index = Array.IndexOf(args, "--assistant-network-diagnostic");
            try { await AssistantNetworkDiagnostics.RunAsync(args[index + 1]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--wechat-diagnostic"))
        {
            int index = Array.IndexOf(args, "--wechat-diagnostic");
            try { await WeChatDiagnostics.RunAsync(args[index + 1], args.Contains("--send-test")); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--wechat-test"))
        {
            int index = Array.IndexOf(args, "--wechat-test");
            string directory = index + 1 < args.Length ? args[index + 1] : Path.Combine(AppContext.BaseDirectory, "wechat-test");
            try { Directory.CreateDirectory(directory); await WeChatSmokeTests.RunAndSaveAsync(directory); exit(0); }
            catch { exit(1); }
            return true;
        }
        if (args.Contains("--chatgpt-test"))
        {
            int index = Array.IndexOf(args, "--chatgpt-test");
            string directory = index + 1 < args.Length ? args[index + 1] : Path.Combine(AppContext.BaseDirectory, "chatgpt-test");
            try { Directory.CreateDirectory(directory); await ChatGptSmokeTests.RunAndSaveAsync(directory); exit(0); }
            catch { exit(1); }
            return true;
        }
        if (args.Contains("--assistant-memory-test"))
        {
            int index = Array.IndexOf(args, "--assistant-memory-test");
            try { await AssistantMemoryTests.RunAsync(args[index + 1]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--assistant-auto-memory-test"))
        {
            int index = Array.IndexOf(args, "--assistant-auto-memory-test");
            try { await AutomaticPreferenceTests.RunAsync(args[index + 1]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--assistant-memory-live-test"))
        {
            int index = Array.IndexOf(args, "--assistant-memory-live-test");
            try { await AssistantMemoryDiagnostics.LiveAsync(args[index + 1]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--assistant-auto-memory-live-test"))
        {
            int index = Array.IndexOf(args, "--assistant-auto-memory-live-test");
            try { await AssistantMemoryDiagnostics.LiveAutoAsync(args[index + 1]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--assistant-identity-diagnostic"))
        {
            int index = Array.IndexOf(args, "--assistant-identity-diagnostic");
            try { await AssistantMemoryDiagnostics.InspectIdentityAsync(args[index + 1]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--assistant-save-persona"))
        {
            int index = Array.IndexOf(args, "--assistant-save-persona");
            try { await AssistantMemoryDiagnostics.SavePersonaAsync(args[index + 1], args[index + 2]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--assistant-identity-live-test") || args.Contains("--assistant-identity-read-test"))
        {
            bool production = args.Contains("--assistant-identity-read-test");
            int index = Array.IndexOf(args, production ? "--assistant-identity-read-test" : "--assistant-identity-live-test");
            try { await AssistantMemoryDiagnostics.LiveIdentityAsync(args[index + 1], args[index + 2], production); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--task-control-test"))
        {
            int index = Array.IndexOf(args, "--task-control-test");
            string directory = index + 1 < args.Length ? args[index + 1] : Path.Combine(AppContext.BaseDirectory, "task-control-test");
            try { await TaskControlTests.RunAndSaveAsync(directory); exit(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit(1); }
            return true;
        }
        if (args.Contains("--assistant-natural-summary-live-test"))
        {
            int index = Array.IndexOf(args, "--assistant-natural-summary-live-test");
            try { await AssistantMemoryDiagnostics.LiveNaturalSummaryAsync(args[index + 1]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--assistant-voice-live-test"))
        {
            int index = Array.IndexOf(args, "--assistant-voice-live-test");
            try { await AssistantVoiceDiagnostics.RunAsync(args[index + 1], args.Contains("--candidate-voice")); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--assistant-voice-context-test"))
        {
            int index = Array.IndexOf(args, "--assistant-voice-context-test");
            try { await AssistantVoiceDiagnostics.RunContextAsync(args[index + 1], args.Contains("--candidate-voice")); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--assistant-install-voice"))
        {
            int index = Array.IndexOf(args, "--assistant-install-voice");
            try { await AssistantVoiceDiagnostics.InstallRulesAsync(args[index + 1], args[index + 2]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--assistant-name-reproduction"))
        {
            int index = Array.IndexOf(args, "--assistant-name-reproduction");
            try { await AssistantMemoryDiagnostics.ReproduceNameAsync(args[index + 1]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--remote-control-test"))
        {
            int index = Array.IndexOf(args, "--remote-control-test");
            try { await RemoteControlTests.RunAndSaveAsync(args[index + 1]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--task-control-model-test"))
        {
            int index = Array.IndexOf(args, "--task-control-model-test");
            try { await TaskControlDiagnostics.ModelAsync(args[index + 1]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--task-progress-live-test"))
        {
            int index = Array.IndexOf(args, "--task-progress-live-test");
            try { await TaskControlDiagnostics.ProgressLiveAsync(args[index + 1], args[index + 2], args[index + 3]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--task-control-probe"))
        {
            int index = Array.IndexOf(args, "--task-control-probe");
            try { await TaskControlDiagnostics.ProbeAsync(args[index + 1], args[index + 2], args[index + 3]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--remote-control-probe"))
        {
            int index = Array.IndexOf(args, "--remote-control-probe");
            try { await TaskControlDiagnostics.RemoteProbeAsync(args[index + 1], args[index + 2], args.Length > index + 3 ? args[index + 3] : ""); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--remote-control-live-test"))
        {
            int index = Array.IndexOf(args, "--remote-control-live-test");
            try { await TaskControlDiagnostics.RemoteLiveAsync(args[index + 1], args[index + 2]); exit(0); } catch { exit(1); }
            return true;
        }
        if (args.Contains("--chatgpt-live-test"))
        {
            int index = Array.IndexOf(args, "--chatgpt-live-test");
            string directory = index + 1 < args.Length ? args[index + 1] : Path.Combine(AppContext.BaseDirectory, "chatgpt-live-test");
            try { await ChatGptLiveTest.RunAndSaveAsync(directory); exit(0); }
            catch { exit(1); }
            return true;
        }
        if (args.Contains("--wechat-live-test"))
        {
            int index = Array.IndexOf(args, "--wechat-live-test");
            string directory = index + 1 < args.Length ? args[index + 1] : Path.Combine(AppContext.BaseDirectory, "wechat-live-test");
            try { await WeChatLiveTest.RunAndSaveAsync(directory); exit(0); }
            catch { exit(1); }
            return true;
        }
        if (args.Contains("--wechat-ai-completion-test"))
        {
            int index = Array.IndexOf(args, "--wechat-ai-completion-test");
            string directory = index + 1 < args.Length ? args[index + 1] : Path.Combine(AppContext.BaseDirectory, "wechat-ai-completion-test");
            try { await WeChatLiveTest.RunAndSaveAsync(directory, true, args.Contains("--exclusive-auth-refresh")); exit(0); }
            catch { exit(1); }
            return true;
        }
        return false;
    }
}
