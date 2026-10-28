using System.Buffers.Binary;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteInspector.Commands;
using RemoteInspector.Core;
using RemoteInspector.Core.Models;
using RemoteInspector.Desktop;
using RemoteInspector.Protocol;

internal static class Program
{
    private static int checks;
    public static async Task<int> Main(string[] args)
    {
        string artifacts = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "artifacts/verification");
        Directory.CreateDirectory(artifacts);
        try
        {
            if (args.Contains("--lifecycle-child-stopped") || args.Contains("--lifecycle-child-listening"))
                return await LifecycleChild(artifacts, args.Contains("--lifecycle-child-stopped"));
            if (args.Contains("--ui-only")) { await UiChecks(artifacts); Console.WriteLine($"PASS: {checks} UI assertions"); return 0; }
            if (args.Contains("--shutdown-only")) { await UiChecks(artifacts, true); Console.WriteLine($"PASS: {checks} shutdown assertions"); return 0; }
            await MediaChecks(artifacts);
            await ProtocolChecks();
            SnapshotChecks(artifacts);
            TreeStateChecks();
            PropertyEditorChecks();
            LayerAndOrderChecks(artifacts);
            await IntegrationChecks(artifacts);
            await UiChecks(artifacts);
            await LifecycleProcessChecks(artifacts);
            Console.WriteLine($"PASS: {checks} assertions; artifacts: {artifacts}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    { try { await action(); } catch (T) { Check(true, message); return; } throw new Exception("FAIL: " + message); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static async Task MediaChecks(string artifacts)
    {
        Check(ImageCoordinates.TryNormalize(400, 400, 800, 400, 200, 100, out float u, out float v) && u == .5f && v == 1f, "screenshot click maps the actual image rectangle to Unity bottom-left coordinates");
        Check(!ImageCoordinates.TryNormalize(400, 400, 800, 400, 200, 50, out _, out _), "screenshot letterbox clicks are rejected");
        Check(!ImageCoordinates.TryNormalize(0, 1, 1, 1, 0, 0, out _, out _), "zero-size screenshot cannot produce a ray");
        Check(new FileRow("folder", "folder", true, 0).Icon != new FileRow("a.txt", "a.txt", false, 1).Icon, "remote file and directory icons are distinct");
        string decoded = Path.Combine(artifacts, "decoder-smoke.log");
        await CompressedLogDecoder.DecodeAsync(Path.Combine(AppContext.BaseDirectory, "tools", "LogDecoder", "BqLog_LogDecoder.exe"), Path.Combine(AppContext.BaseDirectory, "fixtures", "decoder-smoke.logcompr"), decoded);
        Check(File.ReadAllText(decoded).Contains("===Awake: Step1") && new FileInfo(decoded).Length > 1000, "bundled BqLog decoder reads a real compressed log fixture");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => CompressedLogDecoder.DecodeAsync(Path.Combine(AppContext.BaseDirectory, "tools", "LogDecoder", "BqLog_LogDecoder.exe"), Path.Combine(AppContext.BaseDirectory, "fixtures", "decoder-smoke.logcompr"), decoded, token: canceled.Token), "compressed log decoding supports cancellation");
    }
    private static async Task ProtocolChecks()
    {
        // Literal expected framing: total=10, command=0x10, sequence=0x1234, body={}
        byte[] golden = { 10, 0, 0, 0, 16, 0, 52, 18, 123, 125 };
        Check(Wire.Encode(new MessagePacket(Command.GET_NODE_TREE, 0x1234, "{}")).SequenceEqual(golden), "8-byte little-endian wire header");
        await using var fragmented = new FragmentedStream(golden.Concat(golden).ToArray());
        Check((await Wire.ReadAsync(fragmented)).sequence == 0x1234 && (await Wire.ReadAsync(fragmented)).bodyJson == "{}", "fragmented and coalesced frames");
        var invalid = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(invalid, int.MaxValue);
        await Throws<InvalidDataException>(() => Wire.ReadAsync(new MemoryStream(invalid)), "oversized frame rejected before allocation");
        await Throws<EndOfStreamException>(() => Wire.ReadAsync(new MemoryStream(golden[..9])), "truncated frame rejected");
        var json = "{\"instanceId\":42,\"name\":\"测试节点\",\"activeSelf\":true,\"components\":[{\"typeName\":\"Game.Test\",\"serializedFields\":[{\"fieldName\":\"speed\",\"valueType\":\"number\",\"fieldType\":\"Single\",\"doubleValue\":2.5}]}]}";
        var detail = Wire.Body<NodeDetailResponse>(new MessagePacket(Command.NODE_DETAIL_RESPONSE, 4, json));
        Check(detail.instanceId == 42 && detail.components[0].serializedFields[0].doubleValue == 2.5, "Unity-style public-field JSON / Unicode / nested list");
        foreach (bool v1 in new[] { true, false })
        {
            var decoded = BinaryNodeTreeCodec.Decode(BinaryFixture(v1, 1));
            Check(decoded?.allNodes[0].componentTypes.Single() == "Transform" && decoded.rootIndices[0] == 0, $"independent binary tree v{(v1 ? 1 : 2)} fixture");
        }
        var large = BinaryNodeTreeCodec.Decode(BinaryFixture(false, 65536));
        Check(large?.totalNodes == 65536 && large.allNodes.Count == 65536, "binary v2 preserves counts above 65535");
        Check(BinaryNodeTreeCodec.Decode(BinaryFixture(false, 1)[..^1]) == null, "truncated binary tree rejected");
        var bad = BinaryFixture(false, 1); BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(2), int.MaxValue);
        Check(BinaryNodeTreeCodec.Decode(bad) == null, "binary count allocation bounded");
    }
    private static byte[] BinaryFixture(bool v1, int count)
    {
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, Encoding.UTF8, true);
        void Count(int value) { if (v1) writer.Write((ushort)value); else writer.Write(value); }
        void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); Count(bytes.Length); writer.Write(bytes); }
        writer.Write((byte)(v1 ? 1 : 2)); writer.Write((byte)0); Count(count); Count(1); Count(0);
        Count(1); Text("Transform");
        for (int i = 0; i < count; i++)
        { writer.Write(i + 42); Text("Root" + i); writer.Write((byte)3); Text("Untagged"); writer.Write((sbyte)0); Count(0); Count(1); writer.Write((ushort)0); }
        writer.Flush(); return memory.ToArray();
    }
    private static void SnapshotChecks(string artifacts)
    {
        string golden = Path.Combine(artifacts, "legacy-v1.bin");
        using (var writer = new BinaryWriter(File.Create(golden)))
        {
            void Text(string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
            writer.Write("RISNP"u8.ToArray()); writer.Write((short)1); writer.Write((short)0); writer.Write(123L);
            writer.Write(1); writer.Write(42); Text("{\"instanceId\":42,\"name\":\"旧快照\",\"components\":[]}");
            Text("{\"allNodes\":[{\"instanceId\":42,\"name\":\"旧快照\"}],\"rootNodes\":[{\"instanceId\":42,\"name\":\"旧快照\"}]}");
            Text("{\"selectedInstanceId\":42,\"selectedPath\":\"Root\",\"expandedPaths\":[\"Root\"]}");
            writer.Write(1); Text("55"); writer.Write(MockRuntime.Image.Length); writer.Write(MockRuntime.Image);
        }
        var loaded = SnapshotFile.Load(golden);
        Check(loaded.NodeDetails[42].name == "旧快照" && loaded.SelectedInstanceId == 42 && loaded.TexturesPng["55"].SequenceEqual(MockRuntime.Image), "independently authored legacy RISNP v1 snapshot");
        string saved = Path.Combine(artifacts, "roundtrip.bin"); SnapshotFile.Save(saved, loaded);
        using (var reader = new BinaryReader(File.OpenRead(saved)))
        {
            Check(Encoding.ASCII.GetString(reader.ReadBytes(5)) == "RISNP" && reader.ReadInt16() == 1, "export retains legacy magic and version");
            reader.ReadInt16(); reader.ReadInt64();
            Check(reader.BaseStream.Position == 17 && reader.ReadInt32() == 1, "snapshot has exact 17-byte legacy header");
        }
        Check(SnapshotFile.Load(saved).NodeDetails[42].instanceId == 42, "snapshot roundtrip");
        string truncated = Path.Combine(artifacts, "truncated.bin"); File.WriteAllBytes(truncated, File.ReadAllBytes(golden)[..^1]);
        try { SnapshotFile.Load(truncated); throw new Exception("Truncated snapshot accepted."); }
        catch (EndOfStreamException) { Check(true, "truncated snapshot rejected"); }
    }
    private static void TreeStateChecks()
    {
        var root = new NodeInfo { instanceId = 1, name = "ActiveRoot", path = "ActiveRoot", activeSelf = true, activeInHierarchy = true, childIds = new() { 2 } };
        var disabled = new NodeInfo { instanceId = 2, name = "DisabledParent", path = "ActiveRoot/DisabledParent", activeSelf = false, activeInHierarchy = false, childIds = new() { 3 } };
        var child = new NodeInfo { instanceId = 3, name = "EnabledChild", path = "ActiveRoot/DisabledParent/EnabledChild", activeSelf = true, activeInHierarchy = false, componentTypes = new() { "Camera" }, childIds = new() };
        var tree = new NodeTreeResponse { allNodes = new() { root, disabled, child }, rootNodes = new() { root } };
        var expanded = new HashSet<int> { 1, 2 };
        var all = new TreeProjection(tree, "", 0, expanded, 3);
        var r = all.Roots.Single(); r.Load(); var d = r.Children.Single(); d.Load(); var c = d.Children.Single();
        Check(r.State == "已激活" && d.State == "自身未激活" && c.State == "父级未激活", "tree distinguishes active, self-disabled and ancestor-disabled nodes");
        Check(c.NameColor == d.NameColor && c.NameColor != r.NameColor, "ancestor-disabled child is dimmed like the old Editor");
        Check(all.ActiveCount == 1 && all.InactiveCount == 2, "activation counts use activeInHierarchy rather than activeSelf");
        var inactive = new TreeProjection(tree, "", 2, expanded, 3);
        var ir = inactive.Roots.Single(); ir.Load(); var id = ir.Children.Single(); id.Load();
        Check(inactive.MatchCount == 2 && ir.IsContext && !id.IsContext && id.Children.Single().State == "父级未激活", "inactive filter retains active parent context without counting it as a match");
        var active = new TreeProjection(tree, "", 1, expanded, 3); active.Roots.Single().Load();
        Check(active.MatchCount == 1 && active.Roots.Single().Children.Count == 0, "active-only filter excludes activeSelf-only descendants");
        var search = new TreeProjection(tree, "Camera", 2, expanded, 3); search.Roots.Single().Load();
        var path = search.Roots.Single().Children.Single(); path.Load();
        Check(search.MatchCount == 1 && path.IsContext && path.Children.Single().IsSelected, "component search keeps ancestor chain and inactive selection");
        var refreshed = new TreeProjection(tree, "", 0, expanded, 3); refreshed.Roots.Single().Load();
        Check(refreshed.Roots.Single().IsExpanded && refreshed.Roots.Single().Children.Single().IsExpanded, "tree refresh retains expanded IDs");
        child.activeInHierarchy = true; disabled.activeSelf = true; disabled.activeInHierarchy = true;
        var updated = new TreeProjection(tree, "", 2, expanded, 3);
        Check(updated.MatchCount == 0 && updated.ActiveCount == 3, "refreshed parent activation updates descendant classification");
    }
    private static async Task IntegrationChecks(string artifacts)
    {
        await using var host = new InspectorHost();
        host.Start(0, IPAddress.Loopback);
        await using var a = await MockRuntime.Connect(host.Port, "Device A");
        await using var b = await MockRuntime.Connect(host.Port, "Device B", binary: true);
        await Until(() => host.Sessions.Length == 2 && host.Sessions.All(s => s.Hello != null));
        var sa = host.Sessions.Single(s => s.Hello.deviceName == "Device A");
        var sb = host.Sessions.Single(s => s.Hello.deviceName == "Device B");
        await Task.WhenAll(sa.RefreshTreeAsync(), sb.RefreshTreeAsync());
        Check(sa.Tree.allNodes.Single().name == "Device A" && sb.Tree.allNodes.Single().name == "Device B", "two simultaneous clients keep independent node trees");
        Check(sb.Tree.allNodes[0].path == "Root/Device B", "binary tree responses reach the session model");
        await Task.WhenAll(sa.GetDetailAsync(42), sb.GetDetailAsync(42));
        Check(sa.Details[42].name == "Device A" && sb.Details[42].name == "Device B", "same instance IDs cannot cross sessions");
        var response = Wire.Body<SetPropertyResponse>(await sa.RequestAsync(Command.SET_PROPERTY, new SetPropertyRequest { instanceId = 42, componentType = "Game.Test", propertyPath = "speed", numberValue = 9.25 }, default, 5, Command.SET_PROPERTY_RESPONSE));
        await sa.GetDetailAsync(42);
        Check(response.success && sa.Details[42].components[0].serializedFields[0].doubleValue == 9.25, "property write acknowledgement followed by readback");
        var rpc = Wire.Body<RpcCallResponse>(await sa.RequestAsync(Command.RPC_CALL, new RpcCallRequest { methodName = "Echo", argsJson = "[42]" }, default, 5, Command.RPC_RESPONSE));
        Check(rpc.resultJson == "[42]", "RPC request and response correlation");
        Check((await sa.GetTextureAsync(55)).SequenceEqual(MockRuntime.Image), "legacy texture response sequence zero and base64 string slices");
        Check((await sa.CaptureAsync()).SequenceEqual(MockRuntime.Image), "screenshot independently encoded chunk slices");
        string download = Path.Combine(artifacts, "download.txt");
        await sa.DownloadAsync("test.txt", false, download);
        Check(File.ReadAllText(download) == "hello remote", "download streams chunks and commits complete file");
        string failed = Path.Combine(artifacts, "failed.txt"); File.WriteAllText(failed, "preserve");
        await Throws<InvalidDataException>(() => sa.DownloadAsync("bad.txt", false, failed), "out-of-order download rejected");
        Check(File.ReadAllText(failed) == "preserve" && !Directory.GetFiles(artifacts, "*.partial").Any(), "failed download preserves destination and removes partial file");
        string upload = Path.Combine(artifacts, "upload.bin"); byte[] bytes = Enumerable.Range(0, 150000).Select(i => (byte)i).ToArray(); File.WriteAllBytes(upload, bytes);
        await sa.UploadAsync(upload, "patch/upload.bin");
        Check(a.Uploaded.SequenceEqual(bytes), "multi-chunk upload with final acknowledgement");
        string empty = Path.Combine(artifacts, "empty.bin"); File.WriteAllBytes(empty, Array.Empty<byte>());
        await sa.UploadAsync(empty, "empty.bin"); Check(a.Uploaded.Length == 0, "empty file upload");
        using (var cancel = new CancellationTokenSource(150))
            await Throws<OperationCanceledException>(() => sa.RequestAsync(Command.RPC_CALL, new RpcCallRequest { methodName = "NoReply" }, cancel.Token, 5, Command.RPC_RESPONSE), "cancelled request releases correlation slot");
        await Throws<OperationCanceledException>(() => sa.RequestAsync(Command.RPC_CALL, new RpcCallRequest { methodName = "NoReply" }, default, 1, Command.RPC_RESPONSE), "request timeout without caller cancellation");
        var pending = sb.RequestAsync(Command.RPC_CALL, new RpcCallRequest { methodName = "NoReply" }, default, 5, Command.RPC_RESPONSE);
        sb.Disconnect();
        await Throws<Exception>(async () => await pending, "disconnect terminates pending requests");
        Check(sa.IsConnected, "disconnecting one client preserves the other");
        await host.StopAsync(); Check(!host.IsRunning && host.Sessions.Length == 0, "listener shutdown joins connections");
        host.Start(0, IPAddress.Loopback);
        using var malformed = new TcpClient(); await malformed.ConnectAsync(IPAddress.Loopback, host.Port);
        await Until(() => host.Sessions.Length == 1);
        await malformed.GetStream().WriteAsync(new byte[8]);
        await Until(() => host.Sessions.Length == 0);
        Check(host.IsRunning, "malformed peer is closed without stopping the listener");
        Task[] stops = Enumerable.Range(0, 8).Select(_ => host.StopAsync()).ToArray();
        Check(stops.All(t => ReferenceEquals(t, stops[0])), "concurrent stops share one cleanup task");
        await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(3));
        await host.DisposeAsync();
        host.Start(0, IPAddress.Loopback);
        Check(host.IsRunning, "listener can restart after concurrent stop and dispose");
        await host.StopAsync();
    }
    private enum NonSequentialState { None = -5, Ready = 10, Done = 42 }
    private static void PropertyEditorChecks()
    {
        var catalog = EnumCatalog.Scan(new[] { typeof(Program).Assembly.Location });
        var values = catalog.Resolve(nameof(NonSequentialState), Enum.GetNames<NonSequentialState>(), "Ready", 10);
        Check(values?["Done"] == 42 && values["None"] == -5, "metadata enum catalog preserves non-contiguous and negative constants");
        var choices = PropertyRow.Presets("enum", "10", enumValues: values);
        Check(choices.Single(c => c.Label.StartsWith("Done")).Value == "42", "enum choices transmit actual value rather than menu index");
        Check(catalog.Resolve(nameof(NonSequentialState), Enum.GetNames<NonSequentialState>(), "Ready", 999) == null, "mismatched enum metadata is not used");
        Check(PropertyRow.Presets("enum", "0").Count == 0, "enum names without verified constants do not guess a mapping");
        Check(PropertyRow.Presets("int", "0").All(c => c.Value != "0.5"), "integer presets exclude fractional values");
        Check(PropertyRow.Presets("layerMask", "0").Any(c => c.Value == int.MinValue.ToString()), "layer mask presets support the signed bit 31");
        var color = PropertyRow.Presets("color", "0,0,0,0").Single(c => c.Label == "白色");
        var row = new PropertyRow { Label = "Color", Type = "color", Value = color.Value, Request = new SetPropertyRequest { valueType = "materialColor", intValue = 2, stringValue = "_Color" } };
        Check(row.ToRequest().w == 1 && row.ToRequest().intValue == 2 && row.ToRequest().valueType == "materialColor", "color preset preserves material slot and protocol type");
    }
    private static void LayerAndOrderChecks(string artifacts)
    {
        var layers = LayerCatalog.Load(Path.Combine(AppContext.BaseDirectory, "layers.json"));
        Check(layers.Names.Length == 32 && layers.Name(0) == "Default" && layers.Name(5) == "UI" && layers.Name(13) == "Player", "project TagManager layer slots are preserved without compacting empty indices");
        var mask = new PropertyRow { Type = "layerMask", Value = "0", Layers = layers, Request = new SetPropertyRequest() };
        mask.ToggleLayer(0, true); mask.ToggleLayer(5, true); mask.ToggleLayer(31, true);
        Check(mask.ToRequest().intValue == unchecked((int)0x80000021) && mask.LayerMaskText.Contains("UI"), "LayerMask preserves the sign bit and named layer selections");
        mask.ToggleLayer(5, false); Check((mask.ToRequest().intValue & 32) == 0 && (mask.ToRequest().intValue & 1) != 0, "LayerMask deselection changes only the chosen bit");
        Check(layers.Summary(0) == "Nothing" && layers.Summary(-1) == "Everything", "LayerMask Nothing/Everything summaries");
        var vector = new PropertyRow { Type = "vector3", Value = "1, 2, 3", Request = new SetPropertyRequest { valueType = "vector3" } };
        vector.AxisY = "9.5";
        Check(vector.ToRequest().x == 1 && vector.ToRequest().y == 9.5f && vector.ToRequest().z == 3, "Inspector axis editor updates one component without changing the other axes");
        var z = new NodeInfo { instanceId = 100, name = "Z", activeSelf = true, activeInHierarchy = true, childIds = new() { 99, 2 } };
        var a = new NodeInfo { instanceId = 1, name = "A", activeSelf = true, activeInHierarchy = true, childIds = new() };
        var c1 = new NodeInfo { instanceId = 99, name = "Z-child", activeSelf = true, activeInHierarchy = true, childIds = new() };
        var c2 = new NodeInfo { instanceId = 2, name = "A-child", activeSelf = true, activeInHierarchy = true, childIds = new() };
        var remote = new NodeTreeResponse { rootNodes = new() { z, a }, allNodes = new() { a, c2, z, c1 } };
        var tree = new TreeProjection(remote, "", 0, new(), 0); tree.Roots[0].Load();
        Check(tree.Roots.Select(r => r.Node.instanceId).SequenceEqual(new[] { 100, 1 }) && tree.Roots[0].Children.Select(r => r.Node.instanceId).SequenceEqual(new[] { 99, 2 }), "rootNodes and childIds order override name, ID and allNodes order");
        var filtered = new TreeProjection(remote, "child", 0, new(), 0); filtered.Roots[0].Load();
        Check(filtered.Roots[0].Children.Select(r => r.Node.instanceId).SequenceEqual(new[] { 99, 2 }), "search results retain remote sibling order");
    }
    private static Task UiChecks(string artifacts, bool shutdownOnly = false)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception failure = null;
            var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try
                {
                    if (shutdownOnly) { await VerifyShutdownScenarios(app, artifacts); return; }
                    var window = new MainWindow(Path.Combine(artifacts, "ui-settings.json"));
                    Check(((TextBox)window.FindName("PortBox")).Text == "9555", "listening port defaults to 9555 instead of restoring an old random port");
                    window.ShowActivated = false; window.ShowInTaskbar = false;
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = -20000; window.Top = -20000;
                    window.Show();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                    ((TextBox)window.FindName("PortBox")).Text = port.ToString();
                    ((Button)window.FindName("ListenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await using var mock = await MockRuntime.Connect(port, "模拟 Android · QA", states: true);
                    var combo = (ComboBox)window.FindName("SessionsBox");
                    await Until(() => combo.Items.Count == 1);
                    var session = ((SessionView)combo.Items[0]).Session;
                    var tree = (TreeView)window.FindName("NodeTree");
                    await Until(() => tree.Items.Count > 0);
                    Check(mock.TreeRequests == 1, "client handshake automatically refreshes the tree once without manual input");
                    ((TreeRow)tree.Items[0]).IsExpanded = true;
                    ((TreeRow)tree.Items[0]).Children[0].IsExpanded = true;
                    await Render(window, Path.Combine(artifacts, "desktop-overview.png"));
                    var rootRow = (TreeRow)tree.Items[0];
                    Check(rootRow.Children[0].State == "自身未激活" && rootRow.Children[0].Children[0].State == "父级未激活", "WPF tree displays inherited inactive state through actual controls");
                    ((TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0)).IsSelected = true;
                    await Until(() => window.PropertyRows.Count > 0);
                    // Search must preserve the remote order and restore the browsing context.
                    var expectedOrder = session.Tree.rootNodes.Select(n => n.instanceId).ToArray();
                    Check(tree.Items.OfType<TreeRow>().Select(r => r.Node.instanceId).SequenceEqual(expectedOrder), "hierarchy root order follows remote order without alphabetic sorting");
                    var treeScroll = Descendants<ScrollViewer>(tree).First();
                    treeScroll.ScrollToVerticalOffset(300);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    double beforeSearch = treeScroll.VerticalOffset;
                    var search = (TextBox)window.FindName("SearchBox"); search.Text = "不存在的节点";
                    search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                    await Until(() => tree.Items.Count == 0);
                    ((Button)window.FindName("ClearSearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Render(window, Path.Combine(artifacts, "desktop-search-restored.png"));
                    Check(Math.Abs(treeScroll.VerticalOffset - beforeSearch) < 2, "clearing a search with no selection restores the prior scroll position");
                    search.Text = "继承未激活子节点";
                    search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                    await Until(() => tree.Items.Count == 1);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var resultRoot = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0); resultRoot.IsExpanded = true;
                    resultRoot.UpdateLayout();
                    var resultParent = (TreeViewItem)resultRoot.ItemContainerGenerator.ContainerFromIndex(0); resultParent.IsExpanded = true;
                    resultParent.UpdateLayout();
                    ((TreeViewItem)resultParent.ItemContainerGenerator.ContainerFromIndex(0)).IsSelected = true;
                    await Until(() => session.Details.ContainsKey(44) && window.PropertyRows.First().Request.instanceId == 44);
                    ((Button)window.FindName("ClearSearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Render(window, Path.Combine(artifacts, "desktop-search-selection.png"));
                    Check(tree.SelectedItem is TreeRow { Node.instanceId: 44 } && tree.Items.OfType<TreeRow>().Select(r => r.Node.instanceId).SequenceEqual(expectedOrder), "clearing search keeps the chosen result located in the original remote hierarchy");
                    var selectedContainer = Descendants<TreeViewItem>(tree).First(c => c.DataContext is TreeRow { Node.instanceId: 44 });
                    double selectedY = selectedContainer.TransformToAncestor(tree).Transform(new Point()).Y;
                    Descendants<Button>((DependencyObject)window.Content).First(b => Equals(b.Content, "刷新节点树")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => ((TextBlock)window.FindName("StatusLabel")).Text == "节点树已刷新。");
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    selectedContainer = Descendants<TreeViewItem>(tree).First(c => c.DataContext is TreeRow { Node.instanceId: 44 });
                    Check(Math.Abs(selectedContainer.TransformToAncestor(tree).Transform(new Point()).Y - selectedY) < 2, "refresh preserves selected node screen offset");
                    treeScroll.ScrollToTop(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    ((TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0)).IsSelected = true;
                    await Until(() => window.PropertyRows.First().Request.instanceId == 42);
                    Check(window.PropertyRows.Any(r => r.Label.Contains("speed") && r.Type == "float" && r.CanEdit), "WPF maps Runtime number/Single into editable float; " + ((TextBlock)window.FindName("StatusLabel")).Text + "; " + string.Join(",", window.PropertyRows.Select(r => r.Label)));
                    var speed = window.PropertyRows.Single(r => r.Label.Contains("speed")); speed.Value = "8.75";
                    Check(speed.ToRequest().numberValue == 8.75, "property editor parses invariant numeric value");
                    var grid = (DataGrid)window.FindName("PropertiesGrid");
                    grid.ScrollIntoView(speed); await Render(window, Path.Combine(artifacts, "desktop-inspector.png"));
                    Check(grid.Columns[0].ActualWidth > 120 && grid.Columns[2].ActualWidth > 160, "grouped inspector retains usable field and value widths");
                    Check(window.PropertyRows.Select(r => r.ComponentGroup).Distinct().Count() == 4 && window.PropertyRows.Any(r => r.Section.StartsWith("材质[")), "inspector groups components and material slots separately");
                    var apply = Descendants<Button>(grid).First(x => x.DataContext == speed && Equals(x.Content, "应用"));
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => session.Details.TryGetValue(42, out var d) && d.components[0].serializedFields[0].doubleValue == 8.75);
                    Check(mock.Speed == 8.75, "WPF Apply button executes actual socket property write");
                    var componentGroup = Descendants<Expander>(grid).First(x => x.DataContext is System.Windows.Data.CollectionViewGroup { IsBottomLevel: false, Name: "Game.Test [0]" });
                    componentGroup.IsExpanded = false;
                    var previousSpeed = window.PropertyRows.Single(r => r.FieldLabel == "speed");
                    Descendants<Button>((DependencyObject)window.Content).First(x => Equals(x.Content, "刷新详情")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => !ReferenceEquals(previousSpeed, window.PropertyRows.Single(r => r.FieldLabel == "speed")));
                    await Render(window, Path.Combine(artifacts, "desktop-property-groups.png"));
                    componentGroup = Descendants<Expander>(grid).First(x => x.DataContext is System.Windows.Data.CollectionViewGroup { IsBottomLevel: false, Name: "Game.Test [0]" });
                    Check(!componentGroup.IsExpanded, "refresh preserves collapsed component group");
                    componentGroup.IsExpanded = true;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var numeric = window.PropertyRows.Single(r => r.FieldLabel == "speed"); grid.ScrollIntoView(numeric);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var presetButton = Descendants<Button>(grid).First(x => x.DataContext == numeric && Equals(x.Content, "▾") && x.IsVisible);
                    presetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var preset = presetButton.ContextMenu.Items.OfType<MenuItem>().First(x => Equals(x.Header, "1"));
                    preset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); presetButton.ContextMenu.IsOpen = false;
                    Check(numeric.Value == "1" && numeric.ToRequest().numberValue == 1 && mock.Speed == 8.75, "numeric popup selects a preset without sending until Apply");
                    var enumRow = window.PropertyRows.Single(r => r.FieldLabel == "clearFlags"); grid.ScrollIntoView(enumRow);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var enumPicker = Descendants<ComboBox>(grid).First(x => x.DataContext == enumRow && x.IsVisible);
                    enumPicker.SelectedValue = "4";
                    Check(enumRow.Value == "4" && enumRow.ToRequest().intValue == 4, "named enum dropdown binds verified integer value");
                    Descendants<Button>(grid).First(x => x.DataContext == enumRow && Equals(x.Content, "应用")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => mock.ClearFlags == 4 && session.Details[42].components[0].serializedFields.Any(f => f.fieldName == "clearFlags" && f.enumValue == 4));
                    Check(mock.ClearFlags == 4, "enum dropdown choice writes through TCP and reads back");
                    var propertySearch = (TextBox)window.FindName("PropertySearch"); propertySearch.Text = "材质[0]";
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(grid.Items.Count == 5, "property search filters material rows while retaining groups");
                    var materialGroup = Descendants<Expander>(grid).First(x => x.DataContext is System.Windows.Data.CollectionViewGroup { IsBottomLevel: true } g && g.Name.ToString().StartsWith("材质[0]"));
                    Check(!materialGroup.IsExpanded, "material details start collapsed for a new node");
                    materialGroup.IsExpanded = true;
                    await Render(window, Path.Combine(artifacts, "desktop-material-properties.png"));
                    var colorRow = window.PropertyRows.Single(r => r.FieldLabel == "_Color"); grid.ScrollIntoView(colorRow);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var colorPreset = Descendants<Button>(grid).First(x => x.DataContext == colorRow && Equals(x.Content, "▾") && x.IsVisible);
                    colorPreset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    colorPreset.ContextMenu.Items.OfType<MenuItem>().Single(x => Equals(x.Header, "白色")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    colorPreset.ContextMenu.IsOpen = false;
                    Check(colorRow.ToRequest().x == 1 && colorRow.ToRequest().w == 1 && colorRow.ToRequest().valueType == "materialColor", "material color popup fills RGBA without altering property identity");
                    propertySearch.Clear();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Descendants<Button>((DependencyObject)window.Content).First(x => Equals(x.Content, "全部折叠")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(Descendants<Expander>(grid).Where(x => x.DataContext is System.Windows.Data.CollectionViewGroup { IsBottomLevel: false }).All(x => !x.IsExpanded), "collapse-all folds component groups");
                    Descendants<Button>((DependencyObject)window.Content).First(x => Equals(x.Content, "全部展开")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(Descendants<Expander>(grid).All(x => x.IsExpanded), "expand-all restores nested property groups");
                    propertySearch.Text = "cullingMask";
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var maskRow = window.PropertyRows.Single(r => r.FieldLabel == "cullingMask"); grid.ScrollIntoView(maskRow);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(maskRow.LayerMaskText.Contains("Default") && maskRow.LayerMaskText.Contains("UI"), "Camera Culling Mask displays project layer names");
                    var layerButton = Descendants<Button>(grid).First(x => x.DataContext == maskRow && Equals(x.Content, maskRow.LayerMaskText));
                    layerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var playerLayer = layerButton.ContextMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Player"));
                    playerLayer.IsChecked = true; playerLayer.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Check(maskRow.ToRequest().intValue == (1 | 32 | 8192) && layerButton.ContextMenu.IsOpen, "LayerMask multiselect keeps Default + UI + Player with original bit positions");
                    layerButton.ContextMenu.IsOpen = false;
                    Descendants<Button>(grid).First(x => x.DataContext == maskRow && Equals(x.Content, "应用")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => mock.CullingMask == (1 | 32 | 8192) && session.Details[42].components.Any(c => c.typeName == "UnityEngine.Camera" && c.serializedFields[0].intValue == (1 | 32 | 8192)));
                    await Render(window, Path.Combine(artifacts, "desktop-camera-layers.png"));
                    Check(mock.CullingMask == 8225, "Camera LayerMask multiselect writes and reads back the exact mask over TCP");
                    propertySearch.Clear(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var inactiveFilter = (ComboBox)window.FindName("TreeStateFilter"); inactiveFilter.SelectedIndex = 2;
                    await Render(window, Path.Combine(artifacts, "desktop-inactive-filter.png"));
                    Check(((TreeRow)tree.Items[0]).IsContext && ((TreeRow)tree.Items[0]).Children.Count == 1, "WPF inactive filter keeps hierarchy context");
                    inactiveFilter.SelectedIndex = 0;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var activeRow = window.PropertyRows.Single(r => r.Label == "GameObject / activeSelf");
                    grid.ScrollIntoView(activeRow); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Descendants<CheckBox>(grid).First(x => x.DataContext == activeRow && x.IsVisible).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
                    await Dispatcher.Yield(DispatcherPriority.DataBind);
                    Check(activeRow.Value == "False", "Inspector boolean checkbox replaces manual True/False input: " + activeRow.Value);
                    ((FrameworkElement)window.Content).UpdateLayout();
                    Descendants<Button>(grid).First(x => x.DataContext == activeRow && Equals(x.Content, "应用")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => session.Tree.allNodes[0].activeSelf == false && session.Details[42].activeSelf == false && ((TreeRow)tree.Items[0]).State == "自身未激活");
                    await Render(window, Path.Combine(artifacts, "desktop-node-disabled.png"));
                    Check(((TreeRow)tree.Items[0]).State == "自身未激活" && ((TreeRow)tree.Items[0]).IsSelected, "WPF activeSelf write refreshes tree while retaining inactive selection");
                    var tabs = (TabControl)window.FindName("Tabs"); var diagnosticsTabs = (TabControl)window.FindName("DiagnosticsTabs"); diagnosticsTabs.SelectedIndex = 0;
                    await Task.Delay(250); await Render(window, Path.Combine(artifacts, "desktop-logs.png"));
                    Check(window.Logs.Count > 0, "WPF receives Runtime log messages");
                    diagnosticsTabs.SelectedIndex = 1; window.UpdateLayout(); await Render(window, Path.Combine(artifacts, "desktop-fps.png"));
                    Check(window.FrameRows.Count > 0, "WPF FPS samples and graph are populated");
                    diagnosticsTabs.SelectedIndex = 2; window.UpdateLayout();
                    ((Button)window.FindName("RpcChooseFunction")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => ((DataGrid)window.FindName("RpcGrid")).Items.Count > 0);
                    Check(((DataGrid)window.FindName("RpcGrid")).Items.Count == 1, "WPF RPC list command");
                    ((TextBox)window.FindName("RpcFunctionSearch")).Text = "missing-function";
                    Check(((DataGrid)window.FindName("RpcGrid")).Items.Count == 0, "RPC function picker filters by keyword");
                    ((TextBox)window.FindName("RpcFunctionSearch")).Clear();
                    ((DataGrid)window.FindName("RpcGrid")).SelectedIndex = 0;
                    Check(((TextBox)window.FindName("RpcMethod")).Text == "Echo" && !((System.Windows.Controls.Primitives.Popup)window.FindName("RpcFunctionPicker")).IsOpen, "choosing an RPC fills method and closes the function picker");
                    var rpcArgsBox = (TextBox)window.FindName("RpcArgs"); var rpcExecuteButton = (Button)window.FindName("RpcExecute");
                    rpcArgsBox.Text = "{bad"; Check(!rpcExecuteButton.IsEnabled, "invalid JSON disables RPC execute");
                    rpcArgsBox.Text = "{}"; Check(!rpcExecuteButton.IsEnabled, "RPC parameters must be a JSON array");
                    rpcArgsBox.Text = "[42]"; rpcExecuteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => ((TextBlock)window.FindName("RpcCallStatus")).Text.Contains("成功"));
                    Check(((TextBox)window.FindName("RpcResult")).Text.Contains("42") && ((Expander)window.FindName("RpcResultPanel")).IsExpanded, "RPC execution formats and expands the returned result");
                    string callResultBeforeTool = ((TextBox)window.FindName("RpcResult")).Text;
                    ((Button)window.FindName("RpcIFixButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => ((TextBlock)window.FindName("RpcToolStatus")).Text.Contains("成功"));
                    Check(rpcArgsBox.Text == "[42]" && ((TextBox)window.FindName("RpcMethod")).Text == "Echo" && ((TextBox)window.FindName("RpcResult")).Text == callResultBeforeTool, "one-click hot-reload preserves RPC method, parameters and result");
                    ((Expander)window.FindName("RpcResultPanel")).IsExpanded = false;
                    await Render(window, Path.Combine(artifacts, "desktop-rpc.png"));
                    window.UpdateLayout();
                    var filesBefore = ((DataGrid)window.FindName("FilesGrid")).ItemsSource;
                    Descendants<Button>((DependencyObject)window.Content).First(x => Equals(x.Content, "刷新目录")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => !ReferenceEquals(filesBefore, ((DataGrid)window.FindName("FilesGrid")).ItemsSource));
                    Check(((DataGrid)window.FindName("FilesGrid")).Items.Count == 2, "WPF remote directory listing");
                    await Render(window, Path.Combine(artifacts, "desktop-files.png"));
                    var fileGrid = (DataGrid)window.FindName("FilesGrid");
                    fileGrid.SelectedIndex = 1;
                    var folderRow = (DataGridRow)fileGrid.ItemContainerGenerator.ContainerFromIndex(0);
                    folderRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent });
                    Check(fileGrid.SelectedItem is FileRow { IsDirectory: true }, "file context menu targets the row under the pointer instead of an older selection");
                    fileGrid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = folderRow });
                    await Until(() => mock.LastBrowsePath == "patches" && window.Breadcrumbs.Count == 2);
                    Check(window.Breadcrumbs.Last().Path == "patches", "double-clicking a directory navigates to that exact directory");
                    ((TextBox)window.FindName("RemotePath")).Text = "patches/hotfix";
                    ((TextBox)window.FindName("RemotePath")).RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                    await Until(() => window.Breadcrumbs.Count == 3);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(window.Breadcrumbs[1].Path == "patches" && window.Breadcrumbs[2].Path == "patches/hotfix", "file path generates clickable ancestor breadcrumbs");
                    var crumbs = (ItemsControl)window.FindName("PathBreadcrumbs");
                    Descendants<Button>(crumbs).Single(b => Equals(b.Content, "patches")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => mock.LastBrowsePath == "patches" && window.Breadcrumbs.Count == 2);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Descendants<Button>(crumbs).Single(b => Equals(b.Content, "PersistentRoot")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => mock.LastBrowsePath == "" && window.Breadcrumbs.Count == 1);
                    Check(((TextBlock)window.FindName("PersistentPath")).Text == "/mock/persistent", "PersistentRoot returns to remote root while the absolute root remains a read-only label");
                    diagnosticsTabs.SelectedIndex = 0;
                    await Render(window, Path.Combine(artifacts, "desktop-workspace.png"));
                    Check(((DataGrid)window.FindName("LogsGrid")).IsVisible && ((DataGrid)window.FindName("FilesGrid")).IsVisible && grid.IsVisible, "Inspector, logs and file browser are simultaneously visible in separate panes");
                    var logGrid = (DataGrid)window.FindName("LogsGrid");
                    var logRect = logGrid.TransformToAncestor(window).TransformBounds(new Rect(logGrid.RenderSize));
                    var fileRect = fileGrid.TransformToAncestor(window).TransformBounds(new Rect(fileGrid.RenderSize));
                    Check(!logRect.IntersectsWith(fileRect), "log and file panes occupy disjoint rectangles");
                    logGrid.SelectedIndex = 0;
                    Check(((TextBox)window.FindName("LogDetails")).Text.Contains("模拟客户端"), "selecting a log reveals its full message and stack area");
                    tabs.SelectedIndex = 0;
                    void ClickNamed(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    void ClickText(string text) => Descendants<Button>((DependencyObject)window.Content).First(b => Equals(b.Content, text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ClickNamed("FavoriteNode");
                    Check(Equals(((Button)window.FindName("FavoriteNode")).Content, "★ 已收藏"), "favorite toggles for current session and node");
                    ClickNamed("LocateInspectorNode");
                    await Until(() => tree.SelectedItem is TreeRow { Node.instanceId: 42 });
                    var pathSearch = (TextBox)window.FindName("SearchBox");
                    pathSearch.Text = session.Tree.allNodes.First(n => n.instanceId == 42).path;
                    pathSearch.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pathSearch), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                    await Until(() => pathSearch.Text.Length == 0 && tree.SelectedItem is TreeRow { Node.instanceId: 42 } && typeof(MainWindow).GetField("operation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(window) == null);
                    Check(window.PropertyRows.First().Request.instanceId == 42, "full path search directly locates Inspector node");
                    ClickNamed("BackNode");
                    await Until(() => window.PropertyRows.First().Request.instanceId == 44 && tabs.SelectedIndex == 0);
                    ClickNamed("ForwardNode");
                    await Until(() => window.PropertyRows.First().Request.instanceId == 42);
                    Check(((TextBlock)window.FindName("NodeIdentity")).Text.Contains("42"), "node history navigates back and forward with matching Inspector identity");
                    ((Expander)window.FindName("PropertyTools")).IsExpanded = true;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    ClickText("记录基准");
                    var compared = window.PropertyRows.Single(r => r.FieldLabel == "speed");
                    string original = compared.Value; compared.Value = "123.5";
                    Check(compared.IsDirty && compared.IsChanged, "edited property marks pending write and baseline difference");
                    ClickText("还原未应用编辑");
                    Check(compared.Value == original && !compared.IsDirty && !compared.IsChanged, "revert restores the last remote value without sending a write");
                    compared.Value = "456";
                    ClickText("刷新详情");
                    await Until(() => !ReferenceEquals(compared, window.PropertyRows.Single(r => r.FieldLabel == "speed")));
                    Check(!window.PropertyRows.Single(r => r.FieldLabel == "speed").IsDirty, "refresh replaces pending edits with confirmed remote data");
                    var follow = (CheckBox)window.FindName("FollowLogs"); follow.IsChecked = false;
                    Check(follow.IsChecked == false, "log auto-follow can pause without stopping log reception");
                    ClickText("回到最新"); Check(follow.IsChecked == true, "latest log action resumes follow");
                    var watched = window.PropertyRows.Single(r => r.FieldLabel == "speed"); grid.SelectedItem = watched;
                    ClickText("固定所选属性");
                    Check(window.MonitorRows.Count == 1 && window.MonitorRows[0].Node == 42, "pinning a property preserves node identity in the monitor");
                    tabs.SelectedIndex = 4;
                    ((TabControl)window.FindName("WatchTabs")).SelectedIndex = 1;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    ClickText("刷新监视值");
                    await Until(() => !((TextBlock)window.FindName("OperationLabel")).Text.Contains("远端请求"));
                    Check(window.PropertyRows.First().Request.instanceId == 42 && window.MonitorRows[0].Value == watched.Value, "monitor refresh keeps the Inspector target and reads confirmed values");
                    var monitorGrid = (DataGrid)window.FindName("MonitorGrid"); monitorGrid.SelectedIndex = 0;
                    ClickText("移除所选"); Check(window.MonitorRows.Count == 0, "pinned property can be removed");
                    var layout = (ComboBox)window.FindName("LayoutPreset"); layout.SelectedIndex = 2;
                    Check(((ColumnDefinition)window.FindName("DiagnosticsWidthColumn")).Width.Value == .65, "log layout allocates more width to diagnostics");
                    layout.SelectedIndex = 0;
                    diagnosticsTabs.SelectedIndex = 2;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    ((TextBox)window.FindName("RpcMethod")).Text = "Echo"; ((TextBox)window.FindName("RpcArgs")).Text = "[42]";
                    ClickText("保存模板"); ((TextBox)window.FindName("RpcArgs")).Text = "[]";
                    var rpcTemplatesPicker = (ComboBox)window.FindName("RpcTemplatePicker");
                    rpcTemplatesPicker.SelectedIndex = 0;
                    Check(((TextBox)window.FindName("RpcArgs")).Text == "[42]", "RPC template restores exact parameters without executing");
                    diagnosticsTabs.SelectedIndex = 0; tabs.SelectedIndex = 0;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var interval = (ComboBox)window.FindName("RefreshInterval"); interval.SelectedIndex = 1;
                    var beforeAuto = window.PropertyRows.First();
                    ((TextBox)window.FindName("SearchBox")).Focus();
                    await Until(() => !ReferenceEquals(beforeAuto, window.PropertyRows.First()));
                    Check(true, "automatic Inspector refresh reads new remote details");
                    var dirtyAuto = window.PropertyRows.Single(r => r.FieldLabel == "speed"); dirtyAuto.Value = "77";
                    await Task.Delay(1300);
                    Check(ReferenceEquals(dirtyAuto, window.PropertyRows.Single(r => r.FieldLabel == "speed")) && dirtyAuto.Value == "77", "automatic refresh pauses while an unapplied edit exists");
                    interval.SelectedIndex = 0; ClickText("还原未应用编辑");
                    string incident = Path.Combine(artifacts, "test-incident.zip");
                    await window.SaveIncidentAsync(incident);
                    using (var zip = System.IO.Compression.ZipFile.OpenRead(incident))
                    {
                        Check(new[] { "snapshot.bin", "logs.txt", "frames.json", "incident.json" }.All(n => zip.GetEntry(n) != null), "incident bundle contains snapshot, logs, frames and capture metadata");
                        using var input = zip.GetEntry("snapshot.bin").Open();
                        using var output = File.Create(Path.Combine(artifacts, "incident-roundtrip.bin")); input.CopyTo(output);
                    }
                    Check(SnapshotFile.Load(Path.Combine(artifacts, "incident-roundtrip.bin")).NodeDetails.ContainsKey(42), "incident snapshot can reopen with the selected node details");
                    ((Expander)window.FindName("PropertyTools")).IsExpanded = false;
                    await Render(window, Path.Combine(artifacts, "desktop-debug-tools.png"));
                    var readableTexture = window.PropertyRows.Single(r => r.TextureId == 55);
                    var unreadableTexture = window.PropertyRows.Single(r => r.TextureId == 56);
                    Check(readableTexture.CanPreview && !unreadableTexture.CanPreview && !readableTexture.UseText, "texture preview availability follows remote readable metadata");
                    grid.ScrollIntoView(unreadableTexture); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var disabledTextureButton = Descendants<Button>(grid).First(b => b.DataContext == unreadableTexture && Equals(b.Content, unreadableTexture.TextureState));
                    Check(!disabledTextureButton.IsEnabled, "unreadable texture retains a disabled gray preview button");
                    grid.ScrollIntoView(readableTexture); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var textureButton = Descendants<Button>(grid).First(b => b.DataContext == readableTexture && Equals(b.Content, readableTexture.TextureState));
                    textureButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => window.OwnedWindows.Cast<Window>().Any(w => w.Title.StartsWith("纹理预览")));
                    var textureWindow = window.OwnedWindows.Cast<Window>().Single(w => w.Title.StartsWith("纹理预览"));
                    Check(Descendants<System.Windows.Controls.Image>(textureWindow).Any(i => i.Source != null), "row preview opens a separate texture window with decoded image");
                    await Render(textureWindow, Path.Combine(artifacts, "desktop-texture-popup.png")); textureWindow.Close();
                    Check((await session.GetTextureAsync(55, original: true)).SequenceEqual(MockRuntime.Image), "original texture export uses its own protocol command and correlated response");
                    tabs.SelectedIndex = 2;
                    ((FrameworkElement)window.Content).UpdateLayout();
                    var capture = Descendants<Button>((DependencyObject)window.Content).First(x => Equals(x.Content, "抓取游戏画面"));
                    capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => ((System.Windows.Controls.Image)window.FindName("PreviewImage")).Source != null);
                    await Render(window, Path.Combine(artifacts, "desktop-image.png"));
                    Check(((System.Windows.Controls.Image)window.FindName("PreviewImage")).Source.Width > 0, "WPF screenshot command decodes received image");
                    var screenshotImage = (System.Windows.Controls.Image)window.FindName("PreviewImage");
                    var originalScreenshot = screenshotImage.Source;
                    screenshotImage.Source = BitmapSource.Create(2400, 1200, 96, 96, PixelFormats.Bgra32, null, new byte[2400 * 1200 * 4], 2400 * 4);
                    var zoom = (Slider)window.FindName("ImageZoom"); zoom.Value = 50;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var imageViewport = (ScrollViewer)window.FindName("ImageViewport");
                    Check(Math.Abs(screenshotImage.ActualWidth - 1200) < 1 && Math.Abs(screenshotImage.ActualHeight - 600) < 1, "image zoom slider sets explicit aspect-preserving display size");
                    imageViewport.ScrollToHorizontalOffset(100); imageViewport.ScrollToVerticalOffset(120);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var displayedPoint = screenshotImage.TranslatePoint(new Point(300, 450), imageViewport);
                    var localPoint = imageViewport.TranslatePoint(displayedPoint, screenshotImage);
                    Check(ImageCoordinates.TryNormalize(screenshotImage.ActualWidth, screenshotImage.ActualHeight, 2400, 1200, localPoint.X, localPoint.Y, out float pickedU, out float pickedV) && Math.Abs(pickedU - .25f) < .001 && Math.Abs(pickedV - .25f) < .001, "zoomed and scrolled screenshot retains correct Unity pick coordinates");
                    Descendants<Button>((DependencyObject)window.Content).First(b => Equals(b.Content, "适应窗口")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(screenshotImage.ActualWidth <= imageViewport.ViewportWidth && screenshotImage.ActualHeight <= imageViewport.ViewportHeight, "fit image action contains a large screenshot in the viewport");
                    await Render(window, Path.Combine(artifacts, "desktop-image-zoom.png"));
                    Descendants<Button>((DependencyObject)window.Content).First(b => Equals(b.Content, "原始大小")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(zoom.Value == 100 && Math.Abs(screenshotImage.ActualWidth - 2400) < 1, "original size restores one image pixel per display unit");
                    screenshotImage.Source = originalScreenshot;
                    Descendants<Button>((DependencyObject)window.Content).First(b => Equals(b.Content, "适应窗口")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var savedFiles = fileGrid.ItemsSource;
                    fileGrid.ItemsSource = new[] { new FileRow("fixture.logcompr", "fixture.logcompr", false, 26838) };
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var compressedRow = (DataGridRow)fileGrid.ItemContainerGenerator.ContainerFromIndex(0);
                    fileGrid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = compressedRow });
                    var remoteTabs = (TabControl)window.FindName("RemoteFilesTabs");
                    await Until(() => ((ContentControl)window.FindName("CompressedLogContent")).Content is CompressedLogView);
                    var logView = (CompressedLogView)((ContentControl)window.FindName("CompressedLogContent")).Content;
                    await Until(() => !logView.IsBusy && logView.Text.Text.Contains("===Awake: Step1"));
                    Check(remoteTabs.SelectedIndex == 1 && !window.OwnedWindows.Cast<Window>().Any(w => w.Title.StartsWith("压缩日志")), "opening compressed log selects the embedded log tab without a separate window");
                    logView.Keyword.Text = "Awake";
                    Check(logView.Text.SelectedText.Equals("Awake", StringComparison.OrdinalIgnoreCase), "compressed log keyword search selects the first match");
                    int firstMatch = logView.Text.SelectionStart; logView.FindNext();
                    Check(logView.Text.SelectionStart > firstMatch, "compressed log next match navigates forward");
                    logView.FindNext(true); Check(logView.Text.SelectionStart == firstMatch, "compressed log previous match returns to the prior match");
                    logView.Keyword.Text = "does-not-exist-9876"; Check(logView.Text.SelectionLength == 0, "missing keyword does not create an invalid selection");
                    logView.Keyword.Clear();
                    await logView.ReloadAsync();
                    string exportedLog = Path.Combine(artifacts, "tab-export.log"); logView.ExportTo(exportedLog);
                    Check(File.ReadAllText(exportedLog).Contains("===Awake: Step1"), "embedded log refresh and full-text export succeed");
                    await Render(window, Path.Combine(artifacts, "desktop-compressed-log.png"));
                    remoteTabs.SelectedIndex = 0; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(fileGrid.Items.Count == 1 && fileGrid.IsVisible, "returning to file tab retains the previous remote directory listing");
                    fileGrid.ItemsSource = savedFiles;
                    await window.PickScreenshotAsync(.5f, .5f, 1);
                    Check(((ListBox)window.FindName("PickResults")).Items.Count == 1, "screenshot pick response populates dedicated hit nodes without reordering hierarchy");
                    await Render(window, Path.Combine(artifacts, "desktop-pick-results.png"));
                    Check(Descendants<TextBlock>((ListBox)window.FindName("PickResults")).Any(t => t.Text.Contains("继承未激活子节点")), "hit list displays the returned object path");
                    ((ListBox)window.FindName("PickResults")).SelectedIndex = 0;
                    await Until(() => window.PropertyRows.First().Request.instanceId == 44 && tabs.SelectedIndex == 0);
                    Check(tree.Items.OfType<TreeRow>().Select(r => r.Node.instanceId).SequenceEqual(expectedOrder), "clicking a hit navigates to the original tree while retaining remote order");
                    tabs.SelectedIndex = 4; ((TabControl)window.FindName("WatchTabs")).SelectedIndex = 0;
                    ((CheckBox)window.FindName("AutoWatchNodes")).IsChecked = false;
                    treeScroll.ScrollToTop(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var watchTreeItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
                    tree.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = watchTreeItem });
                    await Until(() => window.PropertyRows.First().Request.instanceId == 42);
                    var watchMenuItems = tree.ContextMenu.Items.OfType<MenuItem>().ToArray();
                    watchMenuItems[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Check(window.NodeWatchRows.Single().Node == 42, "tree right-click adds the actual node under the pointer to monitoring");
                    watchMenuItems[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Check(window.NodeWatchRows.Count == 0, "tree right-click removes node monitoring");
                    watchMenuItems[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await window.PollNodeWatches(true);
                    Check(window.NodeWatchRows.Count == 1 && window.NodeWatchChanges.Count == 0, "node watch establishes a baseline without reporting false changes");
                    mock.ChangeWatchedNode();
                    ((ComboBox)window.FindName("WatchInterval")).SelectedIndex = 0;
                    ((CheckBox)window.FindName("AutoWatchNodes")).IsChecked = true;
                    await Until(() => window.NodeWatchChanges.Any(c => c.Kind == "组件新增"));
                    ((CheckBox)window.FindName("AutoWatchNodes")).IsChecked = false;
                    Check(window.NodeWatchChanges.Any(c => c.Kind == "节点变化" && c.Field == "名称") && window.NodeWatchChanges.Any(c => c.Kind == "组件新增"), "dynamic watch detects node renaming and added components");
                    Check(window.NodeWatchChanges.Any(c => c.Kind == "组件启用变化") && window.NodeWatchChanges.Any(c => c.Kind == "参数变化" && c.Field.Contains("speed") && c.After == "37.5"), "dynamic watch reports enabled state and parameter before/after values");
                    var watchedGrid = (DataGrid)window.FindName("NodeWatchGrid"); watchedGrid.SelectedIndex = 0;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Descendants<Button>((DependencyObject)window.Content).First(b => Equals(b.Content, "定位监听节点")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => window.PropertyRows.First().Request.instanceId == 42 && tabs.SelectedIndex == 0);
                    var disabledParameters = window.PropertyRows.Single(r => r.FieldLabel == "speed");
                    Check(disabledParameters.CanEdit && disabledParameters.ComponentInactive && disabledParameters.ParameterOpacity < 1 && disabledParameters.ComponentStateLabel == "组件已禁用", "disabled component state dims parameters while keeping their values editable");
                    Check(!window.PropertyRows.Any(r => r.Section == "图片与纹理") && new ComponentTitleConverter().Convert("Game.Test [0]", typeof(string), null, System.Globalization.CultureInfo.InvariantCulture).Equals("Test"), "Inspector removes invalid image summaries and array-style component suffixes");
                    await Render(window, Path.Combine(artifacts, "desktop-component-state.png"));
                    mock.WatchRemovedComponent = true; await window.PollNodeWatches(true);
                    Check(window.NodeWatchChanges.Any(c => c.Kind == "组件移除"), "dynamic watch detects component removal");
                    tabs.SelectedIndex = 4;
                    await Render(window, Path.Combine(artifacts, "desktop-node-watches.png"));
                    mock.WatchDestroyed = true; await window.PollNodeWatches(true);
                    Check(window.NodeWatchRows.Count == 0 && window.NodeWatchChanges.Any(c => c.Destroyed && c.Kind == "节点销毁"), "confirmed destruction automatically removes watch while preserving its change record");
                    window.AddNodeWatch(5000);
                    ((Button)window.FindName("ListenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => !session.IsConnected);
                    await window.PollNodeWatches(true);
                    Check(window.NodeWatchRows.Count == 1, "disconnect pauses monitoring without treating a live node as destroyed");
                    await Until(() => Equals(((Button)window.FindName("ListenButton")).Content, "启动监听"));
                    await VerifyWindowClose(window, app, "window closes after listener has already stopped");
                    await VerifyShutdownScenarios(app, artifacts);
                }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            try { Dispatcher.Run(); }
            catch (Exception ex) { failure ??= ex; }
            if (failure != null) done.SetException(failure); else done.SetResult();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }
    private static async Task Render(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout(); content.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var image = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
        Check(new FileInfo(path).Length > (window is MainWindow ? 10000 : 1000), "WPF offscreen render " + Path.GetFileName(path));
    }
    private static async Task VerifyWindowClose(MainWindow window, App app, string scenario)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception closeError = null;
        window.Closed += (_, _) => closed.TrySetResult();
        DispatcherUnhandledExceptionEventHandler handler = (_, e) => { closeError = e.Exception; e.Handled = true; };
        app.DispatcherUnhandledException += handler;
        try
        {
            window.Close();
            window.Close(); // Repeated close requests must not bypass or repeat cleanup.
            await Task.WhenAny(closed.Task, Task.Delay(2000));
            Check(closed.Task.IsCompleted && closeError == null, scenario + (closeError == null ? "" : ": " + closeError.Message));
        }
        finally { app.DispatcherUnhandledException -= handler; }
    }
    private static async Task VerifyShutdownScenarios(App app, string artifacts)
    {
        MainWindow Create(string name)
        {
            var window = new MainWindow(Path.Combine(artifacts, "shutdown-" + name + ".json"))
            { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            window.Show();
            ((CheckBox)window.FindName("AutoStats")).IsChecked = false;
            return window;
        }
        int Start(MainWindow window)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            ((TextBox)window.FindName("PortBox")).Text = port.ToString();
            ((Button)window.FindName("ListenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return port;
        }
        var idle = Create("idle");
        await VerifyWindowClose(idle, app, "repeated close succeeds without ever starting the listener");
        var stopped = Create("stopped");
        Start(stopped);
        ((Button)stopped.FindName("ListenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Equals(((Button)stopped.FindName("ListenButton")).Content, "启动监听"));
        await VerifyWindowClose(stopped, app, "stop listener then close succeeds with no clients");
        var listening = Create("listening");
        int listeningPort = Start(listening);
        await VerifyWindowClose(listening, app, "close while AcceptTcpClientAsync is pending");
        var rebound = new TcpListener(IPAddress.Any, listeningPort); rebound.Start(); rebound.Stop();
        Check(true, "closing releases the listening port");
        var connected = Create("connected");
        int connectedPort = Start(connected);
        await using (var runtime = await MockRuntime.Connect(connectedPort, "Shutdown Device"))
        {
            var combo = (ComboBox)connected.FindName("SessionsBox");
            await Until(() => combo.Items.Count == 1);
            var session = ((SessionView)combo.Items[0]).Session;
            Task request = session.RequestAsync(Command.RPC_CALL, new RpcCallRequest { methodName = "NoReply" }, default, 30, Command.RPC_RESPONSE);
            await VerifyWindowClose(connected, app, "close with a connected client and outstanding request");
            await Task.WhenAny(request, Task.Delay(2000));
            Check(request.IsCompleted, "shutdown completes the outstanding request without waiting for its timeout");
            await Throws<Exception>(async () => await request, "shutdown faults the disconnected request");
            Check(!session.IsConnected, "window close disconnects the active client");
        }
        var racing = Create("stop-race");
        int racePort = Start(racing);
        using (var socket = new TcpClient())
        {
            await socket.ConnectAsync(IPAddress.Loopback, racePort);
            await Until(() => ((ComboBox)racing.FindName("SessionsBox")).Items.Count == 1);
            ((Button)racing.FindName("ListenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await VerifyWindowClose(racing, app, "immediate close during listener stop joins the existing cleanup");
        }
        // The settings file cannot be written when its path names a directory.
        var unwritable = new MainWindow(artifacts)
        { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        unwritable.Show();
        await VerifyWindowClose(unwritable, app, "settings write failure does not prevent closing");
    }
    private static Task<int> LifecycleChild(string artifacts, bool stopFirst)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // Use the production styles without its StartupUri, since this test supplies the window explicitly.
                var markup = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml"));
                System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var dictionary = new System.Xml.Linq.XElement(ns + "ResourceDictionary",
                    new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                    markup.Root.Element(ns + "Application.Resources").Elements());
                var app = new App { Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString()) };
                var window = new MainWindow(Path.Combine(artifacts, "process-settings.json"))
                { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
                window.Loaded += async (_, _) =>
                {
                    try
                    {
                        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                        ((TextBox)window.FindName("PortBox")).Text = port.ToString();
                        var listen = (Button)window.FindName("ListenButton");
                        listen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (stopFirst)
                        {
                            listen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            await Until(() => Equals(listen.Content, "启动监听"));
                        }
                        window.Close();
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
                };
                done.SetResult(app.Run(window));
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task;
    }
    private static async Task LifecycleProcessChecks(string artifacts)
    {
        foreach (string scenario in new[] { "stopped", "listening" })
        {
            var start = new ProcessStartInfo(Environment.ProcessPath)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add(artifacts); start.ArgumentList.Add("--lifecycle-child-" + scenario);
            using var process = Process.Start(start);
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                string messages = await output + await error;
                Check(process.ExitCode == 0, "actual WPF application process exits after closing: " + scenario + (messages.Length == 0 ? "" : " · " + messages));
            }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
    }
}

internal sealed class MockRuntime : IAsyncDisposable
{
    public static readonly byte[] Image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1sAAAAASUVORK5CYII=");
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly SemaphoreSlim sendLock = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly string name;
    private readonly bool binary;
    private readonly bool states;
    private bool active = true;
    private readonly Task loop;
    private MemoryStream upload;
    public byte[] Uploaded { get; private set; } = Array.Empty<byte>();
    public int TreeRequests;
    public bool WatchMutation, WatchRemovedComponent, WatchDestroyed;
    public void ChangeWatchedNode() { WatchMutation = true; active = true; Speed = 37.5; }
    public double Speed { get; private set; } = 2.5;
    public int ClearFlags { get; private set; } = 1;
    public int CullingMask { get; private set; } = 33;
    public string LastBrowsePath { get; private set; }
    private MockRuntime(TcpClient client, string name, bool binary, bool states) { this.client = client; stream = client.GetStream(); this.name = name; this.binary = binary; this.states = states; loop = Run(); }
    public static async Task<MockRuntime> Connect(int port, string name, bool binary = false, bool states = false)
    {
        var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
        var runtime = new MockRuntime(client, name, binary, states);
        await runtime.Send(Command.CLIENT_HELLO, 1, new ClientHelloRequest { deviceName = name, platform = "Android", sessionId = Guid.NewGuid().ToString("N"), unityVersion = "2022.3" });
        await runtime.Send(Command.LOG_MESSAGE, 0, new LogMessage { message = "模拟客户端已连接 — 日志转发正常", timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds() });
        return runtime;
    }
    private async Task Send(Command command, ushort seq, object body)
    {
        await sendLock.WaitAsync(lifetime.Token);
        try { await stream.WriteAsync(Wire.Encode(command, seq, body), lifetime.Token); }
        finally { sendLock.Release(); }
    }
    private async Task Run()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var p = await Wire.ReadAsync(stream, lifetime.Token);
                switch (p.GetCommand())
                {
                    case Command.HEARTBEAT: await Send(Command.HEARTBEAT, p.sequence, new HeartbeatMessage()); break;
                    case Command.GET_NODE_TREE:
                        Interlocked.Increment(ref TreeRequests);
                        var node = new NodeInfo { instanceId = 42, name = name, path = "Root/" + name, activeSelf = active, activeInHierarchy = active, childIds = states ? new() { 43 } : new(), componentTypes = new() { "Game.Test" } };
                        if (WatchMutation) { node.name = "已变更节点"; node.path = "Root/已变更节点"; }
                        if (binary)
                        {
                            byte[] body = BinaryNodeTreeCodec.Encode(new BinaryNodeTreeDto { totalNodes = 1, rootCount = 1, rootIndices = new[] { 0 }, stringPool = new() { "Game.Test" },
                                allNodes = new() { new BinaryNodeDto { instanceId = 42, path = node.path, activeSelf = true, activeInHierarchy = true, componentTypes = node.componentTypes, childIds = new() } } });
                            await sendLock.WaitAsync(lifetime.Token);
                            try { await stream.WriteAsync(Wire.Encode(new MessagePacket(Command.NODE_TREE_RESPONSE_BINARY, p.sequence, body)), lifetime.Token); }
                            finally { sendLock.Release(); }
                            break;
                        }
                        var all = new List<NodeInfo> { node };
                        var roots = new List<NodeInfo> { node };
                        if (states)
                        {
                            all.Add(new NodeInfo { instanceId = 43, name = "未激活父节点", path = node.path + "/未激活父节点", activeSelf = false, activeInHierarchy = false, childIds = new() { 44 } });
                            all.Add(new NodeInfo { instanceId = 44, name = "继承未激活子节点", path = node.path + "/未激活父节点/继承未激活子节点", activeSelf = true, activeInHierarchy = false, childIds = new() });
                            for (int i = 0; i < 40; i++)
                            {
                                var extra = new NodeInfo { instanceId = 5000 + i, name = $"Z-node-{39 - i:00}", path = $"Z-node-{39 - i:00}", activeSelf = true, activeInHierarchy = true, childIds = new() };
                                all.Add(extra); roots.Add(extra);
                            }
                        }
                        if (WatchDestroyed) { all.RemoveAll(n => n.instanceId == 42); roots.RemoveAll(n => n.instanceId == 42); }
                        await Send(Command.NODE_TREE_RESPONSE, p.sequence, new NodeTreeResponse { sceneName = "QA Scene", rootNodes = roots, allNodes = all }); break;
                    case Command.GET_NODE_DETAIL:
                        int detailId = Wire.Body<GetNodeDetailRequest>(p).instanceId;
                        var component = new ComponentDetailData { typeName = "Game.Test", displayName = "TestComponent", hasEnabled = true, enabled = true, serializedFields = new()
                        { new SerializedFieldData { fieldName = "speed", valueType = "number", fieldType = "Single", doubleValue = Speed }, new SerializedFieldData { fieldName = "title", valueType = "string", fieldType = "String", stringValue = "测试对象" } } };
                        component.enabled = !WatchMutation;
                        var components = new List<ComponentDetailData> { component };
                        if (WatchMutation && !WatchRemovedComponent) components.Add(new ComponentDetailData { typeName = "Game.NewComponent", displayName = "NewComponent" });
                        if (states)
                        {
                            component.serializedFields.Add(new SerializedFieldData { fieldName = "clearFlags", valueType = "enum", fieldType = "CameraClearFlags", enumValue = ClearFlags, stringValue = ClearFlags == 1 ? "Skybox" : "Nothing", enumNames = new[] { "Skybox", "Color", "SolidColor", "Depth", "Nothing" } });
                            component.serializedFields.Add(new SerializedFieldData { fieldName = "previewTexture", valueType = "texture", fieldType = "Texture2D", textureInstanceId = 55 });
                            component.serializedFields.Add(new SerializedFieldData { fieldName = "unreadableTexture", valueType = "texture", fieldType = "Texture2D", textureInstanceId = 56 });
                            component.serializedFields.Add(new SerializedFieldData { fieldName = "offset", valueType = "vector3", fieldType = "Vector3", x = 2, y = 3, z = 4 });
                            components.Add(new ComponentDetailData { typeName = "UnityEngine.MeshRenderer", displayName = "MeshRenderer", materials = new()
                            { new MaterialInfo { name = "角色材质", slotIndex = 0, visible = true, renderQueue = 2000, enabledKeywords = new() { "_NORMALMAP" }, shaderAllKeywords = new() { "_NORMALMAP", "_EMISSION" },
                                properties = new() { new ShaderPropertyData { name = "_Color", typeName = "Color", colorValue = new[] { 1f, 0.5f, 0.2f, 1f } } } } } });
                            components.Add(new ComponentDetailData { typeName = "UnityEngine.Camera", displayName = "Camera", hasEnabled = true, enabled = true, serializedFields = new()
                            { new SerializedFieldData { fieldName = "cullingMask", fieldType = "LayerMask", valueType = "layerMask", intValue = CullingMask },
                              new SerializedFieldData { fieldName = "fieldOfView", fieldType = "Single", valueType = "number", doubleValue = 60 } } });
                        }
                        await Send(Command.NODE_DETAIL_RESPONSE, p.sequence, new NodeDetailResponse { instanceId = detailId, name = name, activeSelf = detailId == 42 ? active : detailId == 44, components = components, texturePreviewMetadata = true, readableTextureIds = new() { 55 } }); break;
                    case Command.SET_PROPERTY:
                        var property = Wire.Body<SetPropertyRequest>(p);
                        if (property.componentType == "GameObject" && property.propertyPath == "activeSelf") active = property.boolValue;
                        else if (property.propertyPath == "clearFlags") ClearFlags = property.intValue;
                        else if (property.propertyPath == "cullingMask") CullingMask = property.intValue;
                        else if (property.propertyPath == "speed") Speed = property.numberValue;
                        await Send(Command.SET_PROPERTY_RESPONSE, p.sequence, new SetPropertyResponse { instanceId = 42, success = true }); break;
                    case Command.GET_FRAME_STATS:
                        await Send(Command.FRAME_STATS_RESPONSE, p.sequence, new FrameStatsResponse { fps = 59.8f, avgFrameTime = 16.7f, minFrameTime = 14, maxFrameTime = 21 }); break;
                    case Command.RPC_CALL:
                        var rpc = Wire.Body<RpcCallRequest>(p);
                        if (rpc.methodName != "NoReply") await Send(Command.RPC_RESPONSE, p.sequence, new RpcCallResponse { success = true, resultJson = rpc.argsJson, methodName = rpc.methodName }); break;
                    case Command.RPC_LIST:
                        await Send(Command.RPC_LIST_RESPONSE, p.sequence, new RpcListResponse { methods = new[] { new RpcMethodInfo { name = "Echo", signature = "(string text)", description = "返回传入参数" } } }); break;
                    case Command.FILE_BROWSE_LIST:
                        LastBrowsePath = Wire.Body<FileBrowseListRequest>(p).relativePath ?? "";
                        await Send(Command.FILE_BROWSE_LIST_RESPONSE, p.sequence, new FileBrowseListResponse { success = true, relativePath = LastBrowsePath, persistentDataPath = "/mock/persistent", entries = new[]
                        { new FileEntry { name = "patches", relativePath = string.IsNullOrEmpty(LastBrowsePath) ? "patches" : LastBrowsePath + "/patches", isDirectory = true },
                          new FileEntry { name = "test.txt", relativePath = string.IsNullOrEmpty(LastBrowsePath) ? "test.txt" : LastBrowsePath + "/test.txt", size = 12 } } }); break;
                    case Command.GET_TEXTURE:
                        string base64 = Convert.ToBase64String(Image);
                        // Current Runtime slices ONE base64 string, always sequence zero.
                        for (int i = 0; i < 2; i++) await Send(Command.TEXTURE_RESPONSE, 0, new TextureResponse { instanceId = Wire.Body<GetTextureRequest>(p).instanceId, chunkIndex = i, totalChunks = 2, isLast = i == 1, status = "ok", data = i == 0 ? base64[..32] : base64[32..] });
                        break;
                    case (Command)InspectionProtocol.TextureOriginal:
                        await Send(Command.TEXTURE_RESPONSE, p.sequence, new TextureResponse { instanceId = Wire.Body<GetTextureRequest>(p).instanceId, chunkIndex = 0, totalChunks = 1, isLast = true, status = "ok", data = Convert.ToBase64String(Image) }); break;
                    case (Command)InspectionProtocol.ScreenPick:
                        await Send((Command)InspectionProtocol.ScreenPickResponse, p.sequence, new ScreenPickResponse { success = true, hits = new() { new ScreenPickHit { instanceId = 44, name = "继承未激活子节点", path = "Root/未激活父节点/继承未激活子节点", kind = "NGUI", camera = "UI Camera" } } }); break;
                    case Command.SCREEN_CAPTURE:
                        for (int i = 0; i < 2; i++) await Send(Command.SCREEN_CAPTURE_CHUNK, p.sequence, new ScreenCaptureChunk { success = true, chunkIndex = i, totalChunks = 2, isLast = i == 1, data = Convert.ToBase64String(i == 0 ? Image[..17] : Image[17..]) }); break;
                    case Command.FILE_BROWSE_DOWNLOAD:
                        string path = Wire.Body<FileBrowseDownloadRequest>(p).relativePath;
                        byte[] downloaded = path == "fixture.logcompr" ? File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "decoder-smoke.logcompr")) : "hello remote"u8.ToArray();
                        await Send(Command.FILE_BROWSE_DOWNLOAD_CHUNK, p.sequence, new FileBrowseDownloadChunk { relativePath = path, totalSize = downloaded.Length, chunkIndex = path == "bad.txt" ? 1 : 0, totalChunks = 1, isLast = true, data = Convert.ToBase64String(downloaded) }); break;
                    case Command.FILE_UPLOAD_START: upload?.Dispose(); upload = new MemoryStream(); break;
                    case Command.FILE_UPLOAD_CHUNK: upload.Write(Convert.FromBase64String(Wire.Body<FileUploadChunkRequest>(p).data)); break;
                    case Command.FILE_UPLOAD_END: Uploaded = upload.ToArray(); await Send(Command.FILE_UPLOAD_ACK, p.sequence, new FileUploadAck { success = true }); break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); client.Dispose(); await loop; upload?.Dispose(); }
}
