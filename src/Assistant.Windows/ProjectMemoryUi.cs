using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;

namespace CodexDashboard;

internal sealed partial class ChatGptAssistant
{
    private IProjectMemoryResolver? projectMemoryResolver;
    private Func<CancellationToken, Task<ProjectMemoryTarget?>>? projectMemoryTargetProvider;
    private event Action? ProjectMemoryContextChanged;
    internal IProjectMemoryResolver? ProjectMemoryResolver
    {
        get => projectMemoryResolver;
        set
        {
            if (ReferenceEquals(projectMemoryResolver, value)) return;
            projectMemoryResolver = value; ProjectMemoryContextChanged?.Invoke();
        }
    }
    internal Func<CancellationToken, Task<ProjectMemoryTarget?>>? ProjectMemoryTargetProvider
    {
        get => projectMemoryTargetProvider;
        set
        {
            if (ReferenceEquals(projectMemoryTargetProvider, value)) return;
            projectMemoryTargetProvider = value; ProjectMemoryContextChanged?.Invoke();
        }
    }
    internal async Task<ProjectMemoryEditorWindow> CreateProjectMemoryEditorAsync(string? entryId = null,
        CancellationToken cancellationToken = default)
    {
        var resolver = ProjectMemoryResolver;
        var provider = ProjectMemoryTargetProvider;
        string profile = Memory.Profile;
        bool Current() => ReferenceEquals(resolver, ProjectMemoryResolver)
            && ReferenceEquals(provider, ProjectMemoryTargetProvider) && Memory.Profile == profile;
        if (resolver == null || provider == null)
            throw new InvalidOperationException("尚未取得可核对的项目。请先在微信发送“任务列表”并选定明确任务，或发送“切换到 完整名称”，再打开项目记忆。");
        var target = await provider(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Current() || target == null)
            throw new InvalidOperationException("当前任务、项目来源或账户已变化，请重新选择任务后再打开项目记忆。");
        var snapshot = Memory.CaptureProjectEditor(target, resolver, entryId);
        if (!Current()) throw new InvalidOperationException("当前项目来源或账户已变化，本次未打开编辑器。");
        var editor = new ProjectMemoryEditorWindow(Memory, snapshot, resolver, provider, Current);
        ProjectMemoryContextChanged += editor.InvalidateContext;
        Memory.Changed += editor.CheckContextNow;
        Changed += editor.CheckContextNow;
        editor.Closed += (_, _) =>
        {
            ProjectMemoryContextChanged -= editor.InvalidateContext;
            Memory.Changed -= editor.CheckContextNow;
            Changed -= editor.CheckContextNow;
        };
        return editor;
    }
}

// A read-only presentation of the saved scope; never a target selector.
public sealed class ProjectMemoryScopeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is AssistantMemoryEntry entry ? Describe(entry) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    internal static string BackendLabel(string backend) => backend switch
    { "assistant-owned" => "助手自建任务", "desktop-existing" => "桌面已有任务", _ => "未核对的任务来源" };
    internal static string Describe(AssistantMemoryEntry entry)
    {
        if (entry.Metadata?.Scope.Kind != AssistantMemoryScopeKind.Project) return "";
        try
        {
            var project = ProjectMemoryProof.ParseReference(entry.Metadata.Scope.ProjectReference!);
            return (project.HostId == "local" ? "仅此本机项目" : "仅此远程项目 · " + HostIdentity.DisplayName(project.HostId)) + " · " + BackendLabel(project.Backend) + " · "
                + (project.ProfileId ?? "桌面默认配置") + "\n项目目录标识：" + project.CanonicalDirectory;
        }
        catch { return "项目范围未能核对；不能作为个人记忆修改或使用。"; }
    }
}
