# Vulkan 1.4 background replay and queue overlap

## Objective and current disposition

Implement the supported output transaction required by E2, then evaluate and
implement Phase F's useful native queue execution. This follows the
[Phase E investigation](vulkan14-phase-e-reuse-and-descriptors-2026-09-09.md)
and [barrier research](vulkan14-barrier-performance-research-2026-09-09.md).

The background output must retain exact resource readiness, target ownership,
submission receipts, completion tracking, and fresh output writes. Only exact
native command artifacts may be reused. Desktop PresentNow and XR policies
remain fresh. Diagnostic key equality alone is not accepted as replay evidence.

D4's EarlyVisibility-to-BuildIndirect candidate remains rejected. It missed the
promotion gate and did not expose proven independent work at that boundary.
Reopening D4 requires a worthwhile dependency boundary, complete consumer
coverage, stable paired measurements, and GPU scheduling evidence. NVIDIA GPU
performance-counter permission blocked the previous Nsight capture. There is no
calendar wait and no D4 promotion prerequisite for E2 or F.

## E2 implementation and current validation

`VulkanExplicitTargetRendererHost.SubmitBackgroundProductionFrame` provides an
intentional presentationless `SceneCapture` / `Background` / `BlockForExact`
transaction, with no stale-output fallback. The returned output description
carries its canonical scheduling request into visibility collection and history
publication. The recorder permits only the dedicated, fully proven indirect
secondary artifact to replay. Whole-primary reuse and producer deferral remain
disabled. Desktop PresentNow and XR retain their fresh recording contracts.

Cold exact preparation exposed a separate real failure: asynchronous CPU mesh
index creation ran outside the renderer's thread-local wrapper-creation scope.
The CPU index buffer existed but the lookup-only Vulkan port could never create
its native wrapper. Index build tickets now expose completion, invalidation and
faults; exact preparation waits for that CPU result, then the existing program
creation port creates the Vulkan wrapper under the current renderer. Ordinary
asynchronous preparation remains nonblocking. An indirect operation that failed
to bind is no longer attested as successfully recorded.

Release RenderBench validation on RTX 3090 / driver 610.88, with standard and
synchronization validation enabled, completed these runs:

| Run | Completed frames | Native indirect reuses | Validation errors |
| --- | ---: | ---: | ---: |
| Background, static/material/root/buffer/viewport changes | 225 | 70 | 0 |
| Matching foreground recording control | 225 | 0 | 0 |

The foreground control rejected replay despite complete matching keys. This
proves that the background contract grants the permission and that foreground
policy remains effective. Both runs read back completed receipts; the inspected
material image changes from magenta to cyan. Standard and synchronization
validation reported zero errors in each run; each had four startup Vulkan loader
registry warnings and no rendering validation warnings. These counts establish
native replay, not a frame-time speedup or default-policy promotion.

The final-scissor comparison, `reports/final-scissor-controls.png`, confirms
fresh output for material mutation, root and buffer invalidation, an 800×450
viewport shrink, and a 960×540 restore; the old 90-row retained-pixel band is
gone. RenderDoc previously isolated the fault in `resize_capture.rdc`: at pixel
300,50, EID 165 (`scissorClipped`) inherited an old `PostProcessFBO` scissor
after `MatchDestinationRenderArea` updated the viewport. The earlier fixture
target was clean. `VPRC_RenderQuadToFBO` now scopes the crop to its destination,
and the full-resolution viewport fallback uses the explicit `FinalOutput`
extent. An explicit whole-physical-extent clear alone does not attest a missing
terminal writer. E2 is complete.

## F candidate and native execution

The selected experiment overlaps Advanced WorkClassification with independent
GTAO, froxel construction and background shading. The prior D3 timing sample
measured classification at approximately 1.515 ms and the independent work at
0.191 + 0.214 + 0.022 = 0.427 ms. Thus 0.427 ms is an optimistic overlap ceiling,
before extra submission costs or shared GPU-resource contention. It is not a
predicted speedup.

The implementation uses two distinct compute-capable queues in the graphics
family, so there is no queue-family ownership transfer. Four retained primaries
form one output transaction:

1. G0 finishes visibility and establishes shared input-image layouts, then
   signals `Ready`.
2. C waits for `Ready`, classifies work and produces indirect arguments, then
   signals `Classified`.
3. G1 follows G0 on the graphics queue without waiting for C and records GTAO,
   froxel and background work.
4. G2 waits for `Classified`, shades and finishes the output, then signals the
   normal graphics timeline and target completion fence.

Per-slot prefix fences settle the additional queue's lifetime domain. The final
fence proves the joined transaction; accepted partial submissions are drained
before reusing their resources, and binary semaphores are replaced after a
rejected tail. Native acceptance, lifetime-pin transfer and post-submit state
publication must all succeed before recording state can feed a successor.
Producer completion markers bind only to the final joined timeline. Query pools
are retained separately by every command buffer that uses them.

This is a selected mono presentationless Advanced executor. The generic
cross-family frame-graph support gate remains closed. `GraphicsComputeTransfer`,
desktop/XR, and other output families reject explicit unsupported requests.
The explicit transfer request reports `NotSupported` with zero Vulkan validation
errors. `Auto` resolves to graphics-only because the measured split regressed;
the receipt reports requested/executed modes and native submission count.

The formerly pending publication and final-access issues are resolved. Manual
collection carries the explicit nonzero output frame identity through world swap,
global-resource capture, canonical scene publication, and finalization of the
already prepared package. It does not clamp zero, rerun world swap, or reprepare
the package. `AcceptedFramePlan` now propagates through the split path; split
preparation occurs after `TryPrepare...`; and G2 records its real Identity and
Metadata `General`/read accesses, barriers, and completion journal. Readback
prefix completion occurs before pool/fence reuse, shutdown retires split work
before borrowed target fences are destroyed, and production timing samples only
completed slots. Temporary hooks and logging were removed.

## F validation and measured disposition

`advanced-control-lit` and `advanced-split-lit` each completed 140 frames with
normal lit Advanced shading. Standard and synchronization validation reported
zero errors; each run reported four Vulkan loader warnings. The native one-submit
and four-submit readbacks were byte-identical, and the inspected
`reports/advanced-split-lit/output.png` showed visible lit
geometry.

`advanced-mode-changes` completed 80 frames across three slots, switching
Compute → Only → Auto → Compute for 20 frames each. It passed with zero errors;
Both `GraphicsOnly` and `Auto` selected one submission; `GraphicsCompute`
selected four. A separate `advanced-auto` run completed 16 frames with one
submission per frame. `advanced-gateway-recovery-g1` and `advanced-gateway-recovery-g2` each
injected exactly one pre-native gateway rejection after validation and pin
acquisition. G1 followed accepted G0/C; G2 followed accepted G0/C/G1. The
same-host recovery completed 40 frames with four submissions per frame, with readback and
dispose/exit succeeding and zero errors.

The paired timing protocol used six sequential alternating runs,
C1/S1/S2/C2/C3/S3, with 80 warm-up and 400 measured frames per run at 1280×720,
three slots, RTX 3090 / driver 610.88, DescriptorIndexing ShippingFast, and
validation disabled. It used normal lit Advanced shading and no RenderDoc.
There are 2,400 completed GPU samples with source identity. The exact table is
in `reports/overlap-performance.txt` and `.json`:

| Metric (median of run medians) | Graphics-only | Split | Change |
| --- | ---: | ---: | ---: |
| GPU command-buffer timing | 0.939312 ms | 1.371344 ms | +45.9945% |
| CPU timing | 7.42925 ms | 7.8792 ms | +6.06% |
| Allocations | 322,296 bytes | 340,744 bytes | +5.72% |

The graphics-only control GPU spread was 0.5236%. The CPU change is below the
10.91% control spread, so it is not attributed to the split. All three paired
outputs were byte-identical. GPU timing covers coarse G0-start through G2-end
on the main queue; it does not prove physical overlap. An Nsight scheduling trace
would be required for that claim.

F1–F3 are complete. The implementation remains opt-in and `Auto` remains
graphics-only because the measured outcome is a regression, not a promotion.
D4's rejected early-visibility candidate remains removed. Its next experiment
requires a new boundary, stable timings, and scheduling-trace counter permission;
there is no calendar prerequisite.

## Durable RenderBench reproduction

Reference `XREngine.RenderBench` from a local .NET 10 Windows harness, run from
the repository root, and configure `XRE_VK_DESCRIPTOR_BACKEND=DescriptorIndexing`,
`XRE_FORCE_MESH_SUBMISSION_STRATEGY=GpuIndirectZeroReadback` and
`XRE_ZERO_READBACK_MATERIAL_DRAW_PATH=BindlessMaterialTable`. Enable
`XRE_VULKAN_VALIDATION=1` and `XRE_VULKAN_SYNC_VALIDATION=1` for correctness;
disable validation for timings. Keep harness outputs under `Build/_AgentValidation/`.
The benchmark uses the production timing field, not the raw target-path timing:

```csharp
using System.Diagnostics;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.RenderBench;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

// options supplies Width=1280, Height=720, FrameSlots=3 and an output directory.
using var scene = new RenderBenchProductionScene(
    options, EOcclusionCullingMode.Disabled, useAdvancedPipeline: true);
RuntimeEngine.Rendering.Settings.VulkanQueueOverlapMode = EVulkanQueueOverlapMode.GraphicsCompute;
RuntimeEngine.Rendering.Settings.AdvancedRenderPipelineMode = EAdvancedRenderPipelineMode.Required;
RuntimeEngine.Rendering.Stats.EnableTracking = true;
VulkanExplicitProductionSubmissionReceipt receipt = default;
for (int frame = 0; frame < 140; frame++)
    receipt = scene.SubmitStep(1.0 / 60.0, backgroundCapture: true);
VulkanGpuCommandBufferTimingSample timing =
    RuntimeEngine.Rendering.Stats.Vulkan.LastCompletedVulkanFrameGpuCommandBufferTiming;
if (!timing.IsCompleted)
    throw new InvalidOperationException("No completed production timing sample.");
var wait = Stopwatch.StartNew();
while (!scene.Host.TryGetProductionSubmissionCompletion(in receipt, out bool completed) || !completed)
{
    if (wait.Elapsed.TotalSeconds > 15)
        throw new TimeoutException("Production receipt did not complete.");
    Thread.Sleep(1);
}
int byteCount = checked((int)(options.Width * options.Height * 4));
if (!scene.Host.TryReadbackProductionColor(in receipt, byteCount, out byte[]? rgba))
    throw new InvalidOperationException("Completed production readback failed.");
```

Submit enough frames to fill and retire every slot before sampling or reading
back. `host.LastCompletedGpuFrameNanoseconds` is the raw target-path value and
is not the production timing metric used above.

## Evidence locations

Disposable evidence for this continuation lives under
`Build/_AgentValidation/20260909-202419-vulkan14-ef/`. Required findings and
reproduction commands will be copied here; the implementation must not depend
on ignored artifacts.

Final verification completed: the uninstrumented source build passed with zero
warnings and errors in 5.07 seconds (`logs/final-build.log`), and
`advanced-final` completed 16 four-submit frames with zero errors plus successful
readback and dispose/exit. `background-final-clean` completed 225 frames with
70 native reuses and zero errors. Its six phase RGBA files were byte-identical to
the visually verified final-scissor control. `git diff --check` passed, temporary
hooks/log strings were absent, and no task-owned GPU processes remained. No tests
were added or modified; validation used production runtime harnesses and a narrow
build. No editor/headset session, commit or push was needed.

The prior broker reasoning attempt failed because API billing had no available
credit. Native agents are used for bounded independent architecture and
validation-path review; no broker result is claimed for this continuation.

## Primary references

- [Khronos synchronization examples](https://docs.vulkan.org/guide/latest/synchronization_examples.html)
  describe semaphore memory dependencies and paired exclusive queue-family
  ownership transfers.
- [Khronos timeline semaphore sample](https://docs.vulkan.org/samples/latest/samples/extensions/timeline_semaphore/README.html)
  demonstrates independently submitted compute and graphics queues.
- [NVIDIA async compute guidance](https://developer.nvidia.com/blog/advanced-api-performance-async-compute-and-overlap/)
  explains why independent work and complementary hardware demand must be
  measured; additional queues alone do not imply a speedup.
