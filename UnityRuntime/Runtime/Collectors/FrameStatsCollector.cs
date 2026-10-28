// ============================================================================
// FrameStatsCollector.cs
// 帧率统计采集器 - 每帧采集FPS，支持每秒汇总发送
// Runtime层 - 数据采集
// ============================================================================
 
using System;
using System.Collections.Generic;
using UnityEngine;

namespace RemoteInspector.Runtime.Collectors
{
    /// <summary>
    /// 帧率统计采集器
    /// 每帧采集FPS数据，按配置频率汇总发送
    /// </summary>
    public class FrameStatsCollector
    {
        [Serializable]
        public class FrameSample
        {
            public float frameTime;
            public long frameIndex;
            public long timestamp;
        }

        [Serializable]
        public class FrameStatsData
        {
            public ushort sequence;
            public float fps;
            public float avgFrameTime; // ms
            public float minFrameTime;  // ms
            public float maxFrameTime;  // ms
            public int sampleCount;
            public long timestamp;
            public string unityVersion;
            public string platform;
            public float estimatedTextureMemoryMB;
            public int gameObjectCount;
            public int vertexCount;
        }

        // 样本缓冲区（1秒的数据）
        private readonly List<FrameSample> _samples = new List<FrameSample>();
        private long _frameIndex = 0;
        private float _lastSampleTime = 0f;
        private const float SAMPLE_INTERVAL = 1f; // 每1秒汇总发送一次

        // 最新统计数据（供编辑器查询）
        private FrameStatsData _latestStats;

        // 纹理内存估算缓存（FindObjectsOfTypeAll 开销大，5秒缓存一次）
        private float _cachedTextureMemoryMB = -1f;
        private long _textureMemoryCacheTime;
        private const long TEXTURE_MEMORY_CACHE_LIFETIME_MS = 5000;

        /// <summary>
        /// 每帧调用，采集数据
        /// </summary>
        public void Update()
        {
            _frameIndex++;
            float now = Time.realtimeSinceStartup;

            _samples.Add(new FrameSample
            {
                frameTime = Time.deltaTime,
                frameIndex = _frameIndex,
                timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            });

            // 每1秒汇总一次
            if (now - _lastSampleTime >= SAMPLE_INTERVAL)
            {
                _lastSampleTime = now;
                _latestStats = CalculateStats(_samples);
                _samples.Clear();
            }
        }

        /// <summary>
        /// 获取最新统计（每秒更新一次）
        /// </summary>
        public FrameStatsData GetLatestStats()
        {
            return _latestStats ?? CreateEmptyStats();
        }

        private FrameStatsData CalculateStats(List<FrameSample> samples)
        {
            var stats = new FrameStatsData
            {
                sequence = 0,
                timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(),
                sampleCount = samples.Count,
                gameObjectCount = -1, // 暂不采集，消耗大
                vertexCount = -1
            };

            if (samples.Count == 0)
            {
                stats.fps = 0;
                stats.avgFrameTime = 0;
                stats.minFrameTime = 0;
                stats.maxFrameTime = 0;
                return stats;
            }

            float totalMs = 0f;
            float minMs = float.MaxValue;
            float maxMs = 0f;

            foreach (var s in samples)
            {
                float ms = s.frameTime * 1000f;
                totalMs += ms;
                if (ms < minMs) minMs = ms;
                if (ms > maxMs) maxMs = ms;
            }

            stats.avgFrameTime = totalMs / samples.Count;
            stats.minFrameTime = minMs;
            stats.maxFrameTime = maxMs;
            stats.fps = stats.avgFrameTime > 0 ? 1000f / stats.avgFrameTime : 0f;

            // 估算纹理内存
            stats.estimatedTextureMemoryMB = EstimateTextureMemory();

            return stats;
        }

        private float EstimateTextureMemory()
        {
            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            // 缓存有效则直接返回
            if (_cachedTextureMemoryMB >= 0 && now - _textureMemoryCacheTime < TEXTURE_MEMORY_CACHE_LIFETIME_MS)
                return _cachedTextureMemoryMB;

            // 重新计算
            long totalBytes = 0;
            Texture2D[] textures = Resources.FindObjectsOfTypeAll<Texture2D>();
            foreach (Texture2D tex in textures)
            {
                if (tex == null) continue;
                // 估算：宽 * 高 * 字节数(4 for RGBA32)
                int memSize = tex.width * tex.height * 4;
                totalBytes += memSize;
            }

            _cachedTextureMemoryMB = totalBytes / (1024f * 1024f);
            _textureMemoryCacheTime = now;
            return _cachedTextureMemoryMB;
        }

        private FrameStatsData CreateEmptyStats()
        {
            return new FrameStatsData
            {
                sequence = 0,
                fps = 0,
                avgFrameTime = 0,
                minFrameTime = 0,
                maxFrameTime = 0,
                sampleCount = 0,
                timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                unityVersion = Application.unityVersion,
                platform = Application.platform.ToString()
            };
        }
    }
}
