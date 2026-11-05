using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteInspector.Commands;
using RemoteInspector.Core;
using RemoteInspector.Protocol;

namespace RemoteInspector.Desktop;

public sealed class ReflectionRpcCache
{
    public ReflectionRpcResponse Directory;
    public Dictionary<string, ReflectionRpcResponse> Methods { get; } = new();
}
public sealed record RpcAssemblyChoice(string Label, string Value);
public sealed record ReflectionTypeRow(ReflectionRpcType Value) { public string Name => Value.name; public string Description => Value.name + "\n" + Value.assembly; }
public sealed record ReflectionMethodRow(ReflectionRpcMethod Value) { public string Signature => Value.signature; public bool Callable => Value.callable; public string Reason => Value.reason; }
public sealed record RpcInstanceChoice(string Label, int Id);
public partial class ReflectionRpcView : UserControl, IDisposable
{
    private readonly InspectorSession session;
    private readonly ReflectionRpcCache cache;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Func<string>> editors = new();
    private ReflectionRpcType selectedType;
    private ReflectionRpcMethod selectedMethod;
    private bool busy, initializing = true;
    private ReflectionRpcResponse publishedDirectory;
    public ReflectionRpcView(InspectorSession session, ReflectionRpcCache cache, bool embedded = false)
    {
        this.session = session; this.cache = cache; InitializeComponent();
        if (embedded) { BrowserHeader.Visibility = Visibility.Collapsed; BrowserRoot.Margin = new Thickness(4); }
        ConnectionInfo.Text = session.DisplayName;
        AssemblyPicker.ItemsSource = new[] { new RpcAssemblyChoice("全部程序集", "") }; AssemblyPicker.SelectedIndex = 0;
        if (cache.Directory != null) PublishDirectory(cache.Directory);
        Method_Selected(this, null);
        RefreshCallFavorites();
        initializing = false; UpdateButtons();
        JsonMode_Changed(this, null);
        IsVisibleChanged += (_, _) => { if (IsVisible && !busy && cache.Directory != null && !ReferenceEquals(publishedDirectory, cache.Directory)) PublishDirectory(cache.Directory); };

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; Execute_Click(this, e); } };
    }
    public void Dispose() { if (!lifetime.IsCancellationRequested) lifetime.Cancel(); }
    private static string CacheKey(ReflectionRpcType type) => type.assembly + "\n" + type.name;
    private ReflectionRpcRequest Request() => new() { assembly = selectedType?.assembly, type = selectedType?.name, method = selectedMethod?.key,
        includeInactive = IncludeInactive.IsChecked == true, includeOtherScenes = IncludeOtherScenes.IsChecked == true,
        targetId = (InstancePicker.SelectedItem as RpcInstanceChoice)?.Id ?? 0, argsJson = ArgumentsJson.Text };
    private async Task<ReflectionRpcResponse> Send(ushort command, ushort response, ReflectionRpcRequest request)
    {
        var packet = await session.RequestAsync((Command)command, request, lifetime.Token, 20, (Command)response);
        var value = Wire.Body<ReflectionRpcResponse>(packet);
        if (!value.success) throw new InvalidOperationException(value.error ?? "远端请求失败");
        return value;
    }
    private async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; UpdateButtons();
        try { await action(); }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) Status.Text = "响应等待已超时；执行请求可能仍在远端运行，请确认后再重试。"; }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) { Status.Text = ex.Message; Result.Text = ex.Message; } }
        finally { busy = false; if (!lifetime.IsCancellationRequested) UpdateButtons(); }
    }
    private void UpdateButtons()
    {
        if (Execute == null) return;
        ParameterHeading.Visibility = JsonMode.Visibility = selectedMethod == null ? Visibility.Collapsed : Visibility.Visible;
        RefreshDirectory.IsEnabled = !busy; RefreshMethods.IsEnabled = !busy && selectedType != null;
        RefreshInstances.IsEnabled = !busy && selectedMethod != null && !selectedMethod.isStatic;
        TypesGrid.IsEnabled = MethodsGrid.IsEnabled = AssemblyPicker.IsEnabled = !busy;
        ClassSearch.IsEnabled = MethodSearch.IsEnabled = JsonMode.IsEnabled = ArgumentsJson.IsEnabled = !busy;
        IncludeInactive.IsEnabled = IncludeOtherScenes.IsEnabled = !busy;
        ParameterEditors.IsEnabled = !busy && JsonMode.IsChecked != true;
        InstancePicker.IsEnabled = !busy && selectedMethod != null && !selectedMethod.isStatic;
        bool valid = false;
        try { using var json = JsonDocument.Parse(ArgumentsJson.Text); valid = json.RootElement.ValueKind == JsonValueKind.Array; } catch (JsonException) { }
        Validation.Text = selectedMethod == null ? "" : !selectedMethod.callable ? selectedMethod.reason : valid ? "" : "参数必须为合法 JSON 数组";
        Execute.IsEnabled = !busy && selectedMethod?.callable == true && valid;
    }
    private async void RefreshDirectory_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        Status.Text = "正在获取程序集与类…";
        var value = await Send(ReflectionRpcProtocol.Query, ReflectionRpcProtocol.QueryResponse, new() { assembly = (AssemblyPicker.SelectedItem as RpcAssemblyChoice)?.Value, search = ClassSearch.Text.Trim(), refresh = true });
        cache.Directory = value; cache.Methods.Clear(); PublishDirectory(value);
        Status.Text = $"目录已刷新 · {value.types.Count} 类" + (value.truncated ? " · 结果已截断，请缩小程序集或关键字再刷新" : "");
    });
    private void PublishDirectory(ReflectionRpcResponse value)
    {
        publishedDirectory = value;
        string selected = (AssemblyPicker.SelectedItem as RpcAssemblyChoice)?.Value ?? "";
        initializing = true;
        var choices = new[] { new RpcAssemblyChoice("全部程序集", "") }.Concat(value.assemblies.Select(a => new RpcAssemblyChoice(a.Split(',')[0], a))).ToArray();
        AssemblyPicker.ItemsSource = choices; AssemblyPicker.SelectedItem = choices.FirstOrDefault(c => c.Value == selected) ?? choices[0];
        initializing = false; FilterTypes();
        Status.Text = "已加载本会话缓存，点击刷新可重新获取远端数据。";
    }
    private void Assembly_Changed(object sender, SelectionChangedEventArgs e) { if (!initializing) FilterTypes(); }
    private void ClassSearch_Changed(object sender, TextChangedEventArgs e) { if (!initializing) FilterTypes(); }
    private void FilterTypes()
    {
        string assembly = (AssemblyPicker.SelectedItem as RpcAssemblyChoice)?.Value ?? "";
        string[] words = ClassSearch.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        var rows = (cache.Directory?.types ?? new()).Where(t => (assembly.Length == 0 || t.assembly == assembly) && words.All(w => SearchHighlight.Matches(t.name, w))).ToArray();
        TypesGrid.ItemsSource = rows.Select(t => new ReflectionTypeRow(t)).ToArray(); ClassCount.Text = $"缓存匹配 {rows.Length} 类 · 无结果时点刷新目录";
    }
    private async void Type_Selected(object sender, SelectionChangedEventArgs e)
    {
        selectedType = (TypesGrid.SelectedItem as ReflectionTypeRow)?.Value; selectedMethod = null;
        MethodSearch.Clear();
        ParameterEditors.Children.Clear(); editors.Clear(); InstancePicker.ItemsSource = null; SelectedSignature.Text = "选择方法";
        FilterMethods(); UpdateButtons();
        var type = selectedType;
        if (type == null || cache.Methods.ContainsKey(CacheKey(type))) return;
        try
        {
            // Directory publication or favorite restoration may still own the request gate.
            while (busy && !lifetime.IsCancellationRequested) await Task.Delay(20, lifetime.Token);
            if (lifetime.IsCancellationRequested || selectedType != type || cache.Methods.ContainsKey(CacheKey(type))) return;
            await Run(() => LoadTypeMethods(type));
        }
        catch (OperationCanceledException) { }
    }
    private async Task LoadTypeMethods(ReflectionRpcType type)
    {
        Status.Text = "正在加载 " + type.name + " 的方法…";
        var value = await Send(ReflectionRpcProtocol.Query, ReflectionRpcProtocol.QueryResponse, new() { assembly = type.assembly, type = type.name });
        if (cache.Methods.Count >= 64) cache.Methods.Clear(); cache.Methods[CacheKey(type)] = value;
        if (selectedType != type) return;
        FilterMethods(); Status.Text = $"已加载 {type.name}：{value.methods.Count} 个方法，可直接搜索签名。";
    }
    private async void RefreshMethods_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var value = await Send(ReflectionRpcProtocol.Query, ReflectionRpcProtocol.QueryResponse, Request());
        if (cache.Methods.Count >= 64) cache.Methods.Clear(); cache.Methods[CacheKey(selectedType)] = value;
        FilterMethods(); Status.Text = $"已刷新 {selectedType.name}：远端返回 {value.methods.Count} 个方法；仅列出该类声明的方法。";
    });
    private void ClearMethodSearch_Click(object sender, RoutedEventArgs e) => MethodSearch.Clear();
    private void MethodSearch_Changed(object sender, TextChangedEventArgs e) { if (!initializing) FilterMethods(); }
    private void FilterMethods()
    {
        ReflectionRpcResponse data = null;
        if (selectedType != null) cache.Methods.TryGetValue(CacheKey(selectedType), out data);
        string[] words = MethodSearch.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        var methods = data?.methods ?? new();
        var matches = methods.Where(m => words.All(w => SearchHighlight.Matches(m.signature, w))).Select(m => new ReflectionMethodRow(m)).ToArray();
        MethodsGrid.ItemsSource = matches;
        MethodCount.Text = selectedType == null ? "先选择左侧类，再点击刷新方法" : data == null ? "尚未加载 · 点击刷新方法获取远端列表" : methods.Count == 0
            ? "远端返回 0 个方法 · 当前类没有可枚举的声明方法（不含字段、构造器和属性访问器）；若为 IL2CPP 包，请检查代码裁剪。"
            : matches.Length == 0 ? $"无匹配 · 已加载 {methods.Count} 个方法，清除搜索查看全部"
            : $"匹配 {matches.Length} / {methods.Count} 个方法 · 灰色项可查看不支持原因";
        MethodEmptyState.Text = selectedType == null ? "选择业务类开始查找" : data == null ? "点击右上角「刷新方法」加载" : methods.Count == 0 ? "该类未返回声明方法\n请确认选中的是业务脚本类" : "搜索无匹配，点击 × 清除";
        MethodEmptyState.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Method_Selected(object sender, SelectionChangedEventArgs e)
    {
        selectedMethod = (MethodsGrid.SelectedItem as ReflectionMethodRow)?.Value; editors.Clear(); ParameterEditors.Children.Clear();
        Timing.Text = "尚未执行当前方法"; Result.Clear();
        var method = selectedMethod; InstanceOptions.Visibility = method != null && !method.isStatic ? Visibility.Visible : Visibility.Collapsed; SelectedSignature.Text = method == null ? "选择方法" : selectedType.name + "\n" + method.signature;
        InstancePicker.ItemsSource = new[] { new RpcInstanceChoice(method?.isStatic == true ? "静态方法，无需实例" : "优先当前场景激活实例（唯一时调用）", 0) }; InstancePicker.SelectedIndex = 0;
        InstancePicker.IsEnabled = method != null && !method.isStatic;
        if (method != null) foreach (var parameter in method.parameters)
        {
            ParameterEditors.Children.Add(new TextBlock { Text = parameter.name + "  ·  " + parameter.type + (parameter.optional ? "（可选）" : ""), Margin = new Thickness(4, 7, 4, 2), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = parameter.type });
            if (parameter.choices?.Length > 0)
            {
                string initialChoice = parameter.defaultJson ?? "";
                try { using var value = JsonDocument.Parse(initialChoice); initialChoice = value.RootElement.ValueKind == JsonValueKind.String ? value.RootElement.GetString() : value.RootElement.GetRawText(); } catch { }
                var choice = new ComboBox { ItemsSource = parameter.choices, IsEditable = parameter.type != "System.Boolean", Text = initialChoice };
                if (parameter.choices.Contains(initialChoice)) choice.SelectedItem = initialChoice;
                editors.Add(() => parameter.type == "System.Boolean" ? choice.Text : JsonSerializer.Serialize(choice.Text));
                choice.SelectionChanged += (_, _) => ComposeArguments(); choice.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => ComposeArguments())); ParameterEditors.Children.Add(choice);
            }
            else
            {
                bool text = parameter.type == "System.String" || parameter.type == "System.Char";
                string initial = parameter.defaultJson ?? "null";
                if (text) { try { initial = JsonSerializer.Deserialize<string>(initial) ?? ""; } catch { initial = ""; } }
                var box = new TextBox { Text = initial, ToolTip = text ? "直接输入文本" : "JSON 值；数组用 []，向量用 {\"x\":0,\"y\":0,\"z\":0}" };
                bool untouched = true;
                editors.Add(() => text ? untouched && parameter.defaultJson == "null" ? "null" : JsonSerializer.Serialize(box.Text) : box.Text.Trim()); box.TextChanged += (_, _) => { untouched = false; ComposeArguments(); }; ParameterEditors.Children.Add(box);
            }
        }
        if (method != null && method.parameters.Count == 0) ParameterEditors.Children.Add(new TextBlock { Text = "此方法无需参数", Margin = new Thickness(4, 8, 4, 8) });
        ComposeArguments(); UpdateButtons();
    }
    private void ComposeArguments() { if (ArgumentsJson != null && JsonMode.IsChecked != true) ArgumentsJson.Text = "[" + string.Join(",", editors.Select(get => get())) + "]"; }
    private void Arguments_Changed(object sender, TextChangedEventArgs e) { if (!initializing) UpdateButtons(); }
    private void JsonMode_Changed(object sender, RoutedEventArgs e) { if (ArgumentsJson == null) return; ArgumentsJson.IsReadOnly = JsonMode.IsChecked != true; ArgumentsJson.Visibility = JsonMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; ParameterEditors.IsEnabled = JsonMode.IsChecked != true; ComposeArguments(); }
    private async void RefreshInstances_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var value = await Send(ReflectionRpcProtocol.Instances, ReflectionRpcProtocol.InstancesResponse, Request());
        var choices = new[] { new RpcInstanceChoice("优先当前场景激活实例（唯一时调用）", 0) }.Concat(value.instances.Select(i => new RpcInstanceChoice(i.path + " · " + i.state + " [" + i.id + "]", i.id))).ToArray();
        InstancePicker.ItemsSource = choices;
        var preferred = value.instances.Where(i => i.priority == 0).ToArray();
        InstancePicker.SelectedItem = preferred.Length == 1 ? choices.First(c => c.Id == preferred[0].id) : choices[0];
        Status.Text = $"{value.instances.Count} 个候选实例" + (preferred.Length > 1 ? " · 多个激活实例，请选择明确目标" : "");
    });
    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        UpdateButtons(); if (!Execute.IsEnabled) return;
        var request = Request();
        await Run(async () =>
        {
            var clock = Stopwatch.StartNew(); Timing.Text = "执行中…"; Result.Clear();
            var packet = await session.RequestAsync((Command)ReflectionRpcProtocol.Invoke, request, lifetime.Token, 20, (Command)ReflectionRpcProtocol.InvokeResponse);
            var value = Wire.Body<ReflectionRpcResponse>(packet);
            Timing.Text = $"{(value.success ? "成功" : "失败")} · 执行 {value.executionTimeMs:F3} ms · 往返 {clock.Elapsed.TotalMilliseconds:F1} ms";
            Result.Text = value.success ? Pretty(value.resultJson) : value.error;
            Status.Text = string.IsNullOrEmpty(value.target) ? "静态方法调用完成" : "调用实例：" + value.target;
        });
    }
    private static string Pretty(string text) { try { using var json = JsonDocument.Parse(text ?? "null"); return JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); } catch { return text ?? ""; } }
    private void CopyResult_Click(object sender, RoutedEventArgs e) { try { if (Result.Text.Length > 0) Clipboard.SetText(Timing.Text + "\n" + Status.Text + "\n" + Result.Text); } catch (Exception ex) { Status.Text = ex.Message; } }
}
