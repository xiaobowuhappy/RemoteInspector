using System;
using System.Collections.Generic;
using UnityEngine;

namespace Coplaydev.Performance
{
    internal sealed class PerformanceRuntime : IDisposable
    {
        private readonly PerformanceConfig _config;
        private readonly BackgroundEventWriter _writer;
        private readonly PerformanceDiagnostics _diagnostics;
        private readonly IStackSampler _stackSampler;
        private readonly MainThreadWatchdog _watchdog;
        private long _frameStartMs;
        private long _expectedTransientStartMs;
        private long _expectedTransientUntilMs;
        private bool _disposed;
        private int _resourcesReleased;

        private void ReleaseResources()
        {
            if (System.Threading.Interlocked.Exchange(ref _resourcesReleased, 1) != 0) return;
            try { (_stackSampler as IDisposable)?.Dispose(); }
            finally { _writer.Dispose(); }
        }
        private bool _collectionEnabled = true;
        private readonly string _architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();

        public void SetCollectionEnabled(bool enabled)
        {
            _watchdog.SetCollectionEnabled(enabled);
            _collectionEnabled = enabled;
            _frameStartMs = MonotonicClock.Milliseconds;
        }

        internal PerformanceDiagnosticsSnapshot Diagnostics => _diagnostics.Snapshot();

        public PerformanceRuntime(PerformanceConfig config, IStackSampler customStackSampler)
        {
            _config = config;
            _diagnostics = new PerformanceDiagnostics();
            var directory = System.IO.Path.Combine(Application.persistentDataPath, "performance-events");
            _writer = new BackgroundEventWriter(directory, config.maxQueueBytes, _diagnostics,
                Application.platform.ToString(), Application.version);
            if (config.nativeStackSamplingEnabled)
                _stackSampler = customStackSampler == null
                    ? new CompositeStackSampler(new NativeStackSamplerBridge())
                    : new CompositeStackSampler(new NativeStackSamplerBridge(), customStackSampler);
            else
                _stackSampler = customStackSampler == null
                    ? new CompositeStackSampler()
                    : new CompositeStackSampler(customStackSampler);
            _watchdog = new MainThreadWatchdog(config, _stackSampler, OnWatchdogSignal);
            _watchdog.WorkerStopped = ReleaseResources;
            _watchdog.SnapshotReady = value =>
            {
                value.buildId = config.buildId;
                value.architecture = _architecture;
                _writer.TryEnqueue(value);
                Performance.PublishJank(value);
            };
        }

        public void Start()
        {
            _frameStartMs = MonotonicClock.Milliseconds;
            _stackSampler.RegisterMainThread();
            _watchdog.SetAppState(AppRunState.Foreground);
            _watchdog.Start();
        }

        public void EarlyTick()
        {
            if (_disposed) return;
            _frameStartMs = MonotonicClock.Milliseconds;
            _watchdog.Beat();
        }

        public void LateTick()
        {
            if (_disposed) return;
            var now = MonotonicClock.Milliseconds;
            var frameMs = Math.Max(0, now - _frameStartMs);

            if (_collectionEnabled && !_config.watchdogEnabled && frameMs >= _config.longStallThresholdMs)
            {
                var duration = (int)Math.Min(int.MaxValue, frameMs);
                if (ShouldSuppressExpectedTransient(duration, now)) _diagnostics.ExpectedTransientFiltered();
                else RecordJank(duration, now, null);
            }

            _watchdog.Beat();
        }

        private void RecordJank(int durationMs, long endMs, List<StackSample> nativeStacks)
        {
            if (ShouldSuppressExpectedTransient(durationMs, endMs))
            {
                _diagnostics.ExpectedTransientFiltered();
                return;
            }
            _diagnostics.Jank();
            var stacks = nativeStacks ?? new List<StackSample>();
            stacks = CompactStacks(stacks);
            var value = new JankEvent
            {
                buildId = _config.buildId,
                architecture = _architecture,
                eventId = Guid.NewGuid().ToString("N"),
                captureStatus = "watchdog_disabled",
                utcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - durationMs,
                startMonotonicMs = endMs - durationMs,
                status = "recovered",
                durationMs = durationMs,
                stacks = stacks
            };
            _writer.TryEnqueue(value);
                Performance.PublishJank(value);
        }

        private void OnWatchdogSignal(long monotonicMs, bool active, long generation, List<StackSample> samples)
        {
            if (active) { _diagnostics.WatchdogStall(); _diagnostics.Jank(); }
        }

        internal static List<StackSample> CompactStacks(List<StackSample> samples)
        {
            var compact = new List<StackSample>();
            var byStack = new Dictionary<string, StackSample>();
            for (var i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                if (sample == null || string.IsNullOrEmpty(sample.stack)) continue;
                var weight = Math.Max(1, sample.sampleCount);
                var key = (sample.source ?? "") + "\n" + (sample.thread ?? "") + "\n" + sample.stack;
                StackSample existing;
                if (!byStack.TryGetValue(key, out existing))
                {
                    sample.sampleCount = weight;
                    if (sample.lastMonotonicMs == 0) sample.lastMonotonicMs = sample.monotonicMs;
                    byStack.Add(key, sample);
                    compact.Add(sample);
                    continue;
                }

                var total = existing.sampleCount + weight;
                existing.sampleCostUs = (int)(((long)existing.sampleCostUs * existing.sampleCount +
                                                (long)sample.sampleCostUs * weight) / total);
                existing.sampleCount = total;
                existing.lastMonotonicMs = Math.Max(existing.lastMonotonicMs,
                    sample.lastMonotonicMs == 0 ? sample.monotonicMs : sample.lastMonotonicMs);
                existing.monotonicMs = Math.Min(existing.monotonicMs, sample.monotonicMs);
                existing.quality = Math.Max(existing.quality, sample.quality);
            }
            return compact;
        }

        public void SetPage(string _)
        {
            if (_config.navigationTransientIgnoreMs > 0)
                MarkExpectedTransient(_config.navigationTransientIgnoreMs);
        }

        public void MarkExpectedTransient(int durationMs)
        {
            durationMs = Math.Max(0, Math.Min(_config.freezeMs - 1, durationMs));
            var now = MonotonicClock.Milliseconds;
            if (durationMs <= 0)
            {
                _expectedTransientStartMs = 0;
                _expectedTransientUntilMs = 0;
                _watchdog.SuppressSamplingUntil(0);
                return;
            }
            _expectedTransientStartMs = now;
            _expectedTransientUntilMs = now + durationMs;
            _watchdog.SuppressSamplingUntil(_expectedTransientUntilMs);
        }

        private bool ShouldSuppressExpectedTransient(int durationMs, long endMs)
        {
            if (durationMs >= _config.freezeMs) return false;
            var startMs = endMs - durationMs;
            return startMs >= _expectedTransientStartMs - _config.watchdogPollMs &&
                   startMs <= _expectedTransientUntilMs && endMs >= _expectedTransientStartMs;
        }

        public void SetAppState(AppRunState state)
        {
            _watchdog.SetAppState(state);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _watchdog.SetAppState(AppRunState.Quitting);
            _watchdog.Dispose();
            if (!_watchdog.HasStopped) return; // Never release a sampler still used by the worker.
            ReleaseResources();
        }

    }
}
