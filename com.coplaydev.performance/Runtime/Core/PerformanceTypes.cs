using System;
using System.Collections.Generic;

namespace Coplaydev.Performance
{
    public enum AppRunState { Foreground, Background, Paused, Quitting }

    [Serializable]
    public sealed class StackSample
    {
        public long generation;
        public long monotonicMs;
        public long lastMonotonicMs;
        public string source;
        public string thread;
        public string stack;
        public int sampleCostUs;
        public int quality;
        public int sampleCount = 1;
    }

    [Serializable]
    public sealed class JankEvent
    {
        public string buildId;
        public string architecture;
        public string captureStatus;
        public string eventId;
        public string status;
        public long utcMs;
        public long startMonotonicMs;
        public long durationMs;
        public List<StackSample> stacks = new List<StackSample>();
    }
}
