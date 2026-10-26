using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core;

namespace CodexDashboard;
internal static class FileChannelRuntimeTests
{
    private sealed class Stub(Func<HttpRequestMessage,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return send(r); } }
    internal static async Task<bool> TryRunAsync(string[] args, Action<int> exit)
    {
        if (args is not ["--file-channel-runtime-test", var root]) return false;
        Directory.CreateDirectory(root); var checks = new Dictionary<string,bool>();
        void Check(string key, bool ok) { checks[key] = ok; if (!ok) throw new InvalidOperationException(key); }
        try
        {
            int uploads = 0, filesSent = 0;
            string hex = "";
            bool uploadedExact = false;
            bool holdSend = false;
            var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var store = new AssistantArtifactStore(root);
            await using var f = await DurableRuntimeTests.Fixture.CreateAsync(Path.Combine(root,"runtime"), modelEnabled:false, artifacts:store,
                channelRequestOverride: async (r, ct) =>
                {
                    if (r.RequestUri!.AbsolutePath.EndsWith("getuploadurl"))
                    {
                        using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
                        hex = json.RootElement.GetProperty("aeskey").GetString()!;
                        return new(HttpStatusCode.OK) { Content = new StringContent("{\"upload_param\":\"synthetic\"}") };
                    }
                    if (r.RequestUri.AbsolutePath.EndsWith("sendmessage"))
                    {
                        using var json = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
                        var item = json.RootElement.GetProperty("msg").GetProperty("item_list")[0];
                        if (item.GetProperty("type").GetInt32() == 4)
                        {
                            filesSent++;
                            if (holdSend) { sending.TrySetResult(); await releaseSend.Task.WaitAsync(ct); }
                            Check("file_message_has_selected_name", item.GetProperty("file_item").GetProperty("file_name").GetString() == "selected.txt");
                            return new(HttpStatusCode.OK) { Content = new StringContent("{\"ret\":0,\"message_id\":\"1234567890123456789\"}") };
                        }
                    }
                    return null;
                });
            f.Channel.FileMediaFactory = () => new WeChatMediaClient(new Stub(async r =>
            {
                uploads++;
                using var aes = Aes.Create(); aes.Key = Convert.FromHexString(hex);
                uploadedExact = Encoding.UTF8.GetString(aes.DecryptEcb(await r.Content!.ReadAsByteArrayAsync(), PaddingMode.PKCS7)) == "Selected immutable body";
                var response = new HttpResponseMessage(HttpStatusCode.OK); response.Headers.Add("x-encrypted-param", "synthetic-download"); return response;
            }));
            await f.InputAsync("file-help", "文件帮助");
            await ConversationHistoryRuntimeTests.Settled(f, "file-help");
            Check("file_help_matches_active_services_without_upload_or_model", f.Sent.Any(s => s.Contains("准备文件") && s.Contains("待发送文件"))
                && f.Sent.Any(s => s.Contains("没有开启附件读取")) && uploads == 0 && filesSent == 0 && f.Script.Calls == 0
                && DurableAssistantRuntime.IsKnownReadonlyInput("文件帮助"));
            string path = Path.Combine(root,"selected.txt"); await File.WriteAllTextAsync(path,"Selected immutable body");
            var taskChoice = f.Runtime.ArtifactTaskChoices().Single();
            var sourceTask = new TaskReference(taskChoice.Identity.HostId, taskChoice.Identity.ThreadId, taskChoice.Record.TurnId, taskChoice.Identity.ProfileId);
            var selected = await f.Runtime.ImportArtifactAsync(f.Runtime.Identity!,path,default,sourceTask);
            Check("selected_artifact_keeps_source_task", selected.Task == sourceTask && store.Load(f.Runtime.Identity!, selected.Id).Task == sourceTask);
            bool wrongTurn = false;
            try { await f.Runtime.ImportArtifactAsync(f.Runtime.Identity!,path,default,sourceTask with { TurnId = "other-turn" }); } catch (InvalidOperationException) { wrongTurn = true; }
            Check("changed_task_turn_is_not_silently_reassociated", wrongTurn);
            await File.WriteAllTextAsync(path,"Changed local source");
            await f.InputAsync("list-prepared","待发送文件");
            await ConversationHistoryRuntimeTests.Settled(f,"list-prepared");
            Check("list_prepared_without_model_or_network_upload", !f.Sent.Last().Contains(selected.Id) && f.Sent.Last().Contains("未请求发送") && uploads == 0 && filesSent == 0 && f.Script.Calls == 0);
            Check("prepared_list_has_copyable_exact_send_command", f.Sent.Last().Contains("发送文件 1"));
            f.Settings.ChatGpt.Enabled = true;
            bool foundPrepared = false;
            f.Script.Handler = (request, ct) =>
            {
                if (request.Observations.IsEmpty) return Task.FromResult(ToolModelResponse.Call(new ModelToolCall("find-prepared", "list_prepared_files", "{\"query\":\"selected\",\"page\":1}")));
                using var factsDocument = JsonDocument.Parse(request.Observations.Last().ResultJson);
                var facts = factsDocument.RootElement.GetProperty("facts");
                foundPrepared = facts.GetProperty("files")[0].GetProperty("Id").GetString() == selected.Id
                    && facts.GetProperty("Total").GetInt32() == 1 && !facts.GetProperty("currentAuthorization").GetBoolean()
                    && !facts.GetProperty("deliveryVerified").GetBoolean() && !facts.ToString().Contains(path);
                return Task.FromResult(ToolModelResponse.Reply("找到准备好的文件。"));
            };
            await f.InputAsync("find-prepared-naturally", "帮我找一下准备过的selected文件");
            await ConversationHistoryRuntimeTests.Settled(f, "find-prepared-naturally");
            Check("natural_file_search_uses_saved_snapshot_without_upload_or_action", foundPrepared && uploads == 0 && filesSent == 0 && (await f.ReadAsync()).Operations.IsEmpty);
            f.Settings.ChatGpt.Enabled = false;
            await f.InputAsync("send-selected","发送文件 1");
            await ConversationHistoryRuntimeTests.Settled(f,"send-selected");
            Check("explicit_command_sends_snapshot_and_records_api_acceptance", uploadedExact && uploads == 1 && filesSent == 1
                && (await f.ReadAsync()).Operations.Single().Stage == OperationStage.Accepted && (await f.ReadAsync()).Operations.Single().Task == sourceTask && f.Sent.Last().Contains("接口已受理"));
            await f.InputAsync("check-file-status", "待发送文件");
            await ConversationHistoryRuntimeTests.Settled(f, "check-file-status");
            Check("prepared_list_shows_api_acceptance_without_phone_delivery_claim", f.Sent.Last().Contains("接口已受理，手机收信未核实") && uploads == 1 && filesSent == 1);
            bool windowCurrent = true;
            var preparedWindow = new PreparedFilesWindow((page, query, ct) => f.Runtime.ReadPreparedFilesAsync(f.Runtime.Identity!, page, query, ct), () => windowCurrent);
            await preparedWindow.RefreshAsync();
            var fileList = ((System.Windows.Controls.DockPanel)preparedWindow.Content).Children.OfType<System.Windows.Controls.ListBox>().Single();
            preparedWindow.Measure(new System.Windows.Size(480, 360));
            preparedWindow.Arrange(new System.Windows.Rect(0, 0, 480, 360));
            Check("desktop_prepared_files_shows_snapshot_and_send_state", fileList.Items.Count == 1
                && ((System.Windows.Controls.TextBlock)((System.Windows.Controls.ListBoxItem)fileList.Items[0]).Content).Text.Contains("接口已受理"));
            var displayedFile = (System.Windows.Controls.ListBoxItem)fileList.Items[0];
            Check("desktop_file_view_hides_internal_id_and_copies_name_query", !((System.Windows.Controls.TextBlock)displayedFile.Content).Text.Contains(selected.Id)
                && (string)displayedFile.Tag == "待发送文件 查找 selected.txt"
                && PreparedFilesWindow.LookupCommand("report;notes.txt") == "待发送文件");
            var visual = (System.Windows.FrameworkElement)preparedWindow.Content;
            visual.Measure(new System.Windows.Size(440, 300));
            visual.Arrange(new System.Windows.Rect(0, 0, 440, 300));
            visual.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(440, 300, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var imageOutput = File.Create(Path.Combine(root, "prepared-files-small.png"))) png.Save(imageOutput);
            windowCurrent = false; preparedWindow.InvalidateIfIdentityChanged();
            Check("desktop_file_list_clears_on_binding_change", fileList.Items.Count == 0);
            preparedWindow.Close();
            var queries = new List<(int Page, string? Query)>();
            var searchWindow = new PreparedFilesWindow((page, query, ct) =>
            {
                queries.Add((page, query));
                return Task.FromResult(new ArtifactPage([], page, 3, 12, new Dictionary<string, ArtifactSendSummary>()));
            }, () => true);
            await searchWindow.RefreshAsync();
            var searchPanel = (System.Windows.Controls.DockPanel)searchWindow.Content;
            var header = searchPanel.Children.OfType<System.Windows.Controls.StackPanel>().Single();
            var filter = header.Children.OfType<System.Windows.Controls.DockPanel>().Single();
            var input = filter.Children.OfType<System.Windows.Controls.TextBox>().Single();
            var find = filter.Children.OfType<System.Windows.Controls.Button>().Single();
            var navigation = searchPanel.Children.OfType<System.Windows.Controls.WrapPanel>().Single();
            var nextPage = navigation.Children.OfType<System.Windows.Controls.Button>().Single(b => (string)b.Content == "下一页");
            input.Text = "报告"; find.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            input.Text = "尚未提交的新词"; nextPage.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check("paging_keeps_applied_search_until_new_search_submitted", queries.Last() == (2, "报告"));
            find.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check("submitting_new_search_returns_to_first_page", queries.Last() == (1, "尚未提交的新词"));
            searchWindow.Close();
            await f.InputAsync("send-selected","发送文件 1");
            Check("duplicate_source_does_not_send_again", filesSent == 1 && uploads == 1);
            await f.InputAsync("incomplete-id","发送文件 " + selected.Id[..12]);
            await ConversationHistoryRuntimeTests.Settled(f,"incomplete-id");
            Check("ambiguous_short_command_does_not_send", filesSent == 1 && f.Sent.Last().Contains("按列表序号"));
            var uploaded = new WeChatUploadedFile { BotId = f.Binding.BotId, UserId = f.Binding.UserId, DownloadParameter = "synthetic", Base64Key = "synthetic", Length = 1 };
            holdSend = true;
            var occupying = f.Channel.SendArtifactAsync(uploaded,"selected.txt","occupy",f.Runtime.Identity!,default,() => true);
            await sending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bool current = true;
            var waiting = f.Channel.SendArtifactAsync(uploaded,"selected.txt","waiting",f.Runtime.Identity!,default,() => current);
            current = false;
            releaseSend.TrySetResult();
            await occupying;
            bool notSent = false;
            try { await waiting; } catch (FileSendNotStartedException) { notSent = true; }
            Check("changed_request_while_waiting_for_gate_never_calls_api", notSent && filesSent == 2);
            holdSend = false;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool cancelledBeforeSend = false;
            try { await f.Channel.SendArtifactAsync(uploaded,"selected.txt","cancelled",f.Runtime.Identity!,cancelled.Token,() => true); }
            catch (FileSendNotStartedException) { cancelledBeforeSend = true; }
            Check("cancelled_wait_is_proven_not_sent", cancelledBeforeSend && filesSent == 2);
            holdSend = true;
            sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var activeIdentity = f.Runtime.Identity!;
            var interrupted = f.Channel.SendArtifactAsync(uploaded,"selected.txt","session-active",activeIdentity,default,() => true);
            await sending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queuedAtStop = f.Channel.SendArtifactAsync(uploaded,"selected.txt","session-queued",activeIdentity,default,() => true);
            f.Settings.WeChat.ClawBotEnabled = false;
            await f.Channel.ConfigureAsync();
            bool activeCancelled = false, queuedCancelled = false;
            try { await interrupted; } catch (OperationCanceledException) { activeCancelled = true; }
            try { await queuedAtStop; } catch (FileSendNotStartedException) { queuedCancelled = true; }
            Check("channel_stop_cancels_active_and_queued_file_sends", activeCancelled && queuedCancelled && filesSent == 3);
            File.WriteAllText(Path.Combine(root,"checks.json"),JsonSerializer.Serialize(checks)); exit(0);
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(root,"failure.txt"),error.ToString()); exit(1); }
        return true;
    }
}
