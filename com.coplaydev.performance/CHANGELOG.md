# Changelog

## Unreleased

- Added generation-safe Windows x64 registration and capture for arbitrary native threads.
- Added automatic Windows `GfxDeviceW*` RenderThread discovery and sampling beside UnityMain.
- Rejected self-capture, exited threads, stale tokens and captures faster than 100 Hz.
- Serialized concurrent arbitrary-thread captures and bounded unwind work to protect target threads.
- Made Watchdog shutdown interruptible so Domain Reload cannot leave a native-calling thread alive.
- Closed the Android delayed-signal race between the final sample and `EndStall`.

## 0.8.0

- Added durable local aggregation by session, build, scene, page and dominant stack.
- Added occurrence count, total/max duration and first/last occurrence timestamps.
- Kept the worst-duration event as the representative stack and context evidence.
- Prevented upload acknowledgements from deleting an aggregate updated during upload.
- Removed the per-session occurrence cutoff; bounded queues and disk capacity remain the overload controls.

## 0.7.0

- Reduced detection to one rule: report foreground UnityMain stalls lasting at least 500ms.
- Removed sustained-frame state machines, minor/major thresholds and client-side fingerprint deduplication.
- Reduced event kinds to `LongStall` and `Freeze`; event/upload schema is now version 3.
- Kept frame timing only as a watchdog-miss fallback and as lightweight event context.

## 0.6.0

- Switched the default pipeline to long-stall-only reporting with a 500ms threshold.
- Filtered single-frame spikes from persistence/upload while retaining lightweight frame metrics.
- Added automatic navigation transient suppression through `SetPage` and an explicit expected-work API.
- Delayed native sampling to the long-stall boundary and reduced the default sample count to six.
- Added diagnostics counters for filtered peak frames, short stalls and expected transients.

## 0.5.0

- Added Windows x64 UnityMain sampling with suspend/context capture, PE module RVA output and x64 unwind metadata walking.
- Added Windows CMake/MSVC build automation and separate PDB archival output.
- Added Windows ABI and end-to-end suspend/capture/resume smoke validation.

## 0.4.0

- Moved native stack draining and symbol formatting off the Unity main thread.
- Added native ABI version validation and isolated failing custom stack samplers.
- Added bounded iOS suspend sampling, resume failure handling and arm64e pointer normalization.
- Added bounded Android frame walking, stack bounds hardening and lock-free atomic validation.
- Made initialization rollback, upload coroutine cancellation and writer shutdown retry-safe.

## 0.3.0

- Added explicit UnityMain registration to the native ABI.
- Added stall generations so delayed native samples cannot enter a later event.
- Added adaptive front-loaded sampling, cooldown and per-minute sample budgets.
- Added Android arm64 signal/ucontext/frame-pointer sampler source and CMake project.
- Added iOS arm64 bounded suspend/register/frame-pointer sampler source.
- Added sample quality/cost fields and dominant-stack aggregation.

## 0.2.0

- Moved serialization, queue writes and upload queue reads off the Unity main thread.
- Added CRC validation, crash recovery and corrupt-file quarantine.
- Added exponential upload backoff, power-of-two repeat summaries and SDK self diagnostics.
- Added GC memory ProfilerRecorder metrics when supported by Unity.
- Added watchdog-only stall recovery events and production build configuration validation.

## 0.1.0

- PlayerLoop-based frame monitoring and main-thread heartbeat.
- Single-frame, sustained, severe and freeze classification.
- Bounded breadcrumb and metric collection.
- Native stack sampler ABI plus managed recovery-stack fallback.
- Fingerprinting, session deduplication, atomic disk queue and bounded upload pipeline.
