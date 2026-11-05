using System;
using System.Collections.Generic;
namespace RemoteInspector.Protocol
{
    public static class ReflectionRpcProtocol
    {
        public const ushort Query = 0x0098, QueryResponse = 0x0099, Invoke = 0x009a, InvokeResponse = 0x009b, Instances = 0x009c, InstancesResponse = 0x009d;
    }
    [Serializable] public class ReflectionRpcRequest
    {
        public string assembly, type, method, search, argsJson;
        public bool refresh, includeInactive, includeOtherScenes;
        public int targetId;
    }
    [Serializable] public class ReflectionRpcType
    {
        public string assembly, name;
    }
    [Serializable] public class ReflectionRpcParameter
    {
        public string name, type, defaultJson;
        public bool optional;
        public string[] choices;
    }
    [Serializable] public class ReflectionRpcMethod
    {
        public string key, name, signature, reason;
        public bool isStatic, callable;
        public List<ReflectionRpcParameter> parameters = new List<ReflectionRpcParameter>();
    }
    [Serializable] public class ReflectionRpcInstance
    {
        public int id, priority;
        public string path, state;
    }
    [Serializable] public class ReflectionRpcResponse
    {
        public bool success, truncated;
        public string error, resultJson, target;
        public double executionTimeMs;
        public long timestamp;
        public List<string> assemblies = new List<string>();
        public List<ReflectionRpcType> types = new List<ReflectionRpcType>();
        public List<ReflectionRpcMethod> methods = new List<ReflectionRpcMethod>();
        public List<ReflectionRpcInstance> instances = new List<ReflectionRpcInstance>();
    }
}
