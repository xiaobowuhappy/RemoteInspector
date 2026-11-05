using System;
using UnityEngine;

namespace Coplaydev.Performance
{
    [Serializable]
    public sealed class PerformanceConfig
    {
        public bool enabled = true;
        public bool watchdogEnabled = true;
        public bool nativeStackSamplingEnabled = true;
        public int longStallThresholdMs = 500;
        public int navigationTransientIgnoreMs = 1000;
        [Range(0f, 1f)] public float sessionSampleRate = 1f;
        public int freezeMs = 1000;
        public int watchdogPollMs = 50;
        public int[] stackSampleOffsetsMs = { 0, 50, 150, 350, 750, 1250 };
        public int maxStackSamples = 6;
        public int maxStackSamplesPerMinute = 20;
        public int stackSamplingCooldownMs = 3000;
        public int maxQueueBytes = 10 * 1024 * 1024;
        public string buildId = "development";

        public void Sanitize()
        {
            longStallThresholdMs = Mathf.Clamp(longStallThresholdMs, 100, 10000);
            freezeMs = Mathf.Max(longStallThresholdMs, freezeMs);
            navigationTransientIgnoreMs = Mathf.Clamp(navigationTransientIgnoreMs, 0, 10000);
            watchdogPollMs = Mathf.Clamp(watchdogPollMs, 20, 1000);
            maxStackSamples = Mathf.Clamp(maxStackSamples, 1, 64);
            maxStackSamplesPerMinute = Mathf.Clamp(maxStackSamplesPerMinute, 1, 600);
            stackSamplingCooldownMs = Mathf.Clamp(stackSamplingCooldownMs, 0, 60000);
            if (stackSampleOffsetsMs == null || stackSampleOffsetsMs.Length == 0)
                stackSampleOffsetsMs = new[] { 0, 50, 150, 350, 750, 1250 };
            Array.Sort(stackSampleOffsetsMs);
            for (var i = 0; i < stackSampleOffsetsMs.Length; i++)
                stackSampleOffsetsMs[i] = Mathf.Clamp(stackSampleOffsetsMs[i], 0, 60000);
            maxQueueBytes = Mathf.Clamp(maxQueueBytes, 128 * 1024, 100 * 1024 * 1024);
        }

        public static PerformanceConfig Load()
        {
            var asset = Resources.Load<TextAsset>("performance-config");
            var config = asset == null ? new PerformanceConfig() : JsonUtility.FromJson<PerformanceConfig>(asset.text);
            if (config == null) config = new PerformanceConfig();
            config.Sanitize();
            return config;
        }
    }
}
