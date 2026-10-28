using System.Text.Json;

namespace RemoteInspector.Core;

public sealed class LayerCatalog
{
    public string[] Names { get; set; } = new string[32];
    public string Name(int index) => string.IsNullOrWhiteSpace(Names.ElementAtOrDefault(index)) ? $"Layer {index}（未命名）" : Names[index];
    public string Summary(int mask)
    {
        if (mask == 0) return "Nothing";
        if (mask == -1) return "Everything";
        var selected = Enumerable.Range(0, 32).Where(i => (mask & (1 << i)) != 0).Select(Name).ToArray();
        return selected.Length <= 3 ? string.Join(", ", selected) : string.Join(", ", selected.Take(2)) + $" + {selected.Length - 2} 层";
    }
    public static LayerCatalog Load(string path)
    {
        if (File.Exists(path))
        {
            var catalog = JsonSerializer.Deserialize<LayerCatalog>(File.ReadAllText(path));
            if (catalog?.Names?.Length == 32) return catalog;
            throw new InvalidDataException("Layer catalog must contain exactly 32 slots.");
        }
        var fallback = new LayerCatalog();
        fallback.Names[0] = "Default"; fallback.Names[1] = "TransparentFX"; fallback.Names[2] = "Ignore Raycast";
        fallback.Names[4] = "Water"; fallback.Names[5] = "UI";
        return fallback;
    }
    public static LayerCatalog ReadTagManager(string path)
    {
        var names = new List<string>(); bool reading = false;
        foreach (string line in File.ReadLines(path))
        {
            if (line == "  layers:") { reading = true; continue; }
            if (!reading) continue;
            if (!line.StartsWith("  -", StringComparison.Ordinal)) break;
            string value = line[3..].Trim();
            if (value.StartsWith('"')) value = JsonSerializer.Deserialize<string>(value);
            else if (value.StartsWith('\'') && value.EndsWith('\'')) value = value[1..^1].Replace("''", "'");
            names.Add(value ?? "");
        }
        if (names.Count != 32) throw new InvalidDataException("TagManager layers must have 32 entries; empty slots must be retained.");
        return new LayerCatalog { Names = names.ToArray() };
    }
}
