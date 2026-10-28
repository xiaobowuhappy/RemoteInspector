using System.Security.Cryptography;
using RemoteInspector.Commands;
using RemoteInspector.Protocol;

namespace RemoteInspector.Core;

public static class Transfers
{
    public static async Task<byte[]> CaptureAsync(this InspectorSession session, int maxSize = 1280, CancellationToken token = default)
    {
        using var exchange = session.OpenExchange(Command.SCREEN_CAPTURE_CHUNK);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await session.SendAtAsync(Command.SCREEN_CAPTURE, exchange.Sequence, new ScreenCaptureRequest { maxSize = maxSize }, timeout.Token);
        using var output = new MemoryStream();
        int index = 0, count = -1;
        while (true)
        {
            var chunk = Wire.Body<ScreenCaptureChunk>(await exchange.ReadAsync(timeout.Token));
            if (!chunk.success) throw new IOException(chunk.error ?? "Capture failed.");
            ValidateChunk(chunk.chunkIndex, chunk.totalChunks, index++, ref count, chunk.isLast);
            Append(output, chunk.data);
            if (chunk.isLast) return output.ToArray();
        }
    }

    public static async Task<byte[]> GetTextureAsync(this InspectorSession session, int id, CancellationToken token = default, bool original = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await session.TextureLock.WaitAsync(timeout.Token);
        try
        {
        using var exchange = session.OpenExchange(Command.TEXTURE_RESPONSE);
        session.TextureExchange = exchange;
        await session.SendAtAsync(original ? (Command)InspectionProtocol.TextureOriginal : Command.GET_TEXTURE, exchange.Sequence,
            new GetTextureRequest { instanceId = id, forceRefresh = true }, timeout.Token);
        using var output = new MemoryStream();
        int index = 0, count = -1;
        while (true)
        {
            var chunk = Wire.Body<TextureResponse>(await exchange.ReadAsync(timeout.Token));
            if (chunk.instanceId != id) throw new InvalidDataException("Texture ID mismatch.");
            if (chunk.cached)
                return session.Images.TryGetValue(id, out var cached) ? cached : throw new IOException("Remote texture cache is unavailable; refresh required.");
            if (!string.IsNullOrEmpty(chunk.status) && chunk.status != "ok") throw new IOException("Texture: " + chunk.status);
            ValidateChunk(chunk.chunkIndex, chunk.totalChunks, index++, ref count, chunk.isLast);
            Append(output, chunk.data);
            if (chunk.isLast)
            {
                var bytes = output.ToArray();
                if (session.Images.Count >= 128 || session.Images.Values.Sum(v => (long)v.Length) + bytes.Length > 128 * 1024 * 1024)
                    session.Images.Clear();
                session.Images[id] = bytes;
                return bytes;
            }
        }
        }
        finally { session.TextureExchange = null; session.TextureLock.Release(); }
    }

    public static async Task DownloadAsync(this InspectorSession session, string relativePath, bool directory,
        string destination, IProgress<string> progress = null, CancellationToken token = default)
    {
        using var exchange = session.OpenExchange(Command.FILE_BROWSE_DOWNLOAD_CHUNK);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await session.SendAtAsync(Command.FILE_BROWSE_DOWNLOAD, exchange.Sequence,
                new FileBrowseDownloadRequest { relativePath = relativePath, isDirectory = directory }, timeout.Token);
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                int index = 0, count = -1;
                long expectedSize = -1;
                while (true)
                {
                    var chunk = Wire.Body<FileBrowseDownloadChunk>(await exchange.ReadAsync(timeout.Token));
                    if (chunk.error) throw new IOException(chunk.errorMessage);
                    if (chunk.relativePath != relativePath) throw new InvalidDataException("Download path mismatch.");
                    ValidateChunk(chunk.chunkIndex, chunk.totalChunks, index++, ref count, chunk.isLast);
                    if (expectedSize < 0) expectedSize = chunk.totalSize;
                    if (expectedSize < 0 || expectedSize > 1024L * 1024 * 1024 || chunk.totalSize != expectedSize)
                        throw new InvalidDataException("Invalid download size (maximum 1 GiB).");
                    byte[] bytes = Convert.FromBase64String(chunk.data ?? "");
                    if (output.Position + bytes.Length > expectedSize) throw new InvalidDataException("Download exceeds declared size.");
                    await output.WriteAsync(bytes, timeout.Token);
                    progress?.Report($"下载 {index}/{count}");
                    if (!chunk.isLast) continue;
                    if (output.Position != expectedSize) throw new InvalidDataException("Download size mismatch.");
                    break;
                }
            }
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static async Task UploadAsync(this InspectorSession session, string source, string relativePath,
        IProgress<string> progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Replace('\\', '/').Split('/').Contains(".."))
            throw new ArgumentException("Upload requires a relative file path inside persistentDataPath.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await session.UploadLock.WaitAsync(timeout.Token);
        bool started = false, completed = false;
        try
        {
            using var exchange = session.OpenExchange(Command.FILE_UPLOAD_ACK);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (input.Length > 1024L * 1024 * 1024) throw new IOException("Upload exceeds 1 GiB.");
            string md5 = Convert.ToHexString(await MD5.HashDataAsync(input, timeout.Token)).ToLowerInvariant();
            input.Position = 0;
            int total = Math.Max(1, (int)((input.Length + 65535) / 65536));
            await session.SendAtAsync(Command.FILE_UPLOAD_START, exchange.Sequence, new FileUploadStartRequest
            {
                relativePath = relativePath, fileName = Path.GetFileName(source), totalSize = input.Length, totalChunks = total, md5 = md5
            }, timeout.Token);
            started = true;
            var buffer = new byte[65536];
            for (int i = 0; i < total; i++)
            {
                if (exchange.Channel.Reader.TryRead(out var early))
                {
                    var rejection = Wire.Body<FileUploadAck>(early);
                    throw new IOException(rejection.error ?? "Runtime rejected or prematurely completed the upload.");
                }
                int length = (int)Math.Min(buffer.Length, input.Length - input.Position);
                await input.ReadExactlyAsync(buffer.AsMemory(0, length), timeout.Token);
                await session.SendAtAsync(Command.FILE_UPLOAD_CHUNK, exchange.Sequence, new FileUploadChunkRequest
                { chunkIndex = i, totalChunks = total, data = Convert.ToBase64String(buffer, 0, length) }, timeout.Token);
                progress?.Report($"上传 {i + 1}/{total}");
            }
            await session.SendAtAsync(Command.FILE_UPLOAD_END, exchange.Sequence,
                new FileUploadEndRequest { relativePath = relativePath }, timeout.Token);
            var ack = Wire.Body<FileUploadAck>(await exchange.ReadAsync(timeout.Token));
            if (!ack.success) throw new IOException(ack.error);
            completed = true;
        }
        finally
        {
            // Existing Runtime has one upload slot and no abort command. Disconnect releases it.
            if (started && !completed) session.Disconnect();
            session.UploadLock.Release();
        }
    }

    private static void ValidateChunk(int actual, int total, int expected, ref int count, bool last)
    {
        if (count < 0) count = total;
        if (total < 1 || total > 32768 || count != total || actual != expected || actual >= total || last != (actual == total - 1))
            throw new InvalidDataException("Invalid chunk sequence.");
    }
    private static void Append(MemoryStream output, string data)
    {
        var bytes = Convert.FromBase64String(data ?? "");
        if (output.Length + bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("Image exceeds 64 MiB.");
        output.Write(bytes);
    }
}
