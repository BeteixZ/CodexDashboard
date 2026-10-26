using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Assistant.Core;

namespace CodexDashboard;

// View-only synthetic snapshots: no ledger, model, channel, task transport or production settings.
internal static class OperationDetailWindowTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--operation-detail-window-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--operation-detail-window-test") throw new ArgumentException("new_output_required");
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
            File.WriteAllText(Path.Combine(directory, "operation-detail-window-checks.json"), JsonSerializer.Serialize(new
            { count = checks.Count, failures = checks.Count(x => !x.Value), productionDataRead = false, networkUsed = false, actualModelUsed = false, checks },
                new JsonSerializerOptions { WriteIndented = true }));
            if (!value) throw new InvalidOperationException(name);
        }
        var a = new ConversationIdentity("PRIVATE_DETAIL_USER_A", "PRIVATE_DETAIL_BINDING_A", "main");
        var b = new ConversationIdentity("PRIVATE_DETAIL_USER_B", "PRIVATE_DETAIL_BINDING_B", "main");
        var id = Guid.Parse("1fb01063953449e6973f51b310359c01");
        var other = Guid.Parse("1fb01063953449e6973f51b310359c02");
        string body = "操作：补充运行中的任务\n当前保存的状态：结果未知，不会自动重试\n触发这项操作的原始消息：\n" + string.Join("\n", Enumerable.Repeat("检查合成测试结果，保留尚未验证的部分，不把接口受理说成手机已经收到。", 24))
            + "\n\n最近处理记录：指令已受理，尚未核实生效。\n发送记录：接口已受理，手机送达未知。";
        const string secondBody = "另一项完整操作。\n尚未开始，也没有发送记录。";
        AssistantWorkspaceSnapshot Snapshot(ConversationIdentity identity, string text) => new(
            "合成能力说明。", "当前交流目标：合成任务。",
            "当前没有已登记的运行错误。\n本页按当前绑定读取，操作可搜索、翻页；完整记录仍保留。结果未知的事项不会自动重试。"
                + "\n提醒只在助手运行时检查；电脑关机或助手退出期间不会发送。微信接口受理不代表手机已收到。", DateTimeOffset.UtcNow,
            [],
            [new(id.ToString("N"), "一次性提醒", "待核对发送记录", "截短的列表预览", "10-21 06:00:00"),
             new(other.ToString("N"), "发送指令", "等待处理", "另一项列表预览", "10-21 06:00:00")], [],
            [new(other.ToString("N"), "一次性提醒", "等待触发", "已有事项列表预览", "10-21 06:00:00")],
            [new("synthetic-conversation", "你", "已持久接收", "原对话完整正文。\n保留这一行。", "合成任务", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "接收时间")],
            identity, 1, [new(identity, other, 1, "已有事项完整正文。")],
            [new(identity, id, text), new(identity, other, secondBody)]);
        ConversationIdentity current = a;
        var snapshot = Snapshot(a, body); int reads = 0;
        var window = Hidden(new AssistantWorkWindow(_ => { reads++; return Task.FromResult(snapshot); }, value => value == current));
        try
        {
            Check("details_start_empty_and_all_detail_controls_are_readonly", window.OperationDetailBox.IsReadOnly
                && window.OperationTechnicalBox.IsReadOnly && window.OperationsGrid.IsReadOnly
                && window.OperationTechnicalBox.Text.Length == 0 && !window.OperationTechnicalExpander.IsExpanded && reads == 0);
            window.Show(); await Idle(window); await window.RefreshAsync(); window.OperationsTab.IsSelected = true; await Idle(window);
            Check("refresh_loads_list_without_automatically_revealing_operation", window.OperationsGrid.Items.Count == 2
                && window.OperationDetailBox.Text != body && window.OperationTechnicalBox.Text.Length == 0);
            int before = reads; window.OperationsGrid.SelectedIndex = 0;
            Check("selection_shows_entire_snapshot_operation_original_request_and_actual_result_without_any_new_read", reads == before
                && window.OperationDetailBox.Text == body && window.OperationDetailBox.Text.Length > 180
                && window.OperationDetailBox.Text.Contains("手机送达未知"));
            Check("technical_operation_identifier_is_collapsed_and_removed_from_primary_columns",
                !window.OperationTechnicalExpander.IsExpanded && window.OperationTechnicalBox.Text.Contains(id.ToString("N"))
                && !window.OperationTechnicalBox.Text.Contains("PRIVATE_")
                && !window.OperationsGrid.Columns.Any(column => column.Header?.ToString() == "完整编号"));
            Capture(window, Path.Combine(directory, "operation-details.png"));
            window.Width = window.MinWidth; window.Height = window.MinHeight; await Idle(window);
            Check("minimum_window_keeps_list_scrollable_body_and_fixed_refresh_reachable",
                window.OperationsGrid.ActualHeight >= 60 && window.OperationDetailScroll.ViewportHeight >= 60
                && Inside(window.RefreshButton, window) && Inside(window.OperationDetailScroll, window));
            window.OperationDetailScroll.ScrollToEnd(); await Idle(window);
            Check("long_full_operation_is_scrollable_at_minimum_size_without_changing_content",
                window.OperationDetailScroll.ScrollableHeight > 0 && window.OperationDetailScroll.VerticalOffset > 0
                && window.OperationDetailBox.Text == body && Inside(window.OperationTechnicalExpander, window));
            Capture(window, Path.Combine(directory, "operation-details-minimum.png"));
            window.OperationTechnicalExpander.IsExpanded = true;
            await Task.Delay(350); await Idle(window); window.OperationDetailScroll.ScrollToEnd(); await Idle(window);
            Check("expanded_identifier_is_visible_at_bottom_without_losing_full_body", Inside(window.OperationTechnicalBox, window)
                && window.OperationDetailBox.Text == body && window.OperationTechnicalBox.Text.Contains(id.ToString("N")));
            Capture(window, Path.Combine(directory, "operation-details-expanded.png"));
            window.OperationsGrid.SelectedIndex = 1;
            Check("selecting_another_item_clears_old_body_and_collapses_technical_panel",
                window.OperationDetailBox.Text == secondBody && !window.OperationTechnicalExpander.IsExpanded
                && !window.OperationTechnicalBox.Text.Contains(id.ToString("N")));
            window.ConversationTab.IsSelected = true; window.ConversationGrid.SelectedIndex = 0;
            Check("existing_conversation_page_keeps_full_original_body_and_readonly_controls",
                window.WorkTabs.Items.Count == 7 && window.ConversationGrid.IsReadOnly && window.ConversationDetailBox.IsReadOnly
                && window.ConversationDetailBox.Text.StartsWith("原对话完整正文。\n保留这一行。", StringComparison.Ordinal));
            window.WorkTabs.SelectedIndex = 0; window.CommitmentsGrid.SelectedIndex = 0;
            Check("existing_commitment_detail_and_all_seven_tabs_remain_available", window.CommitmentDetailBox.Text == "已有事项完整正文。"
                && window.WorkTabs.Items.Count == 7 && window.CommitmentTechnicalBox.Text.Contains(other.ToString("N")));
            window.OperationsTab.IsSelected = true;
            snapshot = snapshot with { OperationDetails = [new(a, id, body), new(a, id, "PRIVATE_DUPLICATE_DETAIL")] };
            await window.RefreshAsync(); window.OperationsGrid.SelectedIndex = 0;
            Check("duplicate_detail_ids_do_not_choose_an_arbitrary_body", window.OperationTechnicalBox.Text.Length == 0
                && !window.OperationDetailBox.Text.Contains("PRIVATE_") && window.OperationDetailBox.Text != body);
            snapshot = Snapshot(a, body) with { OperationDetails = [new(b, id, "PRIVATE_OTHER_IDENTITY_DETAIL")] };
            await window.RefreshAsync(); window.OperationsGrid.SelectedIndex = 0;
            Check("same_id_detail_from_another_identity_is_never_displayed", !window.OperationDetailBox.Text.Contains("PRIVATE_")
                && window.OperationTechnicalBox.Text.Length == 0);
            snapshot = Snapshot(a, body) with { Operations = [new(id.ToString("N")[..8], "提醒", "等待", "预览", "现在")] };
            await window.RefreshAsync(); window.OperationsGrid.SelectedIndex = 0;
            Check("abbreviated_row_id_cannot_resolve_full_item_by_guessing", window.OperationDetailBox.Text != body
                && window.OperationTechnicalBox.Text.Length == 0);
            snapshot = Snapshot(a, body) with { OperationDetails = null };
            await window.RefreshAsync(); window.OperationsGrid.SelectedIndex = 0;
            Check("old_snapshot_without_details_does_not_expand_truncated_preview_as_full_operation", window.OperationDetailBox.Text != "截短的列表预览"
                && window.OperationDetailBox.Text.Contains("没有可核对") && window.OperationTechnicalBox.Text.Length == 0);
            var many = Enumerable.Range(0, 205).Select(i => new AssistantWorkspaceRow(Guid.NewGuid().ToString("N"),
                "合成操作", "结果未知", "合成目标 " + i, "现在")).ToArray();
            snapshot = Snapshot(a, body) with { Operations = many,
                OperationDetails = many.Select((row, i) => new AssistantOperationDetail(a, Guid.Parse(row.Id),
                    i == 204 ? "完整原话末尾：EARLIEST_OPERATION_NEEDLE" : "普通详情 " + i)).ToArray() };
            await window.RefreshAsync();
            Check("operation_history_pages_all_205_records", window.OperationsGrid.Items.Count == 50
                && window.OperationCountText.Text.Contains("205") && window.NextOperationsButton.IsEnabled);
            window.ShowOperationPage("", 4);
            Check("last_operation_page_reaches_records_older_than_200", window.OperationsGrid.Items.Count == 5
                && !window.NextOperationsButton.IsEnabled && window.PreviousOperationsButton.IsEnabled);
            window.ShowOperationPage("earliest_operation_needle", 0); window.OperationsGrid.SelectedIndex = 0;
            Check("operation_search_matches_full_detail_beyond_preview", window.OperationsGrid.Items.Count == 1
                && window.OperationDetailBox.Text.Contains("EARLIEST_OPERATION_NEEDLE"));
            window.ShowOperationPage(many[204].Id, 0);
            Check("operation_search_accepts_complete_id_and_clears_previous_detail", window.OperationsGrid.Items.Count == 1
                && window.OperationTechnicalBox.Text.Length == 0);
            window.ShowOperationPage("no_such_operation", 0);
            Check("operation_search_empty_result_disables_paging", window.OperationsGrid.Items.Count == 0
                && !window.NextOperationsButton.IsEnabled && !window.PreviousOperationsButton.IsEnabled);
            snapshot = Snapshot(a, body); await window.RefreshAsync(); window.OperationsGrid.SelectedIndex = 0;
            snapshot = snapshot with { Messages = many, MessageDetails = many.Select((row, i) =>
                new AssistantMessageDetail(a, row.Id, row.Kind, i == 204 ? "末尾完整正文 MESSAGE_BODY_NEEDLE" : "合成发送正文 " + i)).ToArray() };
            await window.RefreshAsync(); window.MessagesTab.IsSelected = true;
            Check("message_history_pages_all_records", window.MessagesGrid.Items.Count == 50 && window.NextMessagesButton.IsEnabled);
            window.ShowMessagePage("", 4);
            Check("message_last_page_preserves_older_records", window.MessagesGrid.Items.Count == 5 && !window.NextMessagesButton.IsEnabled);
            window.ShowMessagePage("message_body_needle", 0); window.MessagesGrid.SelectedIndex = 0;
            Check("message_full_body_search_and_read_are_not_limited_to_preview", window.MessagesGrid.Items.Count == 1
                && window.MessageDetailBox.Text == "末尾完整正文 MESSAGE_BODY_NEEDLE" && window.MessageDetailBox.IsReadOnly);
            await Idle(window); Capture(window, Path.Combine(directory, "message-search-minimum.png"));
            window.ShowMessagePage(many[204].Id, 0);
            Check("message_id_search_clears_prior_body", window.MessagesGrid.Items.Count == 1 && window.MessageDetailBox.Text == "");
            window.MessagesGrid.SelectedIndex = 0;
            current = b; window.InvalidateIfIdentityChanged();
            Check("identity_change_clears_message_query_body_and_rows", window.MessageDetailBox.Text == ""
                && window.MessagesGrid.Items.Count == 0 && window.MessageSearchBox.Text == "" && window.MessageCountText.Text == "");
            Check("binding_change_clears_full_operation_metadata_all_rows_and_original_history",
                window.OperationDetailBox.Text != body && window.OperationTechnicalBox.Text.Length == 0
                && window.OperationsGrid.Items.Count == 0 && window.ConversationGrid.Items.Count == 0
                && window.ConversationDetailBox.Text.Length == 0 && window.ReadAtText.Text.Length == 0);
            snapshot = Snapshot(b, "当前身份的新操作。"); await window.RefreshAsync(); window.OperationsGrid.SelectedIndex = 0;
            Check("explicit_refresh_only_reveals_new_identity_current_snapshot", window.OperationDetailBox.Text == "当前身份的新操作。"
                && window.OperationTechnicalBox.Text.Contains(id.ToString("N")) && !window.OperationDetailBox.Text.Contains(body));
            window.Close();
            Check("closing_window_clears_previous_body_and_metadata", window.OperationTechnicalBox.Text.Length == 0
                && window.OperationsGrid.Items.Count == 0 && window.ConversationGrid.Items.Count == 0
                && window.OperationDetailBox.Text != "当前身份的新操作。");
        }
        finally { window.Close(); }

        current = a; var initial = Snapshot(a, body);
        var delayed = new TaskCompletionSource<AssistantWorkspaceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool wait = false;
        var slow = new AssistantWorkWindow(_ => wait ? delayed.Task : Task.FromResult(initial), value => value == current);
        try
        {
            await slow.RefreshAsync(); slow.OperationsGrid.SelectedIndex = 0; wait = true;
            Task loading = slow.RefreshAsync();
            Check("refresh_immediately_clears_selected_operation_before_delayed_read_finishes",
                slow.OperationDetailBox.Text != body && slow.OperationTechnicalBox.Text.Length == 0 && !slow.RefreshButton.IsEnabled);
            current = b; slow.InvalidateIfIdentityChanged(); current = a;
            delayed.SetResult(initial); await loading;
            Check("identity_a_b_a_late_old_snapshot_cannot_repopulate_detail_or_list", slow.OperationsGrid.Items.Count == 0
                && slow.OperationDetailBox.Text != body && slow.OperationTechnicalBox.Text.Length == 0 && slow.RefreshButton.IsEnabled);
        }
        finally { slow.Close(); }
        bool fail = false; current = a;
        var failed = new AssistantWorkWindow(_ => fail
            ? Task.FromException<AssistantWorkspaceSnapshot>(new IOException("PRIVATE_RAW_REASON_APPROVAL_BODY"))
            : Task.FromResult(initial), value => value == current);
        try
        {
            await failed.RefreshAsync(); failed.OperationsGrid.SelectedIndex = 0; fail = true; await failed.RefreshAsync();
            Check("failed_read_clears_old_detail_without_disclosing_raw_exception",
                failed.OperationDetailBox.Text != body && failed.OperationTechnicalBox.Text.Length == 0
                && !failed.ContextText.Text.Contains("PRIVATE_") && !failed.DiagnosticText.Text.Contains("PRIVATE_") && failed.RefreshButton.IsEnabled);
        }
        finally { failed.Close(); }
        var closingRead = new TaskCompletionSource<AssistantWorkspaceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new AssistantWorkWindow(_ => closingRead.Task, value => value == current);
        Task pending = closed.RefreshAsync(); closed.Close(); closingRead.SetResult(initial); await pending;
        Check("closed_window_rejects_late_snapshot_completion", closed.OperationsGrid.Items.Count == 0
            && closed.OperationDetailBox.Text != body && closed.OperationTechnicalBox.Text.Length == 0);
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
