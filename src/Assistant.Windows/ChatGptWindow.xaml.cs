using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CodexDashboard;

public partial class ChatGptWindow : Window
{
    private readonly ChatGptAssistant assistant;
    private readonly Settings settings;
    private readonly Func<string?, CancellationToken, Task<ChatGptAccount>> signIn;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? login;
    private bool syncing = true, busy;
    private bool AcceptsUiChanges => !lifetime.IsCancellationRequested
        && (Application.Current is not AssistantApp app || !app.Exiting);
    internal ChatGptWindow(ChatGptAssistant assistant, Settings settings, bool startLogin = false,
        Func<string?, CancellationToken, Task<ChatGptAccount>>? signIn = null)
    {
        InitializeComponent(); this.assistant = assistant; this.settings = settings;
        this.signIn = signIn ?? ((client, cancellation) => assistant.Auth.SignInAsync(client, cancellation));
        EnabledBox.IsChecked = settings.ChatGpt.Enabled; ContextBox.IsChecked = settings.ChatGpt.IncludeTaskContext;
        CompletionSummaryBox.IsChecked = settings.ChatGpt.CompletionSummariesEnabled;
        TaskControlBox.IsChecked = settings.ChatGpt.TaskControlEnabled;
        assistant.Changed += StatusChanged;
        Closed += (_, _) => { lifetime.Cancel(); login?.Cancel(); assistant.Changed -= StatusChanged; };
        ReloadAccounts(); syncing = false; StatusChanged();
        Loaded += async (_, _) => { if (startLogin) await SignInAsync(false); else if (assistant.Auth.Find(settings.ChatGpt.ActiveAccount)?.AccessToken.Length > 0) await RefreshModelsAsync(); };
    }
    private void StatusChanged()
    {
        if (!AcceptsUiChanges) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(StatusChanged); return; }
        ConnectionText.Text = assistant.Status;
    }
    private void ReloadAccounts()
    {
        bool previous = syncing; syncing = true;
        AccountBox.ItemsSource = assistant.Auth.Accounts.ToArray(); AccountBox.SelectedItem = assistant.Auth.Find(settings.ChatGpt.ActiveAccount);
        syncing = previous;
    }
    private void SetBusy(bool value)
    {
        busy = value;
        if (!AcceptsUiChanges) return;
        ConnectButton.IsEnabled = AddButton.IsEnabled = SignOutButton.IsEnabled = RefreshButton.IsEnabled = TestButton.IsEnabled = AccountBox.IsEnabled = ModelBox.IsEnabled = EnabledBox.IsEnabled = ContextBox.IsEnabled = CompletionSummaryBox.IsEnabled = TaskControlBox.IsEnabled = !value;
    }
    private async Task SignInAsync(bool add)
    {
        if (busy || !AcceptsUiChanges) return;
        SetBusy(true); login = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        bool completed = false, configurationAttempted = false, configurationSaved = false;
        CancelLoginButton.Visibility = Visibility.Visible; ResultText.Text = "请在浏览器完成 ChatGPT 登录，并允许此应用使用订阅额度。";
        try
        {
            string? selected = add ? null : (AccountBox.SelectedItem as ChatGptAccount)?.ClientId;
            var account = await signIn(selected, login.Token);
            login.Token.ThrowIfCancellationRequested();
            if (!AcceptsUiChanges) return;
            string model = settings.ChatGpt.ActiveAccount == account.ClientId ? settings.ChatGpt.Model : "";
            configurationAttempted = true;
            assistant.Configure(account.ClientId, model, account.Sharing, ContextBox.IsChecked == true);
            configurationSaved = true; ReloadAccounts();
            EnabledBox.IsChecked = account.Sharing;
            // Resolve the account's model before the welcome modal. A pending
            // first-use notice must not leave the enabled assistant without a model.
            if (account.Sharing) await assistant.EnsureModelAsync(login.Token);
            login.Token.ThrowIfCancellationRequested();
            if (!AcceptsUiChanges) return;
            if (account.Sharing && !account.Welcomed)
            {
                MessageBox.Show(this, "你正在使用 ChatGPT 订阅额度。\n本应用中的 AI 请求会使用你的订阅额度或可用积分，可在 ChatGPT 设置 → 用量中管理。", "You're using your ChatGPT plan", MessageBoxButton.OK, MessageBoxImage.Information);
                account.Welcomed = true; assistant.Auth.Save();
            }
            ResultText.Text = account.Sharing ? "授权成功，正在读取此账户的可用模型…" : "已登录，但未授权使用 ChatGPT 订阅；AI 问答尚未启用。";
            completed = true;
        }
        catch (Exception ex)
        {
            if (AcceptsUiChanges)
            {
                RestoreSavedOptions();
                if (configurationAttempted && !configurationSaved)
                {
                    ResultText.Text = "账户授权已完成，但设置尚未保存。已恢复原设置，请检查数据目录后重试。";
                    ResultText.BringIntoView();
                }
                else ShowError(ex);
            }
        }
        finally
        {
            if (AcceptsUiChanges) CancelLoginButton.Visibility = Visibility.Collapsed;
            login.Dispose(); login = null; SetBusy(false);
        }
        if (completed && AcceptsUiChanges && assistant.Auth.Find(settings.ChatGpt.ActiveAccount)?.Sharing == true) await RefreshModelsAsync();
    }
    private async Task RefreshModelsAsync()
    {
        if (busy || !AcceptsUiChanges) return; SetBusy(true);
        try
        {
            string selected = (AccountBox.SelectedItem as ChatGptAccount)?.ClientId ?? "";
            var models = await assistant.ModelsAsync(selected, lifetime.Token);
            if (!AcceptsUiChanges || selected != settings.ChatGpt.ActiveAccount) return;
            syncing = true; ModelBox.ItemsSource = models; ModelBox.SelectedItem = models.FirstOrDefault(m => m.Slug == settings.ChatGpt.Model) ?? models.First(); syncing = false;
            if (SaveOptions()) ResultText.Text = assistant.ConfigurationSavedWarning ?? "模型列表已更新。请点击“测试实际生成”验证当前模型权限。";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { syncing = false; SetBusy(false); }
    }
    private bool SaveOptions()
    {
        if (syncing || !AcceptsUiChanges) return false;
        try
        {
            assistant.Configure((AccountBox.SelectedItem as ChatGptAccount)?.ClientId ?? "", (ModelBox.SelectedItem as ChatGptModel)?.Slug ?? "",
                EnabledBox.IsChecked == true, ContextBox.IsChecked == true, CompletionSummaryBox.IsChecked == true, TaskControlBox.IsChecked == true);
            ResultText.Text = assistant.ConfigurationSavedWarning ?? "设置已保存。";
            return true;
        }
        catch
        {
            RestoreSavedOptions();
            ResultText.Text = "设置尚未保存，请检查数据目录后重试。已恢复原设置。";
            ResultText.BringIntoView();
            return false;
        }
    }
    private void RestoreSavedOptions()
    {
        bool previous = syncing; syncing = true;
        try
        {
            ReloadAccounts();
            string savedModel = settings.ChatGpt.Model;
            var models = ModelBox.Items.OfType<ChatGptModel>().ToArray();
            if (savedModel.Length > 0 && !models.Any(x => x.Slug == savedModel))
                models = new[] { new ChatGptModel(savedModel, savedModel) };
            ModelBox.ItemsSource = models;
            ModelBox.SelectedItem = models.FirstOrDefault(x => x.Slug == savedModel);
            EnabledBox.IsChecked = settings.ChatGpt.Enabled; ContextBox.IsChecked = settings.ChatGpt.IncludeTaskContext;
            CompletionSummaryBox.IsChecked = settings.ChatGpt.CompletionSummariesEnabled;
            TaskControlBox.IsChecked = settings.ChatGpt.TaskControlEnabled;
        }
        finally { syncing = previous; }
    }
    private async void AccountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || busy || !AcceptsUiChanges) return;
        syncing = true; ModelBox.ItemsSource = null; syncing = false;
        if (SaveOptions()) await RefreshModelsAsync();
    }
    private void ModelChanged(object sender, SelectionChangedEventArgs e) => SaveOptions();
    private void OptionsChanged(object sender, RoutedEventArgs e) => SaveOptions();
    private async void ConnectClicked(object sender, RoutedEventArgs e) => await SignInAsync(false);
    private async void AddClicked(object sender, RoutedEventArgs e) => await SignInAsync(true);
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshModelsAsync();
    private async void TestClicked(object sender, RoutedEventArgs e)
    {
        if (busy || !AcceptsUiChanges) return; SetBusy(true); ResultText.Text = "正在使用 ChatGPT 订阅额度测试生成…";
        try
        {
            string result = await assistant.TestAsync(lifetime.Token);
            if (AcceptsUiChanges) ResultText.Text = result;
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }
    private async void SignOutClicked(object sender, RoutedEventArgs e)
    {
        if (busy || !AcceptsUiChanges || AccountBox.SelectedItem is not ChatGptAccount account) return;
        SetBusy(true);
        try
        {
            try { assistant.Configure(account.ClientId, "", false, ContextBox.IsChecked == true, false, false); }
            catch
            {
                RestoreSavedOptions(); ResultText.Text = "设置尚未保存，本次未退出账户。请检查数据目录后重试。";
                ResultText.BringIntoView(); return;
            }
            RestoreSavedOptions();
            bool revoked = await assistant.Auth.SignOutAsync(account.ClientId, lifetime.Token);
            if (!AcceptsUiChanges) return;
            syncing = true; ModelBox.ItemsSource = null; syncing = false;
            ResultText.Text = revoked ? "已退出，注册信息保留供下次登录。" : "已清除本机凭据；未确认远程撤销，请在 ChatGPT 设置中断开此应用。";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }
    private void CancelLoginClicked(object sender, RoutedEventArgs e) => login?.Cancel();
    private void ShowError(Exception ex)
    {
        if (!AcceptsUiChanges) return;
        ResultText.Text = ChatGptAssistant.Explain(ex); assistant.ReportError(ex);
    }
    private void UsageClicked(object sender, RoutedEventArgs e)
    { try { Process.Start(new ProcessStartInfo("https://chatgpt.com/settings/usage") { UseShellExecute = true }); } catch { ResultText.Text = "请在 ChatGPT 设置 → 用量中管理。"; } }
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    private void MemoryClicked(object sender, RoutedEventArgs e) => new AssistantMemoryWindow(assistant, settings) { Owner = this }.ShowDialog();
}
