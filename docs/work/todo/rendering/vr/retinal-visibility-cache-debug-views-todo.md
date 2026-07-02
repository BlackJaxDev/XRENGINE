# Retinal Visibility Cache VR Debug Views TODO

Last Updated: 2026-07-02
Owner: Rendering / XR
Status: Proposed
Parent Plan: [Retinal Visibility Cache Rendering TODO](retinal-visibility-cache-rendering-todo.md)

Related docs:

- [Retinal Visibility Cache Rendering Design](../../../design/rendering/retinal-visibility-cache-rendering-design.md)
- [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md)
- [OpenXR Future Work TODO](openxr-future-work-todo.md)
- [VR Rendering Performance Contract TODO](../optimization/vr-rendering-performance-contract-todo.md)
- [Visibility Buffer Rendering TODO](../optimization/visibility-buffer-rendering-todo.md)
- [Vulkan Manual Validation Guide](../../vulkan.md)

## Goal

Add extensive, view-set-aware debug views for Retinal Visibility Cache (RVC) VR
rendering. The debug surface should make every RVC stage inspectable across
mono, stereo, quad-view, foveated, and fallback paths without changing renderer
semantics or hiding unsupported accelerated modes.

Debug output must answer:

- Which views were rendered, what role each view had, and which effective render
  mode produced them.
- Which RVC resources were written for each view.
- Why pixels, shadelets, lights, or full views used RVC, Forward+, local shading,
  reuse, or fallback.
- Whether output differs between serial, single-pass stereo, and Vulkan parallel
  command-buffer recording.
- Whether any visual artifact is caused by visibility, material reconstruction,
  shadelet reuse, lighting, resolve, post, or XR composition.

## Mode Support Answer

Yes, RVC should support serial, single-pass stereo, and parallel Vulkan
recording. These are execution and resource-model lanes over the same
`RenderFrameViewSet` semantics, not separate renderer meanings.

| Requested mode | RVC support expectation | Debug requirement |
| --- | --- | --- |
| `SequentialViews` | Required correctness/reference lane. Each output view records and renders independently from the same view-set contract. | Every debug view must work here first. This is the baseline for A/B comparisons and artifact isolation. |
| `SinglePassStereo` | Required stereo compatibility lane for two-eye stereo when the backend can express the eye pair as a stereo/layered/multiview target. It must preserve per-eye visibility, foveation metadata, histories, and fallback reasons. | Debug views must show left/right layers separately and report whether the effective path was true single-pass stereo or compatibility per-eye rendering. |
| `ParallelCommandBufferRecording` | Required Vulkan optimization lane. It records safe view or pass command buffers in parallel after immutable view inputs and frame-graph resources are prepared. It must produce the same RVC semantics as `SequentialViews`. | Debug views must report worker ownership, per-view command-buffer identity, merge/submit timing, and any unsupported-mode rejection. |
| Quad-view RVC | Required high-end target. Quad-view can run sequentially or through Vulkan parallel recording. Single-pass stereo remains the two-eye baseline and may evolve into broader multiview/layered sub-batches, but quad-view correctness cannot depend on true single-pass support. | Debug selection must handle left wide, right wide, left inset, right inset, mirror, and optional diagnostic views without hard-coding two eyes. |

OpenGL may host correctness slices where practical, but full RVC debug coverage
is Vulkan-first. Explicit requests for unsupported RVC, foveation, multiview, or
parallel recording paths must fail visibly or report a clear fallback reason.

## Design Principles

- Debug views are selected by `RenderFrameViewSet` view identity, not by
  hard-coded left/right eye assumptions.
- Debug semantics are identical across `SequentialViews`, `SinglePassStereo`,
  and `ParallelCommandBufferRecording`; only scheduling/resource layout changes.
- Debug output is opt-in unless it is a cheap steady-state counter.
- GPU counters use delayed readback. No synchronous readback in render,
  swap/present, visible collection, fixed update, or per-frame update paths.
- Debug passes and overlays must have explicit frame-graph resources, barriers,
  names, formats, dimensions, view roles, and lifetime.
- Debug modes must not introduce per-frame heap allocations in hot paths.
- Debug state must be machine-readable through logs/profiler/MCP where useful,
  and visually inspectable through ImGui, screenshots, or RenderDoc.
- Unsupported debug views are reported as unsupported with a reason, not silently
  replaced with unrelated output.

## Debug View Inventory

| Area | Views and overlays |
| --- | --- |
| View set | View role, eye, wide/inset relationship, viewport, swapchain image, render target size, FOV, hidden-area mesh, foveal center, gaze source, foveation mode, `VR.ViewRenderMode`, effective render path, fallback reason. |
| Visibility | Depth, linear depth, visibility ID, instance ID, meshlet/draw ID, primitive ID, material ID, transform/editor-selection ID, hidden-area stencil, HZB levels, HZB reject mask, uncertain/post-pass mask, wide-to-inset depth agreement. |
| Reconstruction | Reconstructed position, normal, tangent, UV, velocity, material row, material resource generation, derivative/texture-LOD mode, reconstruction error against Forward+. |
| Shadelets | Shadelet density, shadelet key hash, pixel-to-shadelet map, per-tile local indices, material-bin occupancy, cache hit/miss, overflow/cap pressure, local-shading fallback mask. |
| Reuse | Intra-view reuse, inset/wide reuse, stereo reuse, temporal reuse, rejected reuse attempts, rejection reason, disocclusion-local shading, stale material/resource generation, roughness/specular gating. |
| Lighting | Head-space cluster ID, cluster occupancy, exact light count, rejected light count, top-K selection, aggregate contribution, reservoir weight/confidence, shadow/cookie/probe resource references, per-view lighting fallback. |
| Resolve and post | Final resolved color, eye-specific specular correction, foveated edge AA, foveated TAA/reprojection, upsample weights, wide/inset composition mask, confidence/age, stale-shading rejection, mirror/debug output. |
| Performance | Per-view timings, pass timings, worker thread IDs, command-buffer reuse/record status, fragment/shadelet counts, unique-shadelet ratio, delayed readback latency, debug overhead. |

## Phase 0 - Debug Contracts And Resource Naming

- [ ] Define an `RvcDebugViewMode` enum or equivalent that covers every inventory
  item without binding public semantics to backend resource handles.
- [ ] Define a common view selector: frame view index, role, parent eye,
  wide/inset relationship, mirror/debug flag, and runtime OpenXR view index.
- [ ] Define debug output channels:
  - ImGui diagnostics overlay and per-view debug pane.
  - Profiler counters and frame-output manifest rows.
  - Logs for capability/fallback events.
  - MCP-readable render state and screenshot capture where useful.
  - RenderDoc labels and resource names.
- [ ] Define frame-graph naming conventions, for example
  `RVC/View0.LeftWide/VisibilityID`,
  `RVC/View2.LeftInset/PixelToShadelet`, and
  `RVC/Shared/HeadSpaceLightClusters`.
- [ ] Define debug formats and precision for every debug resource.
- [ ] Define the fallback policy when a debug resource cannot be produced on a
  backend or path.
- [ ] Define delayed GPU readback buffers for counters, including frame latency
  and overflow behavior.
- [ ] Define the overhead budget for "debug views enabled but not captured" and
  for "capturing one debug view this frame."

Acceptance criteria:

- [ ] The debug contract is documented before RVC pass implementation begins.
- [ ] Every debug resource has a stable name, view identity, format, and
  fallback reason.
- [ ] Source-contract tests can validate that no debug mode assumes exactly two
  views.

## Phase 1 - View-Set And Mode Inspector

- [ ] Add a `RenderFrameViewSet` inspector that lists every frame view and its
  current role, dimensions, FOV, viewport, swapchain/render target identity,
  foveation state, and fallback reason.
- [ ] Display the requested and effective `VR.ViewRenderMode`.
- [ ] Display whether `SinglePassStereo` is true stereo/layered/multiview or a
  compatibility path.
- [ ] Display whether `ParallelCommandBufferRecording` used Vulkan parallel
  recording or was rejected as unsupported.
- [ ] Add per-view thumbnails for final color and selected debug view.
- [ ] Add hidden-area mesh/visibility-mask visualization per view.
- [ ] Add a mode-comparison capture path that can save the same validation scene
  under sequential, single-pass stereo, and parallel Vulkan recording.

Acceptance criteria:

- [ ] Mono, stereo, and quad-view/emulated-quad-view configurations all show the
  correct view count and roles.
- [ ] Requested and effective render mode are visible in logs, profiler data,
  and the ImGui diagnostics surface.
- [ ] Unsupported mode requests produce actionable diagnostics.

## Phase 2 - Visibility, HZB, And Payload Debug Views

- [ ] Add per-view depth and linear-depth views.
- [ ] Add visibility payload views for instance, draw/meshlet, primitive,
  material, transform, and editor selection identity.
- [ ] Add payload overflow and unsupported-material masks.
- [ ] Add hidden-area stencil/mask visualization.
- [ ] Add HZB debug views:
  - previous/early HZB sample used
  - current-frame HZB
  - reject mask
  - uncertain/post-pass mask
  - raster lane mask
- [ ] Add same-eye wide-to-inset HZB seed visualization and depth-agreement
  heatmap.
- [ ] Add stereo disagreement/disocclusion masks that force local per-view
  visibility or shading.
- [ ] Add counters for visible, culled, uncertain, post-pass, page-request, and
  raster-lane counts.

Acceptance criteria:

- [ ] Visibility debug output can identify the exact source record for every
  visible opaque pixel in supported scenes.
- [ ] HZB rejection can be distinguished from material/resolve artifacts.
- [ ] Rapid head motion and stereo disocclusion artifacts have a visible mask
  instead of being inferred from final color.

## Phase 3 - Material Reconstruction And Shadelet Debug Views

- [ ] Add reconstruction debug views for position, normal, tangent, UV,
  material row, material resource generation, previous position, and velocity.
- [ ] Add reconstruction error heatmaps against the Forward+ correctness oracle.
- [ ] Add derivative and texture-LOD strategy visualization.
- [ ] Add shadelet density visualization for 1x1, 2x2, 4x4, and 8x8 regions.
- [ ] Add pixel-to-shadelet map visualization, including tile base offsets and
  local indices.
- [ ] Add shadelet key hash or bucket visualization.
- [ ] Add material-bin occupancy and divergent-material hot-spot visualization.
- [ ] Add cache miss, overflow, cap-pressure, and local-shading fallback masks.
- [ ] Add side-by-side per-pixel material reconstruction versus shadelet resolve
  capture.

Acceptance criteria:

- [ ] Foveal reconstruction errors are visible and measurable before stereo
  reuse is enabled.
- [ ] Shadelet density boundaries can be inspected without relying on final
  color artifacts.
- [ ] Material-bin and overflow diagnostics explain performance cliffs.

## Phase 4 - Reuse And A/B Harness Debug Views

- [ ] Add overlays for intra-view, inset/wide, stereo, and temporal shadelet
  reuse.
- [ ] Add rejected-reuse overlays grouped by reason:
  - primitive/material mismatch
  - barycentric/world-position mismatch
  - normal or depth disagreement
  - deformation/LOD version mismatch
  - material resource generation mismatch
  - roughness/specular gating
  - disocclusion or edge rejection
- [ ] Add foveal/guard/mid/peripheral region overlays for reuse policy.
- [ ] Add per-view local-shading masks for one-eye-only or newly visible pixels.
- [ ] Add an A/B debug view for per-view shading versus cross-view reuse using
  the quality tolerances from the parent RVC TODO.
- [ ] Add counters for attempted reuse, accepted reuse, rejected reuse by reason,
  and resulting unique-shadelet ratio.

Acceptance criteria:

- [ ] Stereo reuse can be left disabled while counters and overlays prove the
  measured reuse ceiling.
- [ ] The A/B harness can show both visual difference and numeric region metrics.
- [ ] Reuse artifacts can be traced to a named rejection/gating failure.

## Phase 5 - Shared Lighting Debug Views

- [ ] Add head-space cluster ID and cluster occupancy views.
- [ ] Add per-shadelet cluster assignment visualization.
- [ ] Add exact-light count and rejected-light count overlays.
- [ ] Add top-K exact light debug views.
- [ ] Add aggregate contribution and energy-error visualization.
- [ ] Add reservoir weight, confidence, and age views if reservoir lighting is
  adopted.
- [ ] Add shadow map, cookie, probe, and clustered-light resource-reference
  diagnostics using logical material/resource IDs rather than backend handles.
- [ ] Add per-view specular correction and broad-specular sharing comparison
  views.
- [ ] Keep old per-view Forward+ tile-grid debug output available for comparison
  while the head-space cluster path is validated.

Acceptance criteria:

- [ ] Exact-light shared clusters can be compared against per-view Forward+
  lighting.
- [ ] Many-light scenes explain whether cost comes from cluster occupancy,
  exact-light lists, aggregates, shadows, or resolve.
- [ ] Specular errors are identifiable as shared-lighting or per-view resolve
  problems.

## Phase 6 - Resolve, Post, Composition, And Mirror Debug Views

- [ ] Add final per-view RVC resolve debug output before transparent Forward+
  overlay.
- [ ] Add transparent Forward+ overlay/fallback contribution view.
- [ ] Add edge-aware AA and upsample weight views.
- [ ] Add foveated TAA/reprojection history validity, motion-vector, and
  rejection views.
- [ ] Add confidence/age/stale-shading views for temporal cache entries.
- [ ] Add wide/inset composition and edge-blend visualization.
- [ ] Add desktop mirror/cyclopean debug integration that can choose submitted
  eye, RVC final, or selected intermediate debug view.
- [ ] Add final-output checker diagnostics for black, stale, wrong-layer, or
  wrong-view output.

Acceptance criteria:

- [ ] Final-color problems can be separated from visibility, material, lighting,
  transparent overlay, post, and XR composition problems.
- [ ] Wide/inset resolve can be inspected in both headset submission and desktop
  mirror output.
- [ ] Debug output remains usable when RVC falls back to Forward+ for some
  materials or views.

## Phase 7 - Tooling, MCP, RenderDoc, And Profiler Integration

- [ ] Add ImGui controls for selecting:
  - debug view mode
  - frame view
  - mip/layer/slice
  - channel swizzle
  - numeric range and palette
  - freeze-frame/capture behavior
- [ ] Add MCP tools only if they materially help editor iteration, for example:
  - `get_rvc_debug_state`
  - `set_rvc_debug_view`
  - `capture_rvc_debug_view`
  - `dump_rvc_frame_graph`
- [ ] Regenerate MCP docs with `pwsh Tools/Reports/generate_mcp_docs.ps1` after
  adding or renaming MCP tools.
- [ ] Add RenderDoc event labels and resource names for every RVC pass and
  debug target.
- [ ] Add profiler rows for RVC pass timings, debug overhead, delayed readback
  latency, shadelet counts, reuse ratios, and mode selection.
- [ ] Add a compact capture bundle that records selected screenshots, profiler
  rows, logs, and the effective RVC/mode matrix under `Build/_AgentValidation`.

Acceptance criteria:

- [ ] The editor iteration loop can capture an RVC debug view through MCP into
  the current run root.
- [ ] RenderDoc captures show named RVC passes and resources.
- [ ] Profiling can distinguish RVC cost from debug-view cost.

## Phase 8 - Validation Matrix

- [ ] Validate `DesktopMono` sequential RVC debug output.
- [ ] Validate OpenXR/OpenVR stereo `SequentialViews`.
- [ ] Validate OpenXR Vulkan `SinglePassStereo`, including true stereo/layered
  and compatibility-path reporting where applicable.
- [ ] Validate OpenXR Vulkan `ParallelCommandBufferRecording`.
- [ ] Validate quad-view or emulated quad-view sequential output.
- [ ] Validate quad-view or emulated quad-view Vulkan parallel recording output.
- [ ] Validate OpenGL correctness slices and unsupported-mode diagnostics.
- [ ] Validate debug-off versus debug-on overhead.
- [ ] Validate no hot-path allocation regression with debug views registered but
  disabled.
- [ ] Validate delayed readback latency and counter overflow behavior.
- [ ] Capture before/after images for at least one visibility issue, one
  material reconstruction issue, one reuse issue, one lighting issue, and one
  resolve/composition issue.

Acceptance criteria:

- [ ] RVC debug views are available across all supported RVC validation scenes.
- [ ] Serial, single-pass stereo, and parallel Vulkan lanes produce comparable
  debug semantics for the same scene.
- [ ] Unsupported lanes fail visibly with actionable diagnostics.
- [ ] The parent RVC TODO can reference these debug views as the standard
  validation surface for later implementation phases.

## Open Questions

- Should debug view selection be a global renderer setting, a per-viewport
  editor state, or both?
- Which debug views should be available in shipping/development builds versus
  editor-only builds?
- Should `SinglePassStereo` debug output require an explicit stereo-array debug
  texture even when the effective path is compatibility per-eye rendering?
- Should quad-view multiview/layered recording get its own requested mode later,
  or remain an implementation detail under the RVC Vulkan path?
- What is the exact overhead budget for always-available RVC counters on target
  VR hardware?
