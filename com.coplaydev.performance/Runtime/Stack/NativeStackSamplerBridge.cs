using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace Coplaydev.Performance
{
    // All built-in capture paths share a non-waiting gate. Never wait for a lock whose
    // owner might be the thread being suspended. Charge attempts, not just successes.
    internal static class NativeCaptureBudget
    {
        private static int _busy;
        private static long _windowMs, _lastMs;
        private static int _attempts;
        [ThreadStatic] internal static bool SamplingWindow;
        private static int _windowAttempts;
        public static bool TryEnter()
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
            var now = MonotonicClock.Milliseconds;
            if (now - _windowMs >= 60000) { _windowMs = now; _attempts = 0; _windowAttempts = 0; }
            if ((_lastMs != 0 && now - _lastMs < 10) || (SamplingWindow ? _windowAttempts >= 600 : _attempts >= 120))
            { Exit(); return false; }
            _lastMs = now;
            if (SamplingWindow) _windowAttempts++; else _attempts++;
            return true;
        }
        public static void Exit() => Volatile.Write(ref _busy, 0);
    }
    // ABI for Android/iOS/Windows native plugins. Failures are contained; no recovery-point
    // managed stack is fabricated when a true blocked-thread sample is unavailable.
    public sealed class NativeStackSamplerBridge : IStackSampler, IDisposable
    {
        private const int ExpectedAbiVersion = 3;
        private bool _available;
        private readonly List<StackSample> _renderSamples = new List<StackSample>();
        private NativeThreadStackRegistration _renderThread;
        private int _renderCaptureFailures;
        public string Name => "native-main-thread";
        public bool IsSupported => _available;

        public NativeStackSamplerBridge()
        {
            try { _available = PerfNative_GetAbiVersion() == ExpectedAbiVersion && PerfNative_IsSupported() == 1; }
            catch { _available = false; }
        }

        public void RegisterMainThread()
        {
            if (!_available) return;
            try { _available = PerfNative_RegisterMainThread() == 1; }
            catch { _available = false; }
        }

        internal bool TryCaptureMainThread(out StackSample sample)
        {
            sample = null;
            if (!_available || !NativeCaptureBudget.TryEnter()) return false;
            try
            {
                var json = Marshal.PtrToStringUTF8(PerfNative_CaptureMainThread(MonotonicClock.Milliseconds));
                if (string.IsNullOrEmpty(json)) return false;
                sample = JsonUtility.FromJson<StackSample>(json);
                return sample != null && !string.IsNullOrEmpty(sample.stack);
            }
            catch { return false; }
            finally { NativeCaptureBudget.Exit(); }
        }

        public void BeginStall(long generation, long monotonicMs)
        {
            // 仅重置本段缓存并绑定卡顿编号；真正抓栈发生在Sample中。
            if (!_available) return;
            _renderSamples.Clear();
            try { PerfNative_BeginStall(generation, monotonicMs); } catch { _available = false; }
        }

        public void Sample(long generation, long monotonicMs)
        {
            if (!_available) return;
            try
            {
                if (NativeCaptureBudget.TryEnter())
                {
                    try { PerfNative_RequestSample(generation, monotonicMs); }
                    finally { NativeCaptureBudget.Exit(); }
                }
                if (_renderThread == null || !_renderThread.IsValid)
                    _renderThread = ThreadStackCapture.RegisterThreadByName("GfxDeviceW", "RenderThread/GfxDeviceW");
                StackSample renderSample;
                if (_renderThread != null && _renderThread.TryCapture(out renderSample))
                {
                    renderSample.generation = generation;
                    renderSample.monotonicMs = monotonicMs;
                    renderSample.source = "native-render-thread";
                    _renderSamples.Add(renderSample);
                    _renderCaptureFailures = 0;
                }
                else if (_renderThread != null && ++_renderCaptureFailures >= 3)
                {
                    _renderThread.Dispose();
                    _renderThread = null;
                    _renderCaptureFailures = 0;
                }
            }
            catch { _available = false; }
        }

        public List<StackSample> EndStall(long generation, long monotonicMs)
        {
            // 从Native取回本段已经抓到的栈。即使主线程仍卡住，也能执行到这里。
            var result = new List<StackSample>();
            if (!_available || generation <= 0) return result;
            try
            {
                var json = Marshal.PtrToStringUTF8(PerfNative_EndStall(generation, monotonicMs));
                if (!string.IsNullOrEmpty(json))
                {
                    var envelope = JsonUtility.FromJson<SampleEnvelope>(json);
                    if (envelope != null && envelope.samples != null) result.AddRange(envelope.samples);
                }
            }
            catch { _available = false; }
            result.AddRange(_renderSamples);
            return result;
        }

        public void Dispose()
        {
            _renderThread?.Dispose();
            _renderThread = null;
        }

        [Serializable] private sealed class SampleEnvelope { public StackSample[] samples; }

#if UNITY_ANDROID && !UNITY_EDITOR
        internal const string Library = "perf_watchdog";
#elif UNITY_IOS && !UNITY_EDITOR
        internal const string Library = "__Internal";
#elif (UNITY_STANDALONE_WIN && !UNITY_EDITOR) || UNITY_EDITOR_WIN
        internal const string Library = "perf_watchdog";
#else
        internal const string Library = "__Internal";
#endif
        [DllImport(Library)] private static extern IntPtr PerfNative_CaptureMainThread(long monotonicMs);
        [DllImport(Library)] private static extern int PerfNative_IsSupported();
        [DllImport(Library)] private static extern int PerfNative_GetAbiVersion();
        [DllImport(Library)] private static extern int PerfNative_RegisterMainThread();
        [DllImport(Library)] private static extern void PerfNative_BeginStall(long generation, long monotonicMs);
        [DllImport(Library)] private static extern void PerfNative_RequestSample(long generation, long monotonicMs);
        [DllImport(Library)] private static extern IntPtr PerfNative_EndStall(long generation, long monotonicMs);
    }

    /// <summary>
    /// A native registration for the thread that called RegisterCurrentThread. Capture must be
    /// requested from another thread. Dispose before the target thread exits whenever possible.
    /// </summary>
    public sealed class NativeThreadStackRegistration : IDisposable
    {
        private long _token;
        public string ThreadName { get; }
        public bool IsValid => Interlocked.Read(ref _token) != 0;

        internal NativeThreadStackRegistration(ulong token, string threadName)
        {
            _token = unchecked((long)token);
            ThreadName = threadName;
        }

        public bool TryCapture(out StackSample sample)
        {
            sample = null;
            var token = unchecked((ulong)Interlocked.Read(ref _token));
            if (token == 0) return false;
            if (!NativeCaptureBudget.TryEnter()) return false;
            try
            {
                var json = Marshal.PtrToStringUTF8(PerfNative_CaptureThread(token, MonotonicClock.Milliseconds));
                if (string.IsNullOrEmpty(json)) return false;
                sample = JsonUtility.FromJson<StackSample>(json);
                if (sample == null || string.IsNullOrEmpty(sample.stack)) return false;
                sample.source = "native-thread";
                sample.thread = ThreadName;
                return true;
            }
            catch { return false; }
            finally { NativeCaptureBudget.Exit(); }
        }

        public void Dispose()
        {
            var token = unchecked((ulong)Interlocked.Exchange(ref _token, 0));
            if (token == 0) return;
            try { PerfNative_UnregisterThread(token); } catch { }
            GC.SuppressFinalize(this);
        }

        ~NativeThreadStackRegistration() { Dispose(); }

        [DllImport(NativeStackSamplerBridge.Library)] private static extern IntPtr PerfNative_CaptureThread(ulong token, long monotonicMs);
        [DllImport(NativeStackSamplerBridge.Library)] private static extern void PerfNative_UnregisterThread(ulong token);
    }

    public static class ThreadStackCapture
    {
        public static bool IsSupported
        {
            get
            {
                return GetExtensionVersion() >= 1;
            }
        }

        public static NativeThreadStackRegistration RegisterCurrentThread(string threadName)
        {
            if (!IsSupported) return null;
            try
            {
                var token = PerfNative_RegisterCurrentThread();
                if (token == 0) return null;
                var safeName = string.IsNullOrEmpty(threadName) ? "unnamed-thread" : threadName;
                if (safeName.Length > 64) safeName = safeName.Substring(0, 64);
                return new NativeThreadStackRegistration(token, safeName);
            }
            catch { return null; }
        }

        public static NativeThreadStackRegistration RegisterThreadByName(string nativeNamePrefix, string displayName = null)
        {
            if (GetExtensionVersion() < 2 || string.IsNullOrEmpty(nativeNamePrefix)) return null;
            try
            {
                var token = PerfNative_RegisterThreadByName(nativeNamePrefix);
                if (token == 0) return null;
                var safeName = string.IsNullOrEmpty(displayName) ? nativeNamePrefix : displayName;
                if (safeName.Length > 64) safeName = safeName.Substring(0, 64);
                return new NativeThreadStackRegistration(token, safeName);
            }
            catch { return null; }
        }

        [DllImport(NativeStackSamplerBridge.Library)] private static extern int PerfNative_GetThreadCaptureVersion();
        [DllImport(NativeStackSamplerBridge.Library)] private static extern ulong PerfNative_RegisterCurrentThread();
        [DllImport(NativeStackSamplerBridge.Library, CharSet = CharSet.Ansi)] private static extern ulong PerfNative_RegisterThreadByName(string threadNamePrefix);

        private static int GetExtensionVersion()
        {
            try { return PerfNative_GetThreadCaptureVersion(); }
            catch { return 0; }
        }
    }
}
