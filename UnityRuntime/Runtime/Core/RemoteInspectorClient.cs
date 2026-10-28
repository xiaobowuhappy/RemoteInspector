// ============================================================================
// RemoteInspectorClient.cs
// 客户端TCP连接管理 - 连接编辑器、发送心跳、处理响应
// Runtime层 - 核心网络层
// ============================================================================

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using RemoteInspector.Commands;
using RemoteInspector.Protocol;

namespace RemoteInspector.Runtime.Core
{
    /// <summary>
    /// 客户端TCP连接
    /// 负责连接编辑器服务器、发送心跳、接收和处理响应 
    /// </summary>
    [ExecuteInEditMode]
    public class RemoteInspectorClient : MonoBehaviour
    { 
        private static string _lastRemoteIp = "10.5.8.176"; 
        private static int   _lastRemotePort = 9555;   
        private const float HeartbeatTimeout = 30f;
        private static bool _isCollectBgLogs = false;

        private static RemoteInspectorClient _mInspectorClient;

        /// <summary>
        /// 对外接口启动连接
        /// </summary>
        /// <param name="remoteIp"></param>
        /// <param name="remotePort"></param>
        public static void StartConnect(string remoteIp, int remotePort)
        {
            _lastRemoteIp = !string.IsNullOrWhiteSpace(remoteIp) ? remoteIp : _lastRemoteIp;
            _lastRemotePort = 0 < remotePort ? remotePort: _lastRemotePort; 
            
            GameObject inspectorClient  = GameObject.Find(nameof(RemoteInspectorClient));
            if (inspectorClient == null)
                inspectorClient = new GameObject(nameof(RemoteInspectorClient));
            if (inspectorClient != null)
            {
                _mInspectorClient = inspectorClient.GetComponent<RemoteInspectorClient>();
                if (_mInspectorClient == null)
                {
                    _mInspectorClient = inspectorClient.AddComponent<RemoteInspectorClient>();
                }
            } 
            
            if (_mInspectorClient != null)
            {
                _mInspectorClient.Connect(_lastRemoteIp, _lastRemotePort);
            }
        }

        public static void StopConnect()
        {
            if (_mInspectorClient != null)
            {
                _mInspectorClient.StopAutoConnect();
            }
        }
 
        
        // 运行时状态
        private TcpClient _client;
        private NetworkStream _stream;
        private Thread _receiveThread;
        private bool _isRunning;
        private bool _isPaused;

        // 主线程任务队列：后台线程收到需要 Unity API 的命令后入队，主线程 Update 中执行
        private readonly ConcurrentQueue<Action> _mainThreadTasks = new ConcurrentQueue<Action>();
        private const int MAX_TASKS_PER_FRAME = 5; // 每帧最多处理 5 个任务，防止帧时间过长

        // 连接后首次发送节点树标记
        private bool _needsInitialTreeSend;

        // 日志转发
        private readonly List<LogMessage> _logBatch = new List<LogMessage>();
        private const int LOG_BATCH_SIZE = 10;
        private float _logFlushTimer;
        private const float LOG_FLUSH_INTERVAL = 0.2f; // 200ms
        private int _nextLogSeq;
        private bool _logRegistered;

        // 日志限流
        private int _logCountThisSecond;
        private float _logThrottleTimer;
        private const int MAX_LOGS_PER_SECOND = 50;
        private int _droppedLogCount;

        // 接收缓冲区
        private byte[] _recvBuffer = new byte[64 * 1024];
        private int _recvPos = 0;

        // 心跳
        private double _lastHeartbeatTime;
        private double _lastSendTime;
        private int _nextSequenceRaw = 1; // 使用 int + Interlocked 保证线程安全

        /// <summary>线程安全的序列号生成</summary>
        private ushort GetNextSequence()
        {
            return (ushort)(Interlocked.Increment(ref _nextSequenceRaw) & 0xFFFF);
        }

        private static double NowSeconds()
        {
            return DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0;
        }

        // 消息处理
        private MessageHandler _messageHandler;

        /// <summary>获取 RPC 处理器（用于注册远程可调用函数）</summary>
        public RpcHandler Rpc => _messageHandler?.Rpc;

        // 连接状态
        private ConnectionState _connectionState = ConnectionState.Disconnected;
        public ConnectionState State => _connectionState;

        public enum ConnectionState
        {
            Disconnected,
            Connecting,
            Connected,
            Error,
            Stop,
        }

        // 事件
        public event Action<ConnectionState> OnConnectionStateChanged;
        public event Action OnPaused;
        public event Action OnResumed;
     

        // ==================== Unity生命周期 ====================

        private string _deviceName;
        private string _platform;
        private string _unityVersion;
        private string _systemVersion; 
        
        private void Awake()
        {
            _deviceName = SystemInfo.deviceName;
            _platform = Application.platform.ToString();
            _unityVersion = Application.unityVersion;
            _systemVersion = SystemInfo.operatingSystem; 
            _messageHandler = new MessageHandler(this);
#if !UNITY_EDITOR
            DontDestroyOnLoad(gameObject);
#endif
        }

        public void StopAutoConnect()
        {
            Disconnect();
            SetState(ConnectionState.Stop); 
        }

        private void Update()
        { 
            if (_connectionState == ConnectionState.Disconnected)
            { 
                //当前连接失败 延迟3s 自动尝试连接 
                Connect(_lastRemoteIp, _lastRemotePort);
                return;
            }
            
            if (_connectionState != ConnectionState.Connected) return;

            // 使用与后台线程一致的 DateTimeOffset 时间源
            double time = NowSeconds();

            // 心跳发送
            if (time - _lastSendTime >= MessageProtocol.HEARTBEAT_INTERVAL)
            {
                _lastSendTime = time;
                SendHeartbeat();
            }

            // 心跳超时
            if (time - _lastHeartbeatTime >= HeartbeatTimeout)
            {
                Debug.LogWarning("[RemoteInspector] Heartbeat timeout, disconnecting");
                Disconnect();
                return;
            }

            // 处理主线程任务队列（需要 Unity API 的命令）
            ProcessMainThreadTasks();

            // FrameStats 采集更新
            _messageHandler?.UpdateFrameStats();

            // 连接后首次发送节点树
            if (_needsInitialTreeSend)
            {
                _needsInitialTreeSend = false;
                _messageHandler?.CollectAndSendNodeTree(GetNextSequence());
            }

            // 日志限流计时器
            _logThrottleTimer += Time.unscaledDeltaTime;
            if (_logThrottleTimer >= 1f)
            {
                _logThrottleTimer = 0f;
                _logCountThisSecond = 0;
            }

            // 日志批量刷新
            if (_logRegistered)
            {
                _logFlushTimer += Time.unscaledDeltaTime;
                if (_logFlushTimer >= LOG_FLUSH_INTERVAL || _logBatch.Count >= LOG_BATCH_SIZE)
                {
                    FlushLogBatch();
                    _logFlushTimer = 0f;
                }
            }
        }

        /// <summary>
        /// 处理主线程任务队列
        /// 后台线程收到的需要 Unity API 的命令在这里执行
        /// </summary>
        private void ProcessMainThreadTasks()
        {
            int processed = 0;
            while (processed < MAX_TASKS_PER_FRAME && _mainThreadTasks.TryDequeue(out var task))
            {
                try
                {
                    task();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[RemoteInspector] Main thread task error: {ex.Message}");
                }
                processed++;
            }
        }

        private void OnDestroy()
        {
            Disconnect();
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus && _connectionState == ConnectionState.Connected)
            {
                // 移动端进入后台，断开连接节省资源
                Disconnect();
            }
        }

        // ==================== 日志转发 ====================

        /// <summary>
        /// 注册 Unity 日志回调，将日志转发给编辑器
        /// </summary>
        private void RegisterLogCallback()
        {
            if (_logRegistered) return;
            _logRegistered = true;
            _isCollectBgLogs = true;
            Application.logMessageReceived += OnLogMessageReceived;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
            _logFlushTimer = 0f;
            Debug.Log("[RemoteInspector] Log forwarding enabled");
        }

        /// <summary>
        /// 注销日志回调
        /// </summary>
        private void UnregisterLogCallback()
        {
            if (!_logRegistered) return;
            _logRegistered = false;
            _isCollectBgLogs = false;
            Application.logMessageReceived -= OnLogMessageReceived;
            Application.logMessageReceivedThreaded -= OnLogMessageReceived;

            // 断开前发送剩余日志
            FlushLogBatch();
        }

        public static void DispatchBgLog(bq.def.log_level level, string log_format_content)
        {
            if(!_isCollectBgLogs) return;
            UnityEngine.LogType logType = UnityEngine.LogType.Log;
            switch (level)
            {
                case bq.def.log_level.debug:
                    logType = UnityEngine.LogType.Log;
                    break;
                case bq.def.log_level.error:
                    logType = UnityEngine.LogType.Error;
                    break;
                case bq.def.log_level.warning:
                    logType = UnityEngine.LogType.Warning;
                    break;
                default:
                    logType = UnityEngine.LogType.Exception; 
                    break;
            } 
            _mInspectorClient?.OnLogMessageReceived(log_format_content, "", logType);
        }
        
        
        /// <summary>
        /// 日志回调（在主线程或日志线程中调用）
        /// 将日志消息缓存到批量列表中（含限流）
        /// </summary>
        private void OnLogMessageReceived(string condition, string stackTrace, UnityEngine.LogType type)
        {
            // 避免无限递归：不转发 RemoteInspector 自己的网络层日志
            if (condition != null && condition.StartsWith("[RemoteInspector]") &&
                (type == UnityEngine.LogType.Log || type == UnityEngine.LogType.Warning))
                return;

            // 日志限流：每秒最多 MAX_LOGS_PER_SECOND 条
            if (Interlocked.Increment(ref _logCountThisSecond) > MAX_LOGS_PER_SECOND)
            {
                Interlocked.Increment(ref _droppedLogCount);
                return;
            }

            // 截断过长的日志
            if (condition != null && condition.Length > 4096)
                condition = condition.Substring(0, 4096);

            var msg = new LogMessage
            {
                sequence = (ushort)Interlocked.Increment(ref _nextLogSeq),
                logType = (int)type,
                message = condition ?? "",
                stackTrace = (type == UnityEngine.LogType.Error || type == UnityEngine.LogType.Exception) ? (stackTrace ?? "") : "",
                timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };

            // 线程安全添加到批量列表
            lock (_logBatch)
            {
                _logBatch.Add(msg);
            }
        }

        /// <summary>
        /// 将缓存的日志批量发送给编辑器
        /// </summary>
        private void FlushLogBatch()
        {
            LogMessage[] toSend;
            int dropped = Interlocked.Exchange(ref _droppedLogCount, 0);

            lock (_logBatch)
            {
                if (_logBatch.Count == 0 && dropped == 0) return;
                toSend = _logBatch.ToArray();
                _logBatch.Clear();
            }

            try
            {
                // 如果有丢弃的日志，在前面插入一条提示
                if (dropped > 0)
                {
                    var dropMsg = new LogMessage
                    {
                        sequence = (ushort)Interlocked.Increment(ref _nextLogSeq),
                        logType = 1, // Warning
                        message = $"[RemoteInspector] {dropped} log(s) throttled (>{MAX_LOGS_PER_SECOND}/s)",
                        stackTrace = "",
                        timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
                    };

                    if (toSend.Length == 0)
                    {
                        string json = MessageSerializer.SerializeBody(dropMsg);
                        SendPacket(Command.LOG_MESSAGE, dropMsg.sequence, json);
                        return;
                    }
                    else
                    {
                        // 合并到批量中
                        var merged = new LogMessage[toSend.Length + 1];
                        merged[0] = dropMsg;
                        Array.Copy(toSend, 0, merged, 1, toSend.Length);
                        toSend = merged;
                    }
                }

                if (toSend.Length == 1)
                {
                    // 单条直接发送
                    string json = MessageSerializer.SerializeBody(toSend[0]);
                    SendPacket(Command.LOG_MESSAGE, toSend[0].sequence, json);
                }
                else
                {
                    // 多条打包发送
                    var batch = new LogBatchMessage
                    {
                        sequence = toSend[0].sequence,
                        entries = toSend
                    };
                    string json = MessageSerializer.SerializeBody(batch);
                    SendPacket(Command.LOG_MESSAGE, batch.sequence, json);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] Log flush error: {ex.Message}");
            }
        }

        [ContextMenu("执行连接")]
        public void DoConnect()
        { 
            Connect(_lastRemoteIp, _lastRemotePort);
        }
        
        // ==================== 连接控制 ====================

        /// <summary>
        /// 连接编辑器
        /// </summary>
        public void Connect(string ip, int port)
        {
            if (_connectionState == ConnectionState.Connected || _connectionState == ConnectionState.Connecting)
            {
                Debug.LogWarning("[RemoteInspector] Already connected or connecting");
                return;
            }

            _lastRemoteIp = ip;
            _lastRemotePort = port;

            SetState(ConnectionState.Connecting);
            _isRunning = true;

            // 连接线程
            Thread connectThread = new Thread(ConnectThread);
            connectThread.IsBackground = true;
            connectThread.Start();
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            _isRunning = false;

            // 注销日志回调
            UnregisterLogCallback();

            // 立即关闭所有进行中的文件上传,释放 FileStream 句柄 + 删除 partial 文件
            //   原因:断线后正在 CHUNK 阶段的 session 永远不会收到 END,
            //   FileStream 不会释放 → 进程级资源泄漏 + 磁盘污染
            try { _messageHandler?.FileUpload?.DisposeAll(); } catch { }

            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            try { _receiveThread?.Join(2000); } catch { }

            _stream = null;
            _client = null;
            _receiveThread = null;

            SetState(ConnectionState.Disconnected);
            Debug.Log("[RemoteInspector] Disconnected");
        }

        private void ConnectThread()
        {
            try
            {
                _client = new TcpClient();
                _client.NoDelay = true;
                _client.ReceiveBufferSize = 64 * 1024;
                _client.SendBufferSize = 64 * 1024;
                _client.Connect(_lastRemoteIp, _lastRemotePort);

                _stream = _client.GetStream();

                // 启动接收线程
                _receiveThread = new Thread(ReceiveLoop);
                _receiveThread.IsBackground = true;
                _receiveThread.Start();

                // 连接成功
                SetState(ConnectionState.Connected);
                // 使用 DateTimeOffset 替代 Time.realtimeSinceStartup（线程安全）
                double nowSec = NowSeconds();
                _lastHeartbeatTime = nowSec;
                _lastSendTime = nowSec;

                Debug.Log($"[RemoteInspector] Connected to {_lastRemoteIp}:{_lastRemotePort}");

                // 发送握手消息（设备标识）
                SendClientHello();

                // 注册日志回调 — 必须在主线程执行，入队到主线程任务队列
                _mainThreadTasks.Enqueue(() => RegisterLogCallback());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] Connection failed: {ex.Message}");
                Disconnect();
                SetState(ConnectionState.Error);
            }
        }

        // ==================== 接收循环 ====================

        private void ReceiveLoop()
        {
            while (_isRunning && _stream != null)
            {
                try
                {
                    if (!_stream.DataAvailable)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    int available = _client.Available;
                    if (available <= 0)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    if (_recvPos + available > _recvBuffer.Length)
                    {
                        Array.Resize(ref _recvBuffer, Math.Max(_recvBuffer.Length * 2, _recvPos + available));
                    }

                    int read = _stream.Read(_recvBuffer, _recvPos, available);
                    if (read <= 0) break;

                    _recvPos += read;
                    ProcessBuffer();
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Debug.LogError($"[RemoteInspector] Receive error: {ex.Message}");
                    break;
                }
            }

            _isRunning = false;
        }

        private void ProcessBuffer()
        {
            while (_recvPos >= MessageProtocol.HEADER_SIZE)
            {
                int totalLength = BitConverter.ToInt32(_recvBuffer, 0);

                if (totalLength < MessageProtocol.HEADER_SIZE || totalLength > MessageProtocol.MAX_BODY_SIZE)
                {
                    Debug.LogWarning($"[RemoteInspector] Invalid packet length: {totalLength}");
                    Disconnect();
                    return;
                }

                if (_recvPos < totalLength)
                    return;

                byte[] packetData = new byte[totalLength];
                Buffer.BlockCopy(_recvBuffer, 0, packetData, 0, totalLength);

                int remaining = _recvPos - totalLength;
                Buffer.BlockCopy(_recvBuffer, totalLength, _recvBuffer, 0, remaining);
                _recvPos = remaining;

                var packet = MessageSerializer.Deserialize(packetData);
                if (packet != null)
                {
                    HandlePacket(packet);
                }
            }
        }

        private void HandlePacket(MessagePacket packet)
        { 
            Command cmd = packet.GetCommand();

            // 心跳特殊处理（不需要Unity API，直接回复）
            if (cmd == Command.HEARTBEAT)
            {
                // 使用 DateTimeOffset 替代 Time.realtimeSinceStartup（线程安全）
                _lastHeartbeatTime = NowSeconds();
                // 立即回复心跳
                SendHeartbeat();
                return;
            }

            // 需要 Unity API 的命令 → 入队主线程执行
            // 这些命令会访问 GameObject、Transform、Material 等非线程安全对象
            if (RequiresMainThread(cmd))
            {
                _mainThreadTasks.Enqueue(() => _messageHandler?.Handle(packet));
                return;
            }

            // 不需要 Unity API 的命令（RPC、文件上传等）→ 直接在后台线程处理
            _messageHandler?.Handle(packet);
        }

        /// <summary>
        /// 判断命令是否需要在主线程执行（涉及 Unity API 调用）
        /// </summary>
        private static bool RequiresMainThread(Command cmd)
        {
            switch (cmd)
            {
                case Command.GET_NODE_TREE:
                case Command.GET_NODE_DETAIL:
                case Command.SET_PROPERTY:
                case Command.GET_TEXTURE:
                case Command.GET_FRAME_STATS:
                case Command.PAUSE_CLIENT:
                case Command.RESUME_CLIENT:
                case Command.FILE_BROWSE_LIST:
                case Command.FILE_BROWSE_MKDIR:
                case Command.FILE_BROWSE_DELETE:
                case Command.FILE_BROWSE_DOWNLOAD:
                case (Command)InspectionProtocol.ScreenPick:
                case (Command)InspectionProtocol.TextureOriginal:
                case Command.SCREEN_CAPTURE:
                case Command.REPLACE_IMAGE_SPRITE:
                    // 文件上传也走 main thread:
                    //   FileUploadHandler.ResolveDiskPath 调 Application.persistentDataPath,
                    //   Unity 限制只能在 main thread 访问。TCP 接收线程直接处理会报错。
                    case Command.FILE_UPLOAD_START:
                    case Command.FILE_UPLOAD_CHUNK:
                    case Command.FILE_UPLOAD_END:
                    return true;
                default:
                    return false;
            }
        }

        // ==================== 发送消息 ====================

        /// <summary>
        /// 发送原始数据
        /// </summary>
        public void Send(byte[] data)
        {
            if (_stream == null || !_isRunning) return;

            try
            {
                lock (_stream)
                {
                    _stream.Write(data, 0, data.Length);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] Send error: {ex.Message}");
                Disconnect();
            }
        }

        /// <summary>
        /// 发送消息包(JSON body)
        /// </summary>
        public void SendPacket(Command cmd, ushort seq, string bodyJson = null)
        {
            var packet = new MessagePacket(cmd, seq, bodyJson);
            byte[] data = MessageSerializer.Serialize(packet);
            Send(data);
        }

        /// <summary>
        /// 发送消息包(Binary body) — 海量大场景性能优化第一波(2026-06-25)
        /// 用于 NODE_TREE_RESPONSE_BINARY / NODE_DETAIL_RESPONSE_BINARY 等,不走 UTF-8 编码。
        /// </summary>
        public void SendPacket(Command cmd, ushort seq, byte[] bodyBytes)
        {
            var packet = new MessagePacket(cmd, seq, bodyBytes);
            byte[] data = MessageSerializer.Serialize(packet);
            Send(data);
        }

        /// <summary>
        /// 发送心跳
        /// </summary>
        private void SendHeartbeat()
        {
            var hb = new HeartbeatMessage
            {
                sequence = GetNextSequence(),
                clientTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };
            string json = MessageSerializer.SerializeBody(hb);
            SendPacket(Command.HEARTBEAT, hb.sequence, json);
        }

        /// <summary>
        /// 发送客户端握手消息（设备标识信息）
        /// </summary>
        private void SendClientHello()
        {
            var hello = new ClientHelloRequest
            {
                sequence = GetNextSequence(),
                sessionId = System.Guid.NewGuid().ToString("N"),
                deviceName = _deviceName,
                platform = _platform,
                unityVersion = _unityVersion,
                systemVersion = _systemVersion
            };
            string json = MessageSerializer.SerializeBody(hello);
            SendPacket(Command.CLIENT_HELLO, hello.sequence, json);
            Debug.Log($"[RemoteInspector] Sent hello: {hello.deviceName} [{hello.platform}]");
        }

        /// <summary>
        /// 请求发送节点树数据（连接成功后调用）
        /// 标记为主线程任务，在 Update 中执行
        /// </summary>
        public void RequestNodeTree()
        {
            _needsInitialTreeSend = true;
        }

        // ==================== 客户端控制 ====================

        /// <summary>
        /// 暂停游戏（Time.timeScale=0）
        /// </summary>
        public void Pause()
        {
            if (_isPaused) return;
            _isPaused = true;
            Time.timeScale = 0f;
            OnPaused?.Invoke();
            Debug.Log("[RemoteInspector] Client paused (timeScale=0)");
        }

        /// <summary>
        /// 恢复游戏
        /// </summary>
        public void Resume()
        {
            if (!_isPaused) return;
            _isPaused = false;
            Time.timeScale = 1f;
            OnResumed?.Invoke();
            Debug.Log("[RemoteInspector] Client resumed (timeScale=1)");
        }

        public bool IsPaused => _isPaused;

        // ==================== 屏幕截图 ====================

        /// <summary>
        /// 启动一次屏幕截图(异步)。
        /// 必须在主线程调用(由 MessageHandler 在主线程任务队列中触发)。
        /// 内部用协程 + WaitForEndOfFrame 等待帧末再截图,避开 Unity 的
        /// "CaptureScreenshotAsTexture must be called at end of frame" 限制。
        /// </summary>
        public void StartScreenCapture(int maxSize, ushort seq)
        {
            if (!isActiveAndEnabled)
            {
                SendScreenCaptureError(seq, "RemoteInspectorClient is not active");
                return;
            }
            if (!Application.isPlaying)
            {
                // Edit 模式下协程不会跑,WaitForEndOfFrame 永远不返回。
                // 直接发错误响应,告诉用户切到 Play 模式。
                SendScreenCaptureError(seq, "截图需要 Play 模式(Edit 模式协程无法运行 WaitForEndOfFrame)");
                return;
            }
            try
            {
                StartCoroutine(ScreenCaptureCoroutine(maxSize, seq));
            }
            catch (Exception ex)
            {
                SendScreenCaptureError(seq, "StartCoroutine failed: " + ex.Message);
            }
        }

        private IEnumerator ScreenCaptureCoroutine(int maxSize, ushort seq)
        {
            // 必须等帧末(渲染流程结束后)才能正确抓取画面
            yield return new WaitForEndOfFrame();
            DoScreenCaptureAndSend(maxSize, seq);
        }

        private void DoScreenCaptureAndSend(int maxSize, ushort seq)
        {
            Texture2D capture = null;
            Texture2D scaled = null;
            try
            {
                capture = ScreenCapture.CaptureScreenshotAsTexture();
                if (capture == null)
                {
                    SendScreenCaptureError(seq, "Screen capture returned null texture");
                    return;
                }

                // 缩放到 maxSize 以内
                if (capture.width > maxSize || capture.height > maxSize)
                {
                    float scale = Mathf.Min((float)maxSize / capture.width, (float)maxSize / capture.height);
                    int w = Mathf.Max(1, Mathf.RoundToInt(capture.width * scale));
                    int h = Mathf.Max(1, Mathf.RoundToInt(capture.height * scale));

                    RenderTexture rt = RenderTexture.GetTemporary(w, h);
                    Graphics.Blit(capture, rt);
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    scaled = new Texture2D(w, h, TextureFormat.RGB24, false);
                    scaled.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    scaled.Apply();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);

                    UnityEngine.Object.Destroy(capture);
                    capture = null;
                }
                else
                {
                    scaled = capture;
                    capture = null;
                }

                int imgWidth = scaled.width;
                int imgHeight = scaled.height;
                byte[] jpgBytes = scaled.EncodeToJPG(75);

                // 分块发送(32KB/块,与纹理传输一致)
                const int chunkSize = 32 * 1024;
                int totalChunks = (int)Math.Ceiling((double)jpgBytes.Length / chunkSize);
                if (totalChunks == 0) totalChunks = 1;

                Debug.Log($"[RemoteInspector] ScreenCapture: {jpgBytes.Length} bytes, {totalChunks} chunks, {imgWidth}x{imgHeight}");

                for (int i = 0; i < totalChunks; i++)
                {
                    int offset = i * chunkSize;
                    int len = Math.Min(chunkSize, jpgBytes.Length - offset);
                    byte[] slice = new byte[len];
                    Array.Copy(jpgBytes, offset, slice, 0, len);

                    var chunk = new ScreenCaptureChunk
                    {
                        sequence = seq,
                        success = true,
                        chunkIndex = i,
                        totalChunks = totalChunks,
                        data = Convert.ToBase64String(slice),
                        width = (i == 0) ? imgWidth : 0,
                        height = (i == 0) ? imgHeight : 0,
                        isLast = (i == totalChunks - 1)
                    };

                    string json = MessageSerializer.SerializeBody(chunk);
                    SendPacket(Command.SCREEN_CAPTURE_CHUNK, seq, json);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] DoScreenCaptureAndSend error: {ex.Message}");
                SendScreenCaptureError(seq, ex.Message);
            }
            finally
            {
                if (capture != null) UnityEngine.Object.Destroy(capture);
                if (scaled != null) UnityEngine.Object.Destroy(scaled);
            }
        }

        private void SendScreenCaptureError(ushort seq, string error)
        {
            try
            {
                var errChunk = new ScreenCaptureChunk
                {
                    sequence = seq,
                    success = false,
                    error = error ?? "unknown",
                    isLast = true
                };
                string errJson = MessageSerializer.SerializeBody(errChunk);
                SendPacket(Command.SCREEN_CAPTURE_CHUNK, seq, errJson);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RemoteInspector] SendScreenCaptureError failed: {ex.Message}");
            }
        }

        // ==================== 状态管理 ====================

        private void SetState(ConnectionState state)
        {
            if (_connectionState == state) return;
            _connectionState = state;
            OnConnectionStateChanged?.Invoke(state);
        }
    }
}
