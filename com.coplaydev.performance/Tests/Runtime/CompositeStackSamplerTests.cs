using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;

namespace Coplaydev.Performance.Tests
{
    public sealed class CompositeStackSamplerTests
    {
        [Test]
        public void ThrowingSampler_IsDisabledWithoutBlockingHealthySampler()
        {
            var healthy = new CountingSampler();
            var sampler = new CompositeStackSampler(new ThrowingSampler(), healthy);

            sampler.RegisterMainThread();
            sampler.BeginStall(1, 10);
            sampler.Sample(1, 20);
            var result = sampler.EndStall(1, 30);

            Assert.That(healthy.Calls, Is.EqualTo(4));
            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].stack, Is.EqualTo("healthy"));
        }

        [Test]
        public void WatchdogDispose_WakesLongPollImmediately()
        {
            var config = new PerformanceConfig { watchdogPollMs = 1000 };
            config.Sanitize();
            var watchdog = new MainThreadWatchdog(config, new CompositeStackSampler(), (_, __, ___, ____) => { });
            watchdog.Start();
            var timer = Stopwatch.StartNew();

            watchdog.Dispose();

            timer.Stop();
            Assert.That(timer.ElapsedMilliseconds, Is.LessThan(500));
        }

        private sealed class ThrowingSampler : IStackSampler
        {
            public string Name => "throwing";
            public bool IsSupported => true;
            public void RegisterMainThread() => throw new InvalidOperationException();
            public void BeginStall(long generation, long monotonicMs) => throw new InvalidOperationException();
            public void Sample(long generation, long monotonicMs) => throw new InvalidOperationException();
            public List<StackSample> EndStall(long generation, long monotonicMs) => throw new InvalidOperationException();
        }

        private sealed class CountingSampler : IStackSampler
        {
            public int Calls;
            public string Name => "healthy";
            public bool IsSupported => true;
            public void RegisterMainThread() => Calls++;
            public void BeginStall(long generation, long monotonicMs) => Calls++;
            public void Sample(long generation, long monotonicMs) => Calls++;
            public List<StackSample> EndStall(long generation, long monotonicMs)
            {
                Calls++;
                return new List<StackSample> { new StackSample { generation = generation, stack = "healthy" } };
            }
        }
    }
}
