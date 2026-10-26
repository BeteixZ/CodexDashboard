using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodexDashboard;

internal static class WebSmokeTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(WebMonitorServer server, Settings settings, DashboardViewModel vm)
    {
        var checks = new Dictionary<string, bool>();
        bool enabled = settings.WebEnabled;
        string originalKey = settings.WebAccessKey;
        settings.WebEnabled = true;
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        async Task<HttpResponseMessage> Get(string path, string? key = null, string? host = null, HttpMethod? method = null)
        {
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, server.LocalUrl + path);
            if (key != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (host != null) request.Headers.Host = host;
            return await client.SendAsync(request);
        }
        try
        {
            await server.ConfigureAsync(settings, true);
            server.Publish(vm);
            using (var response = await Get("/"))
            {
                checks["loginPage"] = response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsStringAsync()).Contains("app.js");
                checks["responseHeaders"] = response.Headers.CacheControl?.NoStore == true && response.Headers.Contains("Content-Security-Policy");
            }
            using (var response = await Get("/api/snapshot")) checks["requiresKey"] = response.StatusCode == HttpStatusCode.Unauthorized;
            using (var response = await Get("/api/snapshot", "incorrect")) checks["rejectsWrongKey"] = response.StatusCode == HttpStatusCode.Unauthorized;
            using (var response = await Get("/api/snapshot", originalKey))
            {
                string body = await response.Content.ReadAsStringAsync();
                using var data = JsonDocument.Parse(body);
                checks["liveSnapshot"] = response.StatusCode == HttpStatusCode.OK && data.RootElement.GetProperty("tasks").GetArrayLength() == vm.AllTasks.Count;
                checks["taskOverviews"] = data.RootElement.GetProperty("tasks").EnumerateArray().All(t => !string.IsNullOrWhiteSpace(t.GetProperty("oneLineSummary").GetString()) && !string.IsNullOrWhiteSpace(t.GetProperty("summarySource").GetString()) && t.TryGetProperty("summary", out _));
                checks["noSettingsOrSecrets"] = !body.Contains(originalKey) && !data.RootElement.TryGetProperty("settings", out _) && data.RootElement.GetProperty("tasks").EnumerateArray().All(t => !t.TryGetProperty("cwd", out _) && !t.TryGetProperty("raw", out _) && !t.TryGetProperty("finalReport", out _));
            }
            using (var response = await Get("/api/snapshot", originalKey, method: HttpMethod.Post)) checks["readOnly"] = response.StatusCode == HttpStatusCode.MethodNotAllowed;
            using (var response = await Get("/api/snapshot", originalKey, "untrusted.example")) checks["hostValidation"] = response.StatusCode == HttpStatusCode.Forbidden;
            using (var response = await Get("/settings.json", originalKey)) checks["fixedAssetPaths"] = response.StatusCode == HttpStatusCode.NotFound;
            using (var response = await Get("/app.js")) checks["scriptServed"] = response.StatusCode == HttpStatusCode.OK;
            checks["privateNetworks"] = new[] { "127.0.0.1", "10.1.2.3", "172.16.0.1", "192.168.1.2", "100.64.1.1", "::ffff:127.0.0.1", "fd00::1" }.All(a => WebMonitorServer.IsPrivateAddress(IPAddress.Parse(a)))
                && new[] { "8.8.8.8", "172.15.0.1", "100.63.0.1", "2001:4860:4860::8888" }.All(a => !WebMonitorServer.IsPrivateAddress(IPAddress.Parse(a)));
            settings.WebAccessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await server.ConfigureAsync(settings, true);
            using (var response = await Get("/api/snapshot", originalKey)) checks["oldKeyRevoked"] = response.StatusCode == HttpStatusCode.Unauthorized;
            using (var response = await Get("/api/snapshot", settings.WebAccessKey)) checks["newKeyAccepted"] = response.StatusCode == HttpStatusCode.OK;
            settings.WebEnabled = false;
            await server.ConfigureAsync(settings, true);
            checks["disableServer"] = server.LocalUrl.Length == 0;
        }
        finally
        {
            settings.WebAccessKey = originalKey;
            settings.WebEnabled = enabled;
            await server.ConfigureAsync(settings, true);
        }
        return checks;
    }
}
