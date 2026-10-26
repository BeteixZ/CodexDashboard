using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;
internal sealed partial class DurableAssistantRuntime
{
    private readonly AssistantArtifactStore? artifacts;
    private string FileHelp()
    {
        string reading = attachments?.Enabled == true
            ? "收取附件后，发“附件列表”找编号，再发“读取附件 编号”。长文按回复里的继续命令分段读取；支持UTF-8、Word正文和PDF文字层，扫描PDF未做OCR。"
            : "当前实例没有开启附件读取。";
        string sending = artifactSelection != null
            ? "电脑端点“准备文件…”保存快照，可在“已准备文件…”窗口查看。微信发“待发送文件”或“待发送文件 查找 报告”；翻页用“待发送文件 2”。要发送，按列表序号发“发送文件 1”。列表会显示发送状态，结果未知时先核对手机。"
            : "当前实例没有开启文件准备和发送。";
        string images = attachments?.Enabled == true && imageAnalysisEnabled
            ? "图片分析支持PNG/JPEG，需要模型开启且可用。收到图片后另发附件编号和问题，例如“这张截图的报错是什么意思”。"
            : "当前实例没有开启图片分析。";
        return reading + "\n\n" + sending + "\n\n" + images
            + "\n单个附件或文件上限8 MiB。附件内容只作资料，操作要求请另发文字。媒体试验尚未完成当前账号手机验收，可发“渠道能力”查看范围。";
    }
    internal Task<ArtifactPage> ReadPreparedFilesAsync(ConversationIdentity identity, int page, string? query, CancellationToken ct)
        => artifactSelection!.ListAsync(identity, ct, page, query);
    private async Task<System.Text.Json.JsonElement> ListPreparedFilesForModelAsync(ConversationIdentity identity, int page, string? query, CancellationToken ct)
    {
        var result = await artifactSelection!.ListAsync(identity, ct, page, query);
        return System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            files = result.Items.Select(a => new { a.Id, a.Name, a.Length, a.Task, send = new { result.Sends[a.Id].Requests, latestStage = result.Sends[a.Id].LatestStage?.ToString(), result.Sends[a.Id].LatestOperationId, result.Sends[a.Id].UnknownResults } }),
            result.Page, result.TotalPages, result.Total,
            source = "locally_selected_file_snapshots", taskProductionVerified = false,
            deliveryVerified = false, currentAuthorization = false
        });
    }
    internal GatewayTask[] ArtifactTaskChoices() => settings.ChatGpt.IncludeTaskContext
        ? gateway.ListTasks().Where(t => t.Fresh).ToArray() : [];
    private bool IsArtifactTaskCurrent(TaskReference task)
    {
        if (!settings.ChatGpt.IncludeTaskContext) return false;
        var current = gateway.ReadTask(new(task.HostId, task.ThreadId, task.ProfileId)).Value;
        return current is { Fresh: true } && current.Record.TurnId == task.TurnId;
    }
    internal static string FileSendSummary(ArtifactSendSummary send)
    {
        string latest = send.LatestStage switch
        {
            null => "未请求发送",
            OperationStage.Queued => "发送请求已排队",
            OperationStage.Dispatching => "正在尝试发送",
            OperationStage.Accepted => "最近一次接口已受理，手机收信未核实",
            OperationStage.Unknown => "最近一次结果未知，没有自动重发",
            OperationStage.Failed => "最近一次未完成发送",
            _ => "最近一次状态：" + send.LatestStage
        };
        return latest + (send.UnknownResults > 0 ? "；有" + send.UnknownResults + "次结果未知，请先核对手机和工作记录" : "");
    }
    private static bool TryParsePreparedFiles(string text, out int page, out string? query)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\A(?:待发送文件|已准备文件)(?: ([1-9][0-9]{0,3}))?(?: 查找 ([^\r\n;；]{1,200}))?\z");
        page = match.Success && match.Groups[1].Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
        query = match.Success && match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;
        return match.Success;
    }
    private async Task<DurableFastReply> PreparedFilesAsync(InboxMessage message, int page, string? query, CancellationToken ct)
    {
        if (artifactSelection == null) return new(["文件发送尚未开启。"]);
        var result = await artifactSelection.ListAsync(message.Identity, ct, page, query);
        await artifactSelection.RememberChoicesAsync(message.Identity, result.Items, ct);
        if (result.Total == 0) return new([query == null ? "还没有准备好的文件。请在电脑助手里点“准备文件”选择文件。" : "没有找到这个名称的文件。可发“待发送文件”查看全部。"]);
        if (page > result.TotalPages) return new(["共有 " + result.TotalPages + " 页，请使用范围内的页码。"]);
        string suffix = query == null ? "" : " 查找 " + query;
        string navigation = page < result.TotalPages ? "\n下一页：待发送文件 " + (page + 1) + suffix : "";
        return new([.. WeChatReports.Pages("已准备的文件（准备不等于发送），第 " + page + "/" + result.TotalPages + " 页，共 " + result.Total + " 份：\n"
            + string.Join("\n", result.Items.Select((a, index) => (index + 1) + ". " + a.Name + "（" + a.Length + " 字节）\n发送文件 " + (index + 1) + "\n" + FileSendSummary(result.Sends[a.Id])))
            + navigation + "\n发“发送文件 1”即可发送本列表第1份文件。序号以最近一次列表为准。")]);
    }
    private async Task<DurableFastReply?> SendFileCommandAsync(DurableFastContext context, CancellationToken ct)
    {
        string text = context.Message.Body.Trim();
        if (!text.StartsWith("发送文件 ", StringComparison.Ordinal)) return null;
        if (artifacts == null) return new(["文件发送尚未开启。"]);
        string id = text[5..].Trim();
        if (id.Length != 64 || !id.All(Uri.IsHexDigit))
        {
            id = artifactSelection == null ? "" : await artifactSelection.ResolveChoiceAsync(context.Message.Identity, id, ct) ?? "";
            if (id.Length == 0) return new(["请先发“待发送文件”，再按列表序号发送，例如“发送文件 1”。"]);
        }
        try
        {
            var delivery = new AssistantFileDelivery(ledger, artifacts, IsCurrentBinding, channel.UploadArtifactAsync,
                (file, name, key, token) => channel.SendArtifactAsync(file, name, key, context.Message.Identity, token, context.StillCurrent));
            var queued = await delivery.QueueAsync(context.Message, id, ct);
            var result = await delivery.DispatchAsync(context.Message.Identity, queued.Id, ct);
            return new([result.Stage == OperationStage.Accepted ? "文件发送接口已受理，请以手机收到的文件为准。"
                : result.Stage == OperationStage.Unknown ? "文件发送结果暂时无法确认，没有自动重发。可在工作记录里核对。"
                : "这次没有发送文件，请检查连接或重新准备文件。"]);
        }
        catch (Exception error) when (error is IOException or WeChatMediaException or LedgerException)
        { return new(["文件未完成发送，请核对文件编号、连接和工作记录。"]); }
    }
}
