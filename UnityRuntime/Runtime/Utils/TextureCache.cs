// ============================================================================
// TextureCache.cs
// 纹理缓存管理器 - 编辑器端缓存已接收的纹理，避免重复显示和传输
// Runtime层 - 工具类
// ============================================================================

#if !UNITY_EDITOR
// 运行时不需要缓存管理端功能，此文件为空占位
// 缓存管理由编辑器端TextureCache负责
namespace RemoteInspector.Runtime.Utils
{
    public class TextureCache { }
}
#else
// 编辑器端纹理缓存
// 放在Runtime目录不影响编辑器编译
namespace RemoteInspector.Editor.Core
{
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>
    /// 编辑器端纹理缓存
    /// 缓存已接收的纹理缩略图，避免重复显示
    /// </summary>
    public class TextureCache
    {
        private readonly Dictionary<int, Texture2D> _cache = new Dictionary<int, Texture2D>();

        /// <summary>
        /// 添加纹理到缓存
        /// </summary>
        public void Add(int instanceId, Texture2D texture)
        {
            if (_cache.ContainsKey(instanceId))
                return;
            _cache[instanceId] = texture;
        }

        /// <summary>
        /// 获取缓存的纹理
        /// </summary>
        public Texture2D Get(int instanceId)
        {
            _cache.TryGetValue(instanceId, out var tex);
            return tex;
        }

        /// <summary>
        /// 是否已缓存
        /// </summary>
        public bool Contains(int instanceId) => _cache.ContainsKey(instanceId);

        /// <summary>
        /// 清理缓存
        /// </summary>
        public void Clear()
        {
            foreach (var tex in _cache.Values)
            {
                if (tex != null) Object.DestroyImmediate(tex);
            }
            _cache.Clear();
        }

        /// <summary>
        /// 缓存数量
        /// </summary>
        public int Count => _cache.Count;
    }
}
#endif
