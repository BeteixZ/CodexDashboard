using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

// Real Responses parsing and memory capture, with synthetic credentials and HTTP only.
internal static class GenerationCaptureTests
{
    private const string Answer = "这一轮检查做完了，报告记录测试通过。长期运行效果还没验证，暂时不能把它当作最终验收。";
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        int index = Array.IndexOf(args, "--generation-capture-test");
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
            File.WriteAllText(Path.Combine(directory, "generation-capture-checks.json"), JsonSerializer.Serialize(new
            {
                count = checks.Count, passed = checks.Values.All(v => v), checks,
                actualCodexProcesses = 0, actualNetworkRequests = 0, productionDataRead = false,
                runtime = "actual Responses parser, typed output and memory snapshots with synthetic HTTP"
            }, Settings.Json));
            if (!value) throw new InvalidOperationException(name);
        }
        await SummaryAsync(Path.Combine(directory, "summary"), Check);
        await ChangesAsync(Path.Combine(directory, "changes"), Check);
        await ToolContextAsync(Path.Combine(directory, "tool-context"), Check);
        await PreparationAsync(Check);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Settings Settings;
        internal readonly AssistantMemoryStore Memory;
        internal readonly ChatGptAssistant Assistant;
        internal readonly List<string> Requests = new();
        internal Func<int, Task<string>> Respond = _ => Task.FromResult(Answer);
        internal Fixture(string directory)
        {
            Directory.CreateDirectory(directory);
            string vault = Path.Combine(directory, "synthetic.credentials");
            new ChatGptVault { Accounts = new() { new ChatGptAccount
            {
                ClientId = "capture-client", Subject = "capture-user", AccessToken = "SYNTHETIC_ACCESS",
                Scope = ChatGptAuth.Scopes, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            } } }.Save(vault);
            Settings = new Settings { ChatGpt = new()
            {
                ActiveAccount = "capture-client", Model = "capture-model", Enabled = true,
                CompletionSummariesEnabled = true, PersonalizationEnabled = true, AutoMemoryEnabled = false
            } };
            Memory = new AssistantMemoryStore(Path.Combine(directory, "synthetic-memory"), () => Settings.ChatGpt.ActiveAccount + "/synthetic-owner");
            Memory.SaveRules("CAPTURE_RULE_A：回答简短且保留未验证事项。");
            Memory.Add("CAPTURE_MEMORY_A：用户喜欢简洁解释。");
            Assistant = new ChatGptAssistant(Settings, () => { }, new ChatGptAuth(vault), new Handler(async (request, ct) =>
            {
                if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal) != true)
                    throw new InvalidOperationException("Unexpected synthetic request.");
                Requests.Add(await request.Content!.ReadAsStringAsync(ct));
                string answer = await Respond(Requests.Count);
                string stream = "data: " + JsonSerializer.Serialize(new { type = "response.output_text.done", text = answer })
                    + "\n\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n";
                return new(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
            }), Memory);
        }
        internal AssistantGenerationPersonalization Capture() => Memory.CaptureForGeneration(Settings.ChatGpt.PersonalizationEnabled, DateTimeOffset.UtcNow);
        internal OutputDependencies Dependencies() => AssistantMemoryDependencies.ToOutputDependencies(Capture());
        public void Dispose() => Assistant.Dispose();
    }

    private static WeChatReport Report() => WeChatReports.FromTask(new TaskRecord
    {
        Id = "capture-task", Host = "synthetic-host", Project = "synthetic", Title = "隔离摘要",
        TurnId = "capture-turn", Status = TaskStatusNames.Completed, FinalReportTurnId = "capture-turn",
        FinalReportSource = "synthetic public report", FinalReport = "公开最终报告：测试用例已通过。尚未开展长时间稳定性测试，也没有手机送达证据。"
    })!;

    private static async Task SummaryAsync(string directory, Action<string, bool> check)
    {
        using var f = new Fixture(directory);
        var expected = f.Dependencies();
        f.Respond = n => Task.FromResult(n == 1 ? Report().Text : Answer);
        var rewritten = await f.Assistant.SummarizeAsync(Report(), default);
        check("summary_rewrite_retains_exact_actual_capture_and_typed_dependencies", rewritten.RewriteRetried
            && rewritten.ModelRequests == 2 && OutputDependencies.Equivalent(expected, rewritten.Dependencies));
        using var first = JsonDocument.Parse(f.Requests[0]); using var second = JsonDocument.Parse(f.Requests[1]);
        check("summary_rewrite_reuses_memory_and_report_without_prior_output", first.RootElement.GetProperty("input").GetRawText() == second.RootElement.GetProperty("input").GetRawText()
            && first.RootElement.GetProperty("instructions").GetString()!.Contains("CAPTURE_RULE_A")
            && second.RootElement.GetProperty("instructions").GetString()!.Contains(CompletionSummaryText.RewriteInstructions)
            && !second.RootElement.GetProperty("input").ToString().Contains(Answer, StringComparison.Ordinal));
        f.Requests.Clear();
        f.Respond = n => n == 1 ? Task.FromException<string>(new HttpRequestException(HttpRequestError.ConnectionError, "synthetic interruption")) : Task.FromResult(Answer);
        var retried = await f.Assistant.SummarizeAsync(Report(), default);
        check("summary_network_retry_reuses_identical_request_and_capture", retried.NetworkRetried && retried.ModelRequests == 2
            && f.Requests[0] == f.Requests[1] && OutputDependencies.Equivalent(expected, retried.Dependencies));
        f.Requests.Clear();
        f.Respond = _ =>
        {
            f.Memory.SaveRules("CAPTURE_RULE_B");
            return Task.FromException<string>(new HttpRequestException(HttpRequestError.ConnectionError, "synthetic interruption"));
        };
        check("changed_capture_during_network_failure_never_recaptures_or_retries_http", await RejectAsync(() => f.Assistant.SummarizeAsync(Report(), default))
            && f.Requests.Count == 1);
    }

    private static async Task ChangesAsync(string directory, Action<string, bool> check)
    {
        using var f = new Fixture(directory);
        string originalRules = f.Memory.Capture().Rules;
        var before = f.Dependencies();
        f.Respond = _ =>
        {
            f.Memory.SaveRules("CAPTURE_RULE_B"); f.Memory.SaveRules(originalRules);
            return Task.FromResult(Answer);
        };
        check("rules_aba_discards_generation_before_releasing_prose", await RejectAsync(() => f.Assistant.SummarizeAsync(Report(), default)) && f.Requests.Count == 1);
        check("rules_aba_changes_persistable_output_dependencies", !OutputDependencies.Equivalent(before, f.Dependencies()));
        f.Requests.Clear();
        f.Respond = _ => { f.Assistant.SetPersonalizationEnabled(false); f.Assistant.SetPersonalizationEnabled(true); return Task.FromResult(Answer); };
        check("personalization_aba_discards_generation", await RejectAsync(() => f.Assistant.SummarizeAsync(Report(), default)) && f.Requests.Count == 1);
        f.Requests.Clear();
        f.Respond = _ =>
        {
            f.Assistant.Configure("capture-client", "other-model", true, true, true);
            f.Assistant.Configure("capture-client", "capture-model", true, true, true);
            return Task.FromResult(Answer);
        };
        check("model_aba_discards_generation_even_when_final_model_matches", await RejectAsync(() => f.Assistant.SummarizeAsync(Report(), default)) && f.Requests.Count == 1);
        f.Requests.Clear();
        f.Respond = _ =>
        {
            f.Assistant.Configure("other-client", "capture-model", true, true, true);
            f.Assistant.Configure("capture-client", "capture-model", true, true, true);
            return Task.FromResult(Answer);
        };
        check("account_aba_fences_original_profile_before_switch", await RejectAsync(() => f.Assistant.SummarizeAsync(Report(), default)) && f.Requests.Count == 1);
        var unchanged = f.Capture();
        f.Assistant.Configure("capture-client", "capture-model", true, true, true);
        check("identical_configuration_does_not_invalidate_capture", f.Memory.IsGenerationPersonalizationCurrent(unchanged, true, DateTimeOffset.UtcNow));
        f.Assistant.SetPersonalizationEnabled(false); f.Requests.Clear();
        f.Respond = _ => { f.Memory.Add("UNUSED_MEMORY_DURING_DISABLED_GENERATION"); f.Memory.SaveRules("UNUSED_RULE_DURING_DISABLED_GENERATION"); return Task.FromResult(Answer); };
        var disabled = await f.Assistant.SummarizeAsync(Report(), default);
        check("disabled_personalization_binds_policy_only_and_ignores_unused_memory_edits", disabled.Dependencies is { Mode: OutputDependencyMode.Bound, Contexts.Length: 1 }
            && disabled.Dependencies.Contexts[0].Provider == AssistantMemoryDependencies.PolicyProvider
            && !f.Requests[0].Contains("CAPTURE_MEMORY_A", StringComparison.Ordinal) && !f.Requests[0].Contains("CAPTURE_RULE_A", StringComparison.Ordinal));
        f.Assistant.SetPersonalizationEnabled(true); f.Requests.Clear();
        f.Respond = _ => Task.FromResult(Answer);
        f.Assistant.PublicOutputObserver = (_, _) => f.Memory.Add("changed during output observer");
        check("observer_mutation_cannot_release_prechange_output", await RejectAsync(() => f.Assistant.SummarizeAsync(Report(), default)) && f.Requests.Count == 1);
    }

    private static ToolModelRequest Request(GenerationContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var input = new InboxMessage(Guid.NewGuid(), new("synthetic-user", "synthetic-binding", "synthetic-conversation"),
            "capture-source", "说明当前情况", MessageTrust.AuthenticatedUser, now, now, 1, InboxStage.Processing);
        return new(input, 1, [], [], [], GenerationContext: context);
    }

    private static async Task ToolContextAsync(string directory, Action<string, bool> check)
    {
        using var f = new Fixture(directory);
        var captured = f.Capture();
        var context = new GenerationContext(Guid.NewGuid(), 0, AssistantMemoryDependencies.ToOutputDependencies(captured));
        var model = (IToolModel)f.Assistant;
        f.Respond = _ => Task.FromResult(JsonSerializer.Serialize(new { calls = Array.Empty<object>(), replies = new[] { Answer } }));
        check("bound_tool_generation_requires_host_capture_resolver_before_http", await RejectAsync(() => model.GenerateAsync(Request(context), default)) && f.Requests.Count == 0);
        f.Assistant.GenerationPersonalizationProvider = _ => captured;
        check("configured_production_resolver_rejects_missing_generation_context", await RejectAsync(() => model.GenerateAsync(Request(context) with { GenerationContext = null }, default))
            && f.Requests.Count == 0);
        check("resolver_cannot_substitute_independent_for_bound_capture", await RejectAsync(() => model.GenerateAsync(Request(context with { Dependencies = OutputDependencies.Independent }), default))
            && f.Requests.Count == 0);
        var response = await model.GenerateAsync(Request(context), default);
        check("tool_generation_injects_the_exact_host_bound_rules_and_memory", response.ReplySegments.SequenceEqual(new[] { Answer })
            && f.Requests.Count == 1 && f.Requests[0].Contains("CAPTURE_RULE_A", StringComparison.Ordinal) && f.Requests[0].Contains("CAPTURE_MEMORY_A", StringComparison.Ordinal));
        f.Memory.Add("NEW_MEMORY_AFTER_CAPTURE");
        check("stale_tool_context_is_not_replaced_by_latest_memory", await RejectAsync(() => model.GenerateAsync(Request(context), default)) && f.Requests.Count == 1);
    }

    private sealed class SummaryStub(bool generated, OutputDependencies? dependencies) : ICompletionSummarizer
    {
        public bool CompletionSummariesEnabled => true;
        public Task<CompletionSummary> SummarizeAsync(WeChatReport report, CancellationToken ct)
            => Task.FromResult(new CompletionSummary(Answer, "synthetic", generated, "synthetic fallback", Dependencies: dependencies));
    }
    private static async Task PreparationAsync(Action<string, bool> check)
    {
        async Task<PreparedCompletion> Prepare(bool generated, OutputDependencies? dependencies)
        {
            var settings = new Settings();
            await using var service = new WeChatService(settings, () => new(Array.Empty<TaskRecord>(), null, Array.Empty<HostSnapshot>()), () => { },
                new Handler((_, _) => throw new InvalidOperationException("No network is allowed in preparation.")), receiveMessages: false, summarizer: new SummaryStub(generated, dependencies));
            var report = Report();
            var notification = new WeChatNotification("synthetic-capture", "", "", DateTimeOffset.UtcNow, new("synthetic-task", report.TurnId, report.Id, report));
            var method = typeof(WeChatService).GetMethod("PrepareCompletionWithDependenciesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            return await (Task<PreparedCompletion>)method.Invoke(service, new object[] { notification, CancellationToken.None, true })!;
        }
        var missing = await Prepare(true, null);
        check("generated_summary_without_manifest_remains_legacy_unknown", missing.Text == Answer && missing.Dependencies.Mode == OutputDependencyMode.LegacyUnknown);
        var independent = await Prepare(true, OutputDependencies.Independent);
        check("synthetic_summary_independence_requires_explicit_declaration", independent.Text == Answer && independent.Dependencies.Mode == OutputDependencyMode.Independent);
        var fallback = await Prepare(false, null);
        check("program_authored_summary_fallback_is_explicitly_independent", fallback.Dependencies.Mode == OutputDependencyMode.Independent && fallback.Text != Answer);
    }
    private static async Task<bool> RejectAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (ChatGptException) { return true; }
    }
}
