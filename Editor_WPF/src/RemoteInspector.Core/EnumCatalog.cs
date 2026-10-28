using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace RemoteInspector.Core;

/// <summary>Reads constants as metadata only. Never loads or executes game/Unity assemblies.</summary>
public sealed class EnumCatalog
{
    public List<EnumDefinition> Definitions { get; set; } = new();
    public IReadOnlyDictionary<string, int> Resolve(string type, string[] names, string currentName, int currentValue)
    {
        if (names == null || names.Length == 0) return null;
        var candidates = Definitions.Where(d => (d.FullName == type || d.Name == type) &&
            d.Values.Keys.ToHashSet().SetEquals(names)).ToList();
        if (candidates.Count == 0) return null;
        var first = candidates[0].Values;
        // Name collisions with different constants require an explicit full type name.
        if (candidates.Any(d => d.Values.Any(p => first[p.Key] != p.Value))) return null;
        if (currentName != null && first.TryGetValue(currentName, out int actual) && actual != currentValue) return null;
        return first;
    }
    public static EnumCatalog Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<EnumCatalog>(File.ReadAllText(path)) ?? new() : new();
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static EnumCatalog Scan(IEnumerable<string> assemblies, Action<string> diagnostic = null)
    {
        var result = new EnumCatalog();
        foreach (string path in assemblies.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var image = new PEReader(stream);
                if (!image.HasMetadata) continue;
                var metadata = image.GetMetadataReader();
                foreach (var handle in metadata.TypeDefinitions)
                {
                    var type = metadata.GetTypeDefinition(handle);
                    if (type.BaseType.Kind != HandleKind.TypeReference) continue;
                    var baseType = metadata.GetTypeReference((TypeReferenceHandle)type.BaseType);
                    if (metadata.GetString(baseType.Namespace) != "System" || metadata.GetString(baseType.Name) != "Enum") continue;
                    var values = new Dictionary<string, int>();
                    foreach (var fieldHandle in type.GetFields())
                    {
                        var field = metadata.GetFieldDefinition(fieldHandle);
                        if ((field.Attributes & FieldAttributes.Literal) == 0 || field.GetDefaultValue().IsNil) continue;
                        var constant = metadata.GetConstant(field.GetDefaultValue());
                        var reader = metadata.GetBlobReader(constant.Value);
                        long value = constant.TypeCode switch
                        {
                            ConstantTypeCode.SByte => reader.ReadSByte(), ConstantTypeCode.Byte => reader.ReadByte(),
                            ConstantTypeCode.Int16 => reader.ReadInt16(), ConstantTypeCode.UInt16 => reader.ReadUInt16(),
                            ConstantTypeCode.Int32 => reader.ReadInt32(), ConstantTypeCode.UInt32 => reader.ReadUInt32(),
                            ConstantTypeCode.Int64 => reader.ReadInt64(), _ => long.MaxValue
                        };
                        if (value is >= int.MinValue and <= int.MaxValue) values[metadata.GetString(field.Name)] = (int)value;
                    }
                    if (values.Count == 0) continue;
                    string FullName(TypeDefinition definition)
                    {
                        string name = metadata.GetString(definition.Name);
                        var parent = definition.GetDeclaringType();
                        if (!parent.IsNil) return FullName(metadata.GetTypeDefinition(parent)) + "+" + name;
                        string ns = metadata.GetString(definition.Namespace);
                        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                    }
                    result.Definitions.Add(new EnumDefinition { Name = metadata.GetString(type.Name), FullName = FullName(type), Values = values });
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
            { diagnostic?.Invoke(path + ": " + ex.Message); }
        }
        result.Definitions = result.Definitions.GroupBy(d => d.FullName + ":" + string.Join(";", d.Values.Select(p => p.Key + "=" + p.Value)))
            .Select(g => g.First()).OrderBy(d => d.FullName).ToList();
        return result;
    }
}
public sealed class EnumDefinition
{
    public string Name { get; set; }
    public string FullName { get; set; }
    public Dictionary<string, int> Values { get; set; } = new();
}
