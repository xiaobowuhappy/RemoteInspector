using System;
using System.Collections.Generic;
using System.Threading;
using Coplaydev.Performance;
using RemoteInspector.Commands;
using RemoteInspector.Protocol;

namespace RemoteInspector.Runtime.Core
{
    /// <summary>Only bounded DTO copying runs on the SDK thread. Network I/O has its own worker.</summary>
    internal sealed class PerformanceBridge : IDisposable, IPerformanceForwarder, IStackSamplingReceiver
    {
        private readonly RemoteInspectorClient _client;
        private readonly IDisposable _forwardRegistration;
        private readonly object _gate = new object();
        private readonly List<PerformanceJank> _pending = new List<PerformanceJank>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _worker;
        private bool _disposed;
        private long _connection;
        private IDisposable _sampling;
        private string _samplingId;
        private readonly Queue<SamplingWindowBatch> _sampleBatches = new Queue<SamplingWindowBatch>();
        private int _transportDropped;
        private static string _buildId;
        private static string _architecture;
        public PerformanceBridge(RemoteInspectorClient client)
        {
            _client = client;
            _buildId = Performance.BuildId;
            _architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
            _worker = new Thread(SendLoop) { IsBackground = true, Name = "RemoteInspector.Performance" };
            _forwardRegistration = Performance.RegisterForwarder(this);
            _client.OnConnectionStateChanged += OnConnection;
            _worker.Start();
        }
        private void OnConnection(RemoteInspectorClient.ConnectionState state)
        { lock (_gate) { _connection++; _pending.Clear(); _sampling?.Dispose(); _sampling = null; _samplingId = null; _sampleBatches.Clear(); } }
        private void OnJank(JankEvent value)
        {
            if (value == null || _client.State != RemoteInspectorClient.ConnectionState.Connected) return;
            var dto = new PerformanceJank { eventId = value.eventId, utcMs = value.utcMs, durationMs = value.durationMs,
                status = value.status, captureStatus = value.captureStatus, buildId = value.buildId, architecture = value.architecture };
            int remaining = 256 * 1024;
            foreach (var sample in value.stacks ?? new List<StackSample>())
            {
                if (dto.stacks.Count >= 128 || remaining <= 0) break;
                if (sample == null || string.IsNullOrEmpty(sample.stack)) continue;
                string text = sample.stack ?? "";
                if (text.Length > remaining) text = text.Substring(0, remaining) + "\n[truncated]";
                remaining -= text.Length;
                dto.stacks.Add(new PerformanceStack { thread = sample.thread, stack = text, sampleCount = Math.Max(1, sample.sampleCount),
                    firstSeenOffsetMs = Math.Max(0, sample.monotonicMs - value.startMonotonicMs),
                    lastSeenOffsetMs = Math.Max(0, (sample.lastMonotonicMs == 0 ? sample.monotonicMs : sample.lastMonotonicMs) - value.startMonotonicMs) });
            }
            lock (_gate)
            {
                if (_disposed || _client.State != RemoteInspectorClient.ConnectionState.Connected) return;
                int i = _pending.FindIndex(p => p.eventId == dto.eventId);
                if (i >= 0)
                {
                    if (_pending[i].status != "ongoing" && dto.status == "ongoing") return;
                    _pending[i] = dto;
                }
                else { if (_pending.Count >= 32) _pending.RemoveAt(0); _pending.Add(dto); }
                _wake.Set();
            }
        }
        private void SendLoop()
        {
            try { ProcessPending(); }
            catch { /* Diagnostic transport must never crash the game. */ }
            finally { ReleaseWorker(); }
        }
        private void ProcessPending()
        {
            bool reportedFailure = false;
            while (WaitForPending())
            {
                SendSampleBatches();
                PerformanceJank dto;
                long connection;
                while (TryTakePending(out dto, out connection))
                {
                    Exception error;
                    if (TrySendPending(dto, connection, out error)) reportedFailure = false;
                    else if (!reportedFailure)
                    {
                        reportedFailure = true;
                        ReportSendFailure(dto, error);
                    }
                }
            }
        }
        private bool WaitForPending()
        {
            _wake.WaitOne();
            lock (_gate) { return !_disposed; }
        }
        private bool TryTakePending(out PerformanceJank dto, out long connection)
        {
            lock (_gate)
            {
                connection = _connection;
                dto = null;
                if (_disposed || _pending.Count == 0) return false;
                dto = _pending[0];
                _pending.RemoveAt(0);
                return true;
            }
        }
        private bool IsCurrentConnection(long connection)
        {
            lock (_gate) { return !_disposed && _connection == connection; }
        }
        private bool TrySendPending(PerformanceJank dto, long connection, out Exception error)
        {
            error = null;
            try
            {
                string json = MessageSerializer.SerializeBody(dto);
                if (string.IsNullOrEmpty(json)) throw new InvalidOperationException("Jank serialization returned an empty payload");
                if (IsCurrentConnection(connection) && _client.State == RemoteInspectorClient.ConnectionState.Connected)
                    _client.SendPacket((Command)PerformanceProtocol.Jank, 0, json);
                return true;
            }
            catch (Exception ex) { error = ex; return false; }
        }
        private static void ReportSendFailure(PerformanceJank dto, Exception error)
        {
            try { UnityEngine.Debug.LogWarning("[RemoteInspector.Performance] event=" + dto.eventId + " status=" + dto.status + " forward failed: " + error.GetType().Name + ": " + error.Message); }
            catch { /* A custom log handler must not terminate forwarding either. */ }
        }
        private void ReleaseWorker()
        {
            lock (_gate) { _disposed = true; _pending.Clear(); _wake.Dispose(); }
        }
        public void HandleSampling(MessagePacket packet)
        {
            var request = MessageSerializer.DeserializeBody<SamplingWindowRequest>(packet);
            var ack = new SamplingWindowAck { sessionId = request?.sessionId };
            try
            {
                if (request == null || string.IsNullOrEmpty(request.sessionId) || request.sessionId.Length > 64) throw new ArgumentException("Invalid sampling session ID");
                lock (_gate)
                {
                    if (_disposed) throw new ObjectDisposedException(nameof(PerformanceBridge));
                    if (packet.commandId == PerformanceProtocol.SamplingStop)
                    { if (_samplingId == request.sessionId) { _sampling?.Dispose(); _sampling = null; } }
                    else
                    {
                        if (_sampling != null) throw new InvalidOperationException("SamplingSessionBusy");
                        _samplingId = request.sessionId; _transportDropped = 0; _sampleBatches.Clear();
                        try { _sampling = Performance.StartUnityMainSampling(new StackSamplingOptions { DurationMs = request.durationMs, IntervalMs = request.intervalMs, MaxSamples = request.maxSamples }, this, request.sessionId); }
                        catch { _samplingId = null; throw; }
                    }
                }
                ack.success = true;
            }
            catch (Exception ex) { ack.error = ex.Message; }
            _client.SendPacket((Command)PerformanceProtocol.SamplingAck, packet.sequence, MessageSerializer.SerializeBody(ack));
        }
        void IStackSamplingReceiver.OnStackSamples(StackSamplingBatch batch)
        {
            var dto = new SamplingWindowBatch { sessionId = batch.SessionId, completed = batch.Completed, error = batch.Error, attempts = batch.Attempts, captured = batch.Captured, dropped = batch.Dropped, buildId = _buildId, architecture = _architecture };
            foreach (var sample in batch.Samples)
                dto.samples.Add(new SamplingWindowSample { stack = sample.stack, thread = sample.thread, timestamp = batch.StartedUtcMs + Math.Max(0, sample.monotonicMs - batch.StartedMonotonicMs) });
            lock (_gate)
            {
                if (_disposed || _samplingId != batch.SessionId) return;
                if (_sampleBatches.Count >= 8) { var lost = _sampleBatches.Dequeue(); _transportDropped += lost.samples.Count; }
                dto.transportDropped = _transportDropped; _sampleBatches.Enqueue(dto);
                if (batch.Completed) _sampling = null;
                _wake.Set();
            }
        }
        private void SendSampleBatches()
        {
            while (true)
            {
                SamplingWindowBatch batch; long connection;
                lock (_gate)
                { if (_disposed || _sampleBatches.Count == 0) return; batch = _sampleBatches.Dequeue(); connection = _connection; }
                try
                {
                    string json = MessageSerializer.SerializeBody(batch);
                    if (!string.IsNullOrEmpty(json) && IsCurrentConnection(connection)) _client.SendPacket((Command)PerformanceProtocol.SamplingBatch, 0, json);
                }
                catch { /* The desktop will time out a missing final batch; never kill the sender. */ }
            }
        }
        public static void HandleStack(MessagePacket packet, RemoteInspectorClient client)
        {
            // Deliberately not dispatched to UnityMain: it may be blocked.
            string stack, error; long age;
            bool success = Performance.TryGetUnityMainStack(out stack, out age, out error);
            var response = new MainStackResponse { success = success, stack = stack, ageMs = age, error = error,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), buildId = Performance.BuildId ?? _buildId, architecture = _architecture };
            client.SendPacket((Command)PerformanceProtocol.MainStackResponse, packet.sequence, MessageSerializer.SerializeBody(response));
        }
        public void Dispose()
        {
            _sampling?.Dispose();
            _forwardRegistration.Dispose();
            _client.OnConnectionStateChanged -= OnConnection;
            lock (_gate) { if (_disposed) return; _disposed = true; _pending.Clear(); _wake.Set(); }
            // Do not join a network worker from UnityMain; disconnect closes its socket.
        }
    

void IPerformanceForwarder.OnJank(JankEvent snapshot)
        {
            OnJank(snapshot);
        }
}
}
