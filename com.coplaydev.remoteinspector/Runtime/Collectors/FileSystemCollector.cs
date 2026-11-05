// ============================================================================
// FileSystemCollector.cs
// 文件系统操作采集器 - 读取/操作 Application.persistentDataPath 目录
// Runtime层 - Collectors
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using RemoteInspector.Protocol;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace RemoteInspector.Runtime.Collectors
{
    /// <summary>
    /// 文件系统采集器
    /// 所有路径均相对于 Application.persistentDataPath
    /// </summary>
    public class FileSystemCollector
    {
        /// <summary>
        /// 列出指定目录下的所有文件和子目录
        /// </summary>
        /// <param name="relativePath">相对路径，空/null 表示根目录</param>
        public FileBrowseListResponse ListDirectory(string relativePath)
        {
            var resp = new FileBrowseListResponse();
            resp.persistentDataPath = Application.persistentDataPath;

            try
            {
                string absPath = ResolveAbsPath(relativePath);

                // 安全检查：不允许越过 persistentDataPath
                if (!IsInsidePersistentData(absPath))
                {
                    resp.success = false;
                    resp.error = "Path is outside persistentDataPath";
                    return resp;
                }

                if (!Directory.Exists(absPath))
                {
                    resp.success = false;
                    resp.error = $"Directory not found: {relativePath}";
                    return resp;
                }

                var entries = new List<FileEntry>();

                // 子目录
                foreach (string dir in Directory.GetDirectories(absPath))
                {
                    string name = Path.GetFileName(dir);
                    string relChild = MakeRelative(dir);
                    long lastMod = 0;
                    try
                    {
                        lastMod = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir)).ToUnixTimeMilliseconds();
                    }
                    catch { }

                    entries.Add(new FileEntry
                    {
                        name = name,
                        relativePath = relChild,
                        isDirectory = true,
                        size = 0,
                        lastModified = lastMod
                    });
                }

                // 文件
                foreach (string file in Directory.GetFiles(absPath))
                {
                    string name = Path.GetFileName(file);
                    string relChild = MakeRelative(file);
                    long size = 0;
                    long lastMod = 0;
                    try
                    {
                        var fi = new FileInfo(file);
                        size = fi.Length;
                        lastMod = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                    }
                    catch { }

                    entries.Add(new FileEntry
                    {
                        name = name,
                        relativePath = relChild,
                        isDirectory = false,
                        size = size,
                        lastModified = lastMod
                    });
                }

                resp.success = true;
                resp.relativePath = NormalizePath(relativePath);
                resp.entries = entries.ToArray();
            }
            catch (Exception ex)
            {
                resp.success = false;
                resp.error = ex.Message;
                Debug.LogError($"[RemoteInspector] FileSystemCollector.ListDirectory error: {ex.Message}");
            }

            return resp;
        }

        /// <summary>
        /// 创建目录
        /// </summary>
        public FileBrowseMkdirResponse CreateDirectory(string relativePath)
        {
            var resp = new FileBrowseMkdirResponse();
            resp.parentRelativePath = GetParentRelative(relativePath);

            try
            {
                string absPath = ResolveAbsPath(relativePath);

                if (!IsInsidePersistentData(absPath))
                {
                    resp.success = false;
                    resp.error = "Path is outside persistentDataPath";
                    return resp;
                }

                if (Directory.Exists(absPath))
                {
                    resp.success = false;
                    resp.error = $"Directory already exists: {relativePath}";
                    return resp;
                }

                Directory.CreateDirectory(absPath);
                resp.success = true;
                Debug.Log($"[RemoteInspector] Created directory: {absPath}");
            }
            catch (Exception ex)
            {
                resp.success = false;
                resp.error = ex.Message;
                Debug.LogError($"[RemoteInspector] FileSystemCollector.CreateDirectory error: {ex.Message}");
            }

            return resp;
        }

        /// <summary>
        /// 删除文件或目录
        /// </summary>
        public FileBrowseDeleteResponse Delete(string relativePath, bool isDirectory)
        {
            var resp = new FileBrowseDeleteResponse();
            resp.parentRelativePath = GetParentRelative(relativePath);

            try
            {
                string absPath = ResolveAbsPath(relativePath);

                if (!IsInsidePersistentData(absPath))
                {
                    resp.success = false;
                    resp.error = "Path is outside persistentDataPath";
                    return resp;
                }

                if (isDirectory)
                {
                    if (!Directory.Exists(absPath))
                    {
                        resp.success = false;
                        resp.error = $"Directory not found: {relativePath}";
                        return resp;
                    }
                    Directory.Delete(absPath, recursive: true);
                    Debug.Log($"[RemoteInspector] Deleted directory: {absPath}");
                }
                else
                {
                    if (!File.Exists(absPath))
                    {
                        resp.success = false;
                        resp.error = $"File not found: {relativePath}";
                        return resp;
                    }
                    File.Delete(absPath);
                    Debug.Log($"[RemoteInspector] Deleted file: {absPath}");
                }

                resp.success = true;
            }
            catch (Exception ex)
            {
                resp.success = false;
                resp.error = ex.Message;
                Debug.LogError($"[RemoteInspector] FileSystemCollector.Delete error: {ex.Message}");
            }

            return resp;
        }

        /// <summary>
        /// 读取文件内容（分块方式，由 MessageHandler 调用时逐块产生）
        /// 返回所有分块数据
        /// </summary>
        public List<FileBrowseDownloadChunk> ReadFileChunks(string relativePath, ushort sequence)
        {
            var chunks = new List<FileBrowseDownloadChunk>();

            try
            {
                string absPath = ResolveAbsPath(relativePath);

                if (!IsInsidePersistentData(absPath))
                {
                    chunks.Add(new FileBrowseDownloadChunk
                    {
                        sequence = sequence,
                        relativePath = relativePath,
                        error = true,
                        errorMessage = "Path is outside persistentDataPath"
                    });
                    return chunks;
                }

                if (!File.Exists(absPath))
                {
                    chunks.Add(new FileBrowseDownloadChunk
                    {
                        sequence = sequence,
                        relativePath = relativePath,
                        error = true,
                        errorMessage = $"File not found: {relativePath}"
                    });
                    return chunks;
                }

                byte[] allBytes = File.ReadAllBytes(absPath);
                long totalSize = allBytes.Length;
                string fileName = Path.GetFileName(absPath);
                const int chunkSize = 64 * 1024; // 64KB
                int totalChunks = (int)Math.Ceiling((double)allBytes.Length / chunkSize);
                if (totalChunks == 0) totalChunks = 1;

                for (int i = 0; i < totalChunks; i++)
                {
                    int offset = i * chunkSize;
                    int len = Math.Min(chunkSize, allBytes.Length - offset);
                    byte[] slice = new byte[len];
                    Array.Copy(allBytes, offset, slice, 0, len);

                    chunks.Add(new FileBrowseDownloadChunk
                    {
                        sequence = sequence,
                        relativePath = relativePath,
                        fileName = fileName,
                        totalSize = totalSize,
                        chunkIndex = i,
                        totalChunks = totalChunks,
                        data = Convert.ToBase64String(slice),
                        isLast = (i == totalChunks - 1),
                        error = false
                    });
                }
            }
            catch (Exception ex)
            {
                chunks.Clear();
                chunks.Add(new FileBrowseDownloadChunk
                {
                    sequence = sequence,
                    relativePath = relativePath,
                    error = true,
                    errorMessage = ex.Message
                });
                Debug.LogError($"[RemoteInspector] FileSystemCollector.ReadFileChunks error: {ex.Message}");
            }

            return chunks;
        }

        /// <summary>
        /// 将目录打包为 zip 内存字节，然后分块返回（供下载用）
        /// </summary>
        public List<FileBrowseDownloadChunk> ReadDirectoryAsZipChunks(string relativePath, ushort sequence)
        {
            var chunks = new List<FileBrowseDownloadChunk>();

            try
            {
                string absPath = ResolveAbsPath(relativePath);

                if (!IsInsidePersistentData(absPath))
                {
                    chunks.Add(MakeErrorChunk(sequence, relativePath, "Path is outside persistentDataPath"));
                    return chunks;
                }

                if (!Directory.Exists(absPath))
                {
                    chunks.Add(MakeErrorChunk(sequence, relativePath, $"Directory not found: {relativePath}"));
                    return chunks;
                }

                byte[] zipBytes = ZipDirectory(absPath);
                long totalSize = zipBytes.Length;
                string fileName = Path.GetFileName(absPath) + ".zip";
                const int chunkSize = 64 * 1024;
                int totalChunks = (int)Math.Ceiling((double)zipBytes.Length / chunkSize);
                if (totalChunks == 0) totalChunks = 1;

                for (int i = 0; i < totalChunks; i++)
                {
                    int offset = i * chunkSize;
                    int len = Math.Min(chunkSize, zipBytes.Length - offset);
                    byte[] slice = new byte[len];
                    Array.Copy(zipBytes, offset, slice, 0, len);

                    chunks.Add(new FileBrowseDownloadChunk
                    {
                        sequence = sequence,
                        relativePath = relativePath,
                        fileName = fileName,
                        totalSize = totalSize,
                        chunkIndex = i,
                        totalChunks = totalChunks,
                        data = Convert.ToBase64String(slice),
                        isLast = (i == totalChunks - 1),
                        error = false
                    });
                }

                Debug.Log($"[RemoteInspector] Zipped directory '{relativePath}' -> {totalSize} bytes, {totalChunks} chunks");
            }
            catch (Exception ex)
            {
                chunks.Clear();
                chunks.Add(MakeErrorChunk(sequence, relativePath, ex.Message));
                Debug.LogError($"[RemoteInspector] FileSystemCollector.ReadDirectoryAsZipChunks error: {ex.Message}");
            }

            return chunks;
        }

        /// <summary>
        /// 将目录打包为 zip 字节数组（存于内存，不落盘）
        /// </summary>
        private byte[] ZipDirectory(string absDirPath)
        {
            using (var outStream = new MemoryStream())
            {
                using (var archive = new ZipArchive(outStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    string rootName = Path.GetFileName(absDirPath);

                    void AddEntries(string dirAbsPath, string entryPrefix)
                    {
                        foreach (string file in Directory.GetFiles(dirAbsPath))
                        {
                            string entryName = string.IsNullOrEmpty(entryPrefix)
                                ? Path.GetFileName(file)
                                : entryPrefix + "/" + Path.GetFileName(file);

                            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                            using (var es = entry.Open())
                            using (var fs = File.OpenRead(file))
                            {
                                fs.CopyTo(es);
                            }
                        }

                        foreach (string subDir in Directory.GetDirectories(dirAbsPath))
                        {
                            string subName = Path.GetFileName(subDir);
                            string subPrefix = string.IsNullOrEmpty(entryPrefix) ? subName : entryPrefix + "/" + subName;
                            AddEntries(subDir, subPrefix);
                        }
                    }

                    AddEntries(absDirPath, rootName);
                }
                return outStream.ToArray();
            }
        }

        private static FileBrowseDownloadChunk MakeErrorChunk(ushort sequence, string relativePath, string errorMessage)
        {
            return new FileBrowseDownloadChunk
            {
                sequence = sequence,
                relativePath = relativePath,
                error = true,
                errorMessage = errorMessage
            };
        }

        // ==================== 工具方法 ====================

        private string PersistentRoot => Application.persistentDataPath.Replace('\\', '/').TrimEnd('/');

        private string ResolveAbsPath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath) || relativePath == "/" || relativePath == ".")
                return Application.persistentDataPath;

            string norm = relativePath.Replace('\\', '/').Trim('/');
            return Path.Combine(Application.persistentDataPath, norm);
        }

        private string MakeRelative(string absPath)
        {
            string norm = absPath.Replace('\\', '/');
            string root = PersistentRoot;
            if (norm.StartsWith(root))
            {
                string rel = norm.Substring(root.Length).TrimStart('/');
                return rel;
            }
            return absPath;
        }

        private bool IsInsidePersistentData(string absPath)
        {
            string norm = Path.GetFullPath(absPath).Replace('\\', '/').TrimEnd('/');
            string root = Path.GetFullPath(Application.persistentDataPath).Replace('\\', '/').TrimEnd('/');
            return norm == root || norm.StartsWith(root + "/");
        }

        private string NormalizePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return "";
            return relativePath.Replace('\\', '/').Trim('/');
        }

        private string GetParentRelative(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return "";
            string norm = relativePath.Replace('\\', '/').TrimEnd('/');
            int idx = norm.LastIndexOf('/');
            return idx < 0 ? "" : norm.Substring(0, idx);
        }
    }
}
