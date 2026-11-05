// ============================================================================
// BinaryNodeTreeBuilder.cs
// 把 Runtime NodeTreeCollector.NodeTreeResponse 转为 BinaryNodeTreeDto,
// 调用 BinaryNodeTreeCodec 编码为 byte[] 发送。
//
// 2026-06-25 海量场景性能优化第一波 — 在 Runtime 端构建二进制协议。
// ============================================================================

using System.Collections.Generic;
using RemoteInspector.Runtime.Collectors;
using RemoteInspector.Protocol;

namespace RemoteInspector.Runtime.Core
{
    /// <summary>
    /// Runtime NodeTreeResponse → BinaryNodeTreeDto 适配器
    /// </summary>
    public static class BinaryNodeTreeBuilder
    {
        /// <summary>
        /// 构建 binary DTO,顺便填充 stringPool(component name 频次 ≥ 2 入池)。
        /// </summary>
        public static BinaryNodeTreeDto Build(NodeTreeCollector.NodeTreeResponse resp)
        {
            if (resp == null) return null;

            var dto = new BinaryNodeTreeDto
            {
                allNodes = new List<BinaryNodeDto>(resp.allNodes?.Count ?? 0),
                rootCount = (ushort)(resp.rootNodes?.Count ?? 0),
            };

            // ─── Build allNodes + 收集 component name 频次 ───
            var freq = new Dictionary<string, int>(256);
            if (resp.allNodes != null)
            {
                foreach (var src in resp.allNodes)
                {
                    bool isDDOL = (src.path ?? "").StartsWith("[DDOL] ", System.StringComparison.Ordinal);
                    var node = new BinaryNodeDto
                    {
                        instanceId = src.instanceId,
                        path = src.path ?? "",
                        activeSelf = src.activeSelf,
                        activeInHierarchy = src.activeInHierarchy,
                        isDontDestroyOnLoad = isDDOL,
                        tag = src.tag ?? "",
                        layer = src.layer,
                        childIds = src.childIds != null ? new List<int>(src.childIds) : new List<int>(),
                        componentTypes = src.componentTypes != null ? new List<string>(src.componentTypes) : new List<string>(),
                    };
                    dto.allNodes.Add(node);

                    if (src.componentTypes != null)
                    {
                        foreach (var ct in src.componentTypes)
                        {
                            if (string.IsNullOrEmpty(ct)) continue;
                            freq.TryGetValue(ct, out var c);
                            freq[ct] = c + 1;
                        }
                    }
                }
            }

            // ─── String pool:频次 ≥ 2 的 component name 入池 ───
            var pool = new List<string>(128);
            foreach (var kv in freq)
            {
                if (kv.Value >= 2)
                    pool.Add(kv.Key);
            }
            dto.stringPool = pool;

            // ─── Root indices:把所有 root 节点的 allNodes 索引收集 ───
            dto.rootIndices = new int[dto.rootCount];
            if (resp.rootNodes != null)
            {
                var indexById = new Dictionary<int, int>(dto.allNodes.Count);
                for (int i = 0; i < dto.allNodes.Count; i++)
                    indexById[dto.allNodes[i].instanceId] = i;

                for (int i = 0; i < dto.rootCount; i++)
                {
                    var r = resp.rootNodes[i];
                    dto.rootIndices[i] = indexById.TryGetValue(r.instanceId, out var ix) ? ix : 0;
                }
            }

            return dto;
        }

        /// <summary>一站式:Build → Encode → byte[]</summary>
        public static byte[] EncodeResponse(NodeTreeCollector.NodeTreeResponse resp)
        {
            var dto = Build(resp);
            if (dto == null) return null;
            return BinaryNodeTreeCodec.Encode(dto);
        }
    }
}
