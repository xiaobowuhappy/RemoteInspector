using System.Text;
using System.Text.Json;
using RemoteInspector.Core.Models;
using RemoteInspector.Protocol;

namespace RemoteInspector.Core;

public sealed class SnapshotData
{
    public long SavedAtUtcTicks;
    public NodeTreeResponse NodeTree;
    public Dictionary<int, NodeDetailResponse> NodeDetails = new();
    public int SelectedInstanceId;
    public string SelectedPath;
    public List<string> ExpandedPaths = new();
    public Dictionary<string, byte[]> TexturesPng = new();
}

/// <summary>Compatible with the existing RISNP v1 file layout (17-byte header).</summary>
public static class SnapshotFile
{
    private static readonly byte[] Magic = "RISNP"u8.ToArray();
    public static void Save(string path, SnapshotData snapshot)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var output = new BinaryWriter(File.Create(temp), Encoding.UTF8))
            {
                output.Write(Magic); output.Write((short)1); output.Write((short)0); output.Write(snapshot.SavedAtUtcTicks);
                output.Write(snapshot.NodeDetails.Count);
                foreach (var pair in snapshot.NodeDetails) { output.Write(pair.Key); WriteJson(output, pair.Value); }
                WriteJson(output, snapshot.NodeTree);
                WriteJson(output, new Metadata { selectedInstanceId = snapshot.SelectedInstanceId,
                    selectedPath = snapshot.SelectedPath, expandedPaths = snapshot.ExpandedPaths });
                output.Write(snapshot.TexturesPng.Count);
                foreach (var pair in snapshot.TexturesPng)
                { WriteBytes(output, Encoding.UTF8.GetBytes(pair.Key)); WriteBytes(output, pair.Value); }
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static SnapshotData Load(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        if (!reader.ReadBytes(5).SequenceEqual(Magic) || reader.ReadInt16() != 1)
            throw new InvalidDataException("Expected a RISNP v1 snapshot.");
        reader.ReadInt16();
        var result = new SnapshotData { SavedAtUtcTicks = reader.ReadInt64() };
        int details = ReadCount(reader, 1_000_000);
        for (int i = 0; i < details; i++)
        {
            int id = reader.ReadInt32();
            result.NodeDetails.Add(id, ReadJson<NodeDetailResponse>(reader));
        }
        result.NodeTree = ReadJson<NodeTreeResponse>(reader);
        var meta = ReadJson<Metadata>(reader);
        result.SelectedInstanceId = meta?.selectedInstanceId ?? 0;
        result.SelectedPath = meta?.selectedPath;
        result.ExpandedPaths = meta?.expandedPaths ?? new();
        int images = ReadCount(reader, 10000);
        long totalImages = 0;
        for (int i = 0; i < images; i++)
        {
            string key = Encoding.UTF8.GetString(ReadBytes(reader));
            var bytes = ReadBytes(reader);
            totalImages += bytes.Length;
            if (totalImages > 256 * 1024 * 1024) throw new InvalidDataException("Snapshot images exceed 256 MiB.");
            result.TexturesPng.Add(key, bytes);
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Unexpected snapshot trailing bytes.");
        return result;
    }
    private static int ReadCount(BinaryReader reader, int maximum)
    {
        int value = reader.ReadInt32();
        if (value < 0 || value > maximum) throw new InvalidDataException("Invalid snapshot count or length.");
        return value;
    }
    private static byte[] ReadBytes(BinaryReader reader)
    {
        int size = ReadCount(reader, 64 * 1024 * 1024);
        if (size > reader.BaseStream.Length - reader.BaseStream.Position) throw new EndOfStreamException("Truncated snapshot.");
        return reader.ReadBytes(size);
    }
    private static T ReadJson<T>(BinaryReader reader) where T : class
    { var bytes = ReadBytes(reader); return bytes.Length == 0 ? null : JsonSerializer.Deserialize<T>(bytes, Wire.Json); }
    private static void WriteBytes(BinaryWriter writer, byte[] bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
    private static void WriteJson<T>(BinaryWriter writer, T value) => WriteBytes(writer,
        value == null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json));
    private sealed class Metadata
    {
        public int selectedInstanceId;
        public string selectedPath;
        public List<string> expandedPaths;
    }
}
