using System;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace CodexDashboard;

internal sealed class AssistantTrayService : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu;
    internal AssistantTrayService(Action show, Action exit)
    {
        menu = new();
        menu.Items.Add("打开个人助手", null, (_, _) => show());
        menu.Items.Add("退出个人助手", null, (_, _) => exit());
        icon = new() { Text = "Codex 助手", Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => show();
    }
    public void Dispose() { icon.Visible = false; icon.Dispose(); menu.Dispose(); }
}
