using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using RemoteInspector.Core;
using RemoteInspector.Protocol;

namespace RemoteInspector.Desktop;

public partial class MainWindow
{
    private string currentOperationName = "请求";
    private DateTime lastDetailRefresh;
    private bool autoDetailBusy;
    private CancellationTokenSource autoDetailRequest;
    public System.Collections.ObjectModel.ObservableCollection<MonitorValue> MonitorRows { get; } = new();
    private readonly Dictionary<Guid, List<MonitorValue>> monitors = new();
    private readonly Dictionary<string, string> rpcTemplates = new();
    private static string OperationName(string source) => source switch
    {
        "Upload_Click" => "上传文件", "Download_Click" => "下载文件", "FullSnapshot_Click" => "完整采集",
        "SaveIncident_Click" => "保存现场", "Detail_Click" => "刷新详情", "Apply_Click" => "写入属性",
        "Refresh_Click" => "采集节点树", "Rpc_Click" => "执行 RPC", _ => "远端请求"
    };
    private async Task AutoRefreshDetail()
    {
        int interval = RefreshInterval.SelectedIndex switch { 1 => 1, 2 => 2, 3 => 5, _ => 0 };
        if (System.Windows.Input.Keyboard.FocusedElement is TextBox { IsReadOnly: false } input && PropertiesGrid.IsAncestorOf(input)) return;
        if (interval == 0 || autoDetailBusy || operation != null || selectionRequest != null || selectedNode == 0 ||
            PropertyRows.Any(r => r.IsDirty) || DateTime.UtcNow - lastDetailRefresh < TimeSpan.FromSeconds(interval) || Selected?.Session.IsConnected != true) return;
        autoDetailBusy = true; lastDetailRefresh = DateTime.UtcNow;
        using var refresh = new CancellationTokenSource(); autoDetailRequest = refresh;
        try
        {
            var s = Selected.Session; int id = selectedNode;
            await s.GetDetailAsync(id, refresh.Token);
            if (!refresh.IsCancellationRequested && !closing && IsCurrent(s) && selectedNode == id && !PropertyRows.Any(r => r.IsDirty)) ShowDetail(s.Details[id], s.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closing) StatusLabel.Text = "自动刷新：" + ex.Message; }
        finally { if (autoDetailRequest == refresh) autoDetailRequest = null; autoDetailBusy = false; }
    }
    private void Layout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext == null || ToolsHeight == null) return;
        int index = LayoutPreset.SelectedIndex;
        double ratio = index == 1 ? .26 : .3;
        TreeWidthColumn.Width = new GridLength(ratio, GridUnitType.Star); InspectorWidthColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
        double logs = index is 2 or 3 ? .65 : .4;
        DiagnosticsWidthColumn.Width = new GridLength(logs, GridUnitType.Star); FilesWidthColumn.Width = new GridLength(1 - logs, GridUnitType.Star);
        ToolsHeight.Height = new GridLength(index == 1 ? 215 : index is 2 or 3 ? Math.Max(290, ActualHeight * .43) : 290);
        if (index is 2 or 3) DiagnosticsTabs.SelectedIndex = index == 3 ? 1 : 0;
    }
    private void PinProperty_Click(object sender, RoutedEventArgs e)
    {
        if (PropertiesGrid.SelectedItem is not PropertyRow row) { StatusLabel.Text = "请先选择要监视的属性行。"; return; }
        if (!monitors.TryGetValue(Scope, out var list)) monitors[Scope] = list = new();
        if (list.Any(v => v.Node == selectedNode && v.Key == PropertyKey(row))) return;
        var value = new MonitorValue(selectedNode, PropertyKey(row), renderedTree?.allNodes?.FirstOrDefault(n => n.instanceId == selectedNode)?.path ?? NodeLabel.Text, row.Label, row.Value, DateTime.Now.ToString("HH:mm:ss"));
        list.Add(value); MonitorRows.Add(value); StatusLabel.Text = "已固定属性，切换到“监视”可跨节点查看。";
    }
    private void UpdateMonitorRows()
    {
        if (!monitors.TryGetValue(Scope, out var list)) return;
        UpdateMonitorValues(selectedNode, PropertyRows);
        PublishMonitorRows();
    }
    private void UpdateMonitorValues(int node, IEnumerable<PropertyRow> rows)
    {
        if (!monitors.TryGetValue(Scope, out var list)) return;
        var values = rows.ToDictionary(PropertyKey);
        for (int i = 0; i < list.Count; i++)
            if (list[i].Node == node)
                list[i] = values.TryGetValue(list[i].Key, out var row)
                    ? list[i] with { Value = row.Value, Updated = DateTime.Now.ToString("HH:mm:ss") }
                    : list[i] with { Value = "字段已不可用", Updated = DateTime.Now.ToString("HH:mm:ss") };
    }
    private void PublishMonitorRows()
    {
        MonitorRows.Clear(); if (monitors.TryGetValue(Scope, out var list)) foreach (var row in list) MonitorRows.Add(row);
    }

    private void UnpinProperty_Click(object sender, RoutedEventArgs e)
    { if (MonitorGrid.SelectedItem is MonitorValue value) { monitors.GetValueOrDefault(Scope)?.Remove(value); MonitorRows.Remove(value); } }
    private async void RefreshMonitor_Click(object sender, RoutedEventArgs e) => await Run(async token =>
    {
        var s = Live(); var scope = Scope;
        if (!monitors.TryGetValue(scope, out var list)) return;
        foreach (int node in list.Select(r => r.Node).Distinct().ToArray())
        {
            await s.GetDetailAsync(node, token);
            if (!IsCurrent(s)) return;
            UpdateMonitorValues(node, BuildDetailRows(s.Details[node], s.Id));
        }
        PublishMonitorRows();
    });
    private void SaveRpcTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string name = RpcMethod.Text.Trim(); if (name.Length == 0) throw new InvalidOperationException("请先选择 RPC 方法。");
            using var json = JsonDocument.Parse(RpcArgs.Text);
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("参数须为 JSON 数组。");
            rpcTemplates[name] = RpcArgs.Text; RefreshRpcTemplates(); StatusLabel.Text = "已保存参数模板：" + name + "；选择模板后仍需点击执行。";
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    private DateTime operationStarted;
    private readonly Dictionary<Guid, List<int>> navigation = new();
    private readonly Dictionary<Guid, int> navigationPositions = new();
    private readonly Dictionary<Guid, HashSet<int>> favorites = new();
    private readonly Dictionary<(Guid, int, string), string> propertyBaselines = new();
    private bool navigating;
    private Guid Scope => snapshot == null ? Selected?.Session.Id ?? Guid.Empty : Guid.Empty;
    private static string PropertyKey(PropertyRow row) => $"{row.ComponentGroup}\u001f{row.Section}\u001f{row.Label}\u001f{row.Type}";
    private void InitializeDebugTools() => UpdateNavigationButtons();
    private void ResetNavigation()
    {
        UpdateNavigationButtons();
        MonitorRows.Clear();
        if (monitors.TryGetValue(Scope, out var list)) foreach (var value in list) MonitorRows.Add(value);
    }
    private void RecordNavigation(int id)
    {
        if (!navigation.TryGetValue(Scope, out var list)) navigation[Scope] = list = new();
        int position = navigationPositions.GetValueOrDefault(Scope, -1);
        if (!navigating && (position < 0 || list[position] != id))
        {
            if (position + 1 < list.Count) list.RemoveRange(position + 1, list.Count - position - 1);
            list.Add(id); if (list.Count > 100) list.RemoveAt(0);
            navigationPositions[Scope] = list.Count - 1;
        }
        UpdateNodeIdentity(); UpdateNavigationButtons();
    }
    private void UpdateNavigationButtons()
    {
        if (BackNode == null) return;
        int position = navigationPositions.GetValueOrDefault(Scope, -1);
        BackNode.IsEnabled = position > 0;
        ForwardNode.IsEnabled = navigation.TryGetValue(Scope, out var list) && position + 1 < list.Count;
    }
    private void UpdateNodeIdentity()
    {
        var node = renderedTree?.allNodes?.FirstOrDefault(n => n.instanceId == selectedNode);
        NodeIdentity.Text = node == null ? "" : $"{node.path}  ·  ID {node.instanceId}  ·  {(node.activeInHierarchy ? "已激活" : node.activeSelf ? "父级未激活" : "自身未激活")}";
        NodeIdentity.ToolTip = NodeIdentity.Text;
        FavoriteNode.Content = favorites.TryGetValue(Scope, out var set) && set.Contains(selectedNode) ? "★ 已收藏" : "☆ 收藏";
    }
    private async Task NavigateNode(int id)
    {
        if (operation != null) { StatusLabel.Text = "请等待当前操作完成或先取消。"; return; }
        if (renderedTree?.allNodes?.Any(n => n.instanceId == id) != true) { StatusLabel.Text = "该节点已不在当前节点树中，请刷新。"; return; }
        selectionRequest?.Cancel();
        SearchBox.Clear(); TreeStateFilter.SelectedIndex = 0; ApplyTreeQuery();
        selectedNode = id; if (Selected != null && snapshot == null) Selected.SelectedNode = id;
        RecordNavigation(id); RenderTree(renderedTree); RestoreTreeView(id, 0);
        if (snapshot != null)
        {
            if (snapshot.NodeDetails.TryGetValue(id, out var cached)) ShowDetail(cached, Guid.Empty);
            else { PropertyRows.Clear(); RawDetail.Clear(); NodeLabel.Text = "快照未采集此节点详情"; }
            return;
        }
        await Run(async token => { var session = Live(); await session.GetDetailAsync(id, token); if (IsCurrent(session) && selectedNode == id) ShowDetail(session.Details[id], session.Id); });
    }
    private async void History_Click(object sender, RoutedEventArgs e)
    {
        if (!navigation.TryGetValue(Scope, out var list)) return;
        int position = navigationPositions.GetValueOrDefault(Scope, -1) + int.Parse((string)((Button)sender).Tag);
        if (position < 0 || position >= list.Count) return;
        navigationPositions[Scope] = position; navigating = true;
        try { await NavigateNode(list[position]); } finally { navigating = false; UpdateNavigationButtons(); }
    }
    private void LocateInspectorNode_Click(object sender, RoutedEventArgs e)
    {
        if (detail == null) { StatusLabel.Text = "请先选择节点查看属性。"; return; }
        if (renderedTree?.allNodes?.Any(n => n.instanceId == detail.instanceId) != true)
        { StatusLabel.Text = "关联节点已不在当前场景树中，请刷新。"; return; }
        selectedNode = detail.instanceId;
        if (Selected != null && snapshot == null) Selected.SelectedNode = selectedNode;
        SearchBox.Clear(); TreeStateFilter.SelectedIndex = 0; ApplyTreeQuery();
        RenderTree(renderedTree); RestoreTreeView(selectedNode, 0);
    }
    private void TreeFavorite_Click(object sender, RoutedEventArgs e)
    { if (contextWatchRow?.Node != null) ToggleFavorite(contextWatchRow.Node.instanceId); }
    private void Favorite_Click(object sender, RoutedEventArgs e) => ToggleFavorite(detail?.instanceId ?? selectedNode);
    private void ToggleFavorite(int id)
    {
        if (id == 0) return;
        if (!favorites.TryGetValue(Scope, out var set)) favorites[Scope] = set = new();
        if (!set.Add(id)) set.Remove(id);
        UpdateNodeIdentity();
    }
    private void Favorites_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender; var menu = new ContextMenu();
        foreach (var node in renderedTree?.allNodes?.Where(n => favorites.GetValueOrDefault(Scope)?.Contains(n.instanceId) == true) ?? Enumerable.Empty<RemoteInspector.Core.Models.NodeInfo>())
        {
            int id = node.instanceId; var item = new MenuItem { Header = node.path + $" [{id}]" };
            var remove = new MenuItem { Header = "移除收藏" };
            remove.Click += (_, _) => { favorites.GetValueOrDefault(Scope)?.Remove(id); UpdateNodeIdentity(); };
            var locate = new MenuItem { Header = "定位节点" }; locate.Click += async (_, _) => await NavigateNode(id);
            item.Items.Add(locate); item.Items.Add(remove); menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "此会话暂无收藏", IsEnabled = false });
        if (favorites.GetValueOrDefault(Scope)?.Count > 0)
        {
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "清空此会话收藏" };
            clear.Click += (_, _) => { favorites.Remove(Scope); UpdateNodeIdentity(); };
            menu.Items.Add(clear);
        }
        button.ContextMenu = menu; menu.PlacementTarget = button; menu.IsOpen = true;
    }
    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try { if (renderedTree?.allNodes?.FirstOrDefault(n => n.instanceId == selectedNode)?.path is { } path) Clipboard.SetText(path); }
        catch (Exception ex) { StatusLabel.Text = "复制失败：" + ex.Message; }
    }
    private void Baseline_Click(object sender, RoutedEventArgs e)
    {
        bool clear = (string)((Button)sender).Tag == "clear";
        foreach (var row in PropertyRows)
        {
            var key = (detailSession, selectedNode, PropertyKey(row));
            if (clear) propertyBaselines.Remove(key); else propertyBaselines[key] = row.Value;
            row.SetBaseline(clear ? null : row.Value);
        }
        CollectionViewSource.GetDefaultView(PropertyRows).Refresh();
        StatusLabel.Text = clear ? "当前节点的基准已清除。" : "已记录当前显示值；刷新详情后可比较变化。未应用的编辑也会计入基准。";
    }
    private void Changes_Click(object sender, RoutedEventArgs e) => CollectionViewSource.GetDefaultView(PropertyRows).Refresh();
    private void RevertEdits_Click(object sender, RoutedEventArgs e) { foreach (var row in PropertyRows) row.RevertEdit(); CollectionViewSource.GetDefaultView(PropertyRows).Refresh(); }
    private void LatestLog_Click(object sender, RoutedEventArgs e) { FollowLogs.IsChecked = true; if (LogsGrid.Items.Count > 0) LogsGrid.ScrollIntoView(LogsGrid.Items[LogsGrid.Items.Count - 1]); }
    private void LogMenu_Click(object sender, RoutedEventArgs e)
    {
        if (LogsGrid.SelectedItem is not LogRow row) return;
        try
        {
            if ((string)((MenuItem)sender).Tag == "copy") Clipboard.SetText($"{row.Time} [{row.Level}] {row.Text}");
            else LogFilter.Text = row.Text.Split('\n')[0];
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    private void Log_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Right && e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(LogsGrid, source) is DataGridRow row)
            LogsGrid.SelectedItem = row.Item;
    }
    private async void SaveIncident_Click(object sender, RoutedEventArgs e)
    {
        var save = new SaveFileDialog { Filter = "调试现场包|*.zip", FileName = "Incident_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip" };
        if (save.ShowDialog(this) != true) return;
        await Run(token => SaveIncidentAsync(save.FileName, token));
    }
    public async Task SaveIncidentAsync(string path, CancellationToken token = default)
    {
        var session = Selected?.Session;
        var tree = snapshot?.NodeTree ?? session?.Tree ?? throw new InvalidOperationException("请先采集节点树或加载快照。");
        var data = snapshot ?? new SnapshotData { SavedAtUtcTicks = DateTime.UtcNow.Ticks, NodeTree = tree,
            NodeDetails = session.Details.ToDictionary(p => p.Key, p => p.Value), SelectedInstanceId = selectedNode,
            SelectedPath = tree.allNodes?.FirstOrDefault(n => n.instanceId == selectedNode)?.path,
            ExpandedPaths = tree.allNodes?.Where(n => BrowseExpanded().Contains(n.instanceId)).Select(n => n.path).ToList() ?? new() };
        var logs = Logs.Select(r => $"{r.Time} [{r.Level}] {r.Text}").ToArray();
        var frames = FrameRows.ToArray(); var picture = imageBytes == null ? null : Png(imageBytes);
        var metadata = JsonSerializer.Serialize(new { capturedAt = DateTimeOffset.Now, device = session?.DisplayName,
            endpoint = session?.Endpoint, connected = session?.IsConnected, offline = snapshot != null,
            nodeCount = tree.allNodes?.Count, cachedDetails = data.NodeDetails.Count,
            note = "使用最近采集的树、已缓存详情、当前会话近期日志及最近图片；各数据并非同一帧采集。" }, new JsonSerializerOptions { WriteIndented = true });
        await Task.Run(() =>
        {
            string staging = Path.Combine(Path.GetTempPath(), "RemoteInspector-" + Guid.NewGuid().ToString("N"));
            string partial = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            Directory.CreateDirectory(staging);
            try
            {
                token.ThrowIfCancellationRequested(); SnapshotFile.Save(Path.Combine(staging, "snapshot.bin"), data);
                File.WriteAllLines(Path.Combine(staging, "logs.txt"), logs);
                File.WriteAllText(Path.Combine(staging, "frames.json"), JsonSerializer.Serialize(frames));
                File.WriteAllText(Path.Combine(staging, "incident.json"), metadata);
                if (picture != null) File.WriteAllBytes(Path.Combine(staging, "latest-image.png"), picture);
                token.ThrowIfCancellationRequested(); ZipFile.CreateFromDirectory(staging, partial);
                token.ThrowIfCancellationRequested(); File.Move(partial, path, true);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); Directory.Delete(staging, true); }
        }, token);
        StatusLabel.Text = "问题现场已保存：" + path;
    }
}

public sealed record MonitorValue(int Node, string Key, string Path, string Label, string Value, string Updated);
