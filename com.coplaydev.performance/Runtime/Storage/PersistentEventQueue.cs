using System.IO;
using UnityEngine;

namespace Coplaydev.Performance
{
    internal sealed class PersistentEventQueue
    {
        private readonly string _directory;
        private readonly object _gate = new object();
        private readonly CompactJankLog _compactLog;

        public PersistentEventQueue(string directory, int maxBytes, string platform, string version)
        {
            _directory = directory;
            Directory.CreateDirectory(_directory);
            _compactLog = new CompactJankLog(_directory, maxBytes, platform, version);
            RecoverTemporaryFiles();
        }

        public void WriteEvent(JankEvent value)
        {
            lock (_gate)
            {
                _compactLog.Append(value);
            }
        }

        public void WriteEvents(System.Collections.Generic.List<JankEvent> values)
        {
            lock (_gate) _compactLog.AppendBatch(values);
        }

        private void RecoverTemporaryFiles()
        {
            foreach (var temporary in Directory.GetFiles(_directory, "*.tmp"))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                    try { File.Delete(temporary); } catch { }
                }
            }
        }
    }
}
