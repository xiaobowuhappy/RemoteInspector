// ============================================================================
// ComponentCollector.cs
// 组件数据采集器 - 收集GameObject所有组件的详细信息
// Runtime层 - 数据采集
// ============================================================================
 
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace RemoteInspector.Runtime.Collectors
{
    using RemoteInspector.Runtime.Core;

    /// <summary>
    /// 组件数据采集器
    /// </summary>
    public class ComponentCollector
    {
        // 反射缓存：Type → FieldInfo[]，避免每次 GetFields
        private static readonly Dictionary<Type, FieldInfo[]> _fieldCache = new Dictionary<Type, FieldInfo[]>();
        private static readonly Dictionary<Type, List<int>> _serializedFieldIndices = new Dictionary<Type, List<int>>();

        // 反射元数据缓存:BaseType → 该层 GetFields(flags) 结果,避免 WalkInheritance 重复反射
        //   海量场景下 GetSerializableCandidateFields 会 walk 5-8 层 baseType,每层 GetFields 是 ~50-100ns
        //   缓存后从 800ns 降到 ~10ns。Thread-safe(Unity main thread only,但加锁保险)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, FieldInfo[]> s_baseTypeFieldsCache
            = new System.Collections.Concurrent.ConcurrentDictionary<Type, FieldInfo[]>();

        // ==================== 数据结构 ====================

        [Serializable]
        public class Vector3Data
        {
            public float x, y, z;
            public static Vector3Data From(Vector3 v) => new Vector3Data { x = v.x, y = v.y, z = v.z };
            public Vector3 To() => new Vector3(x, y, z);
        }

        [Serializable]
        public class TransformData
        {
            public Vector3Data localPosition;
            public Vector3Data localRotation;
            public Vector3Data localScale;
            public Vector3Data position;
            public Vector3Data rotation;
            public Vector3Data scale;
            public int childCount;
            public int siblingIndex;
        }

        [Serializable]
        public class SerializedFieldData
        {
            public string fieldName;
            public string fieldType;
            public string stringValue;
            public long intValue;
            public double doubleValue;
            public bool boolValue;
            public int enumValue;
            public bool isNumeric;
            public bool isNull;
            public float x;
            public float y;
            public float z;
            public float w;
            public string[] enumNames;
            public string valueType;
            /// <summary>当字段为Texture类型时，纹理的InstanceID（详细模式下填充）</summary>
            public int textureInstanceId;
            /// <summary>当字段为UnityObject类型时，对象名称（详细模式下填充）</summary>
            public string objectName;
        }

        [Serializable]
        public class ShaderPropertyData
        {
            public string name;
            public string typeName;
            public ShaderPropertyType type;
            public float floatValue;
            public float[] colorValue;
            public float[] vectorValue;
            public int textureInstanceId;
        }

        [Serializable]
        public class MaterialInfo
        {
            public int slotIndex;
            public bool visible = true;
            public int renderQueue;
            public string name;
            public string shaderName;
            public int propertyCount;
            public List<ShaderPropertyData> properties;
            public int mainTextureInstanceId;
            /// <summary>
            /// 已启用的 Shader Keywords(Runtime 填,Editor 解析)。
            /// 注:必须用 List&lt;string&gt; 而非 string[] — Unity 的 JsonUtility 对 string[] 序列化不可靠。
            /// </summary>
            public System.Collections.Generic.List<string> enabledKeywords = new System.Collections.Generic.List<string>();

            /// <summary>
            /// Shader 自身定义的**全部可用** keywords(从 <c>Shader.keywordSpace.keywordNames</c> 取)。
            /// </summary>
            public System.Collections.Generic.List<string> shaderAllKeywords = new System.Collections.Generic.List<string>();
        }

        [Serializable]
        public class MeshFilterInfo
        {
            public string meshName;
            public int vertexCount;
            public int triangleCount;
            public int subMeshCount;
            public long fileSize;
        }

        [Serializable]
        public class ImageInfo
        {
            public string spriteName;
            public string atlasName;
            public int mainTextureInstanceId;
            public float pixelsPerUnit;
            public float spriteWidth;
            public float spriteHeight;
            public string colorHex;
        }

        [Serializable]
        public class ParticleSystemRendererInfo
        {
            /// <summary>拖尾材质(主材质走 materials 通道)</summary>
            public List<MaterialInfo> trailMaterials = new List<MaterialInfo>();
        }

        [Serializable]
        public class ParticleSystemInfo
        {
            public float emitRate;
            public bool isPlaying;
            public int particleCount;
            public bool isStopped;
            public bool isPaused;
        }

        [Serializable]
        public class UITextureInfo
        {
            public string textureName;
            public string textureTypeName;
            public int width;
            public int height;
            public int mainTextureInstanceId;
            public string colorHex;
        }

        [Serializable]
        public class ComponentDetailData
        {
            public string typeName;
            public string displayName;
            public bool enabled;
            public bool hasEnabled;
            public TransformData transformData;
            public List<MaterialInfo> materials;
            public MeshFilterInfo meshFilterInfo;
            public ImageInfo imageInfo;
            public ParticleSystemRendererInfo particleSystemRendererInfo;
            public ParticleSystemInfo particleSystemInfo;
            public UITextureInfo uiTextureInfo;
            public List<SerializedFieldData> serializedFields;
        }

        [Serializable]
        public class NodeDetailResponse
        {
            public bool texturePreviewMetadata;
            public List<int> readableTextureIds;
            public ushort sequence;
            public int instanceId;
            public string name;
            public bool activeSelf;
            public List<ComponentDetailData> components;
            public long timestamp;
        }

        // ==================== 采集逻辑 ====================

        /// <summary>
        /// 采集指定GameObject的所有组件信息
        /// </summary>
        /// <param name="instanceId">GameObject InstanceID</param>
        /// <param name="detailed">是否详细模式（包含对象引用、纹理InstanceID）</param>
        public NodeDetailResponse Collect(int instanceId, bool detailed = false)
        {
            var response = new NodeDetailResponse
            {
                sequence = 0,
                instanceId = instanceId,
                timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };

            // 通过InstanceID找到GameObject
            GameObject go = FindGameObjectByInstanceId(instanceId);
            if (go == null)
            {
                response.name = $"[Unknown InstanceID: {instanceId}]";
                response.components = new List<ComponentDetailData>();
                return response;
            }

            response.name = go.name;
            response.activeSelf = go.activeSelf;
            response.components = new List<ComponentDetailData>();

            // 嵌套深度 + 循环引用防御(2026-06-25 加):
            //   当前 CollectComponent 不嵌套调用,但未来扩展(Component field → Material → Texture → ...)
            //   可能栈溢出 / 循环引用。每帧 Collect 重建这两个状态,保证 GC 压力可控。
            _collectDepth = 0;
            _visitedInstanceIds.Clear();
            _visitedInstanceIds.Add(go.GetInstanceID()); // 起点

            Component[] components = go.GetComponents<Component>();
            foreach (Component comp in components)
            {
                if (comp == null) continue;

                var detail = CollectComponent(comp, detailed);
                if (detail != null)
                    response.components.Add(detail);
            }

            return response;
        }

        // ─── 嵌套防御状态(每帧 Collect 重建)───
        /// <summary>当前递归深度上限 — 超限字段直接标 "[Truncated]"</summary>
        private const int k_MaxCollectDepth = 4;
        private int _collectDepth;
        /// <summary>已访问 instanceId — 同一对象重复嵌套 → 标 "[Circular]"</summary>
        private readonly HashSet<int> _visitedInstanceIds = new HashSet<int>(64);

        /// <summary>
        /// 检查嵌套 — 当前层数未超,且目标 instanceId 没访问过。
        /// 调用者负责:进入前 TryEnterNested(obj),出来后 LeaveNested(obj)。
        /// </summary>
        private bool TryEnterNested(UnityEngine.Object obj)
        {
            if (obj == null) return false;
            if (_collectDepth >= k_MaxCollectDepth) return false;
            int id = obj.GetInstanceID();
            if (!_visitedInstanceIds.Add(id)) return false; // 已访问过 = 循环
            _collectDepth++;
            return true;
        }

        /// <summary>退出嵌套层</summary>
        private void LeaveNested()
        {
            if (_collectDepth > 0) _collectDepth--;
        }

        private ComponentDetailData CollectComponent(Component comp, bool detailed = false)
        {
            var data = new ComponentDetailData
            {
                typeName = comp.GetType().FullName,
                displayName = comp.GetType().Name,
                hasEnabled = comp is Behaviour,
                enabled = !(comp is Behaviour) || ((Behaviour)comp).enabled,
                materials = new List<MaterialInfo>(),
                serializedFields = new List<SerializedFieldData>()
            };

            // Transform特殊处理
            Transform tf = comp as Transform;
            if (tf != null)
            {
                data.transformData = new TransformData
                {
                    localPosition = Vector3Data.From(tf.localPosition),
                    localRotation = Vector3Data.From(tf.localEulerAngles),
                    localScale = Vector3Data.From(tf.localScale),
                    position = Vector3Data.From(tf.position),
                    rotation = Vector3Data.From(tf.eulerAngles),
                    scale = Vector3Data.From(tf.lossyScale),
                    childCount = tf.childCount,
                    siblingIndex = tf.GetSiblingIndex()
                };
                return data;
            }

            // 第一步:针对常见 Renderer/Collider/Camera/Filter 等专门类型,
            //         走强类型 API 收集友好字段(不会显示 m_CastShadows 这种底层字段名)
            // 强类型字段先收集,再补齐组件自身的基础序列化字段。
            CollectCommonComponentFields(comp, data.serializedFields, detailed);
            CollectSerializedFields(comp, data.serializedFields, detailed);

            // 第二步:摘要信息(MeshFilter / Image 等)
            CollectComponentSummaries(comp, data);

            // Renderer 材质
            Renderer renderer = comp as Renderer;
            if (renderer != null)
            {
                RendererMaterialSlots.Slot[] slots = RendererMaterialSlots.GetSlots(renderer);
                if (slots != null)
                {
                    for (int i = 0; i < slots.Length; i++)
                    {
                        MaterialInfo materialInfo = CollectMaterial(slots[i].material);
                        materialInfo.slotIndex = i;
                        materialInfo.visible = slots[i].visible;
                        data.materials.Add(materialInfo);
                    }
                }
            }

            return data;
        }

        /// <summary>
        /// 收集组件的摘要信息(用于 UI 顶部展示,代替堆叠式 SerializedField)
        ///   - MeshFilter: mesh name / 三角面 / 顶点 / subMesh
        ///   - Image(UnityEngine.UI): sprite / atlas / 主纹理
        /// </summary>
        private void CollectComponentSummaries(Component comp, ComponentDetailData data)
        {
            // MeshFilter — 永远填(基础信息,非详细模式也该有)
            if (comp is MeshFilter mf)
            {
                try
                {
                    var mesh = mf.sharedMesh;
                    if (mesh != null)
                    {
                        // 三角面数 = sum(GetIndexCount(submesh)) / 3(单三角面索引)
                        // 或直接 mesh.triangles.Length / 3(更通用但更慢)
                        long triCount = 0;
                        int subCount = mesh.subMeshCount;
                        for (int i = 0; i < subCount; i++)
                        {
                            triCount += mesh.GetIndexCount(i) / 3;
                        }

                        data.meshFilterInfo = new MeshFilterInfo
                        {
                            meshName       = mesh.name,
                            vertexCount    = mesh.vertexCount,
                            triangleCount  = (int)triCount,
                            subMeshCount   = subCount,
                            fileSize       = (long)mesh.bounds.size.magnitude, // 占用 placeholder,精确字节数需 Profiler API
                        };
                    }
                    else
                    {
                        data.meshFilterInfo = new MeshFilterInfo
                        {
                            meshName      = "",
                            vertexCount   = 0,
                            triangleCount = 0,
                            subMeshCount  = 0,
                            fileSize      = 0,
                        };
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[RemoteInspector] MeshFilter collect error: {ex.Message}");
                }
                return;
            }

            // UnityEngine.UI.Image — 用反射访问,避免硬依赖 UnityEngine.UI
            // (项目可能没引入 uGUI package,直接 typeof(Image) 会编译失败)
            try
            {
                if (comp.GetType().FullName == "UnityEngine.UI.Image")
                {
                    CollectImageViaReflection(comp, data);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemoteInspector] Image collect error: {ex.Message}");
            }

            // NGUI UITexture — 用反射访问,避免硬依赖 NGUI
            try
            {
                if (comp.GetType().FullName == "UITexture")
                {
                    CollectUITextureViaReflection(comp, data);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemoteInspector] UITexture collect error: {ex.Message}");
            }

            // ParticleSystemRenderer — 主材质已走 renderer.materials,这里只补拖尾材质
            try
            {
                if (comp is ParticleSystemRenderer psr)
                {
                    data.particleSystemRendererInfo = new ParticleSystemRendererInfo();
                    // trailMaterial 是单独字段;trails 启用时才有值
                    if (psr.trailMaterial != null)
                    {
                        data.particleSystemRendererInfo.trailMaterials.Add(CollectMaterial(psr.trailMaterial));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemoteInspector] ParticleSystemRenderer collect error: {ex.Message}");
            }

            // ParticleSystem — 运行状态(emission rate / isPlaying / particleCount)
            try
            {
                if (comp is ParticleSystem ps)
                {
                    float emitRate = 0f;
                    bool hasEmit = false;
                    try
                    {
                        var emission = ps.emission;
                        if (emission.enabled)
                        {
                            emitRate = emission.rateOverTime.constantMax;
                            hasEmit = true;
                        }
                    }
                    catch { }

                    data.particleSystemInfo = new ParticleSystemInfo
                    {
                        emitRate = hasEmit ? emitRate : 0f,
                        isPlaying = ps.isPlaying,
                        isStopped = ps.isStopped,
                        isPaused = ps.isPaused,
                        particleCount = ps.particleCount
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemoteInspector] ParticleSystem collect error: {ex.Message}");
            }
        }

        private void CollectImageViaReflection(Component comp, ComponentDetailData data)
        {
            // Image.sprite (UnityEngine.Sprite) — 通过反射
            var spriteProp = comp.GetType().GetProperty("sprite",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var colorProp = comp.GetType().GetProperty("color",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            UnityEngine.Object sprite = spriteProp?.GetValue(comp) as UnityEngine.Object;
            Color color = colorProp != null ? (Color)colorProp.GetValue(comp) : Color.white;

            string spriteName = "";
            string atlasName = "None";
            int mainTexId = 0;
            float pixelsPerUnit = 100f;
            float sw = 0f, sh = 0f;

            if (sprite != null)
            {
                spriteName = sprite.name;

                // Sprite.atlas (UnityEngine.U2D.SpriteAtlas,Unity 2017+)
                try
                {
                    var atlasProp = sprite.GetType().GetProperty("atlas");
                    var atlasVal = atlasProp?.GetValue(sprite) as UnityEngine.Object;
                    if (atlasVal != null) atlasName = atlasVal.name;
                }
                catch { }

                // Sprite.texture / Sprite.rect
                try
                {
                    var texProp = sprite.GetType().GetProperty("texture");
                    var tex = texProp?.GetValue(sprite) as Texture;
                    if (tex != null) mainTexId = tex.GetInstanceID();

                    var rectProp = sprite.GetType().GetProperty("rect");
                    var rect = (Rect)rectProp.GetValue(sprite);
                    sw = rect.width;
                    sh = rect.height;

                    var ppuProp = sprite.GetType().GetProperty("pixelsPerUnit");
                    pixelsPerUnit = (float)ppuProp.GetValue(sprite);
                }
                catch { }
            }

            data.imageInfo = new ImageInfo
            {
                spriteName            = spriteName,
                atlasName             = atlasName,
                mainTextureInstanceId = mainTexId,
                pixelsPerUnit         = pixelsPerUnit,
                spriteWidth           = sw,
                spriteHeight          = sh,
                colorHex              = ColorToHex(color),
            };
        }

        /// <summary>
        /// NGUI UITexture 摘要 — 通过反射访问 mainTexture / color,避免硬依赖 NGUI。
        /// UITexture 没有 sprite / atlas 概念,直接用 mainTexture(Texture 类型)。
        /// </summary>
        private void CollectUITextureViaReflection(Component comp, ComponentDetailData data)
        {
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

            // UITexture.mainTexture(Texture) — 反射
            var mainTexProp = comp.GetType().GetProperty("mainTexture", flags);
            Texture tex = mainTexProp != null ? mainTexProp.GetValue(comp) as Texture : null;

            // UITexture.color(Color) — 从 UIWidget 基类继承
            var colorProp = comp.GetType().GetProperty("color", flags);
            Color color = colorProp != null ? (Color)colorProp.GetValue(comp) : Color.white;

            int width = 0, height = 0, texId = 0;
            string texName = "(none)";
            string texTypeName = "";

            if (tex != null)
            {
                texName = tex.name;
                texTypeName = tex.GetType().Name;  // Texture2D / RenderTexture / Texture
                width = tex.width;
                height = tex.height;
                texId = tex.GetInstanceID();
            }

            data.uiTextureInfo = new UITextureInfo
            {
                textureName           = texName,
                textureTypeName       = texTypeName,
                width                 = width,
                height                = height,
                mainTextureInstanceId = texId,
                colorHex              = ColorToHex(color),
            };
        }

        private static string ColorToHex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}",
                (int)(c.r * 255f), (int)(c.g * 255f), (int)(c.b * 255f), (int)(c.a * 255f));
        }

        private MaterialInfo CollectMaterial(Material mat)
        {
            if (mat == null)
            {
                return new MaterialInfo
                {
                    name = "",
                    shaderName = "",
                    properties = new List<ShaderPropertyData>(),
                    enabledKeywords = new List<string>(),
                    shaderAllKeywords = new List<string>()
                };
            }

            var info = new MaterialInfo
            {
                name = mat.name,
                shaderName = mat.shader != null ? mat.shader.name : "",
                renderQueue = mat.renderQueue,
                propertyCount = mat.shader != null ? mat.shader.GetPropertyCount() : 0,
                properties = new List<ShaderPropertyData>(),
                mainTextureInstanceId = 0
            };

            if (mat.mainTexture != null)
                info.mainTextureInstanceId = mat.mainTexture.GetInstanceID();

            // 材质实例**实际启用**的 Shader Keywords(全局 + 多编译 keyword 都在这里)
            try
            {
                string[] kws = mat.shaderKeywords;
                info.enabledKeywords = kws != null ? new List<string>(kws) : new List<string>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemoteInspector] Material '{mat.name}' enabled-keyword read error: {ex.Message}");
                info.enabledKeywords = new List<string>();
            }

            // Shader 自身定义的**全部可用** keywords(从 Shader.keywordSpace.keywordNames)
            try
            {
                if (mat.shader != null)
                {
                    var keywordSpace = mat.shader.keywordSpace;
                    if (keywordSpace != null && keywordSpace.keywordNames != null)
                    {
                        info.shaderAllKeywords = new List<string>(keywordSpace.keywordNames);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemoteInspector] Material '{mat.name}' shader-all-keywords read error: {ex.Message}");
                info.shaderAllKeywords = new List<string>();
            }

            if (mat.shader != null)
            {
                for (int i = 0; i < mat.shader.GetPropertyCount(); i++)
                {
                    var prop = new ShaderPropertyData
                    {
                        name = mat.shader.GetPropertyName(i),
                        type = mat.shader.GetPropertyType(i),
                        typeName = mat.shader.GetPropertyType(i).ToString()
                    };

                    switch (prop.type)
                    {
                        case ShaderPropertyType.Color:
                            Color c = mat.GetColor(prop.name);
                            prop.colorValue = new float[] { c.r, c.g, c.b, c.a };
                            break;
                        case ShaderPropertyType.Vector:
                            Vector4 v = mat.GetVector(prop.name);
                            prop.vectorValue = new float[] { v.x, v.y, v.z, v.w };
                            break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                            prop.floatValue = mat.GetFloat(prop.name);
                            break;
                        case ShaderPropertyType.Texture:
                            Texture tex = mat.GetTexture(prop.name);
                            if (tex != null)
                                prop.textureInstanceId = tex.GetInstanceID();
                            break;
                    }

                    info.properties.Add(prop);
                }
            }

            return info;
        }

        private bool CollectCommonComponentFields(Component comp, List<SerializedFieldData> fields, bool detailed = false)
        {
            // 专门类型 — 强类型 API,显示友好字段名(不走反射)
            // 返回 true 表示已处理完毕,Collector 不会再走反射(避免重复显示 m_xxx 底层字段)

            var psr = comp as ParticleSystemRenderer;
            if (psr != null) { CollectParticleSystemRendererFields(psr, fields); return true; }

            var ps = comp as ParticleSystem;
            if (ps != null) { CollectParticleSystemFields(ps, fields); return true; }

            var smr = comp as SkinnedMeshRenderer;
            if (smr != null) { CollectSkinnedMeshRendererFields(smr, fields, detailed); return true; }

            var mr = comp as MeshRenderer;
            if (mr != null) { CollectMeshRendererFields(mr, fields); return true; }

            var trail = comp as TrailRenderer;
            if (trail != null) { CollectTrailRendererFields(trail, fields); return true; }

            var line = comp as LineRenderer;
            if (line != null) { CollectLineRendererFields(line, fields); return true; }

            var mf = comp as MeshFilter;
            if (mf != null) { CollectMeshFilterFields(mf, fields, detailed); return true; }

            // 其它专门类型(BoxCollider/Camera 等)
            var box = comp as BoxCollider;
            if (box != null)
            {
                fields.Add(MakeVector3Field("center", "Vector3", box.center));
                fields.Add(MakeVector3Field("size", "Vector3", box.size));
                fields.Add(MakeBoolField("isTrigger", box.isTrigger));
                return true;
            }

            var camera = comp as Camera;
            if (camera != null)
            {
                fields.Add(MakeIntField("cullingMask", "LayerMask", camera.cullingMask, "layermask"));
                fields.Add(MakeFloatField("fieldOfView", camera.fieldOfView));
                fields.Add(MakeFloatField("nearClipPlane", camera.nearClipPlane));
                fields.Add(MakeFloatField("farClipPlane", camera.farClipPlane));
                return true;
            }

            return false;
        }

        // ==================== 专门类型字段收集(强类型 API) ====================

        private void CollectRendererFields(Renderer r, List<SerializedFieldData> fields)
        {
            // 通用 Renderer 字段 — 所有 Renderer 子类共享
            // 用 try-catch 保护不同 Unity 版本下 API 差异
            // Unity 2017+: castShadows(bool) 已过时,改用 shadowCastingMode(enum)
            // Unity 2017+: motionVectors(bool) 已过时,改用 motionVectorGenerationMode(enum)
            try { fields.Add(MakeEnumField("shadowCastingMode", r.shadowCastingMode)); } catch { }
            try { fields.Add(MakeBoolField("receiveShadows", r.receiveShadows)); } catch { }
            try { fields.Add(MakeEnumField("motionVectorGenerationMode", r.motionVectorGenerationMode)); } catch { }
            try { fields.Add(MakeEnumField("lightProbeUsage", r.lightProbeUsage)); } catch { }
            try { fields.Add(MakeEnumField("reflectionProbeUsage", r.reflectionProbeUsage)); } catch { }
            try { fields.Add(MakeIntField("renderingLayerMask", "UInt32", (int)r.renderingLayerMask, "int")); } catch { }
            try { fields.Add(MakeIntField("rendererPriority", "Int32", r.rendererPriority, "int")); } catch { }
        }

        private void CollectMeshRendererFields(MeshRenderer mr, List<SerializedFieldData> fields)
        {
            CollectRendererFields(mr, fields);
            // 注: autoUVMaxDistance / autoUVMaxAngle 实际是 UnityEditor.MeshUtility 的
            //     静态方法,不是 MeshRenderer 属性,这里仅展示 instance 可用字段
#if UNITY_EDITOR
            try
            {
                object scaleInLightmap = GetPublicInstanceMemberValue(mr, "scaleInLightmap");
                if (scaleInLightmap is float scaleValue)
                    fields.Add(MakeFloatField("scaleInLightmap", scaleValue));
            }
            catch { }

            try
            {
                object stitchLightmapSeams = GetPublicInstanceMemberValue(mr, "stitchLightmapSeams");
                if (stitchLightmapSeams is bool stitchValue)
                    fields.Add(MakeBoolField("stitchLightmapSeams", stitchValue));
            }
            catch { }
#endif
#if UNITY_EDITOR
            try
            {
                // receiveGI 在 2022.3 是 enum(ReceiveGI),早期是 int
                object gi = GetPublicInstanceMemberValue(mr, "receiveGI");
                if (gi is Enum giEnum)
                    fields.Add(MakeEnumField("receiveGI", giEnum));
            }
            catch { }
#endif
        }

        private void CollectSkinnedMeshRendererFields(SkinnedMeshRenderer smr, List<SerializedFieldData> fields, bool detailed)
        {
            CollectRendererFields(smr, fields);
            try { fields.Add(MakeEnumField("quality", smr.quality)); } catch { }
            try { fields.Add(MakeBoolField("updateWhenOffscreen", smr.updateWhenOffscreen)); } catch { }

            // 详细模式:显示对象引用(sharedMesh / bones / rootBone)
            if (detailed)
            {
                try { fields.Add(MakeObjectField("sharedMesh", smr.sharedMesh)); } catch { }
                try
                {
                    if (smr.bones != null)
                    {
                        fields.Add(new SerializedFieldData
                        {
                            fieldName = "bones",
                            fieldType = "Transform[]",
                            stringValue = $"[{smr.bones.Length}]",
                            objectName = $"Transform[{smr.bones.Length}]",
                            isNull = false,
                            valueType = "object"
                        });
                    }
                }
                catch { }
                try { fields.Add(MakeObjectField("rootBone", smr.rootBone)); } catch { }
            }
        }

        private void CollectMeshFilterFields(MeshFilter mf, List<SerializedFieldData> fields, bool detailed)
        {
            if (!detailed) return;

            try
            {
                var mesh = mf.sharedMesh;
                if (mesh != null)
                {
                    fields.Add(new SerializedFieldData
                    {
                        fieldName = "sharedMesh",
                        fieldType = "Mesh",
                        stringValue = $"{mesh.name}  (verts={mesh.vertexCount})",
                        objectName = mesh.name,
                        isNull = false,
                        valueType = "object"
                    });
                }
                else
                {
                    fields.Add(new SerializedFieldData
                    {
                        fieldName = "sharedMesh",
                        fieldType = "Mesh",
                        stringValue = "None",
                        objectName = "None",
                        isNull = true,
                        valueType = "object"
                    });
                }
            }
            catch { }
        }

        private void CollectParticleSystemFields(ParticleSystem ps, List<SerializedFieldData> fields)
        {
            // ParticleSystem 的 MainModule / Emission 等都是 struct,
            // 反射只能看到 m_Particles 之类的底层字段,无法直接拿到 startColor / startSize 等。
            // 必须走 ps.main / ps.emission 强类型 API。
            try
            {
                var main = ps.main;
                fields.Add(MakeFloatField("duration", main.duration));
                fields.Add(MakeBoolField("loop", main.loop));
                fields.Add(MakeBoolField("playOnAwake", main.playOnAwake));
                fields.Add(MakeColorField("startColor", main.startColor.color));
                // startSize / startLifetime / startSpeed / gravityModifier 都是 MinMaxCurve,取 constant
                fields.Add(MakeMinMaxCurveField("startSize", main.startSize));
                fields.Add(MakeMinMaxCurveField("startLifetime", main.startLifetime));
                fields.Add(MakeMinMaxCurveField("startSpeed", main.startSpeed));
                fields.Add(MakeMinMaxCurveField("gravityModifier", main.gravityModifier));
                fields.Add(MakeIntField("maxParticles", "Int32", main.maxParticles, "int"));
                fields.Add(MakeFloatField("simulationSpeed", main.simulationSpeed));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemoteInspector] ParticleSystem main module collect error: {ex.Message}");
            }
        }

        private void CollectParticleSystemRendererFields(ParticleSystemRenderer psr, List<SerializedFieldData> fields)
        {
            CollectRendererFields(psr, fields);
            try { fields.Add(MakeEnumField("renderMode", psr.renderMode)); } catch { }
            try { fields.Add(MakeEnumField("alignment", psr.alignment)); } catch { }
            // 注: ParticleSystemRenderer 没有独立的 sortingMode 属性,
            //     排序设置继承自 Renderer(sortingLayerID / sortingLayerName / sortingOrder)
            try { fields.Add(MakeIntField("sortingOrder", "Int32", psr.sortingOrder, "int")); } catch { }
            try { fields.Add(MakeFloatField("sortingFudge", psr.sortingFudge)); } catch { }
            try { fields.Add(MakeEnumField("maskInteraction", psr.maskInteraction)); } catch { }
        }

        private void CollectTrailRendererFields(TrailRenderer tr, List<SerializedFieldData> fields)
        {
            CollectRendererFields(tr, fields);
            try { fields.Add(MakeFloatField("time", tr.time)); } catch { }
            try { fields.Add(MakeFloatField("startWidth", tr.startWidth)); } catch { }
            try { fields.Add(MakeFloatField("endWidth", tr.endWidth)); } catch { }
            try { fields.Add(MakeFloatField("minVertexDistance", tr.minVertexDistance)); } catch { }
            try { fields.Add(MakeBoolField("autodestruct", tr.autodestruct)); } catch { }
            try { fields.Add(MakeBoolField("emitting", tr.emitting)); } catch { }
        }

        private void CollectLineRendererFields(LineRenderer lr, List<SerializedFieldData> fields)
        {
            CollectRendererFields(lr, fields);
            try { fields.Add(MakeFloatField("startWidth", lr.startWidth)); } catch { }
            try { fields.Add(MakeFloatField("endWidth", lr.endWidth)); } catch { }
            try { fields.Add(MakeIntField("positionCount", "Int32", lr.positionCount, "int")); } catch { }
            try { fields.Add(MakeBoolField("useWorldSpace", lr.useWorldSpace)); } catch { }
            try { fields.Add(MakeIntField("numCapVertices", "Int32", lr.numCapVertices, "int")); } catch { }
            try { fields.Add(MakeIntField("numCornerVertices", "Int32", lr.numCornerVertices, "int")); } catch { }
            try { fields.Add(MakeEnumField("alignment", lr.alignment)); } catch { }
            try { fields.Add(MakeEnumField("textureMode", lr.textureMode)); } catch { }
        }

        private SerializedFieldData MakeBoolField(string name, bool value)
        {
            return new SerializedFieldData
            {
                fieldName = name,
                fieldType = "Boolean",
                stringValue = value.ToString(),
                boolValue = value,
                valueType = "bool"
            };
        }

        private SerializedFieldData MakeIntField(string name, string type, int value, string valueType)
        {
            return new SerializedFieldData
            {
                fieldName = name,
                fieldType = type,
                stringValue = value.ToString(),
                intValue = value,
                doubleValue = value,
                isNumeric = true,
                valueType = valueType
            };
        }

        private SerializedFieldData MakeFloatField(string name, float value)
        {
            return new SerializedFieldData
            {
                fieldName = name,
                fieldType = "Single",
                stringValue = value.ToString("F4"),
                doubleValue = value,
                isNumeric = true,
                valueType = "number"
            };
        }

#if UNITY_EDITOR
        private static object GetPublicInstanceMemberValue(object target, string memberName)
        {
            if (target == null || string.IsNullOrEmpty(memberName))
                return null;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            Type type = target.GetType();

            PropertyInfo property = type.GetProperty(memberName, flags);
            if (property != null)
                return property.GetValue(target, null);

            FieldInfo field = type.GetField(memberName, flags);
            if (field != null)
                return field.GetValue(target);

            return null;
        }
#endif

        private SerializedFieldData MakeVector3Field(string name, string type, Vector3 value)
        {
            return new SerializedFieldData
            {
                fieldName = name,
                fieldType = type,
                stringValue = $"({value.x:F3}, {value.y:F3}, {value.z:F3})",
                x = value.x,
                y = value.y,
                z = value.z,
                valueType = "vector3"
            };
        }

        private SerializedFieldData MakeEnumField(string name, Enum value)
        {
            Type t = value != null ? value.GetType() : null;
            return new SerializedFieldData
            {
                fieldName = name,
                fieldType = t != null ? t.Name : "Enum",
                stringValue = value != null ? value.ToString() : "null",
                intValue = value != null ? Convert.ToInt32(value) : 0,
                enumValue = value != null ? Convert.ToInt32(value) : 0,
                enumNames = t != null ? Enum.GetNames(t) : new string[0],
                valueType = "enum"
            };
        }

        private SerializedFieldData MakeColorField(string name, Color value)
        {
            return new SerializedFieldData
            {
                fieldName = name,
                fieldType = "Color",
                stringValue = $"RGBA({value.r:F2},{value.g:F2},{value.b:F2},{value.a:F2})",
                x = value.r,
                y = value.g,
                z = value.b,
                w = value.a,
                valueType = "color"
            };
        }

        private SerializedFieldData MakeObjectField(string name, UnityEngine.Object obj)
        {
            bool isNull = obj == null;
            Texture tex = obj as Texture;
            return new SerializedFieldData
            {
                fieldName = name,
                fieldType = obj != null ? obj.GetType().Name : "Object",
                stringValue = obj != null ? obj.name : "None",
                objectName = obj != null ? obj.name : "None",
                isNull = isNull,
                textureInstanceId = tex != null ? tex.GetInstanceID() : 0,
                valueType = tex != null ? "texture" : "object"
            };
        }

        private SerializedFieldData MakeMinMaxCurveField(string name, ParticleSystem.MinMaxCurve curve)
        {
            // 取 constant(适用于 Constant 模式),非 Constant 模式显示模式 + 范围
            float value;
            string display;
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    value = curve.constant;
                    display = curve.constant.ToString("F4");
                    break;
                case ParticleSystemCurveMode.Curve:
                    value = curve.curveMultiplier;
                    display = $"Curve x{curve.curveMultiplier:F2}";
                    break;
                case ParticleSystemCurveMode.TwoCurves:
                    value = curve.curveMultiplier;
                    display = $"TwoCurves x{curve.curveMultiplier:F2}";
                    break;
                case ParticleSystemCurveMode.TwoConstants:
                    value = curve.constantMin;
                    display = $"[{curve.constantMin:F3}, {curve.constantMax:F3}]";
                    break;
                default:
                    value = curve.constant;
                    display = curve.constant.ToString("F4");
                    break;
            }
            return new SerializedFieldData
            {
                fieldName = name,
                fieldType = "Single",
                stringValue = display,
                doubleValue = value,
                isNumeric = true,
                valueType = "number"
            };
        }

        private void CollectSerializedFields(Component comp, List<SerializedFieldData> fields, bool detailed = false)
        {
            Type type = comp.GetType();

            // 获取缓存的字段信息
            if (!_fieldCache.TryGetValue(type, out FieldInfo[] allFields))
            {
                allFields = GetSerializableCandidateFields(type);
                _fieldCache[type] = allFields;

                // 预计算哪些字段需要序列化
                var indices = new List<int>();
                for (int i = 0; i < allFields.Length; i++)
                {
                    if (ShouldCollectField(allFields[i]))
                        indices.Add(i);
                }
                _serializedFieldIndices[type] = indices;
            }

            var indicesToCollect = _serializedFieldIndices[type];

            foreach (int idx in indicesToCollect)
            {
                FieldInfo field = allFields[idx];

                string valueTypeName = GetValueTypeName(field.FieldType);

                var data = new SerializedFieldData
                {
                    fieldName = field.Name,
                    fieldType = GetCleanTypeName(field.FieldType),
                    valueType = valueTypeName
                };

                try
                {
                    // 用 ReflectionCache(IL.Emit fast getter)— 比 FieldInfo.GetValue 快 ~50x
                    // 50k 节点 × 5 字段 × 5 组件 = 1.25M 次调用,优化前 ~625ms → 优化后 ~12ms
                    object value = ReflectionCache.GetFieldValue(field, comp);
                    SetFieldValue(data, value, field.FieldType, detailed);
                }
                catch (Exception)
                {
                    data.stringValue = "[Error reading value]";
                }

                if (!ContainsField(fields, data.fieldName))
                    fields.Add(data);
            }
        }

        private bool ContainsField(List<SerializedFieldData> fields, string fieldName)
        {
            if (fields == null || string.IsNullOrEmpty(fieldName))
                return false;

            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i] != null && fields[i].fieldName == fieldName)
                    return true;
            }
            return false;
        }

        private bool IsUnsupportedType(Type t)
        {
            return t.IsPointer || t == typeof(IntPtr) || t == typeof(UIntPtr)
                || t == typeof(Delegate)
                || t.IsSubclassOf(typeof(Delegate));
        }

        private bool ShouldCollectField(FieldInfo field)
        {
            if (field == null)
                return false;

            if (field.IsStatic || field.IsInitOnly || field.IsNotSerialized)
                return false;

            if (field.GetCustomAttribute<NonSerializedAttribute>() != null)
                return false;

            bool isSerializedField = field.IsPublic || field.GetCustomAttribute<SerializeField>() != null;
            return isSerializedField && !IsUnsupportedType(field.FieldType);
        }

        private FieldInfo[] GetSerializableCandidateFields(Type type)
        {
            var fields = new List<FieldInfo>();
            const BindingFlags flags = BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly;

            for (Type current = type;
                 current != null
                 && current != typeof(MonoBehaviour)
                 && current != typeof(Behaviour)
                 && current != typeof(Component)
                 && current != typeof(UnityEngine.Object);
                 current = current.BaseType)
            {
                // 缓存:避免每节点重复 GetFields(flags)— 海量场景 50k 节点 × 5 层 baseType = 250k 次反射
                // 缓存后第二次起走 ConcurrentDictionary 查找,~10ns/次
                if (!s_baseTypeFieldsCache.TryGetValue(current, out var baseFields))
                {
                    baseFields = current.GetFields(flags);
                    s_baseTypeFieldsCache[current] = baseFields;
                }
                fields.AddRange(baseFields);
            }

            return fields.ToArray();
        }

        private string GetCleanTypeName(Type t)
        {
            if (t.IsGenericType)
            {
                var genericType = t.GetGenericTypeDefinition();
                var args = t.GetGenericArguments();
                string argsStr = string.Join(", ", Array.ConvertAll(args, a => GetCleanTypeName(a)));
                return genericType.Name.Split('`')[0] + "<" + argsStr + ">";
            }
            return t.Name;
        }

        private string GetValueTypeName(Type t)
        {
            if (IsNumericType(t)) return "number";
            if (t == typeof(int)) return "int";
            if (t == typeof(float)) return "float";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string";
            if (t == typeof(Vector2)) return "vector2";
            if (t == typeof(Vector3)) return "vector3";
            if (t == typeof(Vector4)) return "vector4";
            if (t == typeof(Color)) return "color";
            if (t.IsEnum) return "enum";
            if (t.IsArray) return "array";
            return "object";
        }

        /// <summary>
        /// 判断是否为基本数值类型（非详细模式下显示）
        /// </summary>
        private static bool IsBasicValueType(string valueType)
        {
            switch (valueType)
            {
                case "number":
                case "int":
                case "float":
                case "bool":
                case "string":
                case "vector2":
                case "vector3":
                case "vector4":
                case "color":
                case "enum":
                    return true;
                default:
                    return false;
            }
        }

        private void SetFieldValue(SerializedFieldData data, object value, Type fieldType, bool detailed = false)
        {
            data.isNumeric = IsNumericType(fieldType);
            data.isNull = IsNullValue(value);

            if (data.isNull)
            {
                data.stringValue = "None";
                data.objectName = "None";
                return;
            }

            if (data.isNumeric)
            {
                if (fieldType == typeof(float))
                {
                    float floatValue = (float)value;
                    data.doubleValue = floatValue;
                    data.stringValue = floatValue.ToString("F4");
                }
                else if (fieldType == typeof(double))
                {
                    double doubleValue = (double)value;
                    data.doubleValue = doubleValue;
                    data.stringValue = doubleValue.ToString("F4");
                }
                else
                {
                    if (value is IConvertible)
                    {
                        try { data.intValue = Convert.ToInt64(value); }
                        catch { }
                    }
                    data.stringValue = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                }
                return;
            }

            if (fieldType == typeof(int)) { data.intValue = (int)value; data.stringValue = ((int)value).ToString(); }
            else if (fieldType == typeof(long)) { data.intValue = (long)value; data.stringValue = ((long)value).ToString(); }
            else if (fieldType == typeof(float)) { data.doubleValue = (float)value; data.stringValue = ((float)value).ToString("F4"); }
            else if (fieldType == typeof(double)) { data.doubleValue = (double)value; data.stringValue = ((double)value).ToString("F4"); }
            else if (fieldType == typeof(bool)) { data.boolValue = (bool)value; data.stringValue = ((bool)value).ToString(); }
            else if (fieldType == typeof(string)) { data.stringValue = (string)value; }
            else if (fieldType == typeof(Vector2)) { Vector2 v = (Vector2)value; data.x = v.x; data.y = v.y; data.stringValue = $"({v.x:F3}, {v.y:F3})"; }
            else if (fieldType == typeof(Vector3)) { Vector3 v = (Vector3)value; data.x = v.x; data.y = v.y; data.z = v.z; data.stringValue = $"({v.x:F3}, {v.y:F3}, {v.z:F3})"; }
            else if (fieldType == typeof(Vector4)) { Vector4 v = (Vector4)value; data.x = v.x; data.y = v.y; data.z = v.z; data.w = v.w; data.stringValue = $"({v.x:F3}, {v.y:F3}, {v.z:F3}, {v.w:F3})"; }
            else if (fieldType == typeof(Color)) { Color c = (Color)value; data.x = c.r; data.y = c.g; data.z = c.b; data.w = c.a; data.stringValue = $"RGBA({c.r:F2},{c.g:F2},{c.b:F2},{c.a:F2})"; }
            else if (fieldType.IsEnum) { data.enumValue = System.Convert.ToInt32(value); data.enumNames = Enum.GetNames(fieldType); data.stringValue = value.ToString(); }
            else if (value is UnityEngine.Object obj)
            {
                data.stringValue = obj != null ? obj.name : "None";
                data.objectName = obj != null ? obj.name : "None";
                // 纹理类型: 捕获 InstanceID 供缩略图显示
                if (detailed && obj is Texture)
                {
                    data.textureInstanceId = obj.GetInstanceID();
                    data.valueType = "texture";
                }
            }
            else { data.stringValue = "not null"; }
        }

        private bool IsNullValue(object value)
        {
            var unityObject = value as UnityEngine.Object;
            if (!ReferenceEquals(unityObject, null))
                return unityObject == null;

            return value == null;
        }

        private bool IsNumericType(Type t)
        {
            if (t.IsEnum)
                return false;

            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return true;
                default:
                    return false;
            }
        }

        private GameObject FindGameObjectByInstanceId(int instanceId)
        {
            // 使用缓存查找，避免 FindObjectsOfType 全局扫描
            return GameObjectCache.Find(instanceId);
        }
    }
}
 
