using System;
using System.Collections.Generic;

namespace RemoteInspector.Protocol
{
    public static class PerformanceProtocol
    {
        public const ushort SamplingStart = 0x00B0, SamplingAck = 0x00B1, SamplingBatch = 0x00B2, SamplingStop = 0x00B3;
        public const ushort Jank = 0x0095;
        public const ushort MainStack = 0x0096;
        public const ushort MainStackResponse = 0x0097;
    }
    [Serializable] public class PerformanceStack
    {
        public string thread, stack;
        public int sampleCount;
        public long firstSeenOffsetMs, lastSeenOffsetMs;
    }
    [Serializable] public class PerformanceJank
    {
        public string eventId, buildId, architecture, status, captureStatus;
        public long utcMs, durationMs;
        public List<PerformanceStack> stacks = new List<PerformanceStack>();
    }
    [Serializable] public class MainStackResponse
    {
        public bool success;
        public string stack, error, buildId, architecture;
        public long ageMs, timestamp;
    }
    [Serializable] public class SamplingWindowRequest
    { public string sessionId; public int durationMs = 5000, intervalMs = 50, maxSamples = 400; }
    [Serializable] public class SamplingWindowAck
    { public string sessionId, error; public bool success; }
    [Serializable] public class SamplingWindowSample
    { public string stack, thread; public long timestamp; }
    [Serializable] public class SamplingWindowBatch
    {
        public string sessionId, error, buildId, architecture;
        public bool completed;
        public int attempts, captured, dropped, transportDropped;
        public List<SamplingWindowSample> samples = new List<SamplingWindowSample>();
    }

}
