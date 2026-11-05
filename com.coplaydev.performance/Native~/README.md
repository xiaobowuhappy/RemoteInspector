# Native UnityMain sampler

The package contains Android arm64, iOS arm64 and Windows x64 native samplers.
When native capture is unavailable, events retain an empty stack; no recovery-time managed stack is fabricated.

## Integration

### Android

Build the shared library with the same NDK family used by the target Unity project:

```powershell
./android/build-android.ps1 -NdkPath "<Unity Editor>/Data/PlaybackEngines/AndroidPlayer/NDK"
```

The script installs:

```text
Runtime/Plugins/Android/libs/arm64-v8a/libperf_watchdog.so
```

Keep arm64 frame pointers enabled for the application/native libraries whose stacks must be
walkable. Test every supported Unity/NDK/device family; frame-pointer walking may terminate early
when a library omits frame pointers.

### iOS

`Runtime/Plugins/iOS/perf_watchdog_ios.mm` is compiled into the generated Xcode project by Unity.
The managed bridge uses `__Internal` on iOS.

### Windows x64

Build with a Visual Studio x64 C++ toolchain:

```powershell
./windows/build-windows.ps1 -Configuration RelWithDebInfo
```

The script installs `Runtime/Plugins/x86_64/perf_watchdog.dll`. It keeps
`perf_watchdog.pdb` under `Native~/windows/build/x86_64/symbols`; CI must archive that PDB with
the matching player build and never ship it in the player.

The sampler suspends UnityMain only while acquiring `CONTEXT` and walking Windows x64 unwind
metadata. It resumes before module/RVA formatting. The deadline is checked between unwind steps;
a single OS unwind call cannot be interrupted, so monitor the reported `sampleCostUs` and retain a
remote kill switch. Server symbolication uses the exact player/PDB set and uploaded `module+RVA`.

### Runtime sequence

```text
UnityMain: RegisterMainThread
Watchdog:  BeginStall(generation)
Watchdog:  RequestSample(generation) using adaptive offsets
Watchdog:  EndStall(generation) after observing recovery; drain and symbolize samples
UnityMain: attach the drained samples to the event on the next tick
```

Every sample carries the stall generation, capture cost and quality. A generation mismatch is
discarded so a late native sample cannot contaminate a later stall.

Android implementation requirements:

- Record the Unity main-thread TID during initialization.
- Capture `ucontext` into preallocated storage; never allocate, lock, log, call JNI, or perform IO
  in a signal handler.
- Emit module-relative program counters plus ELF build IDs, never ASLR absolute addresses.
- Bound unwind depth and time, and expose a remote kill switch through `IsSupported`.
- Review the configured realtime signal against other native SDKs before production rollout.

iOS implementation requirements:

- Obtain the main Mach thread once, suspend only for a strictly bounded capture window, and resume
  it on every error path.
- Emit image UUID and image-relative instruction addresses for dSYM symbolication.
- Never suspend while holding an SDK lock and disable capture during background transitions.

Windows implementation requirements:

- Register from UnityMain and never call `RequestSample` from that same thread.
- Keep suspend/context/resume strictly paired; disable future capture if resume fails.
- Do not use DbgHelp symbol resolution while UnityMain is suspended; symbolize from PDB in CI/backend.
- Validate IL2CPP, Mono and every supported Windows/Unity version separately.

Native binaries should be rebuilt with the shipping toolchain. Symbols for every shipping binary
must be archived by Build ID for server symbolication.

## Immediate remote capture (2026-11-04)

Android arm64 and iOS arm64 expose `PerfNative_CaptureMainThread(monotonic_ms)`, returning one StackSample JSON. Invoke only through the managed shared non-waiting capture budget, from a worker thread. Registration is idempotent on the actual UnityMain. Direct capture uses a separate sample/result buffer and never consumes/reset watchdog samples or generations. Android leaves timed-out signal requests pending until handler completion; a signal handler only writes preallocated data and lock-free atomics. iOS balances suspend/resume before symbol formatting and returns null on failure. Windows continues using its existing token-based capture interface.

Android build script invokes the NDK driver directly and statically links libc++; the result depends only on Android system libraries. The arm64 plugin was cross-compiled on Windows; no Android device was connected. iOS native compilation and real-device regression require Xcode on macOS and remain outstanding.


## 2026-11-05 Windows 业务调用栈完整性

修复 GetThreadContext 只请求 CONTEXT_CONTROL 的问题，改为 CONTEXT_CONTROL | CONTEXT_INTEGER，让 RtlVirtualUnwind 能使用 RBP 等非易失寄存器恢复调用帧。展开时复用已验证的代码页查询，避免同一 PC 反复 VirtualQuery，把预算留给更上层调用者。

保留 300 微秒软预算、64 帧上限、2 毫秒超时关闭阈值、原生并发门和采样频率限制；不扫描栈内存猜测返回地址，不展开 MEM_PRIVATE 的 Mono/JIT 动态代码，不在挂起目标线程期间做符号解析。软预算在展开步骤之间检查，单次操作仍可能略超预算，不能承诺硬实时。

对照回归：Native~/windows/tests/business-stack-test.cpp，包含动态栈帧、等待函数和多层业务调用。20 次独立进程测试：旧版每次 3 帧，新版每次 21 帧，PDB 可还原 BusinessWait / BusinessUpdate / BusinessEntry；新版中位耗时 218.5 微秒，最大 318 微秒。测试是可控原生程序，不等同完整 IL2CPP 玩家验收。

新插件已安装至两个工程的 Runtime/Plugins/x86_64/perf_watchdog.dll，.meta 与导入设置保持原样。匹配 PDB 位于 Native~/windows/build/business-stack/symbols，不随包发布。Windows 游戏须重新构建后使用新插件；旧采样文件不会新增已缺失的帧。Android/iOS 与 Mono/JIT 动态栈本轮未修改，IL2CPP 内联、尾调用优化和已有展开信息仍会影响可恢复的业务帧。


## 2026-11-05 展开诊断与 IL2CPP 名称

Windows 展开软预算更新为 1000us，保留 64 帧和 2000us 超时保护，操作间检查不保证硬实时。有效栈末尾通过既有 stack 字符串附带 [unwind-stop: reason, frames=N]，区分完整、预算、帧上限、栈范围、动态代码、无效地址、无进展和内存异常；零帧不会因诊断文字被视为有效栈。ABI/DTO 字段不变。

对照原生业务样本20次均21帧，耗时中位247.5us、最大428us。真实卡顿是否因300us预算截断尚无证据，新结束原因用于确认而非预判。两工程插件已更新；重建并重启Player后生效。原生采样不能恢复已经被尾调用优化消除的帧；PDB可还原存在的IL2CPP方法名，原始C#文件行号需要对应调试映射。
