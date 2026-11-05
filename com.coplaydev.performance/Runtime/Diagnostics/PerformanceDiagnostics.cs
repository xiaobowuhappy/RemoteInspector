using System;
using System.Threading;

namespace Coplaydev.Performance
{
    [Serializable]
    public sealed class PerformanceDiagnosticsSnapshot
    {
        public long janksDetected;
        public long eventsQueued;
        public long eventsDropped;
        public long queueWriteFailures;
        public long watchdogStalls;
        public long expectedTransientsFiltered;
    }

    internal sealed class PerformanceDiagnostics
    {
        private long _janksDetected, _eventsQueued, _eventsDropped;
        private long _queueWriteFailures, _watchdogStalls;
        private long _expectedTransientsFiltered;

        public void Jank() => Interlocked.Increment(ref _janksDetected);
        public void Queued() => Interlocked.Increment(ref _eventsQueued);
        public void Dropped() => Interlocked.Increment(ref _eventsDropped);
        public void QueueWriteFailed() => Interlocked.Increment(ref _queueWriteFailures);
        public void WatchdogStall() => Interlocked.Increment(ref _watchdogStalls);
        public void ExpectedTransientFiltered() => Interlocked.Increment(ref _expectedTransientsFiltered);

        public PerformanceDiagnosticsSnapshot Snapshot() => new PerformanceDiagnosticsSnapshot
        {
            janksDetected = Interlocked.Read(ref _janksDetected),
            eventsQueued = Interlocked.Read(ref _eventsQueued),
            eventsDropped = Interlocked.Read(ref _eventsDropped),
            queueWriteFailures = Interlocked.Read(ref _queueWriteFailures),
            watchdogStalls = Interlocked.Read(ref _watchdogStalls),
            expectedTransientsFiltered = Interlocked.Read(ref _expectedTransientsFiltered)
        };
    }
}
