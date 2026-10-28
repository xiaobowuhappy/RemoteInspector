// ============================================================================
// NodeTreeData.cs
// 节点树数据结构 - 场景GameObject层级结构数据
// Editor层 - 数据定义
// ============================================================================


using System;
using System.Collections.Generic;


namespace RemoteInspector.Core.Models
{
    /// <summary>
    /// 单个节点信息（对应一个GameObject）
    /// </summary>
    [Serializable]
    public class NodeInfo
    {
        /// <summary>GameObject实例ID</summary>
        public int instanceId;

        /// <summary>节点路径（如 "Root/Parent/Child"）</summary>
        public string path;

        /// <summary>节点名称</summary>
        public string name;

        /// <summary>是否激活（self）</summary>
        public bool activeSelf;

        /// <summary>是否在层级中激活</summary>
        public bool activeInHierarchy;

        /// <summary>Tag标签</summary>
        public string tag;

        /// <summary>Layer层级</summary>
        public int layer;

        /// <summary>子节点实例ID列表</summary>
        public List<int> childIds;

        /// <summary>挂载的组件类型名列表</summary>
        public List<string> componentTypes;

        /// <summary>
        /// 快速构造NodeInfo
        /// </summary>


    }

    /// <summary>
    /// 节点树完整响应数据
    /// </summary>
    [Serializable]
    public class NodeTreeResponse
    {
        public ushort sequence;

        /// <summary>根节点列表</summary>
        public List<NodeInfo> rootNodes;

        /// <summary>所有节点的字典（便于编辑器端按ID快速查找）</summary>
        public List<NodeInfo> allNodes;

        /// <summary>场景名称</summary>
        public string sceneName;

        /// <summary>采集时间戳</summary>
        public long timestamp;
    }
}

