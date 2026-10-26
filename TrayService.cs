using System;
using System.Drawing;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace CodexDashboard;

public sealed class TrayService : IDisposable
{
    private readonly App app;
    private readonly DashboardViewModel vm;
    private readonly Settings settings;
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.ToolStripMenuItem summary;
    private readonly Forms.ToolStripMenuItem tasks;
    private readonly Forms.ToolStripMenuItem hud;
    private readonly Forms.ToolStripMenuItem mini;
    private readonly Forms.ToolStripMenuItem through;
    private readonly Forms.ToolStripMenuItem notify;
    private Icon? drawing;
    private ToastWindow? toast;
    private readonly System.Windows.Threading.DispatcherTimer clickTimer;
    private DateTime ignoreSingleBefore;
    public TrayService(App app, DashboardViewModel vm, Settings settings)
    {
        this.app = app; this.vm = vm; this.settings = settings;
        menu = new Forms.ContextMenuStrip();
        summary = new Forms.ToolStripMenuItem("Codex · 正在读取") { Enabled = false };
        tasks = new Forms.ToolStripMenuItem("最近任务");
        hud = new Forms.ToolStripMenuItem("桌面紧凑 HUD", null, (_, _) => app.SetHudMode(HudModes.Compact));
        mini = new Forms.ToolStripMenuItem("超 mini · 仅托盘圆点", null, (_, _) => app.SetHudMode(HudModes.Tray));
        through = new Forms.ToolStripMenuItem("HUD 鼠标穿透", null, (_, _) => { settings.HudClickThrough = !settings.HudClickThrough; app.ApplySettings(); });
        notify = new Forms.ToolStripMenuItem("弹出提醒", null, (_, _) => { settings.Notifications = !settings.Notifications; app.ApplySettings(); });
        menu.Items.AddRange(new Forms.ToolStripItem[] { summary, tasks, new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("打开监控台", null, (_, _) => app.ShowDashboard()), hud, mini, through, notify,
            new Forms.ToolStripMenuItem("打开网页监控", null, (_, _) => app.OpenWeb()),
            new Forms.ToolStripMenuItem("绿 运行 · 黄 等待 · 蓝 结束 · 红 失败 · 灰 未知") { Enabled = false },
            new Forms.ToolStripMenuItem("设置…", null, (_, _) => app.ShowSettings()), new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("退出 Codex Dashboard", null, (_, _) => app.ExitApplication()) });
        icon = new Forms.NotifyIcon { Text = "Codex Dashboard", ContextMenuStrip = menu };
        clickTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime + 30) };
        clickTimer.Tick += (_, _) => { clickTimer.Stop(); Update(); menu.Show(Forms.Cursor.Position); };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left && DateTime.UtcNow >= ignoreSingleBefore) { clickTimer.Stop(); clickTimer.Start(); } };
        icon.DoubleClick += (_, _) => { clickTimer.Stop(); ignoreSingleBefore = DateTime.UtcNow.AddMilliseconds(Forms.SystemInformation.DoubleClickTime); menu.Close(); app.ShowDashboard(); };
        icon.BalloonTipClicked += (_, _) => app.ShowDashboard();
        vm.Updated += Update;
        vm.Notify += OnNotification;
        Update(); icon.Visible = true;
    }
    public void Update()
    {
        var points = vm.HudTasks.Take(4).Select(t => (Name: t.Project.Length > 8 ? t.Project[..8] : t.Project, Status: t.ShortStatus, Color: ColorTranslator.FromHtml(t.Color))).ToList();
        if (vm.Hosts.Any(h => h.Error != null) && points.All(p => p.Color != ColorTranslator.FromHtml("#8A8886")))
        {
            if (points.Count == 4) points.RemoveAt(3);
            points.Add(("数据源", "连接异常", ColorTranslator.FromHtml("#8A8886")));
        }
        string text = "Codex\n" + string.Join("\n", points.Select(p => p.Name + "·" + p.Status.Replace("本轮", "")));
        icon.Text = text.Length > 63 ? text[..63] : text;
        using (var bitmap = RenderDots(points.Select(p => p.Color).ToArray()))
        {
            IntPtr handle = bitmap.GetHicon();
            Icon next;
            try { using var borrowed = Icon.FromHandle(handle); next = (Icon)borrowed.Clone(); }
            finally { DestroyIcon(handle); }
            icon.Icon = next;
            drawing?.Dispose(); drawing = next;
        }
        if (menu.Visible) return;
        summary.Text = vm.HudSummary;
        hud.Checked = settings.HudVisible && settings.HudMode == "compact";
        mini.Checked = settings.HudMode == "tray";
        through.Checked = settings.HudClickThrough;
        notify.Checked = settings.Notifications;
        while (tasks.DropDownItems.Count > 0)
        {
            var item = tasks.DropDownItems[0];
            tasks.DropDownItems.RemoveAt(0); item.Dispose();
        }
        foreach (var task in vm.HudTasks)
        {
            var captured = task;
            string title = $"{task.Project} · {task.StatusLabel}";
            tasks.DropDownItems.Add(new Forms.ToolStripMenuItem(title, null, (_, _) => { vm.Select(captured); app.ShowDashboard(); }));
        }
        if (tasks.DropDownItems.Count == 0) tasks.DropDownItems.Add(new Forms.ToolStripMenuItem("最近没有任务") { Enabled = false });
    }
    public static Bitmap RenderDots(IReadOnlyList<Color> colors)
    {
        var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int count = Math.Clamp(colors.Count, 1, 4);
            for (int i = 0; i < count; i++)
            {
                float x = count == 1 ? 16 : (i % 2 == 0 ? 9 : 23);
                float y = count <= 2 ? 16 : (i < 2 ? 9 : 23);
                using var brush = new SolidBrush(colors.Count == 0 ? ColorTranslator.FromHtml("#8A8886") : colors[i]);
                g.FillEllipse(brush, x - 5, y - 5, 10, 10);
            }
        }
        return bitmap;
    }
    private void OnNotification(TaskRecord task)
    {
        if (!settings.Notifications) return;
        toast?.Close();
        toast = new ToastWindow(task, app);
        toast.Show();
        if (settings.Sound) System.Media.SystemSounds.Asterisk.Play();
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    public void Dispose()
    {
        vm.Updated -= Update; vm.Notify -= OnNotification;
        clickTimer.Stop(); toast?.Close(); icon.Visible = false; icon.Dispose(); menu.Dispose(); drawing?.Dispose();
    }
}
