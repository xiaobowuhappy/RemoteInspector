using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RemoteInspector.Runtime.Core
{
    internal static class RpcArguments
    {
        public static bool Supports(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsArray) return type.GetArrayRank() == 1 && Supports(type.GetElementType());
            return type.IsEnum || type == typeof(string) || type == typeof(bool) || type == typeof(char) ||
                type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
                type == typeof(float) || type == typeof(double) || type == typeof(decimal) ||
                type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) ||
                type == typeof(Color) || type == typeof(Quaternion) || type == typeof(Rect);
        }
        public static object[] Bind(MethodInfo method, string json)
        {
            if ((json ?? "").Length > 65536) throw new ArgumentException("参数最多 64 Ki 字符。");
            var tokens = JArray.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            var parameters = method.GetParameters();
            if (tokens.Count > parameters.Length) throw new ArgumentException("参数数量超过方法声明。");
            var result = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (i >= tokens.Count)
                {
                    if (!p.IsOptional) throw new ArgumentException("缺少参数：" + p.Name);
                    result[i] = p.DefaultValue; continue;
                }
                try { result[i] = ConvertToken(tokens[i], p.ParameterType); }
                catch (Exception ex) { throw new ArgumentException("参数 " + p.Name + "（" + p.ParameterType.Name + "）：" + ex.Message, ex); }
            }
            return result;
        }
        private static object ConvertToken(JToken token, Type type)
        {
            if (!Supports(type)) throw new ArgumentException("暂不支持此参数类型。");
            var nullable = Nullable.GetUnderlyingType(type);
            if (token.Type == JTokenType.Null)
            {
                if (!type.IsValueType || nullable != null) return null;
                throw new ArgumentException("值类型不能为 null。");
            }
            type = nullable ?? type;
            if (type.IsArray)
            {
                var values = token as JArray;
                if (values == null || values.Count > 4096) throw new ArgumentException("需要最多 4096 项的数组。");
                var result = Array.CreateInstance(type.GetElementType(), values.Count);
                for (int i = 0; i < values.Count; i++) result.SetValue(ConvertToken(values[i], type.GetElementType()), i);
                return result;
            }
            if (type == typeof(string) || type == typeof(char))
            {
                if (token.Type != JTokenType.String) throw new ArgumentException("需要带引号的字符串。");
                string text = token.Value<string>();
                if (type == typeof(char)) { if (text.Length != 1) throw new ArgumentException("需要单个字符。"); return text[0]; }
                return text;
            }
            if (type.IsEnum)
            {
                if (token.Type == JTokenType.String) return Enum.Parse(type, token.Value<string>(), true);
                if (token.Type != JTokenType.Integer) throw new ArgumentException("枚举需要名称或整数。");
                return Enum.ToObject(type, Convert.ChangeType(token.ToString(), Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture));
            }
            if (type == typeof(bool))
            {
                if (token.Type != JTokenType.Boolean) throw new ArgumentException("需要 true 或 false。");
                return token.Value<bool>();
            }
            if (type.Namespace == "UnityEngine")
            {
                if (token.Type != JTokenType.Object) throw new ArgumentException("需要包含 x/y/z 或 r/g/b 等字段的 JSON 对象。");
                return JsonUtility.FromJson(token.ToString(Formatting.None), type);
            }
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) throw new ArgumentException("需要 JSON 数字。");
            bool integral = type != typeof(float) && type != typeof(double) && type != typeof(decimal);
            if (integral && token.Type != JTokenType.Integer) throw new ArgumentException("需要整数。");
            object value = Convert.ChangeType(token.ToString(Formatting.None), type, CultureInfo.InvariantCulture);
            if ((value is double && (double.IsNaN((double)value) || double.IsInfinity((double)value))) ||
                (value is float && (float.IsNaN((float)value) || float.IsInfinity((float)value)))) throw new ArgumentException("数值超出范围。");
            return value;
        }
        public static string Result(object value)
        {
            var unityObject = value as UnityEngine.Object;
            if (unityObject != null) return JsonConvert.SerializeObject(new { name = unityObject.name, instanceId = unityObject.GetInstanceID() });
            if (value == null) return "null";
            if (Supports(value.GetType())) return value.GetType().Namespace == "UnityEngine" ? JsonUtility.ToJson(value) : JsonConvert.SerializeObject(value);
            // Do not recursively inspect arbitrary object graphs or execute property getters.
            return JsonConvert.SerializeObject(value.ToString());
        }
        public static string Default(ParameterInfo parameter)
        {
            if (parameter.IsOptional && parameter.DefaultValue != DBNull.Value && parameter.DefaultValue != Type.Missing) return parameter.ParameterType.IsEnum ? JsonConvert.SerializeObject(parameter.DefaultValue.ToString()) : Result(parameter.DefaultValue);
            var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
            if (type.IsEnum) return JsonConvert.SerializeObject(Enum.GetNames(type).FirstOrDefault() ?? "0");
            if (type == typeof(string)) return "\"\"";
            if (type == typeof(char)) return "\"a\"";
            if (type == typeof(bool)) return "false";
            if (type.IsArray) return "[]";
            if (type.Namespace == "UnityEngine") return "{}";
            return type.IsValueType ? "0" : "null";
        }
    }
}
