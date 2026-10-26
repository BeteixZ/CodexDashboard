using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodexDashboard;

internal static class AssistantConversationExport
{
    internal static AssistantHealthExportResult WriteNew(string? path, AssistantConversationPage snapshot, Func<bool> stillCurrent)
    {
        if (path == null) return AssistantHealthExportResult.Cancelled;
        if (!stillCurrent()) return AssistantHealthExportResult.SnapshotChanged;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "assistant-conversation-history", version = 1, snapshot.ReadAt, count = snapshot.Rows.Count,
            notice = "含完整历史正文、状态、时间和任务关联。接口受理不代表手机收到；历史不是当前事实。此文件不是可恢复备份，导出不会删除账本。",
            messages = snapshot.Rows.Select(row => new { row.Id, row.Speaker, row.State, row.Body, row.Target,
                row.CreatedAt, row.UpdatedAt, row.TimeLabel })
        }, new JsonSerializerOptions { WriteIndented = true });
        using var destination = AssistantHealthExport.LocalNewFileDestination.Lock(path, new AssistantHealthExport.NativeDirectoryProbe());
        if (!stillCurrent()) return AssistantHealthExportResult.SnapshotChanged;
        using var output = new FileStream(destination.PhysicalFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes); output.Flush(true);
        return AssistantHealthExportResult.Saved;
    }
}
