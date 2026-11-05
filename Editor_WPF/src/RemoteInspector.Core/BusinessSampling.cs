using System.Threading.Channels;
using RemoteInspector.Commands;
using RemoteInspector.Protocol;

namespace RemoteInspector.Core;
public sealed partial class InspectorSession
{
    public async Task RunBusinessSamplingAsync(int durationMs, Func<SamplingWindowBatch, Task> receive, CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        var queue = Channel.CreateBounded<SamplingWindowBatch>(new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        void OnPacket(InspectorSession sender, MessagePacket packet)
        {
            if (packet.commandId != PerformanceProtocol.SamplingBatch) return;
            var batch = Wire.Body<SamplingWindowBatch>(packet);
            if (batch.sessionId != id) return;
            if (!queue.Writer.TryWrite(batch)) queue.Writer.TryComplete(new IOException("采样接收队列已满，请降低采样频率。"));
        }
        PacketReceived += OnPacket;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(durationMs + 15000));
        try
        {
            var packet = await RequestAsync((Command)PerformanceProtocol.SamplingStart,
                new SamplingWindowRequest { sessionId = id, durationMs = durationMs, intervalMs = 50, maxSamples = 400 }, timeout.Token, 5, (Command)PerformanceProtocol.SamplingAck);
            var ack = Wire.Body<SamplingWindowAck>(packet);
            if (!ack.success || ack.sessionId != id) throw new IOException(ack.error ?? "采样窗口启动失败，请更新游戏 Runtime 和 Performance SDK。");
            while (true)
            {
                var batch = await queue.Reader.ReadAsync(timeout.Token);
                if (batch.samples == null || batch.samples.Count > 8 || batch.samples.Sum(s => (long)(s.stack?.Length ?? 0)) > 8 * 65536) throw new InvalidDataException("采样批次超出限制。");
                await receive(batch);
                if (batch.completed) break;
            }
        }
        finally
        {
            PacketReceived -= OnPacket;
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (IsConnected) await SendAsync((Command)PerformanceProtocol.SamplingStop, new SamplingWindowRequest { sessionId = id }, stop.Token);
            }
            catch { /* Disconnect/expiry also stops the runtime sampling session. */ }
        }
    }
}
