using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Assistant.Core;

namespace CodexDashboard;

// View-only synthetic snapshots: no ledger, model, channel, task transport or production settings.
internal static class CommitmentDetailWindowTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--commitment-detail-window-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--commitment-detail-window-test") throw new ArgumentException("new_output_required");
            await RunAsync(args[1]); exit(0);
        }
        catch (Exception error) { Console.Error.WriteLine(error); exit(1); }
        return true;
    }
    private static async Task RunAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.Any(char.IsControl) || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("new_absolute_output_required");
        for (var parent = new DirectoryInfo(directory); parent != null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("redirected_output_rejected");
        Directory.CreateDirectory(directory);
        var checks = new Dictionary<string, bool>();
        void Check(string name, bool value)
        {
            checks.Add(name, value);
            File.WriteAllText(Path.Combine(directory, "commitment-detail-window-checks.json"), JsonSerializer.Serialize(new
            { count = checks.Count, failures = checks.Count(x => !x.Value), productionDataRead = false, networkUsed = false, actualModelUsed = false, checks },
                new JsonSerializerOptions { WriteIndented = true }));
            if (!value) throw new InvalidOperationException(name);
        }
        var a = new ConversationIdentity("PRIVATE_DETAIL_USER_A", "PRIVATE_DETAIL_BINDING_A", "main");
        var b = new ConversationIdentity("PRIVATE_DETAIL_USER_B", "PRIVATE_DETAIL_BINDING_B", "main");
        var id = Guid.Parse("1fb01063953449e6973f51b310359c01");
        var other = Guid.Parse("1fb01063953449e6973f51b310359c02");
        string body = "完整事项：\n" + string.Join("\n", Enumerable.Repeat("检查合成测试结果，保留尚未验证的部分，不把接口受理说成手机已经收到。", 24))
            + "\n\n安排时间：2026-10-21 09:00:00 +08:00\n通知规则：仅失败时提示。\n最近检查：已完成。\n发送记录：接口已受理，手机送达未知。";
        const string secondBody = "另一项完整安排。\n尚未检查，也没有发送记录。";
        AssistantWorkspaceSnapshot Snapshot(ConversationIdentity identity, string text, long version = 3) => new(
            "合成能力说明。", "当前交流目标：合成任务。",
            "当前没有已登记的运行错误。\n本页按当前绑定读取，每类最多显示最近 200 条；完整记录仍保留。结果未知的事项不会自动重试。"
                + "\n提醒只在助手运行时检查；电脑关机或助手退出期间不会发送。微信接口受理不代表手机已收到。", DateTimeOffset.UtcNow,
            [], [], [],
            [new(id.ToString("N"), "一次性提醒", "待核对发送记录", "截短的列表预览", "10-21 06:00:00"),
             new(other.ToString("N"), "任务订阅", "等待触发", "另一项列表预览", "10-21 06:00:00")],
            [new("synthetic-conversation", "你", "已持久接收", "原对话完整正文。\n保留这一行。", "合成任务", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "接收时间")],
            identity, 1, [new(identity, id, version, text), new(identity, other, 1, secondBody)]);
        ConversationIdentity current = a;
        var snapshot = Snapshot(a, body); int reads = 0;
        var window = Hidden(new AssistantWorkWindow(_ => { reads++; return Task.FromResult(snapshot); }, value => value == current));
        try
        {
            Check("details_start_empty_and_all_detail_controls_are_readonly", window.CommitmentDetailBox.IsReadOnly
                && window.CommitmentTechnicalBox.IsReadOnly && window.CommitmentsGrid.IsReadOnly
                && window.CommitmentTechnicalBox.Text.Length == 0 && !window.CommitmentTechnicalExpander.IsExpanded && reads == 0);
            window.Show(); await Idle(window); await window.RefreshAsync();
            Check("refresh_loads_list_without_automatically_revealing_goal", window.CommitmentsGrid.Items.Count == 2
                && window.CommitmentDetailBox.Text != body && window.CommitmentTechnicalBox.Text.Length == 0);
            int before = reads; window.CommitmentsGrid.SelectedIndex = 0;
            Check("selection_shows_entire_snapshot_goal_rules_and_delivery_without_any_new_read", reads == before
                && window.CommitmentDetailBox.Text == body && window.CommitmentDetailBox.Text.Length > 180
                && window.CommitmentDetailBox.Text.Contains("手机送达未知"));
            Check("technical_details_are_collapsed_and_only_include_exact_work_id_and_version",
                !window.CommitmentTechnicalExpander.IsExpanded && window.CommitmentTechnicalBox.Text.Contains(id.ToString("N"))
                && window.CommitmentTechnicalBox.Text.Contains("记录版本：3") && !window.CommitmentTechnicalBox.Text.Contains("PRIVATE_"));
            Capture(window, Path.Combine(directory, "commitment-details.png"));
            window.Width = window.MinWidth; window.Height = window.MinHeight; await Idle(window);
            Check("minimum_window_keeps_list_scrollable_body_and_fixed_refresh_reachable",
                window.CommitmentsGrid.ActualHeight >= 60 && window.CommitmentDetailScroll.ViewportHeight >= 60
                && Inside(window.RefreshButton, window) && Inside(window.CommitmentDetailScroll, window));
            window.CommitmentDetailScroll.ScrollToEnd(); await Idle(window);
            Check("long_full_goal_is_scrollable_at_minimum_size_without_changing_content",
                window.CommitmentDetailScroll.ScrollableHeight > 0 && window.CommitmentDetailScroll.VerticalOffset > 0
                && window.CommitmentDetailBox.Text == body && Inside(window.CommitmentTechnicalExpander, window));
            Capture(window, Path.Combine(directory, "commitment-details-minimum.png"));
            window.CommitmentTechnicalExpander.IsExpanded = true;
            window.CommitmentsGrid.SelectedIndex = 1;
            Check("selecting_another_item_clears_old_body_and_collapses_technical_panel",
                window.CommitmentDetailBox.Text == secondBody && !window.CommitmentTechnicalExpander.IsExpanded
                && !window.CommitmentTechnicalBox.Text.Contains(id.ToString("N")));
            window.ConversationTab.IsSelected = true; window.ConversationGrid.SelectedIndex = 0;
            Check("existing_conversation_page_keeps_full_original_body_and_readonly_controls",
                window.WorkTabs.Items.Count == 7 && window.ConversationGrid.IsReadOnly && window.ConversationDetailBox.IsReadOnly
                && window.ConversationDetailBox.Text.StartsWith("原对话完整正文。\n保留这一行。", StringComparison.Ordinal));
            window.WorkTabs.SelectedIndex = 0;
            snapshot = snapshot with { CommitmentDetails = [new(a, id, 3, body), new(a, id, 4, "PRIVATE_DUPLICATE_DETAIL")] };
            await window.RefreshAsync(); window.CommitmentsGrid.SelectedIndex = 0;
            Check("duplicate_detail_ids_do_not_choose_an_arbitrary_body", window.CommitmentTechnicalBox.Text.Length == 0
                && !window.CommitmentDetailBox.Text.Contains("PRIVATE_") && window.CommitmentDetailBox.Text != body);
            snapshot = Snapshot(a, body) with { CommitmentDetails = [new(b, id, 3, "PRIVATE_OTHER_IDENTITY_DETAIL")] };
            await window.RefreshAsync(); window.CommitmentsGrid.SelectedIndex = 0;
            Check("same_id_detail_from_another_identity_is_never_displayed", !window.CommitmentDetailBox.Text.Contains("PRIVATE_")
                && window.CommitmentTechnicalBox.Text.Length == 0);
            snapshot = Snapshot(a, body) with { Commitments = [new(id.ToString("N")[..8], "提醒", "等待", "预览", "现在")] };
            await window.RefreshAsync(); window.CommitmentsGrid.SelectedIndex = 0;
            Check("abbreviated_row_id_cannot_resolve_full_item_by_guessing", window.CommitmentDetailBox.Text != body
                && window.CommitmentTechnicalBox.Text.Length == 0);
            snapshot = Snapshot(a, body) with { CommitmentDetails = null };
            await window.RefreshAsync(); window.CommitmentsGrid.SelectedIndex = 0;
            Check("old_snapshot_without_details_does_not_expand_truncated_preview_as_full_goal", window.CommitmentDetailBox.Text != "截短的列表预览"
                && window.CommitmentDetailBox.Text.Contains("没有可核对") && window.CommitmentTechnicalBox.Text.Length == 0);
            snapshot = Snapshot(a, body); await window.RefreshAsync(); window.CommitmentsGrid.SelectedIndex = 0;
            current = b; window.InvalidateIfIdentityChanged();
            Check("binding_change_clears_full_goal_metadata_all_rows_and_original_history",
                window.CommitmentDetailBox.Text != body && window.CommitmentTechnicalBox.Text.Length == 0
                && window.CommitmentsGrid.Items.Count == 0 && window.ConversationGrid.Items.Count == 0
                && window.ConversationDetailBox.Text.Length == 0 && window.ReadAtText.Text.Length == 0);
            snapshot = Snapshot(b, "当前身份的新事项。", 5); await window.RefreshAsync(); window.CommitmentsGrid.SelectedIndex = 0;
            Check("explicit_refresh_only_reveals_new_identity_current_snapshot", window.CommitmentDetailBox.Text == "当前身份的新事项。"
                && window.CommitmentTechnicalBox.Text.Contains("记录版本：5") && !window.CommitmentDetailBox.Text.Contains(body));
            window.Close();
            Check("closing_window_clears_previous_body_and_metadata", window.CommitmentTechnicalBox.Text.Length == 0
                && window.CommitmentsGrid.Items.Count == 0 && window.ConversationGrid.Items.Count == 0
                && window.CommitmentDetailBox.Text != "当前身份的新事项。");
        }
        finally { window.Close(); }

        current = a; var initial = Snapshot(a, body);
        var delayed = new TaskCompletionSource<AssistantWorkspaceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool wait = false;
        var slow = new AssistantWorkWindow(_ => wait ? delayed.Task : Task.FromResult(initial), value => value == current);
        try
        {
            await slow.RefreshAsync(); slow.CommitmentsGrid.SelectedIndex = 0; wait = true;
            Task loading = slow.RefreshAsync();
            Check("refresh_immediately_clears_selected_goal_before_delayed_read_finishes",
                slow.CommitmentDetailBox.Text != body && slow.CommitmentTechnicalBox.Text.Length == 0 && !slow.RefreshButton.IsEnabled);
            current = b; slow.InvalidateIfIdentityChanged(); delayed.SetResult(initial); await loading;
            Check("late_old_binding_read_cannot_repopulate_detail_or_list", slow.CommitmentsGrid.Items.Count == 0
                && slow.CommitmentDetailBox.Text != body && slow.CommitmentTechnicalBox.Text.Length == 0 && slow.RefreshButton.IsEnabled);
        }
        finally { slow.Close(); }
        bool fail = false; current = a;
        var failed = new AssistantWorkWindow(_ => fail
            ? Task.FromException<AssistantWorkspaceSnapshot>(new IOException("PRIVATE_RAW_REASON_APPROVAL_BODY"))
            : Task.FromResult(initial), value => value == current);
        try
        {
            await failed.RefreshAsync(); failed.CommitmentsGrid.SelectedIndex = 0; fail = true; await failed.RefreshAsync();
            Check("failed_read_clears_old_detail_without_disclosing_raw_exception",
                failed.CommitmentDetailBox.Text != body && failed.CommitmentTechnicalBox.Text.Length == 0
                && !failed.ContextText.Text.Contains("PRIVATE_") && !failed.DiagnosticText.Text.Contains("PRIVATE_") && failed.RefreshButton.IsEnabled);
        }
        finally { failed.Close(); }
        var closingRead = new TaskCompletionSource<AssistantWorkspaceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new AssistantWorkWindow(_ => closingRead.Task, value => value == current);
        Task pending = closed.RefreshAsync(); closed.Close(); closingRead.SetResult(initial); await pending;
        Check("closed_window_rejects_late_snapshot_completion", closed.CommitmentsGrid.Items.Count == 0
            && closed.CommitmentDetailBox.Text != body && closed.CommitmentTechnicalBox.Text.Length == 0);
        await SearchAndBrowseAsync(directory, Check);
    }
    private static async Task SearchAndBrowseAsync(string directory, Action<string, bool> check)
    {
        var source = CommitmentDetailsTests.Sample();
        var template = source.WorkItems.Single();
        var items = Enumerable.Range(0, 205).Select(index => template with
        {
            Id = Guid.NewGuid(), UpdatedAt = template.UpdatedAt.AddMinutes(index),
            Goal = index == 0 ? new string('早', 210) + "查找旧安排" : "安排 " + index
        }).ToImmutableArray();
        var foreign = template with { Id = Guid.NewGuid(), Identity = new("other", "other", "main"), Goal = "查找旧安排" };
        source = source with { WorkItems = items.Add(foreign), Schedules = [] };
        var history = new AssistantCommitmentHistory(source);
        var first = history.ReadPage();
        var snapshot = new AssistantWorkspaceSnapshot("只读查看", "当前没有选定任务。", "合成记录，不连接外部服务。",
            template.UpdatedAt, [], [], [], first.Rows, Identity: source.Identity,
            CommitmentDetails: first.Details, CommitmentHistory: history);
        var current = source.Identity;
        int reads = 0;
        var window = Hidden(new AssistantWorkWindow(_ => { reads++; return Task.FromResult(snapshot); }, id => id == current));
        try
        {
            window.Show(); await Idle(window);
            int initialReads = reads;
            var ids = new List<string>();
            do
            {
                ids.AddRange(window.CommitmentsGrid.Items.Cast<AssistantWorkspaceRow>().Select(row => row.Id));
                if (!window.NextCommitmentsButton.IsEnabled) break;
                window.NextCommitmentsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            } while (true);
            check("commitment_browse_reaches_all_205_without_duplicate_or_foreign_rows", ids.Count == 205 && ids.Distinct().Count() == 205
                && ids[^1] == items[0].Id.ToString("N") && window.PreviousCommitmentsButton.IsEnabled
                && window.CommitmentCountText.Text.Contains("201–205"));
            window.PreviousCommitmentsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("commitment_previous_page_returns_the_same_read_order", window.CommitmentsGrid.Items.Cast<AssistantWorkspaceRow>()
                .Select(row => row.Id).SequenceEqual(ids.Skip(150).Take(50)));
            window.CommitmentSearchBox.Text = "查找旧安排";
            window.SearchCommitmentsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.CommitmentsGrid.SelectedIndex = 0;
            check("commitment_search_matches_full_old_goal_beyond_preview_and_opens_details", window.CommitmentsGrid.Items.Count == 1
                && ((AssistantWorkspaceRow)window.CommitmentsGrid.Items[0]).Id == items[0].Id.ToString("N")
                && window.CommitmentDetailBox.Text.Contains(items[0].Goal) && !window.NextCommitmentsButton.IsEnabled);
            window.CommitmentSearchBox.Text = items[17].Id.ToString("D");
            window.SearchCommitmentsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("commitment_search_accepts_full_dashed_id_and_clears_previous_detail", window.CommitmentsGrid.Items.Count == 1
                && ((AssistantWorkspaceRow)window.CommitmentsGrid.Items[0]).Id == items[17].Id.ToString("N")
                && !window.CommitmentDetailBox.Text.Contains(items[0].Goal));
            window.CommitmentSearchBox.Text = "不存在的安排";
            window.SearchCommitmentsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("commitment_empty_search_result_has_no_old_selection", window.CommitmentsGrid.Items.Count == 0
                && window.CommitmentTechnicalBox.Text.Length == 0 && !window.NextCommitmentsButton.IsEnabled);
            window.CommitmentSearchBox.Clear(); window.SearchCommitmentsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("commitment_clear_search_restores_first_page_without_rereading_or_work", window.CommitmentsGrid.Items.Count == 50
                && !window.PreviousCommitmentsButton.IsEnabled && reads == initialReads);
            await Idle(window); Capture(window, Path.Combine(directory, "commitment-search-default.png"));
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            await Idle(window);
            check("commitment_search_and_paging_fit_minimum_window", Inside(window.CommitmentSearchBox, window)
                && Inside(window.NextCommitmentsButton, window) && window.CommitmentsGrid.ActualHeight >= 45
                && window.CommitmentDetailScroll.ViewportHeight >= 45);
            Capture(window, Path.Combine(directory, "commitment-search-minimum.png"));
            current = foreign.Identity; window.InvalidateIfIdentityChanged();
            window.SearchCommitmentsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("commitment_binding_change_discards_search_archive", window.CommitmentsGrid.Items.Count == 0
                && !window.SearchCommitmentsButton.IsEnabled && window.CommitmentSearchBox.Text.Length == 0);
        }
        finally { window.Close(); }
    }
    private static AssistantWorkWindow Hidden(AssistantWorkWindow window)
    {
        window.ShowInTaskbar = false; window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -20000; window.Top = -20000;
        return window;
    }
    private static async Task Idle(Window window)
    { window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); window.UpdateLayout(); }
    private static bool Inside(FrameworkElement element, Window window)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        Rect bounds = element.TransformToAncestor(window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1;
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }
}
