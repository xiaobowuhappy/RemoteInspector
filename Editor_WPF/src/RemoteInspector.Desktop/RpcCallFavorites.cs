using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using RemoteInspector.Protocol;

namespace RemoteInspector.Desktop;

public sealed record RpcCallFavorite(string Assembly, string Type, string MethodKey, string Signature, string Args, bool IncludeInactive, bool IncludeOtherScenes)
{
    public string Label => Type + " · " + Signature;
}
public static class RpcCallFavoriteStore
{
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuperMain", "RemoteInspector", "rpc-call-favorites.json");
    public static List<RpcCallFavorite> Read(string path)
    {
        if (!File.Exists(path)) return new();
        var rows = JsonSerializer.Deserialize<List<RpcCallFavorite>>(File.ReadAllText(path)) ?? new();
        return rows.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Type) && !string.IsNullOrWhiteSpace(r.MethodKey)).Take(200).ToList();
    }
    public static void Write(string path, List<RpcCallFavorite> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
public partial class ReflectionRpcView
{
    public string FavoritesPath { get; set; } = RpcCallFavoriteStore.FilePath;
    private void RefreshCallFavorites()
    {
        try
        {
            var previous = CallFavorites.SelectedItem as RpcCallFavorite;
            var rows = RpcCallFavoriteStore.Read(FavoritesPath);
            CallFavorites.ItemsSource = rows;
            FavoriteCount.Text = " · " + rows.Count;
            CallFavorites.SelectedItem = rows.FirstOrDefault(r => r == previous);
        }
        catch (Exception ex) { Status.Text = "读取调用收藏失败：" + ex.Message; }
    }
    private void CallFavorites_Opened(object sender, EventArgs e) => RefreshCallFavorites();
    private void SaveCallFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (busy || selectedType == null || selectedMethod?.callable != true) { Status.Text = "请先选择可调用的方法。"; return; }
        try
        {
            using var args = JsonDocument.Parse(ArgumentsJson.Text);
            if (args.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("参数必须为 JSON 数组。");
            var item = new RpcCallFavorite(selectedType.assembly, selectedType.name, selectedMethod.key, selectedMethod.signature, ArgumentsJson.Text, IncludeInactive.IsChecked == true, IncludeOtherScenes.IsChecked == true);
            var rows = RpcCallFavoriteStore.Read(FavoritesPath);
            rows.RemoveAll(r => r.Assembly == item.Assembly && r.Type == item.Type && r.MethodKey == item.MethodKey);
            rows.Insert(0, item); RpcCallFavoriteStore.Write(FavoritesPath, rows.Take(200).ToList());
            RefreshCallFavorites(); CallFavorites.SelectedItem = item; Status.Text = "调用已收藏到本地；再次收藏同一方法会更新参数。";
        }
        catch (Exception ex) { Status.Text = "收藏失败：" + ex.Message; }
    }
    private void RemoveCallFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (busy || CallFavorites.SelectedItem is not RpcCallFavorite item) return;
        try
        {
            var rows = RpcCallFavoriteStore.Read(FavoritesPath); rows.RemoveAll(r => r == item);
            RpcCallFavoriteStore.Write(FavoritesPath, rows); RefreshCallFavorites(); Status.Text = "已移除本地调用收藏。";
        }
        catch (Exception ex) { Status.Text = "移除失败：" + ex.Message; }
    }
    private async void LoadCallFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (CallFavorites.SelectedItem is not RpcCallFavorite item) { Status.Text = "请选择一条调用收藏。"; return; }
        await Run(async () =>
        {
            // Explicit load is allowed to query this exact class. Never trust persisted instance IDs.
            var value = await Send(ReflectionRpcProtocol.Query, ReflectionRpcProtocol.QueryResponse,
                new() { assembly = item.Assembly, type = item.Type, refresh = true });
            var method = value.methods.FirstOrDefault(m => m.key == item.MethodKey && m.signature == item.Signature);
            if (method == null || !method.callable) throw new InvalidOperationException("收藏的方法已变更或不可调用，请刷新目录重新选择；未执行调用。");
            cache.Methods[item.Assembly + "\n" + item.Type] = value;
            ClassSearch.Clear(); MethodSearch.Clear();
            var type = new ReflectionRpcType { assembly = item.Assembly, name = item.Type };
            AssemblyPicker.ItemsSource = new[] { new RpcAssemblyChoice(item.Assembly.Split(',')[0], item.Assembly) }; AssemblyPicker.SelectedIndex = 0;
            TypesGrid.ItemsSource = new[] { new ReflectionTypeRow(type) }; TypesGrid.SelectedIndex = 0;
            MethodsGrid.SelectedItem = MethodsGrid.Items.Cast<ReflectionMethodRow>().First(r => r.Value.key == method.key);
            IncludeInactive.IsChecked = item.IncludeInactive; IncludeOtherScenes.IsChecked = item.IncludeOtherScenes;
            InstancePicker.ItemsSource = new[] { new RpcInstanceChoice("优先当前场景激活实例（唯一时调用）", 0) }; InstancePicker.SelectedIndex = 0;
            JsonMode.IsChecked = true; ArgumentsJson.IsReadOnly = false; ArgumentsJson.Text = item.Args;
            Status.Text = "已载入收藏并校验方法；检查参数后手动执行。实例将重新查找，不复用旧对象。";
        });
    }
}
