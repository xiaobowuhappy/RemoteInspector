using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RemoteInspector.Core;

namespace RemoteInspector.Desktop;

public sealed class CompressedLogView : UserControl, IDisposable
{
    private readonly InspectorSession session;
    private readonly string cache;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer followTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer repaint = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Button refresh = new() { Content = "刷新", Name = "RefreshCompressedLog" };
    private readonly CheckBox follow = new() { Content = "每 2 秒刷新" };
    public TextBox Keyword { get; } = new() { Width = 170, ToolTip = "在当前显示文本中查找；Enter 下一处，Shift+Enter 上一处" };
    public TextBox Text { get; } = new() { IsReadOnly = true, IsInactiveSelectionHighlightEnabled = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock state = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock matches = new() { VerticalAlignment = VerticalAlignment.Center };
    private string pendingText;
    private string lastKeyword = "";
    private readonly List<int> positions = new();
    private int position = -1;
    private bool busy, disposed;
    public Guid SessionId => session.Id;
    public string RemotePath { get; }
    public bool IsBusy => busy;
    public CompressedLogView(InspectorSession session, FileRow row)
    {
        this.session = session; RemotePath = row.Path;
        cache = Path.Combine(Path.GetTempPath(), "RemoteInspector-Log-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(cache);
        var panel = new DockPanel { Margin = new Thickness(8) }; Content = panel;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        header.Children.Add(new TextBlock { Text = session.DisplayName + " · " + row.Path, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = row.Path });
        var tools = new WrapPanel(); header.Children.Add(tools); tools.Children.Add(refresh); tools.Children.Add(follow);
        var export = new Button { Content = "导出解码文本" }; tools.Children.Add(export);
        var search = new WrapPanel(); header.Children.Add(search); search.Children.Add(new TextBlock { Text = "关键字", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3) }); search.Children.Add(Keyword);
        var previous = new Button { Content = "上一处" }; var next = new Button { Content = "下一处" }; var clear = new Button { Content = "清除" };
        search.Children.Add(previous); search.Children.Add(next); search.Children.Add(clear); search.Children.Add(matches);
        DockPanel.SetDock(state, Dock.Bottom); panel.Children.Add(state); panel.Children.Add(Text);
        refresh.Click += async (_, _) => await ReloadAsync(); export.Click += (_, _) => Export();
        next.Click += (_, _) => FindNext(); previous.Click += (_, _) => FindNext(true); clear.Click += (_, _) => Keyword.Clear();
        Keyword.TextChanged += (_, _) => { RebuildMatches(); if (positions.Count > 0) FindNext(); };
        Keyword.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FindNext(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; } else if (e.Key == Key.Escape) Keyword.Clear(); };
        followTimer.Tick += async (_, _) => { if (follow.IsChecked == true) await ReloadAsync(); };
        repaint.Tick += (_, _) => FlushText(); followTimer.Start(); repaint.Start();
    }
    private void FlushText()
    {
        string value = Interlocked.Exchange(ref pendingText, null); if (disposed || value == null) return;
        Text.Text = value; RebuildMatches();
        if (Keyword.Text.Length > 0 && positions.Count > 0) FindNext(); else if (follow.IsChecked == true) Text.ScrollToEnd();
    }
    private void RebuildMatches()
    {
        lastKeyword = Keyword.Text; positions.Clear(); position = -1;
        if (lastKeyword.Length > 0)
            for (int start = 0; start <= Text.Text.Length - lastKeyword.Length;)
            {
                int found = Text.Text.IndexOf(lastKeyword, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break; positions.Add(found); start = found + lastKeyword.Length;
            }
        if (positions.Count == 0) Text.Select(0, 0);
        matches.Text = lastKeyword.Length == 0 ? "" : positions.Count == 0 ? "无匹配" : $"{positions.Count} 处";
    }
    public void FindNext(bool backwards = false)
    {
        if (lastKeyword != Keyword.Text) RebuildMatches();
        if (positions.Count == 0) return;
        position = position < 0 ? backwards ? positions.Count - 1 : 0 : (position + (backwards ? -1 : 1) + positions.Count) % positions.Count;
        Text.Select(positions[position], lastKeyword.Length);
        Text.ScrollToLine(Text.GetLineIndexFromCharacterIndex(positions[position]));
        matches.Text = $"{position + 1}/{positions.Count}";
    }
    public async Task ReloadAsync()
    {
        if (busy || disposed) return; busy = true; refresh.IsEnabled = false;
        try
        {
            await session.DownloadAsync(RemotePath, false, Path.Combine(cache, "remote.logcompr"), new Progress<string>(s => { if (!disposed) state.Text = s; }), lifetime.Token);
            await CompressedLogDecoder.DecodeAsync(Path.Combine(AppContext.BaseDirectory, "tools", "LogDecoder", "BqLog_LogDecoder.exe"), Path.Combine(cache, "remote.logcompr"), Path.Combine(cache, "decoded.txt"),
                new LatestProgress(s => Interlocked.Exchange(ref pendingText, s)), lifetime.Token);
            FlushText(); state.Text = "已更新 " + DateTime.Now.ToString("HH:mm:ss") + " · 显示末尾最多 2 Mi 字符；导出完整文本。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) { state.Text = ex.Message; follow.IsChecked = false; } }
        finally { busy = false; refresh.IsEnabled = true; if (disposed) Cleanup(); }
    }
    public void ExportTo(string path)
    {
        if (busy || !File.Exists(Path.Combine(cache, "decoded.txt"))) throw new InvalidOperationException("请等待日志解码完成。");
        File.Copy(Path.Combine(cache, "decoded.txt"), path, true);
    }
    private void Export()
    {
        try
        {
            var save = new SaveFileDialog { Filter = "文本日志|*.log", FileName = Path.GetFileNameWithoutExtension(RemotePath) + ".log" };
            if (save.ShowDialog(Window.GetWindow(this)) == true) ExportTo(save.FileName);
        }
        catch (Exception ex) { state.Text = ex.Message; }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; followTimer.Stop(); repaint.Stop(); lifetime.Cancel(); if (!busy) Cleanup();
    }
    private void Cleanup()
    {
        try { foreach (string path in Directory.GetFiles(cache)) File.Delete(path); Directory.Delete(cache); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        lifetime.Dispose();
    }
    private sealed class LatestProgress(Action<string> update) : IProgress<string> { public void Report(string value) => update(value); }
}
