#if defined(__ANDROID__)

#include "../include/perf_watchdog.h"
#include <android/log.h>
#include <atomic>
#include <dlfcn.h>
#include <pthread.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <string>
#include <sys/syscall.h>
#include <time.h>
#include <ucontext.h>
#include <unistd.h>

namespace {

constexpr uint32_t kMaxSamples = 16;
constexpr uint32_t kMaxFrames = 64;
constexpr int64_t kMaxCaptureUs = 300;
constexpr uint32_t kDisableCaptureUs = 2000;
constexpr uintptr_t kMaxFrameStep = 1024 * 1024;
#ifndef PERF_SAMPLE_SIGNAL
#define PERF_SAMPLE_SIGNAL (SIGRTMIN + 4)
#endif

struct FrameRecord { uintptr_t previous; uintptr_t return_address; };
struct RawSample {
    int64_t generation;
    int64_t monotonic_ms;
    uint32_t count;
    uint32_t cost_us;
    uintptr_t pcs[kMaxFrames];
};

static pid_t g_main_tid = 0;
static uintptr_t g_stack_low = 0;
static uintptr_t g_stack_high = 0;
static RawSample g_samples[kMaxSamples];
static std::atomic<uint32_t> g_sample_count{0};
static std::atomic<int64_t> g_active_generation{0};
static std::atomic<int64_t> g_pending_generation{0};
static std::atomic<int64_t> g_pending_timestamp{0};
static std::atomic<bool> g_request_pending{false};
static std::atomic<bool> g_enabled{true};
static std::string g_result;
static std::string g_direct_result;
static RawSample g_direct_sample;
static bool g_installed = false;

static_assert(std::atomic<uint32_t>::is_always_lock_free, "signal sampler requires lock-free uint32 atomics");
static_assert(std::atomic<int64_t>::is_always_lock_free, "signal sampler requires lock-free int64 atomics");

static int64_t MonotonicMicros() {
    timespec ts{};
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return static_cast<int64_t>(ts.tv_sec) * 1000000LL + ts.tv_nsec / 1000LL;
}

static bool ValidFrame(uintptr_t fp) {
    return fp >= g_stack_low && g_stack_high >= sizeof(FrameRecord) &&
           fp <= g_stack_high - sizeof(FrameRecord) &&
           (fp & (alignof(uintptr_t) - 1)) == 0;
}

static uint32_t WalkFrames(ucontext_t* context, uintptr_t* output, int64_t deadline_us) {
#if defined(__aarch64__)
    uint32_t count = 0;
    uintptr_t pc = static_cast<uintptr_t>(context->uc_mcontext.pc);
    uintptr_t fp = static_cast<uintptr_t>(context->uc_mcontext.regs[29]);
    if (pc != 0) output[count++] = pc;
    while (count < kMaxFrames && ValidFrame(fp) && MonotonicMicros() <= deadline_us) {
        const auto* record = reinterpret_cast<const FrameRecord*>(fp);
        const uintptr_t next = record->previous;
        const uintptr_t lr = record->return_address;
        if (lr == 0) break;
        output[count++] = lr;
        if (next <= fp || next - fp > kMaxFrameStep || !ValidFrame(next)) break;
        fp = next;
    }
    return count;
#else
    (void)context; (void)output;
    return 0;
#endif
}

static void SampleHandler(int, siginfo_t*, void* raw_context) {
    const int64_t generation = g_pending_generation.load(std::memory_order_acquire);
    if (generation == -1) {
        g_direct_sample.monotonic_ms = g_pending_timestamp.load(std::memory_order_relaxed);
        const int64_t begin_us = MonotonicMicros();
        g_direct_sample.count = WalkFrames(reinterpret_cast<ucontext_t*>(raw_context), g_direct_sample.pcs, begin_us + kMaxCaptureUs);
        g_direct_sample.cost_us = static_cast<uint32_t>(MonotonicMicros() - begin_us);
        if (g_direct_sample.cost_us > kDisableCaptureUs) g_enabled.store(false, std::memory_order_release);
        g_request_pending.store(false, std::memory_order_release);
        return;
    }
    if (generation <= 0 || generation != g_active_generation.load(std::memory_order_relaxed)) {
        g_request_pending.store(false, std::memory_order_release);
        return;
    }
    const uint32_t index = g_sample_count.load(std::memory_order_relaxed);
    if (index >= kMaxSamples) {
        g_request_pending.store(false, std::memory_order_release);
        return;
    }

    RawSample& sample = g_samples[index];
    sample.generation = generation;
    sample.monotonic_ms = g_pending_timestamp.load(std::memory_order_relaxed);
    const int64_t begin_us = MonotonicMicros();
    sample.count = WalkFrames(reinterpret_cast<ucontext_t*>(raw_context), sample.pcs, begin_us + kMaxCaptureUs);
    sample.cost_us = static_cast<uint32_t>(MonotonicMicros() - begin_us);
    if (sample.cost_us > kDisableCaptureUs)
        g_enabled.store(false, std::memory_order_release);
    g_sample_count.store(index + 1, std::memory_order_release);
    g_request_pending.store(false, std::memory_order_release);
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

extern "C" __attribute__((visibility("default"))) int32_t PerfNative_IsSupported(void) {
#if defined(__aarch64__)
    return g_enabled.load(std::memory_order_acquire) ? 1 : 0;
#else
    return 0;
#endif
}

extern "C" __attribute__((visibility("default"))) int32_t PerfNative_GetAbiVersion(void) { return 3; }

extern "C" __attribute__((visibility("default"))) int32_t PerfNative_RegisterMainThread(void) {
    const pid_t current_tid = static_cast<pid_t>(syscall(SYS_gettid));
    if (g_installed) return current_tid == g_main_tid ? 1 : 0;
    g_main_tid = current_tid;
    pthread_attr_t attr;
    if (pthread_getattr_np(pthread_self(), &attr) != 0) return 0;
    void* stack_address = nullptr;
    size_t stack_size = 0;
    size_t guard_size = 0;
    const int attr_result = pthread_attr_getstack(&attr, &stack_address, &stack_size);
    pthread_attr_getguardsize(&attr, &guard_size);
    pthread_attr_destroy(&attr);
    if (attr_result != 0 || stack_address == nullptr || stack_size == 0) return 0;
    const uintptr_t stack_base = reinterpret_cast<uintptr_t>(stack_address);
    g_stack_low = stack_base + guard_size;
    g_stack_high = stack_base + stack_size;

    struct sigaction existing{};
    if (sigaction(PERF_SAMPLE_SIGNAL, nullptr, &existing) != 0) return 0;
    if (existing.sa_handler != SIG_DFL && existing.sa_handler != SIG_IGN) return 0;

    struct sigaction action{};
    action.sa_sigaction = SampleHandler;
    action.sa_flags = SA_SIGINFO | SA_RESTART;
    sigemptyset(&action.sa_mask);
    if (sigaction(PERF_SAMPLE_SIGNAL, &action, nullptr) != 0) return 0;
    sigset_t signal_set;
    sigemptyset(&signal_set);
    sigaddset(&signal_set, PERF_SAMPLE_SIGNAL);
    if (pthread_sigmask(SIG_UNBLOCK, &signal_set, nullptr) != 0) {
        sigaction(PERF_SAMPLE_SIGNAL, &existing, nullptr);
        return 0;
    }
    g_installed = true;
    return 1;
}

extern "C" __attribute__((visibility("default"))) void PerfNative_BeginStall(int64_t generation, int64_t) {
    if (!g_installed || !g_enabled.load(std::memory_order_acquire) ||
        g_request_pending.load(std::memory_order_acquire) || generation <= 0) return;
    g_sample_count.store(0, std::memory_order_release);
    g_active_generation.store(generation, std::memory_order_release);
}

extern "C" __attribute__((visibility("default"))) void PerfNative_RequestSample(int64_t generation, int64_t monotonic_ms) {
    if (!g_installed || !g_enabled.load(std::memory_order_acquire) || generation != g_active_generation.load(std::memory_order_acquire)) return;
    if (g_sample_count.load(std::memory_order_acquire) >= kMaxSamples) return;
    bool expected = false;
    if (!g_request_pending.compare_exchange_strong(expected, true, std::memory_order_acq_rel)) return;
    g_pending_timestamp.store(monotonic_ms, std::memory_order_relaxed);
    g_pending_generation.store(generation, std::memory_order_release);
    if (syscall(SYS_tgkill, getpid(), g_main_tid, PERF_SAMPLE_SIGNAL) != 0)
        g_request_pending.store(false, std::memory_order_release);
}

extern "C" __attribute__((visibility("default"))) const char* PerfNative_EndStall(int64_t generation, int64_t) {
    if (generation <= 0 || generation != g_active_generation.exchange(0, std::memory_order_acq_rel)) return nullptr;
    const int64_t wait_deadline = MonotonicMicros() + kDisableCaptureUs;
    while (g_request_pending.load(std::memory_order_acquire) && MonotonicMicros() < wait_deadline)
        syscall(SYS_sched_yield);
    if (g_request_pending.load(std::memory_order_acquire)) {
        // Never start a later generation while an old signal handler might still publish.
        g_enabled.store(false, std::memory_order_release);
        return nullptr;
    }
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

// Independent sample buffer: never reset or consume the watchdog's generation/samples.
extern "C" __attribute__((visibility("default"))) const char* PerfNative_CaptureMainThread(int64_t monotonic_ms) {
    if (!g_installed || !g_enabled.load(std::memory_order_acquire) ||
        static_cast<pid_t>(syscall(SYS_gettid)) == g_main_tid) return nullptr;
    bool expected = false;
    if (!g_request_pending.compare_exchange_strong(expected, true, std::memory_order_acq_rel)) return nullptr;
    g_pending_timestamp.store(monotonic_ms, std::memory_order_relaxed);
    g_pending_generation.store(-1, std::memory_order_release);
    if (syscall(SYS_tgkill, getpid(), g_main_tid, PERF_SAMPLE_SIGNAL) != 0) {
        g_request_pending.store(false, std::memory_order_release); return nullptr;
    }
    const int64_t deadline = MonotonicMicros() + kDisableCaptureUs;
    while (g_request_pending.load(std::memory_order_acquire) && MonotonicMicros() < deadline)
        syscall(SYS_sched_yield);
    // Leave the request pending on timeout. Its buffer cannot be reused until the handler finishes.
    if (g_request_pending.load(std::memory_order_acquire) || g_direct_sample.count == 0) return nullptr;
    g_direct_result = "{\"thread\":\"UnityMain\",\"source\":\"native-main-thread\",\"sampleCount\":1,\"monotonicMs\":" + std::to_string(g_direct_sample.monotonic_ms);
    g_direct_result += ",\"stack\":\"";
    for (uint32_t i = 0; i < g_direct_sample.count; ++i) {
        if (i) g_direct_result += "\\n";
        AppendFrame(g_direct_result, g_direct_sample.pcs[i]);
    }
    g_direct_result += "\",\"sampleCostUs\":" + std::to_string(g_direct_sample.cost_us) + "}";
    return g_direct_result.c_str();
}

#endif
