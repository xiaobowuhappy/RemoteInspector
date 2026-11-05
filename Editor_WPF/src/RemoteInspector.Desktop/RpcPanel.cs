using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using RemoteInspector.Commands;
using RemoteInspector.Protocol;

namespace RemoteInspector.Desktop;

public sealed record RpcTemplateChoice(string Label, string Method, string Args);

public partial class MainWindow
{
    private readonly ObservableCollection<RpcRow> rpcFunctions = new();
    private bool rpcBusy, changingRpcTemplates;
    private readonly Dictionary<Guid, (DateTime Updated, List<RpcRow> Rows)> rpcFunctionCache = new();
    private readonly Dictionary<Guid, ReflectionRpcCache> reflectionRpcCaches = new();
    private ReflectionRpcWindow reflectionRpcWindow;
    private ReflectionRpcView embeddedRpcView;
    private void WorkspacePages_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext == null) return;
        if (!FpsTab.IsSelected && businessWindowActive) { operation?.Cancel(); dynamicStackRequest?.Cancel(); }
        if (RpcTab.IsSelected) EnsureEmbeddedRpc();
        if (SceneWorkspaceTab.IsSelected && snapshot == null && Selected?.Session.Tree is { } tree && !ReferenceEquals(renderedTree, tree)) RenderTree(tree);
    }
    private void EnsureEmbeddedRpc()
    {
        if (embeddedRpcView != null || Selected == null || snapshot != null) return;
        var session = Selected.Session;
        if (!reflectionRpcCaches.TryGetValue(session.Id, out var cache)) reflectionRpcCaches[session.Id] = cache = new();
        embeddedRpcView = new ReflectionRpcView(session, cache, embedded: true); ReflectionRpcHost.Content = embeddedRpcView;
    }
    private void ReflectionRpc_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = Live();
            if (reflectionRpcWindow?.IsVisible == true) { reflectionRpcWindow.Activate(); return; }
            if (!reflectionRpcCaches.TryGetValue(session.Id, out var cache)) reflectionRpcCaches[session.Id] = cache = new();
            reflectionRpcWindow = new ReflectionRpcWindow(session, cache) { Owner = this };
            reflectionRpcWindow.Show();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    private void InitializeRpc()
    {
        RpcGrid.ItemsSource = rpcFunctions; RegisteredFunctions.ItemsSource = rpcFunctions;
        CollectionViewSource.GetDefaultView(rpcFunctions).Filter = value => value is RpcRow row &&
            SearchHighlight.Matches($"{row.Name} {row.Signature} {row.Description}", RpcFunctionSearch.Text);
        ValidateRpcEditor();
    }
    private void ResetRpc()
    {
        RpcModes.SelectedIndex = 0;
        reflectionRpcWindow?.Close(); reflectionRpcWindow = null;
        embeddedRpcView?.Dispose(); embeddedRpcView = null;
        ReflectionRpcHost.Content = new TextBlock { Text = "连接游戏后，点击刷新目录开始查找。", Margin = new Thickness(20) };
        if (RpcTab.IsSelected) EnsureEmbeddedRpc();
        RpcFunctionPicker.IsOpen = false; RpcSuggestions.IsOpen = false; rpcFunctions.Clear(); RpcResult.Clear(); RpcSignature.Text = "";
        RpcCallStatus.Text = "尚未执行"; RpcToolStatus.Text = ""; RpcResultPanel.IsExpanded = true;
        if (Selected != null && rpcFunctionCache.TryGetValue(Selected.Session.Id, out var cached))
        { foreach (var row in cached.Rows) rpcFunctions.Add(row); RpcListStatus.Text = $"缓存 {cached.Rows.Count} 个函数 · {cached.Updated:HH:mm:ss} · 刷新重新拉取"; }
        else RpcListStatus.Text = "点击刷新列表获取远端函数；打开不会自动拉取";
    }
    private void RpcEditor_Changed(object sender, TextChangedEventArgs e)
    {
        if (RpcExecute == null) return;
        ValidateRpcEditor();
        if (!selectingSuggestion && RpcMethod.IsKeyboardFocusWithin && RpcSuggestions != null)
        {
            RpcSuggestionList.ItemsSource = rpcFunctions.Where(r => SearchHighlight.Matches($"{r.Name} {r.Signature} {r.Description}", RpcMethod.Text)).Take(50).ToArray();
            RpcSuggestions.IsOpen = !string.IsNullOrWhiteSpace(RpcMethod.Text) && RpcSuggestionList.Items.Count > 0;
        }
        var row = rpcFunctions.FirstOrDefault(r => r.Name == RpcMethod.Text.Trim());
        RpcSignature.Text = row == null ? "" : row.Name + row.Signature + (string.IsNullOrWhiteSpace(row.Description) ? "" : " · " + row.Description);
        RpcSignature.ToolTip = RpcSignature.Text;
    }
    private bool selectingSuggestion;
    private void CommitRpcSuggestion()
    {
        if (RpcSuggestionList.SelectedItem is not RpcRow row) return;
        selectingSuggestion = true;
        try { RpcMethod.Text = row.Name; RpcMethod.CaretIndex = RpcMethod.Text.Length; RpcSuggestions.IsOpen = false; RpcMethod.Focus(); }
        finally { RpcSuggestionList.SelectedIndex = -1; selectingSuggestion = false; }
    }
    private void RpcSuggestion_MouseUp(object sender, MouseButtonEventArgs e) => CommitRpcSuggestion();
    private void RpcSuggestionList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitRpcSuggestion(); e.Handled = true; }
        else if (e.Key == Key.Escape) { RpcSuggestions.IsOpen = false; RpcMethod.Focus(); e.Handled = true; }
    }
    private void RpcSuggestion_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { RpcSuggestions.IsOpen = false; e.Handled = true; }
        else if (RpcSuggestions.IsOpen && (e.Key == Key.Down || e.Key == Key.Enter))
        {
            if (RpcSuggestionList.Items.Count > 0)
            {
                RpcSuggestionList.SelectedIndex = 0;
                if (e.Key == Key.Enter) CommitRpcSuggestion();
                else { RpcSuggestionList.Focus(); RpcSuggestionList.UpdateLayout(); (RpcSuggestionList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus(); }
            }
            e.Handled = true;
        }
    }
    private bool ValidateRpcEditor()
    {
        if (RpcExecute == null) return false;
        string message = "";
        if (string.IsNullOrWhiteSpace(RpcMethod.Text)) message = "请选择或输入方法名称";
        else
        {
            try { using var json = JsonDocument.Parse(RpcArgs.Text); if (json.RootElement.ValueKind != JsonValueKind.Array) message = "参数必须为 JSON 数组，例如 [42, \"hello\"]；无参数填写 []"; }
            catch (JsonException) { message = "JSON 格式有误，请检查引号、逗号和括号"; }
        }
        RpcValidation.Text = message; RpcValidation.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RpcExecute.IsEnabled = !rpcBusy && message.Length == 0;
        return message.Length == 0;
    }
    private void RegisteredSearch_Changed(object sender, TextChangedEventArgs e)
    { if (RpcFunctionSearch != null) RpcFunctionSearch.Text = RegisteredSearch.Text; }
    private void RpcRegistered_Selected(object sender, SelectionChangedEventArgs e)
    { if (RegisteredFunctions.SelectedItem is RpcRow row) { RpcMethod.Text = row.Name; RpcEditor_Changed(sender, null); } }
    private void RpcFunctionSearch_Changed(object sender, TextChangedEventArgs e)
    { if (RpcGrid?.ItemsSource != null) CollectionViewSource.GetDefaultView(rpcFunctions).Refresh(); }
    private void RpcChoose_Click(object sender, RoutedEventArgs e)
    {
        RpcModes.SelectedIndex = 1; RpcGrid.SelectedItem = null; RpcFunctionPicker.IsOpen = true;
        if (rpcFunctions.Count == 0) RpcListStatus.Text = "点击刷新列表获取远端函数；打开不会自动拉取";
    }
    private async Task LoadRpcFunctions(CancellationToken token)
    {
        var session = Live(); RpcListStatus.Text = "正在获取函数列表…";
        try
        {
            var data = Wire.Body<RpcListResponse>(await session.RequestAsync(Command.RPC_LIST, new RpcListRequest(), token, 15, Command.RPC_LIST_RESPONSE));
            if (!IsCurrent(session)) return;
            rpcFunctions.Clear(); foreach (var row in data.methods ?? Array.Empty<RpcMethodInfo>()) rpcFunctions.Add(new(row.name, row.signature, row.description));
            rpcFunctionCache[session.Id] = (DateTime.Now, rpcFunctions.ToList());
            RpcListStatus.Text = $"{rpcFunctions.Count} 个函数 · {DateTime.Now:HH:mm:ss} 刷新 · 选择后填写参数";
        }
        catch { if (IsCurrent(session)) RpcListStatus.Text = "获取失败，请点击刷新重试"; throw; }
    }
    private async void RpcRefreshAndShow_Click(object sender, RoutedEventArgs e)
    { RpcModes.SelectedIndex = 1; await Run(LoadRpcFunctions); }
    private async void RpcList_Click(object sender, RoutedEventArgs e) => await Run(LoadRpcFunctions);
    private void Rpc_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RpcGrid.SelectedItem is not RpcRow row) return;
        RpcMethod.Text = row.Name; RpcEditor_Changed(sender, null); RpcFunctionPicker.IsOpen = false;
    }
    private void RefreshRpcTemplates()
    {
        changingRpcTemplates = true;
        try
        {
            RpcTemplatePicker.ItemsSource = rpcTemplates.Select(p => new RpcTemplateChoice(p.Key, p.Key, p.Value)).ToList();
            RpcTemplatePicker.SelectedIndex = -1; RpcTemplatePicker.Text = "参数模板…";
        }
        finally { changingRpcTemplates = false; }
    }
    private void RpcTemplates_Opened(object sender, EventArgs e) => RefreshRpcTemplates();
    private void RpcTemplate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (changingRpcTemplates || RpcTemplatePicker.SelectedItem is not RpcTemplateChoice item) return;
        RpcMethod.Text = item.Method; RpcArgs.Text = item.Args;
    }
    private void Rpc_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; Rpc_Click(sender, e); } }
    private async void Rpc_Click(object sender, RoutedEventArgs e)
    { if (!rpcBusy && ValidateRpcEditor()) await Run(t => ExecuteRpc(RpcMethod.Text.Trim(), RpcArgs.Text, t)); }
    private async void QuickRpc_Click(object sender, RoutedEventArgs e)
    { if (!rpcBusy) await Run(t => ExecuteRpc((string)((Button)sender).Tag, "[]", t, true)); }
    private async Task ExecuteRpc(string name, string args, CancellationToken token, bool tool = false)
    {
        var session = Live(); using var parsedArgs = JsonDocument.Parse(args);
        if (parsedArgs.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("参数必须为 JSON 数组。");
        rpcBusy = true; RpcIFixButton.IsEnabled = RpcILRuntimeButton.IsEnabled = false; ValidateRpcEditor();
        string label = tool ? name == "InitInjectFix" ? "IFix" : "ILRuntime" : name;
        var clock = Stopwatch.StartNew();
        if (tool) RpcToolStatus.Text = label + " 正在重载…";
        else { RpcCallStatus.Text = name + " · 执行中…"; RpcResult.Clear(); }
        try
        {
            var packet = await session.RequestAsync(Command.RPC_CALL, new RpcCallRequest { methodName = name, argsJson = args, timeoutMs = 10000 }, token, 15, Command.RPC_RESPONSE);
            if (!IsCurrent(session)) return;
            var response = Wire.Body<RpcCallResponse>(packet);
            string status = $"{label} · {(response.success ? "成功" : "失败")} · 执行 {response.executionTimeMs} ms · 往返 {clock.ElapsedMilliseconds} ms";
            if (tool) RpcToolStatus.Text = status + (response.success ? "" : " · " + response.error);
            else
            {
                RpcCallStatus.Text = status;
                RpcResult.Text = response.success ? FormatRpcJson(response.resultJson) : response.error ?? "远端调用失败";
                RpcResultPanel.IsExpanded = true;
            }
            StatusLabel.Text = status;
        }
        catch (Exception ex)
        {
            if (IsCurrent(session))
            {
                string message = ex is OperationCanceledException ? "调用已取消或响应超时" : ex.Message;
                if (tool) RpcToolStatus.Text = label + " · " + message;
                else { RpcCallStatus.Text = label + " · 调用未完成"; RpcResult.Text = message; RpcResultPanel.IsExpanded = true; }
            }
            throw;
        }
        finally { rpcBusy = false; RpcIFixButton.IsEnabled = RpcILRuntimeButton.IsEnabled = true; ValidateRpcEditor(); }
    }
    private static string FormatRpcJson(string text)
    {
        try { using var json = JsonDocument.Parse(text ?? "null"); return JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); }
        catch (JsonException) { return text ?? ""; }
    }
    private void CopyRpcResult_Click(object sender, RoutedEventArgs e)
    { try { if (!string.IsNullOrEmpty(RpcResult.Text)) Clipboard.SetText(RpcResult.Text); } catch (Exception ex) { StatusLabel.Text = "复制结果失败：" + ex.Message; } }
}
