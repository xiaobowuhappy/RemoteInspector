#pragma once
#include <stdint.h>

#if defined(__cplusplus)
extern "C" {
#endif

// Returned strings are owned by the plugin and must remain valid until the next call.
// RegisterMainThread must be called from UnityMain during SDK startup.
// generation prevents delayed samples from one stall entering the next stall.
// EndStall JSON shape: {"samples":[{"generation":1,"monotonicMs":1,
// "source":"native-main-thread","thread":"unity-main",
// "stack":"module+0xoffset\\n...","sampleCostUs":80,"quality":100}]}
int32_t PerfNative_IsSupported(void);
int32_t PerfNative_GetAbiVersion(void);
int32_t PerfNative_RegisterMainThread(void);
void PerfNative_BeginStall(int64_t generation, int64_t monotonic_ms);
void PerfNative_RequestSample(int64_t generation, int64_t monotonic_ms);
const char* PerfNative_EndStall(int64_t generation, int64_t monotonic_ms);

// Mobile immediate UnityMain capture; uses a buffer independent from watchdog samples.
const char* PerfNative_CaptureMainThread(int64_t monotonic_ms);

// Optional arbitrary-thread capture extension. Register must run on the target thread;
// capture must run on a different thread. Tokens include a slot generation so an exited
// thread or reused OS thread id cannot accidentally target a later registration.
int32_t PerfNative_GetThreadCaptureVersion(void);
uint64_t PerfNative_RegisterCurrentThread(void);
uint64_t PerfNative_RegisterThreadByName(const char* thread_name_prefix);
void PerfNative_UnregisterThread(uint64_t token);
const char* PerfNative_CaptureThread(uint64_t token, int64_t monotonic_ms);

#if defined(__cplusplus)
}
#endif
