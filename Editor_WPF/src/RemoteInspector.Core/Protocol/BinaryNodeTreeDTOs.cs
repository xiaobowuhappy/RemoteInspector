// ============================================================================
// BinaryNodeTreeDTOs.cs
// 节点树二进制协议的独立 DTO - 在 Shared 命名空间定义,
// 避免 BinaryNodeTreeCodec (Shared) 依赖 Runtime/Editor 命名空间。
//
// 2026-06-25 海量场景性能优化第一波。
// ============================================================================

using System.Collections.Generic;

namespace RemoteInspector.Protocol
{
    /// <summary>
    /// 节点树二进制响应 DTO(Shared 层 — Runtime 编码 → 网络 → Editor 解码)
    /// 2026-06-25 v2 升级:totalNodes/rootCount 从 ushort 改为 int — 支持 > 65535 节点的海量场景。
    /// </summary>
    public class BinaryNodeTreeDto
    {
        public int totalNodes;
        public int rootCount;
        public int[] rootIndices;              // 指向 allNodes 数组的索引
        public List<string> stringPool;        // 共享字符串池(常用 component name)
        public List<BinaryNodeDto> allNodes;   // 按 allNodes 顺序
    }

    /// <summary>单个节点的二进制 DTO</summary>
    public class BinaryNodeDto
    {
        public int instanceId;
        public string path;                   // 完整路径,可能含 [DDOL] 前缀
        public bool activeSelf;
        public bool activeInHierarchy;
        public bool isDontDestroyOnLoad;      // 标志位冗余存(path 也含 [DDOL] 前缀)
        public string tag;
        public int layer;
        public List<int> childIds;
        public List<string> componentTypes;
    }
}
