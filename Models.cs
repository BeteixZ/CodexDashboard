using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CodexDashboard;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ProjectFilter
{
    public string Name { get; set; } = "全部项目";
    public string Label { get; set; } = "全部项目";
    public string Detail { get; set; } = "";
}
