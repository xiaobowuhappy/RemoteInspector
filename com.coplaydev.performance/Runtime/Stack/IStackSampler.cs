using System.Collections.Generic;

namespace Coplaydev.Performance
{
    public interface IStackSampler
    {
        string Name { get; }
        bool IsSupported { get; }
        // Called exactly once from the Unity main thread during SDK startup.
        void RegisterMainThread();
        // BeginStall and Sample are invoked by the watchdog thread. Implementations must be
        // bounded, thread-safe and allocation-free on any signal-handler path.
        // 开启一个采样段：清空该段的Native缓存，绑定卡顿编号generation。本方法不抓栈。
        // generation标识同一次逻辑卡顿；一场卡顿可以包含多个采样段。
        void BeginStall(long generation, long monotonicMs);
        // 请求抓取目标UnityMain当前调用栈，结果暂存在采样器内部。
        void Sample(long generation, long monotonicMs);
        // 关闭当前采样段并取回样本，不代表UnityMain已经恢复。
        // 持续卡顿时也会调用它，随后BeginStall开启下一段，以便及时保存证据。
        // 通常由Watchdog调用；显式关闭采集时也可能由控制线程串行调用。
        // 允许分配内存，但不能依赖主线程执行或调用要求主线程的Unity API。
        List<StackSample> EndStall(long generation, long monotonicMs);
    }
}
