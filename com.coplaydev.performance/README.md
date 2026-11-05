# Company Performance SDK

## 采集可靠性

采集控制和前后台通知采用异步状态请求，不等待采样器锁、不在UnityMain结束Native采样。
关闭时正在进行的事件由Watchdog唤醒后保存为interrupted；这不保证接口返回时已完成快照或落盘。
工作线程捕获异常后停止采集，正常退出后释放采样器及写盘线程，避免与仍在运行的采样并发释放。
Windows遇到动态代码边界不调用动态展开表回调，避免暂停目标线程后进入其JIT展开回调。
原生暂停抓栈依然不能提供绝对无死锁保证，需要目标Player压力验证和远程关闭能力。

内置Watchdog、RenderThread和即时抓栈共享非等待并发门，捕获尝试合计最多120次/分钟，
两次尝试至少间隔10ms；各入口原有更严格的预算继续生效。竞争失败跳过，不等待目标线程持有的采集门。
此预算不覆盖接入方自定义IStackSampler实现。它限制频率，不保证操作系统调用的硬实时耗时。
心跳时间和序号使用版本校验一致读取；切后台立即提交中断快照。
文件级新增配置中的 `buildId` 和进程 `architecture`，CI必须注入唯一构建号并归档对应符号。
目前尚未采集各模块的PDB GUID/Age、ELF Build ID或Mach-O UUID。
事件 `captureStatus` 区分已有样本与无有效样本；无样本通过 captureStatus 区分采样器不可用、等待采样、预算耗尽、恢复竞态及空结果；原生 ABI 未提供的细因仍无法精确判断。

## 远程即时拉取UnityMain堆栈（Windows x64 / Android arm64 / iOS arm64）

高频轮询优先使用缓存接口：

```csharp
if (Performance.TryGetUnityMainStack(out var stack, out var ageMs, out var refreshError))
    SendStack(stack, ageMs);
```

缓存命中不创建托管对象，复用不可变字符串；过期时由调用工作线程尝试刷新，实际抓栈最多每秒一次。
刷新失败仍可返回旧快照，务必查看 `ageMs` 和 `refreshError`。没有快照则返回false。
原始抓栈仍包含Native暂停、JSON转换和对象分配；远程序列化/传输的分配由调试工具自身控制。
高频路径不要使用每次创建Task的异步接口，也不要自行逐次拼接日志字符串。

SDK启动时在真实UnityMain注册线程。远程命令在工作线程直接调用：

```csharp
if (Performance.TryCaptureUnityMainStack(out var sample, out var error))
    SendResponse(sample); // 用现有远程工具返回数据
else
    SendError(error);
```

调用方也可 `await Performance.CaptureUnityMainStackAsync()`，但不要在UnityMain上使用
`.Wait()` 或 `.Result`，否则抓到的可能正是等待任务的调用栈。
接口每秒最多尝试一次，拒绝并发请求和主线程自抓取；失败返回明确错误。
它不生成JankEvent，关闭卡顿采集后仍可使用；主线程永久阻塞时，应由远程工作线程直接调用。
Windows 使用任意线程抓栈扩展；Android / iOS 使用独立的主线程快照缓冲区，不重置 Watchdog 的卡顿编号及样本。即时抓栈同样受 NativeCaptureBudget 限制；Android 信号响应超时则保留 pending，禁止重用尚未完成的缓冲区。iOS 在恢复主线程后才进行模块查找与 JSON 序列化。Editor Mono 栈仍可能在 JIT 边界截断。

## 当前日志字段

文件级保留 `platform`、应用 `version`；事件包含 `eventId`、起点 `occurredAtUtc`、
`durationMs`、`status` 和 `stacks`。状态为 `ongoing` 时，时长是已观测下限；
`recovered` 表示观察到心跳恢复，`interrupted` 表示采集被中断。
每条堆栈保留 `thread`、`stack`、`sampleCount`、`firstSeenOffsetMs` 和 `lastSeenOffsetMs`。
偏移相对于事件起点，表示首次/末次命中时刻，不表示函数执行时长；非均匀采样次数不能直接换算时间占比。
不输出 source、generation、采样成本和质量等内部字段。

Unity 2021.3+ 的精简线上长卡顿采集模块。核心只做四件事：PlayerLoop 心跳、Watchdog 判定、
Native 线程采样、本地 JSON 落盘。不会自动联网，也不依赖业务埋点。

## 接入

将包放入项目 `Packages`。SDK 在 `BeforeSceneLoad` 自动启动；业务侧只有两个可选接口：

```csharp
Performance.SetPage("BattleHUD");
Performance.MarkExpectedTransient(800);
var health = Performance.GetDiagnostics();
```

Loading 屏蔽区间（UnityMain 调用，支持跨帧）：

```csharp
Performance.SetCollectionEnabled(false); // 进入 Loading 前
// 场景加载、批量实例化、预热，可跨多个帧
Performance.SetCollectionEnabled(true);  // 游戏准备完成后
```

关闭期间不生成新卡顿事件、不采集堆栈；恢复时重置心跳与帧计时，Loading 时长不计入游戏卡顿。
已写入的历史事件保留。此开关是全局状态，不支持嵌套计数，应由统一 Loading 流程管理。
关闭时若正在记录卡顿，会先结束采样、提交 `interrupted` 最终快照并唤醒写盘线程。
接口返回表示已提交，不保证此时已落盘；主线程不等待磁盘。进程在后台写入完成前被强杀仍可能丢失最后快照。
同事件待写快照合并，批量事件一次重写文件；无新增样本时复用独立堆栈快照。
写盘线程复用事件记录、JSON容器和UTF8缓冲；文件长度和修改时间未变化时复用内存事件，避免重复反序列化。
JsonUtility输出字符串及新增堆栈快照仍有分配，当前并非全链路零分配。
磁盘异常通过 `queueWriteFailures` 计数反馈。

后台或关闭采集时Watchdog等待状态信号，写盘线程无任务时等待入队信号。
持续卡顿快照在前30秒每5秒、30秒至5分钟每30秒、之后每5分钟更新，恢复和关闭时立即提交。
事件持续时间使用64位毫秒，支持跨月持续事件。

配置位于 `Assets/Resources/performance-config.json`。示例见 `Samples~/Basic/Resources`。

## 输出

日志位于：

```text
Application.persistentDataPath/performance-events/jank-events-yyyyMMdd-HHmmss-fff.json
```

一次运行写一个文件，同次运行的事件追加到同一个 `events` 数组。事件只保留发生时间、持续时间和采样堆栈：

```json
{"events":[{"occurredAtUtc":"2026-10-14T06:15:32.481Z","durationMs":2200,"stacks":[{"source":"native-main-thread","thread":"UnityMain","stack":"GameAssembly.dll+0x263465\n...","sampleCount":2}]}]}
```

相同 `source + thread + stack` 在一次卡顿内合并。Windows x64 自动采集 UnityMain，并在存在
`GfxDeviceW*` 时采集 RenderThread；Android/iOS 采集 UnityMain。

## 独立上传模块

核心包不含上传配置、协程或网络请求。可选包 `com.company.performance.upload` 提供显式组件
`PerformanceLogUploader`：配置 HTTPS endpoint 后，由业务调用 `UploadPending()`。它只读取已关闭的
历史会话文件，上传成功且内容未变化时才删除原文件。

## 线程即时抓栈（Windows x64）

工作线程先调用 `ThreadStackCapture.RegisterCurrentThread(name)`，其他线程再通过返回的注册对象
`TryCapture(out sample)` 抓取。不要传 `ManagedThreadId` 或缓存裸 OS 线程 ID；线程退出前必须释放注册对象。

完整语义见 [`Documentation~/analysis-output-and-pipeline.md`](Documentation~/analysis-output-and-pipeline.md)，
Native 接入见 [`Native~/README.md`](Native~/README.md)。

## 远程调试订阅（2026-11-04）

`Performance.JankSnapshot += handler` 订阅已生成的卡顿快照，包含 ongoing/recovered/interrupted；同一 eventId 表示同一事件的持续更新。调用发生在 Watchdog 或回退采集线程，必须只读使用参数，不调用 Unity API、不阻塞或执行网络 I/O；消费者自行复制到有界队列，并在结束时退订。各订阅者异常隔离，不影响后续订阅者。该事件不改变本地写盘流程，也不回放连接前的历史。`Performance.BuildId` 返回当前初始化配置中的构建号（包括显式 overrideConfig），供远程即时抓栈响应标注。

Android 构建：`Native~/android/build-android.ps1 -NdkPath <NDK>`。脚本直接使用 Windows NDK clang++，不依赖 CMake/Ninja，静态链接 C++ 运行库；生成 arm64-v8a/libperf_watchdog.so。Unity 导入设置必须为仅 Android / ARM64、禁用 Editor。iOS .mm 随 Xcode 项目编译；本次 Windows 环境尚未验证 iOS 原生编译与手机实测。

## 通用转发接收接口（2026-11-05）

在 SDK 现有程序集内实现 `IPerformanceForwarder`，仅包含 `void OnJank(JankEvent snapshot)`。外部接收方调用 `Performance.RegisterForwarder(receiver)` 注册，保存返回的 IDisposable 并在结束时释放；也可使用 `Performance.UnregisterForwarder(receiver)` 移除这个对象的全部注册。SDK 不包含任何外部接收工具、TCP、会话或命令号，不负责选择或构造接收方。

同一个对象重复注册只收到一次通知，注册句柄按引用计数释放。强制注销后的旧句柄不会移除后来重新注册的接收方。回调异常逐个隔离，可在回调中注销自身。分发采用注册时写时复制数组，不持注册锁执行回调；没有注册变化时不会为分发表分配数组。

回调发生在 Watchdog/发布线程，快照必须只读，复制必要数据到有界队列后立即返回；不要调用 Unity API、阻塞或发送网络。注销不等待已经开始的回调，接收方需自行保护 Dispose 状态。SDK 不因初始化/停止采集而隐式清除外部注册。旧 JankSnapshot 事件继续兼容，外部接收方不应同时使用两种入口重复处理同一事件。

```csharp
sealed class MyReceiver : IPerformanceForwarder
{
    public void OnJank(JankEvent snapshot) { /* copy/enqueue only */ }
}
var receiver = new MyReceiver();
IDisposable registration = Performance.RegisterForwarder(receiver);
// On shutdown/dispose:
registration.Dispose();
```


## 2026-11-05 卡顿快照链路修正

持续事件取得新样本后立即发布更新；无新样本时继续按 5 秒/30 秒/5 分钟降低写盘频率。恢复/中断仍立即发布，同一 eventId 不变。快照复制保留 lastMonotonicMs，避免合并样本末次偏移丢失。

CompositeStackSampler.IsSupported 现在仅在至少一个未禁用子采样器可用时返回 true。无堆栈时 captureStatus 区分 sampler_unavailable、sampling_pending、sampling_budget_exhausted、recovered_before_sample、native_capture_empty、native_disabled_or_custom_sampler_empty。失败细因仍受原生 ABI 信息限制；不伪造恢复后的托管栈。

SDK 保持 IPerformanceForwarder 通用注册接口及旧 JankSnapshot，不依赖 RemoteInspector，不新增程序集。原生 ABI 和文件格式结构不变，仅扩展已有 captureStatus 字符串取值。


## 2026-11-05 Windows 业务调用栈完整性

修复 GetThreadContext 只请求 CONTEXT_CONTROL 的问题，改为 CONTEXT_CONTROL | CONTEXT_INTEGER，让 RtlVirtualUnwind 能使用 RBP 等非易失寄存器恢复调用帧。展开时复用已验证的代码页查询，避免同一 PC 反复 VirtualQuery，把预算留给更上层调用者。

保留 300 微秒软预算、64 帧上限、2 毫秒超时关闭阈值、原生并发门和采样频率限制；不扫描栈内存猜测返回地址，不展开 MEM_PRIVATE 的 Mono/JIT 动态代码，不在挂起目标线程期间做符号解析。软预算在展开步骤之间检查，单次操作仍可能略超预算，不能承诺硬实时。

对照回归：Native~/windows/tests/business-stack-test.cpp，包含动态栈帧、等待函数和多层业务调用。20 次独立进程测试：旧版每次 3 帧，新版每次 21 帧，PDB 可还原 BusinessWait / BusinessUpdate / BusinessEntry；新版中位耗时 218.5 微秒，最大 318 微秒。测试是可控原生程序，不等同完整 IL2CPP 玩家验收。

新插件已安装至两个工程的 Runtime/Plugins/x86_64/perf_watchdog.dll，.meta 与导入设置保持原样。匹配 PDB 位于 Native~/windows/build/business-stack/symbols，不随包发布。Windows 游戏须重新构建后使用新插件；旧采样文件不会新增已缺失的帧。Android/iOS 与 Mono/JIT 动态栈本轮未修改，IL2CPP 内联、尾调用优化和已有展开信息仍会影响可恢复的业务帧。


## 2026-11-05 展开诊断与 IL2CPP 名称

Windows 展开软预算更新为 1000us，保留 64 帧和 2000us 超时保护，操作间检查不保证硬实时。有效栈末尾通过既有 stack 字符串附带 [unwind-stop: reason, frames=N]，区分完整、预算、帧上限、栈范围、动态代码、无效地址、无进展和内存异常；零帧不会因诊断文字被视为有效栈。ABI/DTO 字段不变。

对照原生业务样本20次均21帧，耗时中位247.5us、最大428us。真实卡顿是否因300us预算截断尚无证据，新结束原因用于确认而非预判。两工程插件已更新；重建并重启Player后生效。原生采样不能恢复已经被尾调用优化消除的帧；PDB可还原存在的IL2CPP方法名，原始C#文件行号需要对应调试映射。


## SDK 业务采样窗口（2026-11-05）

新增 Performance.StartUnityMainSampling(options, receiver, sessionId) 返回 IDisposable；StopUnityMainSampling 取消当前窗口。接口及实现位于 SDK 现有程序集 Performance.cs，未新增 asmdef。接收方实现 IStackSamplingReceiver.OnStackSamples，只读/快速入队，不执行网络或UnityAPI。旧 IPerformanceForwarder / JankSnapshot 与每秒即时抓栈保持兼容。

StackSamplingOptions: DurationMs 100–20000，IntervalMs 20–1000（默认50），MaxSamples 1–400。全局同一时刻一个窗口，专用工作线程直接尝试原生抓栈，不走1秒快照缓存。每8样本或250ms刷新一批，结束/取消追加最终批；包含会话ID、样本时间、累计尝试/成功/跳过及错误。消费者异常隔离。Dispose仅发取消信号，不在UnityMain等待抓栈；退出/重初始化会取消采样。SDK关闭/初始化之外不得直接依赖业务对象。

窗口使用独立600次/分钟尝试预算，普通Watchdog/即时路径保持120次/分钟；两者共享非等待并发门和至少10ms间隔。50ms是期望间隔，竞争/预算耗尽会跳过并计数，窗口不保证命中所有C#方法。最多64帧/原生1000us软预算/2000us超时保护保持原样；本轮无需修改原生ABI或DLL。

仍使用原始地址及同构建符号还原，不从模块名猜测业务函数，不使用恢复点托管栈。批次由注册接收方自行传输，SDK不包含RemoteInspector或TCP依赖。
