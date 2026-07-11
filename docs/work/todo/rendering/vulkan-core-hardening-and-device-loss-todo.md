# Vulkan Core Hardening And Device-Loss TODO

Last Updated: 2026-07-10
Owner: Rendering
Status: Phase 5.2.4b Correctness Remediation Gate Open; Phase 5.2.5 Blocked
Execution: Current worktree only; do not create or switch branches for this effort.

## Goal

Make Vulkan robust and fast enough for normal editor, OpenXR, scene-capture,
light-probe, shadow, mirror, UI-preview, and diagnostic rendering without recurring
`VK_ERROR_DEVICE_LOST` failures caused by cross-context resource churn, stale
descriptors, unsafe resource retirement, or oversized GPU submissions.

This work is not about hiding Vulkan failures behind silent fallbacks. It should
make invalid states visible earlier, isolate independent frame operations, and
turn device-loss investigations into actionable diagnostics.

## Recent Trigger

The July 9, 2026 light-probe capture crash showed the current failure shape:

- `LightProbeBatch` was active and reached probe 10 of 36.
- OpenXR Vulkan eye rendering then reported device loss from an eye fence wait.
- Vulkan logs immediately before device loss showed heavy capture-sized
  resource-plan churn for `SceneCaptureEnvDepth`, `LightProbeEnvColor`,
  G-buffer resources, post-process resources, bloom, exposure, and final output.
- Scene/light-probe capture had a caller-owned cubemap FBO, but still entered
  the full viewport/post/temporal command path.

The immediate mitigation routed Vulkan light-probe capture through the direct
FBO path. This todo tracks the broader engine fixes so the same class of failure
does not reappear in another auxiliary render path.

## Scope

- Vulkan renderer command submission, frame-op modeling, resource planning,
  descriptor binding, image-layout tracking, and resource lifetime.
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
- Diagnostic features are capability-gated and additive. Unsupported standard
  or vendor extensions must be reported explicitly, not silently treated as
  successful diagnostic coverage.

## Implemented Summary

The checked implementation history through Phase 5.2.4a, plus the completed
strict OpenXR slice, is condensed here. Detailed evidence remains in the linked
progress, investigation, and validation records; this section is a summary, not
a replacement for those artifacts.

- **Phases 0-2.1 — diagnosis, containment, and context isolation.** Established
  deterministic repro/result manifests, the device-loss risk taxonomy, a
  first-writer-wins terminal renderer state, named/composable diagnostic
  presets, KHR/EXT/vendor fault collection, object naming, bounded validation
  reports, and allocation-free submission breadcrumbs. Added the local
  `VK_KHR_device_fault` shim required by the current bindings. Introduced
  immutable `FrameOpContext` identity, rejected context-mismatched command
  buffers, isolated planner runtime state and allocation ownership by context,
  added ref-counted shared allocator ownership with deduplicated destruction,
  serialized queue operations through the terminal-state gateway, bounded and
  truncation-aware fault draining, and repaired OpenXR eye/mirror/publish/
  prewarm keys and diagnostics.
- **Phases 3-5.1 — capture, lifetime, and synchronization correctness.** Added
  explicit `RenderCapturePolicy` variants and a minimal direct-FBO path that
  excludes the viewport G-buffer, temporal, bloom, exposure, upscale, and final
  output stack while preserving backend-native orientation and shared clip
  policy; probe octahedral/IBL work remains explicit post-capture work.
  Implemented completion-safe resource retirement, descriptor lifetime
  tracking, and non-waiting lost-device teardown. Centralized per-subresource
  image state, sync2 barriers, ordered submit-chain state propagation, queue
  ownership, pure descriptor-layout resolution, render-graph physical
  dependencies, reverse invalidation, generation-sensitive cached state,
  acquire/present recovery, allocator ownership, API/feature-gated `pNext`
  chains, and ordered OpenXR teardown. The five-lane Phase 5.1 matrix closed
  with zero engine-owned synchronization, layout, acquire/present, or teardown
  errors; the remaining SteamVR messages are narrowly attributed runtime
  exceptions, not a broad allowlist.
- **Phases 5.2.1-5.2.4a — output contract and cheap exact invalidation.** Added
  the unified output-request manifest, priorities, budgets, dependencies,
  degradation telemetry, profiler schema, quiet-window gating, structural versus
  volatile generations, bounded target variants, static/volatile command
  separation, deterministic reuse misses, stable resource identity, exact
  reverse-dependency invalidation, range-compacted local layout/dependency
  tracking, descriptor-pool reverse indexes, structural descriptor allocation
  keys, safe late-race rejection, pressure telemetry, and lock/allocation-free
  validated hot paths.
- **Strict OpenXR view/probe slice — 2026-07-10.** `SinglePassStereo` is now a
  strict true-multiview contract: unsupported capability or render/submit failure
  logs the exact reason and ends the begun frame without projection layers;
  sequential fallback is forbidden. Separately selected sequential/parallel
  modes own independent pipelines, command collections, publication, and
  occlusion keys. True single-pass owns one conservative stereo key. OpenXR
  external planner identity is stable across runtime image rotation while
  command/submission identity retains image slot and generation. Instance/system
  probing now uses exact `Result` control flow, bounded retry, and change-only
  optional-extension reporting. The remaining strict-SPS failure was correctly
  rejected as a retired-buffer-generation reference and was not hidden by a
  fallback.

Implemented decisions that no longer need open questions: capture variants live
behind explicit pipeline policy; diagnostics use composable flags plus curated
presets; and rotating external image handles are variants, not physical-plan
identity.

### Evidence Index

- [Phase 0 baseline and crash taxonomy](../../progress/rendering/vulkan-core-hardening-phase0-2026-07-09.md)
- [Phase 2.1 validation manifest](../../testing/rendering/vulkan-core-hardening-phase21-validation-2026-07-09.json)
- [Phase 4 live lifetime evidence](../../investigations/rendering/vulkan-core-hardening-phase4-live-validation-2026-07-09.md)
- [Phase 5/5.1 live investigation](../../investigations/rendering/vulkan-core-hardening-phase5-live-validation-2026-07-09.md)
  and [validation manifest](../../testing/rendering/vulkan-core-hardening-phase5-validation-2026-07-09.json)
- [CPU framerate regression investigation](../../investigations/rendering/vulkan-cpu-framerate-regression-2026-07-09.md)
- Phase 5.2 validation:
  [5.2.1](../../testing/rendering/vulkan-core-hardening-phase521-validation-2026-07-09.json),
  [5.2.2](../../testing/rendering/vulkan-core-hardening-phase522-validation-2026-07-09.json),
  [5.2.3](../../testing/rendering/vulkan-core-hardening-phase523-validation-2026-07-10.json),
  [5.2.4](../../testing/rendering/vulkan-core-hardening-phase524-validation-2026-07-10.json), and
  [5.2.4a](../../testing/rendering/vulkan-core-hardening-phase524a-validation-2026-07-10.json)
- [Descriptor lifetime freeze investigation](../../investigations/rendering/vulkan-descriptor-lifetime-freeze-2026-07-10.md)
- [Dynamic rendering/OpenXR regression investigation](../../investigations/rendering/vulkan-dynamic-rendering-promotion-2026-07-10.md)

Representative raw evidence remains under
`Build/_AgentValidation/20260709-233000-vulkan-phase521/` and
`Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-09_23-29-11_pid55356/`.

## Inherited Validation Debt

These requirements were not closed by the implementation summarized above and
remain active. They must be reconciled with current evidence rather than silently
discarded.

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

`Immutable frame snapshot -> output requests -> compatible view families -> cached plans/resources -> record or reuse -> deadline-ordered submit DAG`

This phase owns output-neutral scheduling, stable identity, command reuse,
targeted invalidation, local tracking, asynchronous retirement, and the
multi-output performance contract. Related ownership remains with the
[desktop frame-loop decomposition todo](vulkan-desktop-frame-loop-decomposition-todo.md),
[dynamic-rendering migration todo](vulkan-dynamic-rendering-migration-todo.md),
and [primary command-recording fast-path todo](optimization/vulkan-primary-command-recording-fast-path-todo.md).
Those efforts must consume the same output/plan contract and must not introduce a
desktop-only scheduler or a second attachment identity model.

The current settled experimental reuse cohort demonstrated that the architecture
can reach 108.79 samples/s, 8.883 ms p50, 11.031 ms p95, zero record-path
allocation, zero retirement, and zero rejection on the measured workload. That
result is evidence, not permission to promote reuse until the remaining
correctness matrix closes.

### 5.2.4b - Pre-5.2.5 Correctness Remediation Gate

This is a blocking correctness gate. Do not begin 5.2.5 until every acceptance
criterion below is checked. Limit plan work here to the correctness needed to
stop the active generation/target churn; 5.2.5 still owns the general versioned,
nonblocking plan-and-arena architecture.

The Monado session
`xrengine_2026-07-10_14-20-29_pid22804` recorded six retired-uniform-buffer
submission rejections, 38 `MainViewport` signature changes, 47 physical-plan
changes, 18 forced waits, and 182 retirement-backlog reports. The visible top
strip is approximately 111 pixels, the same numerical difference as
`1007 - 896`; treat that as a viewport/extent clue, not a proven cause. Preserve
this diagnosis and subsequent evidence in the
[dynamic rendering/OpenXR investigation](../../investigations/rendering/vulkan-dynamic-rendering-promotion-2026-07-10.md).

- [ ] Extend that investigation with the `14-20-29` session manifest, the
  6/38/47/18/182 baseline counters, the supplied screenshot, exact reproduction
  settings/commands, and the user's observed bloom, blur, top-strip, occlusion,
  and black-frame symptoms before relying on the baseline as durable evidence.

#### 5.2.4b.1 - Make Strict Stereo Submittable Without Lifetime Races

- [x] Convert OpenXR renderer prewarm into two passes: first count every use of
  each `VkMeshRenderer` in the complete eye command buffer, then reserve final
  draw/uniform/descriptor slot capacity before recording or publication. Reuse
  the regular Vulkan capacity helper rather than a divergent OpenXR allocator.
- [ ] Add a regression where the same renderer is used at least three times in
  one strict-stereo command buffer. No referenced uniform buffer, descriptor,
  image view, framebuffer, or plan generation may retire while that buffer is
  recorded, queued, or in flight.
- [ ] Pin all recorded resource generations through the last submitted
  completion ticket and make every pre-submit lifetime rejection name the
  resource, old/new generation, owning output, command buffer, and retirement
  ticket.
- [x] Preserve the strict contract at every failure boundary: requested
  `SinglePassStereo` resolves only to true multiview or unsupported, never to
  sequential. Capability, target, recording, validation, submit, or publish
  failure logs the exact stage and reason and ends a begun OpenXR frame without
  projection layers. A separately selected sequential mode remains legal.
- [ ] Add a zero-tolerance sequential-fallback counter and behavioral tests for
  each strict-SPS failure boundary.

#### 5.2.4b.2 - Stabilize Desktop Planning And Presentation

- [ ] Keep directional-shadow resources registered for the lifetime of a
  compatible pipeline generation. Gate shadow pass execution and dependencies;
  do not add/remove logical resource specs as ordinary shadow work toggles.
- [ ] Keep `MainViewport` physical-plan identity stable for an unchanged
  pipeline, attachment signature, and extent. External desktop/OpenXR target
  rotation binds a bounded slot variant and must not allocate/retire an otherwise
  compatible plan.
- [ ] Prevent stereo target rotation from dirtying desktop resources and prevent
  desktop resize/present state from invalidating the eye family.
- [ ] Remove `ResourcePlanReplacement` waits, force flushes, and global device
  drains from this steady-state repro. Retire replaced generations only behind
  frame-slot/timeline completion; 5.2.5 generalizes this rule to all outputs and
  eviction paths.
- [ ] A rejected desktop frame must not present a cleared or unwritten image.
  Correlate every detected black frame with final-target contents, swapchain
  write/present state, plan/signature changes, retirement, submission rejection,
  and exposure.

#### 5.2.4b.3 - Repair Stereo Layer And Fullscreen-Region Contracts

- [ ] In anti-aliasing resource declaration, derive the TSR layer count with
  `DeclaredLayerCount(builder)` and apply matching `.Layers(...)` and
  `.StereoCompatible(builder.Profile.Stereo)` metadata to `TsrOutputTexture` and
  every `TsrHistory*` resource, including `TsrHistoryColor`. Test the compiled
  descriptor against the factory-created Vulkan image/view shape.
- [x] Fix generic Vulkan blits/copies with `layerIndex == -1` to copy the common
  source/destination layer span and publish transitions for every copied layer.
  Cover history color, history depth, TSR output/history, and final stereo
  publish staging; never silently collapse the operation to layer zero.
- [ ] Audit every stereo post-process descriptor/factory/FBO pair so extent,
  format, mip count, array layers, `StereoCompatible`, and attachment view shape
  agree. True-multiview attachments cover both layers with `viewMask=0x3`;
  per-eye views are allowed only at the final OpenXR publish boundary.
- [ ] Make `PostProcessStereo.fs`, stereo bloom downsample/upsample and copy,
  `FinalPostProcessStereo.fs`, and `TemporalSuperResolutionStereo.fs` derive
  `ScreenOrigin`, `ScreenWidth`, and `ScreenHeight` from the current destination
  attachment/mip. Log the source extent and UV transform and assert that local
  raster coverage maps to `[0,1]`; clamping must not conceal an origin/extent
  mismatch.
- [ ] Require each `BloomBlurTexture` write attachment to be a single-mip,
  two-layer array view with `layerIndex == -1`, and every sampled source-mip view
  to remain a two-layer `sampler2DArray` view.
- [ ] Instrument every stereo fullscreen pass with destination extent, render
  area, viewport/scissor, attachment layer count, view mask, draw-time screen
  uniforms, source extent, and UV transform. In the current
  repro each full-resolution pass must be exactly `896x1007`, two layers, and
  `viewMask=0x3`; fail validation on a width-as-height, preview, desktop, stale
  parent, or mip-extent mismatch.
- [ ] Capture and inspect both layers at pre-TSR color, velocity, current/history
  depth, TSR output/history, every active bloom accumulation mip (including
  0/1/4), final post-process, publish staging, and the acquired OpenXR image.
  Identify the first incorrect pass responsible for the 111-pixel strip.

#### 5.2.4b.4 - Restore Stereo Temporal And Bloom Correctness

- [x] Remove ambient temporal-pipeline lookup from deferred motion-vector
  binding. Capture and pass the immutable temporal snapshot belonging to the
  command collection's pipeline and eye family.
- [x] Define one stereo jitter/velocity/reprojection convention across raster
  motion vectors and TSR: jitter both eyes, use matching coverage, encode the
  current/previous jitter delta exactly once, and keep NDC/UV scale, sign, and Y
  direction consistent.
- [ ] Verify current/previous view-projection matrices, jitter, velocity, history
  readiness, and reset generation independently for both layers. Camera cuts and
  incompatible extent/profile generations reset history; ordinary head motion
  and OpenXR image rotation do not.
- [ ] Make `HistoryReady` layer-complete: color and depth history for both eyes
  must finish before readiness is published. After warmup, missing temporal
  snapshot/matrix paths are zero-tolerance; during an intentional reset, render
  current-frame data, invalidate history, and log the exact temporal key until
  both layers are reseeded.
- [ ] At 1:1 eye resolution, keep temporal accumulation, per-eye history,
  velocity, and reprojection active; bypass only an unnecessary spatial upscale
  kernel. Validate a sub-native-resolution TSR cohort separately. Factor
  mono/stereo TSR algorithm code so history rejection, clamping, and sharpening
  cannot drift.
- [x] Fix `PostProcessStereo.fs` bloom composition so default mip counts 1-4 use
  the accumulated mip-1 result like the mono path and do not double-count coarse
  mips.
- [ ] Validate static pose, head rotation, translation, object motion,
  disocclusion, and motion-stop. Static velocity is zero; moving velocity is
  nonzero, directionally correct, and eye-specific. Both layers retain sharp
  stable detail with no cross-eye history, persistent blur, ghosting, bloom
  displacement, or top-strip corruption.
- [ ] Define deterministic rendered-output thresholds before the run for
  per-eye bloom energy/centroid and independence, scripted velocity samples,
  static-edge sharpness, and temporal convergence. Compare stereo against a
  mono-equivalent controlled input; visual inspection remains required but is
  not the sole oracle.

#### 5.2.4b.5 - Restore Per-Pipeline Occlusion Without Disabling It

- [x] Key command publication, query state, result epochs, stale age, and probe
  budget by the owning pipeline's complete `OcclusionViewKey`; no valid path may
  fall back to ambient `CurrentRenderingPipeline` state.
- [x] Give desktop/editor, each explicitly sequential eye pipeline,
  mirrors/captures, and true single-pass stereo independent hardware-query
  ownership and per-frame probe budgets. One pipeline must not consume the
  global query opportunity for every other pipeline.
- [x] For true single-pass stereo, issue one conservative multiview query and
  cull only when the mesh is occluded in both views. Visibility in either eye
  keeps the mesh visible.
- [ ] Treat missing ownership, unavailable results, stale epochs, camera cuts,
  extent changes, command-set changes, and pipeline recreation as bounded
  fail-visible/reprobe states for that command collection. A camera cut restores
  visibility immediately; another output's negative result is never reused.
  Define the maximum recovery age in the validation manifest and fail the run if
  normal queried state is not restored within it.
- [ ] Emit telemetry keyed by the full `OcclusionViewKey`: pipeline/output ID,
  submissions, resolutions, skips, current/max age, forced-visible recovery, and
  recovery latency.
- [ ] Compare occlusion-enabled desktop and SPS output against a diagnostic
  occlusion-off ground truth. Require final-image and known-visible-sentinel
  parity, require each enabled candidate set to be a subset of its off set, and
  prove every removed mesh is occluded for the owning desktop view or both SPS
  views. No known-visible sentinel may be rejected, and independently valid
  desktop/SPS culls must both remain nonzero.

#### 5.2.4b.6 - Close The Live Correctness Gate

- [ ] Add a deterministic Monado validator with warmup followed by exactly 300
  retained frames using Vulkan dynamic rendering, requested/effective
  `SinglePassStereo`, `EVrMirrorMode.FullIndependentRender`, bloom, TSR,
  `EOcclusionCullingMode.CpuQueryAsync`, and fixed bloom/motion/top-edge/
  occlusion and known-nonblack final-output sentinels. The desktop output must
  visibly change during scripted motion so a stale present cannot pass.
- [ ] Explicitly enable synchronization validation for the 300-frame cohort and
  record the effective diagnostic preset, layers, settings, and any exact
  externally owned allowlist. Validation silently resolving to `Off` fails the
  cohort.
- [ ] Record per frame: output and pipeline identity, external target slot/image,
  extent/layers/view mask, plan and command generations, lifetime validation,
  submit, OpenXR acquire/wait/publish/release/end-frame, desktop final write, and
  present result.
- [ ] Preserve both-layer captures, the frame ledger, filtered logs, and a
  machine-readable
  `docs/work/testing/rendering/vulkan-core-hardening-phase524b-validation-2026-07-10.json`;
  add rendered-output tests in addition to source-contract assertions.
- [ ] Before live acceptance, compile every touched stereo shader, build
  `XREngine.Runtime.Rendering` and the editor, and pass focused unit and
  deterministic rendered-output tests for lifetime, layer shape, fullscreen
  region, temporal/bloom, occlusion, presentation, and strict failure behavior.
- [ ] After warmup, record zero physical-plan/signature changes for unchanged
  outputs, submission rejections of any kind, premature retirements, compatible-
  plan or structural-resource create/retire churn, descriptor pool/set churn,
  sequential fallbacks, global invalidation fallbacks, normal-path global waits
  or force flushes, `VUID-`, `SYNC-HAZARD`, or `UNASSIGNED` engine-owned
  validation errors, first-chance rendering/lifetime exceptions, and device
  loss.
- [ ] All 300 strict-SPS frames use true multiview with `viewMask=0x3`, populate
  both layers, and complete acquire, wait, render, publish, release, and
  `xrEndFrame` with valid projection layers. Exercise precise zero-layer/no-
  fallback behavior separately with injected failures; no failed frame belongs
  in the passing 300-frame window.
- [ ] All 300 desktop frames contain a final write and valid present; none is
  black, cleared-only, unwritten, stale for an extended period, or absent.
- [ ] A controlled rejected desktop frame follows the documented skip-present or
  last-completed-image policy and never publishes a cleared target. Exposure and
  exposure-history values remain finite, nonzero where the scene requires it,
  and owned by the desktop pipeline.
- [ ] TSR histories advance independently for both layers, stereo velocity and
  reprojection pass the static/motion/disocclusion cases, bloom contributes
  correctly in both eyes, and no captured stage contains the top strip.
- [ ] Desktop and SPS retain every known-visible sentinel and final-image parity
  with their occlusion-off references after the bounded recovery window. Every
  omitted candidate has owning-view occlusion proof, SPS requires both-eye
  occlusion, and distinct stable `OcclusionViewKey` instances report independent
  nonzero valid culling work.
- [ ] Resource, descriptor, planner-state, and command-variant counts remain
  bounded with no positive steady-state drift. Visually inspect retained
  desktop and both-eye captures; profiler/log success alone is insufficient.
- [ ] Only after every 5.2.4b checkbox above is complete may 5.2.5 begin.

### 5.2.5 - Make Render Plans And Resource Arenas Versioned And Nonblocking

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
  arena and transient aliasing plan. Reuse compatible allocations across frames
  and alias only resources whose scheduled lifetimes do not overlap.
- [ ] Separate runtime-owned external images from engine-owned allocation and
  retirement. External image rotation must not churn engine resource plans.
- [ ] Add plan-cache hit/miss, plan-generation, arena high-water, alias reuse,
  pending-retirement bytes, and eviction-defer telemetry by output family.

Acceptance criteria:

- [ ] No normal frame, eye render, mirror update, probe face, or cache eviction
  waits for all in-flight GPU work or force-flushes the device.
- [ ] Alternating OpenXR images, mirror targets, probe faces, and desktop
  swapchain images does not grow planner-state count or replace an otherwise
  compatible physical plan.
- [ ] The correlated 43.7-second planner-prune and 8.4-second plan-replacement
  failure shapes are no longer reachable in normal scheduling.

### 5.2.6 - Build One Immutable Scene Snapshot And View-Family DAG

- [ ] Publish one immutable render-world snapshot per engine frame containing
  stable scene objects, transforms, lights, materials, shadow/probe state, and
  GPU-scene references. Output workers must never reread mutable scene state.
- [ ] Group output requests into compatible view families before collection and
  command construction. A family owns shared scene/material/light publication
  and contains one or more view/projection/target variants.
- [ ] Build visibility once per compatible family or compute one conservative
  superset plus compact per-view masks. Do not repeat a full scene traversal for
  every eye, foveated inset, desktop mirror, or probe consumer.
- [ ] For foveated XR eyes, represent wide/inset views as one deadline-critical
  family. Use Vulkan multiview/single-pass and shared visibility/material data
  where feature/quality constraints permit. Keep sequential rendering only as a
  separately selected implementation path for parity or unsupported hardware;
  it is never a fallback from requested `SinglePassStereo`.
- [ ] Compose the normal desktop VR mirror from already rendered eye/family
  outputs by default. Schedule a full independent desktop scene only when the
  selected mirror policy explicitly requires a distinct camera or quality.
- [ ] Model pickup/handheld mirrors and in-world 3D mirrors as view-dependent
  output requests with stable IDs, screen-coverage/visibility input, maximum
  update rate, content-age limit, resolution policy, recursion limit, and
  cacheable last result.
- [ ] Share view-independent work such as material publication, compatible
  shadow results, BRDF/LUT inputs, and GPU-scene buffers across families. Never
  share view-dependent results without an explicit compatibility proof.
- [ ] Model light/reflection probe faces, mip generation, octa conversion,
  irradiance, and prefilter mips as individually schedulable DAG nodes with
  persistent intermediate resources and resumable progress.
- [ ] Ensure one output's optional post-processing does not force unrelated
  outputs through the full desktop temporal/post stack. Apply the existing
  capture policy at graph construction time.

Acceptance criteria:

- [ ] Profiler counters prove one scene snapshot per engine frame and no
  duplicate material/light publication for compatible output families.
- [ ] A composition-only desktop VR mirror adds no scene traversal or independent
  full render.
- [ ] Adding compatible foveated views increases per-view cull/target work but
  does not multiply scene snapshot, material, descriptor, or stable command
  construction by view count.
- [ ] Cached mirrors/probes reuse their prior result within explicit age/quality
  policy and never masquerade as freshly rendered output.

### 5.2.7 - Add A Deadline-Aware CPU/GPU Output Scheduler

- [ ] Schedule the output DAG by deadline, dependency readiness, measured cost,
  and policy priority rather than invoking independent full render loops in
  callback order.
- [ ] Reserve the acquired OpenXR eye budget first. Do not start optional work
  that cannot finish or reach a legal preemption boundary before the eye
  submission deadline.
- [ ] Give desktop, pickup/in-world mirrors, shadows, probes, IBL, uploads, and
  diagnostics explicit rolling CPU/GPU budgets and maximum consecutive work.
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
- [ ] Expose queued/running/deferred/completed/failed state, budget reason,
  content age, deadline miss, and accumulated work for every output.

Acceptance criteria:

- [ ] Optional mirrors/captures/probes cannot cause a deadline-critical eye frame
  to miss its CPU submit budget or wait behind a long auxiliary submission.
- [ ] A 36-probe batch and visible mirrors make bounded forward progress while XR
  remains active; neither monopolizes consecutive frames.
- [ ] No work is silently dropped. Every defer, stale reuse, cadence reduction,
  or quality choice is visible and policy-authorized.

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
  - [ ] `CpuDirect` and a separate `GpuIndirectZeroReadback` cohort.
- [ ] For every cohort record CPU frame p50/p95/p99/worst, observed render
  throughput, GPU family/pass p50/p95, missed XR deadlines, output content age,
  scene snapshots/visibility builds, record/reuse/dirty counts, allocation,
  resource retirement, plan churn, queue waits/submits, and quality/fallback
  events.
- [ ] Capture and inspect screenshots from at least two camera positions for
  desktop, each eye/foveated region, composition mirror, pickup/in-world mirror,
  and representative probe faces/final IBL output.
- [ ] Run StandardValidation and SyncValidation over the mixed-output matrix and
  confirm Phase 5.1 ordered state/lifetime contracts remain clean.
- [ ] On the RTX 3090 / 0.67-scale desktop diagnostic scene, require CPU frame
  p50 <= 10 ms, p95 <= 12 ms, and p99 <= 14 ms, or approve and document a new
  workload-equivalent baseline before promotion.
- [ ] Require dynamic rendering to remain within 5% of legacy p50/p95/p99 across
  repetitions unless an explained GPU-side win justifies a measured CPU cost.
- [ ] Require >=99% clean command reuse, zero stable record-path allocation,
  zero steady resource retirement/plan replacement, zero rejected submissions,
  and zero global waits/force flushes for static-output cohorts.
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
- [ ] Only after all Phase 5.2 acceptance criteria pass may Phase 6 begin.

## Phase 6 - Descriptor And Binding Robustness

Build on Phase 5.2's allocation-free, generation-driven descriptor publication
seam. Descriptor hardening must not restore per-draw fingerprint construction,
global command-cache invalidation, or identical-write generation churn.

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
- [ ] The final validation matrix names exact hardware/runtime configurations,
  run duration/frame counts, diagnostic coverage, warning/VUID allowlist, peak
  memory, maximum submission time, and OpenXR missed-frame threshold.
- [ ] Device-fault and vendor diagnostic artifacts are collected when supported;
  unsupported capabilities are explicit in the result manifest.
- [ ] Documentation, source-contract tests, and smoke tests are updated.

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
