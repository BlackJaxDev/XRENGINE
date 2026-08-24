# Vulkan Resident Draw Stream Phase 2 — Canonical Publication

Date: 2026-08-23  
Status: Publication infrastructure implemented; CPU-direct Vulkan validation passed; material-payload and broader strategy parity pending  
Tracker: [Vulkan resident draw stream and render task pool](../../todo/rendering/optimization/vulkan-resident-draw-stream-and-render-task-pool-todo.md)

## Problem statement

Finish the two open Phase 1 exit gates, then implement the Phase 2 managed
publication layer without introducing a second renderer-neutral allocator or
moving Vulkan template ownership ahead of Phase 3.

## Issues found

- Retained Vulkan command-chain/compiler, OpenXR, and JobManager background
  capacities were omitted from the resolved execution topology.
- Scheduler allocation evidence covered only the warmed one-item inline path.
- `AdvancedGpuRecordTable<T>.Remove` recycled a logical slot immediately and
  its dirty/remap state was destructive single-consumer state.
- `AdvancedPreparationExtractor` fabricated draw/geometry/material handles from
  legacy dense IDs instead of consuming the canonical database.
- `AdvancedSharedGpuSceneDatabase` was not used by the production `GPUScene`
  publication path.
- `BackendReadyFramePackage` carried only live-object compatibility selections,
  with no canonical scene/frame/view/pass/strategy/delta contract.
- CPU-direct submission stopped maintaining the legacy `GPUScene` mirror, so
  the first live publication contained zero resident records despite a healthy
  rendered scene.

## Implemented solution

- The execution topology reserves and names retained legacy thread capacities.
- Render scheduler metrics measure build, dispatch, execute, and merge managed
  allocation deltas, including per-lane execute bytes.
- Canonical record tables publish bounded exact journals, preserve tombstoned
  payloads until cumulative consumer acknowledgement and package/GPU pin
  retirement, and delay generation increment and slot reuse until reclamation.
- The shared database owns an epoch-tagged ordered publication ring, consumer
  tokens, cumulative acknowledgements, and package/GPU leases.
- `GPUScene.SwapCommandBuffers` dual-publishes all resident source primitives
  into `AdvancedSharedGpuSceneDatabase`; registrations retain table-allocated
  handles and never manufacture logical IDs.
- The advanced preparation extractor consumes the canonical draw, geometry,
  and material handles.
- Backend-ready packages publish canonical scene/frame/view/pass records,
  requested/resolved strategy, owner dirty ranges, diagnostic requests,
  compact CPU-visible records, ordered exceptions, and structural template
  projection deltas. Legacy mesh selections remain a temporary parity sidecar.
- CPU-direct keeps the legacy `GPUScene` mirror active so every resolved
  submission strategy feeds the same canonical publisher. It still draws from
  the CPU tree; the mirror exists only for publication parity.

The implementation deliberately does not claim complete material dual-feed
parity yet. Canonical material headers preserve legacy layout/shader/value/
resource revisions, and canonical geometry rows preserve the exact legacy atlas
offsets. The remaining material work is to publish the real packed constant
words, texture/sampler bindings, material-layout rows, and shading-kernel rows
through `AdvancedMaterialDatabase`. Advanced geometry buffer references also
remain pending for Phase 3, so the current rows do not falsely report advanced
residency.

## Validation evidence

| Check | Result | Evidence |
| --- | --- | --- |
| Render runtime Release build | Pass | `dotnet build XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj -c Release --no-restore`; 0 warnings, 0 errors. |
| Editor Release build | Pass | `dotnet build XREngine.Editor/XREngine.Editor.csproj -c Release --no-restore`; 0 warnings, 0 errors. |
| RenderDoc environment | Pass | `rdc doctor`; RenderDoc 1.44, Vulkan layer, replay, and CLI checks passed. |
| Live Vulkan editor | Pass | Named isolated `resident-phase2-diagnostics` session; explicit active-camera captures at `(100,50,100)` and `(0,5,20)` were visually inspected and materially different. |
| Static publication | Pass | At frame 796: 127 draws/instances/geometry/materials; topology/content generation 127; zero topology/content deltas; zero dirty owners; publication accepted. |
| Camera-only publication | Pass | At frame 2345 after moving the camera: resident count 127 and topology/content generation 127 remained unchanged; zero template projection deltas. |
| Add/remove publication | Pass for exercised operations | Final hardened run added a probe at topology generation 128, removed it at 129, and re-added it at 130. The resident count returned from 128 to 127 between additions and no publication was rejected. |
| ABA-safe reclamation | Pass | The reclaimed draw slot reused logical index 128 only after acknowledgement and advanced from generation 1 to generation 2 in the final hardened run. |
| Publication retention | Pass | After removing the package's unnecessary long-lived lease, the publication ring advanced beyond sequence 2,000 with `MinAckSequence` and `MinReclaimableSequence` tracking the current sequence instead of stalling at ring capacity. |
| Vulkan diagnostics | Pass with unrelated warnings | The final PID emitted no VUID, device-loss, fatal, unhandled, or publication-rejection message. Deleting the probe produced an existing MemoryPack warning for `SceneNodeDestroyAssetReference`; older failed iterations remain in the same session log root and are not final-run evidence. |
| Remaining Phase 2 implementation/evidence | Pending | Real packed material constants/textures/layout/kernel publication; reparent/material/mesh mutation matrix; GPU-indirect/available-meshlet dual-publication parity. |

The first live attempt exposed the missing CPU-direct mirror: rendering was
healthy but all canonical resident counts were zero. After enabling the mirror
for every submission strategy, the publication stabilized at 127 records. A
later add/remove/re-add sequence verified bounded topology changes and delayed,
generation-safe slot reuse. A retained package lease then exposed a bounded-ring
stall during a later run; packages now copy the immutable publication data they
need and release that lease immediately, while future Phase 3 GPU consumers must
take their own GPU lease. The hardened run subsequently advanced normally.
MCP screenshots and log extracts are retained under
`Build/_AgentValidation/20260823-202152-resident-stream-phase2/` as disposable
supporting evidence.

No tests were added or modified. Repository policy requires live feature
validation and explicit user clearance before Phase 2 test work begins.

## User-reported result

No user validation report has been received yet.
