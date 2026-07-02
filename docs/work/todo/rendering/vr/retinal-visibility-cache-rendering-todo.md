# Retinal Visibility Cache Rendering TODO

Last Updated: 2026-07-01
Owner: Rendering / XR
Status: Proposed
Target Branch: `rendering-rvc-quad-view-foundation`

Design source:

- [Retinal Visibility Cache Rendering Design](../../../design/rendering/retinal-visibility-cache-rendering-design.md)
- [Nanite Macro Rendering Overview](https://www.elopezr.com/a-macro-view-of-nanite/)
- [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md)
- [OpenXR Future Work TODO](openxr-future-work-todo.md)
- [VR Rendering Performance Contract TODO](../optimization/vr-rendering-performance-contract-todo.md)
- [Visibility Buffer Rendering TODO](../optimization/visibility-buffer-rendering-todo.md)
- [GPU Meshlet Zero-Readback Rendering Design](../../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [Vulkan Descriptor Heap Optimization Design](../../../design/rendering/vulkan-descriptor-heap-optimization-design.md)
- [Production Rendering Pipeline Roadmap](../gpu/production-rendering-pipeline-roadmap.md)

## Goal

Build Retinal Visibility Cache (RVC) as the high-end opaque VR renderer for
OpenXR quad-view/foveated headsets. The renderer should keep per-view visibility
authoritative while sharing material evaluation and stable lighting work across
wide views, inset views, and stereo eyes where the surface match is safe.

The first deliverable is not the cache. The first deliverable is a
view-count-agnostic quad-view Forward+ baseline that can act as the correctness
oracle for later visibility, shadelet, and shared-lighting stages.

## Scope

- `RenderFrameViewSet` and view-count-agnostic renderer plumbing.
- OpenXR quad-view capability detection, session selection, per-view swapchains,
  and foveated/non-foveated view configuration data.
- Four-view Forward+ baseline with per-view diagnostics and stereo fallback.
- Frame-graph/resource-lifetime foundation required before RVC pass work.
- Opaque visibility buffers, material reconstruction, shadelets, and per-view
  resolve.
- Descriptor heap backed Vulkan material/resource tables for shadelet and
  shared-lighting resources, with descriptor indexing as fallback.
- Head-space light clustering, light aggregation, and optional reservoir-backed
  shared lighting.
- Inset/wide, stereo, and temporal shadelet reuse with conservative validation.
- Transparent clustered Forward+ overlay and existing fallback renderer paths.
- GPU/debug visualizers, profiler counters, and XR runtime validation.

## Non-Goals

- Do not make RVC the first implementation slice.
- Do not remove Forward+ as the transparent companion and correctness fallback.
- Do not require every material shader to support compute-side reconstruction
  before a Vulkan fragment-shading-rate foveation fast path can ship.
- Do not hide unsupported quad-view, multiview, fragment shading rate, or RVC
  paths behind silent fallbacks. Report the active path and reason for fallback.
- Do not make desktop OpenGL multiview the architecture target. OpenGL can
  prototype safe correctness slices; Vulkan is the full RVC target.
- Do not design RVC around future backend parity. Use Vulkan extensions where
  they make the renderer cleaner or faster, and report missing feature support
  visibly.
- Do not fork `DefaultRenderPipeline` wholesale. Shared view-set plumbing belongs
  in the existing path first; Stage 2 onward should introduce a sibling
  `RvcRenderPipeline` backed by explicit frame-graph resources.

## Phase 0 - Branch, Baseline, And Contracts

- [ ] Create dedicated branch `rendering-rvc-quad-view-foundation`.
- [ ] Confirm and name target validation scenes so later phases can reference
  them precisely: `DesktopMono`, `Stereo` (OpenVR/OpenXR), `OpaqueDense`,
  `AvatarMaterialDiverse`, `TransparencyFallback`, and `QuadView` (runtime or
  simulator lane where available).
- [ ] Capture current Forward+ reference images and profiler captures for stereo
  and any existing foveated `RenderCommandCollection` ViewSet path.
- [ ] Define the performance baseline RVC must beat as foveated Forward+
  (quad views + fragment shading rate + visibility-mask stenciling), not
  uniform Forward+, and record its unique-fragment-invocation counts for the
  target validation scenes.
- [ ] Inventory current OpenXR runtime support for
  `XR_VIEW_CONFIGURATION_TYPE_PRIMARY_QUAD_VARJO`,
  `XR_VARJO_foveated_rendering`, depth layers, visibility masks, multiview, and
  Vulkan fragment-shading-rate features.
- [ ] Inventory Vulkan descriptor heap support alongside descriptor indexing so
  RVC can report the selected material/resource binding backend before cache
  work begins.
- [ ] Inventory Vulkan `VK_EXT_fragment_density_map` support alongside fragment
  shading rate so the foveation-rate abstraction stays expressible as either a
  shading-rate image or a fragment density map.
- [ ] Stand up the quad-view emulation lane with the Quad-Views-Foveated OpenXR
  API layer so `QuadView` validation runs on non-Varjo hardware, including
  eye-tracked inset movement and runtime inset-boundary blending.
- [ ] Define the `RenderFrameViewSet` contract: stable view identity, parent
  eye, wide/inset relationship, projection, viewport, recommended image size,
  foveation metadata, previous-view state, and mirror/debug views.
- [ ] Define per-view diagnostics: view count, view role, runtime-reported size,
  FOV, swapchain identity, pixel count, GPU timing, stereo mode, foveation mode,
  and fallback reason.
- [ ] Define settings for quad-view enablement, RVC enablement, stereo reuse,
  and diagnostic overlays. Default risky or unvalidated cache reuse off.

### Quality Tolerances

Define these once so the "match Forward+ within tolerance" gates in later
phases are testable rather than subjective.

- [ ] Pick the comparison metric(s) and per-region thresholds against the
  Forward+ oracle: a stricter foveal/guard-band bound and a looser
  mid-field/periphery bound (for example per-pixel max error, SSIM, and
  FLIP/perceptual error). Record the exact numbers.
- [ ] Specify where tolerances are configured (settings/test fixtures) and which
  validation scenes each tolerance applies to.
- [ ] Specify how a comparison is captured deterministically: fixed camera/gaze,
  fixed frame, warm-cache policy, and identical scene state for both renderers.

### Diagnostics And Reporting Surface

Define the single reporting surface that every later "report visibly" / "visible
fallback" / "report missing capability" item targets, to honor the no-silent-
fallback rule.

- [ ] Choose the report channel(s): log channel, ImGui diagnostics overlay,
  profiler counters, and/or MCP-readable render state. Specify which fact goes
  to which channel.
- [ ] Define a single fallback-reason enum/string shared by view-count,
  quad-view, pipeline, material, and backend-capability fallbacks so reasons are
  consistent and machine-readable.
- [ ] Confirm all counters are written to GPU buffers and read back
  double-buffered several frames late; never synchronously in the render loop.

### A/B Validation Harness Contract

Define the harness up front; it is a first-class deliverable in Phase 6 and the
gate for enabling stereo reuse, but multiple phases reference it.

- [ ] Define harness inputs: a fixed validation scene, fixed camera/gaze, and a
  single toggle that renders the same frame with cross-view reuse on versus
  per-view shading off, with all other state identical.
- [ ] Define harness outputs: side-by-side images, per-region error metrics
  (reusing the Quality Tolerances thresholds), and the reuse counters.
- [ ] Define the pass/fail rule for "no perceptible difference": the metric
  threshold that must hold, plus any required human-review step and the content
  set it was validated against.

Acceptance criteria:

- [ ] The branch exists and the first slice has a documented runtime matrix,
  baseline captures, and view-set contract before renderer plumbing changes.
- [ ] Quality tolerances, the diagnostics/reporting surface, and the A/B harness
  contract are documented before the phases that depend on them begin.

### Open Decisions To Resolve First

These choices block Phases 1-2 and should be settled (and recorded in the
design doc) before the corresponding implementation work begins. Full list in
the [design open questions](../../../design/rendering/retinal-visibility-cache-rendering-design.md).

- [ ] Decide where `RenderFrameViewSet` lives: runtime rendering abstractions or
  the OpenXR layer with a renderer-facing adapter (blocks Phase 1).
- [ ] Decide which frame-graph implementation backs `RvcRenderPipeline`:
  engine-owned or an existing abstraction (blocks Phase 2).
- [ ] Decide the first material class admitted into RVC: unlit, opaque PBR, or
  generated material-table shaders only (blocks Phase 3).
- [ ] Decide the RVC resource binding contract: descriptor heap as the preferred
  Vulkan top rung, descriptor indexing as fallback, and no shadelet keys that
  depend on backend descriptor-set handles.
- [ ] Decide the shadelet cache key basis: primitive barycentrics, UVs,
  world-space position, or a hybrid (blocks Phase 4/8).
- [ ] Decide whether the shared lighting cache adopts reservoirs at Phase 5 or
  defers them to Phase 8.
- [ ] Decide how much existing Forward+ tile infrastructure is reused for
  head-space clusters (informs Phase 5).
- [ ] Decide the shared light grid space: truly head-anchored,
  orientation-snapped, or world-aligned with a camera-relative origin, given
  cluster-ID churn under head rotation (blocks Phase 5, interacts with Phase 8
  temporal reuse).
- [ ] Decide which RVC controls are editor quality settings versus backend-only
  renderer policy.

## Phase 1 - View-Set Foundation And Quad-View Forward+ Baseline

- [ ] Replace renderer assumptions of exactly one camera or exactly two VR eyes
  with `RenderFrameViewSet` consumption where frame views are selected.
- [ ] Preserve current mono, stereo, OpenVR, OpenXR, editor preview, and desktop
  mirror behavior.
- [ ] Enumerate OpenXR view configurations before `xrBeginSession` and select
  `XR_VIEW_CONFIGURATION_TYPE_PRIMARY_QUAD_VARJO` only when supported and
  enabled.
- [ ] Treat runtime-reported view count as authoritative. Do not hard-code two
  views in OpenXR swapchain, projection, view-state, or submission code.
- [ ] Maintain foveated-active and non-foveated `XrViewConfigurationView` data,
  or an equivalent oversized viewport layout, so gaze availability can choose
  the correct set without invalid dimensions.
- [ ] Allocate and submit swapchains for every reported view.
- [ ] Stencil each view's `XR_KHR_visibility_mask` hidden-area mesh before
  rendering and handle `XrEventDataVisibilityMaskChangedKHR`.
- [ ] Render all reported views through the existing Forward+ path.
- [ ] Carry per-view projection, viewport, previous-view state, and timing into
  the frame profile.
- [ ] Validate that the wide view remains valid under inset regions.
- [ ] Keep stereo fallback green and visibly report why quad-view was not used.

Acceptance criteria:

- [ ] Four reported views render and submit correctly on supporting runtimes or
  test harnesses.
- [ ] Mono and stereo fallback modes still render unchanged.
- [ ] Profile captures report per-view timings, pixel counts, stereo mode,
  foveation mode, and fallback reasons.

## Phase 2 - Frame Graph And RVC Pipeline Skeleton

- [ ] Add the frame-graph/resource-lifetime foundation needed by RVC before
  visibility-buffer work begins.
- [ ] Model per-view, layered, and transient resources explicitly: depth,
  visibility, velocity, pixel-to-shadelet maps, material shadelets, shared
  lighting, transparency targets, post targets, and mirror/debug outputs.
- [ ] Define resource aliasing rules and backend barriers for OpenGL prototype
  paths and Vulkan production paths.
- [ ] Introduce `RvcRenderPipeline` as a sibling pipeline selectable by setting,
  while sharing scene, culling, material table, light buffer, and Forward+
  fallback services.
- [ ] Share the renderer descriptor heap/material resource table service rather
  than creating RVC-specific descriptor set pools or duplicate texture tables.
- [ ] Keep the existing Forward+ path as a pixel-for-pixel correctness oracle.
- [ ] Add pipeline-level fallback when required frame-graph, visibility target,
  or backend capabilities are missing.

Acceptance criteria:

- [ ] RVC can be selected as an explicit pipeline mode and can fall back with
  visible diagnostics before any cache-specific shading is implemented.
- [ ] RVC resources have explicit lifetimes, dependencies, and debug names.

## Phase 3 - Opaque Visibility Buffer Baseline

- [ ] Add per-view opaque depth and visibility targets.
- [ ] Choose a correctness-first visibility payload format, preferring RG32 or
  equivalent 64-bit identity until packed 32-bit limits are proven safe.
- [ ] Encode enough identity to recover instance, meshlet or draw, primitive or
  local triangle, material, transform, and editor selection data.
- [ ] Define visible fallback behavior for payload overflow, unsupported formats,
  unsupported materials, and backend capability gaps.
- [ ] Add static mesh, skinned mesh, zero-readback indirect, and meshlet
  visibility paths where those source paths are already production-capable.
- [ ] Reconstruct attributes from visibility identity: position, normal, tangent,
  UV, material row, previous position, and velocity inputs.
- [ ] Keep visibility payloads backend-neutral: store draw/material identity,
  not descriptor set handles or backend descriptor objects.
- [ ] Route alpha-test materials through the visibility path only when coverage
  is deterministic and cheap; send expensive alpha logic to Forward+ fallback.
- [ ] Keep transparent, refractive, glass, water, particles, and order-dependent
  materials on clustered Forward+.
- [ ] Add visualizers for depth, visibility ID, primitive ID, material ID,
  reconstruction error, and fallback classes.
- [ ] Define the conservative HZB contract for RVC visibility: previous/early
  depth may reject only when reprojection and dynamic-object state are safe.
- [ ] Render wide-view visibility before same-eye inset visibility and seed the
  inset HZB from wide depth (exact zero-parallax remap since both views share
  one center of projection); record wide-versus-inset depth agreement for
  later reuse validation.
- [ ] Add a current-frame HZB/post-validation pass for newly visible,
  uncertain, HZB-edge, or cross-view-disagreement candidates.
- [ ] Split visibility candidates by execution lane: hardware raster first,
  meshlet/mesh-shader expansion where supported, and later tiny-triangle
  software raster only after profiling proves it useful.
- [ ] Record visible, culled, uncertain, post-pass, page-request, and raster-lane
  counters through delayed GPU readback.

Acceptance criteria:

- [ ] The `OpaqueDense` scene under `DesktopMono` and `Stereo` matches Forward+
  within the per-region Quality Tolerances defined in Phase 0.
- [ ] Unsupported material classes fall back visibly and correctly.
- [ ] Visibility output can be inspected in RenderDoc or equivalent tooling and
  every visible opaque pixel maps to a valid source record.
- [ ] Rapid head-motion and stereo disocclusion validation does not show
  one-eye holes from stale HZB rejection.

## Phase 4 - Foveated Shadelets And Material Cache

- [ ] Generate shadelet keys from visibility, material class, quantized surface
  location, LOD bucket, deformation version, and foveation region.
- [ ] Build per-view pixel-to-shadelet maps.
- [ ] Support 1x1, 2x2, 4x4, and 8x8 shadelet densities.
- [ ] Add a Vulkan `VK_KHR_fragment_shading_rate` foveation fast path for
  materials not yet ported to compute-side material reconstruction. Desktop
  fragment shading rate caps at 4x4, so 8x8 densities require the compute
  shadelet path; express near-UI/hand 1x1 overrides through shading-rate
  combiner ops.
- [ ] Deduplicate shadelets hierarchically: tile-local shared-memory dedup
  first, then a global merge of tile survivors, to avoid global atomic
  contention.
- [ ] Encode per-view pixel-to-shadelet maps with per-tile indirection
  (per-tile base offset plus 16-bit local index) to halve map bandwidth.
- [ ] Ship a counters-only reuse telemetry mode that reports intra-view,
  inset/wide, and cross-eye shadelet key-match ceilings without changing
  shading, as the evidence gate for Phase 6.
- [ ] Force the wide-view region under the inset (minus a blend guard band) to
  the coarsest shadelet rate with no per-view specular; the runtime only
  needs plausible data there for edge blending.
- [ ] Sort or bin shadelets by material before shading to avoid divergent
  compute-side material evaluation.
- [ ] Store material row IDs/resource generations in shadelet records and load
  descriptor heap resource/sampler indices from GPU-visible material rows.
- [ ] Keep descriptor indexing rows semantically identical to descriptor heap
  rows so the fallback path validates the same material/shadelet logic.
- [ ] Implement analytic derivatives or another documented derivative strategy
  for texture LOD and normal mapping.
- [ ] Add edge-aware rejection at depth, normal, material, primitive, and
  disocclusion boundaries.
- [ ] Add caps and visible fallback behavior for shadelet-map memory pressure or
  deduplication overflow.
- [ ] Add shadelet density, material-bin occupancy, cache-miss, and overflow
  overlays/counters.

Acceptance criteria:

- [ ] Foveal regions remain visually equivalent to per-pixel resolve.
- [ ] Mid-field and peripheral regions shade fewer unique samples than visible
  pixels on opaque-heavy scenes.
- [ ] Depth/material boundaries do not show obvious block artifacts.

## Phase 5 - Shared Head-Space Light Clusters

- [ ] Build a shared head-space cluster grid for the full frame view set,
  implementing the Phase 0 grid-space decision (orientation snapping or
  world-aligned camera-relative origin) so stationary surfaces keep stable
  cluster IDs under head rotation.
- [ ] Map shadelets to head-space clusters instead of rebuilding independent
  light lists per view.
- [ ] Reuse existing Forward+ light metadata and buffers where layout and
  lifetime make that safe.
- [ ] Store shadow maps, cookies, probes, and clustered-light buffers as
  heap-backed resource references where Vulkan descriptor heap is active.
- [ ] Keep the old per-view Forward+ tile grid as a debug comparison and
  fallback.
- [ ] Add foveation-specific light budgets for fovea, guard band, mid-field, and
  periphery.
- [ ] Add overlays for cluster occupancy, exact-light count, rejected lights,
  and per-view lighting fallback.

Acceptance criteria:

- [ ] Exact-light shared-cluster mode matches per-view Forward+ lighting within
  tolerance.
- [ ] Quad-view scenes avoid rebuilding equivalent light lists independently for
  all four views.

## Phase 6 - Inset/Wide And Stereo Shadelet Reuse

- [ ] Gate this phase on the Phase 4 telemetry ceilings; descope stereo reuse
  with evidence if measured cross-eye key-match rates on target content are
  low.
- [ ] Share shadelets between wide and inset views for the same eye when
  primitive, surface location, material, normal, LOD, and deformation thresholds
  agree.
- [ ] Share material shadelets between eyes only when the match is conservative:
  primitive, barycentric or world-space key, material, normal, roughness bucket,
  deformation version, and LOD all pass validation.
- [ ] Include material/resource generation in reuse validation so stale heap
  references invalidate shadelets instead of being shared across views.
- [ ] Exclude parallax occlusion, virtual displacement, refraction, sharp
  specular, and other strongly view-dependent materials from stereo reuse unless
  they have an explicit safe key.
- [ ] Keep sharp specular and reflection correction per view, especially in the
  fovea and guard band.
- [ ] Measure the specular-sharing roughness threshold per material class
  through the A/B harness instead of hard-coding 0.35.
- [ ] Default `RvcStereoReuseEnabled` off until the validation harness is green.
- [ ] Build the A/B harness to the Phase 0 contract (per-view shading versus
  cross-view reuse, shared toggle, tolerance-based pass/fail).
- [ ] Add counters for intra-view reuse, inset/wide reuse, stereo reuse,
  temporal reuse, rejected attempts, and disocclusion-local shading.

Acceptance criteria:

- [ ] Measured unique-shadelet count `S < 0.5 * sum(P_i)` on opaque-heavy test
  scenes (target range roughly 20-35% of total view pixels) and below the
  Phase 0 foveated Forward+ unique-invocation baseline, adopted as the exit
  criterion instead of the vaguer "beats Forward+".
- [ ] Opaque-heavy test scenes show material/stable-light reuse across views.
- [ ] The A/B harness shows no perceptible difference on the validated content
  set before stereo reuse is enabled by default.
- [ ] Specular highlights remain eye-correct in foveal regions.

## Phase 7 - Peripheral Light Aggregation And Reservoir Evaluation

- [ ] Generate top-K exact lights per head-space cluster.
- [ ] Compress lower-impact lights into an aggregate representation for
  mid-field and peripheral shadelets.
- [ ] Evaluate reservoir-based shared lighting before hand-rolling long-lived
  temporal light accumulation.
- [ ] Evaluate stereo reuse as reservoir combination where eye agreement is
  imperfect.
- [ ] Add aggregate contribution, reservoir weight, exact-vs-aggregate, and
  energy-error overlays.
- [ ] Add fixtures that verify aggregate light energy and reservoir combination
  behavior.

Acceptance criteria:

- [ ] Many-small-light scenes no longer spike peripheral lighting cost.
- [ ] Aggregate lighting remains stable during head motion and gaze changes.
- [ ] Reservoir paths are either adopted with tests or rejected with documented
  evidence.

## Phase 8 - Temporal Cache, Foveal Latency, And Resolve

- [ ] Add persistent shadelet cache entries for static or diffuse-friendly
  surfaces.
- [ ] Store cache confidence and age so peripheral reuse can degrade smoothly.
- [ ] Evaluate a world-space hash-grid temporal store for stability across LOD
  and topology changes.
- [ ] Invalidate on material changes, animation/deformation version changes,
  LOD changes, shadow caster set changes, view-set changes, and gaze-region
  changes.
- [ ] Decide the foveal anti-aliasing path explicitly: evaluate
  ID-discontinuity edge AA from the visibility buffer as the foveal path,
  with foveated TAA as the fallback rather than the default assumption.
- [ ] Add foveated TAA/reprojection and edge-aware upsampling that understand
  wide/inset view identity.
- [ ] Investigate late foveal inset refresh on Vulkan without violating OpenXR
  frame timing.
- [ ] Late-latch the foveation center through buffer-device-address constants
  written at submit time; never rebuild command buffers for gaze updates.
- [ ] Exploit saccadic suppression: drop foveal refresh quality during detected
  saccades and land quality at the predicted gaze endpoint.
- [ ] Evaluate optional cross-domain extensions as follow-ups behind the
  hand-tuned fallback: saccade-predicted guard-band widening, learned peripheral
  reconstruction, and peripheral checkerboard/quad-rotation sampling.
- [ ] Add diagnostics for stale cache usage, confidence, age, invalidation
  reason, temporal hit rate, and foveal stale-shading rejection.

Acceptance criteria:

- [ ] Static scenes show temporal cache hit rate without visible ghosting.
- [ ] Gaze movement does not expose stale low-quality shading in foveal regions.
- [ ] Wide/inset resolve behaves correctly in desktop mirror and XR submission.

## Phase 9 - Production Vulkan Hardening

- [ ] Validate Vulkan multiview, dynamic rendering, descriptor heap, descriptor
  indexing fallback, fragment shading rate, synchronization2, timeline
  semaphore handoff, explicit barriers, and OpenXR Vulkan swapchain image
  integration.
- [ ] Validate `VK_EXT_mesh_shader` for meshlet expansion where supported and
  keep the Vulkan indirect/compute meshlet path as the visible fallback.
- [ ] Keep OpenGL limited to correctness slices and visibly report missing RVC
  production capabilities.
- [ ] Remove any RVC roadmap or API constraints that exist only for future
  backend parity.
- [ ] Add source-contract tests for view-set packing, view enumeration,
  visibility payload packing, shadelet hashing, shader layout compatibility,
  descriptor backend selection, heap-backed material resource generations, HZB
  post-pass routing, cluster aggregation, reservoir math, and temporal hash-grid
  lookup.
- [ ] Add GPU validation flows using MCP screenshots, logs, and RenderDoc
  captures for visibility, shadelet maps, shared lighting, and final resolve.
- [ ] Document any final payload format, settings, diagnostics, and fallback
  behavior in architecture/developer docs.

Acceptance criteria:

- [ ] RVC has a documented support matrix by runtime and Vulkan feature set.
- [ ] Unsupported production Vulkan paths fail visibly with actionable
  diagnostics.
- [ ] The validated Vulkan path can be benchmarked against quad-view Forward+
  with comparable settings and warm-cache policy.

## Final Validation And Merge

- [ ] Run targeted unit and source-contract tests for view sets, OpenXR view
  enumeration, visibility payloads, shadelet keys, material layouts, cluster
  aggregation, reservoir math, and temporal cache lookup.
- [ ] Run desktop mono and stereo regression paths.
- [ ] Run OpenVR/OpenXR smoke where hardware and runtime are available.
- [ ] Run quad-view runtime or simulator validation where available.
- [ ] Confirm OpenXR missed-deadline/frame-timing counters do not regress in
  mono, stereo, and quad-view fallback modes.
- [ ] Capture side-by-side Forward+ versus RVC images and profiler captures for
  all target validation scenes.
- [ ] Capture RenderDoc analysis for visibility, shadelet generation,
  descriptor heap/resource table state where available, shared lighting,
  transparency overlay, and final resolve on Vulkan.
- [ ] Update design, architecture, launch/settings, and developer docs for any
  final API, setting, runtime, or workflow changes.
- [ ] Merge branch `rendering-rvc-quad-view-foundation` back into `main` after
  implementation, validation, and documentation updates are complete.
