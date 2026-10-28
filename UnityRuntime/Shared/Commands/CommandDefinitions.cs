// ============================================================================
// CommandDefinitions.cs
// 远程检视器所有命令ID枚举定义
// Shared层 - 命令定义
// ============================================================================

namespace RemoteInspector.Commands
{
    /// <summary>
    /// 远程检视器TCP协议命令枚举
    /// 协议格式：4字节长度 + 2字节命令ID + 2字节序列号 = 8字节固定包头
    /// </summary>
    public enum Command : ushort
    {
        // ===================== 心跳相关 =====================
        /// <summary>心跳请求/响应，每5秒发送一次</summary>
        HEARTBEAT = 0x0001,

        // ===================== 会话标识相关 =====================
        /// <summary>客户端握手消息（连接后立即发送，携带设备信息）</summary>
        CLIENT_HELLO = 0x0002,

        // ===================== 节点树相关 =====================
        /// <summary>获取场景节点树</summary>
        GET_NODE_TREE = 0x0010,
        /// <summary>节点树响应数据(JSON,旧版兼容 — 海量场景下体积大,推荐新版本)</summary>
        NODE_TREE_RESPONSE = 0x0011,
        /// <summary>节点树响应数据(二进制 — 体积比 JSON 小 ~80%,序列化/反序列化快 ~70%)
        ///   协议格式:见 BinaryNodeTreeCodec.cs 注释。
        ///   2026-06-25 加 — 海量大场景性能优化第一波。
        ///   老 Editor 不会订阅此 command,Runtime 检测 Editor 是否支持二进制 — 不支持则降级 JSON。</summary>
        NODE_TREE_RESPONSE_BINARY = 0x0111,

        // ===================== 节点详情相关 =====================
        /// <summary>获取单个节点详细信息（含组件）</summary>
        GET_NODE_DETAIL = 0x0012,
        /// <summary>节点详情响应数据(JSON,旧版兼容)</summary>
        NODE_DETAIL_RESPONSE = 0x0013,
        SET_PROPERTY = 0x0014,
        SET_PROPERTY_RESPONSE = 0x0015,
        /// <summary>节点详情响应数据(二进制 — 见 BinaryNodeDetailCodec.cs)
        ///   反射字段值直接写定长 binary,不再走 JsonUtility,避免 List 嵌套/Layer 字段反射 GC。</summary>
        NODE_DETAIL_RESPONSE_BINARY = 0x0113,

        // ===================== 客户端控制相关 =====================
        /// <summary>暂停客户端（Time.timeScale=0，停止数据采集）</summary>
        PAUSE_CLIENT = 0x0020,
        /// <summary>恢复客户端（Time.timeScale恢复，恢复数据采集）</summary>
        RESUME_CLIENT = 0x0021,

        // ===================== 帧率统计相关 =====================
        /// <summary>请求帧率统计数据</summary>
        GET_FRAME_STATS = 0x0030,
        /// <summary>帧率统计数据响应</summary>
        FRAME_STATS_RESPONSE = 0x0031,

        // ===================== 文件上传相关 =====================
        /// <summary>文件上传开始（携带文件名、文件大小、MD5等信息）</summary>
        FILE_UPLOAD_START = 0x0040,
        /// <summary>文件上传数据块</summary>
        FILE_UPLOAD_CHUNK = 0x0041,
        /// <summary>文件上传完成</summary>
        FILE_UPLOAD_END = 0x0042,
        /// <summary>文件上传确认/错误响应</summary>
        FILE_UPLOAD_ACK = 0x0043,

        // ===================== 纹理请求相关 =====================
        /// <summary>请求纹理数据（通过instanceId指定）</summary>
        GET_TEXTURE = 0x0050,
        /// <summary>纹理数据响应（分块Base64传输）</summary>
        TEXTURE_RESPONSE = 0x0051,

        // ===================== RPC 远程调用相关 =====================
        /// <summary>RPC 调用请求（Editor -> Runtime）</summary>
        RPC_CALL = 0x0060,
        /// <summary>RPC 调用响应（Runtime -> Editor）</summary>
        RPC_RESPONSE = 0x0061,
        /// <summary>RPC 注册列表请求（查询客户端可用函数）</summary>
        RPC_LIST = 0x0062,
        /// <summary>RPC 注册列表响应</summary>
        RPC_LIST_RESPONSE = 0x0063,

        // ===================== 日志转发相关 =====================
        /// <summary>客户端日志消息（Runtime -> Editor，实时转发 Debug.Log/Warning/Error）</summary>
        LOG_MESSAGE = 0x0070,

        // ===================== 文件浏览相关 =====================
        /// <summary>列出目录内容请求（Editor -> Runtime）</summary>
        FILE_BROWSE_LIST = 0x0080,
        /// <summary>列出目录内容响应（Runtime -> Editor）</summary>
        FILE_BROWSE_LIST_RESPONSE = 0x0081,
        /// <summary>创建文件夹请求（Editor -> Runtime）</summary>
        FILE_BROWSE_MKDIR = 0x0082,
        /// <summary>创建文件夹响应（Runtime -> Editor）</summary>
        FILE_BROWSE_MKDIR_RESPONSE = 0x0083,
        /// <summary>删除文件/文件夹请求（Editor -> Runtime）</summary>
        FILE_BROWSE_DELETE = 0x0084,
        /// <summary>删除文件/文件夹响应（Runtime -> Editor）</summary>
        FILE_BROWSE_DELETE_RESPONSE = 0x0085,
        /// <summary>文件下载请求（Editor -> Runtime，分块传输）</summary>
        FILE_BROWSE_DOWNLOAD = 0x0086,
        /// <summary>文件下载数据块响应（Runtime -> Editor）</summary>
        FILE_BROWSE_DOWNLOAD_CHUNK = 0x0087,

        // ===================== 画面抓取 =====================
        /// <summary>画面抓取请求（Editor -> Runtime）</summary>
        SCREEN_CAPTURE = 0x0090,
        /// <summary>画面抓取响应 - 分块（Runtime -> Editor）</summary>
        SCREEN_CAPTURE_CHUNK = 0x0091,

        // ===================== 图像替换 =====================
        /// <summary>替换 UnityEngine.UI.Image.sprite(Editor 上传纹理 + Runtime 反射设置 sprite)</summary>
        REPLACE_IMAGE_SPRITE = 0x00A0,
        /// <summary>图像替换响应(Runtime -> Editor,success/error)</summary>
        REPLACE_IMAGE_SPRITE_RESPONSE = 0x00A1,
    }
}
