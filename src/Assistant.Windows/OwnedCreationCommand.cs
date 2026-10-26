using System;
using System.Linq;
using System.Text.Json;
using Assistant.Core;

namespace CodexDashboard;

// Exact creation commands produce proposals only. The normal durable tool loop
// still validates the current user, configuration, workspace and operation.
internal static class OwnedCreationCommand
{
    internal static ToolModelResponse? Resolve(ToolModelRequest request)
    {
        if (request.DeferredWork != null || request.CurrentMessage.Trust != MessageTrust.AuthenticatedUser
            || request.CurrentMessage.Revision != request.Revision) return null;
        string body = request.CurrentMessage.Body.Trim().TrimEnd('。', '！', '!');
        if (body.StartsWith("请", StringComparison.Ordinal)) body = body[1..].TrimStart();
        if (!body.StartsWith("创建", StringComparison.Ordinal) && !body.StartsWith("新建", StringComparison.Ordinal)) return null;
        string scope = body[2..].Trim();
        bool Has(string name) => request.Tools.Any(t => t.Name == name);
        var creation = request.Observations.LastOrDefault(o => o.ToolName == "create_task");
        if (creation != null)
        {
            using var receipt = JsonDocument.Parse(creation.ResultJson);
            var result = receipt.RootElement;
            if (result.GetProperty("status").GetString() != "accepted") return null;
            var facts = result.GetProperty("facts");
            if (!facts.TryGetProperty("newlyBoundTaskEvidence", out var evidence) || evidence.ValueKind != JsonValueKind.Object) return null;
            string? id = evidence.GetProperty("task").GetProperty("ThreadId").GetString();
            if (string.IsNullOrWhiteSpace(id)) return null;
            if (facts.TryGetProperty("selectedCurrentTask", out var selected) && selected.ValueKind == JsonValueKind.True)
                return ToolModelResponse.Reply("任务建好了，已选为当前任务。\n接着发“继续当前任务：要做的事”即可；看结果发“查询当前任务结果”。");
            return ToolModelResponse.Reply("任务建好了。发“任务列表”选中它，就能用“继续当前任务：要做的事”。");
        }
        if (!Has("list_owned_tasks") || !Has("create_task")) return null;
        var listing = request.Observations.LastOrDefault(o => o.ToolName == "list_owned_tasks");
        if (listing == null)
            return request.Observations.Length == 0 && scope.Length is > 0 and <= 80
                ? ToolModelResponse.Call(new ModelToolCall(Guid.NewGuid().ToString("N"), "list_owned_tasks", "{}")) : null;
        using var document = JsonDocument.Parse(listing.ResultJson);
        var root = document.RootElement;
        if (root.GetProperty("status").GetString() != "readonlysuccess") return null;
        var catalogue = root.GetProperty("facts");
        if (!catalogue.GetProperty("completeCatalogue").GetBoolean()
            || catalogue.GetProperty("page").GetProperty("totalPages").GetInt32() != 1) return null;
        bool Matches(string? name) => !string.IsNullOrWhiteSpace(name)
            && (scope.Equals(name, StringComparison.OrdinalIgnoreCase) || scope.Equals(name + "任务", StringComparison.OrdinalIgnoreCase)
                || scope.Equals(name + " 任务", StringComparison.OrdinalIgnoreCase));
        var matches = catalogue.GetProperty("configurations").EnumerateArray()
            .Where(p => Matches(p.GetProperty("Label").GetString()) || Matches(p.GetProperty("configurationId").GetString())).ToArray();
        if (matches.Length != 1 || !matches[0].GetProperty("controlEnabled").GetBoolean()) return null;
        return ToolModelResponse.Call(new ModelToolCall(Guid.NewGuid().ToString("N"), "create_task",
            JsonSerializer.Serialize(new { configurationId = matches[0].GetProperty("configurationId").GetString() })));
    }
}
