using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexDashboard;
internal static class WeChatTypingTests
{
    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => send(r, ct); }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--typing-test", var output]) return false;
        Directory.CreateDirectory(output);
        var checks = new Dictionary<string, bool>();
        try
        {
            foreach (string mode in new[] { "normal", "session-cancel", "start-error", "no-ticket", "cancel-config" })
            {
                var calls = new List<int>();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bool wire = true;
                using var generation = new CancellationTokenSource();
                using var session = new CancellationTokenSource();
                using var gate = new SemaphoreSlim(1, 1);
                var api = new WeChatApi(new Stub(async (request, ct) =>
                {
                    using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    var root = data.RootElement;
                    wire &= root.GetProperty("ilink_user_id").GetString() == "synthetic-user";
                    if (request.RequestUri!.AbsolutePath.EndsWith("getconfig"))
                    {
                        wire &= root.GetProperty("context_token").GetString() == "synthetic-context";
                        if (mode == "cancel-config") { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
                        if (mode == "no-ticket") { entered.TrySetResult(); return Json("{\"ret\":0}"); }
                        return Json("{\"ret\":0,\"typing_ticket\":\"synthetic-ticket\"}");
                    }
                    int status = root.GetProperty("status").GetInt32();
                    wire &= root.GetProperty("typing_ticket").GetString() == "synthetic-ticket";
                    lock (calls) calls.Add(status);
                    if (status == 1) { entered.TrySetResult(); if (mode == "start-error") throw new HttpRequestException("synthetic"); }
                    return Json("{\"ret\":0}");
                }));
                var scope = new WeChatTypingScope(api, new ClawBinding { UserId = "synthetic-user", ContextToken = "synthetic-context" },
                    gate, generation.Token, session.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (mode == "session-cancel") session.Cancel();
                if (mode == "cancel-config") generation.Cancel();
                await scope.DisposeAsync();
                checks[mode + "_wire"] = wire;
                checks[mode + "_lifecycle"] = mode is "no-ticket" or "cancel-config" ? calls.Count == 0
                    : calls.Count == 2 && calls[0] == 1 && calls[1] == 2;
                checks[mode + "_releases_gate"] = gate.CurrentCount == 1;
            }
            File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(checks));
            exit(checks.ContainsValue(false) ? 1 : 0);
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString()); exit(1); }
        return true;
    }
}
