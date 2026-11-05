// ============================================================================
// MessageSerializer.cs
// 消息序列化/反序列化工具 - 基于Unity JsonUtility
// Shared层 - 序列化工具
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using RemoteInspector.Commands;

namespace RemoteInspector.Protocol
{
    /// <summary>
    /// TCP消息序列化/反序列化器
    /// 使用Unity JsonUtility，不依赖第三方库
    /// </summary>
    public static class MessageSerializer
    {
        /// <summary>
        /// 序列化消息包为字节数组
        /// 格式：[4字节int32长度][2字节ushort命令ID][2字节ushort序列号][包体]
        /// 包体由 packet.bodyJson 或 packet.bodyBytes 决定(2026-06-25 加 Binary 路径支持):
        ///   - bodyBytes != null: 直接拷贝(binary command,NODE_TREE_RESPONSE_BINARY 等)
        ///   - bodyJson != null: UTF-8 编码(JSON command,旧路径)
        /// </summary>
        /// <param name="packet">消息包</param>
        /// <returns>完整的字节数组（包含包头）</returns>
        public static byte[] Serialize(MessagePacket packet)
        {
            if (packet == null)
                throw new ArgumentNullException(nameof(packet));

            // 序列化包体
            byte[] bodyBytes = null;
            int bodyLength = 0;

            // 优先 bodyBytes(二进制路径) — 不走 UTF-8 编码
            if (packet.bodyBytes != null && packet.bodyBytes.Length > 0)
            {
                bodyBytes = packet.bodyBytes;
                bodyLength = bodyBytes.Length;
            }
            else if (!string.IsNullOrEmpty(packet.bodyJson))
            {
                bodyBytes = Encoding.UTF8.GetBytes(packet.bodyJson);
                bodyLength = bodyBytes.Length;
            }

            // 总长度 = 包头(8) + 包体
            int totalLength = MessageProtocol.HEADER_SIZE + bodyLength;
            byte[] buffer = new byte[totalLength];

            // 写入包头
            // 偏移0-3: 4字节int32，总长度（包含包头自己）
            WriteInt32(buffer, 0, totalLength);

            // 偏移4-5: 2字节ushort，命令ID
            WriteUInt16(buffer, 4, packet.commandId);

            // 偏移6-7: 2字节ushort，序列号
            WriteUInt16(buffer, 6, packet.sequence);

            // 写入包体
            if (bodyBytes != null && bodyLength > 0)
            {
                Buffer.BlockCopy(bodyBytes, 0, buffer, MessageProtocol.HEADER_SIZE, bodyLength);
            }

            return buffer;
        }

        /// <summary>
        /// 反序列化字节数组为消息包（不解码具体 body,只解析包头 + 提取 body bytes / JSON）
        /// 根据 commandId 决定 body 是 Binary 还是 JSON:
        ///   - Binary command(NODE_TREE_RESPONSE_BINARY / NODE_DETAIL_RESPONSE_BINARY):
        ///     body 直接放 packet.bodyBytes,不 UTF-8 解码
        ///   - JSON command:body UTF-8 解码为 bodyJson
        /// </summary>
        /// <param name="data">完整的字节数据</param>
        /// <returns>消息包，不完整则返回null</returns>
        public static MessagePacket Deserialize(byte[] data)
        {
            if (data == null || data.Length < MessageProtocol.HEADER_SIZE)
                return null;

            // 读取包头
            int totalLength = ReadInt32(data, 0);
            ushort commandId = ReadUInt16(data, 4);
            ushort sequence = ReadUInt16(data, 6);

            // 检查数据完整性
            if (data.Length < totalLength || totalLength < MessageProtocol.HEADER_SIZE)
                return null;

            MessagePacket packet = new MessagePacket();
            packet.commandId = commandId;
            packet.sequence = sequence;

            if (totalLength > MessageProtocol.HEADER_SIZE)
            {
                int bodyLength = totalLength - MessageProtocol.HEADER_SIZE;
                byte[] bodyBytes = new byte[bodyLength];
                Buffer.BlockCopy(data, MessageProtocol.HEADER_SIZE, bodyBytes, 0, bodyLength);

                // 根据 commandId 决定走 binary 还是 JSON
                if (packet.IsBinaryCommand)
                {
                    packet.bodyBytes = bodyBytes;
                }
                else
                {
                    packet.bodyJson = Encoding.UTF8.GetString(bodyBytes);
                }
            }

            return packet;
        }

        /// <summary>
        /// 反序列化包体JSON为指定类型对象
        /// </summary>
        /// <typeparam name="T">目标类型</typeparam>
        /// <param name="packet">消息包</param>
        /// <returns>反序列化后的对象，失败返回null</returns>
        public static T DeserializeBody<T>(MessagePacket packet) where T : class
        {
            if (packet == null || string.IsNullOrEmpty(packet.bodyJson))
                return null;

            try
            {
                return UnityEngine.JsonUtility.FromJson<T>(packet.bodyJson);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 序列化对象为JSON字符串
        /// </summary>
        /// <typeparam name="T">对象类型</typeparam>
        /// <param name="obj">对象实例</param>
        /// <returns>JSON字符串</returns>
        public static string SerializeBody<T>(T obj) where T : class
        {
            if (obj == null)
                return null;

            try
            {
                return UnityEngine.JsonUtility.ToJson(obj);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 创建并序列化消息包
        /// </summary>
        /// <typeparam name="T">包体类型</typeparam>
        /// <param name="cmd">命令ID</param>
        /// <param name="seq">序列号</param>
        /// <param name="body">包体对象</param>
        /// <returns>完整的字节数组</returns>
        public static byte[] CreatePacket<T>(Command cmd, ushort seq, T body) where T : class
        {
            string bodyJson = SerializeBody(body);
            MessagePacket packet = new MessagePacket(cmd, seq, bodyJson);
            return Serialize(packet);
        }

        // ==================== 辅助方法 ====================

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24);
        }

        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }
    }
}
