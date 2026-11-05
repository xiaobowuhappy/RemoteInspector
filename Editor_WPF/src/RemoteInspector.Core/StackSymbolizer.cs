using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Buffers.Binary;
using System.Reflection.PortableExecutable;

namespace RemoteInspector.Core;

/// <summary>Resolve module-relative PCs locally; never execute text supplied by the remote peer.</summary>
public sealed class StackSymbolizer
{
    private static readonly Regex Frame = new(@"(?<module>[\w.@-]+)\+0x(?<pc>[0-9a-fA-F]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UnwindFooter = new(@"^\[unwind-stop: (?<reason>[a-z_]+), frames=(?<frames>\d+)\]$", RegexOptions.Compiled);
    private static readonly Regex Il2CppMethod = new(@"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)_m[A-Fa-f0-9]{40}\b", RegexOptions.Compiled);
    public async Task<string> ResolveAsync(string raw, string symbols, string tool, CancellationToken token = default)
    {
        if (!Directory.Exists(symbols)) throw new DirectoryNotFoundException("请在符号设置中选择当前游戏构建的符号目录。");
        if (!File.Exists(tool)) throw new FileNotFoundException("找不到 llvm-symbolizer，请在符号设置中选择工具。", tool);
        if ((raw?.Length ?? 0) > 1024 * 1024) throw new InvalidDataException("堆栈超过 1 Mi 字符，无法解析。");
        string[] lines = (raw ?? "").Split('\n');
        var output = new StringBuilder();
        var cache = new Dictionary<string, (string Text, bool Success)>(StringComparer.OrdinalIgnoreCase);
        int succeeded = 0, unresolved = 0;
        // Index once per operation, not once per frame. Ignore inaccessible directories and cap work.
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = Directory.EnumerateFiles(symbols, "*", options).Take(50000).ToArray();
        using var pdbSearch = new RecursivePdbSearch(files);
        int count = 0;
        foreach (string line in lines)
        {
            token.ThrowIfCancellationRequested();
            var footer = UnwindFooter.Match(line.Trim());
            if (footer.Success)
            {
                output.AppendLine("采样展开：" + ExplainUnwind(footer.Groups["reason"].Value) + "；原生帧数 " + footer.Groups["frames"].Value);
                continue;
            }
            var match = Frame.Match(line);
            if (!match.Success || ++count > 512) { output.AppendLine(line); continue; }
            string module = match.Groups["module"].Value;
            string key = match.Value;
            if (!cache.TryGetValue(key, out var resolved))
            {
                string[] candidates = files.Where(f => string.Equals(Path.GetFileName(f), module, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(f), module + ".debug", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(f), module + ".sym", StringComparison.OrdinalIgnoreCase)).ToArray();
                // Prefer debug ELF to stripped ELF, but never guess between multiple builds/architectures.
                var debug = candidates.Where(f => f.EndsWith(".debug", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (debug.Length == 1) candidates = debug;
                var dwarf = candidates.Where(f => string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), "DWARF", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (dwarf.Length == 1) candidates = dwarf;
                resolved = candidates.Length != 1 ? (candidates.Length == 0 ? "未找到模块符号：符号目录缺少该模块文件及配套符号" : "存在多个同名模块，请缩小符号目录", false) :
                    await ResolveFrame(pdbSearch.Prepare(candidates[0]), match.Groups["pc"].Value, tool, token).ConfigureAwait(false);
                cache[key] = resolved;
            }
            if (resolved.Success) succeeded++; else unresolved++;
            output.AppendLine(line).Append("    ").AppendLine(resolved.Text.Replace("\n", "\n    "));
        }
        return $"解析结果：成功 {succeeded} 帧，未解析 {unresolved} 帧" + (unresolved > 0 && succeeded > 0 ? "（部分成功，原始地址已保留）" : "") + "\n\n" + output;
    }
    private static async Task<(string Text, bool Success)> ResolveFrame(string file, string pc, string tool, CancellationToken token)
    {
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        bool mach = TryMachOBase(file, out ulong preferredBase);
        string address = mach ? "0x" + checked(Convert.ToUInt64(pc, 16) + preferredBase).ToString("x") : "0x" + pc;
        foreach (string arg in new[] { "--obj=" + file, mach ? "--default-arch=arm64" : "--relative-address", "--inlines", "--demangle", address }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动符号解析工具。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string text = (await stdout).Trim(), error = (await stderr).Trim();
            if (process.ExitCode == 0 && text.Length > 0 && !text.StartsWith("??"))
            {
                var names = Il2CppMethod.Matches(text).Select(m => m.Groups["name"].Value).Distinct().ToArray();
                return ((names.Length > 0 ? "IL2CPP 业务方法：" + string.Join(" → ", names) + "\n" : "") + text, true);
            }
            var missingPdb = Regex.Match(error, @"[A-Za-z0-9_.-]+\.pdb", RegexOptions.IgnoreCase);
            if (missingPdb.Success)
                return ("未解析：缺少或无法读取匹配 PDB：" + missingPdb.Value + "\n请将匹配 PDB 放入所选符号目录或任意子目录；无需创建原始构建机目录。", false);
            return ("未解析（符号缺失或不匹配）" + (error.Length > 0 ? "：" + error : ""), false);
        }
        catch { if (!process.HasExited) process.Kill(true); throw; }
    }
    // LLVM 12 searches beside PE modules; build an isolated pairing when the matching
    // PDB is stored elsewhere in the recursively indexed symbol tree.
    private sealed class RecursivePdbSearch : IDisposable
    {
        private readonly string[] files;
        private readonly Dictionary<string, string> prepared = new(StringComparer.OrdinalIgnoreCase);
        private string staging;
        public RecursivePdbSearch(string[] files) => this.files = files;
        public string Prepare(string module)
        {
            if (prepared.TryGetValue(module, out var existing)) return existing;
            prepared[module] = module;
            if (!module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !module.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return module;
            using var stream = File.OpenRead(module);
            using var pe = new PEReader(stream);
            foreach (var entry in pe.ReadDebugDirectory())
            {
                if (entry.Type != DebugDirectoryEntryType.CodeView) continue;
                var cv = pe.ReadCodeViewDebugDirectoryData(entry);
                string name = cv.Path.Replace('\\', '/').Split('/').Last();
                string pdb = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase) && MatchesPdb(f, cv.Guid, cv.Age));
                if (pdb == null) continue;
                if (string.Equals(Path.GetDirectoryName(pdb), Path.GetDirectoryName(module), StringComparison.OrdinalIgnoreCase)) return module;
                staging ??= Path.Combine(Path.GetTempPath(), "RemoteInspector-Symbols-" + Guid.NewGuid().ToString("N"));
                string folder = Path.Combine(staging, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                string copy = Path.Combine(folder, Path.GetFileName(module));
                File.Copy(module, copy); File.Copy(pdb, Path.Combine(folder, name));
                prepared[module] = copy; return copy;
            }
            return module;
        }
        private static bool MatchesPdb(string file, Guid guid, int age)
        {
            try
            {
                using var stream = File.OpenRead(file); using var reader = new BinaryReader(stream);
                if (Encoding.ASCII.GetString(reader.ReadBytes(24)) != "Microsoft C/C++ MSF 7.00\r\n") return false;
                stream.Position = 32; int block = reader.ReadInt32(); reader.ReadInt32(); int blocks = reader.ReadInt32(); int size = reader.ReadInt32(); reader.ReadInt32(); int map = reader.ReadInt32();
                if (block < 512 || block > 65536 || size < 16 || size > 16 * 1024 * 1024 || blocks <= 0) return false;
                stream.Position = (long)map * block;
                int[] directoryBlocks = Enumerable.Range(0, (size + block - 1) / block).Select(_ => reader.ReadInt32()).ToArray();
                using var directory = new MemoryStream();
                foreach (int index in directoryBlocks) { stream.Position = (long)index * block; directory.Write(reader.ReadBytes(Math.Min(block, size - (int)directory.Length))); }
                directory.Position = 0; using var dirs = new BinaryReader(directory);
                int count = dirs.ReadInt32(); if (count < 2 || count > 100000) return false;
                int[] sizes = Enumerable.Range(0, count).Select(_ => dirs.ReadInt32()).ToArray();
                if (sizes[0] > 0) directory.Position += ((sizes[0] + (long)block - 1) / block) * 4;
                if (sizes[1] < 28) return false;
                int info = dirs.ReadInt32(); stream.Position = (long)info * block;
                reader.ReadUInt32(); reader.ReadUInt32(); uint actualAge = reader.ReadUInt32(); Guid actualGuid = new(reader.ReadBytes(16));
                return actualAge == age && actualGuid == guid;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException) { return false; }
        }
        public void Dispose()
        {
            if (staging == null) return;
            string target = Path.GetFullPath(staging);
            string allowedPrefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "RemoteInspector-Symbols-");
            if (!target.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase)) return;
            try { Directory.Delete(target, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private static string ExplainUnwind(string reason) => reason switch
    {
        "complete" => "已到线程栈底",
        "time_budget" => "达到展开时间预算，堆栈被截断，可能缺少上层业务调用",
        "frame_limit" => "达到帧数上限，堆栈被截断",
        "stack_range" => "超出已登记栈范围，停止展开",
        "dynamic_code" => "到达动态代码边界，未猜测 Mono/JIT 帧",
        "invalid_return" => "返回地址不可执行或不可读，停止展开",
        "no_progress" => "栈指针未前进或跨度异常，停止展开",
        "memory_fault" => "读取展开数据发生内存异常，保留已采集帧",
        "context_failed" => "读取线程上下文失败",
        _ => reason
    };
    // iOS sampler emits image-relative PCs; DWARF uses unslid virtual addresses.
    private static bool TryMachOBase(string file, out ulong preferredBase)
    {
        preferredBase = 0;
        using var stream = File.OpenRead(file);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 32) return false;
        long origin = 0;
        uint magic = reader.ReadUInt32();
        if (magic == 0xbebafeca || magic == 0xbfbafeca)
        {
            bool fat64 = magic == 0xbfbafeca;
            uint count = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
            for (int i = 0; i < Math.Min(count, 64); i++)
            {
                long entry = 8 + i * (fat64 ? 32L : 20L);
                if (entry + (fat64 ? 32 : 20) > stream.Length) return false;
                stream.Position = entry;
                uint cpu = BinaryPrimitives.ReverseEndianness(reader.ReadUInt32()); reader.ReadUInt32();
                ulong offset = fat64 ? BinaryPrimitives.ReverseEndianness(reader.ReadUInt64()) : BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
                if (cpu != 0x0100000c || offset > (ulong)stream.Length - 32) continue;
                origin = (long)offset; stream.Position = origin; magic = reader.ReadUInt32(); break;
            }
        }
        if (magic != 0xfeedfacf) return false;
        stream.Position = origin + 16; uint commands = reader.ReadUInt32();
        long position = origin + 32;
        for (int i = 0; i < Math.Min(commands, 4096); i++)
        {
            if (position + 8 > stream.Length) break;
            stream.Position = position; uint command = reader.ReadUInt32(), size = reader.ReadUInt32();
            if (size < 8 || position + size > stream.Length) break;
            if (command == 0x19 && size >= 72)
            {
                string name = Encoding.ASCII.GetString(reader.ReadBytes(16)).TrimEnd('\0');
                ulong address = reader.ReadUInt64();
                if (name == "__TEXT") { preferredBase = address; break; }
            }
            position += size;
        }
        return true;
    }
}
