using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using RemoteInspector.Core;
using RemoteInspector.Protocol;
using Microsoft.Win32;

namespace RemoteInspector.Desktop;

public sealed record JankRow(PerformanceJank Event, MainStackResponse Frame = null, string BusinessState = null, int MatchCount = 1, long LastSampleUtc = 0, string WindowId = null)
{
    public string Time => DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(Event.utcMs, 0, 253402300799999)).ToLocalTime().ToString("HH:mm:ss.fff");
    public string Kind => Frame == null ? "卡顿事件" : "抓帧事件";
    public string DurationText => Frame == null ? Event.durationMs.ToString() : "—";
    public long Duration => Event.durationMs;
    public string ShortTime => Time[..8];
    public string State => BusinessState ?? (Frame != null ? Frame.success ? Frame.ageMs > 0 ? "缓存快照" : "已抓取" : "抓取失败" : Event.status switch { "ongoing" => "持续中", "recovered" => "已恢复", "interrupted" => "已中断", _ => Event.status });
    public string Status => Event.status switch { "ongoing" => "持续中（时长下限）", "recovered" => "已恢复", "interrupted" => "采集中断", _ => Event.status };
    public int Samples => Frame != null ? Frame.success ? MatchCount : 0 : Event.stacks?.Sum(s => s.sampleCount) ?? 0;
    public string Capture => Samples > 0 ? "已采样" : "无有效堆栈";
}

public partial class MainWindow
{
    public ObservableCollection<JankRow> JankRows { get; } = new();
    private string symbolDirectory = "", symbolTool = Path.Combine(AppContext.BaseDirectory, "tools", "Symbolizer", "llvm-symbolizer.exe");
    private string performanceRaw = "";
    private CancellationTokenSource symbolRequest;
    private Guid performanceScope;
    private long performanceVersion;
    private long lastJankRevision = -1;
    private readonly Dictionary<Guid, List<JankRow>> frameEvents = new();
    private bool updatingPerformanceRows;
    private void RefreshPerformance()
    {
        Guid scope = snapshot == null ? Selected?.Session.Id ?? Guid.Empty : Guid.Empty;
        if (scope != performanceScope)
        {
            performanceScope = scope; lastJankRevision = -1; performanceVersion++;
            symbolRequest?.Cancel(); JankCount.Text = "等待卡顿事件"; performanceRaw = ""; PerformanceStackText.Clear(); PerformanceStackInfo.Text = "选择卡顿记录查看堆栈"; JankRows.Clear();
        }
        if (snapshot != null || Selected == null) return;
        var session = Selected.Session;
        if (lastJankRevision == session.JankRevision) return;
        lastJankRevision = session.JankRevision;
        string selectedId = (JankGrid.SelectedItem as JankRow)?.Event.eventId;
        var values = session.GetJanks().Select(e => new JankRow(e)).Concat(frameEvents.GetValueOrDefault(session.Id) ?? new()).OrderByDescending(r => r.Frame != null).ThenByDescending(r => r.Frame != null ? r.MatchCount : 0).ThenByDescending(r => r.Event.utcMs).ToArray();
        updatingPerformanceRows = true;
        try {
        // Reconcile by identity rather than replacing shifted rows when a new capture arrives.
        for (int i = 0; i < values.Length; i++)
        {
            var desired = values[i];
            int existing = -1;
            for (int j = i; j < JankRows.Count; j++)
                if (JankRows[j].Event.eventId == desired.Event.eventId) { existing = j; break; }
            if (existing < 0) JankRows.Insert(i, desired);
            else
            {
                if (existing != i) JankRows.Move(existing, i);
                if (!ReferenceEquals(JankRows[i].Event, desired.Event) || JankRows[i] != desired) JankRows[i] = desired;
            }
        }
        while (JankRows.Count > values.Length) JankRows.RemoveAt(JankRows.Count - 1);
        if (selectedId != null) JankGrid.SelectedItem = JankRows.FirstOrDefault(r => r.Event.eventId == selectedId);
        } finally { updatingPerformanceRows = false; }
        if (selectedId != null && JankGrid.SelectedItem is JankRow active && (performanceSelectedEvent != active.Event || active.Frame != null && performanceSelectedMatches != active.MatchCount)) Jank_Selected(JankGrid, null);
        JankCount.Text = $"{values.Count(r => r.Frame == null)} 条卡顿 · {values.Count(r => r.Frame != null)} 条抓帧";
    }
    private PerformanceJank performanceSelectedEvent;
    private int performanceSelectedMatches;
    private async void Jank_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (JankGrid.SelectedItem is not JankRow row) return;
        if (updatingPerformanceRows) return;
        if (row.Frame is { } frame)
        {
            bool unchangedStack = performanceSelectedEvent == row.Event && performanceRaw == frame.stack;
            performanceSelectedEvent = row.Event; performanceSelectedMatches = row.MatchCount;
            string info = $"抓帧事件 · 命中 {row.MatchCount} 次 · {row.BusinessState} · 首次 {row.Time} · 末次 {DateTimeOffset.FromUnixTimeMilliseconds(row.LastSampleUtc > 0 ? row.LastSampleUtc : row.Event.utcMs).ToLocalTime():HH:mm:ss.fff} · 快照年龄 {frame.ageMs} ms · Build {frame.buildId} · {frame.architecture}" + (string.IsNullOrEmpty(frame.error) ? "" : " · " + CaptureError(frame.error));
            if (unchangedStack)
            {
                PerformanceStackInfo.Text = info + "\n符号须与游戏构建一致；当前协议未包含模块符号校验 ID。";
                return; // Count-only updates must not repeatedly re-index symbols or copy PDBs.
            }
            SetPerformanceStack(frame.success ? frame.stack : "抓帧失败：" + CaptureError(frame.error), info);
            if (frame.success) await ResolveCurrentStack();
            return;
        }
        performanceSelectedEvent = row.Event;
        var value = row.Event;
        var text = new StringBuilder();
        foreach (var stack in value.stacks ?? new())
            text.AppendLine($"[{stack.thread}] 采样 {stack.sampleCount} 次 · 首末偏移 {stack.firstSeenOffsetMs}—{stack.lastSeenOffsetMs} ms").AppendLine(stack.stack).AppendLine();
        SetPerformanceStack(text.Length == 0 ? "本次事件未取得有效堆栈。\n符号解析只能还原已采集地址，不能补回缺失的采样。" : text.ToString(),
            $"{row.Status} · {value.durationMs} ms · Build {value.buildId} · {value.architecture} · {CaptureReason(value.captureStatus, value.status)}");
        AutoCaptureStack.IsChecked = false; dynamicStackRequest?.Cancel();
        if (text.Length > 0) await ResolveCurrentStack();
    }
    private static string CaptureReason(string capture, string state) => capture switch
    {
        "samples_available" => "已取得有效采样",
        "sampling_pending" => state == "ongoing" ? "等待下一次采样" : "采样开始前事件已结束",
        "native_capture_empty" => "采样已尝试，但采样器未返回有效堆栈",
        "sampler_unavailable" => "采样器不可用：检查原生插件加载、ABI 和主线程注册",
        "sampling_budget_exhausted" => "本分钟采样预算已耗尽",
        "recovered_before_sample" => "采样期间主线程已恢复，丢弃恢复后的样本",
        "native_disabled_or_custom_sampler_empty" => "原生采样未开启，或自定义采样未返回堆栈",
        "watchdog_disabled" => "Watchdog 未开启，仅记录帧耗时",
        "no_valid_samples" when state == "ongoing" => "当前快照尚无有效样本，等待后续采样或结束状态",
        "no_valid_samples" => "未取得有效样本：旧 SDK 未提供失败细因",
        _ => capture ?? "采样状态未知"
    };
    private static string CaptureError(string error) => error switch
    {
        "UnsupportedOrNotInitialized" => "当前平台不支持即时抓栈，或 Performance SDK 尚未初始化（支持 Windows x64 / Android arm64 / iOS arm64，需更新原生插件）",
        "CaptureBusy" => "采样器正忙，请稍后重试",
        "RateLimited" => "抓取过于频繁，请至少间隔 1 秒",
        "MustCallFromWorkerThread" => "远端错误地在主线程调用抓栈接口",
        "NativeCaptureFailed" => "原生采样失败，检查插件与目标线程状态",
        "NoSnapshotAvailable" => "当前没有可用堆栈快照",
        _ => error ?? "未知原因"
    };
    private void SetPerformanceStack(string raw, string info)
    {
        performanceVersion++; symbolRequest?.Cancel(); performanceRaw = raw ?? "";
        PerformanceResolveStatus.Text = "";
        PerformanceStackText.Text = performanceRaw; PerformanceStackInfo.Text = info + "\n符号须与游戏构建一致；当前协议未包含模块符号校验 ID。";
    }
    private async void CaptureMainStack_Click(object sender, RoutedEventArgs e)
    {
        if (dynamicStackBusy) { PerformanceResolveStatus.Text = "正在远程抓栈，请等待本次完成。"; return; }
        await Run(token => CaptureBusinessWindow(token));
    }
    private bool businessWindowActive;
    private async Task CaptureBusinessWindow(CancellationToken token)
    {
        businessWindowActive = true;
        try
        {
            var session = Live();
            int seconds = BusinessWindowLength.SelectedIndex == 1 ? 10 : BusinessWindowLength.SelectedIndex == 2 ? 20 : 5;
            await session.RunBusinessSamplingAsync(seconds * 1000, async batch =>
            {
                if (!IsCurrent(session) || token.IsCancellationRequested) return;
                if (!frameEvents.TryGetValue(session.Id, out var queue)) frameEvents[session.Id] = queue = new();
                foreach (var sample in batch.samples)
                {
                    var result = new MainStackResponse { success = true, stack = "[" + sample.thread + "]\n" + sample.stack, timestamp = sample.timestamp, ageMs = 0, buildId = batch.buildId, architecture = batch.architecture };
                    string state = sample.stack?.Contains("GameAssembly.dll+", StringComparison.OrdinalIgnoreCase) == true || sample.stack?.Contains("libil2cpp.so+", StringComparison.OrdinalIgnoreCase) == true || sample.stack?.Contains("UnityFramework+", StringComparison.OrdinalIgnoreCase) == true ? "业务候选 · 选择后解析" : "未命中业务执行";
                    int same = queue.FindIndex(r => r.WindowId == batch.sessionId && r.Frame.stack == result.stack);
                    if (same >= 0) queue[same] = queue[same] with { MatchCount = queue[same].MatchCount + 1, LastSampleUtc = sample.timestamp };
                    else queue.Add(new JankRow(new PerformanceJank { eventId = "frame-" + Guid.NewGuid().ToString("N"), utcMs = sample.timestamp }, result, state, 1, sample.timestamp, batch.sessionId));
                    if (queue.Count > 200) queue.RemoveAt(0);
                }
                lastJankRevision = -1; RefreshPerformance();
                if (JankGrid.SelectedItem == null && queue.Count > 0) JankGrid.SelectedItem = JankRows.First(r => r.Event.eventId == queue.OrderByDescending(r => r.MatchCount).First().Event.eventId);
                StatusLabel.Text = $"SDK业务窗口 · 尝试 {batch.attempts} · 成功 {batch.captured} · 采样跳过 {batch.dropped} · 传输丢弃 {batch.transportDropped}" + (batch.completed ? " · 已结束" : " · 采集中") + (string.IsNullOrEmpty(batch.error) ? "" : " · " + batch.error);
                await Task.CompletedTask;
            }, token);
        }
        finally { businessWindowActive = false; }
    }
    private async Task CaptureRemoteStack(CancellationToken token)
    {
        var session = Live();
        var packet = await session.RequestAsync((RemoteInspector.Commands.Command)PerformanceProtocol.MainStack, new RequestBase(), token, 10,
            (RemoteInspector.Commands.Command)PerformanceProtocol.MainStackResponse);
        var result = Wire.Body<MainStackResponse>(packet);
        if (!IsCurrent(session) || token.IsCancellationRequested) return;
        if (!frameEvents.TryGetValue(session.Id, out var queue)) frameEvents[session.Id] = queue = new();
        string business = !result.success ? "抓取失败" : result.stack?.Contains("GameAssembly.dll+", StringComparison.OrdinalIgnoreCase) == true || result.stack?.Contains("libil2cpp.so+", StringComparison.OrdinalIgnoreCase) == true || result.stack?.Contains("UnityFramework+", StringComparison.OrdinalIgnoreCase) == true ? "业务候选 · 待符号确认" : "未命中业务执行";
        if (business.StartsWith("业务候选") && Directory.Exists(symbolDirectory) && File.Exists(symbolTool))
        {
            string resolved = await new StackSymbolizer().ResolveAsync(result.stack, symbolDirectory, symbolTool, token);
            if (resolved.Contains("IL2CPP 业务方法：", StringComparison.Ordinal)) business = "命中 IL2CPP 方法";
            else business = "未确认业务方法";
        }
        if (!IsCurrent(session) || token.IsCancellationRequested) return;
        var item = new JankRow(new PerformanceJank { eventId = "frame-" + Guid.NewGuid().ToString("N"), utcMs = result.timestamp > 0 ? result.timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, result, business);
        queue.Add(item); if (queue.Count > 200) queue.RemoveAt(0);
        lastJankRevision = -1; RefreshPerformance();
        // New captures never replace the evidence currently being inspected.
        if (JankGrid.SelectedItem == null)
            JankGrid.SelectedItem = JankRows.First(r => r.Event.eventId == item.Event.eventId);
    }

    private CancellationTokenSource dynamicStackRequest;
    private bool dynamicStackBusy;
    private DateTime lastDynamicStack;
    private async Task PollDynamicStack()
    {
        if (AutoCaptureStack?.IsChecked != true || closing || !FpsTab.IsSelected || snapshot != null || Selected?.Session.IsConnected != true) { dynamicStackRequest?.Cancel(); return; }
        int interval = CaptureStackInterval.SelectedIndex == 0 ? 1 : CaptureStackInterval.SelectedIndex == 2 ? 5 : 2;
        if (dynamicStackBusy || operation != null || DateTime.UtcNow - lastDynamicStack < TimeSpan.FromSeconds(interval)) return;
        dynamicStackBusy = true; lastDynamicStack = DateTime.UtcNow;
        using var request = new CancellationTokenSource(); dynamicStackRequest = request;
        try { await CaptureBusinessWindow(request.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closing) PerformanceResolveStatus.Text = "远程抓栈失败：" + ex.Message; }
        finally { if (ReferenceEquals(dynamicStackRequest, request)) dynamicStackRequest = null; dynamicStackBusy = false; }
    }
    private void PerformanceViewMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TrendPanel == null || JankWorkspace == null) return;
        int mode = PerformanceViewMode.SelectedIndex;
        TrendPanel.Visibility = mode == 2 ? Visibility.Collapsed : Visibility.Visible;
        JankWorkspace.Visibility = mode == 1 ? Visibility.Collapsed : Visibility.Visible;
        TrendChart.Height = mode == 1 ? 240 : 115;
        TrendHeight.Height = mode == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }
    private async void ResolvePerformanceStack_Click(object sender, RoutedEventArgs e) => await ResolveCurrentStack();
    private async Task ResolveCurrentStack()
    {
        if (string.IsNullOrWhiteSpace(performanceRaw)) return;
        symbolRequest?.Cancel(); using var request = new CancellationTokenSource(); symbolRequest = request;
        long version = performanceVersion; string raw = performanceRaw;
        try
        {
            PerformanceResolveStatus.Text = "解析中…";
            string result = await Task.Run(() => new StackSymbolizer().ResolveAsync(raw, symbolDirectory, symbolTool, request.Token));
            if (version == performanceVersion && !closing) { PerformanceStackText.Text = result; PerformanceResolveStatus.Text = result.Split('\n')[0]; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == performanceVersion && !closing) PerformanceResolveStatus.Text = ex.Message; }
        finally { if (ReferenceEquals(symbolRequest, request)) symbolRequest = null; }
    }
    private void RawPerformanceStack_Click(object sender, RoutedEventArgs e) { performanceVersion++; symbolRequest?.Cancel(); PerformanceStackText.Text = performanceRaw; }
    private void CopyPerformanceStack_Click(object sender, RoutedEventArgs e)
    { try { if (PerformanceStackText.Text.Length > 0) Clipboard.SetText(PerformanceStackInfo.Text + "\n" + PerformanceStackText.Text); } catch (Exception ex) { StatusLabel.Text = ex.Message; } }
    private void EnlargePerformanceStack_Click(object sender, RoutedEventArgs e)
    {
        var panel = new DockPanel { Margin = new Thickness(14), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 29, 41)) };
        var toolbar = new WrapPanel(); DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar);
        foreach (var item in new (string Label, RoutedEventHandler Handler)[] { ("解析堆栈", ResolvePerformanceStack_Click), ("原始堆栈", RawPerformanceStack_Click), ("复制", CopyPerformanceStack_Click), ("导出", ExportPerformanceStack_Click) })
        { var button = new Button { Content = item.Label, Style = (Style)FindResource("PerformanceButton") }; button.Click += item.Handler; toolbar.Children.Add(button); }
        var info = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5, 8, 5, 12), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(145, 169, 195)) }; info.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text") { Source = PerformanceStackInfo }); DockPanel.SetDock(info, Dock.Top); panel.Children.Add(info);
        var text = new TextBox { Style = (Style)FindResource("PerformanceInput"), IsReadOnly = true, AcceptsReturn = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        text.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("Text") { Source = PerformanceStackText, Mode = System.Windows.Data.BindingMode.OneWay }); panel.Children.Add(text);
        new Window { Style = (Style)FindResource(typeof(Window)), Title = "性能堆栈详情", Owner = this, Width = 980, Height = 650, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Show();
    }
    private void ExportPerformanceStack_Click(object sender, RoutedEventArgs e)
    {
        var save = new SaveFileDialog { Filter = "文本|*.txt", FileName = "performance-stack.txt" };
        if (save.ShowDialog(this) != true) return;
        try { File.WriteAllText(save.FileName, PerformanceStackInfo.Text + "\n" + PerformanceStackText.Text + "\n\n原始堆栈\n" + performanceRaw); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    private void ClearJanks_Click(object sender, RoutedEventArgs e)
    { Selected?.Session.ClearJanks(); if (Selected != null) frameEvents.Remove(Selected.Session.Id); lastJankRevision = -1; RefreshPerformance(); SetPerformanceStack("", "已清空本机会话卡顿及抓帧记录"); }
    private void PerformanceSettings_Click(object sender, RoutedEventArgs e)
    {
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = "IL2CPP 符号目录（同构建、同架构）", Margin = new Thickness(3) });
        var dir = new TextBox { Text = symbolDirectory, Style = (Style)FindResource("PerformanceInput") }; root.Children.Add(dir);
        var browse = new Button { Content = "选择符号目录", HorizontalAlignment = HorizontalAlignment.Left };
        browse.Click += (_, _) => { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) dir.Text = dialog.FolderName; }; root.Children.Add(browse);
        root.Children.Add(new TextBlock { Text = "llvm-symbolizer.exe", Margin = new Thickness(3) });
        var tool = new TextBox { Text = symbolTool, Style = (Style)FindResource("PerformanceInput") }; root.Children.Add(tool);
        var choose = new Button { Content = "选择解析工具", HorizontalAlignment = HorizontalAlignment.Left };
        choose.Click += (_, _) => { var dialog = new OpenFileDialog { Filter = "符号解析工具|llvm-symbolizer.exe|可执行文件|*.exe" }; if (dialog.ShowDialog(this) == true) tool.Text = dialog.FileName; }; root.Children.Add(choose);
        root.Children.Add(new TextBlock { Text = "Windows：GameAssembly.dll 与匹配 PDB 放在同一目录。\nAndroid：使用包含调试符号的 libil2cpp.so / .so.debug。\niOS：选择 dSYM 目录，自动查找 DWARF / UnityFramework。\n目录内不要混放多个构建或架构。原始地址始终保留。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 10, 3, 10) });
        var window = new Window { Style = (Style)FindResource(typeof(Window)), Title = "性能与符号设置", Owner = this, Width = 640, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root };
        var save = new Button { Content = "保存", HorizontalAlignment = HorizontalAlignment.Right }; root.Children.Add(save);
        save.Click += (_, _) => { symbolDirectory = dir.Text.Trim(); symbolTool = tool.Text.Trim(); window.Close(); };
        window.ShowDialog();
    }
}
