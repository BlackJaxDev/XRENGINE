# Retinal Visibility Cache Rendering Validation Plan

Last Updated: 2026-07-03
Owner: Rendering / XR
Status: Implementation Surfaces Ready, Validation Evidence Pending
Target Branch: `rendering-rvc-quad-view-foundation`

This document is now the validation plan for Retinal Visibility Cache (RVC).
Implementation scope and design rationale live in the architecture document:

- [Retinal Visibility Cache Rendering](../../../../architecture/rendering/retinal-visibility-cache-rendering.md)

Use this plan to prove that the implemented RVC contracts, render-graph
resources, renderer capability hooks, OpenXR quad-view plumbing, visibility-mask
support, and diagnostic paths behave correctly on real desktop, OpenVR, OpenXR,
quad-view, and Vulkan configurations.

Do not check a validation item until the named evidence exists. Acceptable
evidence includes source-test output, build logs, runtime logs, MCP screenshots,
RenderDoc captures/exports, profiler traces, benchmark tables, and linked
durable notes under `docs/work/`.

## Evidence Rules

- Store disposable validation output under `Build/_AgentValidation/<run>/`.
- Record the exact command, branch, commit/worktree state, runtime, GPU, driver,
  render API, headset/runtime, scene, settings, and date for every run.
- Compare RVC against the foveated Forward+ oracle where quad/foveated support
  exists. Uniform Forward+ is useful as a secondary reference, not the target
  baseline.
- Treat a silent fallback as a failure. Missing support must appear in logs,
  counters, frame profiles, or overlays with an actionable reason.
- Keep GPU counter readback delayed. Any synchronous render-loop readback path
  is a failure unless explicitly whitelisted for a one-off diagnostic run.
- Preserve the raw capture or log and link its path from the relevant checklist
  note before checking the item.

## Implemented Surface Under Test

The current branch contains code surfaces for:

- `RenderFrameViewSet` view identity, quad wide/inset roles, foveation metadata,
  previous state, mirror/debug views, and per-view diagnostics.
- RVC settings, `RvcRenderPipeline` selection, capability resolution, visible
  Forward+ fallback, and engine stats hooks.
- OpenXR active view-configuration selection, stereo/quad view snapshots,
  max-view swapchain storage, per-view frame-profile publication, and
  `XR_KHR_visibility_mask` mesh fetch state.
- RVC frame-graph resources for per-view depth, visibility, velocity, HZB,
  reconstruction error, pixel-to-shadelet maps, transparency, final resolve,
  mirror debug, and shared buffers.
- `VPRC_RvcPass` graph stages for OpenXR mask stencil, visibility,
  reconstruction, HZB, shadelets, foveated shading rate, shared lighting,
  temporal cache, transparency, resolve, and diagnostics.
- Renderer capability hooks for descriptor backend selection, material resource
  table support, visibility source paths, OpenXR visibility-mask stencil, and
  Vulkan production features.
- Source contracts and tests for visibility payloads, shadelet keys, reuse,
  reservoirs, temporal hashing, fallback decisions, and RVC wiring.

The graph stages intentionally warn while backend shader dispatch is not linked.
That warning is acceptable for the foundation slice and becomes a failure for
any validation run that claims a production RVC GPU stage is implemented.

## Preflight Evidence

- [x] RVC source-contract tests pass.
  - Command: `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter RvcRenderingContractTests -v:minimal`
  - Latest result: 20 passed, 0 failed.
  - Known unrelated warnings: existing `Magick.NET-Q16-HDRI-AnyCPU` NuGet
    vulnerability warnings.
- [x] Runtime rendering project builds after RVC code-surface implementation.
  - Command: `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -v:minimal`
  - Known unrelated warnings: existing `Magick.NET-Q16-HDRI-AnyCPU` NuGet
    vulnerability warnings.
- [ ] Capture the current branch/worktree summary before validation.
  - Include `git branch --show-current`, `git status --short`, and a concise
    list of changed RVC files.
- [ ] Create a validation run root under `Build/_AgentValidation/`.
  - Recommended shape:
    `Build/_AgentValidation/<yyyyMMdd-HHmmss>-rvc-validation/`
  - Include `logs/`, `mcp-captures/`, `mcp-output/`, `renderdoc/`,
    `reports/`, and `scratch/`.

## Phase 1 - Runtime And Baseline Inventory

Goal: establish the runtime matrix and Forward+ oracle evidence that every RVC
quality and performance claim compares against.

- [ ] Inventory OpenXR runtime support.
  - Record runtime name/version and enabled instance extensions.
  - Probe `XR_VIEW_CONFIGURATION_TYPE_PRIMARY_QUAD_VARJO`.
  - Probe `XR_VARJO_foveated_rendering`.
  - Probe `XR_KHR_visibility_mask`, depth layers, multiview, and Vulkan interop
    support.
  - Save the support matrix and link it from the RVC architecture doc or a
    durable `docs/work/` note.
- [ ] Inventory Vulkan production feature support.
  - Record descriptor heap or descriptor buffer support, descriptor indexing,
    fragment shading rate, fragment density map, synchronization2, dynamic
    rendering, multiview, mesh shader, and timeline semaphore support.
  - Verify missing support is diagnostic, not silently downgraded.
- [ ] Stand up the quad-view emulation lane.
  - Install/configure Quad-Views-Foveated OpenXR API layer or equivalent.
  - Verify the engine sees four views on non-Varjo hardware.
  - Exercise eye-tracked inset movement and runtime inset-boundary blending.
  - Record setup notes and required environment variables.
- [ ] Capture Forward+ desktop mono reference.
  - Scene: `DesktopMono`.
  - Evidence: screenshot, runtime logs, render stats, settings dump, scene hash.
- [ ] Capture Forward+ stereo reference.
  - Scene: `Stereo` in the stable VR path.
  - Evidence: left/right captures, desktop mirror, runtime logs, render stats,
    settings dump, scene hash.
- [ ] Capture foveated or quad Forward+ reference where available.
  - Capture foveation-on and foveation-off runs.
  - Evidence: all submitted views, desktop mirror, profiler data, frame logs.
- [ ] Define the numeric performance baseline.
  - Record submitted pixels, unique fragment invocation counts, GPU pass timing,
    CPU command build time, and missed-deadline counters for `OpaqueDense`,
    `AvatarMaterialDiverse`, `TransparencyFallback`, and `QuadView`.

Exit evidence:

- [ ] Runtime support matrix exists and is linked.
- [ ] Vulkan feature matrix exists and is linked.
- [ ] Forward+ desktop, stereo, and any available foveated/quad references exist.
- [ ] RVC comparison baseline is foveated Forward+ with recorded counters.

## Phase 2 - Mode And Fallback Regression

Goal: prove that selecting RVC does not break existing paths and that unsupported
paths fall back visibly.

- [ ] Validate desktop mono with RVC off.
  - Confirm image, frame logs, and render stats match the current default path.
- [ ] Validate desktop mono with `RvcPipelineMode=ForwardPlusOracle`.
  - Confirm it uses Forward+ intentionally and reports no fallback error.
- [ ] Validate desktop mono with cache modes requested on unsupported backends.
  - Confirm visible fallback reason and no silent success.
- [ ] Validate OpenVR stereo behavior.
  - Confirm left/right rendering, desktop mirror, frame timing, and settings
    remain stable.
- [ ] Validate OpenXR stereo behavior.
  - Confirm `xrBeginSession`, `xrLocateViews`, swapchain acquire/release, and
    submission use the active stereo view configuration.
- [ ] Validate editor preview and desktop mirror behavior.
  - Confirm preview texture selection stays stable when quad-view storage exists.
- [ ] Validate OpenGL correctness-slice behavior.
  - Confirm visibility/debug slices can be selected where supported.
  - Confirm full production RVC on OpenGL fails visibly with
    `UnsupportedOpenGlProductionPath` or equivalent diagnostics.

Exit evidence:

- [ ] Mono, stereo, OpenVR, OpenXR stereo, editor preview, and mirror paths still
  render.
- [ ] Every unsupported RVC request reports a machine-readable fallback reason.
- [ ] OpenGL is documented in logs as a correctness slice, not production parity.

## Phase 3 - OpenXR Quad-View And Visibility Mask Validation

Goal: prove runtime-reported view count, quad roles, foveated view dimensions,
and visibility masks behave correctly.

- [ ] Validate active OpenXR view configuration selection.
  - Confirm quad view is selected only when enabled and runtime-supported.
  - Confirm stereo fallback records why quad was not selected.
- [ ] Validate four reported views render and submit.
  - Capture all submitted views and desktop mirror.
  - Confirm view indexes 2 and 3 use the runtime FOV/pose and viewport size
    while sharing the existing eye-family scene rig.
- [ ] Validate moving inset behavior.
  - Capture static gaze and moving-gaze screenshots.
  - Confirm wide views remain valid under moving inset regions.
- [ ] Validate swapchain image lifecycle.
  - Confirm every acquired image is released on success and render failure.
  - Confirm no per-view image-count or framebuffer array overrun.
- [ ] Validate `XR_KHR_visibility_mask` function lookup.
  - Evidence: log entries for extension support, function lookup success or
    visible `NativeFunctionMissing` status.
- [ ] Validate hidden and visible mesh fetch.
  - Evidence: per-view vertex/index counts, mask revision changes, and runtime
    status for missing mesh data.
- [ ] Validate visibility-mask stencil graph stage.
  - Evidence: RenderDoc event/resource capture showing the mask stage before
    visibility rendering, or a log proving the stage was skipped with a visible
    reason.
- [ ] Validate visibility-mask invalidation.
  - Trigger or simulate `XrEventDataVisibilityMaskChangedKHR`.
  - Confirm cached mask state revision changes and mesh fetch refreshes.

Exit evidence:

- [ ] Four submitted views are captured on hardware or simulator.
- [ ] Quad/stereo fallback reason is visible and accurate.
- [ ] Visibility-mask fetch and stencil behavior is proven or visibly skipped.

## Phase 4 - RVC Frame Graph And Resource Validation

Goal: prove declared RVC resources, graph stages, barriers, and diagnostics
match the architecture.

- [ ] Inspect RVC resource creation.
  - Verify per-view texture arrays for depth, visibility, velocity, HZB,
    reconstruction error, pixel-to-shadelet, transparency, final resolve, and
    mirror/debug output.
  - Verify shared buffers for visibility source records, material resource rows,
    mask vertices/indices, indirect args, shadelets, light clusters, lighting,
    reservoirs, temporal cache, and counters.
- [ ] Inspect framebuffers.
  - Verify visibility, transparency, resolve, and debug framebuffer attachments.
- [ ] Inspect RVC pass order.
  - Expected order: mask stencil, visibility, reconstruction, HZB,
    pixel-to-shadelet, material shadelets, foveated shading rate, head-space
    light clusters, shared lighting, reuse validation, temporal cache,
    transparency, resolve, diagnostics.
- [ ] Validate Vulkan synchronization2 barriers.
  - Evidence: RenderDoc event order and image layouts for attachment, storage,
    sampled, transfer, and OpenXR swapchain transitions.
- [ ] Validate OpenGL correctness barriers where the slice is supported.
  - Evidence: logs or RenderDoc capture showing coherent prototype ordering.
- [ ] Validate active-stage diagnostics.
  - Foundation runs may show `RVC.Pass.*.KernelPending`.
  - Production-stage validation must show no kernel-pending warning for a stage
    claimed as implemented.
- [ ] Validate RVC frame profiles.
  - Confirm projection, viewport, previous view-projection, runtime view index,
    swapchain identity, pixel count, stereo mode, foveation mode, GPU timing,
    and fallback reason are populated.

Exit evidence:

- [ ] RenderDoc capture or logs prove resources and pass order.
- [ ] Barrier behavior is inspected on the targeted backend.
- [ ] Frame profiles contain all required per-view fields.

## Phase 5 - Visibility Source Path Validation

Goal: prove every accepted opaque pixel maps to a valid source record and that
unsupported content falls back visibly.

- [ ] Validate static mesh visibility source path.
  - Evidence: per-view depth/visibility outputs, valid instance/draw/primitive
    identity, material row, transform, and editor selection metadata.
- [ ] Validate skinned mesh visibility source path.
  - Evidence: deformation/version identity is present and stale reuse is
    rejected after animation changes.
- [ ] Validate zero-readback indirect visibility source path.
  - Evidence: GPU indirect sources are reused and draw visibility does not read
    back to CPU in the render loop.
- [ ] Validate meshlet or mesh-shader visibility source path.
  - Evidence: mesh shader selected when supported, compute meshlet expansion
    selected as visible fallback otherwise.
- [ ] Validate unsupported material fallback.
  - Materials: transparent, refractive, order-dependent, expensive alpha-test,
    strongly view-dependent.
  - Evidence: fallback counters and Forward+ output.
- [ ] Validate rapid head motion and stereo disocclusion.
  - Evidence: captures showing no stale-HZB one-eye holes.

Exit evidence:

- [ ] Every opaque pixel in a validation capture maps to a valid source record.
- [ ] Unsupported materials fall back visibly and correctly.
- [ ] Rapid head motion and disocclusion do not expose stale visibility.

## Phase 6 - Shadelet, Lighting, Temporal, And Resolve Validation

Goal: prove the cache path matches the Forward+ oracle within quality
tolerances while shading fewer unique samples where expected.

- [ ] Validate attribute reconstruction.
  - Evidence: position, normal, tangent, UV, material row, previous position,
    and velocity reconstruction outputs plus reconstruction-error visualizer.
- [ ] Validate conservative HZB and post-validation.
  - Evidence: uncertain, newly visible, edge, and cross-view disagreement
    candidates are post-validated.
- [ ] Validate shadelet map generation.
  - Evidence: pixel-to-shadelet map, tile-local dedup, global merge, material
    bins, density overlay, cache-miss overlay, and overflow counters.
- [ ] Validate compute-side material shading.
  - Evidence: material rows match descriptor heap and descriptor indexing
    backends, and foveal output matches Forward+ within tolerance.
- [ ] Validate fragment shading rate fast path.
  - Evidence: `VK_KHR_fragment_shading_rate` path for material classes not yet
    ported to compute reconstruction, including near-UI/hand 1x1 overrides.
- [ ] Validate shared head-space light clusters.
  - Evidence: cluster occupancy, exact-light counts, rejected lights, and
    comparison against per-view Forward+ tile grid.
- [ ] Validate peripheral light aggregation and reservoirs.
  - Evidence: aggregate contribution, reservoir weight, exact-vs-aggregate, and
    energy error overlays.
- [ ] Validate reuse domains.
  - Domains: intra-view, inset/wide, stereo, temporal.
  - Evidence: accepted/rejected reuse counters and reasons.
- [ ] Validate A/B reuse harness.
  - Render identical frame with reuse enabled and disabled.
  - Evidence: side-by-side captures, per-region metrics, counters, and pass/fail
    report.
- [ ] Validate temporal cache.
  - Evidence: confidence, age, invalidation reason, temporal hit rate, stale
    rejection, and no visible ghosting in static scenes.
- [ ] Validate foveated resolve.
  - Evidence: visibility-edge AA, foveated TAA fallback where used, wide/inset
    identity handling, desktop mirror, and XR submitted images.

Quality gates:

- [ ] `OpaqueDense` desktop mono and stereo match Forward+ within tolerance.
- [ ] Foveal regions remain visually equivalent to per-pixel resolve.
- [ ] Mid-field and peripheral regions shade fewer unique samples than visible
  pixels on opaque-heavy scenes.
- [ ] Exact-light shared-cluster mode matches per-view Forward+ lighting within
  tolerance.
- [ ] Static scenes show temporal hit rate without visible ghosting.
- [ ] Gaze movement does not expose stale low-quality shading in foveal regions.
- [ ] Wide/inset resolve behaves correctly in desktop mirror and XR submission.

## Phase 7 - Vulkan Production Hardening

Goal: prove the Vulkan path is robust enough for real OpenXR benchmarking.

- [ ] Validate Vulkan multiview integration.
  - Confirm true stereo paths stay isolated from four-view sequential paths.
  - Verify view masks, layered targets, and fallback diagnostics.
- [ ] Validate dynamic rendering.
  - Evidence: RenderDoc event order and attachment state.
- [ ] Validate explicit synchronization.
  - Evidence: synchronization2 barriers for images and buffers used by RVC.
- [ ] Validate timeline semaphore handoff where applicable.
  - Evidence: OpenXR swapchain handoff has no missed or stale image usage.
- [ ] Validate descriptor heap backend.
  - Evidence: selected when supported, resource rows valid, no duplicate
    RVC-specific texture table.
- [ ] Validate descriptor indexing fallback.
  - Evidence: selected when heap is unavailable and material/shadelet logic is
    semantically identical.
- [ ] Validate missing descriptor backend failure.
  - Evidence: visible fallback when neither heap nor indexing is available.
- [ ] Validate fragment density map alternative.
  - Evidence: explicit selection or visible unsupported diagnostic.
- [ ] Validate `VK_EXT_mesh_shader`.
  - Evidence: mesh shader expansion selected where supported, indirect/compute
    meshlet path selected otherwise.

Exit evidence:

- [ ] Unsupported production Vulkan paths fail visibly with actionable
  diagnostics.
- [ ] Validated Vulkan path can be benchmarked against quad-view Forward+ with
  comparable settings and warm-cache policy.

## Phase 8 - Performance, Counters, And Timing

Goal: prove RVC performance claims with delayed counters and profiler evidence.

- [ ] Validate delayed GPU counter readback.
  - Evidence: no synchronous render-loop readback for RVC counters.
- [ ] Validate per-view GPU timing.
  - Evidence: `GpuMilliseconds` populated in frame profiles from resolved GPU
    timing, with fallback/unknown state documented when unavailable.
- [ ] Validate RVC counter categories.
  - Counters: visible, culled, uncertain, post-validated, page requests,
    raster lane, shadelets, material bins, cache hits/misses, reuse accepts,
    reuse rejects, temporal hits, temporal invalidations.
- [ ] Capture warm-cache performance runs.
  - Scenes: `OpaqueDense`, `AvatarMaterialDiverse`, `TransparencyFallback`,
    `QuadView`.
  - Evidence: profiler traces, frame stats, GPU timings, submitted pixels,
    unique shadelets, and missed-deadline counters.
- [ ] Compare against Forward+ oracle.
  - Evidence: table showing Forward+ versus RVC timing/counter deltas.

Exit evidence:

- [ ] RVC reports complete timing and counter data.
- [ ] Performance report compares RVC to foveated Forward+.
- [ ] No performance result depends on synchronous GPU readback.

## Phase 9 - Documentation And Owner Handoff

Goal: collect durable evidence and prepare the branch for owner review without
committing or merging unless requested.

- [ ] Update the architecture doc with validated runtime support matrix links.
- [ ] Update developer docs for launch flags, settings, diagnostics, runtime
  requirements, and fallback behavior.
- [ ] Link all baseline, RVC, RenderDoc, profiler, and MCP evidence.
- [ ] Summarize risks and known limitations.
- [ ] Confirm final docs are linked from `docs/architecture/rendering/README.md`.
- [ ] Prepare handoff notes.
  - Include changed files, validation evidence, residual risks, and follow-ups.
  - Do not commit or merge unless explicitly requested.
  - Merge branch `rendering-rvc-quad-view-foundation` back into `main` only
    after owner approval, validation, and final docs are complete.

Final exit evidence:

- [ ] Runtime and Vulkan support matrices are documented.
- [ ] All target validation scenes have baseline and RVC captures.
- [ ] RenderDoc captures exist for visibility, shadelets, shared lighting, and
  final resolve.
- [ ] Profiler report compares Forward+ and RVC warm-cache runs.
- [ ] Final architecture and developer docs are linked.

## Validation Scene Matrix

| Scene | Purpose | Required Evidence |
|-------|---------|-------------------|
| `DesktopMono` | Non-XR regression and baseline | Screenshot, logs, settings, frame stats |
| `Stereo` | Stable VR oracle | Left/right captures, mirror, logs, profiler |
| `OpaqueDense` | Visibility, shadelets, cache efficiency | Forward+ and RVC captures, counters, RenderDoc |
| `AvatarMaterialDiverse` | Skinned/deformation/material diversity | Visibility records, reuse rejection, quality report |
| `TransparencyFallback` | Forward+ companion path | Fallback counters, composite capture |
| `QuadView` | Wide/inset runtime behavior | Four submitted views, moving gaze captures, frame profile |

## Quality Thresholds

Use `RvcQualityToleranceSet.Default` unless a validation note records an
approved override.

| Region | Max Error | Min SSIM | Max FLIP |
|--------|-----------|----------|----------|
| Fovea | `1/255` | `0.995` | `0.010` |
| Guard band | `2/255` | `0.990` | `0.015` |
| Mid-field | `4/255` | `0.975` | `0.030` |
| Periphery | `8/255` | `0.940` | `0.060` |
