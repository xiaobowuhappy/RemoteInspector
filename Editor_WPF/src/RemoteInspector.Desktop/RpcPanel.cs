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
    private void InitializeRpc()
    {
        RpcGrid.ItemsSource = rpcFunctions;
        CollectionViewSource.GetDefaultView(rpcFunctions).Filter = value => value is RpcRow row &&
            $"{row.Name} {row.Signature} {row.Description}".Contains(RpcFunctionSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        ValidateRpcEditor();
    }
    private void ResetRpc()
    {
        RpcFunctionPicker.IsOpen = false; rpcFunctions.Clear(); RpcResult.Clear(); RpcSignature.Text = "";
        RpcCallStatus.Text = "尚未执行"; RpcToolStatus.Text = ""; RpcResultPanel.IsExpanded = false;
        RpcListStatus.Text = "尚未获取函数列表";
    }
    private void RpcEditor_Changed(object sender, TextChangedEventArgs e)
    {
        if (RpcExecute == null) return;
        ValidateRpcEditor();
        var row = rpcFunctions.FirstOrDefault(r => r.Name == RpcMethod.Text.Trim());
        RpcSignature.Text = row == null ? "" : row.Name + row.Signature + (string.IsNullOrWhiteSpace(row.Description) ? "" : " · " + row.Description);
        RpcSignature.ToolTip = RpcSignature.Text;
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
    private void RpcFunctionSearch_Changed(object sender, TextChangedEventArgs e)
    { if (RpcGrid?.ItemsSource != null) CollectionViewSource.GetDefaultView(rpcFunctions).Refresh(); }
    private async void RpcChoose_Click(object sender, RoutedEventArgs e)
    {
        RpcGrid.SelectedItem = null; RpcFunctionPicker.IsOpen = true;
        if (rpcFunctions.Count == 0) await Run(LoadRpcFunctions);
    }
    private async Task LoadRpcFunctions(CancellationToken token)
    {
        var session = Live(); RpcListStatus.Text = "正在获取函数列表…";
        try
        {
            var data = Wire.Body<RpcListResponse>(await session.RequestAsync(Command.RPC_LIST, new RpcListRequest(), token, 15, Command.RPC_LIST_RESPONSE));
            if (!IsCurrent(session)) return;
            rpcFunctions.Clear(); foreach (var row in data.methods ?? Array.Empty<RpcMethodInfo>()) rpcFunctions.Add(new(row.name, row.signature, row.description));
            RpcListStatus.Text = $"{rpcFunctions.Count} 个函数 · 选择后填写参数，不自动执行";
        }
        catch { if (IsCurrent(session)) RpcListStatus.Text = "获取失败，请点击刷新重试"; throw; }
    }
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
            string status = $"{label} · {(response.success ? "成功" : "失败")} · {clock.ElapsedMilliseconds} ms";
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
