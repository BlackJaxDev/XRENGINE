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
| Background, static/material/root/buffer/viewport changes | 225 | 69 | 0 |
| Matching foreground recording control | 225 | 0 | 0 |

The foreground control rejected replay 223 times despite complete matching
keys. This proves that the background contract grants the permission and that
foreground policy remains effective. Both runs read back completed receipts;
the inspected material image changes from magenta to cyan. Four startup Vulkan
loader registry warnings occurred per run; no rendering validation warnings were
reported. These counts establish native replay, not a frame-time speedup.

Image inspection found retained pixels outside a shrunken viewport in both
paths. Whole-output resize freshness remains open at the user's wrap-up request;
these initial runs alone do not close E2. The compared image is
`reports/replay-controls.png` under the evidence root. Material mutation,
indirect-buffer replacement and restored viewport captures were inspected too.

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
desktop/XR and other output families reject explicit unsupported requests.
`Auto` remains graphics-only pending a measured net benefit. Submission receipts
report requested/executed modes and the actual native frame submission count.

The manual RenderBench lifecycle now finalizes its already prepared canonical
GPU-scene package after the world publishes its swapped buffers. It preserves
package identity/generation and collection membership; it does not fabricate
readiness or rerun package preparation. The next graphics-only bootstrap run
reached native preparation but failed with:

> Authoring-owned canonical views require a valid scene publication generation.

The first world swap currently stamps the publication with ambient
`RuntimeEngine.Rendering.State.RenderFrameId`, which is zero before the manual
lifecycle calls `BeginRenderFrame`. The explicit output already owns a nonzero
frame identity; publication must use the appropriate frame authority without
clamping zero, relaxing readiness, or performing a second world swap. This fix
was not applied before the user's wrap-up request.

Final review also identified missing final-segment image access publication:
G2 inherits entry layouts from G1, but must record its own real reads/writes for
Identity, Metadata, Depth, AO, HDR, Velocity, Reactive and ShadingDiagnostics.
Re-emit their exact transitions after the split, or record equivalent accesses
in G2's journal. Do not copy inherited entries into fake exit states.

The candidate executor is therefore **disabled** by
`AdvancedQueueOverlapRuntimeValidated`; an explicit multi-queue request reports
the unfinished validation instead of silently running graphics-only. Enable it
only while completing the identified fixes and native validation.

Consequently no four-submit frame, synchronization-valid split output, partial
native submission failure, or paired performance comparison is claimed. F1 is
complete; F2/F3 remain unchecked. Reviewed code is retained for continuation.

## Wrap-up validation and remaining sequence

- The final Release RenderBench build passed with **zero warnings and errors**
  (`logs/f-build-final.log`, elapsed 10.22 seconds).
- `git diff --check` passed. Git emitted only existing LF/CRLF conversion notices.
- No tests were added or changed. Runtime fixtures exercised the production
  background/foreground paths; no native queue-overlap result was fabricated.
- No editor or headset session was started for this continuation. All launched
  runtime fixtures exited. No commit or push was made.

Resume by fixing exact-output initialization for subrect rendering, the manual
Advanced publication frame identity, and G2 image access publication. The clear
belongs in the terminal output command buffer (G2 under F), must cover the whole
physical extent, and must not count as a terminal producer. For publication,
keep the ambient world-swap overload and add an explicit frame-ID overload; pair
scene publication with the captured global-resource frame ID. Then run a small graphics-only and
four-submit synchronization-validation comparison, inspect both outputs, cover
multiple frame slots and rejection recovery, and finally collect stable paired
CPU/GPU timings. Keep `Auto` graphics-only unless those measurements justify
promotion. D4's rejected early-visibility candidate stays removed; its next
experiment requires measured independent work and scheduling evidence, not a
calendar date.

## Evidence locations

Disposable evidence for this continuation lives under
`Build/_AgentValidation/20260909-202419-vulkan14-ef/`. Required findings and
reproduction commands will be copied here; the implementation must not depend
on ignored artifacts.

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
