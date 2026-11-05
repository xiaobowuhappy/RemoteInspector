// ============================================================================
// NodeTreeCollector.cs
// 节点树采集器 - 收集场景中所有GameObject层级结构
// Runtime层 - 数据采集
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RemoteInspector.Runtime.Collectors
{
    /// <summary>
    /// 节点树采集器
    /// 收集场景中的GameObject树状结构
    /// 带缓存机制：场景未变化时返回缓存结果，避免重复遍历
    /// </summary>
    public class NodeTreeCollector
    {
        // 缓存
        private NodeTreeResponse _cachedResponse;
        private bool _isDirty = true;
        private int _lastSceneCount = -1;
        private string _lastSceneName = "";
        [Serializable]
        public class NodeInfo
        {
            public int instanceId;
            public string name;
            public bool activeSelf;
            public bool activeInHierarchy;
            public string tag;
            public int layer;
            public List<int> childIds;
            public List<string> componentTypes;
            public string path;
        }

        [Serializable]
        public class NodeTreeResponse
        {
            public ushort sequence;
            public List<NodeInfo> rootNodes;
            public List<NodeInfo> allNodes;
            public string sceneName;
            public long timestamp;
        }

        /// <summary>
        /// 采集当前场景的节点树
        /// 如果场景未变化则返回缓存结果
        /// </summary>
        public NodeTreeResponse Collect()
        {
            // 检测场景是否变化
            if (!_isDirty && !SceneChanged())
            {
                return _cachedResponse;
            }

            var response = CollectInternal();
            _cachedResponse = response;
            _isDirty = false;
            return response;
        }

        /// <summary>
        /// 标记缓存失效（外部可调用强制刷新）
        /// </summary>
        public void MarkDirty()
        {
            _isDirty = true;
        }

        /// <summary>
        /// 检测场景变化（场景数量或活动场景名改变）
        /// </summary>
        private bool SceneChanged()
        {
            int count = SceneManager.sceneCount;
            string name = SceneManager.GetActiveScene().name;
            if (count != _lastSceneCount || name != _lastSceneName)
            {
                _lastSceneCount = count;
                _lastSceneName = name;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 实际采集逻辑
        /// 覆盖范围:
        ///   1. 所有已加载 Scene 的根对象(SceneManager.GetRootGameObjects)
        ///   2. DontDestroyOnLoad 场景的根对象(Unity API 不直接暴露,需用 FindObjectsByType 过滤)
        ///   3. 排除 HideFlags.HideAndDontSave 的内部对象(Resources/Editor 隐藏资产)
        /// </summary>
        private NodeTreeResponse CollectInternal()
        {
            var response = new NodeTreeResponse
            {
                sequence = 0,
                rootNodes = new List<NodeInfo>(),
                allNodes = new List<NodeInfo>(),
                sceneName = SceneManager.GetActiveScene().name,
                timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };

            var allNodesDict = new Dictionary<int, NodeInfo>();

            // ── 1. 一次性收集所有"根对象":普通场景根 + DontDestroyOnLoad 根 ──
            //   Unity API 不暴露 DontDestroyOnLoad Scene 的 GetRootGameObjects() —
            //   DDOL 对象在 buildIndex=-1 的内部 Scene 里,必须 FindObjectsByType 过滤。
            //   FindObjectsByType 是 2022.2+ 推荐的零 GC 全场景扫描 API(比旧的 FindObjectsOfType 快)。
            var rootGameObjects = new List<GameObject>();

            // (a) 普通场景根
            for (int sceneIdx = 0; sceneIdx < SceneManager.sceneCount; sceneIdx++)
            {
                Scene scene = SceneManager.GetSceneAt(sceneIdx);
                if (!scene.isLoaded) continue;
                rootGameObjects.AddRange(scene.GetRootGameObjects());
            }

            // (b) DontDestroyOnLoad 根 — 通过 transform.parent==null && scene.buildIndex==-1 识别
            //     HideAndDontSave 过滤:Editor 内部资产 / Resources 内部对象 不暴露给 Inspector
            var allGameObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            foreach (var go in allGameObjects)
            {
                if (go == null) continue;
                if (go.transform.parent != null) continue;                // 只要根
                if (go.scene.buildIndex != -1) continue;                  // 只要 DDOL 场景
                if ((go.hideFlags & HideFlags.HideAndDontSave) != 0) continue; // 排除内部资产
                rootGameObjects.Add(go);
            }

            // ── 2. 递归收集所有根 + 同时填充 rootNodes ──
            foreach (var root in rootGameObjects)
            {
                // DDOL 节点的 path 加 [DDOL] 前缀,UI 端可视觉区分(例:"[DDOL] EventSystem/Panel/Button")
                // 普通节点保持原 path(SceneName/RootName/...)
                string rootPath = root.scene.buildIndex == -1
                    ? k_DontDestroyOnLoadPrefix + root.name
                    : root.name;

                CollectNodeRecursive(root.transform, null, response.allNodes, allNodesDict, rootPath);

                if (allNodesDict.TryGetValue(root.GetInstanceID(), out var node))
                {
                    response.rootNodes.Add(node);
                }
            }

            return response;
        }

        /// <summary>DontDestroyOnLoad 节点 path 前缀(与普通场景节点视觉区分)</summary>
        private const string k_DontDestroyOnLoadPrefix = "[DDOL] ";

        private void CollectNodeRecursive(Transform transform, NodeInfo parent,
            List<NodeInfo> allNodes, Dictionary<int, NodeInfo> dict, string parentPath)
        {
            if (transform == null) return;

            GameObject go = transform.gameObject;
            int instanceId = go.GetInstanceID();

            // 避免重复（同一个GameObject可能被多处引用）
            if (dict.ContainsKey(instanceId)) return;

            string nodePath = string.IsNullOrEmpty(parentPath)
                ? go.name
                : parentPath + "/" + go.name;

            var node = new NodeInfo
            {
                instanceId = instanceId,
                name = go.name,
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                tag = go.tag,
                layer = go.layer,
                childIds = new List<int>(),
                componentTypes = new List<string>(),
                path = nodePath
            };

            // 收集子节点ID
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                node.childIds.Add(child.gameObject.GetInstanceID());
            }

            // 收集组件类型
            Component[] components = go.GetComponents<Component>();
            foreach (Component comp in components)
            {
                if (comp != null)
                    node.componentTypes.Add(comp.GetType().Name);
            }

            dict[instanceId] = node;
            allNodes.Add(node);

            // 递归子节点
            for (int i = 0; i < transform.childCount; i++)
            {
                CollectNodeRecursive(transform.GetChild(i), node, allNodes, dict, nodePath);
            }
        }
    }
}
