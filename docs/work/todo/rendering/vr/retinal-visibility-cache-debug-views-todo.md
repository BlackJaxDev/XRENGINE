# Retinal Visibility Cache VR Debug Views TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Retinal Visibility Cache Rendering](../../../../architecture/rendering/retinal-visibility-cache-rendering.md)
Design: [Retinal Visibility Cache Rendering Design](../../../design/rendering/retinal-visibility-cache-rendering-design.md)
Validation: [Retinal Visibility Cache Validation](../../../testing/rendering/retinal-visibility-cache-validation.md)

## Current State

RVC has foundation contracts in `XREngine.Runtime.Core/Settings/RvcRenderingContracts`, including `ERvcDebugViewMode`, `RvcRenderingSettings.DebugViewMode`, and `RvcDiagnosticsSnapshot`. `RvcRenderPipeline` declares mirror and debug frame-graph resources, and `VPRC_RvcPass` declares diagnostic graph stages. The editor-facing inspector, per-view debug texture output, MCP capture tools, RenderDoc labeling, and complete debug-view shaders are still open work.

## Open Code Items

### Contracts And Resource Metadata

- [ ] Extend `ERvcDebugViewMode` to cover the full RVC debug inventory. Files or types: `XREngine.Runtime.Core/Settings/RvcRenderingContracts/Enums/ERvcDebugViewMode.cs`, `RvcDiagnosticsSnapshot`, `RvcRenderingSettings`. Done when each view-set, visibility, reconstruction, shadelet, reuse, lighting, temporal, resolve, and performance debug mode has a stable enum value and test coverage.
- [ ] Add a common RVC debug view selector. Files or types: `RenderFrameViewSet`, `RenderFrameViewDescriptor`, `RvcDiagnosticsSnapshot`, `RvcRenderPipeline`. Done when a debug request can select frame view index, role, parent eye, wide/inset relation, mirror/debug view, and runtime OpenXR view index without left/right assumptions.
- [ ] Define debug resource metadata. Files or types: `RvcFrameGraphContract`, `RvcRenderPipeline`, `VPRC_RvcPass`. Done when every debug resource has a stable name, format, dimensions, view identity, lifetime, and fallback reason.
- [ ] Add delayed counter readback contracts for RVC debug counters. Files or types: `RvcRuntimeContracts`, `RuntimeEngine.Rendering.Stats.Rvc`. Done when counter latency and overflow behavior are represented without synchronous render-loop readback.
- [ ] Add source-contract tests for debug modes and view counts. Files or types: `XREngine.UnitTests/Rendering/RvcRenderingContractTests.cs`. Done when tests prove debug modes do not assume exactly two views.

### View-Set Inspector

- [ ] Add a `RenderFrameViewSet` inspector for RVC. Files or types: editor diagnostics UI, `RenderFrameViewSet`, `RvcFrameProfileSnapshot`. Done when mono, stereo, and quad-view runs list each view role, viewport, swapchain image, FOV, foveation state, render path, and fallback reason.
- [ ] Display requested and effective `VR.ViewRenderMode`. Files or types: RVC diagnostics UI, `RvcPipelineResolution`, OpenXR pipeline selection. Done when logs, profiler rows, and the diagnostics surface show requested mode and effective path.
- [ ] Display single-pass stereo and parallel-recording status. Files or types: RVC diagnostics UI, Vulkan RVC capability reporting. Done when the UI states true stereo/layered/multiview, compatibility per-eye rendering, Vulkan parallel recording, or unsupported rejection.
- [ ] Add per-view thumbnails for final color and selected debug output. Files or types: RVC diagnostics UI, RVC debug framebuffer resources. Done when each submitted view can show final color and the active debug view.
- [ ] Add hidden-area and visibility-mask visualization per view. Files or types: OpenXR visibility-mask resources, RVC diagnostics UI. Done when each view can show hidden or visible mask data when the runtime provides it.
- [ ] Add a mode-comparison capture path. Files or types: editor capture tooling, RVC diagnostics services. Done when one validation scene can save comparable sequential, single-pass stereo, and parallel Vulkan captures.

### Visibility And HZB Views

- [ ] Add depth and linear-depth debug views. Files or types: `RvcFrameGraphContract`, `VPRC_RvcPass`, RVC debug shaders. Done when each view can display depth and linear depth.
- [ ] Add visibility payload debug views. Files or types: visibility source records, RVC debug shaders. Done when instance, draw or meshlet, primitive, material, transform, and editor selection identities are inspectable per visible opaque pixel.
- [ ] Add payload overflow and unsupported-material masks. Files or types: RVC counters, RVC debug shaders. Done when overflow and unsupported-material pixels are visible and counted.
- [ ] Add hidden-area stencil and mask debug output. Files or types: OpenXR mask resources, RVC debug shaders. Done when stencil and mask use can be inspected per view.
- [ ] Add HZB debug views. Files or types: RVC HZB resources, RVC debug shaders. Done when previous/early HZB, current HZB, reject mask, uncertain/post-pass mask, and raster-lane mask are inspectable.
- [ ] Add wide-to-inset and stereo disagreement views. Files or types: RVC HZB resources, RVC reuse diagnostics. Done when same-eye depth agreement and stereo disocclusion masks explain local visibility or shading.
- [ ] Add visibility counters. Files or types: `RuntimeEngine.Rendering.Stats.Rvc`, RVC profiler rows. Done when visible, culled, uncertain, post-pass, page-request, and raster-lane counts are reported.

### Reconstruction And Shadelet Views

- [ ] Add reconstruction debug views. Files or types: RVC reconstruction resources, RVC debug shaders. Done when position, normal, tangent, UV, material row, material resource generation, previous position, and velocity are inspectable.
- [ ] Add reconstruction error heatmaps against Forward+. Files or types: RVC debug shaders, quality comparison helpers. Done when foveal reconstruction errors can be measured before stereo reuse is enabled.
- [ ] Add derivative and texture-LOD visualization. Files or types: RVC material reconstruction shaders. Done when derivative and texture-LOD policy is visible.
- [ ] Add shadelet density and pixel-to-shadelet views. Files or types: shadelet resources, RVC debug shaders. Done when density regions, tile base offsets, and local indices are visible.
- [ ] Add shadelet key and material-bin views. Files or types: shadelet key contracts, material-bin resources. Done when key hash, bucket, occupancy, and divergent-material hot spots are visible.
- [ ] Add cache miss, overflow, cap-pressure, and local-shading masks. Files or types: RVC cache counters, RVC debug shaders. Done when performance cliffs can be explained by named masks.
- [ ] Add a material reconstruction comparison capture. Files or types: RVC capture tooling, debug shaders. Done when per-pixel material reconstruction and shadelet resolve can be compared side by side.

### Reuse And A/B Harness

- [ ] Add reuse overlays. Files or types: RVC reuse diagnostics, RVC debug shaders. Done when intra-view, inset/wide, stereo, and temporal reuse are visible.
- [ ] Add rejected-reuse overlays by reason. Files or types: RVC reuse diagnostics, RVC debug shaders. Done when primitive, material, barycentric, world-position, normal, depth, deformation, LOD, material generation, roughness, specular, disocclusion, and edge rejection reasons are visible.
- [ ] Add foveal, guard-band, mid-field, and peripheral policy overlays. Files or types: `RvcQualityToleranceSet`, RVC debug shaders. Done when reuse policy regions are visible.
- [ ] Add per-view local-shading masks. Files or types: RVC reuse diagnostics, RVC debug shaders. Done when one-eye-only and newly visible pixels are marked.
- [ ] Add an RVC reuse A/B debug view. Files or types: RVC capture tooling, quality comparison helpers. Done when per-view shading and cross-view reuse show visual difference and numeric region metrics.
- [ ] Add reuse counters. Files or types: `RuntimeEngine.Rendering.Stats.Rvc`, RVC profiler rows. Done when attempted, accepted, rejected-by-reason, and unique-shadelet ratio counters are reported.

### Shared Lighting Views

- [ ] Add head-space cluster debug views. Files or types: RVC shared-lighting resources, RVC debug shaders. Done when cluster ID, occupancy, and per-shadelet assignment are visible.
- [ ] Add exact-light and rejected-light overlays. Files or types: RVC shared-lighting resources, RVC debug shaders. Done when exact-light counts, rejected-light counts, and top-K exact lights are visible.
- [ ] Add aggregate and reservoir lighting views. Files or types: RVC shared-lighting resources, reservoir contracts. Done when aggregate contribution, energy error, reservoir weight, confidence, and age are visible when the path is enabled.
- [ ] Add logical resource-reference diagnostics. Files or types: material resource rows, clustered light resources. Done when shadow, cookie, probe, and clustered-light references use logical IDs instead of backend handles.
- [ ] Preserve Forward+ tile-grid comparison output. Files or types: Forward+ debug output, RVC shared-lighting comparison tooling. Done when shared clusters can be compared with per-view Forward+ tile grids.

### Resolve, Post, Composition, And Mirror Views

- [ ] Add final per-view RVC resolve output before transparent overlay. Files or types: RVC resolve resources, RVC debug shaders. Done when final opaque resolve can be inspected per view.
- [ ] Add transparent Forward+ overlay contribution view. Files or types: RVC transparency companion path, RVC debug shaders. Done when transparent fallback contribution is visible.
- [ ] Add edge-aware AA and upsample weight views. Files or types: RVC resolve shaders. Done when edge AA and upsample weights are visible.
- [ ] Add foveated temporal diagnostics. Files or types: RVC temporal resources, RVC debug shaders. Done when TAA or reprojection history validity, motion vector use, and rejection are visible.
- [ ] Add temporal cache age, confidence, and stale-shading views. Files or types: RVC temporal cache resources. Done when stale temporal cache entries are visible and explainable.
- [ ] Add wide/inset composition and edge-blend visualization. Files or types: RVC resolve resources, mirror debug output. Done when headset and mirror composition can be inspected.
- [ ] Add final-output checker diagnostics. Files or types: RVC diagnostics services. Done when black, stale, wrong-layer, and wrong-view output produce named diagnostics.

### Tooling And Profiling

- [ ] Add ImGui controls for RVC debug selection. Files or types: editor diagnostics UI, RVC settings. Done when the editor can select debug view mode, frame view, mip, layer, slice, channel swizzle, numeric range, palette, and freeze-frame behavior.
- [ ] Add MCP tools for RVC debug iteration if useful. Files or types: editor MCP actions. Done when tools such as `get_rvc_debug_state`, `set_rvc_debug_view`, `capture_rvc_debug_view`, or `dump_rvc_frame_graph` exist only if they materially reduce iteration cost.
- [ ] Regenerate MCP documentation after MCP tool changes. Files or types: `Tools/Reports/generate_mcp_docs.ps1`, MCP docs. Done when the generated MCP docs include any new or renamed RVC tools.
- [ ] Add RenderDoc event labels and resource names. Files or types: `VPRC_RvcPass`, backend command labels. Done when RenderDoc captures show named RVC passes and debug targets.
- [ ] Add profiler rows for RVC debug cost. Files or types: RVC profiler services, `RuntimeEngine.Rendering.Stats.Rvc`. Done when pass timing, debug overhead, delayed readback latency, shadelet count, reuse ratio, and mode selection are distinct rows.
- [ ] Add a compact RVC capture bundle. Files or types: editor capture tooling. Done when selected screenshots, profiler rows, logs, and effective RVC mode matrix can be captured together.

## Decisions Needed

- [ ] Decide whether debug view selection is a global renderer setting, per-viewport editor state, or both. Owner: Rendering / Editor.
- [ ] Decide which debug views are available in shipping builds, development builds, and editor-only builds. Owner: Rendering.
- [ ] Decide whether `SinglePassStereo` debug output needs an explicit stereo-array debug texture for compatibility per-eye rendering. Owner: Rendering.
- [ ] Decide whether quad-view multiview or layered recording needs a new requested mode or remains a Vulkan implementation detail. Owner: Rendering.
- [ ] Decide the overhead budget for always-available RVC counters on target VR hardware. Owner: Rendering.

## Out Of Scope

- Runtime validation, screenshots, profiling, hardware checks, and RenderDoc capture review live in the validation doc.
- Production RVC shader dispatch is tracked by the RVC architecture and validation work, not this debug-view todo.
