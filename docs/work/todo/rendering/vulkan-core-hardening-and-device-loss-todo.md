# Vulkan Core Hardening And Recording Code Changes TODO

Last Updated: 2026-08-13
Owner: Rendering
Status: Active

This is the single implementation tracker for Vulkan core hardening, frame-plan
recording, primary recording fast paths, Forward+ render-graph cost, render tail
latency, and advanced-render-pipeline architectural phases 06 through 10. Its
companion, [Vulkan Core Hardening And Recording Testing
TODO](../../testing/rendering/vulkan-core-hardening-and-recording-testing-todo.md), owns every build,
test, capture, stress, visual, and performance validation task.

The required end state is defined by the
[Vulkan Render Loop Target Architecture](../../design/rendering/vulkan-render-loop-target-architecture.md).
The implementation must combine production-grade fault containment and
lifecycle correctness with a small readable ownership surface, zero-allocation
steady-state hot paths, and complete CPU critical-path attribution. Stability,
speed, observability, and source simplification are joint completion gates; none
may be traded away to claim progress on another.

Completed implementation history remains in the
[completed-work record](vulkan-core-hardening-and-device-loss-completed.md).

## Code Changes

### 5. Schedule Outputs And Submission Without Cross-Output Blocking

> Reopened on 2026-08-12. The first closeout removed this checklist before its
> item-level state was preserved in the completed-work sibling. The checklist is
> restored here, and the two completed implementation criteria are also recorded
> in the sibling history. The section remains active: the output DAG is not yet
> the sole execution authority, OpenXR requests do not consistently carry a real
> runtime deadline, and the Sponza camera-motion no-regression gate is still open.

- [ ] Build one deadline-aware submission DAG for uploads, shadows, desktop,
  OpenXR eyes, mirror, probes, captures, and publication.
- [ ] Prioritize acquired OpenXR eyes and reserve their critical path before
  optional output work; make desktop/secondary acquisition nonblocking for
  XR-owned frames.
- [ ] Add bounded, observable deferral, cadence, and stale-reuse policy for
  mirrors, probes, optional effects, and captures.
- [ ] Narrow native queue-lock ownership and never hold it across a blocking
  fence wait; use timeline/frame-slot completion for queue and OpenXR image
  ownership.
- [x] During Win32 modal interactive resize, keep the already-published scene,
  shadow, UI, and presentation generations frozen independently and use WSI
  presentation scaling for the changing surface. Do not rebuild or retire the
  main physical resource plan inside the drag callback; publish one catch-up
  generation after the modal loop exits.
- [ ] Make modal resize dispatch bounded and nonblocking with respect to
  visibility publication, GPU completion, and retirement drains. A missing or
  incompatible frame package must produce an explicit defer/stale-reuse result,
  not leave the interactive-render guard latched indefinitely.
- [x] Add persistent worker recording for independent safe packet classes and
  preserve serial recording for packets that cannot yet be isolated.

### 6. Simplify The Forward+ Render Graph

- [ ] Co-produce or reuse depth, normals, and velocity where possible; skip the
  depth prepass when no consumer requires it.
- [ ] Remove redundant opaque/masked geometry replay, full-resolution
  color/depth copies, paired blits, transitions, and barriers.
- [ ] Model attachment lifetime, aliasing, input attachments, and explicit
  transitions in backend-neutral graph intent with Vulkan realization.
- [ ] Conditionally allocate and execute AO, bloom, probe, shadow, temporal,
  and post-process producers only when their consumers are enabled.

### 7. Bound Shadow, Streaming, And Render-Thread Tail Work

- [ ] Define directional cascade invalidation from camera, light, caster,
  receiver, atlas, and quality state; stabilize projections and reuse unaffected
  cascade recording/data.
- [ ] Add a bounded per-frame directional-cascade update budget and explicit
  temporal policy.
- [ ] Move texture decode, transcode, mip preparation, and upload planning off
  the render thread; batch transfer recording, sparse transitions,
  finalization, and descriptor publication.
- [ ] Publish immutable texture generations with narrow descriptor/command
  invalidation and bounded per-frame upload work.
- [ ] Move pure generic jobs, BVH work, physics preparation, and capture
  preparation to their owning workers; split render-thread-affine work into
  budgeted increments with admission control.

### 8. Add End-To-End CPU Observability And Runtime Diagnostics

- [ ] Publish explicit counters and state for device loss, frame/output status,
  reuse decisions and misses, queue/fence wait, worker wait, allocations,
  jobs, cascade invalidation, uploads, descriptor publication, GPU work, and
  deferred work.
- [ ] Add device-fault, TDR-risk, memory-budget, and submission-breadcrumb
  diagnostics when supported by the active Vulkan device.
- [ ] Add concise Vulkan/OpenXR submit and descriptor-state dumps that preserve
  the last successful submission context without adding steady-state work.
- [ ] Record per-context planner ownership, display/internal extents, registry
  and resource generations, active physical allocation, and every attempted
  cross-context substitution. Promote incompatible context/extent reuse from a
  throttled warning to a structured frame-rejection reason.
- [ ] Extend final-presentation diagnostics with the complete immutable source
  tuple, bound descriptor payload, selected primary/secondary artifacts, layout
  transitions, swapchain image, and submit generation so a stale view cannot be
  mistaken for a valid logical `SourceTexture` binding.
- [ ] Add an interactive-resize liveness watchdog with breadcrumbs for modal
  callback entry/exit, visibility publication, package selection, plan
  replacement, retirement backlog, queue/timeline waits, submission, and
  present. Report renderer hangs separately from validation errors, device loss,
  managed exceptions, and native process crashes.
- [ ] Replace the disconnected desktop lifecycle counters, flat
  `EVulkanCpuStage` interpretation, and targeted Vulkan CPU spans with one
  `VulkanFrameTelemetry` schema; retain compatibility adapters only until every
  dashboard, profiler, MCP tool, and benchmark consumes the shared schema.
- [ ] Define one stable coarse stage taxonomy from frame pacing and snapshot
  handoff through acquire, plan, resource preparation, scheduling, recording,
  submit, output completion, and settlement; keep detailed operation IDs nested
  below those stages rather than expanding the top-level budget vocabulary.
- [ ] Correlate every aggregate or retained span with engine/render frame IDs,
  output and view-set IDs, frame slot, relevant generation, stage/detail ID,
  span/parent/cross-thread link IDs, thread/worker ID, start/end timestamp,
  allocation, operation count/bytes, typed outcome, and wait/reuse/invalidation
  reason.
- [ ] Classify time as engine work, wait, native-driver call, external-runtime
  work, or intrusive diagnostics; prohibit unlabeled blocking calls and ensure
  queue-lock, fence/timeline, acquire, present, worker, collect, and retirement
  waits remain individually visible.
- [ ] Compute inclusive and exclusive time, aggregate worker CPU, wall active
  span, overlap, imbalance, render-thread wait, causal critical path, and
  attributed/unattributed root time after capture without double-counting nested
  or parallel scopes.
- [ ] Keep aggregate mode allocation-free and low-contention with fixed
  per-thread/per-frame storage. Keep targeted traces in pre-warmed bounded rings,
  freeze bounded before/after windows for slow frames, and serialize or aggregate
  only outside measured threads.
- [ ] In detailed captures, attribute at least 99% of each frame-root wall
  interval and emit an explicit `Unattributed` failure record for every gap of
  50 microseconds or more.
- [ ] Publish the same stable IDs and results to the editor frame tree/timeline,
  runtime counters, MCP/component-profiler results, and machine-readable
  JSON/CSV/trace exports; defer all string formatting until consumption.
- [ ] Measure aggregate and targeted observer overhead against the accepted
  clean-profile contract. Diagnostic instrumentation may not masquerade as a
  clean promotion capture or invalidate unrelated reusable commands.

### 9. Make Occlusion Modes Bounded And Effective

- [ ] Separate occlusion candidates, occluders, tested bounds, rasterized
  triangles, queries, Hi-Z invocations, indirect commands, and actual culls in
  runtime telemetry.
- [ ] Add representative open, moderate, occluder-heavy, masked, static, and
  deterministic moving-camera occlusion scenarios.
- [ ] Bound CPU-software candidate selection, sorting, and rasterization; bypass
  cheaply when candidates, occluders, or prior benefit do not justify the work.
- [ ] Define CPU-query latency, refresh, stale-result, and camera-motion policy
  without CPU waits or current-frame result dependencies.
- [ ] Use persistent minimal-format GPU Hi-Z resources; bound pyramid, barriers,
  refinement, and count-copy work, consume visibility on GPU, and cheaply bypass
  ineffective cases.
- [ ] Define selection thresholds and hysteresis, retain a forced diagnostic
  mode, and explicitly mark each CPU-software, CPU-query, and GPU Hi-Z mode as
  production, opt-in, diagnostic-only, or retired.

## Advanced Render Pipeline Phases 06 Through 10

These phases continue the ordered
[Advanced Render Pipeline Architectural Refactor](architectural-refactor/00-advanced-render-pipeline-refactor-todo.md)
after [05 - Attribute Reconstruction](architectural-refactor/05-attribute-reconstruction-todo.md).
They consume the immutable frame, resource, descriptor, scheduling, and
diagnostic contracts in sections 1 through 9 above rather than creating a
parallel renderer architecture.

Sections 10 through 14 are backend-neutral rendering architecture. OpenGL and
Vulkan may use different native encodings, but both must implement the same
logical visibility, material, view, resource-generation, and output contracts;
the Vulkan path additionally inherits every hardening invariant above.

| Former phase | Consolidated section | Dependency outcome |
| --- | --- | --- |
| 06 | 10. Classify Visible Material Work | Attribute reconstruction supplies stable `AdvancedSurface` identity and derivatives. |
| 07 | 11. Shade Native Opaque Materials | Classification supplies bounded visible kernel work. |
| 08 | 12. Integrate Transparency, Special Passes, And Post | Native opaque HDR and depth become the only ordinary scene-color foundation. |
| 09 | 13. Integrate Stereo, XR, Capture, And Editor Views | Every output consumes the same scene/feature contracts through independent context-local plans. |
| 10 | 14. Cut Over And Retire Legacy Rendering | The companion testing tracker supplies the required promotion evidence. |

### 10. Classify Visible Material Work On The GPU

The canonical classification key is shading kernel, material layout,
material-state/coverage class, required attribute/derivative mode, and view
mode. Material-row ID remains data within a compatible kernel, and descriptor
set object identity is never part of the logical key.

#### 10.1 Work Domain And Tile Policy

- [ ] Select initial tile dimensions from measured occupancy and subgroup
  behavior.
- [ ] Define mono and per-eye/layer addressing.
- [ ] Define active-tile, kernel-tile, and optional compact pixel-list records.
- [ ] Reserve capacities from screen size and documented worst-case material
  diversity.
- [ ] Define empty-pixel and background exclusion.

#### 10.2 Classification Kernels

- [ ] Read final visibility and resolve the material/kernel key from immutable
  GPU tables.
- [ ] Build active tiles and per-kernel tile membership.
- [ ] Add a compact pixel-list path for sparse or highly mixed tiles only where
  it wins measured workloads.
- [ ] Use subgroup ballot/scan where available.
- [ ] Provide a deterministic bounded shared-memory fallback when subgroup
  operations are unavailable.
- [ ] Avoid atomics proportional to total registered material count.
- [ ] Skip empty tiles and kernels without CPU involvement.

#### 10.3 GPU Dispatch Construction

- [ ] Prefix-sum or otherwise compact kernel/tile/pixel ranges.
- [ ] Build indirect dispatch arguments entirely on the GPU.
- [ ] Keep bounded fixed command topology over kernel families or use a
  backend-supported indirect execution mechanism.
- [ ] Treat count and range changes as data-only publication that does not
  rerecord otherwise reusable primary or secondary packets.
- [ ] Publish the minimum resource-specific barriers required before native
  shading through the immutable frame plan.
- [ ] Keep delayed statistics readback outside the frame dependency chain.

#### 10.4 Capacity And Overflow

- [ ] Define independent overflow contracts for active tiles, kernel
  memberships, pixel lists, and indirect-argument ranges.
- [ ] Never drop pixels silently.
- [ ] In automatic mode, use a bounded conservative full-tile kernel recovery
  only when it preserves correctness.
- [ ] In required mode, expose an error surface and structured failure when
  correctness cannot be preserved.
- [ ] Record the first overflow cause, required capacity, selected recovery,
  and affected pixels through delayed diagnostics.
- [ ] Grow persistent capacity only at a safe frame boundary through the
  generation transaction in section 1.

#### 10.5 Material Diversity And Kernel Scheduling

- [ ] Keep many material rows sharing one kernel within common dispatch work;
  never create one dispatch per material instance.
- [ ] Order kernel work to reduce pipeline changes without changing visibility
  correctness.
- [ ] Prewarm engine-owned kernel families and backend variants.
- [ ] Define explicit behavior for rare kernels, pending shader compilation,
  and nonresident textures.
- [ ] Expose material eligibility, kernel ID, and selected recovery in editor
  diagnostics.

#### 10.6 Classification Diagnostics

- [ ] Add views for active tiles, kernel IDs, material IDs, mixed-tile density,
  pixel-list density, dispatch ranges, and overflow.
- [ ] Add counters for visible pixels, active tiles, kernel-tile pairs,
  compacted pixels, active kernels, dispatches, overflows, and GPU time.
- [ ] Report classification work independently for every stereo eye/layer.
- [ ] Give every classification buffer a stable capture name.

### 11. Shade Native Opaque Materials, Lighting, Decals, And GI

Compatible opaque and masked surfaces shade directly from reconstructed
visibility into advanced opaque HDR. This section must not recreate the classic
GBuffer, deferred-light accumulation, ordinary opaque Forward+, or full-frame
light-combine graph.

#### 11.1 Native Kernel Interface

- [ ] Define a generated/authored kernel interface receiving `AdvancedSurface`,
  material row, view record, light/decal ranges, shadow tables,
  environment/probe data, and GI resources.
- [ ] Define outputs for opaque HDR, dense velocity, temporal/reactive masks,
  exposure/luminance inputs, and only the minimal optional sidecars required by
  later effects.
- [ ] Load textures through material-row references and the active global
  texture-indirection rung.
- [ ] Bind global scene, material, light, and texture tables once per compatible
  command scope.
- [ ] Compile one kernel per material family/layout/feature contract, not per
  material instance.
- [ ] Define explicit missing-kernel, pending-compile, invalid-layout, and
  nonresident-texture behavior.

#### 11.2 Standard Material Families

- [ ] Implement standard opaque PBR first.
- [ ] Add masked PBR using the coverage decision already established by the
  visibility pass.
- [ ] Add unlit/emissive shading.
- [ ] Add subsequent engine-owned families in measured priority order, such as
  skin, cloth, terrain, toon, and hair cards.
- [ ] Define custom-material opt-in metadata and reject undeclared arbitrary
  shader state.
- [ ] Add kernel prewarm and permutation-budget telemetry.

#### 11.3 Clustered Lighting

- [ ] Define one backend-neutral froxel grid per view using screen-tile X/Y and
  depth-slice Z.
- [ ] Build local point- and spot-light lists on the GPU.
- [ ] Keep directional lights in a bounded global list.
- [ ] Share the same light records and froxel indexing across every native
  material kernel.
- [ ] Define overflow and conservative recovery without silently dropping light
  contribution.
- [ ] Add froxel occupancy, light-count, overflow, and selected-light debug
  views.

#### 11.4 Shared Shadow Records

- [ ] Publish directional, point, spot, cascade, atlas, filter, and fallback
  metadata through GPU shadow records instead of large per-program uniform
  sets.
- [ ] Preserve the relevance, dirty-tile, stale-tile, contact-shadow, and
  bounded cascade-update policies established in section 7.
- [ ] Make every material kernel use shared shadow-sampling helpers.
- [ ] Consume reconstructed screen position and depth consistently under normal
  and reversed depth.
- [ ] Publish machine-readable missing, stale, and unavailable shadow fallback
  state.
- [ ] Keep cascade transitions, atlas edges, cubemap seams, filter modes, and
  stereo addressing explicit in the shadow contract.

#### 11.5 Ambient Occlusion

- [ ] Select the advanced AO contract: depth plus reconstructed normal, a
  compact normal sidecar, or provider-specific visibility sampling.
- [ ] Schedule AO before the lighting contribution that consumes it.
- [ ] Do not recreate a multi-channel GBuffer solely for AO compatibility.
- [ ] Adapt supported AO providers to declared advanced inputs.
- [ ] Mark unsupported providers unavailable for the advanced pipeline instead
  of silently invoking legacy resources.
- [ ] Define coordinates, depth convention, half/full resolution, stereo,
  temporal-history, and camera-cut behavior for every supported provider.

#### 11.6 Decals And Surface Modifiers

- [ ] Build per-tile/froxel decal lists.
- [ ] Apply compatible decals as material/surface modifiers before lighting
  using reconstructed position and normal basis.
- [ ] Define decal ordering, blend semantics, normal blending, material
  filters, and overflow.
- [ ] Route geometry-changing or unsupported decals to an explicit special path
  or error state.
- [ ] Do not require classic deferred decal GBuffer writes.

#### 11.7 Environment, Probes, And GI

- [ ] Publish IBL and light-probe lookup through shared GPU records.
- [ ] Define a narrow `IAdvancedGlobalIlluminationProvider` contract for
  radiance/irradiance queries and optional screen-space outputs.
- [ ] Adapt supported probe, surfel, radiance-cascade, voxel, ReSTIR, and future
  providers without full-frame light-combine compositing.
- [ ] Ensure only one selected GI mode contributes unless an explicitly
  documented blend is requested.
- [ ] Expose unavailable providers and required resources before rendering.
- [ ] Define invalid-history, missing-probe, provider-switch, and stereo
  behavior.

#### 11.8 Background And Uncovered Pixels

- [ ] Shade visibility-sentinel pixels through the selected sky/background
  contract.
- [ ] Preserve atmospheric sky inputs without drawing an ordinary opaque
  forward background mesh where a compute/background kernel suffices.
- [ ] Define clear color, alpha, HDR encoding, and external-capture behavior.
- [ ] Keep procedural/custom background geometry as an explicit compatible
  visibility producer or special pass.

#### 11.9 Native Shading Diagnostics

- [ ] Add views for reconstructed albedo, normal, roughness, metalness,
  emission, AO, direct light, indirect light, shadow factor, decal contribution,
  kernel ID, and final opaque HDR.
- [ ] Add a diagnostic difference view against the original pipeline without
  using the original pipeline in production execution.
- [ ] Record GPU time per classification, kernel family, lighting, shadow, AO,
  decal, and GI stage.

### 12. Integrate Transparency, Special Passes, And Post-Processing

Every late draw must declare whether it is temporally participating
transparency, scene-color-dependent refraction, exact transparency/OIT,
volumetric/atmospheric work, a post-temporal overlay, editor/debug/on-top work,
or UI/presentation. Opaque and masked materials may not use these categories
merely because their native kernel is unavailable.

#### 12.1 Late-Pass Eligibility

- [ ] Add explicit material/pass metadata for blend, refraction, order
  dependence, temporal participation, depth-write behavior, and scene-color
  dependency.
- [ ] Remove advanced-pipeline use of `OpaqueForward` and `MaskedForward`.
- [ ] Reject compatible opaque work that attempts to enter a late path.
- [ ] Render unsupported required-mode opaque work with an observable error
  material or fail pipeline selection.
- [ ] Report late-pass counts and reasons per category.

#### 12.2 Scene Color And Depth Contract

- [ ] Publish native opaque HDR, final visibility depth, optional normal/AO
  sidecars, and exposure state under advanced resource names.
- [ ] Create a dedicated scene-color snapshot only when a visible refractive or
  scene-color-dependent pass requires it.
- [ ] Never sample an attachment while writing the same image without a
  supported feedback-loop contract.
- [ ] Preserve depth testing against final visibility depth.
- [ ] Define internal/output resolution and stereo-layer policy for every
  scene-color consumer.

#### 12.3 Transparency And OIT

- [ ] Port weighted blended OIT to native opaque HDR and advanced depth.
- [ ] Port PPLL and depth peeling through declared resources and typed commands.
- [ ] Define which transparent materials use sorted alpha, weighted OIT, PPLL,
  or depth peeling.
- [ ] Preserve shadow, froxel-light, probe, fog, and texture-table access through
  shared GPU records.
- [ ] Define current/previous transform and reactive-mask behavior for
  transparent motion.
- [ ] Add capacity and overflow diagnostics for OIT buffers without same-frame
  readback.

#### 12.4 Special Material Families

- [ ] Classify water, hair, particles, trails, beams, portals, mirrors, and
  custom effects as native visibility, transparent, refractive, volumetric, or
  unsupported.
- [ ] Give required geometry-displacing opaque effects a specialized visibility
  writer plus native material kernel.
- [ ] Keep simulation and update work outside the pipeline command-chain
  builder.
- [ ] Share global tables and avoid per-object descriptor reconstruction.
- [ ] Expose an editor-visible reason for every unsupported special effect.

#### 12.5 Atmosphere And Volumetric Fog

- [ ] Define sky, aerial-perspective, volumetric-fog, transparency, and
  refraction ordering.
- [ ] Adapt atmosphere and fog providers to final visibility depth and native
  HDR.
- [ ] Preserve half-resolution resources and temporal histories through
  declared generation-owned resources.
- [ ] Fog transparent objects consistently without relying on a legacy
  light-combine output.
- [ ] Define camera-cut, underwater/interior, stereo, and disabled-provider
  behavior.

#### 12.6 Dense Motion And Temporal Inputs

- [ ] Consume visibility-reconstructed opaque velocity directly.
- [ ] Merge transparent/special velocity only for participating pixels.
- [ ] Generate disocclusion, reactive, transparency, and invalid-history masks.
- [ ] Preserve exact jitter and motion-vector conventions required by TAA, TSR,
  DLSS, FSR, XeSS, and other active upscalers.
- [ ] Reset history explicitly for resize, pipeline switch, camera cut,
  view-count change, render-scale change, HDR change, and shader/resource
  generation replacement.

#### 12.7 Temporal And Post Chain

- [ ] Place temporal accumulation correctly relative to participating
  transparency and fog.
- [ ] Reconnect motion blur, depth of field, bloom, exposure, tone mapping,
  color grading, vignette, FXAA/SMAA, TSR, and vendor upscalers to advanced
  resource names.
- [ ] Skip disabled passes before resolving their resources or shaders.
- [ ] Preserve HDR/SDR output encoding and alpha behavior.
- [ ] Keep post-temporal overlays and UI outside temporal history.
- [ ] Remove legacy post-process bindings that assume GBuffer or light-combine
  attachment names.

#### 12.8 Late-Pass Diagnostics

- [ ] Add a pass-category overlay and per-category counts.
- [ ] Add views for scene-color snapshot, transparency accumulation/revealage,
  PPLL/depth-peel occupancy, reactive mask, velocity, history validity, fog,
  bloom, exposure, and final output.

### 13. Integrate Stereo, XR, Capture, And Editor Views

Desktop Advanced, RVC-owned OpenXR eyes, and offscreen consumers share logical
scene, mesh, material, GI, temporal, froxel, and post contracts while retaining
independent output-local pipeline instances, resource generations, histories,
and submission topology.

#### 13.1 View-Set Contract

- [ ] Specialize the immutable section-2 `ViewSetPlan` with view count, layer
  mapping, current/previous matrices, jitter, render region, foveation region,
  and output target.
- [ ] Give every view independent visibility, depth pyramid, history validity,
  material work, velocity, and temporal state.
- [ ] Share only view-independent scene, material, animation, deformation,
  light, and immutable-geometry preparation.
- [ ] Define conservative union rules only for work that is genuinely shared
  across views.
- [ ] Never reuse one eye's occlusion or depth verdict as another eye's
  authoritative result.

#### 13.2 Stereo And Multiview

- [ ] Declare layered visibility, depth, optional barycentric, HDR, velocity,
  reactive, and post-process histories.
- [ ] Add required RVC two-pass, OpenGL single-pass-stereo, and Vulkan
  parallel-recording/multiview variants.
- [ ] Add layered classification and native shading with explicit eye/layer
  addressing.
- [ ] Preserve per-eye derivatives, depth conventions, motion, and temporal
  reprojection.
- [ ] Select transparent, fog, atmosphere, shadow, probe, and post resources by
  explicit view/layer identity.
- [ ] Report the selected stereo mode and every structured fallback reason.

#### 13.3 XR Timing And Foveation

- [ ] Preserve runtime wait, begin, acquire, render, release, and end ordering.
- [ ] Fit RVC compute/graphics work into the section-5 deadline scheduler without
  hidden queue or device waits.
- [ ] Represent runtime-provided swapchains and image-array layers as imported
  generation-owned resources.
- [ ] Define foveated and variable-rate visibility/shading behavior without
  invalidating identity reconstruction.
- [ ] Keep periphery derivative and texture-LOD behavior conservative.
- [ ] Preserve late-latching, predicted-pose, motion-vector, and camera-cut
  contracts.
- [ ] Record CPU/GPU timing against the canonical XR budget while identifying
  capture overhead separately.

#### 13.4 Offscreen And Secondary Views

- [ ] Select the advanced pipeline for scene capture, mirror, portal,
  reflection, light probe, impostor, thumbnail, and test viewports through
  capabilities rather than concrete V2 type checks.
- [ ] Define minimal capture profiles that omit unrequested temporal, post, and
  late stages.
- [ ] Define depth-only and visibility-only capture profiles where useful.
- [ ] Preserve external-target ownership, synchronization, and output format.
- [ ] Avoid executing the main-view post chain for probe or shadow captures.
- [ ] Isolate nested and repeated capture resource names and generations.

#### 13.5 Editor Identity And Selection

- [ ] Resolve transform, component, mesh section, material, primitive, meshlet,
  and instance identity from visibility records.
- [ ] Route picking through asynchronous readback or GPU selection queries,
  never a frame-blocking full visibility readback.
- [ ] Preserve outlines, hover, gizmos, bounds, icons, physics debug, and on-top
  overlays.
- [ ] Add an inspector panel for decoded visibility payload and material-kernel
  eligibility.
- [ ] Replace editor checks for `DefaultRenderPipeline` and
  `DefaultRenderPipeline2` with focused provider interfaces.
- [ ] Prevent editor platform windows and previews from reusing stale or
  cross-context pipeline generations.

#### 13.6 Debug And Capture Tooling

- [ ] Register stable capture names for every advanced resource.
- [ ] Add command annotations for every early/late visibility, classification,
  shading, transparency, temporal, post, and output phase.
- [ ] Add MCP-visible settings and state for selected advanced mode, capability
  result, fallback/error reason, and debug view.
- [ ] Capture final advanced output in viewport screenshots without relying on
  legacy diagnostic FBO names.
- [ ] Make visibility payloads, draw records, material work lists, and indirect
  arguments RenderDoc-friendly.
- [ ] Keep delayed profiler readback bounded and explicitly removable from
  benchmark captures.

### 14. Cut Over Production Rendering And Retire Legacy Architecture

Code cutover begins only after the companion testing tracker records passing
correctness, stability, performance, allocation, readback, desktop, offscreen,
and XR evidence for the affected profile.

#### 14.1 Production Cutover

- [ ] Make `AdvancedRenderPipeline` the desktop and applicable offscreen default
  only after its gates pass; promote the RVC-owned OpenXR eye path only after the
  matching XR gates pass.
- [ ] Replace development selectors with the final pipeline-kind setting and
  documented launch/config behavior.
- [ ] Update generated settings, schemas, editor defaults, launch profiles, and
  unit-testing-world setup.
- [ ] Remove every remaining `DefaultRenderPipeline2`, `Default2`, pipeline-V2
  environment variable, diagnostic label, source-path assertion, and
  documentation instruction.
- [ ] Update `README.md`, `docs/README.md`, runtime overview, rendering
  architecture, material authoring, pipeline authoring, MCP, benchmark, and
  launch documentation.
- [ ] Regenerate MCP documentation if tool names or settings change.

#### 14.2 Legacy Retirement

- [ ] Delete deferred/forward resources, shaders, commands, settings, and tests
  that are unreachable after advanced cutover.
- [ ] Delete the original `DefaultRenderPipeline` after every required desktop,
  offscreen, capture, and XR consumer has migrated.
- [ ] If immediate deletion is blocked by a named required consumer, rename it
  to `LegacyDefaultRenderPipeline`, keep it opt-in, record its owner and exact
  blocker in the closeout, and set a dated deletion gate.
- [ ] Do not preserve both architectures through continued symmetric feature
  development.
- [ ] Move completed and superseded TODO material to the repository's historical
  convention and update every canonical link.
- [ ] Update dependency-free legal/product language only where renderer naming
  changes; do not alter licensing policy.

#### 14.3 Closeout

- [ ] Create a progress closeout under `docs/work/progress/rendering/` with the
  architecture summary, feature matrix, validation commands, images/captures,
  performance tables, remaining risks, and legacy-deletion status.
- [ ] Keep `Build/_AgentValidation/` within its ten-run-root limit and remove
  unneeded disposable evidence.
- [ ] Ensure tracked documentation does not depend on ignored evidence for
  required behavior.
- [ ] Mark the consolidated program complete only after no required work
  remains.

#### 14.4 Program Completion

- [ ] Make `AdvancedRenderPipeline` the desktop production default and keep
  production OpenXR eye output owned by `RvcRenderPipeline`.
- [ ] Route compatible opaque and masked rendering through visibility plus
  native material/lighting shading.
- [ ] Remove the classic GBuffer, deferred light accumulation, ordinary opaque
  Forward+, and light-combine stages from the advanced production graph.
- [ ] Remove `DefaultRenderPipeline2` completely.
- [ ] Delete the original default pipeline or retain exactly one explicit,
  bounded legacy blocker with an owner and dated removal gate.
- [ ] Meet the target architecture's facade, lifecycle-spine, complete Vulkan
  source/line, directory-depth, file-size, method-size, dependency-direction,
  and single-authority budgets from a reproducible final inventory.
- [ ] Demonstrate zero warmed managed hot-path allocation and approved desktop,
  presentationless, and XR p50/p95/p99/worst CPU budgets without moving cost to
  waits, retirement, descriptors, another output, or tail latency.
- [ ] Demonstrate that every hot stream has one canonical measured layout, no
  unconsumed compatibility extraction/conversion pass remains, and bytes touched
  scale with active stage work rather than broad record size or registry size.
- [ ] Demonstrate that data-oriented layouts reduced or preserved source files,
  runtime owners, Vulkan allocations, descriptor bindings, and lifetime
  transitions instead of moving complexity into per-column infrastructure.
- [ ] Demonstrate that unsafe code is confined to audited native/mapped-memory
  owners, safe span-based implementations remain the default when equivalent,
  and all retained unsafe paths pass lifetime, bounds, alignment, concurrency,
  and end-to-end performance gates.
- [ ] Demonstrate one correlated CPU lifecycle tree whose detailed captures
  attribute at least 99% of frame-root time, identify every gap of 50
  microseconds or more, and distinguish exclusive work, waits, driver/external
  time, worker overlap, and the required-output critical path.
- [ ] Demonstrate that a developer can locate every lifecycle owner and explain
  a retained slow frame from the frame spine, editor profiler, and exported
  trace without reconstructing state across `VulkanRenderer` partial files.

## Superseded Trackers

This tracker replaces the following implementation TODOs; their validation and
test work is consolidated in the companion testing tracker.

- Vulkan frame-plan recording refactor
- Vulkan primary command-recording fast path
- Forward+ prepass and render-graph cost
- Occlusion systems performance
- Render tail latency: shadows, streaming, and jobs
- Vulkan runtime code organization: remaining small-facade, source-surface, and
  ownership debt after the 2026-07-30 extraction milestone
- Architectural refactor 06: visible material work classification
- Architectural refactor 07: native material, lighting, decals, and GI
- Architectural refactor 08: transparency, special passes, and post-processing
- Architectural refactor 09: stereo, XR, capture, and editor integration
- Architectural refactor 10: validation, performance, cutover, and retirement
