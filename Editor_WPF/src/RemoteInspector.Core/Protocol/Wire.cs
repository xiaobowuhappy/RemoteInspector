using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteInspector.Commands;

namespace RemoteInspector.Protocol;

public static class Wire
{
    // Unity JsonUtility serializes public fields, with their original casing.
    public static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 128
    };

    public static T Body<T>(MessagePacket packet) => JsonSerializer.Deserialize<T>(packet.bodyJson, Json);

    public static byte[] Encode(Command command, ushort sequence, object body)
    {
        var node = JsonSerializer.SerializeToNode(body ?? new RequestBase(), Json) as JsonObject
            ?? throw new ArgumentException("Request body must be an object.");
        node["sequence"] = sequence;
        return Encode(new MessagePacket(command, sequence, node.ToJsonString(Json)));
    }

    public static byte[] Encode(MessagePacket packet)
    {
        byte[] body = packet.bodyBytes ?? Encoding.UTF8.GetBytes(packet.bodyJson ?? "");
        int length = checked(body.Length + 8);
        if (length > MessageProtocol.MAX_BODY_SIZE) throw new InvalidDataException("Packet exceeds 16 MiB.");
        var bytes = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), packet.commandId);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), packet.sequence);
        body.CopyTo(bytes, 8);
        return bytes;
    }

    public static async Task<MessagePacket> ReadAsync(Stream stream, CancellationToken cancellation = default)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 8 || length > MessageProtocol.MAX_BODY_SIZE)
            throw new InvalidDataException($"Invalid packet length: {length}.");
        var packet = new MessagePacket
        {
            commandId = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)),
            sequence = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6))
        };
        var body = new byte[length - 8];
        await stream.ReadExactlyAsync(body, cancellation).ConfigureAwait(false);
        if (packet.IsBinaryCommand) packet.bodyBytes = body;
        else packet.bodyJson = Encoding.UTF8.GetString(body);
        return packet;
    }
}
