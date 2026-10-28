// ============================================================================
// SerializationHelper.cs
// 序列化辅助工具 - 简化JsonUtility调用，处理IL2CPP兼容性问题
// Runtime层 - 工具类
// ============================================================================

using System;
using UnityEngine;

namespace RemoteInspector.Runtime.Utils
{
    /// <summary>
    /// 序列化辅助类
    /// 封装JsonUtility，提供更友好的API
    /// </summary>
    public static class SerializationHelper
    {
        /// <summary>
        /// 对象序列化为JSON
        /// </summary>
        public static string ToJson<T>(T obj, bool prettyPrint = false) where T : class
        {
            if (obj == null) return null;
            try
            {
                return JsonUtility.ToJson(obj, prettyPrint);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SerializationHelper] ToJson error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// JSON反序列化为对象
        /// </summary>
        public static T FromJson<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SerializationHelper] FromJson error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 合并JSON（用于部分更新）
        /// </summary>
        public static T MergeJson<T>(T original, string updateJson) where T : class
        {
            if (string.IsNullOrEmpty(updateJson)) return original;
            try
            {
                JsonUtility.FromJsonOverwrite(updateJson, original);
                return original;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SerializationHelper] MergeJson error: {ex.Message}");
                return original;
            }
        }

        /// <summary>
        /// 复制对象（序列化后反序列化）
        /// </summary>
        public static T Clone<T>(T obj) where T : class
        {
            if (obj == null) return null;
            string json = JsonUtility.ToJson(obj);
            return JsonUtility.FromJson<T>(json);
        }

        /// <summary>
        /// 获取对象JSON大小估算（字节数）
        /// </summary>
        public static int EstimateSize<T>(T obj) where T : class
        {
            if (obj == null) return 0;
            string json = JsonUtility.ToJson(obj);
            return System.Text.Encoding.UTF8.GetByteCount(json);
        }
    }
}
