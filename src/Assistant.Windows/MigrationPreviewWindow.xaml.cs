using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Codex.Migration;
using Microsoft.Win32;

namespace CodexDashboard;

public partial class MigrationPreviewWindow : Window
{
    private readonly MigrationPreviewModel model = new();
    internal MigrationPreviewState? Preview { get; private set; }
    public MigrationPreviewWindow() => InitializeComponent();
    internal void SetDirectories(string source, string target, string transaction, string? installation = null)
    {
        SourceBox.Text = source; TargetBox.Text = target; TransactionBox.Text = transaction;
        InstallationBox.Text = installation ?? "";
    }
    internal MigrationPreviewState Evaluate()
    {
        Preview = model.Evaluate(new(SourceBox.Text, TargetBox.Text, TransactionBox.Text, InstallationBox.Text));
        SummaryText.Text = Preview.Summary;
        ResolvedSourceText.Text = Preview.ResolvedSource == null ? "" : "核对的旧数据位置：" + Preview.ResolvedSource;
        CandidatesText.Text = Preview.Candidates.Count > 1
            ? "所填目录与旧安装记录指向不同位置，请核对以上实际位置。候选：" + string.Join("；", Preview.Candidates.Select(c => c.Directory))
            : "";
        CategoriesList.ItemsSource = Preview.Categories;
        PhaseText.Text = Preview.Inspection?.Phase is { } phase ? "事务阶段：" + Phase(phase) : "";
        RollbackText.Text = Preview.RollbackDetail;
        return Preview;
    }
    private void CheckClicked(object sender, RoutedEventArgs e) => Evaluate();
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择需要检查的本机目录", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var box = (((Button)sender).Tag as string) switch
        {
            "source" => SourceBox, "installation" => InstallationBox,
            "target" => TargetBox, _ => TransactionBox
        };
        box.Text = dialog.FolderName;
    }
    private static string Phase(MigrationPhase value) => value switch
    {
        MigrationPhase.Preparing => "准备备份", MigrationPhase.Prepared => "备份已准备",
        MigrationPhase.DisablingSource => "正在停用旧配置", MigrationPhase.SourceDisabled => "旧配置已停用",
        MigrationPhase.Publishing => "正在导入新目录", MigrationPhase.Published => "新目录已导入",
        MigrationPhase.Completed => "交接已记录完成", MigrationPhase.RollbackDisablingTarget => "正在停用新目录",
        MigrationPhase.RollbackRestoringSource => "正在恢复旧目录", MigrationPhase.RolledBack => "已回滚",
        _ => "尚未执行"
    };
}
