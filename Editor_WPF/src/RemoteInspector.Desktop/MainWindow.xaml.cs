using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RemoteInspector.Commands;
using RemoteInspector.Core;
using RemoteInspector.Core.Models;
using RemoteInspector.Protocol;

namespace RemoteInspector.Desktop;

public partial class MainWindow : Window
{
    private readonly InspectorHost host = new();
    private readonly ObservableCollection<SessionView> sessions = new();
    private readonly ConcurrentQueue<(Guid Session, LogMessage Log)> incomingLogs = new();
    private readonly ConcurrentQueue<string> diagnostics = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private CancellationTokenSource operation;
    private SnapshotData snapshot;
    private NodeTreeResponse renderedTree;
    private readonly HashSet<int> snapshotExpanded = new();
    private NodeDetailResponse detail;
    private Guid detailSession;
    private int selectedNode;
    private byte[] imageBytes;
    private readonly ObservableCollection<Picture> pictures = new();
    private DateTime lastStatsPoll;
    private bool closing;
    private bool closeAllowed;
    private bool stoppingListener;
    private EnumCatalog enumCatalog = new();
    private LayerCatalog layerCatalog = new();
    private readonly DispatcherTimer treeSearchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string treeQuery = "";
    private bool filteringTree;
    private HashSet<int> browseExpandedBeforeFilter;
    private readonly HashSet<int> filterExpanded = new();
    private double browseScrollBeforeFilter;
    private int? selectedInFilter;
    private TreeProjection treeProjection;
    private long treeRenderVersion;
    private bool restoringTree;
    private CancellationTokenSource selectionRequest;
    private readonly HashSet<string> collapsedPropertyGroups = new();
    private (Guid Session, int Node) propertyGroupScope;
    private readonly string settingsPath;
    public BatchObservableCollection<PropertyRow> PropertyRows { get; } = new();
    public ObservableCollection<LogRow> Logs { get; } = new();
    public ObservableCollection<FrameRow> FrameRows { get; } = new();
    public ObservableCollection<PathCrumb> Breadcrumbs { get; } = new();
    private SessionView Selected => SessionsBox.SelectedItem as SessionView;

    public MainWindow() : this(null) { }

    public MainWindow(string settingsFile)
    {
        settingsPath = settingsFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuperMain", "RemoteInspector", "settings.json");
        InitializeComponent();
        DataContext = this;
        var properties = CollectionViewSource.GetDefaultView(PropertyRows);
        properties.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PropertyRow.ComponentGroup)));
        properties.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PropertyRow.Section)));
        properties.Filter = FilterProperty;
        SessionsBox.ItemsSource = sessions;
        ImageHistory.ItemsSource = pictures;
        CollectionViewSource.GetDefaultView(Logs).Filter = FilterLog;
        host.SessionAdded += OnSessionAdded;
        host.Diagnostic += message => { if (diagnostics.Count < 100) diagnostics.Enqueue(message); };
        timer.Tick += Tick;
        timer.Start();
        InitializeDebugTools();
        InitializeRpc();
        treeSearchDelay.Tick += (_, _) => { treeSearchDelay.Stop(); ApplyTreeQuery(); };
        UpdateBreadcrumbs("");
        try
        {
            enumCatalog = EnumCatalog.Load(Path.Combine(AppContext.BaseDirectory, "enum-values.json"));
            layerCatalog = LayerCatalog.Load(Path.Combine(AppContext.BaseDirectory, "layers.json"));
            if (File.Exists(settingsPath))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath));
                // Listening starts at the standard port each launch; saved test/custom ports are not reused.
                PortBox.Text = "9555";
                if (settings != null)
                {
                    foreach (var pair in settings.RpcTemplates ?? new()) rpcTemplates[pair.Key] = pair.Value;
                    double treeRatio = double.IsFinite(settings.TreeRatio) ? Math.Clamp(settings.TreeRatio, 0.18, 0.45) : 0.3;
                    TreeWidthColumn.Width = new GridLength(treeRatio, GridUnitType.Star); InspectorWidthColumn.Width = new GridLength(1 - treeRatio, GridUnitType.Star);
                    double logRatio = double.IsFinite(settings.DiagnosticsRatio) ? Math.Clamp(settings.DiagnosticsRatio, 0.25, 0.65) : 0.4;
                    DiagnosticsWidthColumn.Width = new GridLength(logRatio, GridUnitType.Star); FilesWidthColumn.Width = new GridLength(1 - logRatio, GridUnitType.Star);
                    ToolsHeight.Height = new GridLength(double.IsFinite(settings.ToolsHeight) ? Math.Clamp(settings.ToolsHeight, 215, Math.Max(215, Height - 520)) : 290);
                }
            }
        }
        catch (Exception ex) { StatusLabel.Text = "配置读取失败，使用默认端口：" + ex.Message; }
    }

    private void OnSessionAdded(InspectorSession session)
    {
        int initialized = 0;
        session.PacketReceived += (sender, packet) =>
        {
            if (packet.GetCommand() == Command.CLIENT_HELLO && Interlocked.Exchange(ref initialized, 1) == 0)
                Dispatcher.BeginInvoke(new Action(() => _ = RefreshConnectedTree(session)));
            if (packet.GetCommand() != Command.LOG_MESSAGE) return;
            if (incomingLogs.Count >= 10000) incomingLogs.TryDequeue(out _);
            using var json = JsonDocument.Parse(packet.bodyJson);
            if (json.RootElement.TryGetProperty("entries", out _))
            {
                foreach (var log in Wire.Body<LogBatchMessage>(packet).entries ?? Array.Empty<LogMessage>())
                    if (incomingLogs.Count < 10000) incomingLogs.Enqueue((sender.Id, log));
            }
            else incomingLogs.Enqueue((sender.Id, Wire.Body<LogMessage>(packet)));
        };
        Dispatcher.BeginInvoke(() =>
        {
            if (closing) return;
            sessions.Add(new SessionView(session));
            if (SessionsBox.SelectedItem == null) SessionsBox.SelectedIndex = 0;
            StatusLabel.Text = "游戏已连接：" + session.Endpoint;
        });
    }

    private async Task RefreshConnectedTree(InspectorSession session)
    {
        if (closing || !session.IsConnected) return;
        try { await session.RefreshTreeAsync(); if (!closing && IsCurrent(session)) { RenderTree(session.Tree); StatusLabel.Text = "连接成功，场景节点树已自动刷新。"; } }
        catch (Exception ex) { if (!closing && IsCurrent(session)) StatusLabel.Text = "自动刷新节点树失败，可点击刷新重试：" + ex.Message; }
    }

    private async void Tick(object sender, EventArgs e)
    {
        foreach (var view in sessions) view.RefreshName();
        if (operation != null) OperationLabel.Text = $"{currentOperationName} · {(DateTime.UtcNow - operationStarted).TotalSeconds:F1}s";
        while (diagnostics.TryDequeue(out var message)) StatusLabel.Text = message;
        bool appendedLogs = false;
        int budget = 500;
        while (budget-- > 0 && incomingLogs.TryDequeue(out var item))
        {
            var view = sessions.FirstOrDefault(v => v.Session.Id == item.Session);
            if (view == null) continue;
            string time;
            try { time = DateTimeOffset.FromUnixTimeMilliseconds(item.Log.timestamp).ToLocalTime().ToString("HH:mm:ss.fff"); }
            catch { time = "--"; }
            var row = new LogRow(time, item.Log.logType, item.Log.message + (string.IsNullOrEmpty(item.Log.stackTrace) ? "" : "\n" + item.Log.stackTrace));
            view.Logs.Add(row);
            if (view.Logs.Count > 5000) view.Logs.RemoveAt(0);
            if (view == Selected && snapshot == null)
            {
                Logs.Add(row); appendedLogs = true;
                if (Logs.Count > 5000) Logs.RemoveAt(0);
            }
        }
        var selected = Selected;
        if (appendedLogs && FollowLogs.IsChecked == true && LogsGrid.Items.Count > 0) LogsGrid.ScrollIntoView(LogsGrid.Items[LogsGrid.Items.Count - 1]);
        if (TreeSourceLabel != null)
            TreeSourceLabel.Text = snapshot != null ? "离线快照 · 状态来自快照采集时刻" :
                selected == null ? "等待游戏连接" : !selected.Session.IsConnected ? "会话已断开 · 显示最后一次采集状态" :
                renderedTree == null ? "等待采集节点树" : "激活状态以最近一次节点树采集为准";
        if (snapshot != null || selected == null) return;
        await AutoRefreshDetail();
        await PollNodeWatches();
        var s = selected.Session;
        if (s.Tree != null && !ReferenceEquals(renderedTree, s.Tree)) RenderTree(s.Tree);
        if (s.Stats != null && !ReferenceEquals(selected.LastStats, s.Stats))
        {
            selected.LastStats = s.Stats;
            var stats = s.Stats;
            FrameRows.Insert(0, new FrameRow(DateTime.Now.ToString("HH:mm:ss"), stats.fps, stats.avgFrameTime, stats.minFrameTime, stats.maxFrameTime));
            if (FrameRows.Count > 180) FrameRows.RemoveAt(FrameRows.Count - 1);
            DrawFps();
            StatsLabel.Text = $"{s.DisplayName}   FPS {stats.fps:F1}   平均 {stats.avgFrameTime:F2} ms   节点 {s.Tree?.allNodes?.Count ?? 0}";
        }
        if (AutoStats.IsChecked == true && s.IsConnected && DateTime.UtcNow - lastStatsPoll > TimeSpan.FromSeconds(1))
        {
            lastStatsPoll = DateTime.UtcNow;
            try { await s.SendAsync(Command.GET_FRAME_STATS, new GetFrameStatsRequest()); }
            catch (Exception ex) { StatusLabel.Text = ex.Message; }
        }
    }

    private async Task Run(Func<CancellationToken, Task> action, [System.Runtime.CompilerServices.CallerMemberName] string source = "")
    {
        if (closing) return;
        if (operation != null) { StatusLabel.Text = "已有操作正在进行，可先取消。"; return; }
        autoDetailRequest?.Cancel();
        using var current = new CancellationTokenSource();
        operation = current;
        var started = DateTime.UtcNow; operationStarted = started;
        currentOperationName = OperationName(source);
        OperationLabel.Text = currentOperationName + "…";
        try { await action(current.Token); }
        catch (OperationCanceledException) { StatusLabel.Text = "操作已取消或响应超时。"; }
        catch (Exception ex) { StatusLabel.Text = "操作失败：" + ex.Message; }
        finally { operation = null; OperationLabel.Text = $"上次操作 {(DateTime.UtcNow - started).TotalSeconds:F1}s"; }
    }
    private InspectorSession Live()
    {
        if (snapshot != null) throw new InvalidOperationException("当前是离线快照，请先返回实时会话。");
        return Selected?.Session is { IsConnected: true } s ? s : throw new InvalidOperationException("请选择已连接的游戏。");
    }
    private bool IsCurrent(InspectorSession session) => snapshot == null && Selected?.Session == session;
    private void Cancel_Click(object sender, RoutedEventArgs e)
    { operation?.Cancel(); selectionRequest?.Cancel(); autoDetailRequest?.Cancel(); }

    private async void Listen_Click(object sender, RoutedEventArgs e)
    {
        if (closing || stoppingListener) return;
        if (host.IsRunning)
        {
            stoppingListener = true; ListenButton.IsEnabled = false;
            operation?.Cancel(); selectionRequest?.Cancel();
            try { await host.StopAsync(); ListenButton.Content = "启动监听"; StatusLabel.Text = "监听已停止。"; }
            catch (Exception ex) { StatusLabel.Text = "停止监听失败：" + ex.Message; }
            finally { stoppingListener = false; if (!closing) ListenButton.IsEnabled = true; }
            return;
        }
        await Run(token =>
        {
            if (!int.TryParse(PortBox.Text, out int port) || port < 1 || port > 65535) throw new ArgumentException("端口范围为 1—65535。");
            host.Start(port);
            ListenButton.Content = "停止监听";
            StatusLabel.Text = $"正在监听 0.0.0.0:{port}；游戏执行 /connect 本机IP {port}。";
            return Task.CompletedTask;
        });
    }
    private void Disconnect_Click(object sender, RoutedEventArgs e)
    { try { Live().Disconnect(); StatusLabel.Text = "所选会话已断开。"; } catch (Exception ex) { StatusLabel.Text = ex.Message; } }
    private void Session_Changed(object sender, SelectionChangedEventArgs e)
    {
        ClearRemoteLog();
        nodeWatchRequest?.Cancel();
        autoDetailRequest?.Cancel();
        selectionRequest?.Cancel(); treeSearchDelay.Stop(); filteringTree = false; treeQuery = ""; SearchBox.Clear();
        browseExpandedBeforeFilter = null; filterExpanded.Clear(); selectedInFilter = null;
        LockInspector.IsChecked = false; MonitorRows.Clear();
        ResetNavigation();
        operation?.Cancel(); snapshot = null; renderedTree = null; detail = null; selectedNode = 0;
        PublishNodeWatches();
        PickResults.ItemsSource = null; PickResultsPanel.Visibility = Visibility.Collapsed;
        PropertyRows.Clear(); NodeTree.ItemsSource = null; Logs.Clear(); FrameRows.Clear(); RawDetail.Clear();
        LogDetails.Clear(); LogDetails.Visibility = Visibility.Collapsed;
        ResetRpc(); FilesGrid.ItemsSource = null; PersistentPath.Text = "";
        RemotePath.Clear(); UpdateBreadcrumbs("");
        imageBytes = null; PreviewImage.Source = null; ImageInfo.Text = ""; pictures.Clear(); DrawFps();
        SnapshotInfo.Text = "当前：实时模式";
        NodeLabel.Text = "选择节点查看组件";
        StatsLabel.Text = Selected?.Session.DisplayName ?? "选择会话";
        if (Selected is { } view)
        {
            selectedNode = view.SelectedNode;
            foreach (var log in view.Logs) Logs.Add(log);
            if (view.Session.Tree != null) RenderTree(view.Session.Tree);
            if (view.Session.Details.TryGetValue(selectedNode, out var cached)) ShowDetail(cached, view.Session.Id);
            view.LastStats = null;
            var target = view.Session;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
            { if (IsCurrent(target) && target.IsConnected && !closing && operation == null) await Run(t => Browse(target, "", t)); }));
        }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Run(async token =>
    { var s = Live(); await s.RefreshTreeAsync(token); if (IsCurrent(s)) { RenderTree(s.Tree); StatusLabel.Text = "节点树已刷新。"; } });
    private async void Control_Click(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        var s = Live(); var command = Enum.Parse<Command>((string)((Button)sender).Tag);
        await s.RequestAsync(command, new RequestBase(), token, 15, command);
        StatusLabel.Text = command == Command.PAUSE_CLIENT ? "游戏已暂停。" : "游戏已恢复。";
    });

    private void RenderTree(NodeTreeResponse tree)
    {
        double scroll = TreeScroll()?.VerticalOffset ?? 0;
        var anchor = CaptureTreeAnchor();
        renderedTree = tree;
        var expanded = filteringTree ? filterExpanded : BrowseExpanded();
        var projection = new TreeProjection(tree, treeQuery, TreeStateFilter.SelectedIndex, expanded, selectedNode);
        treeProjection = projection;
        restoringTree = true;
        NodeTree.ItemsSource = projection.Roots;
        TreeCount.Text = $"{tree.sceneName} · 总计 {projection.Nodes.Count:N0}\n激活 {projection.ActiveCount:N0} · 未激活 {projection.InactiveCount:N0} · 命中 {projection.MatchCount:N0}";
        RestoreTreeView(anchor?.Id, scroll, anchor?.Y);
    }
    private HashSet<int> BrowseExpanded() => snapshot == null ? Selected?.Expanded ?? new HashSet<int>() : snapshotExpanded;
    private ScrollViewer TreeScroll() => VisualChildren<ScrollViewer>(NodeTree).FirstOrDefault();
    private void TreeSearch_Changed(object sender, TextChangedEventArgs e)
    { if (DataContext == null) return; treeSearchDelay.Stop(); treeSearchDelay.Start(); }
    private void ApplyTreeQuery()
    {
        treeSearchDelay.Stop();
        if (renderedTree == null) return;
        bool next = !string.IsNullOrWhiteSpace(SearchBox.Text) || TreeStateFilter.SelectedIndex > 0;
        bool leaving = filteringTree && !next;
        if (!filteringTree && next)
        {
            browseExpandedBeforeFilter = new HashSet<int>(BrowseExpanded());
            filterExpanded.Clear(); filterExpanded.UnionWith(browseExpandedBeforeFilter);
            browseScrollBeforeFilter = TreeScroll()?.VerticalOffset ?? 0;
            selectedInFilter = null;
        }
        if (leaving)
        {
            var expanded = BrowseExpanded(); expanded.Clear();
            expanded.UnionWith(browseExpandedBeforeFilter ?? new HashSet<int>());
        }
        filteringTree = next; treeQuery = SearchBox.Text.Trim();
        RenderTree(renderedTree);
        if (leaving) RestoreTreeView(selectedInFilter, browseScrollBeforeFilter);
        else if (next && !string.IsNullOrWhiteSpace(treeQuery)) RestoreTreeView(treeProjection.Matches.Contains(selectedNode) ? selectedNode : treeProjection.Matches.FirstOrDefault(), 0);
    }
    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchOrLocate();
    private async Task SearchOrLocate()
    {
        string path = SearchBox.Text.Trim();
        var matches = renderedTree?.allNodes?.Where(n => string.Equals(n.path, path, StringComparison.Ordinal)).ToArray();
        if (path.Length > 0 && matches?.Length == 1) await NavigateNode(matches[0].instanceId);
        else { ApplyTreeQuery(); if (matches?.Length > 1) StatusLabel.Text = "存在同路径节点，请在搜索结果中选择。"; }
    }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); ApplyTreeQuery(); SearchBox.Focus(); }
    private void TreeStateFilter_Changed(object sender, SelectionChangedEventArgs e) { if (DataContext != null) ApplyTreeQuery(); }
    private async void Search_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { e.Handled = true; ClearSearch_Click(sender, e); } else if (e.Key == Key.Enter) { e.Handled = true; await SearchOrLocate(); } }
    private (int Id, double Y)? CaptureTreeAnchor()
    {
        var rows = VisualChildren<TreeViewItem>(NodeTree)
            .Where(c => c.IsVisible && c.DataContext is TreeRow { Node: not null })
            .Select(c => (Item: c, Y: c.TransformToAncestor(NodeTree).Transform(new Point()).Y))
            .Where(c => c.Y >= 0 && c.Y < NodeTree.ActualHeight).ToArray();
        var anchor = rows.FirstOrDefault(c => ((TreeRow)c.Item.DataContext).Node.instanceId == selectedNode);
        if (anchor.Item == null) anchor = rows.OrderBy(c => c.Y).FirstOrDefault();
        return anchor.Item == null ? null : (((TreeRow)anchor.Item.DataContext).Node.instanceId, anchor.Y);
    }
    private void Tree_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    { if (restoringTree) e.Handled = true; }
    private void RestoreTreeView(int? reveal, double offset, double? anchorY = null)
    {
        long version = ++treeRenderVersion;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            try
            {
                if (version != treeRenderVersion || closing) return;
                NodeTree.UpdateLayout();
                if (reveal.HasValue && treeProjection.Nodes.ContainsKey(reveal.Value))
                {
                    var path = new List<int> { reveal.Value }; var seen = new HashSet<int>(path);
                    int current = reveal.Value;
                    while (treeProjection.Parents.TryGetValue(current, out int parent) && seen.Add(parent)) { path.Add(parent); current = parent; }
                    path.Reverse();
                    ItemsControl owner = NodeTree;
                    foreach (int id in path)
                    {
                        if (version != treeRenderVersion || closing) return;
                        var row = owner.Items.OfType<TreeRow>().FirstOrDefault(r => r.Node?.instanceId == id);
                        if (row == null) break;
                        owner.ApplyTemplate(); owner.UpdateLayout();
                        var panel = VisualChildren<VirtualizingStackPanel>(owner).FirstOrDefault(p => ItemsControl.GetItemsOwner(p) == owner);
                        panel?.BringIndexIntoViewPublic(owner.Items.IndexOf(row));
                        await Dispatcher.Yield(DispatcherPriority.Loaded);
                        if (version != treeRenderVersion || closing) return;
                        if (owner.ItemContainerGenerator.ContainerFromItem(row) is not TreeViewItem container) break;
                        if (id == reveal.Value)
                        {
                            var scroll = TreeScroll();
                            if (anchorY.HasValue)
                            {
                                NodeTree.UpdateLayout();
                                double y = container.TransformToAncestor(NodeTree).Transform(new Point()).Y;
                                scroll?.ScrollToVerticalOffset(scroll.VerticalOffset + y - anchorY.Value);
                            }
                            else { restoringTree = false; container.BringIntoView(); }
                            return;
                        }
                        row.IsExpanded = true; container.IsExpanded = true; row.Load(); owner = container;
                    }
                }
                TreeScroll()?.ScrollToVerticalOffset(offset);
            }
            catch (Exception ex) { diagnostics.Enqueue("节点定位：" + ex.Message); }
            finally { if (version == treeRenderVersion) restoringTree = false; }
        }));
    }
    private void Tree_Expanded(object sender, RoutedEventArgs e) { if (e.OriginalSource is TreeViewItem { DataContext: TreeRow row }) row.Load(); }
    private async void Tree_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeRow { Node: not null } row) return;
        if (restoringTree || LockInspector.IsChecked == true) return;
        bool retained = selectedNode == row.Node.instanceId && detail?.instanceId == selectedNode &&
            (snapshot != null || detailSession == Selected?.Session.Id);
        autoDetailRequest?.Cancel();
        selectedNode = row.Node.instanceId;
        RecordNavigation(selectedNode);
        if (filteringTree) selectedInFilter = selectedNode;
        if (snapshot == null && Selected != null) Selected.SelectedNode = selectedNode;
        if (retained) return;
        if (snapshot != null)
        {
            if (snapshot.NodeDetails.TryGetValue(selectedNode, out var cached)) ShowDetail(cached, Guid.Empty);
            else { detail = null; PropertyRows.Clear(); RawDetail.Clear(); NodeLabel.Text = row.Path + "（快照未采集详情）"; }
            return;
        }
        selectionRequest?.Cancel();
        using var request = new CancellationTokenSource(); selectionRequest = request;
        try
        {
            var s = Live(); int id = selectedNode;
            await s.GetDetailAsync(id, request.Token);
            if (IsCurrent(s) && id == selectedNode && !request.IsCancellationRequested) ShowDetail(s.Details[id], s.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closing) StatusLabel.Text = "详情读取失败：" + ex.Message; }
        finally { if (selectionRequest == request) selectionRequest = null; }
    }
    private async void Detail_Click(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        var s = Live(); int id = selectedNode;
        if (id == 0) throw new InvalidOperationException("请先选择节点。");
        await s.GetDetailAsync(id, token);
        if (IsCurrent(s) && id == selectedNode) ShowDetail(s.Details[id], s.Id);
    });

    private static string Format(double value) => value.ToString("G9", CultureInfo.InvariantCulture);
    private static string Vector(params float[] values) => string.Join(", ", values.Select(v => v.ToString("G7", CultureInfo.InvariantCulture)));
    private void ShowDetail(NodeDetailResponse data, Guid sessionId)
    {
        bool newScope = propertyGroupScope != (sessionId, data.instanceId);
        if (newScope)
        { collapsedPropertyGroups.Clear(); propertyGroupScope = (sessionId, data.instanceId); }
        detail = data; detailSession = sessionId;
        UpdateNodeIdentity();
        NodeLabel.Text = $"{data.name}  [{data.instanceId}]" + (snapshot == null ? "" : " · 离线只读");
        RawDetail.Text = JsonSerializer.Serialize(data, new JsonSerializerOptions(Wire.Json) { WriteIndented = true });
        using var refresh = PropertyRows.Batch();
        PropertyRows.Clear();
        foreach (var row in BuildDetailRows(data, sessionId)) PropertyRows.Add(row);
        UpdateMonitorRows();
        if (newScope)
            foreach (var row in PropertyRows.Where(r => r.Section.StartsWith("材质[", StringComparison.Ordinal)))
                collapsedPropertyGroups.Add(row.ComponentGroup + "\u001f" + row.Section);
    }

    private List<PropertyRow> BuildDetailRows(NodeDetailResponse data, Guid sessionId)
    {
        var rows = new List<PropertyRow>();
        string componentGroup = "GameObject", section = "属性";
        var currentTree = snapshot?.NodeTree ?? sessions.FirstOrDefault(v => v.Session.Id == sessionId)?.Session.Tree ?? renderedTree;
        bool nodeInactive = !data.activeSelf || currentTree?.allNodes?.FirstOrDefault(n => n.instanceId == data.instanceId)?.activeInHierarchy == false;
        bool componentInactive = nodeInactive; string componentState = data.activeSelf ? "已激活" : "未激活";
        void Add(string label, string type, string value, SetPropertyRequest request, bool writable = true, string hint = null, int textureId = 0, int slot = -1, string textureProperty = null, IReadOnlyDictionary<string, int> enumValues = null)
        {
            var row = new PropertyRow { Label = label, Type = type, Value = value, Request = request,
                ComponentGroup = componentGroup, Section = section, ComponentInactive = componentInactive, ComponentStateLabel = componentState, Layers = layerCatalog, Choices = PropertyRow.Presets(type, value, request.propertyPath, enumValues),
                TextureMetadataKnown = data.texturePreviewMetadata,
                CanPreview = textureId != 0 && (snapshot?.TexturesPng.ContainsKey(textureId.ToString()) == true || data.texturePreviewMetadata && data.readableTextureIds?.Contains(textureId) == true),
                CanEdit = snapshot == null && writable, Hint = hint, TextureId = textureId, MaterialSlot = slot, TextureProperty = textureProperty };
            row.AcceptRemoteValue();
            row.SetBaseline(propertyBaselines.GetValueOrDefault((sessionId, data.instanceId, PropertyKey(row))));
            rows.Add(row);
        }
        SetPropertyRequest Make(string component, int index, string path, string type) => new()
        { instanceId = data.instanceId, componentType = component, componentIndex = index, propertyPath = path, valueType = type };
        Add("GameObject / activeSelf", "bool", data.activeSelf.ToString(), Make("GameObject", 0, "activeSelf", "bool"));
        var indices = new Dictionary<string, int>();
        foreach (var component in data.components ?? new())
        {
            string type = component.typeName ?? component.displayName ?? "Unknown";
            int index = indices.GetValueOrDefault(type); indices[type] = index + 1;
            string prefix = (component.displayName ?? type.Split('.').Last()) + $"[{index}] / ";
            componentGroup = type + $" [{index}]"; section = "属性";
            componentInactive = nodeInactive || component.hasEnabled && !component.enabled;
            componentState = nodeInactive ? "节点未激活" : component.hasEnabled ? component.enabled ? "已启用" : "组件已禁用" : "随节点激活";
            if (component.hasEnabled) Add(prefix + "enabled", "bool", component.enabled.ToString(), Make(type, index, "enabled", "bool"));
            if (component.transformData is { } transform)
            {
                section = "属性";
                foreach (var entry in new[] { ("localPosition", transform.localPosition), ("localRotation", transform.localRotation), ("localScale", transform.localScale) })
                    if (entry.Item2 is { } v) Add(prefix + entry.Item1, "vector3", Vector(v.x, v.y, v.z), Make(type, index, entry.Item1, "vector3"));
            }
            foreach (var field in component.serializedFields ?? new())
            {
                section = "属性";
                string kind = field.valueType ?? "other";
                if (type == "UnityEngine.Camera" && field.fieldName == "cullingMask") kind = "layerMask";
                if (kind == "number") kind = field.fieldType switch
                {
                    "Int32" or "System.Int32" => "int", "Single" or "System.Single" => "float",
                    "Double" or "System.Double" => "double", _ => "number"
                };
                string value = kind switch
                {
                    "bool" => field.boolValue.ToString(), "int" => field.intValue.ToString(CultureInfo.InvariantCulture),
                    "enum" => field.enumValue.ToString(CultureInfo.InvariantCulture), "layerMask" => field.intValue.ToString(CultureInfo.InvariantCulture),
                    "float" or "double" => Format(field.doubleValue), "vector2" => Vector(field.x, field.y),
                    "vector3" => Vector(field.x, field.y, field.z), "vector4" or "color" => Vector(field.x, field.y, field.z, field.w),
                    _ => field.stringValue ?? field.objectName ?? "None"
                };
                bool editable = kind is "bool" or "int" or "enum" or "layerMask" or "float" or "double" or "string" or "vector2" or "vector3" or "vector4" or "color";
                // Runtime setter handles Int32, Single, Double, Boolean, String, vectors, colors and enums only.
                if (kind == "int" && field.fieldType is not ("System.Int32" or "Int32" or "int")) editable = false;
                if (type.StartsWith("UnityEngine.", StringComparison.Ordinal))
                {
                    bool supported = type == "UnityEngine.Camera" && field.fieldName is "cullingMask" or "fieldOfView" or "nearClipPlane" or "farClipPlane"
                        || type == "UnityEngine.BoxCollider" && field.fieldName is "center" or "size" or "isTrigger";
                    editable &= supported;
                }
                var enumValues = kind == "enum" ? enumCatalog.Resolve(field.fieldType, field.enumNames, field.stringValue, field.enumValue) : null;
                Add(prefix + field.fieldName, kind, value, Make(type, index, field.fieldName, kind), editable,
                    field.enumNames == null ? field.fieldType : enumValues != null ? "下拉选择枚举名称及实际值" : "未找到可验证的枚举值映射，请填实际整数值；名称：" + string.Join("; ", field.enumNames), field.textureInstanceId,
                    enumValues: enumValues);
            }
            foreach (var material in component.materials ?? new())
            {
                int slot = material.slotIndex;
                section = $"材质[{slot}] · {material.name}";
                string label = prefix + $"材质[{slot}] {material.name} / ";
                var visible = Make(type, index, "materials.visible", "bool"); visible.intValue = slot;
                Add(label + "visible", "bool", material.visible.ToString(), visible);
                var queue = Make(type, index, "materials.renderQueue", "float"); queue.intValue = slot;
                Add(label + "renderQueue", "float", material.renderQueue.ToString(), queue);
                foreach (string keyword in (material.shaderAllKeywords ?? new()).Union(material.enabledKeywords ?? new()))
                {
                    var req = Make(type, index, "materials.keyword", "bool"); req.intValue = slot; req.stringValue = keyword;
                    Add(label + keyword, "bool", (material.enabledKeywords?.Contains(keyword) == true).ToString(), req);
                }
                foreach (var prop in material.properties ?? new())
                {
                    string kind = prop.typeName switch { "Color" => "color", "Vector" => "vector4", "Float" or "Range" or "Int" => "float", _ => "texture" };
                    var req = Make(type, index, "materials.property", kind == "color" ? "materialColor" : kind == "vector4" ? "materialVector" : "materialFloat");
                    req.intValue = slot; req.stringValue = prop.name;
                    string value = kind switch { "color" => Vector(prop.colorValue ?? new float[4]), "vector4" => Vector(prop.vectorValue ?? new float[4]), "float" => Format(prop.floatValue), _ => prop.textureInstanceId.ToString() };
                    Add(label + prop.name, kind, value, req, kind != "texture", textureId: prop.textureInstanceId, slot: slot, textureProperty: prop.name);
                }
            }

        }
        return rows;
    }

    private bool FilterProperty(object value)
    {
        if (value is not PropertyRow row) return false;
        if (OnlyChanges?.IsChecked == true && !row.IsChanged) return false;
        string text = PropertySearch?.Text?.Trim() ?? "";
        return text.Length == 0 || $"{row.ComponentGroup} {row.Section} {row.Label} {row.Type} {row.Value}".Contains(text, StringComparison.OrdinalIgnoreCase);
    }
    private void PropertiesGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PropertiesGrid.Columns.Count != 4 || PropertiesGrid.ActualWidth < 100) return;
        // Group presenters can measure rows with infinite width; explicit widths keep columns aligned and readable.
        double available = Math.Max(330, PropertiesGrid.ActualWidth - 90);
        double field = Math.Max(120, (available - 80) * 0.36);
        PropertiesGrid.Columns[0].Width = field;
        PropertiesGrid.Columns[1].Width = 0;
        PropertiesGrid.Columns[1].Visibility = Visibility.Collapsed;
        PropertiesGrid.Columns[2].Width = Math.Max(160, available - field - 80);
        PropertiesGrid.Columns[3].Width = 80;
    }
    private void PropertySearch_Changed(object sender, TextChangedEventArgs e)
    { if (DataContext != null) CollectionViewSource.GetDefaultView(PropertyRows).Refresh(); }
    private static string PropertyGroupKey(CollectionViewGroup group)
    {
        object first = group.Items.FirstOrDefault();
        while (first is CollectionViewGroup nested) first = nested.Items.FirstOrDefault();
        return first is PropertyRow row ? row.ComponentGroup + (group.IsBottomLevel ? "\u001f" + row.Section : "") : null;
    }
    private void PropertyGroup_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander { DataContext: CollectionViewGroup group } expander && PropertyGroupKey(group) is { } key)
            expander.IsExpanded = !collapsedPropertyGroups.Contains(key);
    }
    private void PropertyGroup_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { IsLoaded: true, DataContext: CollectionViewGroup group } expander || e.OriginalSource != sender) return;
        string key = PropertyGroupKey(group);
        if (key == null) return;
        if (expander.IsExpanded) collapsedPropertyGroups.Remove(key); else collapsedPropertyGroups.Add(key);
    }
    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }
    private void PropertyGroups_Click(object sender, RoutedEventArgs e)
    {
        bool expand = bool.Parse((string)((Button)sender).Tag);
        void Set(CollectionViewGroup group)
        {
            string key = PropertyGroupKey(group);
            if (key != null) { if (expand) collapsedPropertyGroups.Remove(key); else collapsedPropertyGroups.Add(key); }
            foreach (var nested in group.Items.OfType<CollectionViewGroup>()) Set(nested);
        }
        foreach (var group in CollectionViewSource.GetDefaultView(PropertyRows).Groups?.OfType<CollectionViewGroup>() ?? Enumerable.Empty<CollectionViewGroup>()) Set(group);
        foreach (var expander in VisualChildren<Expander>(PropertiesGrid).ToList()) expander.IsExpanded = expand;
    }
    private void PropertyPresets_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PropertyRow row } button || !row.CanEdit) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var choice in row.Choices)
        {
            var item = new MenuItem { Header = choice.Label, IsCheckable = true, IsChecked = choice.Value == row.Value };
            item.Click += (_, _) => row.Value = choice.Value;
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }
    private void LayerMask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PropertyRow row } button || !row.CanEdit || !row.UseLayerMask) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 600 };
        void RefreshChecks()
        {
            int mask = int.Parse(row.Value, CultureInfo.InvariantCulture);
            foreach (var item in menu.Items.OfType<MenuItem>())
                if (item.Tag is int bit) item.IsChecked = (mask & (1 << bit)) != 0;
        }
        foreach (var pair in new[] { ("Nothing · 全不选", "0"), ("Everything · 全选", "-1") })
        {
            var item = new MenuItem { Header = pair.Item1, StaysOpenOnClick = true };
            item.Click += (_, _) => { row.Value = pair.Item2; RefreshChecks(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        int initial = int.Parse(row.Value, CultureInfo.InvariantCulture);
        for (int i = 0; i < 32; i++)
        {
            if (string.IsNullOrWhiteSpace(row.Layers.Names[i]) && (initial & (1 << i)) == 0) continue;
            int bit = i;
            var item = new MenuItem { Header = row.Layers.Name(bit), Tag = bit, IsCheckable = true, StaysOpenOnClick = true,
                ToolTip = $"Layer {bit} · bit 0x{unchecked((uint)(1 << bit)):X8}" };
            item.Click += (_, _) => { row.ToggleLayer(bit, item.IsChecked); RefreshChecks(); };
            menu.Items.Add(item);
        }
        RefreshChecks(); button.ContextMenu = menu; menu.IsOpen = true;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        var s = Live();
        var row = (PropertyRow)((Button)sender).DataContext;
        if (s.Id != detailSession || selectedNode != row.Request.instanceId) throw new InvalidOperationException("会话或节点已改变，请刷新详情。");
        var response = Wire.Body<SetPropertyResponse>(await s.RequestAsync(Command.SET_PROPERTY, row.ToRequest(), token, 15, Command.SET_PROPERTY_RESPONSE));
        if (!response.success) throw new IOException(response.error);
        if (row.Request.componentType == "GameObject" && row.Request.propertyPath == "activeSelf")
            await s.RefreshTreeAsync(token);
        await s.GetDetailAsync(row.Request.instanceId, token);
        if (IsCurrent(s) && selectedNode == response.instanceId) ShowDetail(s.Details[response.instanceId], s.Id);
        StatusLabel.Text = "属性写入成功，已重新读取远端值。";
    });

    private bool FilterLog(object value)
    {
        if (value is not LogRow row) return false;
        return row.Severity >= (LogLevel?.SelectedIndex ?? 0) && row.Text.Contains(LogFilter?.Text ?? "", StringComparison.OrdinalIgnoreCase);
    }
    private void LogFilter_Changed(object sender, TextChangedEventArgs e) { if (DataContext != null) CollectionViewSource.GetDefaultView(Logs).Refresh(); }
    private void LogLevel_Changed(object sender, SelectionChangedEventArgs e) { if (DataContext != null) CollectionViewSource.GetDefaultView(Logs).Refresh(); }
    private void ClearLogs_Click(object sender, RoutedEventArgs e) { Logs.Clear(); Selected?.Logs.Clear(); }
    private void Log_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogDetails == null) return;
        LogDetails.Text = LogsGrid.SelectedItem is LogRow row ? $"{row.Time} [{row.Level}] {row.Text}" : "";
        LogDetails.Visibility = string.IsNullOrEmpty(LogDetails.Text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog { Filter = "文本日志|*.txt", FileName = "remote-logs.txt" };
            if (dialog.ShowDialog(this) == true) File.WriteAllLines(dialog.FileName,
                CollectionViewSource.GetDefaultView(Logs).Cast<LogRow>().Select(r => $"{r.Time} [{r.Level}] {r.Text}"));
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async Task Browse(InspectorSession s, string path, CancellationToken token)
    {
        var data = Wire.Body<FileBrowseListResponse>(await s.RequestAsync(Command.FILE_BROWSE_LIST,
            new FileBrowseListRequest { relativePath = path }, token, 15, Command.FILE_BROWSE_LIST_RESPONSE));
        if (!data.success) throw new IOException(data.error);
        if (!IsCurrent(s)) return;
        RemotePath.Text = data.relativePath;
        UpdateBreadcrumbs(data.relativePath);
        PersistentPath.Text = data.persistentDataPath;
        FilesGrid.ItemsSource = (data.entries ?? Array.Empty<FileEntry>()).OrderByDescending(f => f.isDirectory).ThenBy(f => f.name)
            .Select(f => new FileRow(f.name, f.relativePath, f.isDirectory, f.size, f.lastModified)).ToList();
        StatusLabel.Text = "目录已加载：" + (string.IsNullOrEmpty(data.relativePath) ? "PersistentRoot" : data.relativePath);
    }
    private void UpdateBreadcrumbs(string path)
    { Breadcrumbs.Clear(); foreach (var crumb in PathCrumb.Build(path)) Breadcrumbs.Add(crumb); }
    private async void Breadcrumb_Click(object sender, RoutedEventArgs e) => await Run(t => Browse(Live(), (string)((Button)sender).Tag, t));
    private async void RemotePath_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await Run(t => Browse(Live(), RemotePath.Text.Trim(), t)); } }
    private async void Browse_Click(object sender, RoutedEventArgs e) => await Run(t => Browse(Live(), RemotePath.Text.Trim(), t));
    private async void Parent_Click(object sender, RoutedEventArgs e) => await Run(t => Browse(Live(), ParentPath(RemotePath.Text), t));
    private static string ParentPath(string path) { path = path.Replace('\\', '/').Trim('/'); int index = path.LastIndexOf('/'); return index < 0 ? "" : path[..index]; }
    private static string Join(string parent, string child) => string.IsNullOrEmpty(parent) ? child : parent.TrimEnd('/') + "/" + child;
    private void File_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right) return;
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(FilesGrid, source) is DataGridRow row)
        { FilesGrid.SelectedItem = row.Item; row.Focus(); }
    }
    private async void File_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(FilesGrid, source) is DataGridRow { Item: FileRow { IsDirectory: true } row })
            await Run(t => Browse(Live(), row.Path, t));
        else if (e.OriginalSource is DependencyObject fileSource && ItemsControl.ContainerFromElement(FilesGrid, fileSource) is DataGridRow { Item: FileRow { IsDirectory: false } file } && file.Name.EndsWith(".logcompr", StringComparison.OrdinalIgnoreCase))
            OpenRemoteLog(file);
    }
    private FileRow FileSelection() => FilesGrid.SelectedItem as FileRow ?? throw new InvalidOperationException("请先选择文件或目录。");
    private async void Mkdir_Click(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        var s = Live(); string name = NewDirectory.Text.Trim();
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\')) throw new ArgumentException("请输入单个目录名。");
        string parent = RemotePath.Text;
        var result = Wire.Body<FileBrowseMkdirResponse>(await s.RequestAsync(Command.FILE_BROWSE_MKDIR,
            new FileBrowseMkdirRequest { relativePath = Join(parent, name) }, token, 15, Command.FILE_BROWSE_MKDIR_RESPONSE));
        if (!result.success) throw new IOException(result.error);
        await Browse(s, parent, token);
    });
    private async void Delete_Click(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        var s = Live(); var row = FileSelection();
        if (MessageBox.Show(this, $"删除游戏设备上的{(row.IsDirectory ? "目录及其内容" : "文件")}：\n{row.Path}", "删除远端文件", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var result = Wire.Body<FileBrowseDeleteResponse>(await s.RequestAsync(Command.FILE_BROWSE_DELETE,
            new FileBrowseDeleteRequest { relativePath = row.Path, isDirectory = row.IsDirectory }, token, 15, Command.FILE_BROWSE_DELETE_RESPONSE));
        if (!result.success) throw new IOException(result.error);
        await Browse(s, result.parentRelativePath, token);
    });
    private async void Download_Click(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        var s = Live(); var row = FileSelection();
        var save = new SaveFileDialog { FileName = row.Name + (row.IsDirectory ? ".zip" : ""), Filter = "所有文件|*.*" };
        if (save.ShowDialog(this) != true) return;
        await s.DownloadAsync(row.Path, row.IsDirectory, save.FileName, new Progress<string>(m => { StatusLabel.Text = m; TransferStatus.Text = m; }), token);
        StatusLabel.Text = "已下载到：" + save.FileName; TransferStatus.Text = StatusLabel.Text;
    });
    private async void Upload_Click(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        var s = Live(); var open = new OpenFileDialog();
        if (open.ShowDialog(this) != true) return;
        string path = string.IsNullOrWhiteSpace(UploadPath.Text) ? Join(RemotePath.Text, Path.GetFileName(open.FileName)) : UploadPath.Text.Trim();
        TransferStatus.Text = "上传目标：" + path;
        await s.UploadAsync(open.FileName, path, new Progress<string>(m => { StatusLabel.Text = m; TransferStatus.Text = m; }), token);
        await Browse(s, ParentPath(path), token);
        StatusLabel.Text = "上传完成：" + path; TransferStatus.Text = StatusLabel.Text;
    });

    private void ShowImage(byte[] bytes, string label, bool screen = false)
    {
        pictures.Insert(0, new Picture(DateTime.Now.ToString("HH:mm:ss") + " · " + label, bytes, screen, Selected?.Session.Id ?? Guid.Empty));
        while (pictures.Count > 20 || pictures.Sum(p => (long)p.Bytes.Length) > 64 * 1024 * 1024) pictures.RemoveAt(pictures.Count - 1);
        ImageHistory.SelectedIndex = 0;
        Tabs.SelectedIndex = 2;
    }
    private void ImageHistory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ImageHistory.SelectedItem is not Picture picture) return;
        byte[] bytes = picture.Bytes;
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        imageBytes = bytes; PreviewImage.Source = bitmap;
        FitImage();
        ImageInfo.Text = $"{picture.Label} · {bitmap.PixelWidth} × {bitmap.PixelHeight} · {bytes.Length / 1024.0:F1} KiB";
    }
    private async void Capture_Click(object sender, RoutedEventArgs e) => await Run(async token =>
    { var s = Live(); var bytes = await s.CaptureAsync(token: token); if (IsCurrent(s)) ShowImage(bytes, "游戏画面", true); });
    private async void Texture_Click(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        if (sender is not Button { DataContext: PropertyRow { CanPreview: true } row }) return;
        if (snapshot != null)
        {
            if (!snapshot.TexturesPng.TryGetValue(row.TextureId.ToString(), out var cached)) throw new IOException("快照没有缓存该纹理。");
            OpenTexturePreview(cached, row, null); return;
        }
        var s = Live(); var bytes = await s.GetTextureAsync(row.TextureId, token);
        if (IsCurrent(s)) OpenTexturePreview(bytes, row, s);
    });
    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (imageBytes == null) throw new InvalidOperationException("尚无图片。");
            var save = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = "remote-image.png" };
            if (save.ShowDialog(this) == true) File.WriteAllBytes(save.FileName, Png(imageBytes));
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    private static byte[] Png(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(decoder.Frames[0]);
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    private async void LoadSnapshot_Click(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        var open = new OpenFileDialog { Filter = "RemoteInspector 快照|*.bin" };
        if (open.ShowDialog(this) != true) return;
        var loaded = await Task.Run(() => SnapshotFile.Load(open.FileName), token);
        navigation.Remove(Guid.Empty); navigationPositions.Remove(Guid.Empty); favorites.Remove(Guid.Empty); monitors.Remove(Guid.Empty);
        foreach (var key in propertyBaselines.Keys.Where(k => k.Item1 == Guid.Empty).ToArray()) propertyBaselines.Remove(key);
        MonitorRows.Clear(); LockInspector.IsChecked = false;
        snapshot = loaded; UpdateNavigationButtons(); detail = null; selectedNode = loaded.SelectedInstanceId; PropertyRows.Clear(); RawDetail.Clear();
        snapshotExpanded.Clear();
        var expandedPaths = loaded.ExpandedPaths.ToHashSet();
        foreach (var node in loaded.NodeTree?.allNodes ?? new())
            if (expandedPaths.Contains(node.path)) snapshotExpanded.Add(node.instanceId);
        renderedTree = null; NodeTree.ItemsSource = null; imageBytes = null; PreviewImage.Source = null; pictures.Clear();
        Logs.Clear(); FrameRows.Clear(); FilesGrid.ItemsSource = null; RpcGrid.ItemsSource = null; RpcResult.Clear();
        if (snapshot.NodeTree != null) RenderTree(snapshot.NodeTree);
        SnapshotInfo.Text = $"离线只读：{open.FileName}\n{snapshot.NodeDetails.Count} 个节点详情，{snapshot.TexturesPng.Count} 个纹理。";
        StatsLabel.Text = "离线快照 · 写入操作已禁用";
        NodeLabel.Text = "选择快照节点";
        StatusLabel.Text = "快照已加载。";
    });
    private async Task SaveSnapshot(bool full, CancellationToken token)
    {
        var save = new SaveFileDialog { Filter = "RemoteInspector 快照|*.bin", FileName = "RemoteInspector_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bin" };
        if (save.ShowDialog(this) != true) return;
        SnapshotData data;
        if (snapshot != null)
        {
            if (full) throw new InvalidOperationException("完整采集需要实时会话。");
            data = snapshot;
        }
        else
        {
            var s = Live();
            if (full) await s.RefreshTreeAsync(token);
            var tree = s.Tree ?? throw new InvalidOperationException("请先获取节点树。");
            var details = new Dictionary<int, NodeDetailResponse>();
            var nodes = tree.allNodes ?? new();
            for (int i = 0; i < nodes.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (full)
                {
                    StatusLabel.Text = $"完整采集 {i + 1}/{nodes.Count} · {nodes[i].name}";
                    await s.GetDetailAsync(nodes[i].instanceId, token);
                }
                if (s.Details.TryGetValue(nodes[i].instanceId, out var value)) details[nodes[i].instanceId] = value;
            }
            data = new SnapshotData { SavedAtUtcTicks = DateTime.UtcNow.Ticks, NodeTree = tree, NodeDetails = details,
                SelectedInstanceId = selectedNode, SelectedPath = nodes.FirstOrDefault(n => n.instanceId == selectedNode)?.path };
            var expanded = Selected?.Expanded ?? new();
            data.ExpandedPaths = nodes.Where(n => expanded.Contains(n.instanceId)).Select(n => n.path).ToList();
            foreach (var pair in s.Images) data.TexturesPng[pair.Key.ToString()] = Png(pair.Value);
        }
        token.ThrowIfCancellationRequested();
        await Task.Run(() => SnapshotFile.Save(save.FileName, data), token);
        StatusLabel.Text = $"快照已保存：{save.FileName}（{data.NodeDetails.Count} 个详情，{data.TexturesPng.Count} 个已缓存纹理）。";
    }
    private async void SaveSnapshot_Click(object sender, RoutedEventArgs e) => await Run(t => SaveSnapshot(false, t));
    private async void FullSnapshot_Click(object sender, RoutedEventArgs e) => await Run(t => SaveSnapshot(true, t));
    private void ExitSnapshot_Click(object sender, RoutedEventArgs e)
    { Session_Changed(sender, null); StatusLabel.Text = "已返回实时模式。"; }

    private void FpsCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawFps();
    private void DrawFps()
    {
        if (FpsCanvas == null) return;
        FpsCanvas.Children.Clear();
        double width = FpsCanvas.ActualWidth, height = FpsCanvas.ActualHeight;
        if (width < 60 || height < 40) return;
        double maximum = Math.Max(60, FrameRows.Count == 0 ? 60 : FrameRows.Max(f => float.IsFinite(f.Fps) ? Math.Max(0, f.Fps) : 0));
        foreach (double fraction in new[] { 0.0, 0.5, 1.0 })
        {
            double y = 12 + (height - 30) * fraction;
            FpsCanvas.Children.Add(new System.Windows.Shapes.Line { X1 = 45, X2 = width - 10, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromRgb(49, 65, 84)) });
            var label = new TextBlock { Text = (maximum * (1 - fraction)).ToString("F0"), Foreground = Brushes.LightSteelBlue, FontSize = 11 };
            Canvas.SetLeft(label, 3); Canvas.SetTop(label, Math.Max(0, y - 7)); FpsCanvas.Children.Add(label);
        }
        var line = new System.Windows.Shapes.Polyline { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2 };
        int i = 0;
        foreach (var frame in FrameRows.Reverse())
        {
            double fps = float.IsFinite(frame.Fps) ? Math.Max(0, frame.Fps) : 0;
            line.Points.Add(new Point(45 + (width - 55) * i++ / Math.Max(1, FrameRows.Count - 1), 12 + (height - 30) * (1 - fps / maximum)));
        }
        FpsCanvas.Children.Add(line);
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (closeAllowed) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        compressedLog?.Dispose();
        nodeWatchRequest?.Cancel();
        operation?.Cancel();
        autoDetailRequest?.Cancel();
        selectionRequest?.Cancel(); treeSearchDelay.Stop();
        timer.Stop();
        IsEnabled = false;
        _ = CompleteCloseAsync();
    }

    private async Task CompleteCloseAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Settings
            {
                Port = int.TryParse(PortBox.Text, out int port) ? port : 9555,
                TreeRatio = TreeWidthColumn.ActualWidth / Math.Max(1, TreeWidthColumn.ActualWidth + InspectorWidthColumn.ActualWidth),
                ToolsHeight = ToolsHeight.ActualHeight,
                RpcTemplates = new Dictionary<string, string>(rpcTemplates),
                DiagnosticsRatio = DiagnosticsWidthColumn.ActualWidth / Math.Max(1, DiagnosticsWidthColumn.ActualWidth + FilesWidthColumn.ActualWidth)
            }));
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning(ex.Message); }
        try { await host.DisposeAsync(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Shutdown: " + ex); }
        finally
        {
            // Even an already-stopped host completes synchronously. Never reenter WPF Closing.
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                _ = Dispatcher.BeginInvoke(new Action(() => { closeAllowed = true; Close(); }));
        }
    }
    private sealed class Settings
    {
        public Dictionary<string, string> RpcTemplates { get; set; } = new();
        public int Port { get; set; } = 9555;
        public double TreeRatio { get; set; } = 0.3;
        public double ToolsHeight { get; set; } = 290;
        public double DiagnosticsRatio { get; set; } = 0.4;
    }
    private sealed record Picture(string Label, byte[] Bytes, bool Screen = false, Guid Session = default);
}

public sealed class SessionView : INotifyPropertyChanged
{
    public InspectorSession Session { get; }
    public List<LogRow> Logs { get; } = new();
    public FrameStatsResponse LastStats;
    public HashSet<int> Expanded { get; } = new();
    public int SelectedNode { get; set; }
    private string lastName;
    public string DisplayName => Session.DisplayName + (Session.IsConnected ? "" : " · 已断开");
    public SessionView(InspectorSession session) => Session = session;
    public void RefreshName()
    {
        if (lastName == DisplayName) return;
        lastName = DisplayName;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
    }
    public event PropertyChangedEventHandler PropertyChanged;
}
