using NUnit.Framework;

namespace Coplaydev.Performance.Tests
{
    public sealed class PerformanceConfigTests
    {
        [Test]
        public void Defaults_FocusOnLongStalls()
        {
            var config = new PerformanceConfig();
            config.Sanitize();

            Assert.That(config.longStallThresholdMs, Is.EqualTo(500));
            Assert.That(config.freezeMs, Is.EqualTo(1000));
            Assert.That(config.maxStackSamples, Is.EqualTo(6));
            Assert.That(config.stackSampleOffsetsMs, Is.EqualTo(new[] { 0, 50, 150, 350, 750, 1250 }));
        }

        [Test]
        public void Sanitize_KeepsFreezeAboveLongStallThreshold()
        {
            var config = new PerformanceConfig { longStallThresholdMs = 800, freezeMs = 300 };
            config.Sanitize();

            Assert.That(config.freezeMs, Is.EqualTo(800));
        }
    }
}
