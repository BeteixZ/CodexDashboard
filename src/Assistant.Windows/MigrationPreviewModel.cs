using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Codex.Migration;

namespace CodexDashboard;

internal sealed record MigrationPreviewRequest(string SourceDirectory, string TargetDirectory,
    string TransactionDirectory, string? InstallationDirectory = null);
internal sealed record MigrationPreviewCategory(string Name, int Count);
internal sealed record MigrationPreviewState(bool Valid, string Code, string Summary,
    string? ResolvedSource, IReadOnlyList<DataDirectoryCandidate> Candidates,
    MigrationPlan? Plan, MigrationInspection? Inspection, IReadOnlyList<MigrationPreviewCategory> Categories,
    string RollbackDetail);

// Read-only presentation boundary. No guard injection, receiver/model construction, or mutating
// migration API is exposed to this model. All source paths come from explicit user input.
internal sealed class MigrationPreviewModel
{
    internal MigrationPreviewState Evaluate(MigrationPreviewRequest input)
    {
        if (new[] { input.SourceDirectory, input.TargetDirectory, input.TransactionDirectory }.Any(string.IsNullOrWhiteSpace))
            return Error("directories_required", "请填写旧数据目录、新助手数据目录，以及单独保存备份的事务目录。");
        try
        {
            foreach (string path in new[] { input.SourceDirectory, input.TargetDirectory, input.TransactionDirectory,
                input.InstallationDirectory }.OfType<string>().Where(p => !string.IsNullOrWhiteSpace(p)))
                if (!IsExplicitLocalPath(path)) return Error("local_absolute_directory_required", "请选择本机绝对目录；不接受网络、设备或含糊的路径。");
            string source = string.IsNullOrWhiteSpace(input.InstallationDirectory)
                ? Path.GetFullPath(input.SourceDirectory)
                : MigrationDiscovery.ResolveDataDirectory(input.InstallationDirectory, input.SourceDirectory);
            if (!IsExplicitLocalPath(source))
                return Error("local_absolute_directory_required", "旧安装目录中的位置记录不是可检查的本机目录。");
            var candidates = MigrationDiscovery.Locate([input.SourceDirectory, source]);
            var request = new MigrationRequest(source, Path.GetFullPath(input.TargetDirectory),
                Path.GetFullPath(input.TransactionDirectory));
            var engine = new MigrationEngine(); // The default production guard remains deny.
            var plan = engine.Plan(request);
            var inspection = engine.Inspect(request);
            if (!inspection.TransactionExists && ContainsPayload(request.TargetDirectory))
                return Error("target_not_empty", "新助手目录已有数据，请选择空目录；不能把现有账户合并覆盖。");
            if (!inspection.TransactionExists && ContainsPayload(request.TransactionDirectory))
                return Error("transaction_not_empty", "备份与事务目录已有文件，请选择空目录或核对原事务记录。");
            var categories = new[]
            {
                new MigrationPreviewCategory("连接与偏好设置", plan.RelativeFiles.Count(f => f == "settings.json")),
                new MigrationPreviewCategory("受保护的模型账户库", plan.RelativeFiles.Count(f => f == "chatgpt.credentials")),
                new MigrationPreviewCategory("任务接续记录", plan.RelativeFiles.Count(f => f == "task-control.state")),
                new MigrationPreviewCategory("助手规则", plan.RelativeFiles.Count(f => f.EndsWith("/AGENTS.md", StringComparison.Ordinal))),
                new MigrationPreviewCategory("受保护的长期记忆", plan.RelativeFiles.Count(f => f.EndsWith("/memory.state", StringComparison.Ordinal)))
            }.Where(c => c.Count > 0).ToArray();
            bool valid = inspection.Rollback != RollbackAssessment.Unverified && plan.RelativeFiles.Contains("settings.json");
            string summary = !valid ? "目录或事务记录还不能核实，请保留原文件并核对所选位置。"
                : inspection.TransactionExists ? "已找到迁移事务。这里只展示记录的阶段；执行前仍需停机与完整性核对。"
                : $"找到 {plan.RelativeFiles.Count} 个可迁移文件，共 {plan.TotalBytes:N0} 字节。当前仅提供检查，正式交接尚未开放。";
            return new(valid, valid ? inspection.Code : "migration_preview_unverified", summary, source,
                candidates, plan, inspection, categories, RollbackText(inspection));
        }
        catch
        {
            // A corrupt or inaccessible source cannot leak exception text, file content or
            // credential material into a preview. Preserve every original file.
            return Error("directory_check_failed", "目录检查未完成。请确认目录存在、互不包含且没有链接，并保留原文件。");
        }
    }
    private static bool IsExplicitLocalPath(string path)
        => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
            && Path.IsPathFullyQualified(path) && !path.Any(char.IsControl) && !path[2..].Contains(':')
            && !path.Split('\\', '/').Any(part => part is "." or ".." || part.EndsWith('.') || part.EndsWith(' '));
    private static MigrationPreviewState Error(string code, string summary)
        => new(false, code, summary, null, [], null, null, [], "尚未取得可核对的回滚记录。");
    private static bool ContainsPayload(string directory) => Directory.Exists(directory)
        && Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path) != ".codex-migration.lock");
    private static string RollbackText(MigrationInspection inspection) => inspection.Rollback switch
    {
        RollbackAssessment.NoTransaction => "尚无迁移事务。首次交接必须保留独立备份。",
        RollbackAssessment.RequiresReconciliation => "新助手已经激活。须先保留并对账新增消息、任务和记忆，不能直接覆盖为旧快照。",
        RollbackAssessment.NeedsReceiverGuardAndIntegrityCheck => "记录尚未标记激活。回滚仍须停止新旧接收者，并核对全部文件没有新增或变化。",
        RollbackAssessment.ResumeRollbackWithGuard => "上次回滚尚未结束。须保持新旧接收者停止，再按原事务恢复。",
        RollbackAssessment.AlreadyRolledBack => "记录显示已回滚，新助手的该数据目录保持停用。",
        RollbackAssessment.FinishForwardRecovery => "上次交接尚未结束。须先核对并恢复原事务，不能覆盖重做。",
        _ => "事务记录无法核实。保留原数据及备份，不执行自动回滚。"
    };
}
