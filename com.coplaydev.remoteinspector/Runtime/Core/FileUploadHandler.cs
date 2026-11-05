// ============================================================================
// FileUploadHandler.cs
// Runtime 端文件上传接收器 - 接收 Editor 发来的 FILE_UPLOAD_START/CHUNK/END,
// 按 relativePath 拼到 persistentDataPath 下,写到磁盘;完成后回 FILE_UPLOAD_ACK
// Runtime 层 - 核心网络层
// ============================================================================

using System;
using System.IO;
using UnityEngine;
using RemoteInspector.Commands;
using RemoteInspector.Protocol;

namespace RemoteInspector.Runtime.Core
{
    /// <summary>文件上传会话状态</summary>
    public enum FileUploadState
    {
        Idle,
        Receiving,
        Completed,
        Failed
    }

    /// <summary>单个文件上传会话(START/CHUNK/END 共用一个)</summary>
    public class FileUploadSession
    {
        public string relativePath;
        public string fileName;
        public long totalSize;
        public string md5;
        public int totalChunks;
        public int receivedChunks;
        public FileUploadState state;
        public string error;
        public FileStream fileStream;
        public string actualDiskPath; // 真实写入磁盘的路径
        public DateTime startTime;
        public float progress => totalSize > 0 ? (float)receivedChunks / totalChunks : 0f;
    }

    /// <summary>
    /// Runtime 端文件上传接收器。
    /// 流程:START 创建临时文件 → CHUNK 写入字节流 → END 关闭流 + 验证大小 + ACK。
    /// </summary>
    public class FileUploadHandler
    {
        private readonly RemoteInspectorClient _client;
        private FileUploadSession _currentSession;

        /// <summary>当前正在进行的会话</summary>
        public FileUploadSession CurrentSession => _currentSession;

        public FileUploadHandler(RemoteInspectorClient client)
        {
            _client = client;
        }

        public void HandleUploadStart(FileUploadStartRequest req)
        {
            if (_currentSession != null && _currentSession.state == FileUploadState.Receiving)
            {
                Debug.LogWarning($"[RemoteInspector] Upload rejected, another in progress: '{_currentSession.fileName}'");
                SendAck(req.sequence, false, "Another upload in progress");
                return;
            }

            _currentSession = new FileUploadSession
            {
                relativePath = req.relativePath,
                fileName = req.fileName,
                totalSize = req.totalSize,
                md5 = req.md5,
                totalChunks = req.totalChunks,
                state = FileUploadState.Receiving,
                startTime = DateTime.Now,
            };

            // 解析磁盘真实路径:relativePath 是 Editor 端的相对路径(可能带子目录),
            //   这里拼到 Application.persistentDataPath 下,自动创建父目录
            string diskPath = ResolveDiskPath(req.relativePath);
            _currentSession.actualDiskPath = diskPath;

            try
            {
                string dir = Path.GetDirectoryName(diskPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                _currentSession.fileStream = new FileStream(diskPath, FileMode.Create, FileAccess.Write);
                Debug.Log($"[RemoteInspector] Upload START: '{req.relativePath}' ({req.totalSize} bytes, " +
                          $"{req.totalChunks} chunks) → {diskPath}");
            }
            catch (Exception ex)
            {
                _currentSession.state = FileUploadState.Failed;
                _currentSession.error = ex.Message;
                Debug.LogError($"[RemoteInspector] Upload START failed: {ex.Message}");
                SendAck(req.sequence, false, ex.Message);
                _currentSession = null;
            }
        }

        public void HandleUploadChunk(FileUploadChunkRequest req)
        {
            if (_currentSession == null || _currentSession.state != FileUploadState.Receiving)
            {
                Debug.LogWarning($"[RemoteInspector] Chunk received but no active upload session");
                return;
            }

            try
            {
                byte[] data = Convert.FromBase64String(req.data);
                _currentSession.fileStream.Write(data, 0, data.Length);
                _currentSession.receivedChunks++;
            }
            catch (Exception ex)
            {
                _currentSession.state = FileUploadState.Failed;
                _currentSession.error = ex.Message;
                Debug.LogError($"[RemoteInspector] Upload CHUNK failed at {req.chunkIndex}: {ex.Message}");
                SendAck(req.sequence, false, ex.Message);
                _currentSession.fileStream?.Dispose();
                _currentSession.fileStream = null;
                _currentSession = null;
            }
        }

        public void HandleUploadEnd(FileUploadEndRequest req)
        {
            if (_currentSession == null)
            {
                Debug.LogWarning("[RemoteInspector] Upload END but no active session");
                return;
            }

            try
            {
                _currentSession.fileStream?.Flush();
                _currentSession.fileStream?.Dispose();
                _currentSession.fileStream = null;

                // 验证大小
                long actualSize = new FileInfo(_currentSession.actualDiskPath).Length;
                if (actualSize != _currentSession.totalSize)
                {
                    throw new Exception($"Size mismatch: expected {_currentSession.totalSize}, got {actualSize}");
                }

                _currentSession.state = FileUploadState.Completed;
                Debug.Log($"[RemoteInspector] Upload DONE: '{_currentSession.relativePath}' → " +
                          $"{_currentSession.actualDiskPath} ({actualSize} bytes)");

                SendAck(req.sequence, true, null, _currentSession.actualDiskPath);
            }
            catch (Exception ex)
            {
                _currentSession.state = FileUploadState.Failed;
                _currentSession.error = ex.Message;
                Debug.LogError($"[RemoteInspector] Upload END failed: {ex.Message}");
                SendAck(req.sequence, false, ex.Message);
            }
            finally
            {
                _currentSession = null;
            }
        }

        /// <summary>
        /// 立即关闭所有进行中的上传会话并释放 FileStream 句柄。
        /// 由 RemoteInspectorClient.Disconnect() 调用 — Client 断线时正在 CHUNK 阶段的 session
        /// 永远不会收到 END,FileStream 不会被释放(进程级资源泄漏),需要强制 abort。
        /// 同时删除 partial 写入磁盘的临时文件(防止污染下次上传)。
        /// </summary>
        public void DisposeAll()
        {
            if (_currentSession == null) return;

            string relPath = _currentSession.relativePath;
            string diskPath = _currentSession.actualDiskPath;
            try
            {
                if (_currentSession.fileStream != null)
                {
                    _currentSession.fileStream.Flush();
                    _currentSession.fileStream.Dispose();
                    _currentSession.fileStream = null;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RemoteInspector] DisposeAll flush/dispose failed: {ex.Message}");
            }

            // 删除 partial 临时文件(可能未完成上传)
            if (!string.IsNullOrEmpty(diskPath))
            {
                try
                {
                    if (File.Exists(diskPath))
                    {
                        File.Delete(diskPath);
                        Debug.Log($"[RemoteInspector] Aborted partial upload, deleted: {diskPath}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[RemoteInspector] Failed to delete partial upload '{diskPath}': {ex.Message}");
                }
            }

            _currentSession.state = FileUploadState.Failed;
            _currentSession.error = "Client disconnected";
            _currentSession = null;

            Debug.Log($"[RemoteInspector] FileUploadHandler disposed (was uploading '{relPath}')");
        }

        /// <summary>
        /// 把 Editor 端 relativePath 解析到 Runtime 磁盘路径。
        /// 默认根目录:Application.persistentDataPath/(不再加 RemoteInspectorUploads 中间层,
        ///   直接落在 persistentDataPath 下,跟 Editor 端显示的相对路径一一对应)
        /// </summary>
        private static string ResolveDiskPath(string relativePath)
        {
            string rel = relativePath ?? "";
            // 去掉前导斜杠
            rel = rel.TrimStart('/', '\\');
            // 把所有 '\\' 统一为 '/'
            rel = rel.Replace('\\', '/');
            return Path.Combine(Application.persistentDataPath, rel);
        }

        private void SendAck(ushort seq, bool success, string error, string fullPath = null)
        {
            var ack = new FileUploadAck
            {
                sequence = seq,
                success = success,
                error = error ?? string.Empty,
                fullPath = fullPath ?? string.Empty,
            };
            string json = MessageSerializer.SerializeBody(ack);
            if (_client != null)
                _client.SendPacket(Command.FILE_UPLOAD_ACK, seq, json);
            else
                Debug.LogError($"[RemoteInspector] FileUploadHandler.SendAck: _client is null!");
        }
    }
}