#if defined(__APPLE__)
#include <TargetConditionals.h>
#endif

#if defined(__APPLE__) && TARGET_OS_IPHONE

#include <atomic>
#include <dlfcn.h>
#include <mach/mach.h>
#include <mach/mach_time.h>
#include <pthread.h>
#include <stdio.h>
#include <string>
#if __has_feature(ptrauth_calls)
#include <ptrauth.h>
#endif

namespace {

constexpr uint32_t kMaxSamples = 16;
constexpr uint32_t kMaxFrames = 64;
constexpr uint64_t kMaxCaptureUs = 300;
constexpr uint64_t kDisableCaptureUs = 2000;
struct FrameRecord { uintptr_t previous; uintptr_t return_address; };
struct RawSample {
    int64_t generation;
    int64_t monotonic_ms;
    uint32_t count;
    uint32_t cost_us;
    uintptr_t pcs[kMaxFrames];
};

static thread_t g_main_thread = MACH_PORT_NULL;
static uintptr_t g_stack_low = 0;
static uintptr_t g_stack_high = 0;
static RawSample g_samples[kMaxSamples];
static std::atomic<uint32_t> g_sample_count{0};
static std::atomic<int64_t> g_active_generation{0};
static std::atomic<bool> g_enabled{true};
static std::string g_result;
static std::string g_direct_result;

static uint64_t MonotonicMicros() {
    static mach_timebase_info_data_t timebase{};
    if (timebase.denom == 0) mach_timebase_info(&timebase);
    const uint64_t ticks = mach_continuous_time();
    return ticks * timebase.numer / timebase.denom / 1000ULL;
}

static bool ValidFrame(uintptr_t fp) {
    return g_stack_high > g_stack_low && g_stack_high - g_stack_low >= sizeof(FrameRecord) &&
           fp >= g_stack_low && fp <= g_stack_high - sizeof(FrameRecord) &&
           (fp & (alignof(uintptr_t) - 1)) == 0;
}

static uintptr_t NormalizeCodeAddress(uintptr_t address) {
#if __has_feature(ptrauth_calls)
    return reinterpret_cast<uintptr_t>(ptrauth_strip(
        reinterpret_cast<void*>(address), ptrauth_key_return_address));
#else
    return address;
#endif
}

static bool ReadFrame(uintptr_t address, FrameRecord* output) {
    mach_vm_size_t copied = 0;
    return mach_vm_read_overwrite(mach_task_self(), static_cast<mach_vm_address_t>(address),
        sizeof(FrameRecord), reinterpret_cast<mach_vm_address_t>(output), &copied) == KERN_SUCCESS &&
        copied == sizeof(FrameRecord);
}

static uint32_t CaptureSuspendedThread(RawSample& sample, uint64_t deadline_us) {
#if defined(__arm64__)
    arm_thread_state64_t state{};
    mach_msg_type_number_t state_count = ARM_THREAD_STATE64_COUNT;
    if (thread_get_state(g_main_thread, ARM_THREAD_STATE64,
        reinterpret_cast<thread_state_t>(&state), &state_count) != KERN_SUCCESS) return 0;
    uint32_t count = 0;
    uintptr_t pc = NormalizeCodeAddress(static_cast<uintptr_t>(state.__pc));
    uintptr_t fp = static_cast<uintptr_t>(state.__fp);
    if (pc != 0) sample.pcs[count++] = pc;
    while (count < kMaxFrames && ValidFrame(fp)) {
        if (MonotonicMicros() >= deadline_us) break;
        FrameRecord record{};
        if (!ReadFrame(fp, &record) || record.return_address == 0) break;
        sample.pcs[count++] = NormalizeCodeAddress(record.return_address);
        if (record.previous <= fp || !ValidFrame(record.previous)) break;
        fp = record.previous;
    }
    return count;
#else
    return 0;
#endif
}

static void AppendEscaped(std::string& output, const char* text) {
    if (!text) { output += "unknown"; return; }
    for (const char* p = text; *p; ++p) {
        if (*p == '\\' || *p == '"') output.push_back('\\');
        output.push_back(*p);
    }
}

static void AppendFrame(std::string& output, uintptr_t pc) {
    Dl_info info{};
    char address[40];
    if (dladdr(reinterpret_cast<void*>(pc), &info) != 0 && info.dli_fbase) {
        const auto relative = pc - reinterpret_cast<uintptr_t>(info.dli_fbase);
        const char* module = info.dli_fname;
        if (module) {
            const char* cursor = module;
            for (const char* p = module; *p; ++p) if (*p == '/') cursor = p + 1;
            module = cursor;
        }
        AppendEscaped(output, module);
        snprintf(address, sizeof(address), "+0x%llx", static_cast<unsigned long long>(relative));
    } else {
        snprintf(address, sizeof(address), "unknown");
    }
    output += address;
}

} // namespace

extern "C" int32_t PerfNative_IsSupported(void) {
#if defined(__arm64__)
    return g_enabled.load(std::memory_order_acquire) ? 1 : 0;
#else
    return 0;
#endif
}

extern "C" int32_t PerfNative_GetAbiVersion(void) {
    return 3;
}

extern "C" int32_t PerfNative_RegisterMainThread(void) {
    if (!pthread_main_np()) return 0;
    if (g_main_thread != MACH_PORT_NULL) return 1;
    g_main_thread = mach_thread_self();
    pthread_t thread = pthread_self();
    g_stack_high = reinterpret_cast<uintptr_t>(pthread_get_stackaddr_np(thread));
    const size_t size = pthread_get_stacksize_np(thread);
    g_stack_low = g_stack_high - size;
    return g_main_thread != MACH_PORT_NULL && size > 0 ? 1 : 0;
}

extern "C" void PerfNative_BeginStall(int64_t generation, int64_t) {
    if (!g_enabled.load(std::memory_order_acquire) || generation <= 0) return;
    g_sample_count.store(0, std::memory_order_release);
    g_active_generation.store(generation, std::memory_order_release);
}

static bool CaptureOne(RawSample& sample) {
    if (!g_enabled.load(std::memory_order_acquire) || g_main_thread == MACH_PORT_NULL || pthread_main_np()) return false;
    // Initialize/read the clock before suspension; no allocator or symbol work while UnityMain is suspended.
    const uint64_t begin_us = MonotonicMicros();
    if (thread_suspend(g_main_thread) != KERN_SUCCESS) return false;
    sample.count = CaptureSuspendedThread(sample, begin_us + kMaxCaptureUs);
    sample.cost_us = static_cast<uint32_t>(MonotonicMicros() - begin_us);
    kern_return_t resumed = KERN_FAILURE;
    for (int attempt = 0; attempt < 3 && resumed != KERN_SUCCESS; ++attempt) resumed = thread_resume(g_main_thread);
    if (resumed != KERN_SUCCESS || sample.cost_us > kDisableCaptureUs) g_enabled.store(false, std::memory_order_release);
    return resumed == KERN_SUCCESS && sample.count > 0;
}

extern "C" void PerfNative_RequestSample(int64_t generation, int64_t monotonic_ms) {
    if (generation != g_active_generation.load(std::memory_order_acquire)) return;
    const uint32_t index = g_sample_count.load(std::memory_order_acquire);
    if (index >= kMaxSamples) return;
    RawSample& sample = g_samples[index]; sample.generation = generation; sample.monotonic_ms = monotonic_ms;
    if (CaptureOne(sample) && generation == g_active_generation.load(std::memory_order_acquire))
        g_sample_count.store(index + 1, std::memory_order_release);
}

extern "C" const char* PerfNative_EndStall(int64_t generation, int64_t) {
    if (generation <= 0 || generation != g_active_generation.exchange(0, std::memory_order_acq_rel)) return nullptr;
    const uint32_t count = g_sample_count.load(std::memory_order_acquire);
    g_result.clear();
    g_result += "{\"samples\":[";
    for (uint32_t i = 0; i < count; ++i) {
        if (i) g_result.push_back(',');
        const RawSample& sample = g_samples[i];
        g_result += "{\"generation\":" + std::to_string(sample.generation);
        g_result += ",\"monotonicMs\":" + std::to_string(sample.monotonic_ms);
        g_result += ",\"source\":\"native-main-thread\",\"thread\":\"unity-main\",\"stack\":\"";
        for (uint32_t frame = 0; frame < sample.count; ++frame) {
            if (frame) g_result += "\\n";
            AppendFrame(g_result, sample.pcs[frame]);
        }
        const int quality = sample.count >= 6 ? 100 : (sample.count > 0 ? 60 : 0);
        g_result += "\",\"sampleCostUs\":" + std::to_string(sample.cost_us);
        g_result += ",\"quality\":" + std::to_string(quality) + "}";
    }
    g_result += "]}";
    return g_result.c_str();
}

extern "C" const char* PerfNative_CaptureMainThread(int64_t monotonic_ms) {
    RawSample sample{}; sample.monotonic_ms = monotonic_ms;
    if (!CaptureOne(sample)) return nullptr;
    // Allocation, dladdr and JSON formatting happen only after resuming UnityMain.
    g_direct_result = "{\"thread\":\"UnityMain\",\"source\":\"native-main-thread\",\"sampleCount\":1,\"monotonicMs\":" + std::to_string(monotonic_ms);
    g_direct_result += ",\"stack\":\"";
    for (uint32_t i = 0; i < sample.count; ++i) {
        if (i) g_direct_result += "\\n";
        AppendFrame(g_direct_result, sample.pcs[i]);
    }
    g_direct_result += "\",\"sampleCostUs\":" + std::to_string(sample.cost_us) + "}";
    return g_direct_result.c_str();
}

#endif
