using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;

internal sealed partial class DurableAssistantRuntime
{
    private async Task<string> RefreshOwnedProjectSourceAsync(InboxMessage message, CancellationToken ct)
    {
        var context = await ledger.ReadConversationContextAsync(message.Identity, ct);
        var runtime = OwnedRuntime;
        if (context.Focus == null || runtime == null) return "请先从任务列表选择已连接的远程自建任务。";
        var tasks = runtime.Tasks().Where(task => SameTask(context.Focus.Task,
            new(task.Handle.Identity.HostId, task.Handle.Identity.ThreadId, ProfileId: task.Handle.Identity.ProfileId))).ToArray();
        if (tasks.Length != 1) return "当前目标不是唯一可核对的自建任务；已有任务的来源由原采集器更新。";
        try
        {
            bool refreshed = await runtime.RefreshProjectProofAsync(tasks[0].ConfigurationId, () => IsCurrentBinding(message.Identity), ct);
            refreshed &= runtime.ProjectProof(tasks[0].ConfigurationId, tasks[0].Snapshot.Cwd) != null;
            return refreshed ? "项目目录来源已刷新。可以审阅这个项目的知识；来源过期或连接断开后会停止使用。"
                : "暂时无法证明这个项目的目录来源，没有启用项目知识。请检查连接和远程目录。";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return "项目来源读取失败，旧证明已停止使用；没有修改项目知识或执行任务。"; }
    }
}
