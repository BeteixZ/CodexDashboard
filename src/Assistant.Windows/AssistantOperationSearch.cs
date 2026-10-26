using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace CodexDashboard;

public partial class AssistantWorkWindow
{
    private int operationPage;
    private string operationQuery = "";
    private const int OperationPageSize = 50;
    private void SearchOperationsClicked(object sender, RoutedEventArgs e) => ShowOperationPage(OperationSearchBox.Text, 0);
    private void OperationSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; ShowOperationPage(OperationSearchBox.Text, 0);
    }
    private void PreviousOperationsClicked(object sender, RoutedEventArgs e) => ShowOperationPage(operationQuery, operationPage - 1);
    private void NextOperationsClicked(object sender, RoutedEventArgs e) => ShowOperationPage(operationQuery, operationPage + 1);
    internal void ShowOperationPage(string query, int pageIndex)
    {
        InvalidateIfIdentityChanged();
        if (displayedSnapshot is not { } snapshot || lifetime.IsCancellationRequested) return;
        operationQuery = query.Trim();
        var details = snapshot.OperationDetails?.Where(d => d.Identity == snapshot.Identity)
            .GroupBy(d => d.OperationId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Body);
        bool Contains(string value) => value.Contains(operationQuery, StringComparison.OrdinalIgnoreCase);
        var rows = snapshot.Operations.Where(row => operationQuery.Length == 0 || Contains(row.Id) || Contains(row.Kind)
            || Contains(row.State) || Contains(row.Target) || (Guid.TryParse(row.Id, out var id) && details != null
                && details.TryGetValue(id, out var body) && Contains(body))).ToArray();
        operationPage = Math.Clamp(pageIndex, 0, Math.Max(0, (rows.Length - 1) / OperationPageSize));
        OperationsGrid.SelectedItem = null; ClearOperationDetails();
        var page = rows.Skip(operationPage * OperationPageSize).Take(OperationPageSize).ToArray();
        OperationsGrid.ItemsSource = page;
        OperationCountText.Text = rows.Length == 0 ? "没有匹配的操作。留空查找可查看全部。"
            : $"本次读取匹配 {rows.Length} 项 · 显示第 {operationPage * OperationPageSize + 1}–{operationPage * OperationPageSize + page.Length} 项 · 刷新记录以加载变化";
        PreviousOperationsButton.IsEnabled = operationPage > 0;
        NextOperationsButton.IsEnabled = (operationPage + 1) * OperationPageSize < rows.Length;
    }
}
