using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Assistant.Core;

namespace CodexDashboard;

// Actual WPF controls with synthetic immutable pages. No storage, credentials,
// transport, model or production settings are opened by this suite.
internal static class HistoryPaginationWindowTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (!args.Contains("--history-pagination-window-test")) return false;
        try
        {
            if (args.Length != 2 || args[0] != "--history-pagination-window-test") throw new ArgumentException("new_output_required");
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
            File.WriteAllText(Path.Combine(directory, "history-pagination-window-checks.json"), JsonSerializer.Serialize(new
            { count = checks.Count, failures = checks.Count(item => !item.Value), productionDataRead = false, networkUsed = false, actualModelUsed = false, checks },
                new JsonSerializerOptions { WriteIndented = true }));
            if (!value) throw new InvalidOperationException(name);
        }

        var a = new ConversationIdentity("PRIVATE_HISTORY_USER_A", "PRIVATE_HISTORY_BINDING_A", "main");
        var b = new ConversationIdentity("PRIVATE_HISTORY_USER_B", "PRIVATE_HISTORY_BINDING_B", "main");
        var time = new DateTimeOffset(2026, 10, 21, 7, 0, 0, TimeSpan.FromHours(8));
        string longBody = "第一行完整原文\n" + new string('文', 900) + "\n最后一行 😀，不可从预览重新拼接。";
        AssistantConversationRow Row(int index, string? body = null) => new(index.ToString("x32"), index % 2 == 0 ? "你" : "助手回复",
            index % 2 == 0 ? "已持久接收" : "渠道接口已受理；手机送达未核实", body ?? "第 " + index + " 条完整正文。",
            "合成任务 / 原始来源", time.AddMinutes(index), time.AddMinutes(index).AddSeconds(1), index % 2 == 0 ? "接收时间" : "准备时间");
        var rows = Enumerable.Range(1, 6).Select(index => Row(index, index == 5 ? longBody : null)).ToArray();
        AssistantConversationPage Page(ConversationIdentity identity, int total, int start, DateTimeOffset readAt, params AssistantConversationRow[] selected)
            => new(identity, readAt, selected, total, selected.Length == 0 ? 0 : start,
                selected.Length == 0 ? 0 : start + selected.Length - 1,
                selected.Length > 0 && start + selected.Length - 1 < total
                    ? new(identity, selected[^1].CreatedAt, selected[^1].Id) : null);
        var first = Page(a, 5, 1, time.AddHours(1), rows[4], rows[3]);
        // One newly saved record shifts the positions on the next independent read.
        var middle = Page(a, 6, 4, time.AddHours(1).AddSeconds(1), rows[2], rows[1]);
        var last = Page(a, 6, 6, time.AddHours(1).AddSeconds(2), rows[0]);
        var latest = Page(a, 6, 1, time.AddHours(1).AddSeconds(3), rows[5], rows[4]);
        var itemId = Guid.Parse("18b21434219a42b995172632c9a22670");
        var operationId = Guid.Parse("18b21434219a42b995172632c9a22671");
        AssistantWorkspaceSnapshot Snapshot(ConversationIdentity identity, AssistantConversationPage? page) => new(
            "现有可用能力。", "当前绑定的只读工作记录。", "历史正文可能已经不再适用；不会自动重发未知结果。", time.AddHours(1),
            [new("pending", "只读检查", "待处理", "已有待处理行", "现在")],
            [new(operationId.ToString("N"), "明确操作", "结果未知", "已有操作列表", "现在")],
            [new("message", "回复", "接口已受理", "已有发送记录", "现在")],
            [new(itemId.ToString("N"), "提醒", "等待触发", "已有安排", "现在")], page?.Rows ?? [rows[4]], identity, page?.TotalCount ?? 1,
            [new(identity, itemId, 2, "保留的完整事项详情。")],
            [new(identity, operationId, "保留的完整操作详情，结果未知不重发。")], page);
        ConversationIdentity current = a;
        var snapshot = Snapshot(a, first); int workspaceReads = 0;
        var requests = new List<AssistantConversationCursor?>();
        Func<AssistantConversationCursor?, Task<AssistantConversationPage>> handler = cursor => Task.FromResult(cursor == null ? latest : last);
        var window = Hidden(new AssistantWorkWindow(_ => { workspaceReads++; return Task.FromResult(snapshot); },
            identity => ReferenceEquals(identity, current), (identity, cursor, _) =>
            {
                if (!ReferenceEquals(identity, current)) throw new InvalidOperationException("wrong_active_identity");
                requests.Add(cursor); return handler(cursor);
            }, (identity, _) => Task.FromResult(first with { Rows = rows, TotalCount = rows.Length, OlderCursor = null })));
        try
        {
            Check("before_first_read_page_controls_disabled_and_no_read_side_effect", !window.OlderConversationButton.IsEnabled
                && !window.LatestConversationButton.IsEnabled && workspaceReads == 0 && requests.Count == 0);
            window.Show(); await Idle(window); await window.RefreshAsync(); window.ConversationTab.IsSelected = true; await Idle(window);
            Check("initial_page_shows_only_its_rows_range_and_independent_read_notice", window.ConversationGrid.Items.Count == 2
                && window.ConversationCountText.Text.Contains("1–2") && window.ConversationCountText.Text.Contains("5")
                && window.ConversationReadAtText.Text.Contains("本页独立读取，不与上次读取拼接；最新记录在上")
                && window.OlderConversationButton.IsEnabled && window.LatestConversationButton.IsEnabled);
            window.ConversationGrid.SelectedIndex = 0;
            Check("selected_page_preserves_full_original_body_source_and_delivery_uncertainty", window.ConversationDetailBox.Text == rows[4].Detail
                && window.ConversationDetailBox.Text.StartsWith(longBody, StringComparison.Ordinal)
                && window.ConversationDetailBox.Text.Contains("手机送达未核实") && window.ConversationDetailBox.IsReadOnly);
            Capture(window, Path.Combine(directory, "history-page-default.png"));
            window.Width = window.MinWidth; window.Height = window.MinHeight; await Idle(window);
            Check("minimum_layout_keeps_both_page_buttons_and_scrollable_body_reachable", Inside(window.OlderConversationButton, window)
                && Inside(window.LatestConversationButton, window) && Inside(window.RefreshButton, window) && Inside(window.ExportConversationButton, window)
                && window.ConversationGrid.ActualHeight >= 45 && window.ConversationDetailBox.ActualHeight >= 45
                && window.ConversationDetailBox.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
            Capture(window, Path.Combine(directory, "history-page-minimum.png"));
            string exportPath = Path.Combine(directory, "all-history.json");
            Check("cancel_export_does_not_write", await window.ExportConversationAsync(() => null) == AssistantHealthExportResult.Cancelled
                && !File.Exists(exportPath));
            Check("desktop_exports_full_snapshot_not_visible_page", await window.ExportConversationAsync(() => exportPath) == AssistantHealthExportResult.Saved);
            using (var document = JsonDocument.Parse(File.ReadAllText(exportPath)))
                Check("export_keeps_original_body_and_state_without_binding_identity", document.RootElement.GetProperty("count").GetInt32() == rows.Length
                    && document.RootElement.GetProperty("messages")[4].GetProperty("Body").GetString() == rows[4].Body
                    && document.RootElement.GetProperty("messages")[4].GetProperty("State").GetString() == rows[4].State
                    && !document.RootElement.TryGetProperty("Identity", out _));
            var originalBytes = File.ReadAllBytes(exportPath);
            Check("existing_export_never_overwritten", await window.ExportConversationAsync(() => exportPath) == AssistantHealthExportResult.Failed
                && File.ReadAllBytes(exportPath).SequenceEqual(originalBytes));
            string stalePath = Path.Combine(directory, "stale-history.json");
            Check("binding_change_in_file_picker_cannot_export_old_history", await window.ExportConversationAsync(() => { current = b; return stalePath; })
                == AssistantHealthExportResult.SnapshotChanged && !File.Exists(stalePath));
            current = a;

            // These are the actual normal Runtime strings, including its optional
            // catch-up line, rather than a shorter layout-only substitute.
            const string normalDiagnostic = "当前没有已登记的运行错误。"
                + "\n本页按当前绑定读取，每类最多显示 200 条；对话页可继续读取更早记录。结果未知的事项不会自动重试。"
                + "\n提醒只在助手运行时检查；电脑关机或助手退出期间不会发送。微信接口受理不代表手机已收到。";
            const string catchUpDiagnostic = normalDiagnostic
                + "\n历史通知：2 条正文已完整受理，1 条仍有未确认部分；部分补报不会显示为全文完成。";
            snapshot = snapshot with { Context = "当前交流目标：local / assistant-owned-synthetic-thread / active-turn。", Diagnostic = normalDiagnostic };
            await window.RefreshAsync(); window.ConversationGrid.SelectedIndex = 0; await Idle(window);
            Check("real_three_line_runtime_diagnostic_and_focus_keep_minimum_page_usable", window.DiagnosticText.Text == normalDiagnostic
                && window.ConversationDiagnosticText.Text == normalDiagnostic && !window.ConversationDiagnostics.IsExpanded
                && Inside(window.ConversationDiagnostics, window) && Inside(window.OlderConversationButton, window)
                && Inside(window.LatestConversationButton, window) && window.ConversationGrid.ActualHeight >= 45
                && window.ConversationDetailBox.ActualHeight >= 45 && window.ConversationDetailBox.Text == rows[4].Detail);
            Capture(window, Path.Combine(directory, "history-real-diagnostic-minimum.png"));
            snapshot = snapshot with { Diagnostic = catchUpDiagnostic }; await window.RefreshAsync();
            window.ConversationGrid.SelectedIndex = 0; await Idle(window);
            Check("real_four_line_catchup_diagnostic_remains_complete_without_hiding_page_controls", window.ConversationDiagnosticText.Text == catchUpDiagnostic
                && window.ConversationGrid.ActualHeight >= 45 && window.ConversationDetailBox.ActualHeight >= 45
                && Inside(window.OlderConversationButton, window) && Inside(window.LatestConversationButton, window)
                && window.ConversationDetailBox.Text == rows[4].Detail);
            Capture(window, Path.Combine(directory, "history-catchup-diagnostic-minimum.png"));
            window.ConversationDiagnostics.IsExpanded = true; await Task.Delay(350); await Idle(window);
            File.WriteAllText(Path.Combine(directory, "expanded-diagnostic-layout.json"), JsonSerializer.Serialize(new
            {
                textExact = window.ConversationDiagnosticText.Text == catchUpDiagnostic,
                textVisible = window.ConversationDiagnosticText.IsVisible,
                expanded = window.ConversationDiagnostics.IsExpanded,
                explanationScrollableHeight = window.ConversationDiagnosticScroll.ScrollableHeight,
                explanationViewportHeight = window.ConversationDiagnosticScroll.ViewportHeight,
                explanationActualHeight = window.ConversationDiagnosticScroll.ActualHeight,
                expanderActualHeight = window.ConversationDiagnostics.ActualHeight,
                olderInside = Inside(window.OlderConversationButton, window), latestInside = Inside(window.LatestConversationButton, window),
                listHeight = window.ConversationGrid.ActualHeight, bodyHeight = window.ConversationDetailBox.ActualHeight,
                windowWidth = window.ActualWidth, windowHeight = window.ActualHeight
            }, new JsonSerializerOptions { WriteIndented = true }));
            Capture(window, Path.Combine(directory, "history-diagnostic-expanded-before-check.png"));
            Check("full_runtime_explanation_is_available_by_explicit_expansion", window.ConversationDiagnosticText.Text == catchUpDiagnostic
                && window.ConversationDiagnosticText.IsVisible && window.ConversationDiagnostics.IsExpanded
                && window.ConversationDiagnosticScroll.ScrollableHeight > 0 && Inside(window.OlderConversationButton, window)
                && Inside(window.LatestConversationButton, window) && window.ConversationGrid.ActualHeight >= 45
                && window.ConversationDetailBox.ActualHeight >= 45);
            Capture(window, Path.Combine(directory, "history-diagnostic-expanded-minimum.png"));
            window.WorkTabs.SelectedIndex = 0; await Idle(window);
            Check("other_tabs_keep_original_diagnostic_appearance", window.DiagnosticText.Visibility == Visibility.Visible
                && window.DiagnosticText.Text == catchUpDiagnostic && window.ConversationDiagnostics.Visibility == Visibility.Collapsed);
            window.ConversationTab.IsSelected = true; await Idle(window);

            var oldRows = window.ConversationGrid.ItemsSource; string otherReadAt = window.ReadAtText.Text;
            int originalWorkspaceReads = workspaceReads;
            var delayed = new TaskCompletionSource<AssistantConversationPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            handler = _ => delayed.Task;
            Task pending = window.ReadConversationPageAsync(first.OlderCursor);
            Check("page_request_immediately_removes_old_selection_and_body_and_disables_buttons", window.ConversationGrid.Items.Count == 0
                && window.ConversationGrid.SelectedItem == null && window.ConversationDetailBox.Text.Length == 0
                && !window.OlderConversationButton.IsEnabled && !window.LatestConversationButton.IsEnabled && requests.Count == 1);
            await window.ReadConversationPageAsync(first.OlderCursor);
            Check("concurrent_click_does_not_duplicate_page_read", requests.Count == 1);
            delayed.SetResult(middle); await pending;
            Check("older_page_replaces_instead_of_appending_and_uses_new_read_range", window.ConversationGrid.Items.Count == 2
                && window.ConversationGrid.Items.Cast<AssistantConversationRow>().Select(row => row.Id).SequenceEqual(new[] { rows[2].Id, rows[1].Id })
                && !ReferenceEquals(oldRows, window.ConversationGrid.ItemsSource) && window.ConversationCountText.Text.Contains("4–5")
                && window.ConversationReadAtText.Text.Contains(middle.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"))
                && workspaceReads == originalWorkspaceReads && window.ReadAtText.Text == otherReadAt);
            // A stale grid object with equal values is not an authorized selection.
            var copiedRow = rows[2] with { };
            window.ConversationGrid.ItemsSource = new[] { copiedRow }; window.ConversationGrid.SelectedIndex = 0;
            Check("value_equal_stale_selection_does_not_reveal_saved_body", !ReferenceEquals(copiedRow, rows[2])
                && window.ConversationDetailBox.Text != copiedRow.Detail);
            window.ConversationGrid.ItemsSource = middle.Rows;
            handler = _ => Task.FromResult(last);
            window.OlderConversationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(window);
            Check("last_page_uses_exact_last_cursor_and_disables_older", requests.Count == 2 && requests[^1] == middle.OlderCursor
                && window.ConversationGrid.Items.Count == 1 && ReferenceEquals(window.ConversationGrid.Items[0], rows[0])
                && window.ConversationCountText.Text.Contains("6–6") && !window.OlderConversationButton.IsEnabled && window.LatestConversationButton.IsEnabled);
            handler = _ => Task.FromResult(latest);
            window.LatestConversationButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle(window);
            Check("return_latest_reads_again_with_null_cursor_and_includes_new_record", requests.Count == 3 && requests[^1] == null
                && window.ConversationGrid.Items.Count == 2 && ReferenceEquals(window.ConversationGrid.Items[0], rows[5])
                && window.ConversationCountText.Text.Contains("1–2") && window.ConversationReadAtText.Text.Contains(latest.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")));
            window.WorkTabs.SelectedIndex = 0; window.CommitmentsGrid.SelectedIndex = 0;
            window.OperationsTab.IsSelected = true; window.OperationsGrid.SelectedIndex = 0;
            Check("paging_preserves_all_seven_tabs_other_snapshot_rows_and_both_detail_views", window.WorkTabs.Items.Count == 7
                && window.WorkGrid.Items.Count == 1 && window.MessagesGrid.Items.Count == 1
                && window.CommitmentDetailBox.Text == "保留的完整事项详情。"
                && window.OperationDetailBox.Text == "保留的完整操作详情，结果未知不重发。" && window.ReadAtText.Text == otherReadAt);
            window.ConversationTab.IsSelected = true;

            handler = _ => Task.FromException<AssistantConversationPage>(new IOException("PRIVATE_RAW_ERROR"));
            await window.ReadConversationPageAsync(latest.OlderCursor);
            Check("failed_page_clears_body_only_and_offers_explicit_latest_retry", window.ConversationGrid.Items.Count == 0
                && window.ConversationDetailBox.Text.Length == 0 && !window.ConversationCountText.Text.Contains("PRIVATE_")
                && window.ConversationCountText.Text.Contains("返回最近") && window.LatestConversationButton.IsEnabled
                && !window.OlderConversationButton.IsEnabled && window.OperationsGrid.Items.Count == 1);
            handler = _ => Task.FromResult(latest with { Identity = b });
            await window.ReadConversationPageAsync(null);
            Check("foreign_identity_page_is_rejected_without_old_or_foreign_body", window.ConversationGrid.Items.Count == 0
                && window.ConversationDetailBox.Text.Length == 0 && window.LatestConversationButton.IsEnabled);
            handler = _ => Task.FromResult(Page(a, 2, 1, time, rows[4], rows[4]));
            await window.ReadConversationPageAsync(null);
            Check("duplicate_row_id_page_is_rejected", window.ConversationGrid.Items.Count == 0 && window.ConversationDetailBox.Text.Length == 0);
            handler = _ => Task.FromResult(Page(a, 2, 1, time, rows[3], rows[4]));
            await window.ReadConversationPageAsync(null);
            Check("out_of_order_page_is_rejected", window.ConversationGrid.Items.Count == 0 && window.ConversationDetailBox.Text.Length == 0);
            handler = _ => Task.FromResult(first with { OlderCursor = first.OlderCursor! with { BeforeId = rows[0].Id } });
            await window.ReadConversationPageAsync(null);
            Check("cursor_not_bound_to_page_tail_is_rejected", window.ConversationGrid.Items.Count == 0 && window.ConversationDetailBox.Text.Length == 0);

            handler = _ => Task.FromResult(latest); await window.ReadConversationPageAsync(null);
            delayed = new TaskCompletionSource<AssistantConversationPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            handler = _ => delayed.Task; pending = window.ReadConversationPageAsync(latest.OlderCursor);
            current = b; window.InvalidateIfIdentityChanged(); current = a;
            delayed.SetResult(middle); await pending;
            Check("a_b_a_identity_change_does_not_restore_inflight_page_or_buttons", window.ConversationGrid.Items.Count == 0
                && window.ConversationDetailBox.Text.Length == 0 && !window.OlderConversationButton.IsEnabled && !window.LatestConversationButton.IsEnabled
                && window.OperationsGrid.Items.Count == 0);
            await window.RefreshAsync();
            Check("explicit_workspace_refresh_restores_current_identity_page", window.ConversationGrid.Items.Count == 2
                && window.LatestConversationButton.IsEnabled && window.OperationsGrid.Items.Count == 1);

            delayed = new TaskCompletionSource<AssistantConversationPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            handler = _ => delayed.Task; pending = window.ReadConversationPageAsync(first.OlderCursor);
            snapshot = Snapshot(a, latest); await window.RefreshAsync();
            Check("global_refresh_detaches_old_request_and_immediately_enables_new_page", window.ConversationGrid.Items.Count == 2
                && ReferenceEquals(window.ConversationGrid.Items[0], rows[5]) && window.LatestConversationButton.IsEnabled);
            delayed.SetResult(middle); await pending;
            Check("late_page_cannot_replace_newer_workspace_snapshot", window.ConversationGrid.Items.Count == 2
                && ReferenceEquals(window.ConversationGrid.Items[0], rows[5]) && window.ConversationCountText.Text.Contains("1–2"));

            snapshot = Snapshot(a, null); await window.RefreshAsync(); window.ConversationGrid.SelectedIndex = 0;
            Check("legacy_snapshot_keeps_full_conversation_without_guessing_an_older_cursor", window.ConversationGrid.Items.Count == 1
                && window.ConversationDetailBox.Text == rows[4].Detail && !window.OlderConversationButton.IsEnabled && window.WorkTabs.Items.Count == 7);

            snapshot = Snapshot(a, first); await window.RefreshAsync();
            delayed = new TaskCompletionSource<AssistantConversationPage>(TaskCreationOptions.RunContinuationsAsynchronously);
            handler = _ => delayed.Task; pending = window.ReadConversationPageAsync(first.OlderCursor);
            window.Close(); delayed.SetResult(middle); await pending;
            Check("closed_window_rejects_late_page_and_clears_all_body_and_buttons", window.ConversationGrid.Items.Count == 0
                && window.ConversationDetailBox.Text.Length == 0 && !window.OlderConversationButton.IsEnabled && !window.LatestConversationButton.IsEnabled
                && window.OperationsGrid.Items.Count == 0);
        }
        finally { window.Close(); }

        current = a;
        var searchQueries = new List<string>();
        var searchable = Hidden(new AssistantWorkWindow(_ => Task.FromResult(Snapshot(a, first)), identity => ReferenceEquals(identity, current),
            (_, _, _) => Task.FromResult(first), searchConversation: (_, query, cursor, _) =>
            {
                searchQueries.Add(query); var value = cursor == null ? first : middle;
                return Task.FromResult(value with { Query = query, OlderCursor = value.OlderCursor is { } next ? next with { Query = query } : null });
            }));
        try
        {
            searchable.Show(); await Idle(searchable); await searchable.RefreshAsync(); searchable.ConversationTab.IsSelected = true;
            searchable.ConversationSearchBox.Text = "  完整正文  "; await searchable.SearchConversationAsync();
            Check("desktop_search_uses_explicit_full_history_query", searchQueries.SequenceEqual(new[] { "完整正文" })
                && searchable.ConversationCountText.Text.StartsWith("搜索结果", StringComparison.Ordinal));
            await searchable.ReadConversationPageAsync(first.OlderCursor! with { Query = "完整正文" });
            Check("older_search_page_preserves_query", searchQueries.Count == 2 && searchQueries[1] == "完整正文"
                && searchable.ConversationCountText.Text.Contains("4–5", StringComparison.Ordinal));
            searchable.Width = searchable.MinWidth; searchable.Height = searchable.MinHeight; await Idle(searchable);
            Check("search_controls_reachable_at_minimum_size", Inside(searchable.SearchConversationButton, searchable)
                && Inside(searchable.ConversationSearchBox, searchable) && searchable.ConversationDetailBox.ActualHeight >= 45);
            Capture(searchable, Path.Combine(directory, "history-search-minimum.png"));
            searchable.ConversationSearchBox.Clear(); await searchable.SearchConversationAsync();
            Check("empty_query_returns_unfiltered_history", !searchable.ConversationCountText.Text.StartsWith("搜索结果", StringComparison.Ordinal)
                && searchQueries.Count == 2);
            searchable.ConversationSearchBox.Text = "旧查询"; current = b; searchable.InvalidateIfIdentityChanged();
            Check("binding_change_clears_query_and_results", searchable.ConversationSearchBox.Text.Length == 0
                && searchable.ConversationGrid.Items.Count == 0 && !searchable.SearchConversationButton.IsEnabled);
        }
        finally { searchable.Close(); }

        var emptyPage = new AssistantConversationPage(a, time, [], 0, 0, 0, null);
        var empty = new AssistantWorkWindow(_ => Task.FromResult(Snapshot(a, emptyPage)), identity => ReferenceEquals(identity, a),
            (_, _, _) => Task.FromResult(emptyPage));
        try
        {
            await empty.RefreshAsync();
            Check("empty_page_has_no_fake_range_or_older_cursor_but_allows_new_latest_read", empty.ConversationGrid.Items.Count == 0
                && empty.ConversationCountText.Text.Contains("没有") && !empty.OlderConversationButton.IsEnabled && empty.LatestConversationButton.IsEnabled);
        }
        finally { empty.Close(); }
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
