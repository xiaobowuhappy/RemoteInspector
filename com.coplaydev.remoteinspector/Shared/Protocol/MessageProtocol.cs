// ============================================================================
// MessageProtocol.cs
// TCP消息协议定义 - 包头结构、协议常量、消息包裹
// Shared层 - 协议定义
// ============================================================================

using System;
using RemoteInspector.Commands;

namespace RemoteInspector.Protocol
{
    /// <summary>
    /// TCP消息协议常量
    /// 包头格式：4字节(int32)长度 + 2字节(ushort)命令ID + 2字节(ushort)序列号 = 8字节
    /// </summary>
    public static class MessageProtocol
    {
        /// <summary>固定包头字节数</summary>
        public const int HEADER_SIZE = 8;

        /// <summary>包体最大大小（16MB，防止恶意数据）</summary>
        public const int MAX_BODY_SIZE = 16 * 1024 * 1024;

        /// <summary>心跳间隔（秒）</summary>
        public const float HEARTBEAT_INTERVAL = 5f;

        /// <summary>心跳超时时间（秒），超过此时间没收到心跳则断开</summary>
        public const float HEARTBEAT_TIMEOUT = 30f;

        /// <summary>纹理分块大小（字节）</summary>
        public const int TEXTURE_CHUNK_SIZE = 32 * 1024; // 32KB

        /// <summary>文件上传分块大小（字节）</summary>
        public const int FILE_CHUNK_SIZE = 64 * 1024; // 64KB

        /// <summary>纹理压缩质量（1-100）</summary>
        public const int TEXTURE_JPG_QUALITY = 85;
    }

    /// <summary>
    /// TCP消息包（包含包头和包体）
    /// </summary>
    [Serializable]
    public class MessagePacket
    {
        /// <summary>命令ID</summary>
        public ushort commandId;

        /// <summary>序列号（用于匹配请求/响应）</summary>
        public ushort sequence;

        /// <summary>包体JSON数据(老路径,NODE_TREE_RESPONSE / NODE_DETAIL_RESPONSE 等用)</summary>
        public string bodyJson;

        /// <summary>
        /// 包体二进制数据(2026-06-25 新增 — NODE_TREE_RESPONSE_BINARY / NODE_DETAIL_RESPONSE_BINARY 用)
        /// 二进制协议比 JSON 体积小 ~80%,序列化/反序列化快 ~70%。
        /// 发送时序列化到 bytes,接收时反序列化为具体类型。
        /// 注意:bodyJson 和 bodyBytes 互斥,根据 commandId 选其一。
        /// </summary>
        public byte[] bodyBytes;

        /// <summary>
        /// 构造消息包
        /// </summary>
        public MessagePacket() { }

        /// <summary>
        /// 构造消息包(JSON body)
        /// </summary>
        public MessagePacket(Command cmd, ushort seq, string body = null)
        {
            commandId = (ushort)cmd;
            sequence = seq;
            bodyJson = body;
        }

        /// <summary>
        /// 构造消息包(Binary body) — 海量大场景性能优化第一波(2026-06-25)
        /// </summary>
        public MessagePacket(Command cmd, ushort seq, byte[] body)
        {
            commandId = (ushort)cmd;
            sequence = seq;
            bodyBytes = body;
        }

        /// <summary>
        /// 获取命令枚举
        /// </summary>
        public Command GetCommand()
        {
            return (Command)commandId;
        }

        /// <summary>是否二进制 command — 路由到 BinaryReader/Serializer</summary>
        public bool IsBinaryCommand
        {
            get
            {
                switch ((Command)commandId)
                {
                    case Command.NODE_TREE_RESPONSE_BINARY:
                    case Command.NODE_DETAIL_RESPONSE_BINARY:
                        return true;
                    default:
                        return false;
                }
            }
        }
    }

    /// <summary>
    /// 请求基础消息（用于请求类命令的通用结构）
    /// </summary>
    [Serializable]
    public class RequestBase
    {
        public ushort sequence;
    }

    /// <summary>
    /// 获取节点详情请求
    /// </summary>
    [Serializable]
    public class GetNodeDetailRequest
    {
        public ushort sequence;
        public int instanceId;
        /// <summary>是否请求详细数据（含对象引用、纹理InstanceID等）</summary>
        public bool detailed;
    }

    /// <summary>
    /// 纹理请求
    /// </summary>
    [Serializable]
    public class GetTextureRequest
    {
        public ushort sequence;
        public int instanceId;
        /// <summary>是否强制重新发送（忽略缓存）</summary>
        public bool forceRefresh;
    }

    [Serializable]
    public class SetPropertyRequest
    {
        public ushort sequence;
        public int instanceId;
        public string componentType;
        public int componentIndex;
        public string propertyPath;
        public string valueType;
        public string stringValue;
        public double numberValue;
        public bool boolValue;
        public int intValue;
        public float x;
        public float y;
        public float z;
        public float w;
    }

    [Serializable]
    public class SetPropertyResponse
    {
        public ushort sequence;
        public int instanceId;
        public bool success;
        public string error;
    }

    /// <summary>
    /// 纹理响应
    /// </summary>
    [Serializable]
    public class TextureResponse
    {
        public ushort sequence;
        public int instanceId;
        /// <summary>纹理名称</summary>
        public string name;
        /// <summary>是否已缓存（true表示编辑器端已有此纹理，无需传输）</summary>
        public bool cached;
        /// <summary>当前块索引（从0开始）</summary>
        public int chunkIndex;
        /// <summary>总块数</summary>
        public int totalChunks;
        /// <summary>Base64编码的JPEG数据（cached=false时有效）</summary>
        public string data;
        /// <summary>纹理宽度</summary>
        public int width;
        /// <summary>纹理高度</summary>
        public int height;
        /// <summary>是否为最后一帧数据</summary>
        public bool isLast;
        /// <summary>ok / unread / not_found / encode_error。</summary>
        public string status;
    }

    /// <summary>
    /// 文件上传开始请求
    /// </summary>
    [Serializable]
    public class FileUploadStartRequest
    {
        public ushort sequence;
        /// <summary>相对路径（相对于Application.persistentDataPath）</summary>
        public string relativePath;
        /// <summary>文件名</summary>
        public string fileName;
        /// <summary>文件总大小（字节）</summary>
        public long totalSize;
        /// <summary>数据块总数</summary>
        public int totalChunks;
        /// <summary>文件MD5校验和</summary>
        public string md5;
    }

    /// <summary>
    /// 文件上传数据块
    /// </summary>
    [Serializable]
    public class FileUploadChunkRequest
    {
        public ushort sequence;
        /// <summary>块索引（从0开始）</summary>
        public int chunkIndex;
        /// <summary>数据块总数</summary>
        public int totalChunks;
        /// <summary>Base64编码的文件数据</summary>
        public string data;
    }

    /// <summary>
    /// 文件上传完成请求
    /// </summary>
    [Serializable]
    public class FileUploadEndRequest
    {
        public ushort sequence;
        /// <summary>相对路径</summary>
        public string relativePath;
    }

    /// <summary>
    /// 文件上传ACK响应
    /// </summary>
    [Serializable]
    public class FileUploadAck
    {
        public ushort sequence;
        /// <summary>是否成功</summary>
        public bool success;
        /// <summary>错误信息（success=false时填写）</summary>
        public string error;
        /// <summary>文件完整路径（success=true时填写）</summary>
        public string fullPath;
    }

    /// <summary>
    /// 心跳消息
    /// </summary>
    [Serializable]
    public class HeartbeatMessage
    {
        public ushort sequence;
        /// <summary>客户端时间戳</summary>
        public long clientTimestamp;
    }

    /// <summary>
    /// 客户端握手消息（连接后立即发送，携带设备标识信息）
    /// </summary>
    [Serializable]
    public class ClientHelloRequest
    {
        public ushort sequence;
        /// <summary>会话唯一标识（GUID）</summary>
        public string sessionId;
        /// <summary>设备名称（手机设备型号，如 "iPhone 15 Pro"）</summary>
        public string deviceName;
        /// <summary>平台信息（如 "Android"、"iOS"、"Windows"）</summary>
        public string platform;
        /// <summary>Unity版本</summary>
        public string unityVersion;
        /// <summary>操作系统版本</summary>
        public string systemVersion;
    }

    /// <summary>
    /// 帧率统计数据请求
    /// </summary>
    [Serializable]
    public class GetFrameStatsRequest
    {
        public ushort sequence;
    }

    // ===================== RPC 远程调用消息 =====================

    /// <summary>
    /// RPC 调用请求（Editor -> Runtime）
    /// </summary>
    [Serializable]
    public class RpcCallRequest
    {
        public ushort sequence;
        /// <summary>目标方法名（需客户端已注册）</summary>
        public string methodName;
        /// <summary>JSON数组格式的参数列表</summary>
        public string argsJson;
        /// <summary>超时时间（毫秒），0=使用默认5秒</summary>
        public int timeoutMs;
    }

    /// <summary>
    /// RPC 调用响应（Runtime -> Editor）
    /// </summary>
    [Serializable]
    public class RpcCallResponse
    {
        public ushort sequence;
        /// <summary>对应请求的方法名（用于匹配）</summary>
        public string methodName;
        /// <summary>是否成功执行</summary>
        public bool success;
        /// <summary>返回值（JSON格式），success=true时有效</summary>
        public string resultJson;
        /// <summary>错误信息，success=false时有效</summary>
        public string error;
        /// <summary>执行耗时（毫秒）</summary>
        public long executionTimeMs;
    }

    /// <summary>
    /// RPC 注册列表请求（查询可用函数）
    /// </summary>
    [Serializable]
    public class RpcListRequest
    {
        public ushort sequence;
    }

    /// <summary>
    /// RPC 注册函数描述
    /// </summary>
    [Serializable]
    public class RpcMethodInfo
    {
        /// <summary>方法名（调用标识）</summary>
        public string name;
        /// <summary>参数签名描述，如 "(string message, int count)"</summary>
        public string signature;
        /// <summary>方法描述（用于UI展示）</summary>
        public string description;
    }

    /// <summary>
    /// RPC 注册列表响应
    /// </summary>
    [Serializable]
    public class RpcListResponse
    {
        public ushort sequence;
        /// <summary>可用函数列表</summary>
        public RpcMethodInfo[] methods;
    }

    // ===================== 日志转发消息 =====================

    /// <summary>
    /// 日志消息类型
    /// </summary>
    public enum LogType
    {
        /// <summary>普通日志 (Debug.Log)</summary>
        Log = 0,
        /// <summary>警告 (Debug.LogWarning)</summary>
        Warning = 1,
        /// <summary>错误 (Debug.LogError)</summary>
        Error = 2,
        /// <summary>异常 (Debug.LogException)</summary>
        Exception = 3
    }

    /// <summary>
    /// 日志消息（Runtime -> Editor）
    /// 客户端将 Unity 的 Debug.Log/LogWarning/LogError/LogException
    /// 实时转发给编辑器进行监听
    /// </summary>
    [Serializable]
    public class LogMessage
    {
        public ushort sequence;
        /// <summary>日志类型</summary>
        public int logType;
        /// <summary>日志内容</summary>
        public string message;
        /// <summary>堆栈跟踪（仅 Error/Exception 类型有效）</summary>
        public string stackTrace;
        /// <summary>发送时间戳（Unix毫秒）</summary>
        public long timestamp;
    }

    /// <summary>
    /// 日志批量消息（多条日志打包发送，减少网络开销）
    /// </summary>
    [Serializable]
    public class LogBatchMessage
    {
        public ushort sequence;
        /// <summary>批量日志条目</summary>
        public LogMessage[] entries;
    }

    // ===================== 文件浏览消息 =====================

    /// <summary>
    /// 文件条目信息（目录或文件）
    /// </summary>
    [Serializable]
    public class FileEntry
    {
        /// <summary>名称（不含路径）</summary>
        public string name;
        /// <summary>相对于 persistentDataPath 的路径（使用 / 分隔）</summary>
        public string relativePath;
        /// <summary>是否为目录</summary>
        public bool isDirectory;
        /// <summary>文件大小（字节），目录为 0</summary>
        public long size;
        /// <summary>最后修改时间（Unix 毫秒时间戳）</summary>
        public long lastModified;
    }

    /// <summary>
    /// 列出目录内容请求（Editor -> Runtime）
    /// </summary>
    [Serializable]
    public class FileBrowseListRequest
    {
        public ushort sequence;
        /// <summary>相对于 persistentDataPath 的路径，空字符串表示根目录</summary>
        public string relativePath;
    }

    /// <summary>
    /// 列出目录内容响应（Runtime -> Editor）
    /// </summary>
    [Serializable]
    public class FileBrowseListResponse
    {
        public ushort sequence;
        /// <summary>是否成功</summary>
        public bool success;
        /// <summary>错误信息</summary>
        public string error;
        /// <summary>当前目录相对路径</summary>
        public string relativePath;
        /// <summary>persistentDataPath 绝对路径（首次返回，供调试）</summary>
        public string persistentDataPath;
        /// <summary>目录/文件列表</summary>
        public FileEntry[] entries;
    }

    /// <summary>
    /// 创建文件夹请求（Editor -> Runtime）
    /// </summary>
    [Serializable]
    public class FileBrowseMkdirRequest
    {
        public ushort sequence;
        /// <summary>要创建的目录相对路径</summary>
        public string relativePath;
    }

    /// <summary>
    /// 创建文件夹响应（Runtime -> Editor）
    /// </summary>
    [Serializable]
    public class FileBrowseMkdirResponse
    {
        public ushort sequence;
        public bool success;
        public string error;
        /// <summary>父目录相对路径（创建后刷新此目录）</summary>
        public string parentRelativePath;
    }

    /// <summary>
    /// 删除文件/文件夹请求（Editor -> Runtime）
    /// </summary>
    [Serializable]
    public class FileBrowseDeleteRequest
    {
        public ushort sequence;
        /// <summary>要删除的路径（相对于 persistentDataPath）</summary>
        public string relativePath;
        /// <summary>是否为目录</summary>
        public bool isDirectory;
    }

    /// <summary>
    /// 删除文件/文件夹响应（Runtime -> Editor）
    /// </summary>
    [Serializable]
    public class FileBrowseDeleteResponse
    {
        public ushort sequence;
        public bool success;
        public string error;
        /// <summary>父目录相对路径（删除后刷新此目录）</summary>
        public string parentRelativePath;
    }

    /// <summary>
    /// 文件下载请求（Editor -> Runtime）
    /// </summary>
    [Serializable]
    public class FileBrowseDownloadRequest
    {
        public ushort sequence;
        /// <summary>要下载的文件/目录相对路径</summary>
        public string relativePath;
        /// <summary>是否为目录（true=打包为zip下载）</summary>
        public bool isDirectory;
    }

    /// <summary>
    /// 文件下载数据块（Runtime -> Editor）
    /// </summary>
    [Serializable]
    public class FileBrowseDownloadChunk
    {
        public ushort sequence;
        /// <summary>文件相对路径</summary>
        public string relativePath;
        /// <summary>文件名</summary>
        public string fileName;
        /// <summary>总大小</summary>
        public long totalSize;
        /// <summary>当前块索引（从 0 开始）</summary>
        public int chunkIndex;
        /// <summary>总块数</summary>
        public int totalChunks;
        /// <summary>Base64 编码的数据</summary>
        public string data;
        /// <summary>是否为最后一块</summary>
        public bool isLast;
        /// <summary>是否出错</summary>
        public bool error;
        /// <summary>错误信息</summary>
        public string errorMessage;
    }

    // ===================== 画面抓取消息 =====================

    /// <summary>
    /// 画面抓取请求（Editor -> Runtime）
    /// </summary>
    [Serializable]
    public class ScreenCaptureRequest
    {
        public ushort sequence;
        /// <summary>截取画面的最大边长（像素），0=使用默认480</summary>
        public int maxSize;
    }

    /// <summary>
    /// 画面抓取响应数据块（Runtime -> Editor，JPG分块传输）
    /// </summary>
    [Serializable]
    public class ScreenCaptureChunk
    {
        public ushort sequence;
        /// <summary>是否成功</summary>
        public bool success;
        /// <summary>错误信息</summary>
        public string error;
        /// <summary>当前块索引（从0开始）</summary>
        public int chunkIndex;
        /// <summary>总块数</summary>
        public int totalChunks;
        /// <summary>Base64编码的JPG数据</summary>
        public string data;
        /// <summary>图片宽度（第一块时有效）</summary>
        public int width;
        /// <summary>图片高度（第一块时有效）</summary>
        public int height;
        /// <summary>是否为最后一块</summary>
        public bool isLast;
    }

    // ===================== 图像替换消息 =====================

    /// <summary>
    /// 替换 Image.sprite / UITexture.mainTexture 请求（Editor -> Runtime）
    /// 用"文件路径引用"模式 — Editor 发本地纹理绝对路径,
    /// Runtime 端 File.ReadAllBytes + Texture2D.LoadImage 加载后赋值。
    /// 注意:仅在 Editor Play Mode / Standalone PC 上能跑通(沙箱平台不支持)
    /// </summary>
    [Serializable]
    public class ReplaceImageSpriteRequest
    {
        public ushort sequence;
        /// <summary>目标 GameObject 的 InstanceID</summary>
        public int instanceId;
        /// <summary>
        /// 纹理组件类型 — "Image"(UnityEngine.UI.Image) 或 "UITexture"(NGUI)。
        /// 不区分大小写,空时默认 "Image"。
        /// </summary>
        public string componentType;
        /// <summary>Sprite 名称(用于创建后的 sprite.name,可选)</summary>
        public string spriteName;
        /// <summary>
        /// 本地纹理文件绝对路径(PNG/JPG)。
        /// Runtime 端会用 File.ReadAllBytes 读取;读取失败会返回错误。
        /// </summary>
        public string filePath;
        /// <summary>Pixels per unit(0 表示用 Image 当前值)</summary>
        public float pixelsPerUnit;
        /// <summary>Material 模式下的 Renderer 组件序号。</summary>
        public int componentIndex;
        /// <summary>Material 模式下的 Renderer 完整类型名。</summary>
        public string rendererType;
        /// <summary>Material 模式下的材质槽序号。</summary>
        public int materialIndex;
        /// <summary>Material 模式下的 Shader 纹理属性名。</summary>
        public string propertyName;
    }

    /// <summary>
    /// 替换 Image.sprite / UITexture.mainTexture 响应（Runtime -> Editor）
    /// </summary>
    [Serializable]
    public class ReplaceImageSpriteResponse
    {
        public ushort sequence;
        public bool success;
        public string error;
        public int spriteInstanceId;
        public int textureInstanceId;
        public int width;
        public int height;
    }
}
