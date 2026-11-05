using System;
using System.Collections.Generic;
using System.Threading;

namespace Coplaydev.Performance
{
    /// <summary>
    /// Optional consumer of read-only jank snapshots. Called on the publishing thread.
    /// Copy/enqueue bounded data and return promptly; do not perform network I/O or call Unity APIs here.
    /// The SDK does not know or own the consumer's transport, destination or lifetime.
    /// </summary>
    public interface IPerformanceForwarder
    {
        void OnJank(JankEvent snapshot);
    }

    internal static class PerformanceForwarding
    {
        private sealed class Entry
        {
            internal readonly IPerformanceForwarder Receiver;
            internal int References;
            internal int Active = 1;
            internal Entry(IPerformanceForwarder receiver) { Receiver = receiver; References = 1; }
        }

        private sealed class Registration : IDisposable
        {
            private Entry _entry;
            internal Registration(Entry entry) { _entry = entry; }
            public void Dispose()
            {
                var entry = Interlocked.Exchange(ref _entry, null);
                if (entry == null) return;
                lock (Gate)
                {
                    if (entry.Active == 0 || --entry.References > 0) return;
                    Volatile.Write(ref entry.Active, 0);
                    Entries.Remove(entry);
                    Volatile.Write(ref _snapshot, Entries.ToArray());
                }
            }
        }

        private static readonly object Gate = new object();
        private static readonly List<Entry> Entries = new List<Entry>();
        private static Entry[] _snapshot = new Entry[0];

        internal static IDisposable Register(IPerformanceForwarder receiver)
        {
            if (receiver == null) throw new ArgumentNullException(nameof(receiver));
            lock (Gate)
            {
                foreach (var existing in Entries)
                {
                    if (!ReferenceEquals(existing.Receiver, receiver)) continue;
                    existing.References++;
                    return new Registration(existing);
                }
                var entry = new Entry(receiver);
                Entries.Add(entry);
                Volatile.Write(ref _snapshot, Entries.ToArray());
                return new Registration(entry);
            }
        }

        internal static void Unregister(IPerformanceForwarder receiver)
        {
            if (receiver == null) return;
            lock (Gate)
            {
                for (int i = 0; i < Entries.Count; i++)
                {
                    var entry = Entries[i];
                    if (!ReferenceEquals(entry.Receiver, receiver)) continue;
                    Volatile.Write(ref entry.Active, 0);
                    Entries.RemoveAt(i);
                    Volatile.Write(ref _snapshot, Entries.ToArray());
                    return;
                }
            }
        }

        internal static void Publish(JankEvent value)
        {
            if (value == null) return;
            // Registration uses copy-on-write. Publishing takes no registration lock and allocates no array.
            foreach (var entry in Volatile.Read(ref _snapshot))
            {
                if (Volatile.Read(ref entry.Active) == 0) continue;
                try { entry.Receiver.OnJank(value); }
                catch { /* One consumer must not break sampling or prevent delivery to others. */ }
            }
        }
    }
}
