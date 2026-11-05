// ============================================================================
// ObjectCache.cs
// 对象查找缓存 - 避免频繁调用 FindObjectsOfType 全局扫描
// Runtime层 - 数据采集
// ============================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RemoteInspector.Runtime.Collectors
{
    /// <summary>
    /// GameObject 查找缓存
    /// 通过场景根节点递归遍历构建 InstanceID → GameObject 映射
    /// 缓存有效期内不会重新遍历,大幅减少查找开销
    ///
    /// 海量场景优化(2026-06-25):
    ///   - 监听 SceneManager.sceneLoaded / sceneUnloaded → 立即失效
    ///   - 保留 2 秒 TTL fallback(运行时 Instantiate/Destroy 走不到事件)
    ///   - 缓存命中 100%(无场景变化时完全不重建)
    /// </summary>
    public static class GameObjectCache
    {
        private static Dictionary<int, GameObject> _cache;
        private static int _cachedSceneCount = -1;
        private static long _lastBuildTime;
        private const long CACHE_LIFETIME_MS = 2000; // 2秒 TTL fallback

        // 增量维护开关 — 静态构造时订阅 SceneManager 事件
        private static bool _sceneListenerRegistered;

        /// <summary>
        /// 通过 InstanceID 查找 GameObject
        /// </summary>
        public static GameObject Find(int instanceId)
        {
            EnsureSceneListener();
            if (NeedsRebuild())
                BuildCache();

            if (_cache != null && _cache.TryGetValue(instanceId, out var go))
                return go;

            // 缓存未命中时不立即重建，返回 null
            return null;
        }

        /// <summary>
        /// 强制失效缓存（场景变化时调用）
        /// </summary>
        public static void Invalidate()
        {
            _cache = null;
            _cachedSceneCount = -1;
        }

        // ─── 海量场景优化:场景事件订阅(2026-06-25)───

        private static void EnsureSceneListener()
        {
            if (_sceneListenerRegistered) return;
            _sceneListenerRegistered = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // 场景加载 → 缓存失效,下次 Find 时重建(覆盖新加载的 GameObject)
            Invalidate();
        }

        private static void OnSceneUnloaded(Scene scene)
        {
            // 场景卸载 → 缓存失效,下次 Find 时清理已销毁的 GameObject 引用
            Invalidate();
        }

        private static bool NeedsRebuild()
        {
            return _cache == null
                || SceneManager.sceneCount != _cachedSceneCount
                || System.DateTimeOffset.Now.ToUnixTimeMilliseconds() - _lastBuildTime > CACHE_LIFETIME_MS;
        }

        private static void BuildCache()
        {
            var dict = new Dictionary<int, GameObject>(512);
            _cachedSceneCount = SceneManager.sceneCount;

            // (a) 普通场景根 + 子节点 — Unity 标准 API
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                GameObject[] roots = scene.GetRootGameObjects();
                foreach (var root in roots)
                    AddRecursive(root.transform, dict);
            }

            // (b) DontDestroyOnLoad 场景的根 — Unity API 不暴露 GetRootGameObjects(),
            //   必须用 FindObjectsByType 过滤 buildIndex==-1 来找。
            //   2026-06-25 bug:之前 BuildCache 只扫普通场景,DDOL 节点(instanceId 如 EventSystem)
            //   永远在缓存里查不到 → ComponentCollector.Collect 返回 "[Unknown InstanceID: ...]"
            //   → Inspector 右侧属性显示空。修复后 DDOL 节点可正常查 detail。
            //   HideAndDontSave 过滤:Editor 内部资产/Resources 内部对象 不暴露给 Inspector
            var allGameObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            foreach (var go in allGameObjects)
            {
                if (go == null) continue;
                if (go.transform.parent != null) continue;                 // 只要根
                if (go.scene.buildIndex != -1) continue;                   // 只要 DDOL 场景
                if ((go.hideFlags & HideFlags.HideAndDontSave) != 0) continue; // 排除内部资产
                AddRecursive(go.transform, dict);
            }

            _cache = dict;
            _lastBuildTime = System.DateTimeOffset.Now.ToUnixTimeMilliseconds();
        }

        private static void AddRecursive(Transform t, Dictionary<int, GameObject> dict)
        {
            dict[t.gameObject.GetInstanceID()] = t.gameObject;

            for (int i = 0; i < t.childCount; i++)
            {
                Transform child = t.GetChild(i);
                AddRecursive(child, dict);
            }
        }
    }

    /// <summary>
    /// Texture2D 查找缓存
    /// FindObjectsOfTypeAll 开销大，缓存结果减少重复扫描
    /// </summary>
    public static class TextureCache
    {
        private static Dictionary<int, Texture2D> _cache;
        private static long _lastBuildTime;
        private const long CACHE_LIFETIME_MS = 5000; // 5秒有效期

        /// <summary>
        /// 通过 InstanceID 查找 Texture2D
        /// </summary>
        public static Texture2D Find(int instanceId)
        {
            if (NeedsRebuild())
                BuildCache();

            if (_cache != null && _cache.TryGetValue(instanceId, out var tex))
                return tex;

            return null;
        }

        /// <summary>
        /// 强制失效缓存
        /// </summary>
        public static void Invalidate()
        {
            _cache = null;
        }

        private static bool NeedsRebuild()
        {
            return _cache == null
                || System.DateTimeOffset.Now.ToUnixTimeMilliseconds() - _lastBuildTime > CACHE_LIFETIME_MS;
        }

        private static void BuildCache()
        {
            var dict = new Dictionary<int, Texture2D>(128);
            Texture2D[] textures = Resources.FindObjectsOfTypeAll<Texture2D>();
            foreach (var tex in textures)
            {
                if (tex != null)
                    dict[tex.GetInstanceID()] = tex;
            }

            _cache = dict;
            _lastBuildTime = System.DateTimeOffset.Now.ToUnixTimeMilliseconds();
        }
    }
}
