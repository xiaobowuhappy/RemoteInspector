#if defined(_WIN32) && defined(_M_X64)

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <atomic>
#include <cwchar>
#include <stdint.h>
#include <stdio.h>
#include <string>

namespace {

constexpr uint32_t kMaxSamples = 16;
constexpr uint32_t kMaxFrames = 64;
constexpr uint64_t kMaxCaptureUs = 1000;
constexpr uint64_t kDisableCaptureUs = 2000;
constexpr uintptr_t kMaxFrameStep = 1024 * 1024;
constexpr uint32_t kMaxRegisteredThreads = 16;
constexpr uint64_t kMinThreadCaptureIntervalUs = 10000;

enum class UnwindStop : uint32_t { Complete, TimeBudget, FrameLimit, StackRange, DynamicCode, InvalidReturn, NoProgress, MemoryFault, ContextFailed };

struct RawSample {
    int64_t generation;
    int64_t monotonic_ms;
    uint32_t count;
    uint32_t cost_us;
    uintptr_t pcs[kMaxFrames];
    UnwindStop stop;
};

static HANDLE g_main_thread = nullptr;
static DWORD g_main_thread_id = 0;
static uintptr_t g_stack_low = 0;
static uintptr_t g_stack_high = 0;
static RawSample g_samples[kMaxSamples];
static std::atomic<uint32_t> g_sample_count{0};
static std::atomic<int64_t> g_active_generation{0};
static std::atomic<bool> g_enabled{true};
static std::string g_result;
static thread_local std::string g_thread_result;
static SRWLOCK g_registry_lock = SRWLOCK_INIT;
static SRWLOCK g_capture_lock = SRWLOCK_INIT;
static uint32_t g_next_token_generation = 1;

struct RegisteredThread {
    HANDLE handle;
    DWORD thread_id;
    uintptr_t stack_low;
    uintptr_t stack_high;
    uint32_t generation;
    uint64_t last_capture_us;
};

static RegisteredThread g_registered_threads[kMaxRegisteredThreads]{};

struct ThreadBasicInformationLocal {
    LONG exit_status;
    PVOID teb_base_address;
    struct { HANDLE process; HANDLE thread; } client_id;
    ULONG_PTR affinity_mask;
    LONG priority;
    LONG base_priority;
};

struct NtTibPrefix {
    PVOID exception_list;
    PVOID stack_base;
    PVOID stack_limit;
};

using NtQueryInformationThreadFn = LONG (NTAPI*)(HANDLE, ULONG, PVOID, ULONG, PULONG);
using GetThreadDescriptionFn = HRESULT (WINAPI*)(HANDLE, PWSTR*);

static uint64_t MonotonicMicros() {
    LARGE_INTEGER counter{};
    LARGE_INTEGER frequency{};
    QueryPerformanceCounter(&counter);
    QueryPerformanceFrequency(&frequency);
    return static_cast<uint64_t>((counter.QuadPart * 1000000ULL) / frequency.QuadPart);
}

static bool ValidStackAddress(uintptr_t address, uintptr_t stack_low, uintptr_t stack_high) {
    return stack_high > stack_low && stack_high - stack_low >= sizeof(uintptr_t) &&
           address >= stack_low && address <= stack_high - sizeof(uintptr_t) &&
           (address & (alignof(uintptr_t) - 1)) == 0;
}

static uint32_t CaptureSuspendedThread(HANDLE thread, uintptr_t stack_low, uintptr_t stack_high,
                                      RawSample& sample, uint64_t deadline_us) {
    sample.stop = UnwindStop::ContextFailed;
    CONTEXT context{};
    // RtlVirtualUnwind may use a nonvolatile frame register (for example RBP)
    // to recover RSP. CONTROL alone leaves those registers zero and truncates
    // stacks at frame-pointer based Windows/IL2CPP/business functions.
    context.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER;
    if (!GetThreadContext(thread, &context)) return 0;

    uint32_t count = 0;
    __try {
        // RIP comes from GetThreadContext and remains useful even when stack bounds are
        // unavailable or the unwind budget is already exhausted. Never discard frame zero.
        if (context.Rip != 0) sample.pcs[count++] = static_cast<uintptr_t>(context.Rip);
        MEMORY_BASIC_INFORMATION code_memory{};
        if (context.Rip != 0)
            VirtualQuery(reinterpret_cast<LPCVOID>(context.Rip), &code_memory, sizeof(code_memory));
        sample.stop = UnwindStop::Complete;
        while (context.Rip != 0) {
            if (count >= kMaxFrames) { sample.stop = UnwindStop::FrameLimit; break; }
            if (code_memory.Type != MEM_IMAGE) { sample.stop = UnwindStop::DynamicCode; break; }
            if (!ValidStackAddress(static_cast<uintptr_t>(context.Rsp), stack_low, stack_high)) { sample.stop = UnwindStop::StackRange; break; }
            if (MonotonicMicros() >= deadline_us) { sample.stop = UnwindStop::TimeBudget; break; }
            const DWORD64 previous_stack = context.Rsp;
            // Do not invoke dynamic function-table callbacks while their owner is suspended.
            // Mono/JIT frames are retained as the boundary, but not unwound here.
            DWORD64 image_base = 0;
            PRUNTIME_FUNCTION function = RtlLookupFunctionEntry(context.Rip, &image_base, nullptr);
            if (function != nullptr) {
                PVOID handler_data = nullptr;
                DWORD64 establisher_frame = 0;
                RtlVirtualUnwind(UNW_FLAG_NHANDLER, image_base, context.Rip, function,
                    &context, &handler_data, &establisher_frame, nullptr);
            } else {
                // The loop already verified MEM_IMAGE. Only image leaf functions
                // may recover a return address from RSP; never do this for JIT memory.
                context.Rip = *reinterpret_cast<const DWORD64*>(context.Rsp);
                context.Rsp += sizeof(DWORD64);
            }
            if (context.Rsp <= previous_stack || context.Rsp - previous_stack > kMaxFrameStep) { sample.stop = UnwindStop::NoProgress; break; }
            if (context.Rip != 0) {
                // Reuse this validation in the next iteration instead of querying
                // the same PC again. Fewer OS queries leave more budget for callers.
                if (VirtualQuery(reinterpret_cast<LPCVOID>(context.Rip), &code_memory, sizeof(code_memory)) == 0 ||
                    code_memory.State != MEM_COMMIT || (code_memory.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0 ||
                    (code_memory.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) == 0)
                    { sample.stop = UnwindStop::InvalidReturn; break; }
                sample.pcs[count++] = static_cast<uintptr_t>(context.Rip);
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // A concurrently changing guard/stack page produces a partial sample, not a crash.
        sample.stop = UnwindStop::MemoryFault;
    }
    return count;
}

// This footer travels through the existing stack string, preserving ABI and DTO compatibility.
// Do not include per-capture timing here: identical stacks must remain mergeable.
static void AppendUnwindStop(std::string& output, const RawSample& sample) {
    if (sample.count == 0) return; // Metadata alone is not a valid stack sample.
    const char* reason = "complete";
    switch (sample.stop) {
        case UnwindStop::TimeBudget: reason = "time_budget"; break;
        case UnwindStop::FrameLimit: reason = "frame_limit"; break;
        case UnwindStop::StackRange: reason = "stack_range"; break;
        case UnwindStop::DynamicCode: reason = "dynamic_code"; break;
        case UnwindStop::InvalidReturn: reason = "invalid_return"; break;
        case UnwindStop::NoProgress: reason = "no_progress"; break;
        case UnwindStop::MemoryFault: reason = "memory_fault"; break;
        case UnwindStop::ContextFailed: reason = "context_failed"; break;
        default: break;
    }
    output += "\\n[unwind-stop: "; output += reason;
    output += ", frames=" + std::to_string(sample.count) + "]";
}

static uint64_t MakeToken(uint32_t slot, uint32_t generation) {
    return (static_cast<uint64_t>(generation) << 32) | static_cast<uint64_t>(slot + 1);
}

static bool DecodeToken(uint64_t token, uint32_t* slot, uint32_t* generation) {
    const uint32_t encoded_slot = static_cast<uint32_t>(token & 0xffffffffULL);
    if (encoded_slot == 0 || encoded_slot > kMaxRegisteredThreads) return false;
    *slot = encoded_slot - 1;
    *generation = static_cast<uint32_t>(token >> 32);
    return *generation != 0;
}

static bool ResumeCapturedThread(HANDLE thread) {
    for (int attempt = 0; attempt < 3; ++attempt) {
        if (ResumeThread(thread) != static_cast<DWORD>(-1)) return true;
    }
    return false;
}

static bool QueryThreadStackLimits(HANDLE thread, uintptr_t* low, uintptr_t* high) {
    const HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    if (ntdll == nullptr) return false;
    const auto query = reinterpret_cast<NtQueryInformationThreadFn>(
        GetProcAddress(ntdll, "NtQueryInformationThread"));
    if (query == nullptr) return false;
    ThreadBasicInformationLocal info{};
    if (query(thread, 0, &info, sizeof(info), nullptr) < 0 || info.teb_base_address == nullptr)
        return false;
    NtTibPrefix tib{};
    SIZE_T copied = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), info.teb_base_address, &tib,
            sizeof(tib), &copied) || copied != sizeof(tib)) return false;
    *low = reinterpret_cast<uintptr_t>(tib.stack_limit);
    *high = reinterpret_cast<uintptr_t>(tib.stack_base);
    return *low != 0 && *high > *low;
}

static uint64_t RegisterThreadHandle(HANDLE handle, DWORD thread_id,
                                     uintptr_t stack_low, uintptr_t stack_high) {
    if (handle == nullptr || thread_id == 0) return 0;
    AcquireSRWLockExclusive(&g_registry_lock);
    uint32_t free_slot = kMaxRegisteredThreads;
    for (uint32_t i = 0; i < kMaxRegisteredThreads; ++i) {
        if (g_registered_threads[i].handle == nullptr && free_slot == kMaxRegisteredThreads)
            free_slot = i;
    }
    if (free_slot == kMaxRegisteredThreads) {
        ReleaseSRWLockExclusive(&g_registry_lock);
        CloseHandle(handle);
        return 0;
    }
    uint32_t generation = g_next_token_generation++;
    if (generation == 0) generation = g_next_token_generation++;
    g_registered_threads[free_slot] = RegisteredThread{
        handle, thread_id, stack_low, stack_high, generation, 0 };
    ReleaseSRWLockExclusive(&g_registry_lock);
    return MakeToken(free_slot, generation);
}

static void AppendEscaped(std::string& output, const char* text) {
    if (!text) { output += "unknown"; return; }
    for (const char* p = text; *p; ++p) {
        if (*p == '\\' || *p == '"') output.push_back('\\');
        output.push_back(*p);
    }
}

static void AppendFrame(std::string& output, uintptr_t pc) {
    HMODULE module = nullptr;
    char path[MAX_PATH]{};
    char offset[40]{};
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCSTR>(pc), &module) && module != nullptr) {
        const DWORD length = GetModuleFileNameA(module, path, MAX_PATH);
        const char* name = path;
        if (length > 0) {
            for (const char* p = path; *p; ++p) if (*p == '\\' || *p == '/') name = p + 1;
        } else {
            name = "unknown";
        }
        AppendEscaped(output, name);
        const auto relative = pc - reinterpret_cast<uintptr_t>(module);
        snprintf(offset, sizeof(offset), "+0x%llx", static_cast<unsigned long long>(relative));
    } else {
        // Not a PE module: commonly Mono JIT code. Never collapse distinct PCs to zero.
        // This is a process-local diagnostic address, not a module RVA usable with a PDB.
        AppendEscaped(output, "dynamic-code-address");
        snprintf(offset, sizeof(offset), ":0x%llx", static_cast<unsigned long long>(pc));
    }
    output += offset;
}

static bool ResumeMainThread() {
    for (int attempt = 0; attempt < 3; ++attempt) {
        if (ResumeThread(g_main_thread) != static_cast<DWORD>(-1)) return true;
    }
    g_enabled.store(false, std::memory_order_release);
    g_active_generation.store(0, std::memory_order_release);
    g_sample_count.store(0, std::memory_order_release);
    return false;
}

} // namespace

#define PERF_EXPORT extern "C" __declspec(dllexport)

PERF_EXPORT int32_t PerfNative_IsSupported(void) {
    return g_enabled.load(std::memory_order_acquire) ? 1 : 0;
}

PERF_EXPORT int32_t PerfNative_GetAbiVersion(void) { return 3; }

PERF_EXPORT int32_t PerfNative_RegisterMainThread(void) {
    if (g_main_thread != nullptr) return GetCurrentThreadId() == g_main_thread_id ? 1 : 0;
    g_main_thread_id = GetCurrentThreadId();
    g_main_thread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT |
        THREAD_QUERY_INFORMATION, FALSE, g_main_thread_id);
    if (g_main_thread == nullptr) return 0;

    ULONG_PTR low = 0;
    ULONG_PTR high = 0;
    GetCurrentThreadStackLimits(&low, &high);
    g_stack_low = static_cast<uintptr_t>(low);
    g_stack_high = static_cast<uintptr_t>(high);
    if (g_stack_low == 0 || g_stack_high <= g_stack_low) {
        CloseHandle(g_main_thread);
        g_main_thread = nullptr;
        return 0;
    }
    return 1;
}

PERF_EXPORT void PerfNative_BeginStall(int64_t generation, int64_t) {
    if (!g_enabled.load(std::memory_order_acquire) || g_main_thread == nullptr || generation <= 0) return;
    g_sample_count.store(0, std::memory_order_release);
    g_active_generation.store(generation, std::memory_order_release);
}

PERF_EXPORT void PerfNative_RequestSample(int64_t generation, int64_t monotonic_ms) {
    if (!g_enabled.load(std::memory_order_acquire) || g_main_thread == nullptr ||
        generation != g_active_generation.load(std::memory_order_acquire)) return;
    const uint32_t index = g_sample_count.load(std::memory_order_acquire);
    if (index >= kMaxSamples || SuspendThread(g_main_thread) == static_cast<DWORD>(-1)) return;

    RawSample& sample = g_samples[index];
    sample.generation = generation;
    sample.monotonic_ms = monotonic_ms;
    const uint64_t begin_us = MonotonicMicros();
    sample.count = CaptureSuspendedThread(g_main_thread, g_stack_low, g_stack_high,
        sample, begin_us + kMaxCaptureUs);
    sample.cost_us = static_cast<uint32_t>(MonotonicMicros() - begin_us);

    if (!ResumeMainThread()) return;
    if (sample.cost_us > kDisableCaptureUs)
        g_enabled.store(false, std::memory_order_release);
    if (generation == g_active_generation.load(std::memory_order_acquire))
        g_sample_count.store(index + 1, std::memory_order_release);
}

PERF_EXPORT const char* PerfNative_EndStall(int64_t generation, int64_t) {
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
        AppendUnwindStop(g_result, sample);
        const int quality = sample.count >= 6 ? 100 : (sample.count > 0 ? 60 : 0);
        g_result += "\",\"sampleCostUs\":" + std::to_string(sample.cost_us);
        g_result += ",\"quality\":" + std::to_string(quality) + "}";
    }
    g_result += "]}";
    return g_result.c_str();
}

PERF_EXPORT uint64_t PerfNative_RegisterCurrentThread(void) {
    const DWORD thread_id = GetCurrentThreadId();
    ULONG_PTR low = 0;
    ULONG_PTR high = 0;
    GetCurrentThreadStackLimits(&low, &high);
    if (low == 0 || high <= low) return 0;

    HANDLE handle = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT |
        THREAD_QUERY_INFORMATION, FALSE, thread_id);
    if (handle == nullptr) return 0;

    return RegisterThreadHandle(handle, thread_id,
        static_cast<uintptr_t>(low), static_cast<uintptr_t>(high));
}

PERF_EXPORT int32_t PerfNative_GetThreadCaptureVersion(void) { return 2; }

PERF_EXPORT uint64_t PerfNative_RegisterThreadByName(const char* thread_name_prefix) {
    if (thread_name_prefix == nullptr || *thread_name_prefix == '\0') return 0;
    wchar_t wanted[128]{};
    const int converted = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
        thread_name_prefix, -1, wanted, static_cast<int>(sizeof(wanted) / sizeof(wanted[0])));
    if (converted <= 1) return 0;

    const HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
    const auto get_description = kernel == nullptr ? nullptr :
        reinterpret_cast<GetThreadDescriptionFn>(GetProcAddress(kernel, "GetThreadDescription"));
    if (get_description == nullptr) return 0;

    const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return 0;
    THREADENTRY32 entry{};
    entry.dwSize = sizeof(entry);
    uint64_t token = 0;
    if (Thread32First(snapshot, &entry)) {
        do {
            if (entry.th32OwnerProcessID != GetCurrentProcessId() ||
                entry.th32ThreadID == GetCurrentThreadId()) continue;
            HANDLE thread = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT |
                THREAD_QUERY_INFORMATION, FALSE, entry.th32ThreadID);
            if (thread == nullptr) continue;
            PWSTR description = nullptr;
            const bool matched = SUCCEEDED(get_description(thread, &description)) &&
                description != nullptr &&
                _wcsnicmp(description, wanted, wcslen(wanted)) == 0;
            if (description != nullptr) LocalFree(description);
            if (!matched) {
                CloseHandle(thread);
                continue;
            }
            uintptr_t low = 0;
            uintptr_t high = 0;
            QueryThreadStackLimits(thread, &low, &high);
            token = RegisterThreadHandle(thread, entry.th32ThreadID, low, high);
            break;
        } while (Thread32Next(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return token;
}

PERF_EXPORT void PerfNative_UnregisterThread(uint64_t token) {
    uint32_t slot = 0;
    uint32_t generation = 0;
    if (!DecodeToken(token, &slot, &generation)) return;
    HANDLE handle = nullptr;
    AcquireSRWLockExclusive(&g_registry_lock);
    RegisteredThread& entry = g_registered_threads[slot];
    if (entry.handle != nullptr && entry.generation == generation) {
        handle = entry.handle;
        entry = RegisteredThread{};
    }
    ReleaseSRWLockExclusive(&g_registry_lock);
    if (handle != nullptr) CloseHandle(handle);
}

PERF_EXPORT const char* PerfNative_CaptureThread(uint64_t token, int64_t monotonic_ms) {
    uint32_t slot = 0;
    uint32_t generation = 0;
    if (!DecodeToken(token, &slot, &generation)) return nullptr;

    AcquireSRWLockExclusive(&g_capture_lock);
    HANDLE thread = nullptr;
    DWORD thread_id = 0;
    uintptr_t stack_low = 0;
    uintptr_t stack_high = 0;
    AcquireSRWLockExclusive(&g_registry_lock);
    RegisteredThread& entry = g_registered_threads[slot];
    if (entry.handle != nullptr && entry.generation == generation) {
        const uint64_t now_us = MonotonicMicros();
        if (entry.last_capture_us == 0 || now_us - entry.last_capture_us >= kMinThreadCaptureIntervalUs) {
            entry.last_capture_us = now_us;
            DuplicateHandle(GetCurrentProcess(), entry.handle, GetCurrentProcess(), &thread,
                0, FALSE, DUPLICATE_SAME_ACCESS);
            thread_id = entry.thread_id;
            stack_low = entry.stack_low;
            stack_high = entry.stack_high;
        }
    }
    ReleaseSRWLockExclusive(&g_registry_lock);

    DWORD exit_code = 0;
    if (thread == nullptr || thread_id == GetCurrentThreadId() ||
        !GetExitCodeThread(thread, &exit_code) || exit_code != STILL_ACTIVE ||
        SuspendThread(thread) == static_cast<DWORD>(-1)) {
        if (thread != nullptr) CloseHandle(thread);
        ReleaseSRWLockExclusive(&g_capture_lock);
        return nullptr;
    }

    RawSample sample{};
    sample.generation = generation;
    sample.monotonic_ms = monotonic_ms;
    const uint64_t begin_us = MonotonicMicros();
    sample.count = CaptureSuspendedThread(thread, stack_low, stack_high,
        sample, begin_us + kMaxCaptureUs);
    sample.cost_us = static_cast<uint32_t>(MonotonicMicros() - begin_us);
    const bool resumed = ResumeCapturedThread(thread);
    CloseHandle(thread);
    if (!resumed || sample.cost_us > kDisableCaptureUs) {
        ReleaseSRWLockExclusive(&g_capture_lock);
        return nullptr;
    }

    g_thread_result.clear();
    g_thread_result += "{\"generation\":" + std::to_string(sample.generation);
    g_thread_result += ",\"monotonicMs\":" + std::to_string(sample.monotonic_ms);
    g_thread_result += ",\"stack\":\"";
    for (uint32_t frame = 0; frame < sample.count; ++frame) {
        if (frame) g_thread_result += "\\n";
        AppendFrame(g_thread_result, sample.pcs[frame]);
    }
    AppendUnwindStop(g_thread_result, sample);
    const int quality = sample.count >= 6 ? 100 : (sample.count > 0 ? 60 : 0);
    g_thread_result += "\",\"sampleCostUs\":" + std::to_string(sample.cost_us);
    g_thread_result += ",\"quality\":" + std::to_string(quality) + "}";
    ReleaseSRWLockExclusive(&g_capture_lock);
    return g_thread_result.c_str();
}

#endif
