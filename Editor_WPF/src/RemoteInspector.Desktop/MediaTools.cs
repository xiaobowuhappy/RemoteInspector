using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using RemoteInspector.Commands;
using RemoteInspector.Core;
using RemoteInspector.Protocol;

namespace RemoteInspector.Desktop;

public partial class MainWindow
{
    private sealed record PickHitRow(ScreenPickHit Hit) { public string Path => Hit.path; public string Kind => Hit.kind + " · " + Hit.camera; }
    private void OpenTexturePreview(byte[] bytes, PropertyRow row, InspectorSession session)
    {
        var bitmap = new BitmapImage(); using (var stream = new MemoryStream(bytes)) { bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); }
        var panel = new DockPanel { Margin = new Thickness(10), Background = new SolidColorBrush(Color.FromRgb(32,39,51)) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Top); panel.Children.Add(buttons);
        var label = new TextBlock { Text = $"{row.FieldLabel} · 预览 {bitmap.PixelWidth} × {bitmap.PixelHeight}", VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(label);
        var export = new Button { Content = "导出预览 PNG" }; buttons.Children.Add(export);
        var original = new Button { Content = "导出原尺寸 PNG", IsEnabled = session?.IsConnected == true }; buttons.Children.Add(original);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap }; DockPanel.SetDock(status, Dock.Bottom); panel.Children.Add(status);
        panel.Children.Add(new ScrollViewer { Content = new Image { Source = bitmap, Stretch = Stretch.Uniform }, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var window = new Window { Owner = this, Title = "纹理预览 · " + row.TextureId, Width = 820, Height = 650, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var lifetime = new CancellationTokenSource(); window.Closed += (_, _) => lifetime.Cancel();
        export.Click += (_, _) =>
        {
            try { var save = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = "texture-" + row.TextureId + "-preview.png" }; if (save.ShowDialog(window) == true) File.WriteAllBytes(save.FileName, Png(bytes)); }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        original.Click += async (_, _) =>
        {
            var save = new SaveFileDialog { Filter = "原尺寸 PNG|*.png", FileName = "texture-" + row.TextureId + ".png" };
            if (save.ShowDialog(window) != true) return;
            original.IsEnabled = false;
            try
            {
                status.Text = "正在读取远端原尺寸纹理…";
                var data = await session.GetTextureAsync(row.TextureId, lifetime.Token, original: true);
                await File.WriteAllBytesAsync(save.FileName, Png(data), lifetime.Token); status.Text = "已导出：" + save.FileName;
            }
            catch (OperationCanceledException) { status.Text = "导出已取消。"; }
            catch (Exception ex) { status.Text = "导出失败：" + ex.Message; }
            finally { original.IsEnabled = session.IsConnected && !lifetime.IsCancellationRequested; }
        };
        window.Show();
    }
    public async Task PickScreenshotAsync(float x, float y, float aspect, CancellationToken token = default)
    {
        var session = Live();
        ScreenPickResponse response;
        try { response = Wire.Body<ScreenPickResponse>(await session.RequestAsync((Command)InspectionProtocol.ScreenPick,
            new ScreenPickRequest { x = x, y = y, aspect = aspect }, token, 8, (Command)InspectionProtocol.ScreenPickResponse)); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new NotSupportedException("远端未响应截图拾取，请更新包含新 Runtime 的游戏包。"); }
        if (!response.success) throw new IOException(response.error);
        if (!IsCurrent(session)) return;
        PickResults.ItemsSource = response.hits?.Select(h => new PickHitRow(h)).ToList(); PickResultsPanel.Visibility = Visibility.Visible;
        StatusLabel.Text = $"相交节点 {response.hits?.Count ?? 0} 个；点击结果定位层级。结果来自点击时的场景，不发送游戏点击事件。";
    }
    private async void Screenshot_Click(object sender, MouseButtonEventArgs e)
    {
        if (PickFromScreenshot.IsChecked != true) return;
        if (ImageHistory.SelectedItem is not Picture { Screen: true } picture || picture.Session != Selected?.Session.Id || snapshot != null)
        { StatusLabel.Text = "请先抓取当前实时会话的游戏画面。纹理预览不能用于场景拾取。"; return; }
        if (PreviewImage.Source is not BitmapSource bitmap) return;
        var point = e.GetPosition(PreviewImage);
        if (!ImageCoordinates.TryNormalize(PreviewImage.ActualWidth, PreviewImage.ActualHeight, bitmap.PixelWidth, bitmap.PixelHeight, point.X, point.Y, out float x, out float y)) return;
        await Run(t => PickScreenshotAsync(x, y, bitmap.PixelWidth / (float)bitmap.PixelHeight, t));
    }
    private async void PickResult_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (PickResults.SelectedItem is not PickHitRow selected) return;
        var hit = selected.Hit;
        if (renderedTree?.allNodes?.Any(n => n.instanceId == hit.instanceId) != true)
        {
            await Run(async t => { var s = Live(); await s.RefreshTreeAsync(t); if (IsCurrent(s)) RenderTree(s.Tree); });
        }
        await NavigateNode(hit.instanceId); Tabs.SelectedIndex = 0;
    }
    private CompressedLogView compressedLog;
    private void ClearRemoteLog()
    {
        compressedLog?.Dispose(); compressedLog = null;
        CompressedLogContent.Content = new TextBlock { Text = "双击远端 .logcompr 文件，在此查看解码日志。", Margin = new Thickness(12) };
        RemoteFilesTabs.SelectedIndex = 0;
    }
    private void OpenRemoteLog(FileRow row)
    {
        try
        {
            var session = Live();
            if (compressedLog == null || compressedLog.SessionId != session.Id || compressedLog.RemotePath != row.Path)
            {
                compressedLog?.Dispose(); compressedLog = new CompressedLogView(session, row);
                CompressedLogContent.Content = compressedLog;
            }
            RemoteFilesTabs.SelectedItem = CompressedLogTab;
            _ = compressedLog.ReloadAsync();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
}
