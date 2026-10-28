using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteInspector.Core;
using RemoteInspector.Core.Models;

namespace RemoteInspector.Desktop;

public sealed record NodeWatchRow(int Node, string Path, string State, string Updated);
public sealed record NodeWatchChange(int Node, string Time, string Path, string Kind, string Field, string Before, string After, bool Destroyed = false);

public partial class MainWindow
{
    private sealed record WatchField(string Component, string Label, string Value);
    private sealed record NodeSample(string Path, Dictionary<string, string> NodeValues, HashSet<string> Components, Dictionary<string, WatchField> Fields);
    private sealed class WatchSession
    {
        public Dictionary<int, NodeWatchRow> Rows { get; } = new();
        public Dictionary<int, NodeSample> Samples { get; } = new();
        public List<NodeWatchChange> Changes { get; } = new();
        public DateTime LastPoll;
    }
    private readonly Dictionary<Guid, WatchSession> nodeWatches = new();
    public ObservableCollection<NodeWatchRow> NodeWatchRows { get; } = new();
    public ObservableCollection<NodeWatchChange> NodeWatchChanges { get; } = new();
    private CancellationTokenSource nodeWatchRequest;
    private bool pollingNodeWatches;
    private TreeRow contextWatchRow;
    private WatchSession Watches => nodeWatches.TryGetValue(Scope, out var state) ? state : nodeWatches[Scope] = new();
    private void PublishNodeWatches()
    {
        if (NodeWatchGrid == null) return;
        int? selected = (NodeWatchGrid.SelectedItem as NodeWatchRow)?.Node;
        NodeWatchRows.Clear(); NodeWatchChanges.Clear();
        if (!nodeWatches.TryGetValue(Scope, out var state)) return;
        foreach (var row in state.Rows.Values) NodeWatchRows.Add(row);
        foreach (var row in state.Changes.AsEnumerable().Reverse()) NodeWatchChanges.Add(row);
        if (selected.HasValue) NodeWatchGrid.SelectedItem = NodeWatchRows.FirstOrDefault(r => r.Node == selected.Value);
    }
    private void WatchTree_RightClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject source = e.OriginalSource as DependencyObject;
        while (source != null && source is not TreeViewItem) source = VisualTreeHelper.GetParent(source);
        contextWatchRow = (source as TreeViewItem)?.DataContext as TreeRow;
        if (source is TreeViewItem item) { item.IsSelected = true; item.Focus(); }
    }
    private void WatchTree_MenuOpening(object sender, ContextMenuEventArgs e)
    {
        contextWatchRow ??= NodeTree.SelectedItem as TreeRow;
        if (contextWatchRow?.Node == null) { e.Handled = true; return; }
        bool watched = Watches.Rows.ContainsKey(contextWatchRow.Node.instanceId);
        var items = NodeTree.ContextMenu.Items.OfType<MenuItem>().ToArray(); bool live = snapshot == null && Selected?.Session.IsConnected == true;
        items[0].IsEnabled = live && !watched; items[1].IsEnabled = live && watched;
        items[2].Header = favorites.GetValueOrDefault(Scope)?.Contains(contextWatchRow.Node.instanceId) == true ? "移除收藏" : "收藏节点";
    }
    public void AddNodeWatch(int id)
    {
        var session = Live();
        var node = session.Tree?.allNodes?.FirstOrDefault(n => n.instanceId == id) ?? throw new InvalidOperationException("请先选择节点。");
        if (Watches.Rows.ContainsKey(id)) return;
        if (Watches.Rows.Count >= 50) throw new InvalidOperationException("单会话最多监听 50 个节点，请先移除不需要的监听。");
        Watches.Rows[id] = new(id, node.path, "等待首次采样", "—"); Watches.LastPoll = DateTime.MinValue;
        PublishNodeWatches(); StatusLabel.Text = "已加入节点监听：" + node.path;
    }
    private void AddNodeWatch_Click(object sender, RoutedEventArgs e)
    {
        try { AddNodeWatch(sender is MenuItem ? contextWatchRow?.Node?.instanceId ?? 0 : selectedNode); }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    private void RemoveWatch(int id)
    { Watches.Rows.Remove(id); Watches.Samples.Remove(id); PublishNodeWatches(); }
    private void RemoveTreeWatch_Click(object sender, RoutedEventArgs e) { if (contextWatchRow?.Node != null) RemoveWatch(contextWatchRow.Node.instanceId); }
    private void RemoveNodeWatch_Click(object sender, RoutedEventArgs e) { if (NodeWatchGrid.SelectedItem is NodeWatchRow row) RemoveWatch(row.Node); }
    private async void JumpNodeWatch_Click(object sender, RoutedEventArgs e)
    { if (NodeWatchGrid.SelectedItem is NodeWatchRow row) { await NavigateNode(row.Node); Tabs.SelectedIndex = 0; } }
    private async void JumpWatchChange_Click(object sender, MouseButtonEventArgs e)
    {
        if (WatchChangesGrid.SelectedItem is not NodeWatchChange row) return;
        if (row.Destroyed) { StatusLabel.Text = "该记录对应的节点已销毁。"; return; }
        await NavigateNode(row.Node); Tabs.SelectedIndex = 0;
    }
    private void ClearWatchChanges_Click(object sender, RoutedEventArgs e) { Watches.Changes.Clear(); PublishNodeWatches(); }
    private async void SampleWatches_Click(object sender, RoutedEventArgs e) => await PollNodeWatches(true);
    private NodeSample Sample(NodeInfo node, NodeDetailResponse data, Guid sessionId)
    {
        var components = new HashSet<string>(); var indices = new Dictionary<string, int>();
        foreach (var component in data.components ?? new())
        {
            string type = component.typeName ?? component.displayName ?? "Unknown";
            int index = indices.GetValueOrDefault(type); indices[type] = index + 1; components.Add(type + " [" + index + "]");
        }
        var fields = BuildDetailRows(data, sessionId).ToDictionary(PropertyKey, r => new WatchField(r.ComponentGroup, r.FieldLabel, r.Value));
        return new(node.path, new() { ["名称"] = node.name, ["路径"] = node.path, ["自身激活"] = node.activeSelf.ToString(), ["层级激活"] = node.activeInHierarchy.ToString(), ["Layer"] = node.layer.ToString(), ["Tag"] = node.tag, ["子节点"] = string.Join(",", node.childIds ?? new()) }, components, fields);
    }
    private static string ComponentLabel(string key) => new ComponentTitleConverter().Convert(key, typeof(string), null, CultureInfo.InvariantCulture).ToString();
    private void RecordSample(WatchSession state, int id, NodeSample current)
    {
        void Add(string kind, string field, string before, string after) => state.Changes.Add(new(id, DateTime.Now.ToString("HH:mm:ss"), current.Path, kind, field, before, after));
        if (state.Samples.TryGetValue(id, out var previous))
        {
            foreach (var field in current.NodeValues)
                if (previous.NodeValues.GetValueOrDefault(field.Key) != field.Value) Add("节点变化", field.Key, previous.NodeValues.GetValueOrDefault(field.Key), field.Value);
            foreach (string component in current.Components.Except(previous.Components)) Add("组件新增", ComponentLabel(component), "—", "已添加");
            foreach (string component in previous.Components.Except(current.Components)) Add("组件移除", ComponentLabel(component), "存在", "已移除");
            foreach (string key in previous.Fields.Keys.Union(current.Fields.Keys))
            {
                previous.Fields.TryGetValue(key, out var before); current.Fields.TryGetValue(key, out var after);
                var field = after ?? before;
                if (field.Component != "GameObject" && (!previous.Components.Contains(field.Component) || !current.Components.Contains(field.Component))) continue;
                if (before?.Value != after?.Value) Add(field.Label == "enabled" ? "组件启用变化" : "参数变化", ComponentLabel(field.Component) + " / " + field.Label, before?.Value ?? "—", after?.Value ?? "—");
            }
        }
        state.Samples[id] = current;
        state.Rows[id] = new(id, current.Path, "监听中 · " + current.Components.Count + " 个组件", DateTime.Now.ToString("HH:mm:ss"));
    }
    public async Task PollNodeWatches(bool force = false)
    {
        if (closing || snapshot != null || Selected?.Session.IsConnected != true || pollingNodeWatches || operation != null || selectionRequest != null || autoDetailBusy) return;
        var session = Selected.Session; var state = Watches;
        if (state.Rows.Count == 0 || !force && (AutoWatchNodes.IsChecked != true || DateTime.UtcNow - state.LastPoll < TimeSpan.FromSeconds(WatchInterval.SelectedIndex switch { 0 => 1, 2 => 5, _ => 2 }))) return;
        pollingNodeWatches = true; state.LastPoll = DateTime.UtcNow;
        using var request = new CancellationTokenSource(); nodeWatchRequest = request;
        try
        {
            await session.RefreshTreeAsync(request.Token);
            if (!IsCurrent(session) || request.IsCancellationRequested) return;
            var nodes = session.Tree.allNodes.ToDictionary(n => n.instanceId);
            foreach (int id in state.Rows.Keys.ToArray())
            {
                request.Token.ThrowIfCancellationRequested();
                if (!state.Rows.ContainsKey(id)) continue;
                if (!nodes.TryGetValue(id, out var node))
                {
                    state.Changes.Add(new(id, DateTime.Now.ToString("HH:mm:ss"), state.Rows[id].Path, "节点销毁", "自动取消监听", "存在", "已销毁", true));
                    state.Rows.Remove(id); state.Samples.Remove(id);
                    if (selectedNode == id) { selectionRequest?.Cancel(); selectedNode = 0; Selected.SelectedNode = 0; detail = null; PropertyRows.Clear(); RawDetail.Clear(); NodeLabel.Text = "节点已销毁"; NodeIdentity.Text = ""; }
                    monitors.GetValueOrDefault(session.Id)?.RemoveAll(r => r.Node == id); continue;
                }
                await session.GetDetailAsync(id, request.Token);
                if (!IsCurrent(session) || !state.Rows.ContainsKey(id)) continue;
                RecordSample(state, id, Sample(node, session.Details[id], session.Id));
            }
            if (state.Changes.Count > 1000) state.Changes.RemoveRange(0, state.Changes.Count - 1000);
            WatchStatus.Text = "最近采样 " + DateTime.Now.ToString("HH:mm:ss") + $" · {state.Rows.Count} 个节点 · {state.Changes.Count} 条变化";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (IsCurrent(session)) WatchStatus.Text = "采样失败，保留监听：" + ex.Message; }
        finally
        {
            state.LastPoll = DateTime.UtcNow; if (nodeWatchRequest == request) nodeWatchRequest = null; pollingNodeWatches = false;
            if (!closing && IsCurrent(session)) { if (state.Changes.Count > 1000) state.Changes.RemoveRange(0, state.Changes.Count - 1000); PublishNodeWatches(); PublishMonitorRows(); }
        }
    }
}
