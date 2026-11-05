// ============================================================================
// CommandConsole.cs
// 客户端聊天命令窗口 - IMGUI实现，按反引号/F12打开
// Runtime层 - 核心网络层
// ============================================================================


using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RemoteInspector.Runtime.Core
{
    /// <summary>
    /// 聊天窗口命令类型
    /// </summary>
    public enum ConsoleCommandType
    {
        Info,
        Warning,
        Error,
        System
    }

    /// <summary>
    /// 控制台日志条目
    /// </summary>
    [Serializable]
    public class ConsoleLogEntry
    {
        public string message;
        public ConsoleCommandType type;
        public float timestamp;

        public ConsoleLogEntry(string msg, ConsoleCommandType t)
        {
            message = msg;
            type = t;
            timestamp = Time.time;
        }
    }

    /// <summary>
    /// 客户端命令控制台
    /// IMGUI实现，按 ` (反引号) 或 F12 打开
    /// 支持命令：/connect /disconnect /pause /resume /status /help /clear
    /// </summary>
    public class CommandConsole : MonoBehaviour
    {
        [Header("Settings")]
        [Tooltip("默认编辑器IP")]
        public string defaultIP = "127.0.0.1";

        [Tooltip("默认编辑器端口")]
        public int defaultPort = 9555;

        // 窗口状态
        private bool _isVisible = false;
        private Rect _windowRect = new Rect(50, 50, 520, 360);

        // 日志
        private readonly List<ConsoleLogEntry> _logs = new List<ConsoleLogEntry>();
        private const int MAX_LOGS = 100;

        // 输入
        private string _inputText = "";
        private readonly List<string> _commandHistory = new List<string>();
        private int _historyIndex = -1;

        // GUIStyle缓存
        private GUIStyle _windowStyle;
        private GUIStyle _logStyleInfo;
        private GUIStyle _logStyleWarning;
        private GUIStyle _logStyleError;
        private GUIStyle _logStyleSystem;
        private bool _stylesInited = false;

        // 滚动
        private Vector2 _scrollPos;

        // 客户端引用
        private RemoteInspectorClient _client;

        private void Awake()
        {
            _client = GetComponent<RemoteInspectorClient>();

            // 键盘监听
            Application.targetFrameRate = 60;
        }

        private void Start()
        {
            // 注册快捷键
            Log("RemoteInspector Console ready. Press ` or F12 to toggle.", ConsoleCommandType.System);
            Log("Commands: /connect <ip> <port> 、/disconnect 、/pause 、/resume 、/status 、/help 、/clear", ConsoleCommandType.System);

            if (_client != null)
            {
                _client.OnConnectionStateChanged += OnConnectionStateChanged;
                Log($"Client initialized. Default: {defaultIP}:{defaultPort}", ConsoleCommandType.System);
            }
        }

        private void OnDestroy()
        {
            if (_client != null)
                _client.OnConnectionStateChanged -= OnConnectionStateChanged;
        }

        private void Update()
        {
            // 快捷键：反引号或F12
            if (Input.GetKeyDown(KeyCode.BackQuote) || Input.GetKeyDown(KeyCode.F12))
            {
                _isVisible = !_isVisible;
            }
        }

        private void OnGUI()
        {
            if (!_isVisible) return;

            InitStyles();

            // 背景遮罩
            GUI.Box(new Rect(0, 0, Screen.width, Screen.height), GUIContent.none);

            _windowRect = GUI.Window(0, _windowRect, DrawConsoleWindow, "RemoteInspector Console", _windowStyle ?? GUI.skin.window);
        }

        private void DrawConsoleWindow(int windowId)
        {
            float w = _windowRect.width;
            float h = _windowRect.height;

            // 日志区域
            float logAreaHeight = h - 70f;
            Rect logRect = new Rect(0, 0, w, logAreaHeight);

            _scrollPos = GUI.BeginScrollView(logRect, _scrollPos, new Rect(0, 0, w - 20, Mathf.Max(logAreaHeight, _logs.Count * 18f)));

            float y = 4;
            foreach (var entry in _logs)
            {
                GUIStyle style = GetStyleForType(entry.type);
                GUI.Label(new Rect(8, y, w - 24, 16), entry.message, style);
                y += 18;
            }

            GUI.EndScrollView();

            // 输入区域
            float inputY = logAreaHeight + 2;
            Rect inputRect = new Rect(4, inputY, w - 100, 22);
            GUI.SetNextControlName("ConsoleInput");
            _inputText = GUI.TextField(inputRect, _inputText, GUI.skin.textField);

            // 发送按钮
            Rect sendRect = new Rect(w - 90, inputY, 42, 22);
            if (GUI.Button(sendRect, "Send") || (Event.current.keyCode == KeyCode.Return && GUI.GetNameOfFocusedControl() == "ConsoleInput"))
            {
                SubmitCommand();
            }

            // 关闭按钮
            Rect closeRect = new Rect(w - 44, inputY, 40, 22);
            if (GUI.Button(closeRect, "Hide"))
            {
                _isVisible = false;
            }

            // 拖动
            GUI.DragWindow(new Rect(0, 0, w, 20));
        }

        private void InitStyles()
        {
            if (_stylesInited) return;
            _stylesInited = true;

            _windowStyle = new GUIStyle(GUI.skin.window)
            {
                padding = new RectOffset(4, 4, 20, 4)
            };

            _logStyleInfo = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f) }
            };

            _logStyleWarning = new GUIStyle(_logStyleInfo)
            {
                normal = { textColor = new Color(1f, 0.85f, 0.3f) }
            };

            _logStyleError = new GUIStyle(_logStyleInfo)
            {
                normal = { textColor = new Color(1f, 0.35f, 0.35f) }
            };

            _logStyleSystem = new GUIStyle(_logStyleInfo)
            {
                fontStyle = FontStyle.Italic,
                normal = { textColor = new Color(0.5f, 0.8f, 1f) }
            };
        }

        private GUIStyle GetStyleForType(ConsoleCommandType type)
        {
            switch (type)
            {
                case ConsoleCommandType.Warning: return _logStyleWarning ?? GUI.skin.label;
                case ConsoleCommandType.Error: return _logStyleError ?? GUI.skin.label;
                case ConsoleCommandType.System: return _logStyleSystem ?? GUI.skin.label;
                default: return _logStyleInfo ?? GUI.skin.label;
            }
        }

        private void SubmitCommand()
        {
            string cmd = _inputText.Trim();
            if (string.IsNullOrEmpty(cmd)) return;

            // 添加到历史
            _commandHistory.Insert(0, cmd);
            if (_commandHistory.Count > 50) _commandHistory.RemoveAt(_commandHistory.Count - 1);
            _historyIndex = -1;

            // 显示命令
            Log($"> {cmd}", ConsoleCommandType.System);

            // 处理命令
            ProcessCommand(cmd);
            _inputText = "";
        }

        private void ProcessCommand(string cmd)
        {
            string[] parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            string cmdName = parts[0].ToLower();

            switch (cmdName)
            {
                case "/connect":
                    HandleConnect(parts);
                    break;
                case "/disconnect":
                    HandleDisconnect();
                    break;
                case "/pause":
                    HandlePause();
                    break;
                case "/resume":
                    HandleResume();
                    break;
                case "/status":
                    HandleStatus();
                    break;
                case "/help":
                    HandleHelp();
                    break;
                case "/clear":
                    _logs.Clear();
                    Log("Console cleared.", ConsoleCommandType.System);
                    break;
                default:
                    Log($"Unknown command: {cmdName}. Type /help for available commands.", ConsoleCommandType.Warning);
                    break;
            }
        }

        private void HandleConnect(string[] parts)
        {
            string ip = defaultIP;
            int port = defaultPort;

            if (parts.Length >= 2) ip = parts[1];
            if (parts.Length >= 3) int.TryParse(parts[2], out port);

            if (_client == null)
            {
                Log("RemoteInspectorClient not found in scene!", ConsoleCommandType.Error);
                return;
            }

            Log($"Connecting to {ip}:{port}...", ConsoleCommandType.Info);
            _client.Connect(ip, port);
        }

        private void HandleDisconnect()
        {
            if (_client == null)
            {
                Log("RemoteInspectorClient not found in scene!", ConsoleCommandType.Error);
                return;
            }
            Log("Disconnecting...", ConsoleCommandType.Info);
            _client.Disconnect();
        }

        private void HandlePause()
        {
            if (_client == null)
            {
                Log("RemoteInspectorClient not found in scene!", ConsoleCommandType.Error);
                return;
            }
            if (_client.IsPaused)
            {
                Log("Already paused.", ConsoleCommandType.Warning);
                return;
            }
            Log("Pausing game (timeScale=0)...", ConsoleCommandType.Info);
            _client.Pause();
        }

        private void HandleResume()
        {
            if (_client == null)
            {
                Log("RemoteInspectorClient not found in scene!", ConsoleCommandType.Error);
                return;
            }
            if (!_client.IsPaused)
            {
                Log("Not paused.", ConsoleCommandType.Warning);
                return;
            }
            Log("Resuming game (timeScale=1)...", ConsoleCommandType.Info);
            _client.Resume();
        }

        private void HandleStatus()
        {
            if (_client == null)
            {
                Log("RemoteInspectorClient not found in scene!", ConsoleCommandType.Error);
                return;
            }

            string state = _client.State.ToString();
            string paused = _client.IsPaused ? "Yes" : "No";
            float fps = 1f / Time.deltaTime;
            Log($"Status: {state} · Paused: {paused} · FPS: {fps:F1} · TargetFPS: {Application.targetFrameRate}", ConsoleCommandType.Info);
            Log($"Scene: {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}", ConsoleCommandType.Info);
        }

        private void HandleHelp()
        {
            Log("=== RemoteInspector Commands ===", ConsoleCommandType.System);
            Log("/connect [ip] [port]  - Connect to editor", ConsoleCommandType.System);
            Log("/disconnect          - Disconnect from editor", ConsoleCommandType.System);
            Log("/pause               - Pause game (timeScale=0)", ConsoleCommandType.System);
            Log("/resume              - Resume game (timeScale=1)", ConsoleCommandType.System);
            Log("/status              - Show connection status", ConsoleCommandType.System);
            Log("/clear               - Clear console", ConsoleCommandType.System);
            Log("/help                - Show this help", ConsoleCommandType.System);
        }

        private void OnConnectionStateChanged(RemoteInspectorClient.ConnectionState state)
        {
            switch (state)
            {
                case RemoteInspectorClient.ConnectionState.Connected:
                    Log("Connected to editor!", ConsoleCommandType.Info);
                    break;
                case RemoteInspectorClient.ConnectionState.Disconnected:
                    Log("Disconnected from editor.", ConsoleCommandType.Warning);
                    break;
                case RemoteInspectorClient.ConnectionState.Error:
                    Log("Connection error!", ConsoleCommandType.Error);
                    break;
                case RemoteInspectorClient.ConnectionState.Connecting:
                    Log("Connecting...", ConsoleCommandType.Info);
                    break;
            }
        }

        /// <summary>
        /// 添加日志（供外部调用）
        /// </summary>
        public void Log(string message, ConsoleCommandType type = ConsoleCommandType.Info)
        {
            _logs.Add(new ConsoleLogEntry(message, type));
            if (_logs.Count > MAX_LOGS)
                _logs.RemoveAt(0);
        }

        /// <summary>
        /// 手动打开控制台
        /// </summary>
        public void Show() => _isVisible = true;

        /// <summary>
        /// 手动关闭控制台
        /// </summary>
        public void Hide() => _isVisible = false;
    }
} 
