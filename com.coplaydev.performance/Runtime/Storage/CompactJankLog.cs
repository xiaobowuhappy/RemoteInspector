using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Coplaydev.Performance
{
    /// <summary>
    /// Session log containing only the fields required for jank diagnosis.
    /// </summary>
    internal sealed class CompactJankLog
    {
        internal const string FilePattern = "jank-events-*.json";
        private static readonly object DiskGate = new object();
        private readonly string _directory;
        private readonly string _path;
        private readonly long _maxBytes;
        private readonly List<CompactJankRecord> _events = new List<CompactJankRecord>();
        private readonly CompactJankFile _envelope;
        private byte[] _utf8Buffer = new byte[4096];
        private long _knownLength = -1;
        private DateTime _knownWriteUtc;

        internal string CurrentPath => _path;

        public CompactJankLog(string directory, int maxQueueBytes, string platform = "unknown", string version = "unknown")
        {
            _envelope = new CompactJankFile { platform = platform, version = version, events = _events };
            _directory = directory;
            _maxBytes = Math.Max(128 * 1024L, Math.Min(5 * 1024 * 1024L, maxQueueBytes / 2L));
            lock (DiskGate)
            {
                var timestamp = DateTime.UtcNow;
                while (true)
                {
                    var candidate = Path.Combine(_directory, "jank-events-" + timestamp.ToString("yyyyMMdd-HHmmss-fff") + ".json");
                    timestamp = timestamp.AddMilliseconds(1);
                    try
                    {
                        var initial = Encoding.UTF8.GetBytes("{\"events\":[]}");
                        using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                        {
                            stream.Write(initial, 0, initial.Length);
                            stream.Flush(true);
                        }
                        _path = candidate;
                        break;
                    }
                    catch (IOException)
                    {
                        // A second runtime reserved the same millisecond; try the next one.
                        if (!File.Exists(candidate)) throw;
                    }
                }
            }
        }

        public string Append(JankEvent value)
        {
            if (value == null) return null;
            return AppendBatch(new List<JankEvent> { value });
        }

        public string AppendBatch(List<JankEvent> values)
        {
            lock (DiskGate)
            {
                ReloadFromDisk();
                foreach (var value in values)
                {
                if (!string.IsNullOrEmpty(value.buildId)) _envelope.buildId = value.buildId;
                if (!string.IsNullOrEmpty(value.architecture)) _envelope.architecture = value.architecture;
                var existingIndex = -1;
                if (!string.IsNullOrEmpty(value.eventId))
                    for (var i = 0; i < _events.Count; i++)
                        if (_events[i].eventId == value.eventId) { existingIndex = i; break; }
                if (existingIndex >= 0)
                {
                    // Reuse the record and formatted timestamp for duration-only updates.
                    var existing = _events[existingIndex];
                    if (existing.status != "ongoing" && !string.IsNullOrEmpty(existing.status) && value.status == "ongoing") continue;
                    existing.status = value.status;
                    existing.durationMs = value.durationMs;
                    existing.captureStatus = value.captureStatus;
                    UpdateStacks(existing, value.stacks, value.startMonotonicMs);
                    continue;
                }
                var record = new CompactJankRecord
                {
                    eventId = string.IsNullOrEmpty(value.eventId) ? Guid.NewGuid().ToString("N") : value.eventId,
                    durationMs = value.durationMs,
                    status = string.IsNullOrEmpty(value.status) ? "recovered" : value.status,
                    captureStatus = value.captureStatus,
                    occurredAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(value.utcMs)
                        .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                    stacks = new List<CompactStack>()
                };
                UpdateStacks(record, value.stacks, value.startMonotonicMs);
                _events.Add(record);
                }

                var bytes = Serialize();
                while (bytes > _maxBytes && _events.Count > 1)
                {
                    _events.RemoveAt(0);
                    bytes = Serialize();
                }

                var temporary = _path + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(_utf8Buffer, 0, bytes);
                    stream.Flush(true);
                }
                try { File.Replace(temporary, _path, null); }
                catch
                {
                    File.Delete(_path);
                    File.Move(temporary, _path);
                }
                var written = new FileInfo(_path);
                _knownLength = written.Length;
                _knownWriteUtc = written.LastWriteTimeUtc;
                TrimOldFiles();
                return _path;
            }
        }

        private void ReloadFromDisk()
        {
            // This writer owns the session file. Reload only if an external writer changed it.
            var info = new FileInfo(_path);
            if (info.Length == _knownLength && info.LastWriteTimeUtc == _knownWriteUtc) return;
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var persisted = JsonUtility.FromJson<CompactJankFile>(json);
            if (persisted == null || persisted.events == null)
                throw new InvalidDataException("Invalid jank event log: " + _path);
            _events.Clear();
            _events.AddRange(persisted.events);
        }

        private static void UpdateStacks(CompactJankRecord record, List<StackSample> samples, long startMs)
        {
            if (record.stacks == null) record.stacks = new List<CompactStack>();
            var count = samples == null ? 0 : samples.Count;
            for (var i = 0; i < count; i++)
            {
                if (i >= record.stacks.Count) record.stacks.Add(new CompactStack());
                record.stacks[i].thread = samples[i].thread;
                record.stacks[i].stack = samples[i].stack;
                record.stacks[i].sampleCount = Math.Max(1, samples[i].sampleCount);
                record.stacks[i].firstSeenOffsetMs = Math.Max(0, samples[i].monotonicMs - startMs);
                record.stacks[i].lastSeenOffsetMs = Math.Max(record.stacks[i].firstSeenOffsetMs,
                    (samples[i].lastMonotonicMs == 0 ? samples[i].monotonicMs : samples[i].lastMonotonicMs) - startMs);
            }
            if (record.stacks.Count > count) record.stacks.RemoveRange(count, record.stacks.Count - count);
        }

        private int Serialize()
        {
            // JsonUtility still allocates its output string; reuse the envelope and UTF8 byte storage.
            var json = JsonUtility.ToJson(_envelope);
            var required = Encoding.UTF8.GetByteCount(json);
            if (_utf8Buffer.Length < required)
                Array.Resize(ref _utf8Buffer, Math.Max(required, _utf8Buffer.Length * 2));
            return Encoding.UTF8.GetBytes(json, 0, json.Length, _utf8Buffer, 0);
        }

        private void TrimOldFiles()
        {
            var files = new System.Collections.Generic.List<FileInfo>();
            long total = 0;
            foreach (var path in Directory.GetFiles(_directory, FilePattern))
            {
                var info = new FileInfo(path);
                files.Add(info);
                total += info.Length;
            }
            files.Sort((left, right) => left.Name.CompareTo(right.Name));
            for (var i = 0; total > _maxBytes && i < files.Count; i++)
            {
                if (string.Equals(files[i].FullName, _path, StringComparison.OrdinalIgnoreCase)) continue;
                total -= files[i].Length;
                files[i].Delete();
            }
        }

        [Serializable]
        private sealed class CompactJankFile
        {
            public string buildId;
            public string architecture;
            public string platform;
            public string version;
            public List<CompactJankRecord> events;
        }

        [Serializable]
        private sealed class CompactJankRecord
        {
            public string captureStatus;
            public string eventId;
            public string occurredAtUtc;
            public long durationMs;
            public string status;
            public List<CompactStack> stacks;
        }

        [Serializable]
        private sealed class CompactStack
        {
            public string thread;
            public int sampleCount;
            public long firstSeenOffsetMs;
            public long lastSeenOffsetMs;
            public string stack;
        }
    }
}
