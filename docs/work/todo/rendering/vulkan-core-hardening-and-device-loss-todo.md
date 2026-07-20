# Vulkan Core Hardening And Device-Loss TODO

Last Updated: 2026-07-20
Owner: Rendering
Status: P0 Complete; Inherited Validation Debt And Phase 5.2+ Remain Open
Execution: Current worktree only; do not create or switch branches for this effort.

This file intentionally contains only open work and the active constraints needed
to execute it. Completed implementation history, dated handoffs, and durable
evidence are in the
[completed-work record](vulkan-core-hardening-and-device-loss-completed.md).

The former Vulkan frame-wide render-loop TODO has been merged into Phase 5.2 of
this tracker. Its architectural rationale remains in the
[Vulkan render-loop design](../../design/rendering/vulkan-render-loop-design.md);
this file is the sole execution checklist for that work.

## Goal

Make Vulkan robust and fast enough for normal editor, OpenXR, scene-capture,
light-probe, shadow, mirror, UI-preview, and diagnostic rendering without recurring
`VK_ERROR_DEVICE_LOST` failures caused by cross-context resource churn, stale
descriptors, unsafe resource retirement, or oversized GPU submissions.

This work is not about hiding Vulkan failures behind silent fallbacks. It should
make invalid states visible earlier, isolate independent frame operations, and
turn device-loss investigations into actionable diagnostics.

## Scope

- Vulkan renderer command submission, frame-op modeling, resource planning,
  descriptor binding, image-layout tracking, and resource lifetime.
- Canonical frame views, view-family and render-batch planning, shared
  visibility, render-graph dataflow, and deadline-aware multi-output scheduling.
- OpenXR Vulkan eye/mirror rendering, especially synchronization between XR
  swapchain work and auxiliary renderer work.
- Scene capture, light probes, reflection/GI probes, shadow captures, UI preview
  viewports, and diagnostic framebuffer captures.
- Tooling for validation layers, RenderDoc/Nsight/RGP captures, crash
  breadcrumbs, profiler counters, and log summaries.

## Non-Goals

- Do not replace Vulkan with OpenGL fallback behavior.
- Do not require DX12 work to land first.
- Do not rewrite every render pipeline before isolating the crash-prone paths.
- Do not accept a fix that only catches `VK_ERROR_DEVICE_LOST` after the GPU is
  already unrecoverable.
- Do not treat in-process logical-device recreation as part of the first
  hardening pass. First make loss containment deterministic, preserve diagnostic
  evidence, and exit or restart the renderer through an explicit operator policy.

## Evidence And Reproducibility Rules

- Treat the first observed failing Vulkan/OpenXR call as the detection point,
  not automatically as the root cause. Preserve validation messages, resource
  churn, allocation pressure, and submission history leading up to it.
- Every baseline and stress result must record:
  - engine commit and dirty-worktree state,
  - GPU vendor/device/driver and Vulkan API version,
  - Vulkan SDK and validation-layer versions,
  - enabled instance/device extensions and features,
  - OpenXR runtime name/version and active graphics requirements,
  - diagnostic preset and relevant environment/settings snapshot,
  - scene/settings hash, resolution, refresh rate, and run duration/frame count.
- Store durable summaries and exact reproduction commands in tracked work docs.
  Large captures and raw logs remain under the per-run
  `Build/_AgentValidation/<run>/` root.
- Keep a machine-readable result manifest for each validation run so two runs
  can be compared without scraping prose logs.

## Invariants

- Vulkan/OpenXR device loss must stop further GPU work immediately and report a
  precise reason, last submitted frame-op context, and known in-flight resource
  generations.
- Device-loss transition is first-writer-wins and thread-safe. Preserve the
  original failing API/result/context; later failures are secondary fallout.
- After confirmed device loss, no queue submit, wait, allocation, mapping,
  descriptor update, command recording, or resource-planner publication may
  begin. Only fault collection and best-effort teardown paths are permitted.
- Main viewport, OpenXR eyes, scene captures, probes, shadows, and UI previews
  must not accidentally mutate each other's resource-planner state.
- Explicit failures are preferred over silent CPU or lower-feature fallbacks.
- Descriptor image info must match the actual image view, sampler, and expected
  layout at the time commands are recorded and submitted.
- Resource destruction must be timeline/fence-safe. Destroying image views,
  framebuffers, descriptor references, buffers, or images still referenced by
  in-flight work is a bug.
- Long GPU work must be budgeted and sliced so Windows TDR and OpenXR frame
  timing are respected.
- Hot paths must avoid per-frame heap allocation, LINQ, captured closures,
  boxing, and string formatting except behind explicit diagnostics.
- Recorded command reuse must be validated from an immutable dependency
  signature that includes every structural, binding-identity, allocation, and
  publication generation consumed by the recording. A global safety boolean or
  diagnostic environment flag is not an acceptable production cache contract.
- Classify render changes as structural, binding-identity, or data-only.
  Data-only updates to a stable frame slot, buffer range, descriptor set, or
  indirect/count buffer must not invalidate unrelated recorded command ranges.
- Vulkan dynamic data uses bounded, frame-indexed upload/storage arenas and
  stable bindings. Ordinary camera, transform, material, skinning, debug-line,
  and GPU-generated count changes must update safe ranges rather than recreate
  exact-sized backing resources or rerecord static scene topology.
- Diagnostic features are capability-gated and additive. Unsupported standard
  or vendor extensions must be reported explicitly, not silently treated as
  successful diagnostic coverage.
- Build one immutable logical view set after OpenXR views are located. Use the
  same predicted display time for view state, the render snapshot, visibility,
  histories, and the submitted projection layer.
- Key temporal state by stable logical-view identity, not transient batch
  position or swapchain image index. Keep view eligibility, exact frustum
  visibility, exact occlusion visibility, and render-batch membership distinct.
- `CpuDirect` uses CPU scene/BVH visibility and never consumes GPU compute-cull
  output. GPU strategies request the GPU-scene BVH and must not hide an
  unavailable accelerated path behind a silent CPU fallback.
- Zero-readback strategies must not require CPU-visible candidate, bucket, draw,
  visibility, or count data in their steady-state path.
- GPU-driven strategies must expose stable pass-level dispatch, barrier, and
  indirect-draw topology after warmup. Changing GPU-resident visibility,
  commands, counts, or statistics is data mutation, not by itself a reason to
  rebuild or rerecord a primary command buffer.
- Desktop, secondary, capture, probe, and debug work must not delay required
  OpenXR submission.
- Transient physical-image aliasing remains disabled until actual
  semaphore-constrained execution intervals prove non-overlap.

## Completion Record

Completed phases, checked criteria, historical handoffs, measurements, and the
evidence index were moved to the
[completed-work record](vulkan-core-hardening-and-device-loss-completed.md).
Keep new completion detail there rather than growing this open-work list.

## P0 Closeout And Post-P0 Dispositions

The immediate desktop visibility, device-loss, and Vulkan recording gate closed
on 2026-07-18. Its implementation history, checked criteria, measurements, and
evidence now live in the
[completed-work record](vulkan-core-hardening-and-device-loss-completed.md).
Inherited validation debt and Phase 5.2+ may proceed; they are no longer blocked
on P0.

The closeout deliberately distinguishes production acceptance from diagnostic
and tooling follow-ups:

- The production GpuIndirectZeroReadback lane is visually present at the
  matched Sponza camera, changes across a second pose and motion sequence, keeps
  requested/consumed counts equal, and records zero readback, mapping, fallback,
  VUID, or stale-generation events.
- StandardValidation completed the 12-run static/moving and occlusion matrix.
  The final SyncValidation interaction run survived streaming, camera motion,
  resize, shader hot reload, and normal shutdown after the command-local
  retirement publication fix. A later 406-sample SyncValidation forced-record
  cohort also recorded zero VUIDs, readbacks, fallbacks, submission rejections,
  and stale collection reuse; its two workload hashes make it correctness
  evidence rather than a stable performance baseline.
- Forced primary recording allocation fell from approximately 888.5 KiB/frame
  to 322.7 KiB/frame by retaining command tracking capacity and removing
  transient framebuffer attachment/layout/signature collections. Cached stable
  production frames already reach zero record-path allocation. Eliminating the
  remaining forced diagnostic-record allocation belongs to the general
  Phase 5.2.5 recording architecture rather than the completed P0 stability
  gate.
- Parallel command-chain workers remain quarantined. Re-enabling them requires
  immutable planner/renderer recording snapshots and the Phase 5.2.5 parallel
  acceptance matrix; P0 accepts the validated serial/cached path.
- XRE_FORCE_CPU_INDIRECT_BUILD=1 now exposes the CPU-built diagnostic
  reference only for GpuIndirectInstrumented. The material-batched path
  rebuilds commands and material runs instead of submitting stale/empty data.
  Focused policy, lifetime, shader, and source-contract coverage passes 56/56.
  The complete three-way visual matrix remains a diagnostic follow-up; the
  CPU-built lane is not a production fallback for the accepted GPU path.
- Engine Vulkan Debug Utils regions are implemented and source-verified.
  Nsight Systems 2026.3.1 exported no Debug Utils marker table, while RenderDoc
  1.44/rdc-cli 0.5.6 timed out without serializing a Vulkan capture. P0 records
  that tooling disposition without claiming external marker visualization.

Post-P0 follow-ups retained in this active tracker:

- [ ] Reduce the remaining approximately 322.7 KiB/frame forced-primary
  diagnostic allocation to zero as part of Phase 5.2.5's allocation work.
- [ ] Replace the current hard-disabled primary-reuse safety quarantine with
  generation-complete dependency validation. The public setting or environment
  override must not be advertised as effective while a compile-time safety gate
  forces reuse off.
- [ ] Re-enable parallel command recording only after workers consume immutable
  recording snapshots and improve or preserve the repeated validation matrix.
- [ ] Capture an external Vulkan action/marker tree showing the named engine
  regions when a compatible Nsight or RenderDoc exporter is available.
- [ ] Complete the matched CpuDirect / CPU-built indirect / GPU-built indirect
  visual and counter matrix for motion, CPU-query occlusion, resize, and
  streaming as Phase 5.2 validation evidence.
## Inherited Validation Debt

These requirements were not closed by the implementation summarized in the
[completed-work record](vulkan-core-hardening-and-device-loss-completed.md) and
remain active. They must be reconciled with current evidence rather than
silently discarded.

- [ ] Record a Vulkan shadow-rendering baseline with the standard repro manifest.
- [ ] Record a Vulkan UI-preview-rendering baseline with the standard repro
  manifest.
- [ ] On hardware that advertises `VK_KHR_device_fault`, run one
  `CrashDiagnostics` launch and record whether KHR fault reports are returned.
- [ ] Measure a steady alternating capture/OpenXR-eye workload and prove that
  independent contexts do not churn the same physical resources except where
  sharing is explicit.
- [ ] Close the aggregate Phase 2.1 live gate: focused and broader Vulkan tests
  plus repeated OpenXR/probe stress are green, with no cross-context churn,
  engine-owned validation error, or device loss.
- [ ] Re-run the original rapid-resize plus probe-capture plus OpenXR-eye
  destruction stress and prove there are no engine-owned destroyed-in-use
  objects. Reconcile the old Phase 4 failure counts against the later clean
  Phase 5.1 lanes and keep any runtime-owned SteamVR exception bounded by exact
  versions, handles, IDs, and ownership evidence.
- [ ] A stable desktop scene reaches at least 99% clean command reuse after
  warmup; every remaining record is attributable to an intentional structural
  or volatile change.
- [ ] Camera motion and ordinary frame-data updates do not rerecord static scene
  command ranges.
- [ ] Reuse enabled and disabled produce validation-clean, visually equivalent
  output in explicit dynamic and legacy render-target modes.

## Phase 5.2 - Multi-Output Render Throughput Architecture Gate

Complete Phase 5.2 before Phase 6. The target data flow is:

`Immutable frame snapshot + logical view set -> output requests -> compatible view families/batches -> shared visibility -> cached plans/resources -> record or reuse -> deadline-ordered submit DAG`

This phase owns output-neutral scheduling, stable identity, command reuse,
targeted invalidation, local tracking, asynchronous retirement, the canonical
logical-view contract, frame-scoped visibility integration, graph dataflow, and
the multi-output performance contract. Related ownership remains with the
[desktop frame-loop decomposition todo](vulkan-desktop-frame-loop-decomposition-todo.md),
[dynamic-rendering migration todo](vulkan-dynamic-rendering-migration-todo.md),
the [GPU-driven occlusion TODO](gpu/gpu-driven-occlusion-culling-architecture-todo.md),
the [VR rendering performance contract](optimization/vr-rendering-performance-contract-todo.md),
and [primary command-recording fast-path TODO](optimization/vulkan-primary-command-recording-fast-path-todo.md).
Those efforts must consume this same frame-view/output/plan contract and must
not introduce a desktop-only scheduler, a second attachment identity model, or
another independently constructed runtime view set.

Phase 5.2 has three separately reported promotion gates:

- **Phase 5.2A - CpuDirect production gate.** This is the current blocking gate
  for beginning Phase 6 and owns the immediate Vulkan/OpenGL CPU-direct
  regression boundary.
- **Phase 5.2B - GPU indirect production gate.** This opens after the compact
  zero-readback and GPU visibility readiness checklists are complete. It owns
  instrumented-reference parity plus production zero-readback performance.
- **Phase 5.2C - GPU meshlet production gate.** This opens only on supported
  hardware after meshlet parity/readiness is complete.

Phase 6 may begin after Phase 5.2A passes while 5.2B/5.2C continue independently.
The entire Vulkan hardening tracker must not be marked complete until every
supported production lane is promoted, explicitly removed from the v1 contract,
or deferred by a recorded owner decision with replacement acceptance criteria.
An unsupported hardware lane reports `unsupported`; an unfinished lane reports
`not ready`, never a zero-cost or passing result.

### 5.2.5 - Make Render Plans And Resource Arenas Versioned And Nonblocking

- [ ] Make resource-generation key construction deterministic before versioning
  plans. `BuildResourceFeatureMaskForGenerationKey` must derive every
  feature-affecting setting from the explicit `XRRenderPipelineInstance` and
  `XRViewport` (or an immutable settings snapshot owned by them), never from
  ambient `RuntimeEngine.Rendering.State.CurrentRenderingPipeline` state.
  Refactor AO resolution accordingly so resize, internal-resolution, explicit
  invalidation, and per-frame checks observe the same enabled state and AO type.
- [ ] Route all resource-generation requests through the same feature snapshot
  and revision. Add a diagnostic assertion/counter when two requests for the
  same pipeline, output kind, extent, and settings revision produce different
  feature masks or structurally different layouts.
- [ ] Break the desktop resize failure loop observed in
  `xrengine_2026-07-16_09-14-14_pid17808`: the resize path resolved no camera and
  requested `features=0x20CE101`, while frame preparation resolved GTAO and
  requested `features=0x220CE101`. The alternating `ViewportResized` and
  `FrameProfileChanged` requests continually superseded the pending generation,
  left the active desktop generation at `1920x1080` after the window reached
  `2560x1369`, and skipped desktop command execution while the independent
  OpenXR eye generation continued submitting with `deviceLost=False`.
- [ ] Decouple swapchain convergence from managed render-resource catch-up. Once
  the live and swapchain extents agree, a pending pipeline generation must not
  repeatedly recreate the same swapchain or grow retired external-image state;
  keep presentation fail-closed until the new generation commits, with bounded
  last-completed-content reuse and explicit progress telemetry.
- [ ] Redefine planner/cache identity around physical compatibility: output kind,
  view-family/pipeline identity, extent, pass graph, attachment signature,
  resource-plan generation, and queue family. Do not key the compiled plan on a
  rotating external swapchain/OpenXR image handle.
- [ ] Bind the current external target slot as a bounded plan/command variant;
  keep plan identity stable across acquisition rotation.
- [ ] Store immutable compiled plan generations. A replacement publishes a new
  generation while old plans, allocators, descriptors, and resources remain
  alive behind their last submitted timeline/retirement ticket.
- [ ] Remove `WaitForAllInFlightWork` and force-flush calls from command
  recording, planner-state prune, physical-plan replacement, imported-texture
  replacement, and normal cache eviction.
- [ ] Evict planner states incrementally only after their last-use timeline is
  complete. Cache capacity pressure must defer/retire work, not globally drain
  the device.
- [ ] Give each concurrently active output family a bounded persistent resource
  arena and candidate transient-lifetime plan. Reuse compatible allocations
  across frames, but keep physical aliasing disabled until scheduled lifetimes
  and semaphore-constrained execution prove non-overlap.
- [ ] Separate runtime-owned external images from engine-owned allocation and
  retirement. External image rotation must not churn engine resource plans.
- [ ] Add plan-cache hit/miss, plan-generation, arena high-water, alias reuse,
  pending-retirement bytes, and eviction-defer telemetry by output family.

Command-recording dependency and reuse requirements:

- [ ] Define one immutable command-recording dependency signature used by
  primary variants, secondary ranges, and command-chain schedules. Include the
  output/pass and attachment signature, render area, view mask, queue family,
  dynamic-rendering inheritance, pipeline/layout generation, mesh/index/vertex
  binding identity, buffer/image/view/sampler allocation generation,
  descriptor-layout/set/publication generation, resource-plan generation, and
  bounded external-target/frame-slot variant.
- [ ] Build the signature from the prepared immutable plan/recording snapshot,
  never by rereading mutable renderer or descriptor state during reuse choice.
- [ ] Classify invalidation as `Structural`, `BindingIdentity`, or `DataOnly`.
  Structural changes rebuild the relevant plan and recorded ranges;
  binding-identity changes invalidate only ranges that consume the binding;
  data-only publication into a completed frame slot preserves compatible
  command recordings.
- [ ] Move the minimum descriptor/resource/publication-generation fingerprint
  required for safe command reuse into Phase 5.2.5. Phase 6 expands descriptor
  coverage and diagnostics; it must not be a prerequisite hidden behind Phase
  5.2A's reuse acceptance gate.
- [ ] Replace `VulkanPrimaryCommandBufferReuseSafe = false` with validated
  capability derived from the complete dependency contract. Remove permanent
  hard-off behavior; settings and environment overrides may select a diagnostic
  policy but must not substitute for correctness validation.
- [ ] Replace global command-buffer dirtiness for local resource mutations with
  dependency-indexed invalidation. Every miss reports the first incompatible
  signature field and affected range/family.
- [ ] Keep static primary/secondary topology separate from volatile overlays,
  uploads, queries, and presentation. A volatile suffix must not force static
  opaque, skybox, shadow, or fixed post-process ranges to rerecord.

Vulkan `CpuDirect` dynamic-data requirements:

- [ ] Define a stable per-object/per-material/per-view/per-pass data layout for
  transforms, previous transforms, material IDs, skinning/blendshape IDs,
  editor IDs, flags, and pass masks. Update dirty ranges instead of rebuilding
  or uploading all visible-object data.
- [ ] Route ordinary dynamic data through bounded frame-indexed, persistently
  mapped host-visible upload arenas and safe device-local copies or direct
  bindings as appropriate. Use timeline/frame-slot completion to prevent range
  overwrite without blocking the render thread.
- [ ] Use stable descriptor bindings plus dynamic/frame-slot offsets where
  practical. Camera motion, animation, and value-only material changes must
  update bytes without changing the recorded binding topology.
- [ ] Make resizable Vulkan buffers capacity-based. Grow only when capacity is
  exceeded, publish the replacement as a new safe generation, and update used
  subranges thereafter. Exact logical element-count changes must not recreate
  backing storage every frame.
- [ ] Apply the capacity contract to editor/debug geometry, including
  `LinesBuffer`, so mesh bounds and other variable debug primitives do not emit
  steady `VkDataBufferRecreated` invalidation.
- [ ] Sort/bucket opaque CPU-direct packets by compatible pass, pipeline/state
  class, material binding layout, and mesh binding where semantics permit.
  Preserve transparent, UI, editor-overlay, and explicitly ordered diagnostic
  behavior.

Pipeline and shader readiness requirements:

- [ ] Derive required graphics/compute pipeline variants from the compiled
  structural plan and prewarm them before a cohort enters steady-state
  measurement. Include CPU-direct, GPU-indirect, meshlet, shadow, velocity,
  editor-ID, override, stereo/multiview, and dynamic/legacy target variants that
  the workload can reach.
- [ ] Keep pipeline creation, shader compilation, texture residency, and asset
  streaming outside command recording. Separate startup, warmup, streaming, and
  steady-state profiler phases.
- [ ] If optional asynchronous compilation is still pending, defer only the
  dependent optional node with an explicit reason. A required production pass
  must be ready before submission; it must not reject or defer the entire frame
  after declared warmup.
- [ ] Version pipeline-cache entries and retire replaced pipelines behind their
  last submitted timeline without globally invalidating unrelated recordings.

Render-graph dataflow requirements:

- [ ] Represent every resource use with resource identity, subresource range,
  stage, access, layout, and read/write intent; version each logical resource
  after every write.
- [ ] Derive producer-to-consumer dependencies from resource versions instead
  of declaration order alone. Reject cycles with the dependency chain and
  reject reads of uninitialized internal resources unless they are explicitly
  imported with a valid initial state.
- [ ] Calculate transient lifetimes from the scheduled graph and real queue
  waits. Preserve synchronization2 barriers, queue-family transfers, timeline
  waits/signals, and binary WSI synchronization in the same plan.
- [ ] Batch adjacent same-queue passes when it keeps submission count bounded
  without sacrificing useful overlap or deadline boundaries.
- [ ] Publish an immutable, versioned `VulkanRenderGraphPlan` whose cache
  identity includes the structural pass graph, resource versions, attachment
  signature, queue plan, and output contract, but excludes rotating external
  image handles and transient matrices.
- [ ] Emit a graph dump containing pass order, resource versions, derived and
  explicit edges, barriers, queue assignments, submissions, output deadlines,
  lifetimes, and predicted/measured durations.
- [ ] Keep physical-image aliasing disabled until tests prove candidate
  lifetimes cannot overlap across actual asynchronous execution intervals.

Acceptance criteria:

- [ ] With GTAO enabled and OpenXR eyes active, repeatedly resize the desktop
  between at least two extents. Each settled extent produces one stable feature
  mask and one committed desktop generation; desktop rendering resumes without
  `ViewportResized`/`FrameProfileChanged` supersession, while eye submission
  remains uninterrupted.
- [ ] The resize regression records no repeated same-extent swapchain recreation,
  unbounded retired-image/resource growth, rejected desktop-frame loop,
  validation error, or device loss. A deterministic automated test covers the
  camera-unavailable resize callback followed by camera-available frame prepare.
- [ ] No normal frame, eye render, mirror update, probe face, or cache eviction
  waits for all in-flight GPU work or force-flushes the device.
- [ ] Alternating OpenXR images, mirror targets, probe faces, and desktop
  swapchain images does not grow planner-state count or replace an otherwise
  compatible physical plan.
- [ ] The correlated 43.7-second planner-prune and 8.4-second plan-replacement
  failure shapes are no longer reachable in normal scheduling.
- [ ] Unit tests reject graph cycles, missing producers, uninitialized reads,
  invalid subresource transitions, and unsafe queue-family ownership plans.
- [ ] Resource-derived dependencies may reorder independent declaration order
  without changing results, and standard/synchronization validation stays clean
  across mono, multiview, async-compute, resize, and OpenXR lanes.
- [ ] Primary reuse is production-enabled without a compile-time hard-off gate
  or a required diagnostic environment flag. Reuse enabled and forced-record
  modes are visually equivalent and validation-clean under descriptor
  publication, streaming, hot reload, resize, and frame-slot rotation.
- [ ] Camera, transform, animation, material-value, query-result, debug-line,
  indirect-command, and count-buffer data updates do not rerecord compatible
  static ranges. Cache misses identify a structural or binding-identity change.
- [ ] After warmup, `LinesBuffer` and other capacity-backed dynamic buffers
  perform no steady backing recreation; growth occurs only on capacity overflow
  and publishes a bounded new generation safely.
- [ ] After declared warmup, required pipeline pending/compile counts,
  pipeline-caused `RecordDeferred`, whole-frame rejection, and render-thread
  shader compilation are zero for retained steady-state cohorts.

### 5.2.6 - Build The Immutable Snapshot, Logical View Set, And View-Family DAG

This section is the canonical runtime contract formerly tracked by the
frame-wide render-loop TODO. It must support mono, stereo, quad-view, desktop,
secondary/published textures, mirrors, captures, and probes without assuming a
fixed six-view layout. Use the existing `RenderFrameViewSet` capacity and stable
logical identities; omit inactive outputs without changing the meaning of the
remaining IDs.

#### 5.2.6.1 - Publish One Scene Snapshot And Canonical Frame View Set

- [ ] Publish one immutable render-world snapshot per engine frame containing
  stable scene objects, transforms, lights, materials, shadow/probe state, and
  GPU-scene references. Output workers must never reread mutable scene state.
- [ ] Add a focused, allocation-free frame-view builder that captures OpenXR,
  desktop, secondary, mirror, and other active output descriptors into one
  `RenderFrameViewSet` after the relevant OpenXR views are located.
- [ ] Build OpenXR descriptors from actual located poses, projections,
  recommended extents, output layers, and previous-view-projection histories.
  Do not synthesize wide/inset entries by copying ordinary eye matrices.
- [ ] Represent wide/inset parent relationships explicitly and validate the
  pose, projection, containment, and depth-convention assumptions required for
  shared visibility or Hi-Z use.
- [ ] Preserve inactive stable IDs or use an explicit stable-key mapping so
  histories never migrate when an optional output toggles. Add an explicit
  secondary/published-output role if `Debug` is not a durable semantic owner.
- [ ] Add an allocation-free adapter from `RenderFrameViewSet` to `GPUViewSet`
  descriptors/constants and replace
  `RenderCommandCollection.ConfigureGpuViewSet`'s independent five-view setup.
- [ ] Make every view consumer obtain matrices, output rectangles, parent IDs,
  predicted time, and history keys from the captured frame set. Once visibility
  generation begins, these values are immutable for the frame.
- [ ] Keep shading-rate/foveation metadata separate from real OpenXR inset-view
  identity and delete duplicated descriptors that only reuse ordinary-eye
  projections.

#### 5.2.6.2 - Plan Runtime View Families And Render Batches

- [ ] Group output requests into compatible view families before collection and
  command construction. A family owns shared scene/material/light publication
  and contains one or more view/projection/target variants.
- [ ] Make `RenderFrameViewBatchPlanner` consume actual backend, target,
  swapchain, extent, format, sample-count, layer, and layout capabilities rather
  than remain a contract/test-only planner.
- [ ] Keep the existing two-eye true-multiview path as the first production
  consumer of a planned `LayeredStereoPair`; replace the global
  `_viewCount != 2` rejection with planned-batch validation.
- [ ] Represent a foveated XR eye family as real wide/inset views. For a typical
  supported four-view configuration, plan a wide stereo pair and an inset
  stereo pair; permit a four-layer batch only when the target/backend explicitly
  supports its extent and layer contract.
- [ ] Plan desktop, secondary, and independent-camera outputs as ordinary
  single-view batches. Preserve parallel-recording and sequential modes with
  exact, visible selection reasons.
- [ ] Include stable view mask, attachment signature, output identity, and
  resource/temporal generation in command-chain and secondary-command-buffer
  identities without putting transient matrices or frame indices in structural
  keys.
- [ ] Keep sequential rendering as a separately selected parity/unsupported-
  hardware path. A requested strict `SinglePassStereo` mode must never silently
  enter sequential rendering after a capability or runtime failure.

#### 5.2.6.3 - Generate Frame-Scoped Exact Visibility For `CpuDirect`

- [ ] Define a frame-scoped candidate record with stable instance/draw identity
  and an exact active-view mask. Keep pass eligibility, frustum visibility,
  occlusion visibility, and final render-batch membership as separate fields or
  buffers with explicit capacity, overflow, retirement, and frame-slot rules.
- [ ] Add a multi-frustum CPU BVH collection API that carries a surviving view
  mask from parent to child, loads/tests each node bound once, and emits exact
  per-view command collections without retraversing the main scene for every
  output.
- [ ] Permit a rejected outer/parent view to reject a contained inset only when
  that relationship is validated for the current runtime configuration/frame.
- [ ] Keep the existing conservative combined-stereo collector as a diagnostic
  comparison lane until exact masked traversal is validated.
- [ ] Move main-camera visibility generation to the frame/family boundary so
  depth, opaque, masked, motion-vector, transparent, and other compatible passes
  consume the shared candidate set and apply their own pass/material filters.
- [ ] Keep shadows, probes, reflections, and independent cameras in distinct
  visibility domains, while sharing the scene snapshot and rebuilding a scene
  BVH at most once per scene revision/frame.

#### 5.2.6.4 - Preserve Accelerated Masked-Visibility Promotion Lanes

The following work is retained but is not part of the current `CpuDirect`
Phase 5.2A promotion gate.

- [ ] Upload all active main-view frusta in a compact frame-view buffer and
  replace the GPU BVH shader's leaf-to-parent walk with a root-down work queue
  or bounded-stack traversal that propagates the surviving view mask.
- [ ] Test only active frusta at each node; use validated parent/inset
  containment to skip safe tests and compact surviving candidates with subgroup
  or prefix-sum operations where supported.
- [ ] Produce one exact GPU visibility mask per surviving candidate and define
  deterministic, visible overflow behavior that never silently falls back to
  CPU on zero-readback strategies.
- [ ] Remove the external OpenXR pass-through cull exception once exact
  multi-view GPU visibility covers it, and move GPU main-camera culling out of
  individual `GPURenderPassCollection` execution.
- [ ] Keep candidate and production counts GPU-resident for GPU strategies.
  No CPU-generated pass mask may be reported as an exact GPU frustum result.
- [ ] Classify visible candidates by view batch, render pass, pipeline/state
  class, material, mesh, and LOD; build compatible multiview union indirect
  lists while preserving the exact logical-view mask in draw metadata.
- [ ] Define traditional-indirect layer suppression when clip/cull distance is
  supported and a distinct meshlet/task path that compacts or rejects per-view
  work without inheriting the traditional mechanism.
- [ ] Measure per-batch union, intersection, and Jaccard similarity. Add a
  hysteretic policy that may split low-similarity batches when saved geometry
  work exceeds added submission cost.
- [ ] Keep transparent sorting and LOD projection correct per point of view;
  document any conservative shared-LOD policy used by a multiview union draw.
- [ ] Compile GPU-driven work into stable pass-level dispatch, resource-specific
  barrier, and `Draw*IndirectCount` packets that can be recorded once and reused
  while frame-slot inputs and GPU-written output contents change.
- [ ] Stop treating GPU-written visibility, command, count, overflow, and delayed
  statistics values as `mutable-gpu-driven-frame-ops`. Only a topology,
  capacity, binding-identity, pipeline, or resource-generation change may force
  the compatible recorded range to rebuild.
- [ ] Keep active draw, material, state-class, and bucket work compact. The CPU
  must not scan all potential buckets, inspect current-frame counts, or construct
  commands per surviving GPU draw in a production zero-readback frame.
- [ ] Use bounded frame-slot resources for GPU cull/compact/indirect output so
  the GPU may mutate contents without racing an in-flight submission or rotating
  structural cache identity every frame.

#### 5.2.6.5 - Integrate Physical-Eye Hi-Z And Persistent Visibility

Detailed algorithm and buffer-format ownership remains with the
[GPU-driven occlusion TODO](gpu/gpu-driven-occlusion-culling-architecture-todo.md).
This tracker owns stable-view integration and graph scheduling.

- [ ] Key persistent/two-phase visibility by stable logical-view identity.
  Maintain outer-eye Hi-Z histories for physical left/right XR eyes plus
  independent desktop and secondary histories when active.
- [ ] Frustum-test inset views with their exact projection, then project bounds
  with the corresponding outer projection before sampling an outer-eye Hi-Z.
  If relationship invariants are not proven, use an independent inset hierarchy
  or disable inset occlusion explicitly.
- [ ] Support temporal Hi-Z as the low-latency/default option and optional
  current-frame outer-eye occluder depth/layered pyramid generation only when
  measured benefit justifies its XR critical-path dependency.
- [ ] Invalidate or conservatively bypass history after camera cuts, tracking
  jumps, projection discontinuities, resource-generation changes, and unsafe
  scene revisions; periodically compare against an occlusion-disabled frame.
- [ ] Extend meshlet occlusion to exact stereo/quad view data and remove its
  mono-only restriction only after the accelerated lane is validated.

#### 5.2.6.6 - Model Auxiliary Outputs Without Duplicating Architecture

- [ ] Compose the normal desktop VR mirror from already rendered eye/family
  outputs by default. Schedule a full independent desktop scene only when its
  policy explicitly requires a distinct camera or quality.
- [ ] Model pickup/handheld and in-world mirrors as view-dependent requests with
  stable IDs, screen coverage, maximum update rate, content-age limit,
  resolution policy, recursion limit, and cacheable last result.
- [ ] Share material publication, compatible shadow results, BRDF/LUT inputs,
  and GPU-scene buffers across families. Never share view-dependent data without
  an explicit compatibility proof.
- [ ] Model probe faces, mip generation, octa conversion, irradiance, and
  prefilter mips as individually schedulable DAG nodes with persistent
  intermediates and resumable progress.
- [ ] Apply capture/post-process policy at graph construction time so one
  output's optional stack does not force unrelated outputs through it.

Acceptance criteria:

- [ ] Runtime diagnostics show the same stable logical views and matrices at
  every consumer boundary for mono, stereo, supported quad, desktop, and
  secondary configurations; optional-output toggles do not migrate history.
- [ ] Two-eye OpenXR uses a planned stereo batch. Supported four-view OpenXR
  uses two planned stereo pairs by default; unsupported combinations are
  rejected or explicitly selected at the planner boundary.
- [ ] `CpuDirect` performs one exact masked main-camera BVH traversal per
  compatible frame visibility domain, matches a per-view reference collector,
  and adds no steady-state managed allocation.
- [ ] Profiler counters prove one scene snapshot and no duplicate
  material/light publication for compatible families. Composition-only desktop
  mirroring adds no scene traversal or independent full render.
- [ ] Cached mirrors/probes report content age and authorized reuse rather than
  masquerading as freshly rendered output.
- [ ] When each accelerated lane is opened, GPU masks match the reference
  collector, zero-readback paths read back no visibility/count data, no geometry
  appears in a view whose bit is clear, and Hi-Z validation reports no
  false-negative visibility failures or inset-coordinate misuse.

### 5.2.7 - Add A Deadline-Aware CPU/GPU Output Scheduler

- [ ] Represent OpenXR submission, desktop present, secondary publication, and
  capture/debug completion as explicit terminal nodes. Attach priority,
  deadline, predicted/measured duration, and authorized reuse/degradation policy
  to relevant graph nodes.
- [ ] Schedule the output DAG by deadline, dependency readiness, measured cost,
  and policy priority rather than invoking independent full render loops in
  callback order.
- [ ] Calculate the reverse longest path to required OpenXR submission and
  schedule that critical path before optional output work. Diagnostics must name
  every node on it and its predicted/measured duration.
- [ ] Reserve the acquired OpenXR eye budget first. Do not start optional work
  that cannot finish or reach a legal preemption boundary before the eye
  submission deadline.
- [ ] Make desktop acquisition nonblocking for XR-owned frames where the
  platform allows it. Permit policy-authorized prior-frame reuse, skipped
  updates, or resolution reduction instead of extending the XR critical path.
- [ ] Double-buffer secondary publication and permit bounded rate reduction or
  prior-frame reuse, especially when the texture is consumed by the XR scene.
  Keep same-frame secondary-to-XR dependencies only for explicitly declared
  pose-sensitive behavior.
- [ ] Give desktop, pickup/in-world mirrors, shadows, probes, IBL, uploads, and
  diagnostics explicit rolling CPU/GPU budgets and maximum consecutive work.
- [ ] Budget optional SSAO, SSR, volumetrics, outer-eye quality, current-frame
  occlusion, captures, and debug work through the same scheduler. Choose
  outer-versus-inset priority from measured policy rather than a hard-coded
  unconditional ordering.
- [ ] Use measured exponential/percentile cost estimates per node to decide
  start/defer. Record predicted versus actual time and correct bad estimates.
- [ ] Make auxiliary work resumable at legal graph boundaries: probe face,
  cubemap mip, octa conversion, irradiance pass, individual prefilter mip,
  shadow slice, and upload batch.
- [ ] Add adaptive mirror/probe cadence based on visibility, screen coverage,
  motion, content age, and available slack. Preserve an explicit fixed-cadence
  mode for validation and user requests.
- [ ] Keep GPU submissions bounded for TDR and frame pacing. Batching outputs is
  permitted only when it reduces CPU overhead without creating an oversized,
  non-preemptible submission.
- [ ] Feed per-node GPU timestamps and queue-idle measurements into existing
  queue-overlap promotion/demotion hysteresis. Do not move all compute work to
  async compute when contention or ownership-transfer cost is worse.
- [ ] Expose queued/running/deferred/completed/failed state, budget reason,
  content age, deadline miss, and accumulated work for every output.

Acceptance criteria:

- [ ] Optional mirrors/captures/probes cannot cause a deadline-critical eye frame
  to miss its CPU submit budget or wait behind a long auxiliary submission.
- [ ] A blocked or unavailable desktop/secondary output cannot block OpenXR;
  every reuse, skip, cadence, resolution, or quality decision is visible in
  telemetry.
- [ ] A 36-probe batch and visible mirrors make bounded forward progress while XR
  remains active; neither monopolizes consecutive frames.
- [ ] No work is silently dropped. Every defer, stale reuse, cadence reduction,
  or quality choice is visible and policy-authorized.
- [ ] XR missed-frame rate and p95/p99 submit margin do not regress from the
  controlled baseline on retained workloads.

### 5.2.8 - Decompose Submission And Remove CPU Serialization

- [ ] Split submit telemetry into queue-lock wait, diagnostic-context build,
  image-contract validation, lifetime validation, native submit, lifetime
  publication, layout publication, and completion advancement.
- [ ] Build one ordered submission DAG across uploads, shadows, scene/view
  families, mirrors, captures, overlays, and presentation. Preserve independent
  queue work where dependencies permit.
- [ ] Use timeline/frame-slot completion for engine queues and OpenXR image
  reuse. Remove per-output fence creation and indefinite CPU waits from normal
  eye/mirror/capture submission.
- [ ] Narrow queue-lock ownership to the native externally synchronized Vulkan
  operation and required adjacent state publication. Do not hold it while
  building diagnostics or scanning unrelated state.
- [ ] Batch compatible command buffers into a submit when it lowers CPU cost,
  but retain separate completion/deadline boundaries for XR-critical and
  auxiliary work.
- [ ] Build render packets and record dirty independent secondary command ranges
  on workers from the immutable frame snapshot. The render thread performs only
  final ordered validation/reuse choice and submission.
- [ ] Give workers persistent per-thread scratch, command pools, and capacity-
  backed packet/recording buffers. Do not allocate or contend on shared global
  collections in the worker hot path.
- [ ] Measure worker record time, render-thread wait-for-worker time, queue-lock
  time, submit count, command buffers per submit, and CPU/GPU overlap by family.

Acceptance criteria:

- [ ] Submit p95 has an attributable breakdown and contains no full global
  dependency/layout scan.
- [ ] OpenXR runtime updates, desktop submission, and auxiliary recording do not
  serialize on a broad engine queue lock.
- [ ] Parallel recording improves or preserves p95 without changing output,
  submission order, fallback policy, or device-loss containment.

### 5.2.9 - Remove Remaining Hot-Path Allocation And Superlinear Work

- [ ] Remove per-FBO attachment-layout arrays, dynamic UI split arrays,
  exact-length frame-op drain arrays, image-layout snapshot arrays/sorts,
  planner registry merge arrays, and retirement-drain temporary arrays from
  steady paths.
- [ ] Complete dynamic-rendering Phase 1.1 bounded inline format-signature
  storage and use the same allocation-free identity representation in planning,
  inheritance, pipeline lookup, and command-cache keys.
- [ ] Replace repeated render-graph sorting with one deterministic compile/sort
  per structural generation. Replace O(n^2) context-order and clear-lifting
  scans with indexed/linear algorithms.
- [ ] Preallocate output/view-family DAG nodes, dependency edges, packet lists,
  touched-resource lists, and telemetry rows to measured high-water marks.
- [ ] Add per-scope allocation counters for snapshot build, family grouping,
  visibility, plan lookup/compile, packet build, primary/secondary recording,
  submit validation/publication, retirement drain, and diagnostics.

Acceptance criteria:

- [ ] Stable desktop and mixed-output frames allocate zero managed bytes in
  collection, planning, recording, submission, and retirement hot paths.
- [ ] Work scales approximately with changed nodes, unique view families, and
  actual draws, not total cached outputs times total scene size.

### 5.2.10 - Validation Matrix And Promotion Gate

This first matrix is the Phase 5.2A `CpuDirect` production gate and the shared
baseline inherited by later lanes. `GpuIndirectInstrumented`,
`GpuIndirectZeroReadback`, and meshlet submission must not be reported as
passing until their readiness checklists open Phase 5.2B or 5.2C. Their current
absence does not block Phase 6 after 5.2A, but it does remain visible tracker
debt and prevents whole-tracker completion under the promotion semantics above.

- [ ] Extend the controlled P0.2 baseline with mono desktop, OpenXR sequential
  stereo, true two-eye multiview, available quad-view lanes, desktop/secondary
  outputs, and current per-pass visibility-dispatch counts. Record candidate and
  emitted-draw counts, culling/recording CPU and GPU duration, queue transfers,
  barrier-stage flushes, missed XR frames, and steady-state allocation.
- [ ] Add matched Release Vulkan/OpenGL `CpuDirect` cohorts with identical
  scene, camera, lights, output extent/scale, warmup state, occlusion, debug
  features, and profiler settings. Run low-, medium-, and high-draw-count plus
  material-diverse workloads and retain the exact configuration manifest.
- [ ] Add focused contract tests proving stable logical-view/history identity,
  planned-batch selection, `CpuDirect` CPU-BVH ownership, and the distinction
  between pass eligibility, frustum visibility, occlusion visibility, and
  render-batch membership.
- [ ] Run validation-disabled performance and validation-enabled correctness as
  separate cohorts with identical workload/settings manifests.
- [ ] Capture at least three 60-second stable repetitions for:
  - [ ] desktop only;
  - [ ] desktop plus ImGui/dynamic text;
  - [ ] OpenXR foveated eye family without a desktop mirror;
  - [ ] OpenXR eyes plus composition desktop mirror;
  - [ ] OpenXR eyes plus pickup/handheld mirror;
  - [ ] desktop with one and four visible in-world mirrors;
  - [ ] light-probe batch without XR;
  - [ ] OpenXR eyes plus mirrors plus a 36-probe batch;
  - [ ] explicit dynamic and legacy render-target modes;
  - [ ] `CpuDirect` with the startup/effective-strategy fingerprint preserved.
- [ ] For every cohort record CPU frame p50/p95/p99/worst, observed render
  throughput, GPU family/pass p50/p95, missed XR deadlines, output content age,
  scene snapshots/visibility builds, record/reuse/dirty counts, allocation,
  resource retirement, plan churn, queue waits/submits, and quality/fallback
  events.
- [ ] Record primary/secondary record and reuse time separately from native
  Vulkan call time and GPU execution. Also record pipeline pending/created/
  cache-hit counts, deferred/rejected-frame reasons, dynamic-buffer capacity/
  growth/recreation, dirty object/range counts, uploaded bytes, pipeline/
  descriptor/vertex/index bind counts, and avoided redundant binds.
- [ ] Also record candidate count after shared traversal, exact visible count
  per logical-view bit, per-batch union/intersection/Jaccard values where
  applicable, per-view frustum/occlusion rejection, Hi-Z source/invalidation/
  bypass/build frequency, graph-node and critical-path GPU timestamps,
  ownership transfers/stage flushes, per-batch draw/triangle/meshlet counts, and
  unexpected-readback counters. Unsupported lanes report `not ready`, not zero.
- [ ] Capture and inspect screenshots from at least two camera positions for
  desktop, each eye/foveated region, composition mirror, pickup/in-world mirror,
  and representative probe faces/final IBL output.
- [ ] Run StandardValidation and SyncValidation over the mixed-output matrix and
  confirm Phase 5.1 ordered state/lifetime contracts remain clean.
- [ ] Cover graphics-only and selected graphics-plus-compute queue plans;
  dedicated transfer is required only on hardware where policy selects it.
- [ ] Cover resize, minimize/restore, swapchain recreation, generation
  replacement/failure, shader reload, scene transition, runtime/session loss,
  camera cuts/tracking jumps, moving occluders, dirty scene/BVH, and device-loss
  diagnostics as applicable to the ready lane.
- [ ] On the RTX 3090 / 0.67-scale desktop diagnostic scene, require CPU frame
  p50 <= 10 ms, p95 <= 12 ms, and p99 <= 14 ms, or approve and document a new
  workload-equivalent baseline before promotion.
- [ ] By default, require warmed Vulkan `CpuDirect` CPU p95 to remain within 10%
  of matched OpenGL `CpuDirect` p95 or beat the absolute Vulkan target above.
  Any exception requires a repeated profile that attributes the delta to named
  backend work, an approved threshold, and a retained follow-up; Debug-build or
  cold-cache results cannot waive this gate.
- [ ] Require Vulkan CPU-direct collection, upload, and state-change curves to
  scale with dirty objects/ranges and compatible state groups rather than all
  visible objects times pass count. Preserve the measured low/medium/high-count
  curves as the baseline for Phase 5.2B/5.2C crossover analysis.
- [ ] Require dynamic rendering to remain within 5% of legacy p50/p95/p99 across
  repetitions unless an explained GPU-side win justifies a measured CPU cost.
- [ ] Require >=99% clean command reuse, zero stable record-path allocation,
  zero steady resource retirement/plan replacement, zero rejected submissions,
  and zero global waits/force flushes for static-output cohorts.
- [ ] Require zero post-warmup required-pipeline deferrals, pipeline-caused
  whole-frame rejections, steady exact-size dynamic-buffer recreation, and
  render-thread shader compilation in retained cohorts.
- [ ] Define and meet an XR missed-deadline threshold before running auxiliary
  work. The threshold must be based on the active runtime refresh rate and
  separately report runtime/compositor-owned misses.
- [ ] Require a composition-only desktop VR mirror to add no independent scene
  render and no more than an approved sub-millisecond CPU p95 delta.
- [ ] Require mixed mirror/probe workload cost to follow the configured scheduler
  budget rather than grow linearly with every potential output each frame.
- [ ] Update the CPU framerate investigation with final before/after tables,
  manifests, screenshots, raw-log paths, and any approved exceptions.

Final acceptance criteria:

- [ ] Desktop-only rendering has an accepted CPU baseline and no longer relies
  on a diagnostic environment flag for acceptable command reuse.
- [ ] Multiple outputs consume one shared frame snapshot and reuse compatible
  visibility, material, plan, resource, and command work.
- [ ] Deadline-critical XR eyes cannot be blocked by optional mirror, probe,
  capture, upload, or diagnostic work.
- [ ] Correctness-safe reuse and targeted invalidation remain clean under
  retirement, resize, hot reload, target rotation, and device-loss injection.
- [ ] No tested workload hides a requested accelerated path behind a CPU fallback
  or unreported quality/cadence reduction.
- [ ] Only after all shared and Phase 5.2A acceptance criteria pass may Phase 6
  begin. Phase 5.2B/5.2C remain independently visible promotion gates and still
  govern whole-tracker completion.

Phase 5.2B/5.2C accelerated-lane promotion requirements, activated separately
when their readiness checklists are complete:

- [ ] Validate GPU indirect instrumented, GPU indirect zero-readback, and each
  supported meshlet strategy independently across mono, sequential stereo,
  parallel recording, true two-eye multiview, supported quad view, desktop
  mirror, and secondary output.
- [ ] Require Phase 5.2B to complete the active-list, overflow, barrier batching,
  indirect-count, delayed-diagnostics, and final-validation contracts in the
  compact zero-readback tracker. Phase 5.2C additionally requires its meshlet
  readiness/parity checklist on each supported hardware lane.
- [ ] Compare disabled, temporal Hi-Z, and current-frame Hi-Z, including camera
  cuts, tracking jumps, moving occluders, and scene/BVH revisions.
- [ ] Require exact GPU visibility to match the per-view reference collector,
  zero unexpected CPU readback for zero-readback lanes, and one main-camera BVH
  traversal per compatible frame visibility domain.
- [ ] Require warmed production GPU lanes to reuse stable pass-level dispatch,
  barrier, and indirect-draw command topology. Per-frame changes to GPU-written
  visibility/command/count contents must produce zero primary rerecords unless a
  reported topology, capacity, binding, pipeline, or resource generation changes.
- [ ] Require production zero-readback CPU submission work to scale with active
  pass/state-class batches, not visible draw count, scene capacity, or the full
  material/bucket table. Instrumented diagnostic readback cost is reported
  separately and cannot stand in for the production result.
- [ ] Require supported quad-view OpenXR to use two planned stereo batches by
  default, with no geometry emitted into a logical view whose exact bit is clear.
- [ ] Require zero steady-state managed allocation in frame-view construction,
  visibility, compaction, graph scheduling, and submission; zero new Vulkan/
  OpenXR validation errors; and no more than 5% two-eye CPU/GPU regression
  without a documented downstream win and owner approval.
- [ ] Capture matched low-, medium-, and high-count scaling curves for 5.2A,
  5.2B, and supported 5.2C lanes with CPU recording and GPU execution separated.
  GPU-driven rendering is not required to beat `CpuDirect` in the low-count
  scene, but a production accelerated lane must demonstrate an approved
  crossover or scaling advantage in a retained high-count/occluded workload.
  If it does not, it remains correctness-ready rather than performance-promoted.

Recommended implementation order:

1. Stabilize deterministic resource generations and versioned graph dataflow.
2. Publish the immutable scene snapshot and canonical live
   `RenderFrameViewSet` with its allocation-free GPU adapter.
3. Put existing two-eye multiview behind runtime batch planning, then add the
   supported wide/inset two-pair quad layout.
4. Implement generation-complete command reuse plus the capacity-backed Vulkan
   CPU-direct upload/data path.
5. Implement exact masked `CpuDirect` traversal and reuse it across compatible
   main-camera passes and outputs.
6. Prewarm all reachable pipeline variants and separate warmup/streaming from
   steady-state submission.
7. Integrate output terminal nodes, XR critical-path analysis, optional-output
   reuse, and deadline-ordered submission.
8. Complete the `CpuDirect` validation matrix and promote Phase 5.2A.
9. Open Phase 5.2B with exact GPU masked traversal, compact batch-oriented
   indirect generation, stable recorded topology, and persistent physical-eye
   Hi-Z.
10. Open Phase 5.2C after GPU indirect promotion and complete meshlet parity on
    supported hardware.

Guardrails:

- Measure two-view masked traversal before widening it; a worst-case work queue
  or global-atomic design must not penalize the common stereo case.
- Select quad batches from real runtime/target capabilities. Different extents
  or layouts must produce an explicit split/rejection reason.
- A low-overlap multiview union may waste geometry work; retain a hysteretic,
  measurable split path.
- Never share outer-eye Hi-Z with an inset until pose, containment, projection,
  depth convention, and history generation are proven compatible.
- Keep current-frame occluder depth graph-selectable because it can improve
  correctness while lengthening the XR critical path.
- Compute transient aliasing from semaphore-constrained execution intervals,
  not topological order alone.
- Separate structural batch/plan identity from frame data so transient matrices
  and frame indices cannot create an unbounded cache.
- Keep shadows, probes, reflections, and independent cameras in explicit
  visibility domains; one shared frame snapshot does not imply one visibility
  result for unrelated cameras.
- Land new runtime code behind the focused owners defined by the Vulkan/OpenXR
  organization trackers; do not grow another broad renderer partial-class
  authority.

## Phase 6 - Descriptor And Binding Robustness

Build on Phase 5.2A's allocation-free, generation-driven descriptor publication
and command-reuse seam. The minimum generations needed to validate cached
recordings already belong to Phase 5.2.5; this phase completes descriptor
coverage, pool robustness, null-binding policy, and crash diagnostics.
Descriptor hardening must not restore per-draw fingerprint construction, global
command-cache invalidation, or identical-write generation churn.

- [ ] Define descriptor fingerprint inputs for all descriptor-affecting resources:
  - [ ] image handle,
  - [ ] image view handle,
  - [ ] sampler handle,
  - [ ] image layout,
  - [ ] buffer handle,
  - [ ] range/offset,
  - [ ] descriptor type,
  - [ ] array index,
  - [ ] shader reflection binding identity.
- [ ] Add stable allocation/resource generation IDs to fingerprints so Vulkan
  handle reuse cannot make a stale descriptor appear current.
- [ ] Ensure program-bound SSBOs and sampled images update descriptor
  fingerprints when resources are recreated.
- [ ] Replace placeholder descriptor use with explicit diagnostic counters:
  - [ ] texture not generated,
  - [ ] resource pending,
  - [ ] image view missing,
  - [ ] layout invalid,
  - [ ] descriptor set stale,
  - [ ] device lost.
- [ ] Define which bindings may legally use null/dummy descriptors. Required
  bindings must prevent recording/submission rather than sampling a placeholder
  that masks a publication or lifetime bug.
- [ ] Audit descriptor-pool capacity, reset/free synchronization, and exhaustion
  telemetry. Include pool generation and remaining capacity in the last-frame
  descriptor dump.
- [ ] Cap repeated placeholder warnings by binding/resource key to keep logs
  readable during crashes.
- [ ] Add validation that `LightProbeIrradianceArray`,
  `LightProbePrefilterArray`, and probe SSBO bindings do not collide with
  G-buffer bindings.
- [ ] Add descriptor-state dump for the last successfully submitted frame before
  device loss.

Acceptance criteria:

- [ ] Descriptor sets are invalidated when any referenced Vulkan handle changes.
- [ ] Stale descriptor use is diagnosable from logs without stepping through the
  renderer.

## Phase 7 - OpenXR Vulkan Submit And Synchronization Hardening

Use the Phase 5.2 output scheduler, timeline completion, view-family, and submit
DAG as the generic execution path. This phase owns OpenXR-specific image/runtime
state-machine correctness and failure attribution, not a second eye-only
submission architecture.

- [ ] Audit OpenXR Vulkan frame phases:
  - [ ] acquire swapchain image,
  - [ ] wait image,
  - [ ] render eye(s),
  - [ ] release image,
  - [ ] submit frame,
  - [ ] mirror/update runtime state.
- [ ] Track every OpenXR swapchain image with an explicit state machine:
  `Available` -> `Acquired` -> `Waited` -> `Rendering` -> `GpuComplete` ->
  `Released`; log and reject illegal transitions.
- [ ] Ensure rendering and memory writes are complete before
  `xrReleaseSwapchainImage`, and keep the runtime-required image layout and any
  queue-family ownership transitions explicit.
- [ ] Ensure auxiliary capture work cannot run inside an unsafe OpenXR image
  ownership window unless explicitly scheduled.
- [ ] Separate OpenXR eye command buffers from capture command buffers in
  diagnostics and resource state.
- [ ] Verify that requested `SinglePassStereo` never invokes sequential eye
  rendering after capability, recording, dependency, submit, publish, runtime,
  session, or device-loss failure. Separately selected sequential rendering is
  an independent mode, not a fallback.
- [ ] Improve `SubmitAndWaitOpenXrCommandBuffers` logs with:
  - [ ] eye index,
  - [ ] swapchain image index,
  - [ ] fence/timeline value,
  - [ ] command buffer labels,
  - [ ] frame-op context.
- [ ] Add a policy for draining/deferring retired resources before OpenXR eye
  rendering when frame slots are pending.
- [ ] Add smoke tests for OpenXR Vulkan with capture work queued during eye
  rendering.
- [ ] Test loss/exit paths at every OpenXR boundary (`xrWaitFrame`, image
  acquire/wait/release, `xrBeginFrame`, `xrEndFrame`) independently from Vulkan
  device loss so runtime/session loss is not mislabeled.

Acceptance criteria:

- [ ] OpenXR submit failures distinguish runtime/session loss, swapchain image
  errors, and actual Vulkan device loss.
- [ ] Requested `SinglePassStereo` never attempts fallback eye rendering after
  any capability, recording, dependency, submit, publish, runtime/session, or
  logical-device failure.

## Phase 8 - GPU Work, Memory Budgeting, And TDR Protection

Extend Phase 5.2's deadline scheduler and resumable auxiliary nodes with Vulkan
memory admission, TDR protection, and subsystem-specific quality policy. Do not
introduce a parallel probe/capture queue that bypasses the shared output DAG.

- [ ] Add per-subsystem GPU work budgets for:
  - [ ] light-probe cubemap faces,
  - [ ] IBL irradiance convolution,
  - [ ] IBL prefilter mip chain,
  - [ ] shadow refresh,
  - [ ] texture uploads,
  - [ ] compute-heavy GI passes,
  - [ ] pipeline/shader warmup.
- [ ] Slice light-probe batch capture so it cannot monopolize multiple OpenXR
  frame intervals.
- [ ] Move IBL convolution to an explicit queued job with budgeted steps:
  - [ ] cubemap mip generation,
  - [ ] octa conversion,
  - [ ] irradiance pass,
  - [ ] prefilter mip pass per mip.
- [ ] Add profiler counters for queued, running, completed, failed, and deferred
  probe-capture work.
- [ ] Add TDR-risk diagnostics for unusually long command submissions.
- [ ] Record GPU timestamps around probe capture and IBL passes.
- [ ] Add a throttle for batch capture when OpenXR frame timing degrades.
- [ ] Add memory-budget telemetry using core/available budget facilities such as
  `VK_EXT_memory_budget` where supported:
  - [ ] heap budget and usage,
  - [ ] VMA allocation/block totals and fragmentation indicators,
  - [ ] pending allocation bytes by frame-op context,
  - [ ] retired-but-not-yet-freed bytes,
  - [ ] descriptor pool/set pressure,
  - [ ] high-water marks immediately preceding device loss.
- [ ] Define soft and hard admission thresholds for optional capture resources.
  Defer work with an explicit reason before allocation pressure becomes an OOM
  or device-loss cascade; do not silently reduce requested quality.
- [ ] Keep the production policy inside Windows' normal TDR budget. Registry TDR
  changes may be documented only as controlled diagnostic experiments, never as
  the fix or a normal launch prerequisite.

Acceptance criteria:

- [ ] A 36-probe batch can run while OpenXR Vulkan is active without a long
  single submission or repeated missed frame budget.
- [ ] Probe capture progress remains visible in logs/profiler.
- [ ] Stress logs show bounded memory/retirement growth and no allocation or
  descriptor-pool exhaustion before, during, or after capture.

## Phase 9 - Tests, Stress Runs, And Tooling

- [ ] Add source-contract tests for:
  - [ ] frame-op context capture before recording,
  - [ ] direct FBO capture policy,
  - [ ] capture pipeline exclusions,
  - [ ] descriptor fingerprint inputs,
  - [ ] resource retirement gates,
  - [ ] image layout assertions.
- [ ] Prefer behavioral/unit tests over string-only source-contract tests for
  new state machines, fingerprints, retirement gates, and preset resolution.
  Keep source-contract tests only as temporary migration guards.
- [ ] Add runtime smoke tests for:
  - [ ] Vulkan editor startup,
  - [ ] Vulkan unit-testing world,
  - [ ] OpenXR Vulkan startup,
  - [ ] light-probe batch capture,
  - [ ] OpenXR Vulkan plus light-probe capture,
  - [ ] rapid resize during capture,
  - [ ] device-lost handling path with an injected mock failure if possible.
- [ ] Add deterministic fault injection at submit, fence/timeline wait,
  allocation, descriptor update/publication, and OpenXR acquire/wait/release
  boundaries. Assert first-error preservation, producer cancellation, zero
  post-loss submissions, and non-blocking teardown.
- [ ] Add RenderDoc capture recipe for the failing and fixed probe path.
- [ ] Add MCP/editor iteration checklist for capture validation:
  - [ ] capture from at least two camera positions,
  - [ ] inspect viewport screenshots,
  - [ ] inspect exported probe textures,
  - [ ] inspect Vulkan logs after shutdown.
- [ ] Fix stale source-contract tests in
  `VulkanDeferredProbeGiFixesTests` so the whole class can be used as a
  regression suite again.
- [ ] Add CI-safe tests that do not require a physical OpenXR headset.
- [ ] Add bounded soak/stress definitions with exact duration/frame count,
  resize cadence, capture cadence, and pass/fail log counters. Avoid acceptance
  criteria based only on one successful run.

Acceptance criteria:

- [ ] Focused tests cover each hardening area.
- [ ] The broader Vulkan probe/deferred test class is no longer stale.
- [ ] At least one automated stress path exercises OpenXR Vulkan plus queued
  capture work.
- [ ] Fault-injection tests prove that all GPU-work producers quiesce after the
  first device-loss transition and teardown cannot wait forever on dead fences.

## Phase 10 - Documentation And Operator Workflow

- [ ] Update `docs/architecture/rendering/vulkan-renderer.md` with the frame-op
  context model.
- [ ] Update `docs/architecture/rendering/openxr-vr-rendering.md` with Vulkan
  submit/device-loss behavior.
- [ ] Add a capture pipeline note under `docs/architecture/rendering/`.
- [ ] Document validation preset launch commands.
- [ ] Document device-loss triage:
  - [ ] what logs to inspect,
  - [ ] what breadcrumbs mean,
  - [ ] when to use RenderDoc,
  - [ ] when to use validation layers,
  - [ ] how to distinguish teardown noise from render failure.
- [ ] Document driver/OS evidence collection on Windows, including Event Viewer
  display-driver/TDR events, without treating OS-level timeout changes as an
  engine fix.
- [ ] Add editor UI/help text only where needed in diagnostics panels; avoid
  normal in-app instructional clutter.
- [ ] Keep `docs/work/todo/rendering/vulkan-deferred-and-probe-gi-fixes-todo.md`
  cross-linked if probe GI remains a separate work item.

Acceptance criteria:

- [ ] A contributor can reproduce a diagnostic Vulkan launch and know where to
  find the relevant logs.
- [ ] The device-loss workflow is documented enough that future incidents do not
  start from scratch.

## Final Acceptance Criteria

- [ ] One canonical immutable frame view set drives every live Vulkan/OpenXR
  consumer with stable logical identities and predicted-time consistency.
- [ ] Generation-complete command dependency validation replaces the hard
  primary-reuse safety quarantine. Production reuse requires no diagnostic
  environment flag, and data-only frame-slot publication does not invalidate
  compatible recorded ranges.
- [ ] `CpuDirect` generates exact main-view visibility with one masked CPU-BVH
  traversal per compatible frame domain and reuses it across eligible passes.
- [ ] Vulkan `CpuDirect` uses bounded capacity-backed dynamic resources and
  dirty-range/frame-slot updates. Ordinary transforms, animation, material
  values, camera motion, queries, and debug geometry do not recreate backing
  storage or rerecord static work after warmup.
- [ ] Matched Release Vulkan/OpenGL CPU-direct baselines and low/medium/high-count
  scaling curves pass the Phase 5.2A thresholds with CPU recording separated
  from native API and GPU execution time.
- [ ] Every promoted GPU-driven lane submits compact active work through stable
  reusable dispatch/barrier/indirect topology, performs no forbidden
  current-frame readback, and demonstrates the approved high-count/occlusion
  crossover or scaling advantage.
- [ ] Supported OpenXR view configurations render through capability-planned
  batches, including two stereo pairs for supported quad view, without silent
  sequential fallback from strict single-pass mode.
- [ ] The versioned render graph rejects invalid dataflow and schedules optional
  outputs so they cannot delay required OpenXR submission.
- [ ] Vulkan light-probe batch capture completes in the unit-testing world while
  OpenXR Vulkan is active.
- [ ] No `VK_ERROR_DEVICE_LOST` occurs in the targeted capture/OpenXR stress
  runs.
- [ ] Validation and sync-validation diagnostic runs are clean for the targeted
  paths or have documented external-driver exceptions.
- [ ] Resource planner logs show stable per-context resource plans instead of
  cross-context physical image churn.
- [ ] Device-loss injection or simulated submit failure stops rendering cleanly
  and reports a useful breadcrumb summary.
- [ ] Hot-path allocation checks remain clean for command recording and
  submission.
- [ ] Declared steady-state cohorts have zero required-pipeline deferrals,
  pipeline-caused whole-frame rejections, render-thread shader compilation, and
  exact-size dynamic-buffer recreation.
- [ ] The final validation matrix names exact hardware/runtime configurations,
  run duration/frame counts, diagnostic coverage, warning/VUID allowlist, peak
  memory, maximum submission time, and OpenXR missed-frame threshold.
- [ ] Device-fault and vendor diagnostic artifacts are collected when supported;
  unsupported capabilities are explicit in the result manifest.
- [ ] Documentation, source-contract tests, and smoke tests are updated.
- [ ] Phase 5.2A is promoted, and every supported Phase 5.2B/5.2C production
  lane is promoted or has an explicit recorded v1 removal/deferral decision;
  unfinished accelerated lanes are not hidden by the overall tracker status.

## Remaining Design Decisions

These are decisions to make when their owning phase begins; they are not
completion checkboxes.

- Whether OpenXR auxiliary capture is forbidden during eye-image ownership or
  admitted only through the scheduler with explicit barriers.
- Whether probe IBL convolution should use compute, fullscreen graphics, or the
  current pass model after measurement.
- Which curated diagnostic preset is the default for local Vulkan crash
  investigations.
- Whether deterministic shutdown remains the v1 device-loss policy or a later
  renderer/editor restart workflow is required.
- The minimum acceptable light-probe throughput while OpenXR is active.

## External Technical References

- [Khronos Vulkan validation overview](https://docs.vulkan.org/guide/latest/validation_overview.html)
- [Khronos synchronization examples](https://docs.vulkan.org/guide/latest/synchronization_examples.html)
- [`VK_KHR_device_fault`](https://docs.vulkan.org/refpages/latest/refpages/source/VK_KHR_device_fault.html)
- [`vkGetDeviceFaultReportsKHR`](https://docs.vulkan.org/refpages/latest/refpages/source/vkGetDeviceFaultReportsKHR.html)
- [`vkGetDeviceFaultDebugInfoKHR`](https://docs.vulkan.org/refpages/latest/refpages/source/vkGetDeviceFaultDebugInfoKHR.html)
- [`VkDeviceFaultInfoKHR`](https://docs.vulkan.org/refpages/latest/refpages/source/VkDeviceFaultInfoKHR.html)
- [`VkDeviceFaultDebugInfoKHR`](https://docs.vulkan.org/refpages/latest/refpages/source/VkDeviceFaultDebugInfoKHR.html)
- [`VK_EXT_device_fault`](https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_device_fault.html)
- [`vkGetDeviceFaultInfoEXT`](https://docs.vulkan.org/refpages/latest/refpages/source/vkGetDeviceFaultInfoEXT.html)
- [`VK_EXT_device_address_binding_report`](https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_device_address_binding_report.html)
- [`VK_NV_device_diagnostic_checkpoints`](https://docs.vulkan.org/refpages/latest/refpages/source/VK_NV_device_diagnostic_checkpoints.html)
- [LunarG Khronos validation-layer settings](https://vulkan.lunarg.com/doc/view/latest/windows/khronos_validation_layer.html)
- [Microsoft WDDM timeout detection and recovery](https://learn.microsoft.com/windows-hardware/drivers/display/timeout-detection-and-recovery)
