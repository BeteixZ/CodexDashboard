#requires -Version 7.0
param([string]$PackageDirectory=$PSScriptRoot, [string]$RenderPreview, [string]$TestDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework
$package=[IO.Path]::GetFullPath($PackageDirectory)
$manifest=Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.version -ne 1 -or $manifest.product -notin @('assistant','dashboard')) { throw 'Unsupported product package.' }
$name=if ($manifest.product -eq 'assistant') { 'CodexAssistant' } else { 'CodexDashboard' }
$display=if ($manifest.product -eq 'assistant') { 'Codex 助手' } else { 'Codex Dashboard' }
[xml]$xaml=@'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
 Width="760" Height="610" MinWidth="660" MinHeight="580" WindowStartupLocation="CenterScreen" Background="#F4F6F8" FontFamily="Microsoft YaHei UI" FontSize="14">
 <Grid Margin="30">
  <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
  <StackPanel><TextBlock x:Name="Heading" FontSize="28" FontWeight="SemiBold" Foreground="#16232B"/><TextBlock Margin="0,8,0,20" Text="独立安装 · 升级保留旧程序与数据" Foreground="#52636C"/></StackPanel>
  <Border Grid.Row="1" Background="White" CornerRadius="12" Padding="22">
   <Grid><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
    <TextBlock Text="程序放在哪里" FontWeight="SemiBold"/>
    <DockPanel Grid.Row="1" Margin="0,8,0,20"><Button x:Name="BrowseProgram" DockPanel.Dock="Right" Content="选择…" Width="76" Margin="10,0,0,0"/><TextBox x:Name="ProgramPath" Padding="8" MinHeight="38" VerticalContentAlignment="Center"/></DockPanel>
    <TextBlock Grid.Row="2" Text="记录与设置放在哪里" FontWeight="SemiBold"/>
    <DockPanel Grid.Row="3" Margin="0,8,0,10"><Button x:Name="BrowseData" DockPanel.Dock="Right" Content="选择…" Width="76" Margin="10,0,0,0"/><TextBox x:Name="DataPath" Padding="8" MinHeight="38" VerticalContentAlignment="Center"/></DockPanel>
    <TextBlock x:Name="DataHint" Grid.Row="4" Text="数据单独保存，卸载程序时保留。两个目录不能重叠。" TextWrapping="Wrap" Foreground="#52636C"/>
   </Grid>
  </Border>
  <StackPanel Grid.Row="2" Margin="0,18,0,16">
   <TextBlock Text="升级前请先退出对应软件。这里仅安装程序，不搬迁旧数据，也不自动启动接收消息。" TextWrapping="Wrap" Foreground="#52636C"/>
   <CheckBox x:Name="AddShortcut" Content="添加到开始菜单" IsChecked="True" Margin="0,12,0,0"/>
   <TextBox x:Name="Status" Margin="0,14,0,0" MinHeight="65" MaxHeight="115" IsReadOnly="True" TextWrapping="Wrap" BorderThickness="0" Background="Transparent" Foreground="#174D38" VerticalScrollBarVisibility="Auto" Text="确认目录后即可安装。"/>
  </StackPanel>
  <StackPanel Grid.Row="3" Orientation="Horizontal" HorizontalAlignment="Right">
   <Button x:Name="Uninstall" Content="卸载此产品" IsEnabled="False" MinWidth="110" Height="40" Margin="0,0,10,0"/>
   <Button x:Name="Launch" Content="启动" IsEnabled="False" MinWidth="95" Height="40" Margin="0,0,10,0"/>
   <Button x:Name="CloseButton" Content="关闭" MinWidth="95" Height="40" Margin="0,0,10,0"/>
   <Button x:Name="Install" Content="安装 / 升级" MinWidth="140" Height="40" Background="#155E43" Foreground="White" BorderThickness="0"/>
  </StackPanel>
 </Grid>
</Window>
'@
$reader=[Xml.XmlNodeReader]::new($xaml)
$window=[Windows.Markup.XamlReader]::Load($reader)
$window.Title="安装 $display"
foreach ($control in @('Heading','ProgramPath','DataPath','BrowseProgram','BrowseData','DataHint','Status','Install','Launch','CloseButton','AddShortcut','Uninstall')) { Set-Variable -Name $control -Value $window.FindName($control) }
$Heading.Text="安装 $display"
$ProgramPath.Text=Join-Path $env:LOCALAPPDATA "Programs/$name"
$DataPath.Text=Join-Path $env:LOCALAPPDATA $name
$script:installProfile='default'
$script:installedRoot=$null
$script:shortcutDirectory=[Environment]::GetFolderPath('Programs')
function Read-ExistingInstallation {
    $Uninstall.IsEnabled=$false
    $record=Join-Path $ProgramPath.Text 'installation.json'
    if (Test-Path -LiteralPath $record -PathType Leaf) {
        $existing=Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
        if ($existing.version -ne 1 -or $existing.product -ne $manifest.product) { throw '所选目录不是本产品的兼容安装，请选择其他目录。' }
        $DataPath.Text=$existing.dataDirectory
        $DataPath.IsReadOnly=$true; $BrowseData.IsEnabled=$false
        $script:installProfile=$existing.protocolProfile
        $DataHint.Text='检测到已有安装。升级沿用原数据目录，不迁移记录或切换绑定。'
        $Uninstall.IsEnabled=$true
    } else {
        $DataPath.IsReadOnly=$false; $BrowseData.IsEnabled=$true
        $script:installProfile='default'
        $DataHint.Text='数据单独保存，卸载程序时保留。两个目录不能重叠。'
    }
}
function Select-Directory($InputControl) {
    $dialog=[Microsoft.Win32.OpenFolderDialog]::new()
    $dialog.Title='选择文件夹'
    if (Test-Path -LiteralPath $InputControl.Text -PathType Container) { $dialog.InitialDirectory=$InputControl.Text }
    if ($dialog.ShowDialog($window)) { $InputControl.Text=$dialog.FolderName }
}
$BrowseProgram.Add_Click({ try { Select-Directory $ProgramPath; Read-ExistingInstallation } catch { $Status.Text=$_.Exception.Message } })
$BrowseData.Add_Click({ try { Select-Directory $DataPath } catch { $Status.Text=$_.Exception.Message } })
$ProgramPath.Add_LostKeyboardFocus({ try { Read-ExistingInstallation } catch { $Status.Text=$_.Exception.Message } })
$CloseButton.Add_Click({ $window.Close() })
$ProgramPath.Add_TextChanged({ $Launch.IsEnabled=$false; $Uninstall.IsEnabled=$false; $script:installedRoot=$null })
$DataPath.Add_TextChanged({ $Launch.IsEnabled=$false; $script:installedRoot=$null })
$Install.Add_Click({
    $Install.IsEnabled=$false; $Launch.IsEnabled=$false; $Status.Text='正在复制并核对程序文件…'
    $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Render)
    try {
        Read-ExistingInstallation
        $result=& (Join-Path $package 'Install-Product.ps1') -PackageDirectory $package -InstallationDirectory $ProgramPath.Text -DataDirectory $DataPath.Text -ProtocolProfile $script:installProfile
        $script:installedRoot=$result.InstallationDirectory
        $Status.Text="安装完成，软件尚未启动。`n程序：$($result.InstallationDirectory)`n数据：$($result.DataDirectory)"
        if ($AddShortcut.IsChecked) {
            try { & (Join-Path $package 'Add-ProductShortcut.ps1') -InstallationDirectory $script:installedRoot -ShortcutDirectory $script:shortcutDirectory | Out-Null }
            catch { $Status.Text+="`n快捷方式未创建：$($_.Exception.Message)" }
        }
        $Launch.IsEnabled=$true
        $Uninstall.IsEnabled=$true
    } catch { $Status.Text="安装未完成：$($_.Exception.Message)" }
    finally { $Install.IsEnabled=$true }
})
$Launch.Add_Click({
    $Launch.IsEnabled=$false
    try { & (Join-Path $script:installedRoot 'Launch-InstalledProduct.ps1') | Out-Null; $Status.Text='已执行启动入口，请查看对应软件窗口或托盘。' }
    catch { $Status.Text="启动未完成：$($_.Exception.Message)"; $Launch.IsEnabled=$true }
})
$Uninstall.Add_Click({
    try {
        Read-ExistingInstallation
        if (!$Uninstall.IsEnabled) { throw '没有找到本产品的安装记录。' }
        $selectedRoot=[IO.Path]::GetFullPath($ProgramPath.Text)
        $preview=& (Join-Path $package 'Uninstall-Product.ps1') -InstallationDirectory $selectedRoot -Preview
        if ($TestDirectory) {
            $testRoot=[IO.Path]::GetFullPath($TestDirectory).TrimEnd('\')+'\'
            if (!$selectedRoot.StartsWith($testRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Test uninstall must remain inside its new test directory.' }
        } else {
            $answer=[Windows.MessageBox]::Show($window,"卸载 $display 的程序文件？`n`n程序：$selectedRoot`n保留数据：$($preview.DataDirectoryPreserved)", '卸载此产品', [Windows.MessageBoxButton]::YesNo, [Windows.MessageBoxImage]::Question)
            if ($answer -ne [Windows.MessageBoxResult]::Yes) { return }
        }
        $Install.IsEnabled=$false; $Uninstall.IsEnabled=$false; $Launch.IsEnabled=$false
        $Status.Text='正在卸载程序，保留数据…'
        $window.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::Render)
        $result=& (Join-Path $package 'Uninstall-Product.ps1') -InstallationDirectory $selectedRoot
        $script:installedRoot=$null
        $Status.Text="程序已卸载。数据仍保存在：`n$($result.DataDirectoryPreserved)"
        if ($result.ShortcutsPreserved -gt 0) { $Status.Text+="`n另保留了 $($result.ShortcutsPreserved) 个已修改或占用的快捷方式。" }
        Read-ExistingInstallation
    } catch { $Status.Text="卸载未完成：$($_.Exception.Message)" }
    finally {
        $Install.IsEnabled=$true
        try { Read-ExistingInstallation } catch { $Uninstall.IsEnabled=$false }
    }
})
if ($TestDirectory) {
    if (![IO.Path]::IsPathFullyQualified($TestDirectory) -or (Test-Path -LiteralPath $TestDirectory)) { throw 'Use a new absolute test directory.' }
    [void][IO.Directory]::CreateDirectory($TestDirectory)
    $script:shortcutDirectory=Join-Path $TestDirectory 'shortcuts'
    $ProgramPath.Text=Join-Path $TestDirectory 'program'
    $DataPath.Text=Join-Path $TestDirectory 'data'
    $expectedData=$DataPath.Text
    [void][IO.Directory]::CreateDirectory($expectedData)
    $sentinel=Join-Path $expectedData 'synthetic-data.txt'
    'SYNTHETIC_GUI_DATA' | Set-Content -LiteralPath $sentinel
    $dataHash=(Get-FileHash -LiteralPath $sentinel).Hash
    $Install.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
    if (!$Launch.IsEnabled) { throw $Status.Text }
    $record=Join-Path $ProgramPath.Text 'installation.json'
    $first=Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
    $DataPath.Text=Join-Path $TestDirectory 'must-not-switch-data'
    $Install.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
    if (!$Launch.IsEnabled) { throw $Status.Text }
    $second=Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
    if ($second.activeRelease -eq $first.activeRelease -or $second.dataDirectory -ne $expectedData -or !$DataPath.IsReadOnly) { throw 'GUI upgrade did not retain data scope.' }
    if (!(Test-Path -LiteralPath (Join-Path $script:shortcutDirectory ($name+'.lnk')))) { throw 'GUI shortcut was not created.' }
    $busy=[IO.FileStream]::new((Join-Path $ProgramPath.Text "releases/$($second.activeRelease)/$name.exe"),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $Uninstall.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
        if (!$Uninstall.IsEnabled -or !(Test-Path -LiteralPath $record) -or !$Status.Text.StartsWith('卸载未完成')) { throw 'GUI did not retain a retry after occupied-file rejection.' }
    } finally { $busy.Dispose() }
    $Uninstall.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
    if ((Test-Path -LiteralPath $ProgramPath.Text) -or (Get-FileHash -LiteralPath $sentinel).Hash -ne $dataHash -or $Launch.IsEnabled -or $Uninstall.IsEnabled) { throw "GUI uninstall failed: $($Status.Text)" }
    if (Test-Path -LiteralPath (Join-Path $script:shortcutDirectory ($name+'.lnk'))) { throw 'GUI uninstall left its unchanged shortcut.' }
    @{ installButton=$true; upgradeButton=$true; uninstallButton=$true; occupiedFileRetry=$true; originalDataPreserved=$true; shortcutCreatedAndRemoved=$true; applicationStarted=$false } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $TestDirectory 'gui-checks.json')
    $window.Close()
    return
}
if ($RenderPreview) {
    if (![IO.Path]::IsPathFullyQualified($RenderPreview) -or (Test-Path -LiteralPath $RenderPreview)) { throw 'Use a new absolute preview path.' }
    $ProgramPath.Text="C:\Apps\$name"; $DataPath.Text="C:\Data\$name"
    $surface=$window.Content
    $surface.Background=$window.Background
    $surface.Measure([Windows.Size]::new(760,610)); $surface.Arrange([Windows.Rect]::new(0,0,760,610)); $surface.UpdateLayout()
    $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new(760,610,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $background=[Windows.Media.DrawingVisual]::new()
    $drawing=$background.RenderOpen()
    $drawing.DrawRectangle($window.Background,$null,[Windows.Rect]::new(0,0,760,610)); $drawing.Close()
    $bitmap.Render($background)
    $bitmap.Render($surface)
    $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $file=[IO.File]::Open($RenderPreview,[IO.FileMode]::CreateNew)
    try { $encoder.Save($file) } finally { $file.Dispose(); $window.Close() }
    return
}
try { Read-ExistingInstallation } catch { $Status.Text=$_.Exception.Message }
[void]$window.ShowDialog()
