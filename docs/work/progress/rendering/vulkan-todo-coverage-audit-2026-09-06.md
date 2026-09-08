# Vulkan Rendering TODO Coverage Audit — 2026-09-06

Baseline: `8b104bf7a` plus the September 6 documentation rewrite.

This audit covers every Markdown file in `architectural-refactor`, `optimization`,
`gpu`, and `vr`, plus the related top-level rendering trackers listed below.
It compares requirements and dated completion evidence against the
[master](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md),
[XR/Advanced execution checklist](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md),
and [completed foundation record](vulkan-phases-0-5-completed.md).

This is a source/document audit, not new runtime acceptance. No build, test,
editor, GPU capture or feature implementation was run. Existing tests were
located as contract evidence, not executed or treated as new passes. The initial audit retained source paths. The subsequent user-requested cleanup
moved completed records and removed superseded TODOs, updating their inbound
links and retaining unique evidence/contract details below and in the master. This audit is a disposition record, not
another checkbox list.

## Findings and changes

- Four documents already record scoped completion: architectural phases 01/02
  and the two meshlet cooking/closeout documents. The meshlet external cache
  condition and all later integration/production gates remain open in their owners.
- Four optimization documents already identify themselves as superseded or
  historical: Deferred+, visibility-buffer rendering, clean baseline profiles,
  and the profiler-counter audit. Their old unchecked rows are not active work.
- The OpenXR fence-wait proposal is fully covered by the new XR checklist.
  Its proposed status and stale source description are now explicitly historical.
- Resident/command/frame-loop and core-hardening/Present-Now ledgers contained
  obsolete execution ownership. Their headers now point to current tasks while
  retaining original details as provenance.
- Architecture documents 03–05 contained missing visibility, deformation and
  reconstruction acceptance detail. Added ARP-A07/A08 and ARP-V51–V60; expanded
  ARP-V17's fixture coverage. No implementation completion was inferred for
  early/late GPU command execution.
- Recovered master RC-A01–A05 and RC-V01–V10: background resume, shadow starvation,
  terminal-failure behavior, worker ownership, diagnostic/context provenance,
  structural/unsafe/layout evidence, resident allocation and inheritance matrices,
  and the exact command-recording cohort budgets. The original 29 F rows retain
  their IDs; CPU-indirect evidence now explicitly includes stable-bin and draw-range
  equivalence.
- Added a master child-owner table so specialized work remains discoverable
  without duplicating its checkboxes. Corrected stale resource-lifecycle cache-
  command and query startup-blocker summaries; those children still require
  their own runtime evidence.

## Architectural phase records

| Document | Disposition | Evidence boundary / remaining owner |
|---|---|---|
| [00-advanced-render-pipeline-refactor-todo.md](../../todo/rendering/architectural-refactor/00-advanced-render-pipeline-refactor-todo.md) | Index / consolidated | Keep architectural invariants and historical order; current execution is the master and XR/Advanced. Corrected obsolete hardening-only ownership. |
| [01-pipeline-identity-and-frame-contract-todo.md](../../todo/COMPLETED/01-pipeline-identity-and-frame-contract-todo.md) | Completed contract record | Identity, capability, frame-stage and resource contracts recorded complete. Not full native shading or production acceptance. |
| [02-gpu-scene-and-material-data-contract-todo.md](../../todo/COMPLETED/02-gpu-scene-and-material-data-contract-todo.md) | Completed contract record | Canonical GPU scene/material publication contracts recorded complete. Material families, classifier semantics and GPU parity still need their active V rows. |
| `03-gpu-visibility-preparation-and-deformation-todo.md` (removed; [current owner](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md#visibility-and-reconstruction-acceptance-carried-from-architecture-documents-0305)) | Consolidated acceptance | Implementation contracts exist. Vulkan/OpenGL deformation output and barrier proof moved to ARP-V51/V52. |
| `04-visibility-buffer-resources-and-geometry-todo.md` (removed; [current owner](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md#visibility-and-reconstruction-acceptance-carried-from-architecture-documents-0305)) | Consolidated audit + acceptance | Actual early/late execution must be audited, not inferred from contracts. ARP-A07 and ARP-V53–V55 own producer/attachment parity and same-frame disocclusion. |
| `05-attribute-reconstruction-todo.md` (removed; [current owner](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md#visibility-and-reconstruction-acceptance-carried-from-architecture-documents-0305)) | Consolidated acceptance | Decode/interpolation/derivative contracts exist. ARP-A08, ARP-V56–V60 and expanded ARP-V17 own shared kernels, raster reference, LOD, isolated timing, captures and motion fixtures. |

## Optimization documents

| Document | Disposition | Evidence boundary / remaining owner |
|---|---|---|
| [compact-zero-readback-rendering-todo.md](../../todo/rendering/optimization/compact-zero-readback-rendering-todo.md) | Retain child | Active-list/Hi-Z/overflow/barrier and delayed-diagnostic details remain specialized work; broad master zero-readback gates do not certify them. |
| [cpu-direct-fast-path-todo.md](../../todo/rendering/optimization/cpu-direct-fast-path-todo.md) | Retain child | Backend-neutral constant/upload/state-cache work, mapped OpenGL rings, dirty ranges and warmup exceed Vulkan-only coverage. |
| [default-pipeline-gpu-hotspots-todo.md](../../todo/rendering/optimization/default-pipeline-gpu-hotspots-todo.md) | Retain child | Default-pipeline GPU baselines, quality scaling, effect-skip correctness and AO resource/cost evidence remain distinct from Advanced acceptance. |
| `deferred-plus-render-path-todo.md` (removed; [current owner](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md#phase-7)) | Already superseded | Header explicitly names Advanced as successor. Historical design only; do not resurrect the Deferred+ proposal. |
| [editor-profiler-ui-render-cost-todo.md](../../todo/rendering/optimization/editor-profiler-ui-render-cost-todo.md) | Retain child | Visible/hidden panels, ProcessLatestData, virtualization and overlay observer-cost checks are not closed by generic profiling counters. |
| [engine-rendering-optimization-roadmap.md](../../todo/rendering/optimization/engine-rendering-optimization-roadmap.md) | Retain index; status corrected | Old WS06-blocked ordering predates later implementation. Master now owns execution; cross-backend children and 5.00 ms desktop / 8.33 ms RVC local gates remain. |
| [material-table-and-texture-binding-ladder-todo.md](../../todo/rendering/optimization/material-table-and-texture-binding-ladder-todo.md) | Retain child | Cross-backend binding tiers, pass layouts, override identity, dirty publication and prewarm remain; do not fold sparse/virtual feature proposals into Phase 7. |
| `rendering-clean-performance-baseline-profile-contract-todo.md` (removed; [current owner](../../investigations/rendering/archive/vulkan-framerate-root-cause-2026-07-28.md)) | Already superseded | Explicitly historical design input to completed WS01. Named profiles/manifests/observer overhead are not a second open implementation checklist. |
| `rendering-profiler-counter-audit.md` (removed; [current owner](vulkan-todo-coverage-audit-2026-09-06.md#retained-profiler-audit-inputs)) | Historical audit | Explicit pre-v2 inventory. Broader GL/assets/shader-cache/streaming/VR gaps are audit inputs, not automatically new Vulkan implementation tasks. |
| `visibility-buffer-rendering-todo.md` (removed; [current owner](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md#phase-7)) | Already superseded | Explicitly folded into Deferred+/Advanced. Traditional-producer bring-up, identity, tangent/LOD and per-eye diagnostics are retained by current ARP contracts and recovered tasks. |
| [vr-rendering-performance-contract-todo.md](../../todo/rendering/optimization/vr-rendering-performance-contract-todo.md) | Retain child | Runtime/backend accounting, 72/90/120 Hz budgets, VRS/foveation/reprojection and whole-frame output costs exceed the submit-only XR checklist. |
| `vulkan-command-recording-architecture-optimization-todo.md` (removed; [current owner](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#recovered-acceptance-contracts-and-specialized-child-ownership)) | Historical ledger; acceptance mapped | Foundation Phase 4/4.5 records current implementation. RC-V07–V09 retain inheritance/lifetime and exact stable/moving cohort budgets; do not rebuild prepared encoding. |
| [vulkan-headless-mcp-component-profiling-todo.md](../../todo/rendering/optimization/vulkan-headless-mcp-component-profiling-todo.md) | Retain separate tool owner | Presentationless/component/headless-WSI and MCP observer/correlation infrastructure are not implied by renderer performance acceptance. |
| [vulkan-managed-vma-concepts-allocator-todo.md](../../todo/rendering/optimization/vulkan-managed-vma-concepts-allocator-todo.md) | Excluded independent allocator work | Separate substantial allocator evolution (selection/alignment/mapping/device-address/budget/defrag); not imported into this consolidation. |
| `vulkan-resident-draw-stream-and-render-task-pool-todo.md` (removed; [current owner](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#recovered-acceptance-contracts-and-specialized-child-ownership)) | Historical ledger; acceptance mapped | Foundation Phase 3/4 supersedes early infrastructure/payload snapshots. RC-V06 and F3-01/F3-03 retain exact allocation-matrix and stable-bin/draw-range parity proof. |
| `xrengine-vulkan-frame-loop-stability-todo.md` (removed; [current owner](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md)) | Consolidated ledger | F0/F1/F3/F5 and Phase 8 own current transaction, attribution, strategy, streaming, lifecycle and release-continuity acceptance; old implementation status is historical. |

## GPU documents

| Document | Disposition | Evidence boundary / remaining owner |
|---|---|---|
| [blendshape-compression-and-gpu-efficiency-todo.md](../../todo/rendering/gpu/blendshape-compression-and-gpu-efficiency-todo.md) | Excluded separate deformation work | Compression/precision/dispatch validation belongs to the blendshape owner; broad Advanced skinned-output checks do not close it. |
| [blendshape-update-classes-and-static-baking-todo.md](../../todo/rendering/gpu/blendshape-update-classes-and-static-baking-todo.md) | Excluded new feature | Independent draft update-class/static-baking proposal. |
| [gpu-driven-animation-todo.md](../../todo/rendering/gpu/gpu-driven-animation-todo.md) | Excluded new feature | Independent GPU animation proposal; no Vulkan tracker migration. |
| [gpu-driven-occlusion-culling-architecture-todo.md](../../todo/rendering/gpu/gpu-driven-occlusion-culling-architecture-todo.md) | Retain child | Persistent visibility, two-phase Hi-Z, BVH, meshlet/stereo and settings/validation requirements remain broader than Advanced visibility acceptance. |
| [gpu-softbody-mesh-rigging-todo.md](../../todo/rendering/gpu/gpu-softbody-mesh-rigging-todo.md) | Excluded new feature | Independent softbody/rigging development; no completion determination made for its runtime claims. |
| [meshlet-import-cooking-and-production-readiness-todo.md](../../todo/COMPLETED/meshlet-import-cooking-and-production-readiness-todo.md) | Completed scoped record | August 22 unconditional production gates accepted; broad model-cache hydration remains an explicit external condition, not newly closed here. |
| [meshlet-production-closeout-work-guide.md](../../todo/COMPLETED/meshlet-production-closeout-work-guide.md) | Completed scoped record | August 22 Gates 1–7 accepted. Keep as closeout evidence; do not create duplicate implementation todos. |
| [openvr-vrclient-gpu-handoff-todo.md](../../todo/rendering/gpu/openvr-vrclient-gpu-handoff-todo.md) | Excluded new integration | Independent cross-process GPU handoff proposal. |
| [production-rendering-pipeline-roadmap.md](../../todo/rendering/gpu/production-rendering-pipeline-roadmap.md) | Retain roadmap | Broader residency/LOD/streaming/culling/cross-backend hardening remains. Meshlet completion and device-address capability setup do not certify all consumers or performance promotion. |
| [skinning-gpu-efficiency-followups-todo.md](../../todo/rendering/gpu/skinning-gpu-efficiency-followups-todo.md) | Retain skinning owner | Baseline, mixed precision and dispatch reuse are specialized work, separate from Advanced deformation parity. |

## VR documents

| Document | Disposition | Evidence boundary / remaining owner |
|---|---|---|
| [openxr-future-work-todo.md](../../todo/rendering/vr/openxr-future-work-todo.md) | Retain future-work owner | Timing follow-ups and optional compositor/pacing extensions remain outside current submission repair scope. |
| [openxr-monado-ci-hardware-followups-todo.md](../../todo/rendering/vr/openxr-monado-ci-hardware-followups-todo.md) | Retain child | CI promotion/ownership, deterministic pose/fault controls and SteamVR/Meta GL/Vulkan hardware matrix are not proved by Monado SPS submissions. |
| [openxr-runtime-code-organization-todo.md](../../todo/rendering/vr/openxr-runtime-code-organization-todo.md) | Excluded major refactor | Independent proposed OpenXR architecture reorganization; not pulled into current TODOs. |
| [openxr-steamvr-openvr-parity-todo.md](../../todo/rendering/vr/openxr-steamvr-openvr-parity-todo.md) | Implementation recorded; acceptance open | Source integration is recorded, hardware validation is pending, and hand-skeleton support is explicitly absent. Not a wholly completed VR feature. |
| `openxr-vulkan-submit-fence-wait-todo.md` (removed; [current owner](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md#phase-6)) | Superseded in this audit | All tracker/lifetime/runtime/timing obligations map to existing XR-I/A/V tasks. Retains July 1 73 ms average / 96.9 ms worst profiler evidence only. |
| [retinal-visibility-cache-debug-views-todo.md](../../todo/rendering/vr/retinal-visibility-cache-debug-views-todo.md) | Excluded independent tooling feature | Separate RVC diagnostic feature proposal; generic ARP diagnostics do not implement it. |
| [vr-mirror-cyclopean-reconstruction-todo.md](../../todo/rendering/vr/vr-mirror-cyclopean-reconstruction-todo.md) | Excluded new feature | Independent cyclopean mirror reconstruction proposal. |

## Related top-level trackers

| Document | Disposition / missing scope |
|---|---|
| [Core hardening](../../todo/rendering/vulkan-core-hardening-and-device-loss-todo.md) | Supporting ledger; old sole-owner claim corrected. RC tasks recover sections 7/8/14.4; section 9 retains detailed occlusion-policy ownership. |
| [Core hardening completed record](../../todo/COMPLETED/vulkan-core-hardening-and-device-loss-completed.md) | Existing historical evidence; not a source of new unfinished implementation merely because narratives mention old blockers. |
| [Present-Now readiness](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#foundation-carryovers) | Historical accepted desktop checkpoint; F0/RC and Phase 8/9 own remaining acceptance and failure injection. |
| [Pipeline resource lifecycle](../../todo/rendering/render-pipeline-resource-lifecycle-todo.md) | Phases 0–5 recorded complete and cache commands removed; cross-backend atomic commit, failed/superseded generation cleanup, imported-target and resize acceptance remain. Older “Current Gaps” heading corrected. |
| [Render-query upgrade](../../todo/rendering/vulkan-render-query-system-upgrade-todo.md) | Query implementation recorded complete; query-specific runtime/stereo/timestamp/provider gates open. July loader failure is historical, not a proven current blocker. |
| [CPU query camera motion](../../todo/rendering/cpu-async-query-camera-motion-todo.md) | Core motion regression has dated passing cases; edge/cut/projection/hierarchy/physical-eye correctness remains specialized work. |
| [Software occlusion](../../todo/rendering/masked-software-occlusion-culling-todo.md) | Scalar implementation/hardening present; viewport visualization, real-scene correctness and optional measured SIMD remain separate. |
| [Physics debug performance](../../todo/rendering/physics-debug-visualization-performance-todo.md) | Packed-frame/batch implementation recorded complete; 1.10x median/1.20x p95 baseline comparison, zero allocation, three-draw limit and GL/Vulkan visual acceptance were deferred. Not fully accepted. |
| [Deferred/probe fixes](../../todo/rendering/vulkan-deferred-and-probe-gi-fixes-todo.md) | Dated fixes exist; original-pipeline GBuffer/probe-image/descriptor/OpenGL comparison remains distinct from Advanced GI acceptance. |
| [Atmospheric scattering](../../todo/rendering/atmospheric-scattering-component-todo.md) | OpenGL mono implementation recorded; visual/profiler/stereo/platform acceptance and optional quality features remain. ARP-V30 alone cannot close them. |
| [Resolved shader-source optimization](../../todo/rendering/resolved-shader-source-optimization-todo.md) | Partial generic optimizer exists; compile-path coverage, static identity, conservative scanning/pruning and diagnostics remain backend-neutral work. |
| [Dynamic rendering/backend completion](../../todo/rendering/vulkan-dynamic-rendering-migration-todo.md) | Dynamic rendering is already promoted, but the document deliberately bundles optional local-read/heap/shader-object/DGC/ray-tracing feature work. Retain separately; do not infer that all modern-backend features are complete or import them into Phase 7. |
| [Default DoF upgrade](../../todo/rendering/default-pipeline-depth-of-field-todo.md) | Independent signed-CoC/half-resolution near/far redesign. Excluded; validating current Advanced DoF does not authorize this upgrade. |

## Explicit exclusions

Fossilize integration and ReSTIR radiance-cache GI were excluded as requested.
Other independent feature/refactor proposals are labeled above and were not
consolidated. The `global-illumination` and `shadows` subfolders were not audited
for feature completion in this pass. Their broad feature roadmaps remain intact;
master shadow/GI integration gates do not replace them.

## Bounded source and evidence checks

- OpenXR replacement plumbing exists in
  `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/`:
  `OpenXrVulkanSubmissionTracker`, `VulkanCommandRuntime.OpenXrSubmission`,
  `VulkanFrameLoop.OpenXR.EyeRendering`, `VulkanFrameLoop.OpenXR.EyeRecordWorkers`
  and `VulkanXrGraphicsBinding` use reservation, registration, polling and retirement.
  This supports superseding the old implementation proposal, not closing XR V rows.
- Advanced identity/frame/GPU-scene/deformation/visibility/reconstruction contracts
  are present under `XREngine.Runtime.Rendering/Rendering/`. Located record and
  decoder/contract tests support structure only. The July phase progress links
  remain in documents 01–03; no new shader or runtime result was claimed.
- Cooked meshlet ownership persists in `XRMesh.Meshlets.cs`, `XRMesh.CookedMeshlets.cs`
  and `XRMesh.CookedBinary.cs`; `GPURenderExpandMeshlets.comp` and the
  `GpuMeshletTaskRecord` layout remain. The scoped completion determination uses
  the existing August 22 gate/evidence records, not source presence alone.
- `RenderResourceGeneration` and `VulkanResourceGenerationTransactionService`
  exist, including pending validation/commit/retirement paths. The lifecycle doc's
  own phase 3–5 closeout supersedes its earlier cache-command inventory.
- `XRRenderQuery`, `VkRenderQuery`, typed descriptors/tickets/slot state machines,
  and timestamp helpers exist. Later Vulkan submission evidence disproves only
  a blanket current startup-blocked assumption, not a query-specific failure.
- `PhysicsDebugFrameRenderer` exists; the physics document explicitly defers its
  current benchmark/live matrix. `ResolvedShaderSourceOptimizer` still includes
  regex-based interface/resource/root processing, so generic compiler-path and
  conservative-pruning audit work must not be declared complete.

The local broker's evidence-only comparison completed with requested and actual
model `gpt-5.6-luna` (run `65d60e226e0340d98f50c4dd3867ad10`). It received only
five tracker snapshots, had no local/editor tools and performed no mutation.
Its proposed omissions were checked against the current text before adding RC
owners. Native read-only audits covered the four subfolders; the coordinator
integrated changes and checked links, IDs and requirement preservation.

## File cleanup after the audit

At the user’s request, five scoped completion records moved to
`docs/work/todo/COMPLETED/` and thirteen superseded TODOs were removed. These
moves do not certify any additional runtime or feature acceptance. Incomplete
specialized children, the architectural index, and independent proposals remain.
The completed foundation record stays in `docs/work/progress/rendering/` because
it is a progress/evidence document rather than an active TODO.

Deleted historical source is recoverable from Git revision `8b104bf7a` using
its original repository path. Current execution follows the replacements below;
obsolete branch instructions and unchecked implementation rows must not be resumed.

| Removed TODO path (relative to docs/work/todo) | Current owner |
|---|---|
| `rendering/architectural-refactor/03-gpu-visibility-preparation-and-deformation-todo.md` | [Current owner](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md#visibility-and-reconstruction-acceptance-carried-from-architecture-documents-0305) |
| `rendering/architectural-refactor/04-visibility-buffer-resources-and-geometry-todo.md` | [Current owner](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md#visibility-and-reconstruction-acceptance-carried-from-architecture-documents-0305) |
| `rendering/architectural-refactor/05-attribute-reconstruction-todo.md` | [Current owner](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md#visibility-and-reconstruction-acceptance-carried-from-architecture-documents-0305) |
| `rendering/optimization/deferred-plus-render-path-todo.md` | [Current owner](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md#phase-7) |
| `rendering/optimization/visibility-buffer-rendering-todo.md` | [Current owner](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md#phase-7) |
| `rendering/optimization/rendering-clean-performance-baseline-profile-contract-todo.md` | [Current owner](../../investigations/rendering/archive/vulkan-framerate-root-cause-2026-07-28.md) |
| `rendering/optimization/rendering-profiler-counter-audit.md` | [Current owner](vulkan-todo-coverage-audit-2026-09-06.md#retained-profiler-audit-inputs) |
| `rendering/optimization/vulkan-command-recording-architecture-optimization-todo.md` | [Current owner](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#recovered-acceptance-contracts-and-specialized-child-ownership) |
| `rendering/optimization/vulkan-resident-draw-stream-and-render-task-pool-todo.md` | [Current owner](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#recovered-acceptance-contracts-and-specialized-child-ownership) |
| `rendering/optimization/xrengine-vulkan-frame-loop-stability-todo.md` | [Current owner](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md) |
| `rendering/vulkan-present-now-frame-readiness-todo.md` | [Current owner](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md#foundation-carryovers) |
| `rendering/vr/openxr-vulkan-submit-fence-wait-todo.md` | [Current owner](../../todo/rendering/vulkan-xr-and-advanced-rendering-todo.md#phase-6) |
| `xrengine-vulkan-frame-loop-stability-todo.md` | [Current owner](../../todo/rendering/vulkan-core-frame-loop-and-resident-rendering-master-todo.md) |

### Retained profiler audit inputs

The following is the July 28 pre-v2 audit snapshot. These are remaining audit
inputs, not assertions that current source lacks every listed counter. Use the
[observer-cost child](../../todo/rendering/optimization/editor-profiler-ui-render-cost-todo.md),
[GPU roadmap](../../todo/rendering/gpu/production-rendering-pipeline-roadmap.md),
and [VR performance contract](../../todo/rendering/optimization/vr-rendering-performance-contract-todo.md)
to assign any confirmed gaps before claiming full measurement coverage.

- OpenGL renderer state churn was mostly invisible: no shader program switch, pipeline switch, VAO bind/skip, buffer target bind, texture bind/skip, active texture unit switch, uniform/sampler call, barrier-kind, indirect-count draw, redundant-state skip, active texture-binding rung, or strategy/pass split fields in v1 capture.
- Scene and asset attribution was too coarse: a high-material or skinned avatar could collapse FPS without a per-frame visible renderer/submesh/triangle/material/texture/skinning row that identifies the asset or cooked representation.
- Shader cache state was partially observable through logs but not exposed as requested/warming/linked/failed/disk-cache/generated counters in profile captures.
- Texture streaming/upload cost existed in texture diagnostics but was not surfaced as frame-level upload jobs, upload bytes, upload time, or resident texture memory in the render capture.
- GPU-driven OpenGL zero-readback paths did not expose active bucket work, empty-bucket skips, full scans, material scatter dispatches, compacted draw-count diagnostics, compaction overflow kinds, Hi-Z one/two-phase mode, or visibility-buffer counters in the generic capture.
- Delayed diagnostic readbacks were not explicitly marked as delayed diagnostics in the NDJSON schema, making zero-readback proof ambiguous.
- GPU timestamp instrumentation exposed timing readiness and pass timings, but not query count, query readback bytes, or whether dense diagnostic mode was enabled.
- VR captures could be mistaken for desktop captures because the manifest did not carry target refresh, frame budget, stereo mode, per-eye timing availability, VRS state, validation/debug warnings, or motion-vector coverage.
- Benchmark manifests omitted several reproducibility variables: backend/GPU/driver, scene label, camera, lights, viewport/render scale, cache mode, shader/texture cache mode, GPU clock policy, validation/debug state, and invalid-environment diagnostics.
- Environment override validation was split across ad hoc scripts and permissive runtime parsers; invalid enum values could be silently ignored in engine launch captures.

### Retained fence-wait baseline

The removed July 1 OpenXR proposal reported 128 FPS-drop records at
`OpenXR.Vulkan.SubmitFenceWait`, approximately 73 ms average and 96.9 ms worst.
Its logs were under
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_12-38-22_pid42516/`
(`profiler-fps-drops.log` and `profiler-render-stalls.log`). This is historical
motivation for the implemented tracker; clean current XR runtime acceptance
remains in the active XR checklist.
