// ============================================================================
// RpcHandler.cs
// Runtime端 RPC 处理器 - 接收编辑器的远程调用请求并执行
// Runtime层 - 核心网络层
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using RemoteInspector.Commands;
using RemoteInspector.Protocol;

namespace RemoteInspector.Runtime.Core
{
    /// <summary>
    /// RPC 调用处理器
    /// 允许 Editor 端远程调用 Runtime 端已注册的函数
    ///
    /// 使用方式（在 Runtime 端）：
    ///   // 1. 获取 RpcHandler 实例
    ///   var rpc = GetComponent&lt;RemoteInspectorClient&gt;().RpcHandler;
    ///   // 2. 注册函数
    ///   rpc.Register("GetPlayerHealth", () => player.health.ToString(), "获取玩家血量");
    ///   rpc.Register("SetTimeScale", (float scale) => { Time.timeScale = scale; }, "设置时间缩放");
    ///   rpc.Register("SpawnEnemy", (string name, int count) => { ... return $"Spawned {count}"; }, "生成敌人");
    /// </summary>
    public class RpcHandler
    {
        private readonly RemoteInspectorClient _client;
        private readonly Dictionary<string, RpcMethodInfo> _registry = new Dictionary<string, RpcMethodInfo>();
        private readonly Dictionary<string, Delegate> _delegates = new Dictionary<string, Delegate>();

        /// <summary>RPC 调用事件（供外部监听/日志）</summary>
        public event Action<string, bool, string, long> OnRpcExecuted; // methodName, success, result/error, timeMs

        /// <summary>默认 RPC 超时时间（毫秒）</summary>
        public const int DEFAULT_TIMEOUT_MS = 5000;

        public RpcHandler(RemoteInspectorClient client)
        {
            _client = client;

            // Register("InitInjectFix", GameManager.InitInjectFix, "Reload IFix Dll!");
            // Register("InitILRuntimeFix", DAO.ILRuntimeManager.Instance.Init, "Reload IlRuntime Dll!");
            
        }

        // ==================== 注册 API ====================

        /// <summary>
        /// 注册无参函数（返回 string）
        /// </summary>
        public void Register(string name, Func<string> action, string description = "")
        {
            RegisterInternal(name, action, description);
        }

        /// <summary>
        /// 注册带1个参数的函数（参数为 string，返回 string）
        /// </summary>
        public void Register(string name, Func<string, string> action, string description = "")
        {
            RegisterInternal(name, action, description);
        }

        /// <summary>
        /// 注册带2个参数的函数
        /// </summary>
        public void Register<T1, T2>(string name, Func<T1, T2, string> action, string description = "")
        {
            RegisterInternal(name, action, description);
        }

        /// <summary>
        /// 注册带3个参数的函数
        /// </summary>
        public void Register<T1, T2, T3>(string name, Func<T1, T2, T3, string> action, string description = "")
        {
            RegisterInternal(name, action, description);
        }

        /// <summary>
        /// 注册 Action（无返回值，自动返回 "ok"）
        /// </summary>
        public void Register(string name, Action action, string description = "")
        {
            RegisterInternal(name, action, description);
        }

        /// <summary>
        /// 注册带1个参数的 Action
        /// </summary>
        public void Register<T1>(string name, Action<T1> action, string description = "")
        {
            RegisterInternal(name, action, description);
        }

        /// <summary>
        /// 注册带2个参数的 Action
        /// </summary>
        public void Register<T1, T2>(string name, Action<T1, T2> action, string description = "")
        {
            RegisterInternal(name, action, description);
        }

        /// <summary>
        /// 注册带3个参数的 Action
        /// </summary>
        public void Register<T1, T2, T3>(string name, Action<T1, T2, T3> action, string description = "")
        {
            RegisterInternal(name, action, description);
        }

        /// <summary>
        /// 注册通用 Delegate（高级用法）
        /// </summary>
        public void Register(string name, Delegate action, string description = "")
        {
            RegisterInternal(name, action, description);
        }

        /// <summary>
        /// 注销函数
        /// </summary>
        public void Unregister(string name)
        {
            _registry.Remove(name);
            _delegates.Remove(name);
        }

        /// <summary>
        /// 检查函数是否已注册
        /// </summary>
        public bool IsRegistered(string name)
        {
            return _registry.ContainsKey(name);
        }

        // ==================== 消息处理 ====================

        /// <summary>
        /// 处理 RPC_CALL 请求
        /// </summary>
        public void HandleRpcCall(MessagePacket packet) { var clock = System.Diagnostics.Stopwatch.StartNew(); string name = ""; var response = new RpcCallResponse { sequence = packet.sequence }; try { var request = MessageSerializer.DeserializeBody<RpcCallRequest>(packet); name = request?.methodName ?? ""; response.methodName = name; Delegate action; if (!_delegates.TryGetValue(name, out action)) throw new ArgumentException("未注册方法：" + name); var value = InvokeDelegate(action, ParseArgs(request.argsJson)); clock.Stop(); response.success = true; response.resultJson = RpcArguments.Result(value); } catch (Exception ex) { clock.Stop(); var actual = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex; response.success = false; response.error = actual.ToString(); } response.executionTimeMs = clock.ElapsedMilliseconds; _client.SendPacket(Command.RPC_RESPONSE, packet.sequence, MessageSerializer.SerializeBody(response)); var handlers = OnRpcExecuted; if (handlers != null) foreach (Action<string, bool, string, long> handler in handlers.GetInvocationList()) try { handler(name, response.success, response.success ? response.resultJson : response.error, response.executionTimeMs); } catch { } }

        /// <summary>
        /// 处理 RPC_LIST 请求
        /// </summary>
        public void HandleRpcList(MessagePacket packet)
        {
            var req = MessageSerializer.DeserializeBody<RpcListRequest>(packet);
            ushort seq = req?.sequence ?? packet.sequence;

            var response = new RpcListResponse
            {
                sequence = seq,
                methods = _registry.Values.ToArray()
            };

            string json = MessageSerializer.SerializeBody(response);
            _client.SendPacket(Command.RPC_LIST_RESPONSE, seq, json);
        }

        /// <summary>
        /// 获取所有已注册函数的列表
        /// </summary>
        public RpcMethodInfo[] GetRegisteredMethods()
        {
            return _registry.Values.ToArray();
        }

        // ==================== 内部方法 ====================

        internal object[] GetRegisteredTargets() { var result = new List<object>(); foreach (var value in _delegates.Values) if (value.Target != null && !result.Any(t => ReferenceEquals(t, value.Target))) result.Add(value.Target); return result.ToArray(); }

        private void RegisterInternal(string name, Delegate action, string description)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("RPC method name cannot be empty");

            var info = new RpcMethodInfo
            {
                name = name,
                signature = BuildSignature(action),
                description = description ?? ""
            };

            _registry[name] = info;
            _delegates[name] = action;

            Debug.Log($"[RemoteInspector] RPC registered: {name}{info.signature}");
        }

        private string BuildSignature(Delegate del)
        {
            var method = del.Method;
            var parameters = method.GetParameters();
            var paramStrs = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                paramStrs[i] = $"{MapTypeName(p.ParameterType)} {p.Name}";
            }
            string returnType = method.ReturnType == typeof(void) ? "void" : MapTypeName(method.ReturnType);
            return $"({string.Join(", ", paramStrs)}) -> {returnType}";
        }

        private string MapTypeName(Type type)
        {
            if (type == typeof(string)) return "string";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(double)) return "double";
            if (type == typeof(long)) return "long";
            if (type == typeof(void)) return "void";
            return type.Name;
        }

        private string[] ParseArgs(string argsJson) { if ((argsJson ?? "").Length > 65536) throw new ArgumentException("参数最多 64 Ki 字符"); return Newtonsoft.Json.Linq.JArray.Parse(string.IsNullOrWhiteSpace(argsJson) ? "[]" : argsJson).Select(t => t.ToString(Newtonsoft.Json.Formatting.None)).ToArray(); }

        private object InvokeDelegate(Delegate del, string[] args) { if (del == null) throw new ArgumentNullException(nameof(del)); return del.DynamicInvoke(RpcArguments.Bind(del.Method, "[" + string.Join(",", args) + "]")); }

        private object ConvertValue(string value, Type targetType)
        {
            if (targetType == typeof(string)) return value;
            if (targetType == typeof(int)) return int.TryParse(value, out int vi) ? vi : 0;
            if (targetType == typeof(float)) return float.TryParse(value, out float vf) ? vf : 0f;
            if (targetType == typeof(double)) return double.TryParse(value, out double vd) ? vd : 0.0;
            if (targetType == typeof(bool)) return bool.TryParse(value, out bool vb) ? vb : false;
            if (targetType == typeof(long)) return long.TryParse(value, out long vl) ? vl : 0L;
            return value;
        }

        private void SendResult(ushort seq, string methodName, bool success, string result, long timeMs)
        {
            var resp = new RpcCallResponse
            {
                sequence = seq,
                methodName = methodName,
                success = success,
                resultJson = result,
                error = null,
                executionTimeMs = timeMs
            };
            string json = MessageSerializer.SerializeBody(resp);
            _client.SendPacket(Command.RPC_RESPONSE, seq, json);
        }

        private void SendError(ushort seq, string methodName, string error)
        {
            var resp = new RpcCallResponse
            {
                sequence = seq,
                methodName = methodName,
                success = false,
                resultJson = null,
                error = error,
                executionTimeMs = 0
            };
            string json = MessageSerializer.SerializeBody(resp);
            _client.SendPacket(Command.RPC_RESPONSE, seq, json);
        }
    }

    /// <summary>
    /// JSON 数组包装器（用于解析 string[] 参数）
    /// </summary>
    [Serializable]
    internal class StringArrayWrapper
    {
        public string[] items;
    }
}
