# Engine.Rendering.Stats Partial Extraction — Remaining Work

Tracks the topic-by-topic split of `XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs` into focused partial files.

## Status

- Main file: **1230 lines** (down from 2367 originally — 48% reduction).
- Build validation command (avoids editor file locks):
  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj `
    -p:BaseOutputPath=C:\Users\DavidEddy\Documents\Extra\XRENGINE\Build\_AgentValidation\Editor\ `
    /consoleloggerparameters:ErrorsOnly -nologo
  ```
- All completed partials currently build green (exit 0).

## Completed Partials

| Partial | Notes |
| --- | --- |
| `Engine.Rendering.Stats.Octree.cs` | Octree counters + helpers |
| `Engine.Rendering.Stats.RenderMatrix.cs` | Render-matrix swap-cycle stats (fields + toggle + props + methods) |
| `Engine.Rendering.Stats.SkinnedBounds.cs` | Deferred + GPU skinned-bounds stats (fields + toggle + props + methods) |
| `Engine.Rendering.Stats.GpuPipelineProfiler.cs` | Profiler proxies |
| `Engine.Rendering.Stats.RtxIo.cs` | RTX IO counters |
| `Engine.Rendering.Stats.GpuFallback.cs` | GPU fallback path stats |
| `Engine.Rendering.Stats.GpuTransparency.cs` | Transparency stats |
| `Engine.Rendering.Stats.GpuMeshlets.cs` | Meshlet stats |
| `Engine.Rendering.Stats.Readback.cs` | Readback stats |
| `Engine.Rendering.Stats.Vram.cs` | VRAM/FBO allocation tracking |
| `Engine.Rendering.Stats.PixelFormats.cs` | `GetBytesPerPixel` overloads |
| `Engine.Rendering.Stats.Vr.cs` | VR / VR-XR record paths |
| `Engine.Rendering.Stats.Helpers.cs` | `AddNonNegative`, `UpdateMaxCounter`, `StopwatchTicksToMilliseconds`, `NormalizeDescriptorBindingClass`, `AppendDiagnosticToken`, `TruncateDiagnosticText` |

## Remaining Work

### Vulkan extraction (split-by-subtopic)

Vulkan is by far the largest remaining block: ~170 fields, ~20 `Record*` methods, large property block, 2 enums. User-approved approach is to split into focused sub-partials.

Recommended order (smallest first to validate pattern):

1. **`Engine.Rendering.Stats.Vulkan.Barriers.cs`** — `RecordVulkanBarrierPlannerPass`, `RecordVulkanAdhocBarrier` + `_vulkanBarrier*`, `_vulkanQueueOwnershipTransfers`, `_vulkanBarrierStageFlushes`, `_vulkanAdhocBarrier*` fields + properties.
2. **`Engine.Rendering.Stats.Vulkan.Indirect.cs`** — `RecordVulkanIndirectSubmission`, `RecordVulkanIndirectBatchMerge`, `RecordVulkanIndirectRecordingMode` + indirect count/non-count path fields + properties.
3. **`Engine.Rendering.Stats.Vulkan.Descriptors.cs`** — `RecordVulkanDescriptorPoolCreate/Destroy/Reset`, `RecordVulkanDescriptorFallback`, `RecordVulkanDescriptorBindingFailure` + descriptor pool/fallback fields + properties.
4. **`Engine.Rendering.Stats.Vulkan.Allocations.cs`** — `RecordVulkanAllocation`, `RecordVulkanOomFallback`, `RecordVulkanDynamicUniformAllocation`, `RecordVulkanDynamicUniformExhaustion`, `RecordVulkanRetiredResourcePlanReplacement` + allocation telemetry fields + properties + `EVulkanAllocationTelemetryClass` enum.
5. **`Engine.Rendering.Stats.Vulkan.FrameLifecycle.cs`** — `RecordVulkanFrameLifecycleTiming`, `RecordVulkanFrameGpuCommandBufferTime`, `RecordVulkanQueueSubmit`, `RecordVulkanQueueOverlapWindow` + frame-lifecycle ticks + properties + `EVulkanGpuDrivenStageTiming` enum.
6. **`Engine.Rendering.Stats.Vulkan.Diagnostics.cs`** — `RecordVulkanValidationMessage`, `RecordVulkanFrameDiagnostics` + `_vulkanDiagnosticLock` + diagnostic text fields + properties.

#### Current Vulkan record-method line locations (in the 1230-line main file)

| Line | Method |
| ---: | --- |
| 707 | `RecordVulkanAllocation` |
| 732 | `RecordVulkanDescriptorPoolCreate` |
| 740 | `RecordVulkanDescriptorPoolDestroy` |
| 748 | `RecordVulkanDescriptorPoolReset` |
| 756 | `RecordVulkanQueueSubmit` |
| 764 | `RecordVulkanOomFallback` |
| 772 | `RecordVulkanFrameDiagnostics` |
| 831 | `RecordVulkanValidationMessage` |
| 844 | `RecordVulkanDescriptorFallback` |
| 879 | `RecordVulkanDescriptorBindingFailure` |
| 904 | `RecordVulkanDynamicUniformAllocation` |
| 914 | `RecordVulkanDynamicUniformExhaustion` |
| 920 | `RecordVulkanRetiredResourcePlanReplacement` |
| 930 | `RecordVulkanFrameLifecycleTiming` |
| 951 | `RecordVulkanFrameGpuCommandBufferTime` |
| 1005 | `RecordVulkanIndirectSubmission` |
| 1025 | `RecordVulkanIndirectBatchMerge` |
| 1037 | `RecordVulkanIndirectRecordingMode` |
| 1054 | `RecordVulkanBarrierPlannerPass` |
| 1072 | `RecordVulkanQueueOverlapWindow` |
| 1096 | `RecordVulkanAdhocBarrier` |

Vulkan fields span roughly lines 22–225. Vulkan properties span roughly lines 280–395.

### Per-sub-partial workflow

For each sub-partial:

1. Identify the exact field, property, and method ranges in the current main file (line numbers shift after each extraction — re-grep before slicing).
2. Build the new partial with namespace + nested partial-class scaffold:
   ```csharp
   using System;
   using System.Threading;
   // additional usings as needed (e.g. using XREngine.Timers; for StopwatchTicksToMilliseconds)

   namespace XREngine
   {
       public static partial class Engine
       {
           public static partial class Rendering
           {
               public static partial class Stats
               {
                   // extracted content
               }
           }
       }
   }
   ```
3. Slice with PowerShell `Get-Content` / array indexing; **delete highest line range first** to keep prior indices valid.
4. Run the build validation command — must exit 0.
5. Fix any missing `using` directives or off-by-one slice losses (re-read boundary lines if a symbol fails to resolve).

### After Vulkan is fully extracted

- Audit main file for any remaining cross-topic helpers; relocate to `Helpers.cs` if shared.
- Confirm `BeginFrame` and remaining shared fields/properties stay in the root partial.
- Final build validation + run targeted profiler/stats tests if any exist.

## Known Pitfalls

- `StopwatchTicksToMilliseconds` requires `using XREngine.Timers;` (`EngineTimer` lives there).
- `ERenderBufferStorage` requires `using XREngine.Rendering;`.
- Slice indexing is 0-based but `Get-Content` line numbers are 1-based — confirm by re-reading boundary lines.
- Trailing `}` may abut the next declaration without a blank-line separator; do not assume a separator when computing delete ranges.
- Editor process may lock `Build/Editor` outputs — always use the alternate `BaseOutputPath` shown above for validation builds.
- Build emits ~904 NuGet vulnerability warnings (Magick.NET-Q16-HDRI-AnyCPU 14.11.1); these are pre-existing and unrelated.
