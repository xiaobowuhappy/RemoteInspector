using RemoteInspector.Core;
if (args.Length == 3 && args[0] == "--layers")
{
    var layers = LayerCatalog.ReadTagManager(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2])));
    File.WriteAllText(args[2], System.Text.Json.JsonSerializer.Serialize(layers, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Exported 32 layer slots to " + args[2]);
    return 0;
}
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: EnumCatalog <output.json> <assembly-directory> [more-directories]");
    return 1;
}
var files = args.Skip(1).SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.dll", SearchOption.AllDirectories) : Array.Empty<string>());
var catalog = EnumCatalog.Scan(files, Console.Error.WriteLine);
catalog.Save(args[0]);
Console.WriteLine($"Exported {catalog.Definitions.Count} enum definitions to {Path.GetFullPath(args[0])}");
return 0;
