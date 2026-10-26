using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CodexDashboard;

public partial class AssistantWorkWindow
{
    private int messagePage;
    private string messageQuery = "";
    private const int MessagePageSize = 50;
    private void SearchMessagesClicked(object sender, RoutedEventArgs e) => ShowMessagePage(MessageSearchBox.Text, 0);
    private void MessageSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; ShowMessagePage(MessageSearchBox.Text, 0);
    }
    private void PreviousMessagesClicked(object sender, RoutedEventArgs e) => ShowMessagePage(messageQuery, messagePage - 1);
    private void NextMessagesClicked(object sender, RoutedEventArgs e) => ShowMessagePage(messageQuery, messagePage + 1);
    internal void ShowMessagePage(string query, int pageIndex)
    {
        InvalidateIfIdentityChanged();
        if (displayedSnapshot is not { } snapshot || lifetime.IsCancellationRequested) return;
        messageQuery = query.Trim();
        var details = snapshot.MessageDetails?.Where(d => d.Identity == snapshot.Identity)
            .GroupBy(d => (d.Id, d.Kind)).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Body);
        bool Contains(string value) => value.Contains(messageQuery, StringComparison.OrdinalIgnoreCase);
        var rows = snapshot.Messages.Where(row => messageQuery.Length == 0 || Contains(row.Id) || Contains(row.Kind)
            || Contains(row.State) || Contains(row.Target) || (details != null
                && details.TryGetValue((row.Id, row.Kind), out var body) && Contains(body))).ToArray();
        messagePage = Math.Clamp(pageIndex, 0, Math.Max(0, (rows.Length - 1) / MessagePageSize));
        MessagesGrid.SelectedItem = null; MessageDetailBox.Clear();
        var page = rows.Skip(messagePage * MessagePageSize).Take(MessagePageSize).ToArray();
        MessagesGrid.ItemsSource = page;
        MessageCountText.Text = rows.Length == 0 ? "没有匹配的发送记录。留空查找可查看全部。"
            : $"本次读取匹配 {rows.Length} 项 · 显示第 {messagePage * MessagePageSize + 1}–{messagePage * MessagePageSize + page.Length} 项 · 接口受理不等于手机送达";
        PreviousMessagesButton.IsEnabled = messagePage > 0;
        NextMessagesButton.IsEnabled = (messagePage + 1) * MessagePageSize < rows.Length;
    }
    private void MessageSelected(object sender, SelectionChangedEventArgs e)
    {
        MessageDetailBox.Clear(); InvalidateIfIdentityChanged();
        if (displayedSnapshot is not { } snapshot || MessagesGrid.SelectedItem is not AssistantWorkspaceRow row) return;
        var matches = snapshot.MessageDetails?.Where(d => d.Identity == snapshot.Identity && d.Id == row.Id && d.Kind == row.Kind).ToArray();
        MessageDetailBox.Text = matches is { Length: 1 } ? matches[0].Body : "这份快照没有可核对的完整正文，请刷新记录。";
    }
}
