// ============================================================================
// MessageHandler.cs
// 客户端消息处理器 - 处理来自编辑器的各种请求
// Runtime层 - 核心网络层
// ============================================================================
 
using System;
using System.Collections.Generic;
using UnityEngine;
using RemoteInspector.Commands;
using RemoteInspector.Protocol;
using RemoteInspector.Runtime.Collectors;

namespace RemoteInspector.Runtime.Core
{
    /// <summary>
    /// 客户端消息处理器
    /// 处理来自编辑器的各种命令请求
    /// </summary>
    public class MessageHandler
    {
        private readonly RemoteInspectorClient _client;

        // 数据采集器
        private NodeTreeCollector _nodeTreeCollector;
        private ComponentCollector _componentCollector;
        private MaterialCollector _materialCollector;
        private FrameStatsCollector _frameStatsCollector;
        private FileSystemCollector _fileSystemCollector;

        // RPC 处理器
        private RpcHandler _rpcHandler;

        // 文件上传接收器(Editor -> Runtime 写文件)
        private FileUploadHandler _fileUploadHandler;

        /// <summary>获取 RPC 处理器（供外部注册函数）</summary>
        public RpcHandler Rpc => _rpcHandler;

        /// <summary>暴露 FileUploadHandler 供 Client 在断线时调用 DisposeAll()(释放进行中的 FileStream)</summary>
        public FileUploadHandler FileUpload => _fileUploadHandler;

        public MessageHandler(RemoteInspectorClient client)
        {
            _client = client;
            _rpcHandler = new RpcHandler(client);
            _fileUploadHandler = new FileUploadHandler(client);
            _nodeTreeCollector = new NodeTreeCollector();
            _componentCollector = new ComponentCollector();
            _materialCollector = new MaterialCollector();
            _frameStatsCollector = new FrameStatsCollector();
            _fileSystemCollector = new FileSystemCollector();
        }

        /// <summary>
        /// 主线程帧更新（采集 FrameStats）
        /// </summary>
        public void UpdateFrameStats()
        {
            _frameStatsCollector.Update();
        }

        /// <summary>
        /// 在主线程上采集并发送节点树（连接后首次调用）
        /// 海量场景性能优化(2026-06-25):Binary 优先,体积比 JSON 小 ~80%,序列化/反序列化快 ~70%。
        ///   注:JSON 路径仍保留给老 Runtime/Editor 兼容(老 Runtime 不会发 Binary,Editor 端订阅 Binary)。
        /// </summary>
        public void CollectAndSendNodeTree(ushort seq)
        {
            try
            {
                var treeData = _nodeTreeCollector.Collect();

                // Binary 主路径
                try
                {
                    byte[] binary = BinaryNodeTreeBuilder.EncodeResponse(treeData);
                    if (binary != null && binary.Length > 0)
                    {
                        _client.SendPacket(Command.NODE_TREE_RESPONSE_BINARY, seq, binary);
                    }
                    else
                    {
                        string json = MessageSerializer.SerializeBody(treeData);
                        Debug.LogWarning($"[RemoteInspector][Client] Binary node tree empty initial seq={seq}, fallback jsonLen={json?.Length ?? 0}");
                        _client.SendPacket(Command.NODE_TREE_RESPONSE, seq, json);
                    }
                }
                catch (Exception binEx)
                {
                    Debug.LogWarning($"[RemoteInspector] Binary node-tree encode failed, fallback JSON: {binEx.Message}");
                    string json = MessageSerializer.SerializeBody(treeData);
                    _client.SendPacket(Command.NODE_TREE_RESPONSE, seq, json);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] CollectAndSendNodeTree error: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理收到的消息包
        /// </summary>
        public void Handle(MessagePacket packet)
        {
            Command cmd = packet.GetCommand();

            switch (cmd)
            {
                case (Command)InspectionProtocol.ScreenPick:
                    InspectionExtensions.HandlePick(packet, _client);
                    break;
                case (Command)InspectionProtocol.TextureOriginal:
                    InspectionExtensions.HandleOriginalTexture(packet, _client);
                    break;
                case Command.GET_NODE_TREE:
                    HandleGetNodeTree(packet);
                    break;
                case Command.GET_NODE_DETAIL:
                    HandleGetNodeDetail(packet);
                    break;
                case Command.SET_PROPERTY:
                    HandleSetProperty(packet);
                    break;
                case Command.PAUSE_CLIENT:
                    HandlePauseClient(packet);
                    break;
                case Command.RESUME_CLIENT:
                    HandleResumeClient(packet);
                    break;
                case Command.GET_FRAME_STATS:
                    HandleGetFrameStats(packet);
                    break;
                case Command.GET_TEXTURE:
                    HandleGetTexture(packet);
                    break;
                case Command.FILE_UPLOAD_START:
                    {
                        var req = MessageSerializer.DeserializeBody<FileUploadStartRequest>(packet);
                        _fileUploadHandler.HandleUploadStart(req);
                    }
                    break;
                case Command.FILE_UPLOAD_CHUNK:
                    {
                        var req = MessageSerializer.DeserializeBody<FileUploadChunkRequest>(packet);
                        _fileUploadHandler.HandleUploadChunk(req);
                    }
                    break;
                case Command.FILE_UPLOAD_END:
                    {
                        var req = MessageSerializer.DeserializeBody<FileUploadEndRequest>(packet);
                        _fileUploadHandler.HandleUploadEnd(req);
                    }
                    break;

                // ===================== 文件浏览 =====================
                case Command.FILE_BROWSE_LIST:
                    HandleFileBrowseList(packet);
                    break;
                case Command.FILE_BROWSE_MKDIR:
                    HandleFileBrowseMkdir(packet);
                    break;
                case Command.FILE_BROWSE_DELETE:
                    HandleFileBrowseDelete(packet);
                    break;
                case Command.FILE_BROWSE_DOWNLOAD:
                    HandleFileBrowseDownload(packet);
                    break;

                // ===================== 画面抓取 =====================
                case Command.SCREEN_CAPTURE:
                    HandleScreenCapture(packet);
                    break;

                // ===================== RPC 远程调用 =====================
                case Command.RPC_CALL:
                    _rpcHandler.HandleRpcCall(packet);
                    break;
                case Command.RPC_LIST:
                    _rpcHandler.HandleRpcList(packet);
                    break;

                // ===================== 图像替换 =====================
                case Command.REPLACE_IMAGE_SPRITE:
                    HandleReplaceImageSprite(packet);
                    break;
            }
        }

        // ==================== 节点树 ====================

        private void HandleGetNodeTree(MessagePacket packet)
        {
            try
            {
                var req = MessageSerializer.DeserializeBody<RequestBase>(packet);
                ushort seq = req?.sequence ?? packet.sequence;

                _nodeTreeCollector.MarkDirty(); // Explicit refresh must see runtime hierarchy and component changes.
                var treeData = _nodeTreeCollector.Collect();

                // Binary 主路径
                try
                {
                    byte[] binary = BinaryNodeTreeBuilder.EncodeResponse(treeData);
                    if (binary != null && binary.Length > 0)
                    {
                        _client.SendPacket(Command.NODE_TREE_RESPONSE_BINARY, seq, binary);
                    }
                    else
                    {
                        string json = MessageSerializer.SerializeBody(treeData);
                        Debug.LogWarning($"[RemoteInspector][Client] Binary node tree empty seq={seq}, fallback jsonLen={json?.Length ?? 0}");
                        _client.SendPacket(Command.NODE_TREE_RESPONSE, seq, json);
                    }
                }
                catch (Exception binEx)
                {
                    Debug.LogWarning($"[RemoteInspector] Binary node-tree encode failed, fallback JSON: {binEx.Message}");
                    string json = MessageSerializer.SerializeBody(treeData);
                    _client.SendPacket(Command.NODE_TREE_RESPONSE, seq, json);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] HandleGetNodeTree error: {ex.Message}");
            }
        }

        // ==================== 节点详情 ====================

        private void HandleGetNodeDetail(MessagePacket packet)
        {
            try
            {
                var req = MessageSerializer.DeserializeBody<GetNodeDetailRequest>(packet);
                if (req == null)
                {
                    Debug.LogWarning("[RemoteInspector] Invalid node detail request");
                    return;
                }

                var detail = _componentCollector.Collect(req.instanceId, req.detailed);
                detail.sequence = req.sequence;
                InspectionExtensions.FillTextureMetadata(detail);

                string json = MessageSerializer.SerializeBody(detail);
                _client.SendPacket(Command.NODE_DETAIL_RESPONSE, req.sequence, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] HandleGetNodeDetail error: {ex.Message}");
            }
        }

        // ==================== 暂停/恢复 ====================

        private void HandleSetProperty(MessagePacket packet)
        {
            SetPropertyResponse resp = new SetPropertyResponse();
            try
            {
                var req = MessageSerializer.DeserializeBody<SetPropertyRequest>(packet);
                if (req == null)
                    return;

                resp.sequence = req.sequence;
                resp.instanceId = req.instanceId;

                GameObject go = GameObjectCache.Find(req.instanceId);
                if (go == null)
                {
                    resp.success = false;
                    resp.error = "远程节点丢失 操作无效";
                    SendSetPropertyResponse(resp);
                    return;
                }

                ApplyProperty(go, req);
                _nodeTreeCollector.MarkDirty();
                resp.success = true;
                SendSetPropertyResponse(resp);
            }
            catch (Exception ex)
            {
                resp.success = false;
                resp.error = ex.Message;
                SendSetPropertyResponse(resp);
            }
        }

        private void SendSetPropertyResponse(SetPropertyResponse resp)
        {
            string json = MessageSerializer.SerializeBody(resp);
            _client.SendPacket(Command.SET_PROPERTY_RESPONSE, resp.sequence, json);
        }

        private void ApplyProperty(GameObject go, SetPropertyRequest req)
        {
            if (req.componentType == "GameObject")
            {
                if (req.propertyPath == "activeSelf")
                    go.SetActive(req.boolValue);
                return;
            }

            Component comp = GetComponentByType(go, req.componentType, req.componentIndex);
            if (comp == null)
                throw new InvalidOperationException("远程节点丢失 操作无效");

            Transform tf = comp as Transform;
            if (tf != null)
            {
                Vector3 v = new Vector3(req.x, req.y, req.z);
                if (req.propertyPath == "localPosition") tf.localPosition = v;
                else if (req.propertyPath == "localRotation") tf.localEulerAngles = v;
                else if (req.propertyPath == "localScale") tf.localScale = v;
                return;
            }

            Behaviour behaviour = comp as Behaviour;
            if (behaviour != null && req.propertyPath == "enabled")
            {
                behaviour.enabled = req.boolValue;
                return;
            }

            BoxCollider box = comp as BoxCollider;
            if (box != null)
            {
                if (req.propertyPath == "center") box.center = new Vector3(req.x, req.y, req.z);
                else if (req.propertyPath == "size") box.size = new Vector3(req.x, req.y, req.z);
                else if (req.propertyPath == "isTrigger") box.isTrigger = req.boolValue;
                return;
            }

            Camera camera = comp as Camera;
            if (camera != null)
            {
                if (req.propertyPath == "cullingMask") camera.cullingMask = req.intValue;
                else if (req.propertyPath == "fieldOfView") camera.fieldOfView = (float)req.numberValue;
                else if (req.propertyPath == "nearClipPlane") camera.nearClipPlane = (float)req.numberValue;
                else if (req.propertyPath == "farClipPlane") camera.farClipPlane = (float)req.numberValue;
                return;
            }

            Renderer renderer = comp as Renderer;
            if (renderer != null && req.propertyPath != null && req.propertyPath.StartsWith("materials."))
            {
                ApplyRendererMaterialProperty(renderer, req);
                return;
            }

            ApplyReflectedField(comp, req);
        }

        private static void ApplyRendererMaterialProperty(Renderer renderer, SetPropertyRequest req)
        {
            switch (req.propertyPath)
            {
                case "materials.visible":
                    RendererMaterialSlots.SetVisible(renderer, req.intValue, req.boolValue);
                    break;
                case "materials.renderQueue":
                    Material queueMaterial = RendererMaterialSlots.GetMaterial(renderer, req.intValue);
                    if (queueMaterial == null) throw new InvalidOperationException("材质层为空，无法修改 Render Queue");
                    queueMaterial.renderQueue = (int)req.numberValue;
                    break;
                case "materials.keyword":
                    Material material = RendererMaterialSlots.GetMaterial(renderer, req.intValue);
                    if (material == null) throw new InvalidOperationException("材质层为空，无法修改关键字");
                    string keyword = (req.stringValue ?? string.Empty).Trim();
                    if (keyword.Length == 0) throw new InvalidOperationException("关键字不能为空");
                    if (req.boolValue) material.EnableKeyword(keyword);
                    else material.DisableKeyword(keyword);
                    break;
                case "materials.property":
                    Material propertyMaterial = RendererMaterialSlots.GetMaterial(renderer, req.intValue);
                    if (propertyMaterial == null) throw new InvalidOperationException("材质层为空，无法修改属性");
                    string propertyName = (req.stringValue ?? string.Empty).Trim();
                    if (!propertyMaterial.HasProperty(propertyName))
                        throw new InvalidOperationException("材质不存在属性: " + propertyName);
                    if (req.valueType == "materialColor")
                        propertyMaterial.SetColor(propertyName, new Color(req.x, req.y, req.z, req.w));
                    else if (req.valueType == "materialVector")
                        propertyMaterial.SetVector(propertyName, new Vector4(req.x, req.y, req.z, req.w));
                    else if (req.valueType == "materialFloat")
                        propertyMaterial.SetFloat(propertyName, (float)req.numberValue);
                    else
                        throw new InvalidOperationException("不支持的材质属性类型: " + req.valueType);
                    break;
                default:
                    throw new InvalidOperationException("不支持的材质层操作: " + req.propertyPath);
            }
        }

        private Component GetComponentByType(GameObject go, string typeName, int index)
        {
            Component[] components = go.GetComponents<Component>();
            int match = 0;
            foreach (var comp in components)
            {
                if (comp == null)
                    continue;

                Type type = comp.GetType();
                if (type.FullName == typeName || type.Name == typeName)
                {
                    if (match == index)
                        return comp;
                    match++;
                }
            }
            return null;
        }

        private void ApplyReflectedField(Component comp, SetPropertyRequest req)
        {
            Type type = comp.GetType();
            var field = type.GetField(req.propertyPath, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field == null)
                return;

            Type ft = field.FieldType;
            if (ft == typeof(bool)) field.SetValue(comp, req.boolValue);
            else if (ft == typeof(int)) field.SetValue(comp, req.intValue);
            else if (ft == typeof(float)) field.SetValue(comp, (float)req.numberValue);
            else if (ft == typeof(double)) field.SetValue(comp, req.numberValue);
            else if (ft == typeof(string)) field.SetValue(comp, req.stringValue);
            else if (ft == typeof(Vector2)) field.SetValue(comp, new Vector2(req.x, req.y));
            else if (ft == typeof(Vector3)) field.SetValue(comp, new Vector3(req.x, req.y, req.z));
            else if (ft == typeof(Vector4)) field.SetValue(comp, new Vector4(req.x, req.y, req.z, req.w));
            else if (ft == typeof(Color)) field.SetValue(comp, new Color(req.x, req.y, req.z, req.w));
            else if (ft.IsEnum) field.SetValue(comp, Enum.ToObject(ft, req.intValue));
        }

        private void HandlePauseClient(MessagePacket packet)
        {
            try
            {
                var req = MessageSerializer.DeserializeBody<RequestBase>(packet);
                ushort seq = req?.sequence ?? packet.sequence;

                _client.Pause();

                // 回复确认
                _client.SendPacket(Command.PAUSE_CLIENT, seq, "{\"success\":true}");
                Debug.Log("[RemoteInspector] Paused by editor");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] HandlePauseClient error: {ex.Message}");
            }
        }

        private void HandleResumeClient(MessagePacket packet)
        {
            try
            {
                var req = MessageSerializer.DeserializeBody<RequestBase>(packet);
                ushort seq = req?.sequence ?? packet.sequence;

                _client.Resume();

                // 回复确认
                _client.SendPacket(Command.RESUME_CLIENT, seq, "{\"success\":true}");
                Debug.Log("[RemoteInspector] Resumed by editor");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] HandleResumeClient error: {ex.Message}");
            }
        }

        // ==================== 帧率统计 ====================

        private void HandleGetFrameStats(MessagePacket packet)
        {
            try
            {
                var req = MessageSerializer.DeserializeBody<RequestBase>(packet);
                ushort seq = req?.sequence ?? packet.sequence;

                var stats = _frameStatsCollector.GetLatestStats();

                string json = MessageSerializer.SerializeBody(stats);
                _client.SendPacket(Command.FRAME_STATS_RESPONSE, seq, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] HandleGetFrameStats error: {ex.Message}");
            }
        }

        // ==================== 纹理请求 ====================

        private void HandleGetTexture(MessagePacket packet)
        {
            try
            {
                var req = MessageSerializer.DeserializeBody<GetTextureRequest>(packet);
                if (req == null) return;

                _materialCollector.SendTexture(req.instanceId, req.forceRefresh, _client);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] HandleGetTexture error: {ex.Message}");
            }
        }

        // ==================== 文件浏览 ====================

        private void HandleFileBrowseList(MessagePacket packet)
        {
            try
            {
                var req = MessageSerializer.DeserializeBody<FileBrowseListRequest>(packet);
                if (req == null) return;

                var resp = _fileSystemCollector.ListDirectory(req.relativePath);
                resp.sequence = req.sequence;

                string json = MessageSerializer.SerializeBody(resp);
                _client.SendPacket(Command.FILE_BROWSE_LIST_RESPONSE, req.sequence, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] HandleFileBrowseList error: {ex.Message}");
            }
        }

        private void HandleFileBrowseMkdir(MessagePacket packet)
        {
            try
            {
                var req = MessageSerializer.DeserializeBody<FileBrowseMkdirRequest>(packet);
                if (req == null) return;

                var resp = _fileSystemCollector.CreateDirectory(req.relativePath);
                resp.sequence = req.sequence;

                string json = MessageSerializer.SerializeBody(resp);
                _client.SendPacket(Command.FILE_BROWSE_MKDIR_RESPONSE, req.sequence, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] HandleFileBrowseMkdir error: {ex.Message}");
            }
        }

        private void HandleFileBrowseDelete(MessagePacket packet)
        {
            try
            {
                var req = MessageSerializer.DeserializeBody<FileBrowseDeleteRequest>(packet);
                if (req == null) return;

                var resp = _fileSystemCollector.Delete(req.relativePath, req.isDirectory);
                resp.sequence = req.sequence;

                string json = MessageSerializer.SerializeBody(resp);
                _client.SendPacket(Command.FILE_BROWSE_DELETE_RESPONSE, req.sequence, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] HandleFileBrowseDelete error: {ex.Message}");
            }
        }

        private void HandleFileBrowseDownload(MessagePacket packet)
        {
            try
            {
                var req = MessageSerializer.DeserializeBody<FileBrowseDownloadRequest>(packet);
                if (req == null) return;

                List<FileBrowseDownloadChunk> chunks;
                if (req.isDirectory)
                {
                    // 目录下载：打包为 zip 再分块
                    chunks = _fileSystemCollector.ReadDirectoryAsZipChunks(req.relativePath, req.sequence);
                }
                else
                {
                    // 文件下载：直接读取分块
                    chunks = _fileSystemCollector.ReadFileChunks(req.relativePath, req.sequence);
                }

                foreach (var chunk in chunks)
                {
                    string json = MessageSerializer.SerializeBody(chunk);
                    _client.SendPacket(Command.FILE_BROWSE_DOWNLOAD_CHUNK, req.sequence, json);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] HandleFileBrowseDownload error: {ex.Message}");
            }
        }

        // ==================== 画面抓取 ====================

        private void HandleScreenCapture(MessagePacket packet)
        {
            try
            {
                var req = MessageSerializer.DeserializeBody<ScreenCaptureRequest>(packet);
                if (req == null) return;

                int maxSize = req.maxSize > 0 ? req.maxSize : 480;

                // 截图必须在 end-of-frame 阶段执行,这里只把任务交给 client 启动协程。
                // (CaptureScreenshotAsTexture 直接在 Update/LateUpdate 调用会抛:
                //   "failed to generate texture! Was method called before the 'end of frame' state was reached?")
                _client.StartScreenCapture(maxSize, req.sequence);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] HandleScreenCapture error: {ex.Message}");
                try
                {
                    var errChunk = new ScreenCaptureChunk
                    {
                        sequence = packet.sequence,
                        success = false,
                        error = ex.Message,
                        isLast = true
                    };
                    string errJson = MessageSerializer.SerializeBody(errChunk);
                    _client.SendPacket(Command.SCREEN_CAPTURE_CHUNK, packet.sequence, errJson);
                }
                catch { }
            }
        }

        // ==================== 图像替换 ====================

        /// <summary>
        /// 处理 Editor 端发来的"用本地纹理替换 Image.sprite / UITexture.mainTexture"请求。
        /// 走"文件路径引用"模式:Editor 发本地绝对路径 → Runtime 用 File.ReadAllBytes 读 →
        /// Texture2D.LoadImage → 反射设到目标组件。
        /// </summary>
        private void HandleReplaceImageSprite(MessagePacket packet)
        {
            try
            {
                var req = MessageSerializer.DeserializeBody<ReplaceImageSpriteRequest>(packet);
                if (req == null || string.IsNullOrEmpty(req.filePath))
                {
                    SendReplaceImageSpriteResponse(packet.sequence, false,
                        "Empty request: filePath required", 0, 0, 0, 0);
                    return;
                }

                // 找目标 GameObject
                GameObject go = GameObjectCache.Find(req.instanceId);
                if (go == null)
                {
                    SendReplaceImageSpriteResponse(packet.sequence, false,
                        $"GameObject (InstanceID={req.instanceId}) not found", 0, 0, 0, 0);
                    return;
                }

                if (string.Equals(req.componentType, "Material", StringComparison.OrdinalIgnoreCase))
                {
                    ReplaceMaterialTexture(go, req, packet.sequence);
                    return;
                }

                // 解析组件类型: "Image" / "UITexture"(默认 Image)
                string compType = string.IsNullOrEmpty(req.componentType) ? "Image" : req.componentType.Trim();
                Component targetComp = compType.Equals("UITexture", StringComparison.OrdinalIgnoreCase)
                    ? FindUITextureComponent(go)
                    : FindImageComponent(go);

                if (targetComp == null)
                {
                    SendReplaceImageSpriteResponse(packet.sequence, false,
                        $"GameObject '{go.name}' has no '{compType}' component", 0, 0, 0, 0);
                    return;
                }

                // 读本地文件 → Texture2D
                Texture2D tex;
                try
                {
                    if (!System.IO.File.Exists(req.filePath))
                    {
                        SendReplaceImageSpriteResponse(packet.sequence, false,
                            $"File not found: {req.filePath}", 0, 0, 0, 0);
                        return;
                    }
                    byte[] bytes = System.IO.File.ReadAllBytes(req.filePath);
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!tex.LoadImage(bytes))
                    {
                        UnityEngine.Object.Destroy(tex);
                        SendReplaceImageSpriteResponse(packet.sequence, false,
                            $"Failed to decode texture from {req.filePath} (LoadImage returned false)", 0, 0, 0, 0);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    SendReplaceImageSpriteResponse(packet.sequence, false,
                        $"Texture load error: {ex.Message}", 0, 0, 0, 0);
                    return;
                }

                // 按组件类型设值
                if (compType.Equals("UITexture", StringComparison.OrdinalIgnoreCase))
                {
                    // UITexture.mainTexture (Texture 类型) — 反射写入
                    var mainTexProp = targetComp.GetType().GetProperty("mainTexture",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (mainTexProp == null)
                    {
                        UnityEngine.Object.Destroy(tex);
                        SendReplaceImageSpriteResponse(packet.sequence, false,
                            "UITexture.mainTexture property not found via reflection", 0, 0, 0, 0);
                        return;
                    }
                    mainTexProp.SetValue(targetComp, tex);
                    string texName = string.IsNullOrEmpty(req.spriteName)
                        ? $"RemoteInspectorTex_{go.name}"
                        : req.spriteName;
                    tex.name = texName;

                    Debug.Log($"[RemoteInspector] Replaced UITexture.mainTexture on '{go.name}' ({tex.width}x{tex.height})");

                    SendReplaceImageSpriteResponse(packet.sequence, true, null,
                        0, tex.GetInstanceID(), tex.width, tex.height);
                    return;
                }

                // Image.sprite 模式 — 创建 Sprite 后反射设置
                float ppu = req.pixelsPerUnit > 0 ? req.pixelsPerUnit : 100f;
                string spriteName = string.IsNullOrEmpty(req.spriteName)
                    ? $"RemoteInspectorSprite_{go.name}"
                    : req.spriteName;
                Sprite sprite = Sprite.Create(
                    tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f),  // pivot center
                    ppu,
                    0,                              // extrude
                    SpriteMeshType.FullRect,
                    new Vector4(0, 0, 0, 0),       // border
                    false);                         // generateFallbackPhysicsShape
                sprite.name = spriteName;

                // 反射设置 Image.sprite
                var imageType = targetComp.GetType();
                var spriteProp = imageType.GetProperty("sprite",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (spriteProp == null)
                {
                    UnityEngine.Object.Destroy(sprite);
                    UnityEngine.Object.Destroy(tex);
                    SendReplaceImageSpriteResponse(packet.sequence, false,
                        "Image.sprite property not found via reflection", 0, 0, 0, 0);
                    return;
                }
                spriteProp.SetValue(targetComp, sprite);

                Debug.Log($"[RemoteInspector] Replaced Image.sprite on '{go.name}' ({tex.width}x{tex.height}, ppu={ppu})");

                SendReplaceImageSpriteResponse(packet.sequence, true, null,
                    sprite.GetInstanceID(), tex.GetInstanceID(), tex.width, tex.height);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] HandleReplaceImageSprite error: {ex.Message}");
                SendReplaceImageSpriteResponse(packet.sequence, false, ex.Message, 0, 0, 0, 0);
            }
        }

        private void ReplaceMaterialTexture(GameObject go, ReplaceImageSpriteRequest req, ushort sequence)
        {
            Renderer renderer = GetComponentByType(go, req.rendererType, req.componentIndex) as Renderer;
            if (renderer == null) throw new InvalidOperationException("目标 Renderer 不存在");

            Material material = RendererMaterialSlots.GetMaterial(renderer, req.materialIndex);
            if (material == null) throw new InvalidOperationException("目标材质层为空");
            if (string.IsNullOrEmpty(req.propertyName) || !material.HasProperty(req.propertyName))
                throw new InvalidOperationException("材质纹理属性不存在: " + req.propertyName);
            if (!System.IO.File.Exists(req.filePath))
                throw new System.IO.FileNotFoundException("纹理文件不存在", req.filePath);

            byte[] bytes = System.IO.File.ReadAllBytes(req.filePath);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(texture);
                throw new InvalidOperationException("无法解析纹理文件: " + req.filePath);
            }
            texture.name = string.IsNullOrEmpty(req.spriteName) ? "RemoteInspectorMaterialTex" : req.spriteName;
            material.SetTexture(req.propertyName, texture);
            SendReplaceImageSpriteResponse(sequence, true, null, 0, texture.GetInstanceID(), texture.width, texture.height);
        }

        /// <summary>
        /// 在 GameObject 上找 NGUI UITexture 组件(用反射避免硬依赖 NGUI)
        /// </summary>
        private static Component FindUITextureComponent(GameObject go)
        {
            // 先尝试精确类名;再退化到任何继承 UIWidget 的组件(避免 NGUI 改名)
            var exactType = System.Type.GetType("UITexture, Assembly-CSharp");
            if (exactType != null) return go.GetComponent(exactType);
            // 回退:扫所有 component 找带 mainTexture 属性的 NGUI 类
            var comps = go.GetComponents<Component>();
            foreach (var c in comps)
            {
                if (c == null) continue;
                var t = c.GetType();
                if (t.FullName == "UITexture") return c;
                // UIWidget 基类有 mainTexture,但 UITexture 直接暴露 set;这里只精确匹配
            }
            return null;
        }

        /// <summary>
        /// 在 GameObject 上找 UnityEngine.UI.Image 组件(用反射避免硬依赖)
        /// </summary>
        private static Component FindImageComponent(GameObject go)
        {
            var imageType = System.Type.GetType("UnityEngine.UI.Image, UnityEngine.UI");
            if (imageType == null) return null;
            return go.GetComponent(imageType);
        }

        private void SendReplaceImageSpriteResponse(ushort seq, bool success, string error,
            int spriteInstanceId, int textureInstanceId, int width, int height)
        {
            try
            {
                var resp = new ReplaceImageSpriteResponse
                {
                    sequence = seq,
                    success = success,
                    error = error,
                    spriteInstanceId = spriteInstanceId,
                    textureInstanceId = textureInstanceId,
                    width = width,
                    height = height,
                };
                string json = MessageSerializer.SerializeBody(resp);
                _client.SendPacket(Command.REPLACE_IMAGE_SPRITE_RESPONSE, seq, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] SendReplaceImageSpriteResponse failed: {ex.Message}");
            }
        }
    }
}
