# 卡顿采集链路与输出

## 判定

UnityMain 每帧向后台 Watchdog 发送心跳。前台状态下，心跳超过 `longStallThresholdMs`（默认500ms）
未更新即进入一次卡顿；主线程恢复时，以恢复时间减去卡顿起点计算持续时间并生成一个事件。
帧计时仅作为 Watchdog 信号丢失时的兜底，同一次卡顿不会重复写入。

页面切换静默窗口只过滤持续时间小于 `freezeMs` 的已知瞬时加载；达到 `freezeMs` 的长冻结仍记录。

## 采样

卡顿确认后按 `stackSampleOffsetsMs` 前密后疏采样，受单次上限、每分钟预算与恢复冷却限制。
Windows x64 采集 UnityMain 和可用的 `GfxDeviceW*` RenderThread；Android/iOS 采集 UnityMain。
采样失败时事件仍落盘，`stacks` 为空。

Native 栈只能说明多个采样时刻线程停在哪些调用链，不能直接得到每个函数的精确耗时。
`sampleCount / 总样本数` 可作为时间占比估计；精确函数耗时需在调试模式配合 Unity Profiler、ETW、
Perfetto 或 Instruments。

## 本地链路

```text
heartbeat → Watchdog → Native samples → stack compact → background writer → session JSON
```

```json
{"events":[{"occurredAtUtc":"2026-10-14T06:15:32.481Z","durationMs":2200,"stacks":[{"source":"native-main-thread","thread":"UnityMain","stack":"GameAssembly.dll+0x17740f\nUnityPlayer.dll+0x78cf1a","sampleCount":3,"sampleCostUs":80,"quality":100}]}]}
```

SDK 不写 breadcrumbs、metrics、context、恢复后托管栈或客户端聚合文件。服务端负责符号化、指纹、
跨设备聚类、版本对比和告警。

## 上传边界

采集包不联网。`com.company.performance.upload` 是独立、可删除的可选包，只上传已关闭的历史会话，
不接触当前写入文件。也可以不用该包，直接由现有日志系统读取输出目录。
