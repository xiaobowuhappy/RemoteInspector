using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RemoteInspector.Desktop;

public partial class MainWindow
{
    private void MoreActions_Click(object sender, RoutedEventArgs e)
    { HeaderActionsMenu.PlacementTarget = MoreActions; HeaderActionsMenu.IsOpen = true; }
    private void HeaderActions_Opened(object sender, RoutedEventArgs e)
    { SessionOperationsMenu.IsEnabled = snapshot == null && Selected?.Session.IsConnected == true; }
    private void HeaderLayout_Click(object sender, RoutedEventArgs e)
    { ApplyLayoutPreset(int.Parse((string)((MenuItem)sender).Tag)); }
    private void UpdateHeaderConnection()
    {
        int count = sessions.Count(s => s.Session.IsConnected);
        HeaderConnectionState.Text = snapshot != null ? "离线快照" : !host.IsRunning ? "未监听" : count == 0 ? "监听中 · 等待设备" : $"已连接 {count} 台设备";
        HeaderConnectionState.Foreground = count > 0 ? Brushes.LightGreen : Brushes.LightSlateGray;
        DisconnectDevice.IsEnabled = snapshot == null && Selected?.Session.IsConnected == true;
        PortBox.IsEnabled = !host.IsRunning && !stoppingListener;
    }
    private double expandedDiagnosticsHeight = 290;
    private void ToggleDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        bool collapse = ToolsWorkspace.Visibility == Visibility.Visible;
        if (collapse) expandedDiagnosticsHeight = ToolsHeight.ActualHeight;
        ToolsWorkspace.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        ToolsHeight.MinHeight = collapse ? 0 : 215;
        ToolsHeight.Height = new GridLength(collapse ? 0 : Math.Max(215, expandedDiagnosticsHeight));
        ToggleDiagnostics.Content = collapse ? "展开信息窗 ▴" : "最小化信息窗 ▾";
    }
    private void MainNavigation_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int index = key == Key.D1 ? 0 : key == Key.D2 ? 1 : key == Key.D3 ? 2 : -1;
        if (index >= 0) { WorkspacePages.SelectedIndex = index; e.Handled = true; }
    }
}
