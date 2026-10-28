using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using RemoteInspector.Commands;
using RemoteInspector.Protocol;
using RemoteInspector.Core.Models;

namespace RemoteInspector.Core;

public sealed class InspectorSession : IAsyncDisposable
{
    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendLock = new(1);
    internal readonly SemaphoreSlim UploadLock = new(1);
    internal readonly SemaphoreSlim TextureLock = new(1);
    internal volatile Exchange TextureExchange;
    private readonly ConcurrentDictionary<ushort, Exchange> pending = new();
    private readonly object sequenceLock = new();
    private ushort sequence;
    private long lastReceive = Stopwatch.GetTimestamp();
    private Task runTask = Task.CompletedTask;
    private int closed;
    public Guid Id { get; } = Guid.NewGuid();
    public string Endpoint { get; }
    public bool IsConnected => Volatile.Read(ref closed) == 0;
    public ClientHelloRequest Hello { get; private set; }
    public NodeTreeResponse Tree { get; private set; }
    public FrameStatsResponse Stats { get; private set; }
    public ConcurrentDictionary<int, NodeDetailResponse> Details { get; } = new();
    public ConcurrentDictionary<int, byte[]> Images { get; } = new();
    public string DisplayName => $"{Hello?.deviceName ?? Endpoint} [{Hello?.platform ?? "握手中"}]";
    public event Action<InspectorSession, MessagePacket> PacketReceived;
    public event Action<InspectorSession> Closed;
    public event Action<string> Diagnostic;

    internal InspectorSession(TcpClient client)
    {
        this.client = client;
        stream = client.GetStream();
        Endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
    }
    internal void Start() => runTask = RunAsync();
    public void Disconnect() => Close();

    private async Task RunAsync()
    {
        var heartbeat = HeartbeatAsync();
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var packet = await Wire.ReadAsync(stream, lifetime.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref lastReceive, Stopwatch.GetTimestamp());
                try
                {
                    UpdateState(packet);
                    if (pending.TryGetValue(packet.sequence, out var exchange) && exchange.Commands.Contains(packet.GetCommand()))
                        if (!exchange.Channel.Writer.TryWrite(packet))
                            exchange.Channel.Writer.TryComplete(new IOException("Response queue exceeded its limit."));
                    // Existing MaterialCollector always sends texture responses with sequence=0.
                    if (packet.sequence == 0 && packet.GetCommand() == Command.TEXTURE_RESPONSE && TextureExchange is { } texture)
                        if (!texture.Channel.Writer.TryWrite(packet)) texture.Channel.Writer.TryComplete(new IOException("Texture queue exceeded its limit."));
                    PacketReceived?.Invoke(this, packet);
                }
                catch (Exception ex)
                {
                    if (pending.TryGetValue(packet.sequence, out var failed)) failed.Channel.Writer.TryComplete(ex);
                    Diagnostic?.Invoke($"{Endpoint} {packet.GetCommand()}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        { if (!lifetime.IsCancellationRequested) Diagnostic?.Invoke($"{Endpoint}: {ex.Message}"); }
        finally { Close(); await heartbeat.ConfigureAwait(false); }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastReceive)).TotalSeconds > 30)
                    throw new IOException("Heartbeat timed out.");
                await SendAsync(Command.HEARTBEAT, new HeartbeatMessage
                { clientTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Diagnostic?.Invoke($"{Endpoint}: {ex.Message}"); Close(); }
    }

    private void UpdateState(MessagePacket packet)
    {
        switch (packet.GetCommand())
        {
            case Command.CLIENT_HELLO: Hello = Wire.Body<ClientHelloRequest>(packet); break;
            case Command.NODE_TREE_RESPONSE: Tree = Wire.Body<NodeTreeResponse>(packet); break;
            case Command.NODE_TREE_RESPONSE_BINARY:
                var dto = BinaryNodeTreeCodec.Decode(packet.bodyBytes) ?? throw new InvalidDataException("Invalid binary node tree.");
                var nodes = dto.allNodes.Select(n => new NodeInfo
                {
                    instanceId = n.instanceId, path = n.path,
                    name = (n.path ?? "").Split('/').Last().Replace("[DDOL] ", ""),
                    tag = n.tag, layer = n.layer, activeSelf = n.activeSelf,
                    activeInHierarchy = n.activeInHierarchy, childIds = n.childIds, componentTypes = n.componentTypes
                }).ToList();
                Tree = new NodeTreeResponse { sequence = packet.sequence, allNodes = nodes,
                    rootNodes = dto.rootIndices.Select(i => i >= 0 && i < nodes.Count ? nodes[i] : throw new InvalidDataException("Invalid root index.")).ToList(),
                    sceneName = Tree?.sceneName ?? "", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                break;
            case Command.NODE_DETAIL_RESPONSE:
                var detail = Wire.Body<NodeDetailResponse>(packet);
                Details[detail.instanceId] = detail;
                break;
            case Command.FRAME_STATS_RESPONSE: Stats = Wire.Body<FrameStatsResponse>(packet); break;
            case Command.NODE_DETAIL_RESPONSE_BINARY:
                throw new NotSupportedException("Runtime currently sends JSON details; binary detail codec is not supplied by the source project.");
        }
    }

    internal Exchange OpenExchange(params Command[] commands)
    {
        lock (sequenceLock)
        {
            for (int i = 0; i < ushort.MaxValue; i++)
            {
                if (++sequence == 0) ++sequence;
                var exchange = new Exchange(this, sequence, commands);
                if (pending.TryAdd(sequence, exchange)) return exchange;
            }
        }
        throw new InvalidOperationException("Too many in-flight requests.");
    }

    public async Task SendAsync(Command command, object body = null, CancellationToken token = default)
        => await SendAtAsync(command, 0, body, token).ConfigureAwait(false);

    internal async Task SendAtAsync(Command command, ushort seq, object body, CancellationToken token)
    {
        if (!IsConnected) throw new IOException("Session is disconnected.");
        var bytes = Wire.Encode(command, seq, body);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await sendLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await stream.WriteAsync(bytes, linked.Token).ConfigureAwait(false); }
        catch { Close(); throw; } // A partially written frame cannot be resumed safely.
        finally { sendLock.Release(); }
    }

    public async Task<MessagePacket> RequestAsync(Command command, object body, CancellationToken token = default,
        int timeoutSeconds = 15, params Command[] responses)
    {
        using var exchange = OpenExchange(responses);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        await SendAtAsync(command, exchange.Sequence, body, timeout.Token).ConfigureAwait(false);
        return await exchange.ReadAsync(timeout.Token).ConfigureAwait(false);
    }

    public Task<MessagePacket> RefreshTreeAsync(CancellationToken token = default) =>
        RequestAsync(Command.GET_NODE_TREE, new RequestBase(), token, 30, Command.NODE_TREE_RESPONSE, Command.NODE_TREE_RESPONSE_BINARY);
    public Task<MessagePacket> GetDetailAsync(int instanceId, CancellationToken token = default) =>
        RequestAsync(Command.GET_NODE_DETAIL, new GetNodeDetailRequest { instanceId = instanceId, detailed = true }, token, 15, Command.NODE_DETAIL_RESPONSE);

    private void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        lifetime.Cancel();
        client.Dispose();
        foreach (var exchange in pending.Values) exchange.Channel.Writer.TryComplete(new IOException("Session disconnected."));
        Closed?.Invoke(this);
    }

    public async ValueTask DisposeAsync() { Close(); await runTask.ConfigureAwait(false); }

    internal sealed class Exchange : IDisposable
    {
        private readonly InspectorSession owner;
        public ushort Sequence { get; }
        public HashSet<Command> Commands { get; }
        public Channel<MessagePacket> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<MessagePacket>(
            new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        public Exchange(InspectorSession owner, ushort sequence, Command[] commands)
        { this.owner = owner; Sequence = sequence; Commands = commands.ToHashSet(); }
        public async Task<MessagePacket> ReadAsync(CancellationToken token) => await Channel.Reader.ReadAsync(token).ConfigureAwait(false);
        public void Dispose() { owner.pending.TryRemove(Sequence, out _); Channel.Writer.TryComplete(); }
    }
}
