using System.Diagnostics;

namespace Coplaydev.Performance
{
    internal static class MonotonicClock
    {
        private static readonly double ToMilliseconds = 1000d / Stopwatch.Frequency;
        public static long Milliseconds => (long)(Stopwatch.GetTimestamp() * ToMilliseconds);
    }
}
