using System.Collections.ObjectModel;
using System.Globalization;
using System.ComponentModel;
using System.Collections.Specialized;
using RemoteInspector.Core.Models;
using RemoteInspector.Core;
using RemoteInspector.Protocol;

namespace RemoteInspector.Desktop;

/// <summary>One linear index per snapshot; filtering retains ancestor paths.</summary>
public sealed class TreeProjection
{
    public IReadOnlyDictionary<int, NodeInfo> Nodes { get; }
    public IReadOnlyDictionary<int, int> Parents { get; }
    internal HashSet<int> Matches { get; }
    internal HashSet<int> Visible { get; }
    internal HashSet<int> AutomaticExpanded { get; } = new();
    internal HashSet<int> Expanded { get; }
    public List<TreeRow> Roots { get; } = new();
    public int ActiveCount { get; }
    public int InactiveCount => Nodes.Count - ActiveCount;
    public int MatchCount => Matches.Count;
    internal int SelectedId { get; set; }
    public TreeProjection(NodeTreeResponse tree, string search, int stateFilter, HashSet<int> expanded, int selectedId)
    {
        Nodes = (tree.allNodes ?? tree.rootNodes ?? new()).Where(n => n != null).ToDictionary(n => n.instanceId);
        Expanded = expanded;
        SelectedId = selectedId;
        ActiveCount = Nodes.Values.Count(n => n.activeInHierarchy);
        var parents = new Dictionary<int, int>();
        foreach (var node in Nodes.Values)
            foreach (int id in node.childIds ?? new())
                if (Nodes.ContainsKey(id)) parents.TryAdd(id, node.instanceId);
        Parents = parents;
        string[] tokens = (search ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        Matches = Nodes.Values.Where(n => (stateFilter == 0 || n.activeInHierarchy == (stateFilter == 1)) &&
            tokens.All(t => (n.name ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) ||
                (n.path ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) ||
                (n.componentTypes?.Any(c => c.Contains(t, StringComparison.OrdinalIgnoreCase)) ?? false)))
            .Select(n => n.instanceId).ToHashSet();
        Visible = new HashSet<int>(Matches);
        if (tokens.Length > 0 || stateFilter != 0)
            foreach (int id in Matches)
            {
                int current = id;
                var visited = new HashSet<int> { current };
                while (parents.TryGetValue(current, out int parent) && visited.Add(parent))
                {
                    bool added = AutomaticExpanded.Add(parent);
                    Visible.Add(parent);
                    if (!added) break;
                    current = parent;
                }
            }
        // Reveal a retained selection without expanding unrelated branches.
        if (Visible.Contains(selectedId))
        {
            int current = selectedId;
            var visited = new HashSet<int> { current };
            while (parents.TryGetValue(current, out int parent) && visited.Add(parent))
            { AutomaticExpanded.Add(parent); current = parent; }
        }
        var rootIds = (tree.rootNodes ?? new()).Where(n => n != null).Select(n => n.instanceId).ToList();
        if (rootIds.Count == 0) rootIds.AddRange(Nodes.Keys.Where(id => !parents.ContainsKey(id)));
        foreach (int id in rootIds.Distinct())
            if (Visible.Contains(id) && Nodes.TryGetValue(id, out var node)) Roots.Add(new TreeRow(node, this, selectedId));
    }
}

public sealed class TreeRow : INotifyPropertyChanged
{
    private readonly TreeProjection projection;
    private readonly HashSet<int> ancestors;
    private readonly int selectedId;
    public NodeInfo Node { get; }
    public string Label => Node?.name ?? "…";
    public bool IsContext => Node != null && !projection.Matches.Contains(Node.instanceId);
    public string State => Node == null ? "" : !Node.activeSelf ? "自身未激活" : Node.activeInHierarchy ? "已激活" : "父级未激活";
    public string StateLabel => (IsContext ? "路径 · " : "") + State;
    public string NameColor => Node?.activeInHierarchy == true && !IsContext ? "#E6EDF5" : "#98A6B8";
    public string StateColor => Node == null ? "#98A6B8" : !Node.activeSelf ? "#A7B1BF" : Node.activeInHierarchy ? "#82DBAE" : "#EDC77F";
    public string StateBackground => Node == null ? "Transparent" : !Node.activeSelf ? "#303946" : Node.activeInHierarchy ? "#1E413A" : "#483E29";
    public string Tooltip => Node == null ? "" : $"{Node.path}\nInstanceID: {Node.instanceId}\nactiveSelf: {Node.activeSelf}\nactiveInHierarchy: {Node.activeInHierarchy}\n{(IsContext ? "此节点用于保留匹配结果的父级路径。" : State)}";
    public string Path => Node?.path;
    public ObservableCollection<TreeRow> Children { get; } = new();
    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set { isSelected = value; if (value && Node != null) projection.SelectedId = Node.instanceId; }
    }
    public bool IsExpanded
    {
        get => Node != null && (projection.Expanded.Contains(Node.instanceId) || projection.AutomaticExpanded.Contains(Node.instanceId));
        set
        {
            if (Node == null) return;
            if (value) { projection.Expanded.Add(Node.instanceId); Load(); }
            else { projection.Expanded.Remove(Node.instanceId); projection.AutomaticExpanded.Remove(Node.instanceId); }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }
    private bool loaded;
    internal TreeRow(NodeInfo node, TreeProjection projection, int selectedId, HashSet<int> ancestors = null)
    {
        Node = node; this.projection = projection; this.selectedId = selectedId; this.ancestors = ancestors ?? new();
        IsSelected = node?.instanceId == selectedId;
        if (node?.childIds?.Any(id => projection.Visible.Contains(id) && !this.ancestors.Contains(id) && id != node.instanceId) == true)
            Children.Add(new TreeRow(null, projection, selectedId));
    }
    public void Load()
    {
        if (loaded || Node == null) return;
        loaded = true; Children.Clear();
        var path = new HashSet<int>(ancestors) { Node.instanceId };
        foreach (int id in Node.childIds ?? new())
            if (!path.Contains(id) && projection.Visible.Contains(id) && projection.Nodes.TryGetValue(id, out var node))
                Children.Add(new TreeRow(node, projection, projection.SelectedId, path));
    }
    public event PropertyChangedEventHandler PropertyChanged;
}

public sealed record PropertyChoice(string Label, string Value);

/// <summary>Publish a single reset after a complete inspector model is built.</summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    private bool batching;
    public IDisposable Batch()
    { batching = true; return new Completion(this); }
    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    { if (!batching) base.OnCollectionChanged(e); }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    { if (!batching) base.OnPropertyChanged(e); }
    private sealed class Completion(BatchObservableCollection<T> owner) : IDisposable
    {
        public void Dispose()
        {
            owner.batching = false;
            owner.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            owner.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            owner.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}

public sealed class PropertyRow : INotifyPropertyChanged
{
    public string Label { get; init; }
    public string FieldLabel => Label.Contains(" / ") ? Label[(Label.LastIndexOf(" / ", StringComparison.Ordinal) + 3)..] : Label;
    public string DisplayLabel => FieldLabel switch
    {
        "activeSelf" => "Active", "enabled" => "Enabled", "localPosition" => "Position", "localRotation" => "Rotation", "localScale" => "Scale",
        "cullingMask" => "Culling Mask", "fieldOfView" => "Field of View", "nearClipPlane" => "Near Clip Plane", "farClipPlane" => "Far Clip Plane", _ => FieldLabel
    };
    public string ComponentGroup { get; init; }
    public bool ComponentInactive { get; init; }
    public string ComponentStateColor => ComponentInactive ? "#9BA5B0" : "#7DCB94";
    public string ComponentStateLabel { get; init; }
    public double ParameterOpacity => FieldLabel is "enabled" or "activeSelf" ? 1 : ComponentInactive ? 0.55 : 1;
    public string Section { get; init; }
    public string Type { get; init; }
    private string value;
    public string Value
    {
        get => value;
        set
        {
            this.value = value;
            foreach (string property in new[] { nameof(Value), nameof(AxisX), nameof(AxisY), nameof(AxisZ), nameof(AxisW), nameof(LayerMaskText), nameof(IsDirty), nameof(IsChanged), nameof(EditState), nameof(BaselineHint) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
    private string remoteValue;
    private string baseline;
    public bool IsDirty => remoteValue != null && Value != remoteValue;
    public bool IsChanged => baseline != null && Value != baseline;
    public string EditState => IsDirty ? "● 未应用" : IsChanged ? "◆ 已变化" : "";
    public string BaselineHint => baseline == null ? "尚未记录基准" : "基准：" + baseline;
    public void AcceptRemoteValue() { remoteValue = Value; NotifyState(); }
    public void RevertEdit() { Value = remoteValue ?? Value; }
    public void SetBaseline(string value) { baseline = value; NotifyState(); }
    private void NotifyState()
    {
        foreach (string name in new[] { nameof(IsDirty), nameof(IsChanged), nameof(EditState), nameof(BaselineHint) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public string Hint { get; init; }
    public bool CanEdit { get; init; }
    public bool IsReadOnly => !CanEdit;
    public int TextureId { get; init; }
    public bool CanPreview { get; init; }
    public bool IsTexture => TextureId != 0;
    public bool TextureMetadataKnown { get; init; }
    public string TextureState => CanPreview ? "可预览" : "预览（不可用）";
    public string TextureHint => CanPreview ? "点击弹出纹理预览，可导出 PNG" : TextureMetadataKnown ? "纹理 Read/Write 未开启或不是可读 Texture2D，无法预览" : "旧 Runtime 未提供 Read/Write 信息，请更新游戏包";
    public bool UseApply => CanEdit;
    public SetPropertyRequest Request { get; init; }
    public int MaterialSlot { get; init; } = -1;
    public string TextureProperty { get; init; }
    public IReadOnlyList<PropertyChoice> Choices { get; init; } = Array.Empty<PropertyChoice>();
    public bool UseDropDown => Type == "enum" && Choices.Count > 0;
    public bool UseBoolean => Type == "bool";
    public bool UseVector => Type is "vector2" or "vector3" or "vector4" or "color";
    public int AxisCount => Type == "vector2" ? 2 : Type == "vector3" ? 3 : 4;
    public string AxisLabelX => Type == "color" ? "R" : "X";
    public string AxisLabelY => Type == "color" ? "G" : "Y";
    public string AxisLabelZ => Type == "color" ? "B" : "Z";
    public string AxisLabelW => Type == "color" ? "A" : "W";
    public bool HasAxisZ => Type is "vector3" or "vector4" or "color";
    public bool HasAxisW => Type is "vector4" or "color";
    public bool UseLayerMask => Type == "layerMask";
    public bool UseText => !IsTexture && !UseDropDown && !UseBoolean && !UseVector && !UseLayerMask;
    public bool HasPresets => !UseDropDown && !UseBoolean && !UseLayerMask && Choices.Count > 0 && CanEdit;
    public LayerCatalog Layers { get; init; } = new();
    public string LayerMaskText => int.TryParse(Value, out int mask) ? Layers.Summary(mask) : Value;
    public void ToggleLayer(int index, bool enabled)
    {
        if (index is < 0 or > 31) throw new ArgumentOutOfRangeException(nameof(index));
        int mask = int.Parse(Value, CultureInfo.InvariantCulture);
        Value = (enabled ? mask | (1 << index) : mask & ~(1 << index)).ToString(CultureInfo.InvariantCulture);
    }
    private string Axis(int index) => (Value ?? "").Split(',').ElementAtOrDefault(index)?.Trim() ?? "0";
    private void SetAxis(int index, string value)
    {
        int count = Type == "vector2" ? 2 : Type == "vector3" ? 3 : 4;
        var parts = Enumerable.Range(0, count).Select(Axis).ToArray();
        parts[index] = value; Value = string.Join(", ", parts);
    }
    public string AxisX { get => Axis(0); set => SetAxis(0, value); }
    public string AxisY { get => Axis(1); set => SetAxis(1, value); }
    public string AxisZ { get => Axis(2); set => SetAxis(2, value); }
    public string AxisW { get => Axis(3); set => SetAxis(3, value); }
    public event PropertyChangedEventHandler PropertyChanged;

    public static IReadOnlyList<PropertyChoice> Presets(string type, string current, string path = null,
        IReadOnlyDictionary<string, int> enumValues = null)
    {
        if (type == "bool") return new[] { new PropertyChoice("True · 开启", "True"), new PropertyChoice("False · 关闭", "False") };
        if (type == "enum" && enumValues != null)
        {
            var choices = enumValues.Select(p => new PropertyChoice($"{p.Key}  ({p.Value})", p.Value.ToString(CultureInfo.InvariantCulture))).ToList();
            if (choices.All(c => c.Value != current)) choices.Insert(0, new PropertyChoice("当前值  (" + current + ")", current));
            return choices;
        }
        if (type == "enum") return Array.Empty<PropertyChoice>(); // Names alone do not determine integer constants.
        var result = new List<PropertyChoice>();
        void Add(string label, string value) => result.Add(new(label, value));
        if (type is "int" or "float" or "double")
        {
            if (path == "materials.renderQueue")
                foreach (var p in new[] { ("默认 Shader 队列", "-1"), ("Background", "1000"), ("Geometry", "2000"), ("AlphaTest", "2450"), ("Transparent", "3000"), ("Overlay", "4000") }) Add(p.Item1, p.Item2);
            else foreach (string number in new[] { "0", "1", "-1", "0.5", "10", "100" })
                if (type != "int" || number != "0.5") Add(number, number);
        }
        if (type == "layerMask")
        { Add("Nothing · 无", "0"); Add("Everything · 全部", "-1"); for (int i = 0; i < 32; i++) Add("仅 Layer " + i, unchecked(1 << i).ToString(CultureInfo.InvariantCulture)); }
        if (type.StartsWith("vector", StringComparison.Ordinal))
        {
            int count = type == "vector2" ? 2 : type == "vector3" ? 3 : 4;
            Add("全部为 0", string.Join(", ", Enumerable.Repeat("0", count)));
            Add("全部为 1", string.Join(", ", Enumerable.Repeat("1", count)));
            for (int i = 0; i < count; i++) Add("单位 " + "XYZW"[i], string.Join(", ", Enumerable.Range(0, count).Select(j => i == j ? "1" : "0")));
        }
        if (type == "color")
            foreach (var p in new[] { ("白色", "1, 1, 1, 1"), ("黑色", "0, 0, 0, 1"), ("透明", "0, 0, 0, 0"), ("红色", "1, 0, 0, 1"), ("绿色", "0, 1, 0, 1"), ("蓝色", "0, 0, 1, 1") }) Add(p.Item1, p.Item2);
        return result;
    }
    public SetPropertyRequest ToRequest()
    {
        // Clone the template so a later UI edit cannot mutate an in-flight request.
        var result = System.Text.Json.JsonSerializer.Deserialize<SetPropertyRequest>(
            System.Text.Json.JsonSerializer.Serialize(Request, Wire.Json), Wire.Json);
        var text = Value?.Trim() ?? "";
        switch (Type)
        {
            case "bool": result.boolValue = bool.Parse(text); break;
            case "int": case "enum": case "layerMask": result.intValue = int.Parse(text, CultureInfo.InvariantCulture); break;
            case "float": case "double": result.numberValue = double.Parse(text, CultureInfo.InvariantCulture); break;
            case "string": result.stringValue = Value ?? ""; break;
            case "vector2": case "vector3": case "vector4": case "color":
                var parts = text.Split(',').Select(s => float.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
                int count = Type == "vector2" ? 2 : Type == "vector3" ? 3 : 4;
                if (parts.Length != count || parts.Any(v => !float.IsFinite(v))) throw new FormatException($"需要 {count} 个有限数值，以逗号分隔。");
                result.x = parts[0]; result.y = parts[1]; result.z = count > 2 ? parts[2] : 0; result.w = count > 3 ? parts[3] : 0;
                break;
            default: throw new InvalidOperationException("该字段只读。");
        }
        if (!double.IsFinite(result.numberValue)) throw new FormatException("数值必须有限。");
        if (Type == "float" && Math.Abs(result.numberValue) > float.MaxValue) throw new FormatException("数值超出 Single 范围。");
        return result;
    }
}

public sealed record LogRow(string Time, int Severity, string Text)
{ public string Level => Severity switch { 1 => "Warning", 2 => "Error", 3 => "Exception", _ => "Log" }; }
public sealed record FrameRow(string Time, float Fps, float Avg, float Min, float Max);
public sealed record RpcRow(string Name, string Signature, string Description);
public sealed record FileRow(string Name, string Path, bool IsDirectory, long Size, long LastModified = 0)
{
    public string Kind => IsDirectory ? "文件夹" : "文件";
    public string Icon => IsDirectory ? "📁" : System.IO.Path.GetExtension(Name).Equals(".logcompr", StringComparison.OrdinalIgnoreCase) ? "📜" : "📄";
    public string SizeText => IsDirectory ? "—" : Size >= 1024 * 1024 ? $"{Size / 1048576.0:F1} MiB" : Size >= 1024 ? $"{Size / 1024.0:F1} KiB" : $"{Size} B";
    public string Modified => LastModified > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(LastModified).ToLocalTime().ToString("MM-dd HH:mm") : "—";
}
public sealed record PathCrumb(string Caption, string Path, bool IsCurrent)
{
    public bool CanNavigate => !IsCurrent;
    public static IReadOnlyList<PathCrumb> Build(string path)
    {
        string[] parts = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<PathCrumb> { new("PersistentRoot", "", parts.Length == 0) };
        for (int i = 0; i < parts.Length; i++) result.Add(new(parts[i], string.Join('/', parts.Take(i + 1)), i == parts.Length - 1));
        return result;
    }
}
