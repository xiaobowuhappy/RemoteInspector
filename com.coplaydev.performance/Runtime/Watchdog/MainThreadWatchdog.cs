using System;
using System.Collections.Generic;
using System.Threading;

namespace Coplaydev.Performance
{
    internal sealed class MainThreadWatchdog : IDisposable
    {
        private readonly PerformanceConfig _config;
        private readonly IStackSampler _sampler;
        private readonly Action<long, bool, long, List<StackSample>> _stallChanged;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private Thread _thread;
        private volatile bool _running;
        private long _heartbeatMs;
        private long _heartbeatSequence;
        private long _heartbeatVersion;
        private long _generation;
        private long _samplingSuppressedUntilMs;
        private int _appState;
        private readonly object _collectionGate = new object();
        private volatile bool _collectionEnabled = true;
        private int _disposed;
        public Action WorkerStopped;
        private long _collectionRevision;
        private bool stalling;
        private long activeGeneration;
        public bool HasStopped => _thread == null || !_thread.IsAlive;

        public void SetCollectionEnabled(bool enabled)
        {
            // Control calls never wait for a sampler or execute Native code on UnityMain.
            if (Volatile.Read(ref _disposed) != 0 || _collectionEnabled == enabled) return;
                _collectionEnabled = enabled;
                Interlocked.Increment(ref _collectionRevision);
                Beat();
            Wake();
        }
        private readonly List<StackSample> _samples = new List<StackSample>();
        private List<StackSample> _cachedStacks;
        private int _cachedSampleCount = -1;
        private int _sampleAttempts, _recoveryRaces;
        private bool _samplingBudgetExhausted;
        private string _eventId;
        private long _stallStartMs, _stallUtcMs, _stallSequence, _lastSnapshotMs;
        public Action<JankEvent> SnapshotReady;

        private void Publish(long now, long generation, string status)
        {
            // 仅有新样本时创建独立快照；时长更新复用只读堆栈，减少持续分配。
            // 相同eventId更新同一条事件；ongoing表示时长仍在增长。
            if (_cachedSampleCount != _samples.Count)
            {
            var copies = new List<StackSample>(_samples.Count);
            foreach (var s in _samples)
                copies.Add(new StackSample { generation = s.generation, monotonicMs = s.monotonicMs,
                    source = s.source, thread = s.thread, stack = s.stack, sampleCostUs = s.sampleCostUs,
                    quality = s.quality, sampleCount = s.sampleCount, lastMonotonicMs = s.lastMonotonicMs });
            _cachedStacks = PerformanceRuntime.CompactStacks(copies);
            _cachedSampleCount = _samples.Count;
            }
            try { SnapshotReady?.Invoke(new JankEvent {
                eventId = _eventId, status = status, utcMs = _stallUtcMs, startMonotonicMs = _stallStartMs,
                durationMs = Math.Max(0, now - _stallStartMs),
                captureStatus = _cachedStacks.Count > 0 ? "samples_available" :
                    !_config.nativeStackSamplingEnabled && !_sampler.IsSupported ? "native_disabled_or_custom_sampler_empty" :
                    !_sampler.IsSupported ? "sampler_unavailable" :
                    _recoveryRaces > 0 ? "recovered_before_sample" :
                    _samplingBudgetExhausted && _sampleAttempts == 0 ? "sampling_budget_exhausted" :
                    _sampleAttempts == 0 ? "sampling_pending" : "native_capture_empty",
                stacks = _cachedStacks }); }
            catch { /* A consumer failure must not terminate the watchdog thread. */ }
            _lastSnapshotMs = now;
        }

        public MainThreadWatchdog(PerformanceConfig config, IStackSampler sampler, Action<long, bool, long, List<StackSample>> stallChanged)
        {
            _config = config;
            _sampler = sampler;
            _stallChanged = stallChanged;
            _heartbeatMs = MonotonicClock.Milliseconds;
        }

        public void Start()
        {
            if (!_config.watchdogEnabled || _running) return;
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "PerformanceWatchdog", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Beat()
        {
            Interlocked.Increment(ref _heartbeatVersion);
            Interlocked.Exchange(ref _heartbeatMs, MonotonicClock.Milliseconds);
            Interlocked.Increment(ref _heartbeatSequence);
            Interlocked.Increment(ref _heartbeatVersion);
        }
        public void SetAppState(AppRunState value)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var previous = (AppRunState)Interlocked.Exchange(ref _appState, (int)value);
            if (previous == value) return;
                Interlocked.Increment(ref _collectionRevision);
                if (value == AppRunState.Foreground) Beat();
            Wake();
        }
        private void Wake() { try { _wake.Set(); } catch (ObjectDisposedException) { } }
        public void SuppressSamplingUntil(long monotonicMs) => Interlocked.Exchange(ref _samplingSuppressedUntilMs, monotonicMs);

        private void Run()
        {
            try { RunCore(); }
            catch (Exception) { /* Fail closed: never let a diagnostic worker crash the game. */ }
            finally
            {
                _running = false;
                try { WorkerStopped?.Invoke(); } catch { }
            }
        }

        private void RunCore()
        {
            // 本线程独立于UnityMain运行；主线程阻塞时仍能检查心跳、采样和提交快照。
            var sampleCount = 0;
            var stallConfirmedMs = 0L;
            var cooldownUntilMs = 0L;
            var sampleHistory = new Queue<long>();
            var offsets = (int[])_config.stackSampleOffsetsMs.Clone();
            var revision = 0L;
            while (_running)
            {
                int waitMs;
                // 前台按固定间隔检测；后台/屏蔽时休眠，由状态变化或退出信号唤醒。
                lock (_collectionGate)
                    waitMs = !_collectionEnabled ||
                        ((AppRunState)Volatile.Read(ref _appState) != AppRunState.Foreground && !stalling)
                        ? Timeout.Infinite : _config.watchdogPollMs;
                _wake.WaitOne(waitMs);
                if (!_running) break;
                lock (_collectionGate)
                {
                    if (revision != _collectionRevision || !_collectionEnabled)
                    {
                        if (stalling)
                        {
                        var endedAt = MonotonicClock.Milliseconds;
                        var tail = _sampler.EndStall(activeGeneration, endedAt);
                        if (tail != null) _samples.AddRange(tail);
                        Publish(endedAt, activeGeneration, "interrupted");
                            stalling = false;
                            _samples.Clear();
                        }
                        revision = _collectionRevision;
                        cooldownUntilMs = 0;
                        if (!_collectionEnabled) continue;
                    }
                    if ((AppRunState)Volatile.Read(ref _appState) != AppRunState.Foreground)
                    {
                        if (stalling)
                        {
                            stalling = false;
                            var stoppedAt = MonotonicClock.Milliseconds;
                            _samples.AddRange(_sampler.EndStall(activeGeneration, stoppedAt));
                            Publish(stoppedAt, activeGeneration, "interrupted");
                            cooldownUntilMs = stoppedAt + _config.stackSamplingCooldownMs;
                            SafeNotify(stoppedAt, false, activeGeneration, null);
                        }
                        continue;
                    }

                    var now = MonotonicClock.Milliseconds;
                    // Read one consistent heartbeat pair. Do not spin if UnityMain was
                    // descheduled halfway through Beat; simply retry on the next poll.
                    var version = Interlocked.Read(ref _heartbeatVersion);
                    if ((version & 1) != 0) continue;
                    var lastHeartbeat = Interlocked.Read(ref _heartbeatMs);
                    var heartbeatSequence = Interlocked.Read(ref _heartbeatSequence);
                    if (version != Interlocked.Read(ref _heartbeatVersion)) continue;
                    var silence = now - lastHeartbeat;
                    if (!stalling && now >= cooldownUntilMs &&
                        now >= Interlocked.Read(ref _samplingSuppressedUntilMs) &&
                        silence >= _config.longStallThresholdMs)
                    {
                        // 心跳停滞达到阈值，建立逻辑卡顿。起点是最后心跳，不是确认时刻。
                        stalling = true;
                        sampleCount = 0;
                        _sampleAttempts = _recoveryRaces = 0; _samplingBudgetExhausted = false;
                        stallConfirmedMs = now;
                        activeGeneration = Interlocked.Increment(ref _generation);
                        _samples.Clear();
                        _stallStartMs = lastHeartbeat;
                        _stallUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - silence;
                        _eventId = _stallUtcMs + "-" + activeGeneration;
                        _cachedSampleCount = -1;
                        _cachedStacks = null;
                        _stallSequence = heartbeatSequence;
                        _lastSnapshotMs = 0;
                        _sampler.BeginStall(activeGeneration, now);
                        SafeNotify(lastHeartbeat, true, activeGeneration, null);
                    }

                    if (stalling && heartbeatSequence != _stallSequence)
                    {
                        // 主线程执行到了新的心跳位置：结束本次连续无心跳区间。
                        stalling = false;
                        cooldownUntilMs = now + _config.stackSamplingCooldownMs;
                        _samples.AddRange(_sampler.EndStall(activeGeneration, now));
                        Publish(lastHeartbeat, activeGeneration, "recovered");
                        SafeNotify(now, false, activeGeneration, null);
                        continue;
                    }

                    while (sampleHistory.Count > 0 && now - sampleHistory.Peek() >= 60000)
                        sampleHistory.Dequeue();

                    _samplingBudgetExhausted = sampleHistory.Count >= _config.maxStackSamplesPerMinute;
                    var allowedCount = Math.Min(_config.maxStackSamples, offsets.Length);
                    if (stalling && sampleCount < allowedCount &&
                        sampleHistory.Count < _config.maxStackSamplesPerMinute &&
                        now - stallConfirmedMs >= offsets[sampleCount])
                    {
                        var heartbeatBefore = Interlocked.Read(ref _heartbeatSequence);
                    // Charge every attempt, including samples racing with recovery.
                    sampleHistory.Enqueue(now);
                    sampleCount++;
                    _sampleAttempts++;
                        _sampler.Sample(activeGeneration, now);
                    // Sample -> EndStall取回本段 -> BeginStall开启下一段。
                    // 这样永久阻塞时也能及时落盘，不必等主线程恢复才拿到Native缓存。
                    // generation保持不变，sampleCount不重置：分段不会绕过单次卡顿采样上限。
                        var segment = _sampler.EndStall(activeGeneration, now);
                        _sampler.BeginStall(activeGeneration, now);
                        if (Interlocked.Read(ref _heartbeatSequence) == heartbeatBefore)
                        {
                            if (segment != null) _samples.AddRange(segment);
                        }
                        else _recoveryRaces++;
                    }
                    var elapsed = now - _stallStartMs;
                    // 永久卡顿逐渐降低写盘频率，避免长时间每5秒重写整个日志。
                    var snapshotInterval = elapsed < 30000 ? 5000 : elapsed < 300000 ? 30000 : 300000;
                    if (stalling && (_lastSnapshotMs == 0 || _cachedSampleCount != _samples.Count || now - _lastSnapshotMs >= snapshotInterval))
                        Publish(now, activeGeneration, "ongoing");
                }
            }

            if (stalling)
            {
                var stoppedAt = MonotonicClock.Milliseconds;
                _samples.AddRange(_sampler.EndStall(activeGeneration, stoppedAt));
                Publish(stoppedAt, activeGeneration, "interrupted");
            }
        }

        private void SafeNotify(long now, bool active, long generation, List<StackSample> samples)
        {
            try { _stallChanged(now, active, generation, samples); } catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _running = false;
            Wake();
            if (_thread != null && _thread.IsAlive && !_thread.Join(2500)) return;
            _thread = null;
            _wake.Dispose();
        }
    }
}
