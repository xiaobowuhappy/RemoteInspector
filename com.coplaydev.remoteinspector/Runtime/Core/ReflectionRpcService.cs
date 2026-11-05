using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using RemoteInspector.Commands;
using RemoteInspector.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RemoteInspector.Runtime.Core
{
    internal sealed class ReflectionRpcService
    {
        private readonly RpcHandler _registered;
        private readonly Dictionary<string, Type[]> _typeCache = new Dictionary<string, Type[]>();
        private readonly List<object> _registeredTargets = new List<object>();
        private static readonly BindingFlags MethodFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        public ReflectionRpcService(RpcHandler registered) { _registered = registered; }
        private static Assembly AssemblyFor(string name)
        {
            var matches = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.FullName == name).ToArray();
            if (matches.Length != 1) throw new ArgumentException("程序集已变化或不唯一，请刷新目录。");
            return matches[0];
        }
        private static Type TypeFor(ReflectionRpcRequest request)
        { return AssemblyFor(request.assembly).GetType(request.type, true, false); }
        private static string Key(MethodInfo method)
        { return method.DeclaringType.FullName + "." + method.Name + "(" + string.Join(",", method.GetParameters().Select(p => p.ParameterType.AssemblyQualifiedName)) + ")"; }
        private static string Reason(MethodInfo method)
        {
            if (method.ContainsGenericParameters || method.IsAbstract || method.IsSpecialName) return "开放泛型、抽象或属性/事件访问器不可调用";
            if (method.GetParameters().Any(p => p.IsOut || p.ParameterType.IsByRef || !RpcArguments.Supports(p.ParameterType))) return "存在暂不支持的参数（ref/out/对象引用或复杂类型）";
            if (typeof(System.Threading.Tasks.Task).IsAssignableFrom(method.ReturnType) || typeof(System.Collections.IEnumerator).IsAssignableFrom(method.ReturnType) ||
                method.GetCustomAttributes(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), false).Length > 0) return "首版仅支持同步方法";
            if (method.ReturnType.IsPointer || method.ReturnType.IsByRef) return "暂不支持指针返回值";
            return "";
        }
        private static ReflectionRpcMethod Describe(MethodInfo method)
        {
            string reason = Reason(method);
            return new ReflectionRpcMethod { key = Key(method), name = method.Name, isStatic = method.IsStatic, callable = reason.Length == 0, reason = reason,
                signature = (method.IsStatic ? "static " : "实例 ") + method.ReturnType.Name + " " + method.Name + "(" + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")",
                parameters = method.GetParameters().Select(p => new ReflectionRpcParameter { name = p.Name, type = p.ParameterType.FullName, optional = p.IsOptional,
                    defaultJson = RpcArguments.Supports(p.ParameterType) ? RpcArguments.Default(p) : "null",
                    choices = p.ParameterType.IsEnum ? Enum.GetNames(p.ParameterType) : p.ParameterType == typeof(bool) ? new[] { "true", "false" } : null }).ToList() };
        }
        public ReflectionRpcResponse Query(ReflectionRpcRequest request)
        {
            var result = new ReflectionRpcResponse { success = true, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).OrderBy(a => a.GetName().Name == "Assembly-CSharp" ? 0 : 1).ThenBy(a => a.FullName).ToArray();
            result.assemblies = assemblies.Select(a => a.FullName).ToList();
            if (!string.IsNullOrEmpty(request.type))
            {
                result.methods = TypeFor(request).GetMethods(MethodFlags).Where(m => !m.IsSpecialName && !m.Name.StartsWith("<", StringComparison.Ordinal)).OrderBy(m => m.Name).ThenBy(Key).Take(1000).Select(Describe).ToList();
                return result;
            }
            if (request.refresh) _typeCache.Clear();
            string[] words = (request.search ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var assembly in assemblies.Where(a => string.IsNullOrEmpty(request.assembly) || a.FullName == request.assembly))
            {
                Type[] types;
                if (!_typeCache.TryGetValue(assembly.FullName, out types))
                {
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                    catch { continue; }
                    _typeCache[assembly.FullName] = types;
                }
                foreach (var type in types.Where(t => t.IsClass && !t.ContainsGenericParameters && t.FullName != null && !t.Name.StartsWith("<", StringComparison.Ordinal)).OrderBy(t => t.FullName))
                {
                    if (!words.All(w => type.FullName.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    if (result.types.Count == 500) { result.truncated = true; return result; }
                    result.types.Add(new ReflectionRpcType { assembly = assembly.FullName, name = type.FullName });
                }
            }
            return result;
        }
        private List<KeyValuePair<ReflectionRpcInstance, object>> FindInstances(ReflectionRpcRequest request, Type type)
        {
            var result = new List<KeyValuePair<ReflectionRpcInstance, object>>();
            if (typeof(Component).IsAssignableFrom(type))
            {
                var active = SceneManager.GetActiveScene();
                foreach (var obj in Resources.FindObjectsOfTypeAll(type))
                {
                    var component = obj as Component;
                    if (component == null || !component.gameObject.scene.IsValid() || !component.gameObject.scene.isLoaded) continue;
                    bool current = component.gameObject.scene == active;
                    bool enabled = component.gameObject.activeInHierarchy && (!(component is Behaviour) || ((Behaviour)component).enabled);
                    if ((!request.includeInactive && !enabled) || (!request.includeOtherScenes && !current)) continue;
                    var parts = new List<string>(); var transform = component.transform;
                    while (transform != null) { parts.Add(transform.name); transform = transform.parent; } parts.Reverse();
                    var info = new ReflectionRpcInstance { id = component.GetInstanceID(), priority = (current ? 0 : 2) + (enabled ? 0 : 1),
                        path = component.gameObject.scene.name + "/" + string.Join("/", parts), state = (current ? "当前场景" : "其他场景") + (enabled ? " · 激活" : " · 未激活/禁用") };
                    result.Add(new KeyValuePair<ReflectionRpcInstance, object>(info, component));
                }
            }
            else
            {
                var targets = _registered.GetRegisteredTargets();
                for (int retired = 0; retired < _registeredTargets.Count; retired++)
                    if (!targets.Any(v => ReferenceEquals(v, _registeredTargets[retired]))) _registeredTargets[retired] = null;
                foreach (var target in targets.Where(type.IsInstanceOfType))
                {
                    int i = _registeredTargets.FindIndex(v => ReferenceEquals(v, target));
                    if (i < 0) { if (_registeredTargets.Count >= 256) break; _registeredTargets.Add(target); i = _registeredTargets.Count - 1; }
                    // Negative IDs are scoped to this handler; no constructors/singleton getters are executed.
                    result.Add(new KeyValuePair<ReflectionRpcInstance, object>(new ReflectionRpcInstance { id = -(i + 1), path = type.FullName + "（已注册对象 " + (i + 1) + "）", state = "已注册实例", priority = 0 }, target));
                }
            }
            return result.OrderBy(p => p.Key.priority).ThenBy(p => p.Key.path).Take(500).ToList();
        }
        public void Handle(MessagePacket packet, RemoteInspectorClient client)
        {
            var command = packet.GetCommand();
            ushort response = command == (Command)ReflectionRpcProtocol.Query ? ReflectionRpcProtocol.QueryResponse : command == (Command)ReflectionRpcProtocol.Instances ? ReflectionRpcProtocol.InstancesResponse : ReflectionRpcProtocol.InvokeResponse;
            var result = new ReflectionRpcResponse();
            var timer = new Stopwatch();
            try
            {
                var request = MessageSerializer.DeserializeBody<ReflectionRpcRequest>(packet) ?? throw new ArgumentException("无效请求。");
                if (command == (Command)ReflectionRpcProtocol.Query) result = Query(request);
                else
                {
                    var type = TypeFor(request);
                    if (command == (Command)ReflectionRpcProtocol.Instances) result.instances = FindInstances(request, type).Select(p => p.Key).ToList();
                    else
                    {
                        var method = type.GetMethods(MethodFlags).SingleOrDefault(m => Key(m) == request.method) ?? throw new ArgumentException("方法签名已变化，请刷新方法列表。");
                        string reason = Reason(method); if (reason.Length > 0) throw new ArgumentException(reason);
                        object target = null;
                        if (!method.IsStatic)
                        {
                            var candidates = FindInstances(request, type);
                            if (request.targetId != 0)
                            {
                                var selected = candidates.Where(p => p.Key.id == request.targetId).ToArray();
                                if (selected.Length != 1) throw new ArgumentException("实例已销毁或不在查询范围，请刷新实例。");
                                target = selected[0].Value; result.target = selected[0].Key.path;
                            }
                            else
                            {
                                var preferred = candidates.Where(p => p.Key.priority == 0).ToArray();
                                if (preferred.Length != 1) throw new ArgumentException(preferred.Length == 0 ? "没有当前场景的激活实例，请选择实例或扩大范围。" : "存在多个激活实例，请明确选择调用目标。");
                                target = preferred[0].Value; result.target = preferred[0].Key.path;
                            }
                        }
                        var args = RpcArguments.Bind(method, request.argsJson);
                        timer.Start(); object value = method.Invoke(target, args); timer.Stop();
                        result.resultJson = method.ReturnType == typeof(void) ? "\"ok\"" : RpcArguments.Result(value);
                        result.executionTimeMs = timer.Elapsed.TotalMilliseconds;
                    }
                    result.success = true; result.timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }
            catch (Exception ex)
            {
                timer.Stop(); result.success = false; result.executionTimeMs = timer.Elapsed.TotalMilliseconds;
                var actual = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                result.error = actual.ToString();
            }
            client.SendPacket((Command)response, packet.sequence, MessageSerializer.SerializeBody(result));
        }
    }
}
