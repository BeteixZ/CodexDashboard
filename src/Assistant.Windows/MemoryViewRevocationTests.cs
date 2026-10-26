using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core;

namespace CodexDashboard;

internal static class MemoryViewRevocationTests
{
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args.Length != 2 || args[0] != "--memory-view-revocation-test") return false;
        string directory = args[1];
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory)) throw new ArgumentException("fresh_isolated_directory_required");
        Directory.CreateDirectory(directory); var checks = new Dictionary<string, bool>();
        async Task Check(string name, Func<Fixture, Task<bool>> action)
        {
            using var f = new Fixture(Path.Combine(directory, name));
            try { checks.Add(name, await action(f)); }
            catch (Exception e) { checks.Add(name, false); File.WriteAllText(Path.Combine(f.Directory, "failure.txt"), e.ToString()); }
            File.WriteAllText(Path.Combine(directory, "view-revocation-checks.json"), JsonSerializer.Serialize(checks, Settings.Json));
        }
        await Check("memory_search_matches_full_text_id_and_source_without_writes", f =>
        {
            f.Memory.AddFromEditor("Another entry with a distant search suffix NEEDLE");
            var before = f.Memory.CaptureEditorSnapshot();
            var window = f.Track(new AssistantMemoryWindow(f.Assistant, f.Settings));
            window.MemorySearchBox.Text = "needle"; window.SearchMemories();
            bool text = window.MemoryList.Items.Count == 1 && window.MemoryCountText.Text.Contains("1 / 2");
            window.MemorySearchBox.Text = f.Entry.Id; window.SearchMemories();
            bool id = window.MemoryList.Items.Count == 1 && ((AssistantMemoryEntry)window.MemoryList.Items[0]).Id == f.Entry.Id;
            window.MemorySearchBox.Text = f.Entry.SourceSummary; window.SearchMemories();
            bool source = window.MemoryList.Items.Cast<AssistantMemoryEntry>().Any(e => e.Id == f.Entry.Id);
            window.MemorySearchBox.Clear(); window.SearchMemories();
            return Task.FromResult(text && id && source && window.MemoryList.Items.Count == 2 && f.Memory.IsEditorSnapshotCurrent(before));
        });
        await Check("search_preserves_unsaved_edit_and_does_not_sign_external_update", f =>
        {
            var window = f.Track(new AssistantMemoryWindow(f.Assistant, f.Settings));
            window.MemoryList.SelectedIndex = 0; window.MemoryText.Text = "未保存的草稿";
            window.MemorySearchBox.Text = "no-result"; window.SearchMemories();
            bool kept = window.MemoryText.Text == "未保存的草稿" && window.MemoryList.SelectedItem != null
                && window.MemoryStatus.Text.Contains("尚未保存");
            f.Memory.AddFromEditor("新增外部内容");
            window.MemoryText.Text = f.Entry.Text; window.SearchMemories();
            return Task.FromResult(kept && window.MemoryList.Items.Count == 0 && window.MemoryText.Text == ""
                && f.Memory.Entries.Count == 2);
        });
        await Check("filtered_memory_revocation_removes_body_and_cannot_reappear", f =>
        {
            var window = f.Track(new AssistantMemoryWindow(f.Assistant, f.Settings));
            window.MemorySearchBox.Text = Fixture.Secret; window.SearchMemories(); window.MemoryList.SelectedIndex = 0;
            f.Memory.Delete(f.Entry.Id);
            window.MemorySearchBox.Clear(); window.SearchMemories();
            return Task.FromResult(window.MemoryList.Items.Count == 0 && window.MemoryText.Text == "");
        });
        await Check("deleted_selected_memory_disappears_from_open_list_and_body", f =>
        {
            var window = f.Track(new AssistantMemoryWindow(f.Assistant, f.Settings));
            window.MemoryTab.IsSelected = true; window.MemoryList.SelectedItem = window.MemoryList.Items.Cast<AssistantMemoryEntry>().Single(e => e.Id == f.Entry.Id);
            f.Memory.Delete(f.Entry.Id);
            return Task.FromResult(window.MemoryText.Text == "" && window.MemoryList.Items.Cast<AssistantMemoryEntry>().All(e => e.Id != f.Entry.Id));
        });
        await Check("unrelated_memory_change_preserves_unsaved_main_editor_draft", f =>
        {
            var window = f.Track(new AssistantMemoryWindow(f.Assistant, f.Settings));
            window.MemoryTab.IsSelected = true; window.MemoryList.SelectedItem = window.MemoryList.Items.Cast<AssistantMemoryEntry>().Single(e => e.Id == f.Entry.Id);
            window.MemoryText.Text = "独立的手工草稿"; f.Memory.AddFromEditor("另一条新增记忆");
            return Task.FromResult(window.MemoryText.Text == "独立的手工草稿" && window.MemoryStatus.Text.Contains("变化"));
        });
        await Check("personal_summary_source_delete_purges_open_source_and_draft", f =>
        {
            var second = f.Memory.AddFromEditor("第二条有效来源");
            var window = f.Track(new SummaryMemoryEditorWindow(f.Memory));
            foreach (ListBoxItem row in window.SourceList.Items) row.IsSelected = true;
            window.BodyBox.Text = "派生草稿包含 " + Fixture.Secret; window.ReviewedBox.IsChecked = true;
            f.Memory.Delete(f.Entry.Id);
            return Task.FromResult(window.BodyBox.Text == "" && !window.SaveButton.IsEnabled
                && window.SourceList.Items.Cast<ListBoxItem>().All(row => ((AssistantMemoryEntry)row.Tag).Id != f.Entry.Id));
        });
        await Check("episode_source_delete_purges_original_draft_and_source_list", f =>
        {
            var window = f.Episode(); f.Select(window, f.Source.Id); window.Body.Text = "尚未保存的 " + Fixture.Secret; window.Reviewed.IsChecked = true;
            f.Memory.Delete(f.Entry.Id);
            return Task.FromResult(window.Body.Text == "" && window.Original.Text == "" && !window.Save.IsEnabled
                && window.Sources.Items.Cast<EpisodeMemoryEditorWindow.SourceRow>().All(row => row.Event.Id != f.Source.Id));
        });
        await Check("unselected_revoked_source_is_removed_without_erasing_unrelated_draft", f =>
        {
            var window = f.Episode(); f.Select(window, f.Report.Id); window.Body.Text = "独立报告草稿";
            f.Memory.Delete(f.Entry.Id);
            return Task.FromResult(window.Body.Text == "独立报告草稿" && window.Original.Text.Contains(f.Report.Body)
                && window.Sources.Items.Cast<EpisodeMemoryEditorWindow.SourceRow>().All(row => row.Event.Id != f.Source.Id));
        });
        await Check("source_revoked_while_generating_cannot_restore_cleared_draft", async f =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<EpisodeSummaryDraft>(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = f.Episode((_, _, _) => { entered.TrySetResult(); return release.Task; });
            f.Select(window, f.Source.Id); window.Body.Text = Fixture.Secret;
            var generation = window.TryGenerateDraftAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Memory.Delete(f.Entry.Id); release.SetResult(new(Fixture.Secret, [EpisodeEventReference.From(f.Source)]));
            return !await generation && window.Body.Text == "" && window.Original.Text == "" && window.SavedEntry == null;
        });
        await Check("deleting_open_episode_summary_clears_its_body_without_deleting_originals", f =>
        {
            var captured = f.Memory.CaptureEpisodeEditor([f.Source, f.Report]);
            var summary = f.Memory.SaveEpisodeFromEditor(captured, Fixture.Secret, [EpisodeEventReference.From(f.Source)]);
            var window = f.Track(new EpisodeMemoryEditorWindow(f.Memory, f.Memory.CaptureEpisodeEditor([f.Source, f.Report], summary.Id)));
            f.Memory.Delete(summary.Id);
            return Task.FromResult(window.Body.Text == "" && window.Original.Text == "" && !window.Save.IsEnabled
                && f.Memory.Entries.Any(e => e.Id == f.Entry.Id));
        });
        await Check("generated_draft_keeps_original_dependencies_after_selection_changes", async f =>
        {
            var window = f.Episode((_, selected, _) => Task.FromResult(new EpisodeSummaryDraft(Fixture.Secret, selected.ToImmutableArray())));
            f.Select(window, f.Source.Id);
            if (!await window.TryGenerateDraftAsync()) return false;
            window.Sources.SelectedItems.Clear(); f.Select(window, f.Report.Id); window.Reviewed.IsChecked = true;
            bool cannotMisattribute = !window.Save.IsEnabled && !window.TrySave();
            f.Memory.Delete(f.Entry.Id);
            return cannotMisattribute && window.Body.Text == "" && window.Original.Text == "";
        });
        await Check("explicitly_cleared_draft_can_be_replaced_with_unrelated_manual_content", async f =>
        {
            var window = f.Episode((_, selected, _) => Task.FromResult(new EpisodeSummaryDraft(Fixture.Secret, selected.ToImmutableArray())));
            f.Select(window, f.Source.Id);
            if (!await window.TryGenerateDraftAsync()) return false;
            window.Sources.SelectedItems.Clear(); f.Select(window, f.Report.Id);
            window.Body.Clear(); window.Body.Text = "重新填写的独立报告摘要"; f.Memory.Delete(f.Entry.Id);
            return window.Body.Text == "重新填写的独立报告摘要" && window.Original.Text.Contains(f.Report.Body);
        });
        await Check("correcting_selected_source_clears_only_derived_editor_content", f =>
        {
            var window = f.Episode(); f.Select(window, f.Source.Id); window.Body.Text = Fixture.Secret;
            f.Memory.Update(f.Entry.Id, "新的已纠正来源");
            return Task.FromResult(window.Body.Text == "" && window.Original.Text == "" && !window.GenerateDraft.IsEnabled
                && f.Memory.Entries.Single().Text == "新的已纠正来源");
        });
        await Check("background_revocation_is_applied_on_window_dispatcher", async f =>
        {
            var window = f.Episode(); f.Select(window, f.Source.Id); window.Body.Text = Fixture.Secret;
            await Task.Run(() => f.Memory.Delete(f.Entry.Id));
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            return window.Body.Text == "" && window.Original.Text == "";
        });
        await Check("expiry_without_write_clears_open_views_and_reload_does_not_restore_prose", async f =>
        {
            f.SeedExpiry(DateTimeOffset.UtcNow.AddMilliseconds(700)); f.Memory.AddFromEditor("另一个有效来源");
            var main = f.Track(new AssistantMemoryWindow(f.Assistant, f.Settings));
            main.MemoryTab.IsSelected = true; main.MemoryList.SelectedItem = main.MemoryList.Items.Cast<AssistantMemoryEntry>().Single(e => e.Id == f.Entry.Id);
            var personal = f.Track(new SummaryMemoryEditorWindow(f.Memory));
            foreach (ListBoxItem row in personal.SourceList.Items) row.IsSelected = true;
            personal.BodyBox.Text = Fixture.Secret;
            var episode = f.Episode(); f.Select(episode, f.Source.Id); episode.Body.Text = Fixture.Secret;
            foreach (Window window in new Window[] { main, personal, episode })
            { window.ShowInTaskbar = false; window.ShowActivated = false; window.Left = -3000; window.Top = -3000; window.Show(); }
            await Task.Delay(1800);
            bool cleared = main.MemoryText.Text == "" && personal.BodyBox.Text == "" && episode.Body.Text == "" && episode.Original.Text == "";
            main.ReloadMemoryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return cleared && main.MemoryList.Items.Cast<AssistantMemoryEntry>().Any(e => e.Id == f.Entry.Id && e.Text == "")
                && episode.Sources.Items.Cast<EpisodeMemoryEditorWindow.SourceRow>().All(row => row.Event.Id != f.Source.Id);
        });
        exit(checks.Values.All(v => v) ? 0 : 1); return true;
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Synthetic view test has no network.");
    }
    private sealed class Fixture : IDisposable
    {
        internal const string Secret = "SYNTHETIC_REVOKED_VIEW";
        internal readonly string Directory;
        internal readonly Settings Settings = new() { LocalEnabled = false };
        internal readonly AssistantMemoryStore Memory;
        internal readonly ChatGptAssistant Assistant;
        internal readonly AssistantMemoryEntry Entry;
        internal readonly ConversationEvent Source, Report;
        private readonly List<Window> windows = [];
        internal Fixture(string directory)
        {
            Directory = directory; System.IO.Directory.CreateDirectory(directory);
            Settings.ChatGpt.ActiveAccount = "synthetic-view-owner";
            Memory = new(directory, () => Settings.ChatGpt.ActiveAccount); Memory.Initialize();
            var identity = new ConversationIdentity("synthetic", "binding", "main"); var now = DateTimeOffset.UtcNow;
            Source = new(Guid.NewGuid(), identity, ConversationSource.TrustedUser, "记住：" + Secret, now, now, 1, SourceMessageId: "synthetic-source");
            Report = new(Guid.NewGuid(), identity, ConversationSource.Report, "独立报告内容", now, now, 1);
            Memory.EpisodeSourceReader = (owner, id) => owner == identity ? id == Source.Id ? Source : id == Report.Id ? Report : null : null;
            Entry = Memory.Add(Secret, new("wechat_manual", Memory.Profile, now, identity, Source.SourceMessageId, Source.Revision,
                Guid.NewGuid(), Guid.NewGuid(), now, now));
            var http = new NoNetwork();
            Assistant = new(Settings, () => { }, new ChatGptAuth(Path.Combine(directory, "absent-synthetic-auth.json"), http), http, Memory);
        }
        internal T Track<T>(T window) where T : Window { windows.Add(window); return window; }
        internal EpisodeMemoryEditorWindow Episode(Func<EpisodeMemoryEditorSnapshot, IReadOnlyList<EpisodeEventReference>, CancellationToken, Task<EpisodeSummaryDraft>>? generate = null) =>
            Track(new EpisodeMemoryEditorWindow(Memory, Memory.CaptureEpisodeEditor([Source, Report]), generate));
        internal void Select(EpisodeMemoryEditorWindow window, Guid id) => window.Sources.SelectedItem =
            window.Sources.Items.Cast<EpisodeMemoryEditorWindow.SourceRow>().Single(row => row.Event.Id == id);
        internal void SeedExpiry(DateTimeOffset expiry)
        {
            // Initial synthetic fixture data: no mutation notification is sent at the later expiry boundary.
            var document = JsonSerializer.Deserialize<AssistantMemoryDocument>(LocalSecret.Unprotect(File.ReadAllText(Memory.DataPath!)), Settings.Json)!;
            int index = document.Entries.FindIndex(e => e.Id == Entry.Id);
            document.Entries[index] = Entry with { Metadata = Entry.Metadata! with { ExpiresAtUtc = expiry } };
            File.WriteAllText(Memory.DataPath!, LocalSecret.Protect(JsonSerializer.Serialize(document, Settings.Json)));
        }
        public void Dispose() { foreach (var window in windows) window.Close(); Assistant.Dispose(); }
    }
}
