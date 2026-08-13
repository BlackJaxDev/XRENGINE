# Vulkan Phase 5 Output Scheduling And Camera-Motion Investigation

## Status

Open as of 2026-08-12. The native FPS-overlay continuity defect is fixed in the
sampled recovery path, and camera-motion stalls are materially shorter, but the
strict unlit-Sponza no-regression gate is not met. Phase 5 also still has five
open architectural criteria. This document is the handoff boundary for the
attempted fixes and the next investigation pass.

The Phase 5 checklist was accidentally removed during an earlier closeout. It
is restored in
`../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md`. The two
completed implementation items are checked there and copied to the completed
sibling; the incomplete items remain active.

## Problem

Moving the editor camera through the unlit Sponza scene can freeze presentation,
reduce frame rate, and make the native FPS text disappear intermittently. Before
the Phase 5 work, the same scene was reported to be fast. The acceptance target
is therefore not merely a stationary warmed frame: camera traversal must remain
responsive while new visibility, pipeline, descriptor, and command-chain state
appears.

Phase 5 additionally requires one deadline-aware executable output DAG,
nonblocking XR/secondary outputs, bounded optional-output deferral, narrow queue
ownership, bounded modal resize, and safe persistent recording workers.

## Reproduction And Findings

### Original camera-motion failure

- The isolated Vulkan session `vulkan-phase5-camera-instability` reproduced the
  freeze after entering an uncached Sponza view. Stable-camera recording was
  approximately 7-17 ms.
- Two rejected frames spent 326.2 and 346.9 ms in recording. The next completed
  frames spent 812.5 and 742.2 ms. Queue submission remained 0.4-8.0 ms, which
  localized the freeze to render-thread CPU preparation/recording rather than a
  queue wait or GPU stall.
- A detailed 419.1 ms frame spent 42.2 ms lowering a new command-chain schedule,
  126.0 ms in primary prewarm, and 189.0 ms encoding the primary command buffer.
- Camera motion exposed cold mesh/pipeline variants. Thirty pipelines compiled
  in one four-second interval, but their reported native compile time totaled
  only 9.24 ms (0.61 ms maximum). Pipeline discovery triggered the cold path;
  native compilation was not the dominant stall.
- The scheduled mesh-secondary executor existed but was not called by the
  authoritative mesh payload recording path. Cold scheduled mesh runs therefore
  fell through to expensive inline-primary encoding.
- Recovery recorded ImGui over the last complete scene but omitted the native
  dynamic-text command buffer. Alternating recovered and completed swapchain
  images caused the FPS text to disappear and reappear.

### Remaining cost after the main fix

Wiring scheduled mesh runs into the authoritative payload path removed the
largest 300-800 ms behavior, but did not restore a strict no-regression result:

| Measurement | Current camera-motion sample |
| --- | ---: |
| Unique frames | 41 |
| Deferred frames | 0 |
| Frames missing dynamic overlay | 0 |
| Vulkan validation errors | 0 |
| Average total Vulkan CPU stage | 86.26 ms |
| p95 total Vulkan CPU stage | 207.19 ms |
| Maximum total Vulkan CPU stage | 218.28 ms |
| Maximum preparation | 23.71 ms |
| Maximum primary handling | 190.97 ms |
| Maximum encoding | 104.52 ms |
| Maximum secondary merge | 6.85 ms |

Later frames returned to roughly 18.7-22.4 ms. A detail-instrumented close-wall
frame completed in 28.5745 ms: 16.2219 ms preparation, 4.3753 ms primary
handling, and 3.9341 ms packet construction, with 673 scheduled/reused chains
and no newly recorded chains. Detailed diagnostics add overhead and are not a
clean performance capture.

Current evidence points to cold command-chain artifact materialization in each
swapchain/frame slot plus expensive compatibility/resource validation. Several
small post-process chains repeatedly report `ResourcePlan` invalidation despite
an unchanged structural packet signature, and some image entries report
`MissingCommandBufferState`. Published uniform slot bases are stable per
renderer/family, so visibility order or uniform-base churn does not explain most
of the invalidation.

The automated viewport-sequence capture under
`Build/_AgentValidation/20260812-122100-vulkan-phase5/mcp-captures/ViewportSequence_20260812_235718_174_12a2b9137693458db1684c679fdaf948/`
is excluded from performance conclusions. Framebuffer readback added periodic
166-183 ms CPU work and the captured frames did not prove that the camera moved.

## Attempted Fixes

### Retained fixes

- Normalize scheduling-only `FrameOpContext` fields out of command-recording
  compatibility so per-frame output requests do not split otherwise reusable
  recording runs.
- Make graphics-pipeline prewarm resumable within a 2 ms admission slice and
  use nonblocking entry to the mesh pipeline preparation gate.
- Replace per-pipeline OS-thread creation with the persistent pipeline compiler
  and suppress routine sub-2 ms compile logging.
- Replace the order-sensitive mesh warm-preparation ledger with a bounded,
  pre-sized signature set keyed by stable pipeline/resource preparation state.
- Call `TryExecuteScheduledMeshCommandChainSecondaryRun` from the authoritative
  mesh payload path. This is the change that removed the worst inline-primary
  camera-motion stalls.
- Record and submit the current native dynamic-text overlay when presenting the
  last complete scene. A 196-frame recovery sample and the later 41-frame
  camera-motion sample both reported zero missing dynamic overlays.
- Keep queue-drain cohorts and materialization scratch storage preallocated and
  bound cold materialization work rather than allocating it on every frame.
- Reject partial persistent-worker batches after the first timeout, quarantine
  artifacts while abandoned workers remain active, and guard primary recording
  before reuse, serial fallback, or artifact migration.

### Implemented Phase 5 items

- Win32 modal resize now freezes the active pipeline/planner/swapchain resource
  generation, suppresses interactive swapchain recreation, uses validated WSI
  presentation scaling where supported, and performs the catch-up after the
  modal loop exits.
- Eligible independent non-graphics secondary packets use persistent workers
  with worker-owned command pools/arenas. Small or ineligible batches retain the
  serial recording path.

### Ruled out or incomplete attempts

- Native pipeline compile duration is too small to explain the largest stalls.
- A stationary warmed-view microbenchmark is insufficient; it hides cold
  per-slot command-chain publication.
- The current detailed frame-data/reuse diagnostics identify broad
  `ResourcePlan` invalidation but do not yet expose the exact resource identity
  responsible for every false invalidation.
- The local agent broker is outside the renderer runtime path. Its bounded,
  read-only evidence run completed within its configured budget; no broker
  budgeting change is indicated by this regression.

## Phase 5 Wrap-Up State

| Criterion | State at wrap-up |
| --- | --- |
| One deadline-aware executable DAG for every output and publication | Open: planner output is not the sole submit/present executor; real uploads and some output phases still follow fixed paths. |
| Acquired OpenXR eye critical path and nonblocking secondary work | Open: acquisition was improved, but frame-slot/image retirement and secondary ImGui paths can still block an XR-owned frame. |
| Bounded cadence, deferral, budgets, and stale reuse | Open: policy/telemetry exists, but canonical identity, deadline/budget consumption, and terminal completion are not end-to-end for every output. |
| Narrow queue lock and timeline/frame-slot ownership | Open: native lock scopes were narrowed, but OpenXR still has synchronous fence/image ownership paths and the gateway audit is not fully closed. |
| Frozen modal-resize generations with WSI scaling and one catch-up | Complete; copied to the completed-work sibling. Live long-duration Win32 soak remains a validation task. |
| Bounded, nonblocking modal dispatch with typed terminal result | Open: typed outcomes and watchdog breadcrumbs exist, but the callback can still enter the normal full render dispatch and inherit blocking work. |
| Persistent safe-packet workers with serial fallback | Complete; copied to the completed-work sibling. Deterministic timeout fault injection remains a validation task. |

## Validation Evidence

- `dotnet build XREngine.Runtime.Rendering.Vulkan/XREngine.Runtime.Rendering.Vulkan.csproj --no-restore --disable-build-servers`
  passed with zero warnings and zero errors during implementation.
- The latest isolated session was `vulkan-phase5-request-scope`; logs are under
  `Build/_AgentValidation/00000000-000000-shared/mcp-sessions/20260812-155638-vulkan-phase5-request-scope/logs/`.
- A coherent close-wall readback is
  `Build/_AgentValidation/20260812-122100-vulkan-phase5/mcp-captures/Screenshot_20260812_165816_121_905d99af43ee4e40a6e66e82bc98c498.png`.
- The latest sampled path had zero validation errors, zero deferred frames, and
  zero missing dynamic overlays. It still had 200+ ms CPU-stage spikes.
- RenderDoc tooling passed `rdc doctor`. A GPU capture was not required to
  localize the observed freeze because queue/GPU-facing time stayed small while
  CPU-stage telemetry isolated preparation and recording.

## Next Steps

1. Extend command-chain invalidation diagnostics with the exact changed
   resource identity/signature, then eliminate the false/broad `ResourcePlan`
   invalidation on stable post-process chains.
2. Make compatible command-chain artifacts reusable across frame slots, or
   incrementally materialize each slot under an explicit CPU deadline. Never
   rebuild hundreds of secondaries synchronously on camera motion.
3. Preserve the last complete scene plus the current dynamic overlay whenever a
   cold replacement topology misses its budget.
4. Make one frozen per-frame scheduling manifest the sole authority for output
   admission, ordering, submit, present, and terminal completion. Carry one
   canonical output ID and real deadline from the host through Vulkan/OpenXR.
5. Finish XR-owned frame-slot/image/ImGui nonblocking behavior and the remaining
   main-device queue/device-idle gateway audit.
6. Split modal callback publication from full render dispatch so the callback
   returns a typed stale/defer result within a fixed budget; then run the Win32
   drag-duration and guard-liveness soak.
7. Re-run a deterministic Sponza traversal after the invalidation fix: capture
   at least ten warmed samples plus cold-view transitions, verify visual camera
   movement, native-overlay continuity, profiler stages, and Vulkan logs.

## Validation Boundary

No tests were added or modified while the integration and regression remain
under live validation, per repository policy. After the live Sponza path is
stable and the user clears test work, add focused output-DAG, modal-resize,
worker-timeout, and OpenXR scheduling coverage and run the companion hardware
and stress matrix.
