using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Coplaydev.Performance
{
    public static partial class Performance
    {
        /// <summary>Read-only snapshot notification on the watchdog/caller thread. Handlers must not block or use Unity APIs.</summary>
        public static event Action<JankEvent> JankSnapshot;
        public static string BuildId { get; private set; }
        /// <summary>Register a generic receiver. Dispose the returned handle to release this registration.</summary>
        public static IDisposable RegisterForwarder(IPerformanceForwarder receiver) => PerformanceForwarding.Register(receiver);
        /// <summary>Remove all registrations of this receiver. In-flight callbacks may finish.</summary>
        public static void UnregisterForwarder(IPerformanceForwarder receiver) => PerformanceForwarding.Unregister(receiver);
        internal static void PublishJank(JankEvent value)
        {
            PerformanceForwarding.Publish(value);
            var handlers = JankSnapshot;
            if (handlers == null) return;
            foreach (Action<JankEvent> handler in handlers.GetInvocationList())
                try { handler(value); } catch { /* Isolate subscribers from the sampler and each other. */ }
        }
        private static PerformanceRuntime _runtime;
        private static int _initialized;
        private static int _unityMainThreadId;
        private static IStackSampler _customStackSampler;
        private static NativeThreadStackRegistration _mainThreadCapture;
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
        private static NativeStackSamplerBridge _mobileMainCapture;
#endif
        private static long _lastDirectCaptureMs;
        private static int _directCaptureBusy;
        // Published as a single immutable reference so readers observe matching stack/time.
        private sealed class DirectStackSnapshot
        {
            public readonly string stack;
            public readonly long sampledAtMs;
            public DirectStackSnapshot(string value, long time) { stack = value; sampledAtMs = time; }
        }
        private static DirectStackSnapshot _directSnapshot;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            StopUnityMainSampling();
            PlayerLoopInstaller.Uninstall();
            _runtime?.Dispose();
            _runtime = null;
            System.Threading.Interlocked.Exchange(ref _mainThreadCapture, null)?.Dispose();
            #if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            _mobileMainCapture?.Dispose(); _mobileMainCapture = null;
#endif
            _lastDirectCaptureMs = 0;
            System.Threading.Volatile.Write(ref _directSnapshot, null);
            _customStackSampler = null;
            _initialized = 0;
            BuildId = null;
            _unityMainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInitialize() => Initialize(null);

        public static void Initialize(PerformanceConfig overrideConfig)
        {
            if (System.Threading.Interlocked.Exchange(ref _initialized, 1) != 0) return;
            PerformanceRuntime runtime = null;
            try
            {
                if (_unityMainThreadId != 0 && System.Threading.Thread.CurrentThread.ManagedThreadId != _unityMainThreadId)
                    throw new InvalidOperationException("Performance SDK must be initialized on UnityMain.");
                _unityMainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                // Register the real OS thread now; remote callbacks must never register themselves as UnityMain.
                if (_mainThreadCapture == null)
                    _mainThreadCapture = ThreadStackCapture.RegisterCurrentThread("UnityMain");
                #if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
                if (_mobileMainCapture == null)
                {
                    _mobileMainCapture = new NativeStackSamplerBridge();
                    _mobileMainCapture.RegisterMainThread();
                }
#endif
                var config = overrideConfig ?? PerformanceConfig.Load();
                config.Sanitize();
                BuildId = config.buildId;
                if (!config.enabled || !IsSampled(config.sessionSampleRate)) return;
                _ = PerformanceHost.Instance;
                runtime = new PerformanceRuntime(config, _customStackSampler);
                _runtime = runtime;
                runtime.Start();
                PlayerLoopInstaller.Install(EarlyTick, LateTick);
            }
            catch (Exception exception)
            {
                StopUnityMainSampling();
            PlayerLoopInstaller.Uninstall();
                runtime?.Dispose();
                Debug.LogWarning("[Performance] initialization failed: " + exception.Message);
                _runtime = null;
                _initialized = 0;
            }
        }

        public static void SetPage(string page) => _runtime?.SetPage(page);
        /// <summary>Call on UnityMain. Disable before Loading, enable when gameplay is ready.</summary>
        public static void SetCollectionEnabled(bool enabled) => _runtime?.SetCollectionEnabled(enabled);
        public static void MarkExpectedTransient(int durationMs = 1000) => _runtime?.MarkExpectedTransient(durationMs);
        public static PerformanceDiagnosticsSnapshot GetDiagnostics() => _runtime?.Diagnostics ?? new PerformanceDiagnosticsSnapshot();

        /// <summary>
        /// Capture UnityMain immediately from a remote/debug worker thread (Windows x64 / Android arm64 / iOS arm64).
        /// Does not create a jank event or depend on the collection switch. Limited to one attempt/second.
        /// </summary>
        public static bool TryCaptureUnityMainStack(out StackSample sample, out string error)
        {
            sample = null;
            error = null;
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == _unityMainThreadId)
            { error = "MustCallFromWorkerThread"; return false; }
            var registration = System.Threading.Volatile.Read(ref _mainThreadCapture);
            bool available = registration != null && registration.IsValid;
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            var mobile = _mobileMainCapture;
            available = mobile != null && mobile.IsSupported;
#endif
            if (!available)
            { error = "UnsupportedOrNotInitialized"; return false; }
            if (System.Threading.Interlocked.CompareExchange(ref _directCaptureBusy, 1, 0) != 0)
            { error = "CaptureBusy"; return false; }
            try
            {
                var now = MonotonicClock.Milliseconds;
                var previous = System.Threading.Interlocked.Read(ref _lastDirectCaptureMs);
                if (previous != 0 && now - previous < 1000)
                { error = "RateLimited"; return false; }
                System.Threading.Interlocked.Exchange(ref _lastDirectCaptureMs, now);
                #if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
                bool captured = mobile.TryCaptureMainThread(out sample);
#else
                bool captured = registration.TryCapture(out sample);
#endif
                if (captured)
                {
                    System.Threading.Volatile.Write(ref _directSnapshot,
                        new DirectStackSnapshot(sample.stack, sample.monotonicMs));
                    return true;
                }
                error = "NativeCaptureFailed";
                return false;
            }
            finally { System.Threading.Volatile.Write(ref _directCaptureBusy, 0); }
        }

        /// <summary>
        /// High-frequency remote polling: reuse the latest string, capture at most once/second.
        /// Call from a worker thread. Returns cache age so callers can distinguish stale evidence.
        /// Cache hits allocate no managed objects. No Task or automatic background jobs are created.
        /// </summary>
        public static bool TryGetUnityMainStack(out string stack, out long ageMs, out string error)
        {
            stack = null;
            ageMs = -1;
            error = null;
            var now = MonotonicClock.Milliseconds;
            var snapshot = System.Threading.Volatile.Read(ref _directSnapshot);
            var previousAttempt = System.Threading.Interlocked.Read(ref _lastDirectCaptureMs);
            if ((snapshot == null || now - snapshot.sampledAtMs >= 1000) &&
                (previousAttempt == 0 || now - previousAttempt >= 1000))
            {
                StackSample ignored;
                TryCaptureUnityMainStack(out ignored, out error);
                snapshot = System.Threading.Volatile.Read(ref _directSnapshot);
            }
            if (snapshot == null)
            {
                if (error == null) error = "NoSnapshotAvailable";
                return false;
            }
            stack = snapshot.stack;
            ageMs = Math.Max(0, MonotonicClock.Milliseconds - snapshot.sampledAtMs);
            return true;
        }

        /// <summary>Await asynchronously; never use Wait/Result on UnityMain, which would capture that wait.</summary>
        public static System.Threading.Tasks.Task<StackSample> CaptureUnityMainStackAsync()
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                StackSample sample;
                string error;
                if (!TryCaptureUnityMainStack(out sample, out error))
                    throw new InvalidOperationException(error);
                return sample;
            });
        }

        // Register before Unity's BeforeSceneLoad initialization (for example from a
        // SubsystemRegistration hook) when integrating a proprietary native sampler.
        public static bool RegisterStackSampler(IStackSampler sampler)
        {
            if (sampler == null || _initialized != 0) return false;
            _customStackSampler = sampler;
            return true;
        }

        internal static void SetAppState(AppRunState state) => _runtime?.SetAppState(state);
        internal static void EarlyTick() => _runtime?.EarlyTick();
        internal static void LateTick() => _runtime?.LateTick();

        internal static void Shutdown()
        {
            StopUnityMainSampling();
            PlayerLoopInstaller.Uninstall();
            _runtime?.Dispose();
            _runtime = null;
            System.Threading.Interlocked.Exchange(ref _mainThreadCapture, null)?.Dispose();
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            _mobileMainCapture?.Dispose(); _mobileMainCapture = null;
#endif
            System.Threading.Volatile.Write(ref _directSnapshot, null);
        }

        private static bool IsSampled(float rate)
        {
            if (rate >= 1f) return true;
            if (rate <= 0f) return false;
            var key = SystemInfo.deviceUniqueIdentifier + "|" + Application.version;
            unchecked
            {
                uint hash = 2166136261;
                for (var i = 0; i < key.Length; i++) hash = (hash ^ key[i]) * 16777619;
                return hash % 10000 < rate * 10000;
            }
        }
    }
}

namespace Coplaydev.Performance
{
    public sealed class StackSamplingOptions
    {
        public int DurationMs = 5000;
        public int IntervalMs = 50;
        public int MaxSamples = 400;
    }
    public sealed class StackSamplingBatch
    {
        public string SessionId;
        public bool Completed;
        public string Error;
        public int Attempts, Captured, Dropped;
        public StackSample[] Samples;
        public long StartedUtcMs, StartedMonotonicMs;
    }
    /// <summary>Read-only bounded batches on a sampling worker. Copy/enqueue only; never send network or call Unity APIs.</summary>
    public interface IStackSamplingReceiver { void OnStackSamples(StackSamplingBatch batch); }

    public static partial class Performance
    {
        private static SamplingSession _samplingSession;
        private static readonly object SamplingGate = new object();
        public static IDisposable StartUnityMainSampling(StackSamplingOptions options, IStackSamplingReceiver receiver, string sessionId = null)
        {
            if (options == null || receiver == null) throw new ArgumentNullException(options == null ? nameof(options) : nameof(receiver));
            bool supported = _mainThreadCapture != null && _mainThreadCapture.IsValid;
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            supported = _mobileMainCapture != null && _mobileMainCapture.IsSupported;
#endif
            if (!supported) throw new InvalidOperationException("UnsupportedOrNotInitialized");
            lock (SamplingGate)
            {
                if (_samplingSession != null && !_samplingSession.Stopped) throw new InvalidOperationException("SamplingSessionBusy");
                var session = new SamplingSession(options, receiver, sessionId ?? Guid.NewGuid().ToString("N"), CaptureSamplingStack);
                _samplingSession = session; session.Start(); return session;
            }
        }
        public static void StopUnityMainSampling()
        { lock (SamplingGate) { _samplingSession?.Dispose(); } }
        private static StackSample CaptureSamplingStack()
        {
            if (Thread.CurrentThread.ManagedThreadId == _unityMainThreadId) return null;
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            StackSample sample;
            return _mobileMainCapture != null && _mobileMainCapture.TryCaptureMainThread(out sample) ? sample : null;
#else
            StackSample sample;
            var registration = Volatile.Read(ref _mainThreadCapture);
            return registration != null && registration.TryCapture(out sample) ? sample : null;
#endif
        }
        internal sealed class SamplingSession : IDisposable
        {
            private readonly IStackSamplingReceiver receiver;
            private readonly Func<StackSample> capture;
            private readonly string id;
            private readonly int duration, interval, maximum;
            private readonly ManualResetEvent cancel = new ManualResetEvent(false);
            private readonly object cancelGate = new object();
            private bool released;
            private int cancelled;
            private readonly Thread worker;
            internal volatile bool Stopped;
            internal SamplingSession(StackSamplingOptions options, IStackSamplingReceiver receiver, string id, Func<StackSample> capture)
            {
                this.receiver = receiver; this.id = id; this.capture = capture;
                duration = Math.Max(100, Math.Min(20000, options.DurationMs));
                interval = Math.Max(20, Math.Min(1000, options.IntervalMs));
                maximum = Math.Max(1, Math.Min(400, options.MaxSamples));
                worker = new Thread(RunSampling) { IsBackground = true, Name = "Performance.BusinessSampling" };
            }
            internal void Start() => worker.Start();
            public void Dispose()
            {
                Interlocked.Exchange(ref cancelled, 1);
                lock (cancelGate) { if (!released) cancel.Set(); }
            }
            private void RunSampling()
            {
                int attempts = 0, captured = 0, dropped = 0;
                long start = MonotonicClock.Milliseconds, utc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), lastFlush = start;
                var pending = new List<StackSample>(8);
                string error = null;
                try
                {
                    NativeCaptureBudget.SamplingWindow = true;
                    while (Volatile.Read(ref cancelled) == 0 && MonotonicClock.Milliseconds - start < duration && attempts < maximum)
                    {
                        attempts++;
                        StackSample sample = capture();
                        if (sample != null && !string.IsNullOrEmpty(sample.stack) && sample.stack.Length <= 65536) { pending.Add(sample); captured++; } else dropped++;
                        long now = MonotonicClock.Milliseconds;
                        if (pending.Count >= 8 || now - lastFlush >= 250)
                        { Publish(pending, false, null, attempts, captured, dropped, utc, start); lastFlush = now; }
                        long remaining = duration - (MonotonicClock.Milliseconds - start);
                        if (remaining <= 0 || cancel.WaitOne((int)Math.Min(interval, remaining))) break;
                    }
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                finally
                {
                    NativeCaptureBudget.SamplingWindow = false;
                    Publish(pending, true, error ?? (Volatile.Read(ref cancelled) != 0 ? "Cancelled" : null), attempts, captured, dropped, utc, start);
                    Stopped = true;
                    lock (SamplingGate) { if (ReferenceEquals(_samplingSession, this)) _samplingSession = null; }
                    lock (cancelGate) { released = true; cancel.Dispose(); }
                }
            }
            private void Publish(List<StackSample> pending, bool completed, string error, int attempts, int captured, int dropped, long utc, long start)
            {
                var batch = new StackSamplingBatch { SessionId = id, Completed = completed, Error = error, Attempts = attempts, Captured = captured, Dropped = dropped, Samples = pending.ToArray(), StartedUtcMs = utc, StartedMonotonicMs = start };
                pending.Clear();
                try { receiver.OnStackSamples(batch); } catch { /* Consumer cannot stop capture or cleanup. */ }
            }
        }
    }
}
