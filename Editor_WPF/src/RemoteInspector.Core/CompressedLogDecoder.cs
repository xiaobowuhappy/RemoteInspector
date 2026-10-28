using System.Diagnostics;
using System.Text;

namespace RemoteInspector.Core;

public static class CompressedLogDecoder
{
    public static async Task DecodeAsync(string executable, string source, string destination, IProgress<string> preview = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add(Path.GetFullPath(source));
        using var process = Process.Start(start) ?? throw new IOException("日志解码器未能启动。");
        using var cancel = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var errors = process.StandardError.ReadToEndAsync(token);
        try
        {
            await using var output = new StreamWriter(destination, false, new UTF8Encoding(false));
            var buffer = new char[32768]; var tail = new StringBuilder(); long total = 0;
            var clock = Stopwatch.StartNew();
            while (true)
            {
                int count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), token);
                if (count == 0) break;
                total += count; if (total > 128 * 1024 * 1024) throw new IOException("解码文本超过 128 Mi 字符限制，请分文件查看。");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                tail.Append(buffer, 0, count); if (tail.Length > 3 * 1024 * 1024) tail.Remove(0, tail.Length - 2 * 1024 * 1024);
                if (clock.ElapsedMilliseconds >= 150) { preview?.Report(tail.ToString()); clock.Restart(); }
            }
            if (tail.Length > 2 * 1024 * 1024) tail.Remove(0, tail.Length - 2 * 1024 * 1024);
            preview?.Report(tail.ToString());
            await process.WaitForExitAsync(token);
            string error = await errors;
            // This bundled BqLog decoder returns appender_decode_result::eof (1) on successful completion.
            if (process.ExitCode is not (0 or 1) || !string.IsNullOrWhiteSpace(error)) throw new IOException("日志解码失败：" + error);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        }
    }
}
