using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace CodexDashboard;

internal static class AssistantLauncher
{
    internal static void Open(Window owner)
    {
        var directory = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(directory, "..", "assistant", "CodexAssistant.exe")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CodexAssistant", "CodexAssistant.exe")
        };
        string? executable = candidates.FirstOrDefault(File.Exists);
        if (executable == null)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择已安装的 Codex 助手", Filter = "Codex 助手|CodexAssistant.exe",
                CheckFileExists = true, Multiselect = false
            };
            if (dialog.ShowDialog(owner) != true) return;
            executable = dialog.FileName;
        }
        try
        {
            if (!string.Equals(Path.GetFileName(executable), "CodexAssistant.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择 CodexAssistant.exe。");
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! });
        }
        catch (Exception) { MessageBox.Show(owner, "助手未能启动，请从助手安装目录打开。", "个人助手"); }
    }
}
