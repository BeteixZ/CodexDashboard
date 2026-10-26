using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexDashboard;

// Public endpoints only: no settings/vault access, no model generation, no
// response body, exception message, proxy credentials or request headers logged.
internal static class AssistantNetworkDiagnostics
{
    internal static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var destination = new Uri(ChatGptAuth.Resource + "/models");
        var proxy = HttpClient.DefaultProxy;
        var proxyUri = proxy.GetProxy(destination);
        var probes = new List<object>();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) {
            Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 1_000_000
        };
        for (int index = 0; index < 5; index++)
        {
            var uri = index == 1 ? new Uri(ChatGptAuth.Issuer + "/.well-known/openid-configuration") : destination;
            var watch = Stopwatch.StartNew();
            try
            {
                using var response = await client.GetAsync(uri);
                probes.Add(new { host = uri.Host, status = (int)response.StatusCode, version = response.Version.ToString(), elapsedMs = watch.ElapsedMilliseconds });
            }
            catch (Exception ex)
            {
                probes.Add(new { host = uri.Host, elapsedMs = watch.ElapsedMilliseconds, failure = AssistantFailure.From(ex, "public_connectivity_probe") });
            }
            if (index < 4) await Task.Delay(500);
        }
        File.WriteAllText(Path.Combine(directory, "network.json"), JsonSerializer.Serialize(new {
            at = DateTimeOffset.UtcNow, authenticated = false, proxyBypassed = proxy.IsBypassed(destination),
            proxyLoopback = proxyUri?.IsLoopback, proxyPort = proxyUri?.Port, probes
        }, Settings.Json));
    }
}
