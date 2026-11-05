using System;
using System.IO;
using NUnit.Framework;

namespace Coplaydev.Performance.Tests
{
    public sealed class CompactJankLogTests
    {
        private sealed class BlockingSampler : IStackSampler
        {
            public readonly System.Threading.ManualResetEventSlim Entered = new System.Threading.ManualResetEventSlim();
            public readonly System.Threading.ManualResetEventSlim Release = new System.Threading.ManualResetEventSlim();
            public string Name => "test";
            public bool IsSupported => true;
            public void RegisterMainThread() { }
            public void BeginStall(long generation, long time) { }
            public void Sample(long generation, long time) { Entered.Set(); Release.Wait(3000); }
            public System.Collections.Generic.List<StackSample> EndStall(long generation, long time)
                => new System.Collections.Generic.List<StackSample>();
        }

        [Test]
        public void Disable_DoesNotWaitForSampler_AndPublishesInterrupted()
        {
            var sampler = new BlockingSampler();
            var saved = new System.Threading.ManualResetEventSlim();
            var config = new PerformanceConfig { longStallThresholdMs = 100, watchdogPollMs = 20 };
            var watchdog = new MainThreadWatchdog(config, sampler, (a, b, c, d) => { });
            watchdog.SnapshotReady = value => { if (value.status == "interrupted") saved.Set(); };
            try
            {
                watchdog.Start();
                Assert.That(sampler.Entered.Wait(2000), Is.True);
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                watchdog.SetCollectionEnabled(false);
                Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(500));
                sampler.Release.Set();
                Assert.That(saved.Wait(2000), Is.True);
            }
            finally { sampler.Release.Set(); watchdog.Dispose(); }
        }

        [Test]
        public void Append_WritesCompactSchemaAndUpdatesSameEvent()
        {
            var directory = Path.Combine(Path.GetTempPath(), "performance-compact-log-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var log = new CompactJankLog(directory, 1024 * 1024);
                log.Append(new JankEvent
                {
                    utcMs = 1791958532481,
                    buildId = "ci-42",
                    architecture = "X64",
                    captureStatus = "samples_available",
                    durationMs = 2200,
                    startMonotonicMs = 1000,
                    stacks = new System.Collections.Generic.List<StackSample>
                    {
                        new StackSample
                        {
                            generation = 7,
                            monotonicMs = 1234,
                            source = "native",
                            thread = "UnityMain",
                            stack = "GameAssembly.dll+0x263465\nUnityPlayer.dll+0x78cf1a",
                            sampleCostUs = 80,
                            quality = 100
                        }
                    }
                });
                log.Append(new JankEvent
                {
                    utcMs = 1791958533481,
                    durationMs = 900,
                    stacks = new System.Collections.Generic.List<StackSample>()
                });

                var files = Directory.GetFiles(directory, CompactJankLog.FilePattern);
                Assert.That(files.Length, Is.EqualTo(1));
                StringAssert.IsMatch(@"jank-events-\d{8}-\d{6}-\d{3}\.json$", files[0].Replace('\\', '/'));
                var line = File.ReadAllText(files[0]);
                StringAssert.Contains("\"occurredAtUtc\"", line);
                StringAssert.Contains("\"durationMs\":2200", line);
                StringAssert.Contains("\"buildId\":\"ci-42\"", line);
                StringAssert.Contains("\"architecture\":\"X64\"", line);
                StringAssert.Contains("\"captureStatus\":\"samples_available\"", line);
                StringAssert.Contains("\"status\":\"recovered\"", line);
                StringAssert.Contains("\"firstSeenOffsetMs\":234", line);
                StringAssert.Contains("\"lastSeenOffsetMs\":234", line);
                StringAssert.DoesNotContain("sampleCostUs", line);
                StringAssert.DoesNotContain("generation", line);
                StringAssert.DoesNotContain("source", line);
                StringAssert.Contains("\"sampleCount\":1", line);
                StringAssert.Contains("\"stacks\"", line);
                StringAssert.Contains("GameAssembly.dll+0x263465", line);
                StringAssert.Contains("eventId", line);
                StringAssert.DoesNotContain("context", line);
                StringAssert.DoesNotContain("breadcrumbs", line);
                StringAssert.Contains("\"platform\":\"unknown\"", line);
                StringAssert.Contains("\"version\":\"unknown\"", line);
                StringAssert.EndsWith("]}", line);

                // Simulate an in-memory list falling behind the file (domain reload or another writer).
                var persisted = line.Substring(0, line.Length - 2) +
                                ",{\"occurredAtUtc\":\"external\",\"durationMs\":777,\"stacks\":[]}]}";
                File.WriteAllText(files[0], persisted);
                log.Append(new JankEvent { utcMs = 1791958534481, durationMs = 1000 });
                var appended = File.ReadAllText(files[0]);
                StringAssert.Contains("\"occurredAtUtc\":\"external\"", appended);
                StringAssert.Contains("\"durationMs\":777", appended);
                log.AppendBatch(new System.Collections.Generic.List<JankEvent>
                {
                    new JankEvent { eventId = "stall-1", status = "ongoing", utcMs = 1791958534481, durationMs = 500 },
                    new JankEvent { eventId = "stall-1", status = "interrupted", utcMs = 1791958534481, durationMs = 3000000000L }
                });
                var final = File.ReadAllText(files[0]);
                Assert.That(final.Split(new[] { "\"eventId\":\"stall-1\"" }, StringSplitOptions.None).Length - 1, Is.EqualTo(1));
                StringAssert.Contains("\"status\":\"interrupted\"", final);
                StringAssert.Contains("\"durationMs\":3000000000", final);
                StringAssert.Contains("\"occurredAtUtc\":\"external\"", final);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }
}
