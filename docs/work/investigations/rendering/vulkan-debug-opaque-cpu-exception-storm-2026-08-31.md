# Vulkan Debug Opaque CPU and Exception Storm Investigation

Date: 2026-08-31  
Status: Resolved and live-validated

## Problem

The Vulkan Debug Opaque Sponza path reported roughly 10–15 ms of CPU render
cost and 22–23 ms between render dispatches even though GPU work was effectively
instant. Visual Studio also reported repeated first-chance exceptions:

- `ReflectionTypeLoadException` (about 82)
- `VulkanPresentNowReadinessException` (about 815)
- its `InvalidOperationException` base type (about 815)

## Root causes

1. The frame overlay conflated the interval between dispatches with CPU render
   work. That interval includes render/present pacing and time that the collect
   thread is deliberately parked waiting for the renderer.
2. Debug Opaque did not consume the canonical Advanced resident-scene package,
   but every scene swap still rebuilt it. A captured frame attributed 9.331 ms
   entirely to
   `GpuIndirect.GPUScene.SwapCommandBuffers.AdvancedPublication`; actual visible
   collection was below 1 ms.
3. Expected pre-acquire `PresentNow` retry state was represented by throwing
   `VulkanPresentNowReadinessException`. Visual Studio therefore counted both
   that derived type and its `InvalidOperationException` base for every retry.
   A texture-upload retry could also keep a readiness attempt alive for the
   watchdog window even after useful progress had ended.
4. Reflection discovery repeatedly called `Assembly.GetTypes()`. The copied
   `OpenVR.NET.dll` was a stale prebuilt artifact that referenced missing
   `SixLabors.ImageSharp, Version=2.0.0.0`, so each discovery service repeated
   the same partial-load failure.

## Corrections

- Added separate overlay fields for dispatch interval, full dispatch CPU,
  Vulkan backend CPU, GPU time, render-waiting-for-collect, and
  collect-waiting-for-render.
- Made canonical GPU-scene publication demand-driven. Default, Advanced, and
  derived RVC pipelines opt in; Debug Opaque and other lightweight pipelines do
  not. Requests coalesce into one allocation-free per-scene bit at the swap
  boundary, and global Advanced resources are captured only when requested.
- Added a non-throwing `VulkanPresentNowReadinessRetry` value for ordinary
  pre-acquire retry outcomes. True capacity, invariant, device, and liveness
  failures remain typed exceptions. A progressed-but-still-active upload now
  returns retry state immediately instead of spinning to the watchdog limit.
- Added `XRLoadableTypeCatalog`, which caches one loadable-type result per
  assembly, preserves partial results, and deduplicates loader diagnostics.
  Runtime/editor reflection discovery now uses the catalog.
- Replaced direct references to an existing OpenVR binary with project
  references to the checked-out `OpenVR.NET` source. The rebuilt library has no
  ImageSharp runtime reference. Third-party generated XML-comment warnings are
  suppressed only for the `OpenVR.NET` project.

## Validation

The final isolated session was `vulkan-cpu-exceptions-4` with Vulkan and forced
Debug Opaque. It rendered Sponza continuously and reported:

| Metric | Final completed-frame sample |
| --- | ---: |
| Whole frame | 11.914 ms |
| Whole-frame p50 / p95 | 11.128 / 12.468 ms |
| Scene collect CPU | 0.343 ms |
| Scene swap CPU | 0.039 ms |
| Scene render CPU | 0.803 ms |
| Present/frame pacing | 10.977 ms |
| Render waiting for collect | 0.048 ms |
| Collect waiting for render | 11.084 ms |
| Vulkan validation errors/messages | 0 / 0 |

The CPU hierarchy records canonical Advanced publication at 0.000 ms in the
steady Debug Opaque frames; global scene swap is approximately 0.05–0.12 ms.
The remaining dispatch interval is therefore presentation pacing, which is why
the collect thread waits for the render side. It is not collect-visible work.

`list_assemblies` enumerated 154 assemblies, including 694 OpenVR.NET types,
with `loaderFailureCount=0`. The stopped-session log scan found no
`ReflectionTypeLoadException`, `VulkanPresentNowReadinessException`,
`InvalidOperationException`, unhandled exception, validation error, or VUID.
The latest historical upload event remains available as typed telemetry
(`VulkanPresentNowReadinessRetry`) while the current sampled frame completed
successfully with no terminal exception.

Evidence:

- Before-demand-gating CPU dump:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260831-220741-vulkan-cpu-exceptions-2/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-08-31_22-08-52_pid57716/profiler-cpu-frame-2026-08-31-22-11-06-564-437ae638.log`
- Final CPU dump:
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260831-222237-vulkan-cpu-exceptions-4/logs/XREngine.Editor_debug/windows_x64/xrengine_2026-08-31_22-23-49_pid35492/profiler-cpu-frame-2026-08-31-22-24-28-334-ed55fdf5.log`
- Final viewport capture:
  `Build/_AgentValidation/20260831-215730-vulkan-cpu-exceptions/mcp-captures/final/Screenshot_20260831_222428_488_660f7d83635f4693a2c2cdca3eb37e41.png`

The editor and OpenVR.NET source project build with zero warnings and errors.
Automated tests were not added or run because repository policy requires live
feature validation first and explicit user clearance before test work for an
active regression; the isolated runtime path is the acceptance evidence here.

## Master TODO coverage

The master Vulkan TODO already covered the PresentNow readiness and asynchronous
texture/pipeline portions in Phases 0, 5.3, and 5.4. It did not explicitly call
out unwanted canonical Advanced publication by opt-out diagnostic pipelines,
and reflection/OpenVR assembly loading is outside the Vulkan frame-loop scope.
The master TODO now records this closeout and the demand-driven publication
invariant.
