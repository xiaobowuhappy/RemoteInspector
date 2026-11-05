// ============================================================================
// MaterialCollector.cs
// 材质采集器 - 收集材质/Shader信息，支持纹理缓存避免重复传输
// Runtime层 - 数据采集
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using RemoteInspector.Commands;
using RemoteInspector.Protocol;
using RemoteInspector.Runtime.Core;

namespace RemoteInspector.Runtime.Collectors
{
    /// <summary>
    /// 材质采集器
    /// 负责收集材质信息 + 纹理分块发送（含缓存）
    /// 大纹理自动缩小到预览尺寸，减少编码开销
    /// </summary>
    public class MaterialCollector
    {
        private readonly HashSet<int> _sentTextureIds = new HashSet<int>();

        /// <summary>纹理预览最大尺寸，超过此尺寸自动缩放</summary>
        private const int MAX_PREVIEW_SIZE = 256;

        /// <summary>
        /// 重置纹理缓存（连接断开时调用）
        /// </summary>
        public void ResetCache()
        {
            _sentTextureIds.Clear();
        }

        /// <summary>
        /// 发送纹理数据（分块，含缓存检测）
        /// 大纹理自动缩放到 MAX_PREVIEW_SIZE 再编码
        /// </summary>
        public void SendTexture(int textureInstanceId, bool forceRefresh, RemoteInspectorClient client)
        {
            // 检查缓存
            if (!forceRefresh && _sentTextureIds.Contains(textureInstanceId))
            {
                // 发送"已缓存"响应
                var resp = new TextureResponse
                {
                    sequence = 0,
                    instanceId = textureInstanceId,
                    cached = true,
                    isLast = true,
                    chunkIndex = 0,
                    totalChunks = 0
                };
                string json = MessageSerializer.SerializeBody(resp);
                client.SendPacket(Command.TEXTURE_RESPONSE, 0, json);
                return;
            }

            // 使用缓存查找纹理
            Texture2D tex = TextureCache.Find(textureInstanceId);
            if (tex == null)
            {
                // 兜底:Material.mainTexture 也可能是 RenderTexture / Cubemap 等非 Texture2D
                tex = FindTextureFallback(textureInstanceId);
            }
            if (tex == null)
            {
                Debug.LogWarning($"[RemoteInspector] Texture not found: instanceId={textureInstanceId}");
                // 关键:不要直接 return,要发一个"无法提供"的响应,
                // 否则 Editor 端 _requestedTextureIds 永远卡着,UI 一直显示"加载中..."
                SendTextureUnavailable(textureInstanceId, client, "not_found");
                return;
            }

            if (!tex.isReadable)
            {
                Debug.LogWarning($"[RemoteInspector] Texture '{tex.name}' is not readable, preview skipped.");
                SendTextureUnavailable(textureInstanceId, client, "unread", tex.name, tex.width, tex.height);
                return;
            }

            // 标记为已发送
            _sentTextureIds.Add(textureInstanceId);

            // 缩放大纹理再编码，大幅减少 CPU 开销
            int origW = tex.width;
            int origH = tex.height;
            Texture2D texToEncode = tex;

            if (origW > MAX_PREVIEW_SIZE || origH > MAX_PREVIEW_SIZE)
            {
                texToEncode = ScaleTexture(tex, MAX_PREVIEW_SIZE);
                if (texToEncode == null)
                    texToEncode = tex; // 缩放失败，用原图
            }

            try
            {
                // 压缩为JPG
                byte[] jpgData = texToEncode.EncodeToJPG(MessageProtocol.TEXTURE_JPG_QUALITY);
                string base64 = Convert.ToBase64String(jpgData);

                // 分块发送
                int chunkSize = MessageProtocol.TEXTURE_CHUNK_SIZE;
                int totalChunks = (int)Math.Ceiling((double)base64.Length / chunkSize);

                for (int i = 0; i < totalChunks; i++)
                {
                    int start = i * chunkSize;
                    int len = Math.Min(chunkSize, base64.Length - start);
                    string chunkData = base64.Substring(start, len);

                    var resp = new TextureResponse
                    {
                        sequence = 0,
                        instanceId = textureInstanceId,
                        name = tex.name,
                        cached = false,
                        chunkIndex = i,
                        totalChunks = totalChunks,
                        data = chunkData,
                        width = origW,  // 报告原始尺寸
                        height = origH,
                        isLast = (i == totalChunks - 1),
                        status = "ok"
                    };

                    string json = MessageSerializer.SerializeBody(resp);
                    client.SendPacket(Command.TEXTURE_RESPONSE, 0, json);
                }

                Debug.Log($"[RemoteInspector] Sent texture '{tex.name}' ({origW}x{origH}) in {totalChunks} chunks" +
                          (texToEncode != tex ? $" [scaled to {texToEncode.width}x{texToEncode.height}]" : ""));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemoteInspector] Texture '{tex.name}' preview encode failed: {ex.Message}");
                SendTextureUnavailable(textureInstanceId, client, "encode_error", tex.name, origW, origH);
            }
            finally
            {
                // 释放临时缩放纹理
                if (texToEncode != tex && texToEncode != null)
                    UnityEngine.Object.Destroy(texToEncode);
            }
        }

        /// <summary>
        /// 将纹理缩小到目标尺寸（等比缩放，最长边 = maxSize）
        /// 必须在主线程调用
        /// </summary>
        private Texture2D ScaleTexture(Texture source, int maxSize)
        {
            try
            {
                if (source == null) return null;

                int srcW = source.width;
                int srcH = source.height;
                float scale = (float)maxSize / Mathf.Max(srcW, srcH);

                int dstW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
                int dstH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));

                RenderTexture rt = RenderTexture.GetTemporary(dstW, dstH, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, rt);

                RenderTexture.active = rt;
                Texture2D scaled = new Texture2D(dstW, dstH, TextureFormat.RGB24, false);
                scaled.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0);
                scaled.Apply(false);

                RenderTexture.active = null;
                RenderTexture.ReleaseTemporary(rt);

                return scaled;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemoteInspector] Texture scale failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 兜底查找:Texture2D 缓存可能漏掉 RenderTexture / Cubemap 等。
        /// Material.mainTexture 在 Unity 文档里返回基类 Texture,
        /// 实际可能是 RT(Camera 输出 / 自定义 RT),不能渲染的纹理也支持预览(转 Texture2D 截图)。
        /// </summary>
        private static Texture2D FindTextureFallback(int instanceId)
        {
            try
            {
                // 全场景扫一遍,代价大但只在 Texture2D 缓存 miss 时触发
                UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll<Texture>();
                foreach (var t in all)
                {
                    if (t != null && t.GetInstanceID() == instanceId)
                    {
                        // RT/Cubemap 转 Texture2D 截图
                        if (t is RenderTexture rt)
                            return ReadRenderTexture(rt);
                        if (t is Cubemap cube)
                            return ReadCubemap(cube);
                        // 其它非 Texture2D 不支持
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemoteInspector] FindTextureFallback failed: {ex.Message}");
            }
            return null;
        }

        private static Texture2D ReadRenderTexture(RenderTexture rt)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex;
        }

        private static Texture2D ReadCubemap(Cubemap cube)
        {
            // 简单用一张平面贴图代表 6 面拼图
            int size = cube.width;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            for (int face = 0; face < 6 && face < 1; face++) // 只取第一面,够用
            {
                Color[] pixels = cube.GetPixels((CubemapFace)face);
                tex.SetPixels(pixels);
            }
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// 找不到纹理时,发"无法提供"响应(cached=true 但 data=null),
        /// Editor 端会 CacheThumbnail(key, null) → 加入 _failedTextureIds,UI 显示"❌ 未找到"
        /// </summary>
        private static void SendTextureUnavailable(int textureInstanceId, RemoteInspectorClient client,
            string status, string textureName = "", int width = 0, int height = 0)
        {
            try
            {
                var resp = new TextureResponse
                {
                    sequence = 0,
                    instanceId = textureInstanceId,
                    name = textureName ?? "",
                    cached = true,         // 表示"无新数据需要传"
                    isLast = true,
                    chunkIndex = 0,
                    totalChunks = 0,
                    data = null,           // null → Editor 端 tex 解出来是 null
                    width = width,
                    height = height,
                    status = status
                };
                string json = MessageSerializer.SerializeBody(resp);
                client.SendPacket(Command.TEXTURE_RESPONSE, 0, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] SendTextureUnavailable failed: {ex.Message}");
            }
        }
    }
}
