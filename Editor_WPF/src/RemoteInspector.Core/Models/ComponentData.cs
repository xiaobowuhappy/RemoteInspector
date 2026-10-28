// ============================================================================
// ComponentData.cs
// 组件数据结构 - GameObject组件详情数据
// Editor层 - 数据定义
// ============================================================================


using System;
using System.Collections.Generic;


namespace RemoteInspector.Core.Models
{
    /// <summary>
    /// Transform组件数据
    /// </summary>
    [Serializable]
    public class TransformData
    {
        // Local Space
        public Vector3Data localPosition;
        public Vector3Data localRotation;
        public Vector3Data localScale;

        // World Space
        public Vector3Data position;
        public Vector3Data rotation;
        public Vector3Data scale;

        public int childCount;
        public int siblingIndex;


    }

    /// <summary>
    /// Vector3简化数据（避免Unity类型在JsonUtility中的问题）
    /// </summary>
    [Serializable]
    public class Vector3Data
    {
        public float x, y, z;

        public Vector3Data() { }
        public Vector3Data(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }


    }

    /// <summary>
    /// 单个序列化字段信息
    /// </summary>
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

        /// <summary>值的类型：int, float, string, bool, enum, vector2, vector3, vector4, color, texture, object</summary>
        public string valueType;

        /// <summary>当字段为Texture类型时，纹理的InstanceID（详细模式）</summary>
        public int textureInstanceId;

        /// <summary>当字段为UnityObject类型时，对象名称（详细模式）</summary>
        public string objectName;
    }

    /// <summary>
    /// 单个组件的完整数据
    /// </summary>
    [Serializable]
    public class ComponentDetailData
    {
        /// <summary>组件类型名</summary>
        public string typeName;

        /// <summary>组件名称（有时是Unity内置名称，如 "Transform"）</summary>
        public string displayName;

        /// <summary>是否启用</summary>
        public bool enabled;
        public bool hasEnabled;

        /// <summary>如果是Transform，填TransformData</summary>
        public TransformData transformData;

        /// <summary>如果是Renderer，填材质信息</summary>
        public List<MaterialInfo> materials;

        /// <summary>如果是MeshFilter，填 Mesh 基础信息(name/三角形/顶点/subMesh)</summary>
        public MeshFilterInfo meshFilterInfo;

        /// <summary>如果是 UnityEngine.UI.Image，填 Sprite / Atlas 信息</summary>
        public ImageInfo imageInfo;

        /// <summary>如果是 ParticleSystemRenderer，填拖尾材质(主材质走 data.materials)</summary>
        public ParticleSystemRendererInfo particleSystemRendererInfo;

        /// <summary>如果是 ParticleSystem，填运行状态(emit rate/isPlaying/particleCount)</summary>
        public ParticleSystemInfo particleSystemInfo;

        /// <summary>如果是 NGUI UITexture，填 mainTexture 摘要</summary>
        public UITextureInfo uiTextureInfo;

        /// <summary>序列化字段列表</summary>
        public List<SerializedFieldData> serializedFields;

        /// <summary>
        /// 快速构建组件数据
        /// </summary>


    }

    /// <summary>
    /// 材质信息
    /// </summary>
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
        /// 当前已启用的 Shader Keywords(Inspector 摘要显示)。
        /// 注:必须用 <c>List&lt;string&gt;</c> 而非 <c>string[]</c> —
        /// Unity 的 <c>JsonUtility</c> 对 <c>string[]</c> 序列化不可靠,
        /// 会静默丢弃为 null。List&lt;string&gt; 是 JsonUtility 明确支持的格式。
        /// </summary>
        public List<string> enabledKeywords = new List<string>();

        /// <summary>
        /// Shader 自身定义的 **全部可用** keywords(<c>Shader.keywordSpace.keywordNames</c>)。
        /// 区别于 <see cref="enabledKeywords"/>:后者是材质实例**实际启用**的子集。
        /// </summary>
        public List<string> shaderAllKeywords = new List<string>();


    }

    /// <summary>
    /// Shader属性数据
    /// </summary>
    [Serializable]
    public class ShaderPropertyData
    {
        public string name;
        public string typeName;
        public int type;

        // 值存储（根据类型使用不同字段）
        public float floatValue;
        public float[] colorValue;
        public float[] vectorValue;
        public int textureInstanceId;
    }

    /// <summary>
    /// 节点详情响应
    /// </summary>
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

    /// <summary>
    /// MeshFilter 关联的 Mesh 基础信息(供 UI 摘要显示)
    /// </summary>
    [Serializable]
    public class MeshFilterInfo
    {
        public string meshName;
        public int vertexCount;
        public int triangleCount;
        public int subMeshCount;
        public long fileSize;
    }

    /// <summary>
    /// UnityEngine.UI.Image 的 Sprite / Atlas 基础信息
    /// </summary>
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

    /// <summary>
    /// ParticleSystemRenderer 额外信息(主材质已走 data.materials,这里只补拖尾材质)
    /// </summary>
    [Serializable]
    public class ParticleSystemRendererInfo
    {
        /// <summary>拖尾材质列表(可能为 0/1 个;null 时为空数组)</summary>
        public List<MaterialInfo> trailMaterials = new List<MaterialInfo>();
    }

    /// <summary>
    /// ParticleSystem 运行状态
    /// </summary>
    [Serializable]
    public class ParticleSystemInfo
    {
        /// <summary>当前 emit rate (粒子/秒);若为曲线则取 constantMax</summary>
        public float emitRate;

        /// <summary>是否正在播放</summary>
        public bool isPlaying;

        /// <summary>当前存活粒子数</summary>
        public int particleCount;

        /// <summary>是否已停止</summary>
        public bool isStopped;

        /// <summary>是否已暂停</summary>
        public bool isPaused;
    }

    /// <summary>
    /// NGUI UITexture 摘要 — mainTexture(name/size/format) + color
    /// </summary>
    [Serializable]
    public class UITextureInfo
    {
        public string textureName;
        public string textureTypeName;  // Texture2D / RenderTexture / Texture ...
        public int width;
        public int height;
        public int mainTextureInstanceId;
        public string colorHex;
    }
}

