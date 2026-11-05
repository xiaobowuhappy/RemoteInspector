using System;
using System.Collections.Generic;
using System.Threading;

namespace Coplaydev.Performance
{
    internal sealed class BackgroundEventWriter : IDisposable
    {
        private readonly string _directory;
        private readonly int _maxBytes;
        private readonly string _platform, _version;
        private readonly PerformanceDiagnostics _diagnostics;
        private readonly List<JankEvent> _pending = new List<JankEvent>();
        private readonly object _gate = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _thread;
        private readonly int _capacity;
        private bool _running = true;
        private int _disposed;

        public BackgroundEventWriter(string directory, int maxBytes, PerformanceDiagnostics diagnostics, string platform, string version, int capacity = 32)
        {
            _directory = directory;
            _platform = platform;
            _version = version;
            _maxBytes = maxBytes;
            _diagnostics = diagnostics;
            _capacity = Math.Max(4, capacity);
            _thread = new Thread(Run) { IsBackground = true, Name = "PerformanceEventWriter", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public bool TryEnqueue(JankEvent value)
        {
            if (value == null) return false;
            lock (_gate)
            {
                if (!_running) return false;
                var index = -1;
                if (!string.IsNullOrEmpty(value.eventId))
                    for (var i = 0; i < _pending.Count; i++)
                        if (_pending[i].eventId == value.eventId) { index = i; break; }
                if (index >= 0)
                {
                    // A late ongoing snapshot must never replace a final snapshot.
                    if (_pending[index].status != "ongoing" && value.status == "ongoing") return true;
                    _pending[index] = value;
                }
                else
                {
                    if (_pending.Count >= _capacity)
                    {
                        if (value.status == "ongoing") { _diagnostics.Dropped(); return false; }
                        var victim = _pending.FindIndex(item => item.status == "ongoing");
                        _pending.RemoveAt(victim < 0 ? 0 : victim);
                        _diagnostics.Dropped();
                    }
                    _pending.Add(value);
                }
                _wake.Set();
                return true;
            }
        }

        private void Run()
        {
            PersistentEventQueue storage = null;
            var batch = new List<JankEvent>(_capacity);
            while (true)
            {
                _wake.WaitOne();
                lock (_gate)
                {
                    batch.AddRange(_pending);
                    _pending.Clear();
                    if (!_running && batch.Count == 0) break;
                }
                try
                {
                    // Directory creation, recovery, serialization and all file I/O stay on this thread.
                    if (storage == null) storage = new PersistentEventQueue(_directory, _maxBytes, _platform, _version);
                    storage.WriteEvents(batch);
                    for (var i = 0; i < batch.Count; i++) _diagnostics.Queued();
                }
                catch { _diagnostics.QueueWriteFailed(); }
                finally { batch.Clear(); }
                lock (_gate) { if (!_running && _pending.Count == 0) break; }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_gate) { _running = false; _wake.Set(); }
            if (!_thread.IsAlive || _thread.Join(5000)) _wake.Dispose();
        }
    }
}
