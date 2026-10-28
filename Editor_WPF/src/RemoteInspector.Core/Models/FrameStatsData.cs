// ============================================================================
// FrameStatsData.cs
// 帧率统计数据结构 - FPS和性能监控数据
// Editor层 - 数据定义
// ============================================================================


using System;
using System.Collections.Generic;


namespace RemoteInspector.Core.Models
{
    /// <summary>
    /// 单帧性能数据
    /// </summary>
    [Serializable]
    public class FrameSample
    {
        /// <summary>帧时间（秒）</summary>
        public float frameTime;

        /// <summary>帧号</summary>
        public long frameIndex;

        /// <summary>时间戳</summary>
        public long timestamp;
    }

    /// <summary>
    /// FPS统计响应数据
    /// </summary>
    [Serializable]
    public class FrameStatsResponse
    {
        public ushort sequence;

        /// <summary>最近1秒的平均FPS</summary>
        public float fps;

        /// <summary>最近1秒的平均帧时间（毫秒）</summary>
        public float avgFrameTime;

        /// <summary>最小帧时间</summary>
        public float minFrameTime;

        /// <summary>最大帧时间</summary>
        public float maxFrameTime;

        /// <summary>采集的帧数</summary>
        public int sampleCount;

        /// <summary>时间戳</summary>
        public long timestamp;

        /// <summary>Unity版本</summary>
        public string unityVersion;

        /// <summary>运行平台</summary>
        public string platform;

        /// <summary>当前渲染的纹理内存估算（MB）</summary>
        public float estimatedTextureMemoryMB;

        /// <summary>当前GameObject总数</summary>
        public int gameObjectCount;

        /// <summary>当前顶点数（如果有网格）</summary>
        public int vertexCount;

        /// <summary>
        /// 构建一个空的统计响应
        /// </summary>



        /// <summary>
        /// 从样本列表计算统计数据
        /// </summary>


    }
}

