# Surfel GI Repair Todo

Status: investigation-driven todo  
Scope: Surfel GI compute pass, G-buffer identity inputs, forward depth-normal prepass, normal/depth decode contracts, debug tooling, and targeted validation.

## Current analysis

Surfel GI appears to be structurally present but blocked by screen-space input contract problems.

The highest-confidence blocker matches the mesh-id suspicion: the engine calls the identity buffer `TransformId`, and `VPRC_SurfelGIPass` samples it as `gTransformId`. `Spawn.comp` stores that value into `surfel.meta.z`, then `Spawn.comp`, `BuildGrid.comp`, and `Shade.comp` use it as a command/world-matrix index. Deferred G-buffer shaders write `TransformId`, but the forward depth-normal prepass currently writes only normal and depth.

Evidence:

- `VPRC_SurfelGIPass` samples `DepthView`, `Normal`, `AlbedoOpacity`, and `TransformId`.
- `VPRC_SurfelGIPass` comments that Surfel GI uses `TransformId` as the command index.
- `Spawn.comp` samples `layout(binding = 3) uniform usampler2D gTransformId`.
- Deferred shaders write `layout(location = 3) out uint TransformId`.
- `DepthNormalPrePass.fs` only declares `layout(location = 0) out vec2 Normal`.
- `CreateForwardDepthPrePassMergeFBO()` attaches only the main `Normal` and `DepthStencil` textures, not `TransformId`.
- `ForwardDepthNormalVariantFactory` injects only the normal output into generated prepass variants.
- An existing `docs/work/todo/forward-depth-normal-transform-id-todo.md` already documents the generic prepass identity bug, but Surfel GI is one of the concrete systems affected by it.

There is a second likely blocker: normal encoding. The G-buffer normal texture is octahedral encoded through `XRENGINE_EncodeNormal(vec3)` into `vec2`, but `Spawn.comp` and `Shade.comp` decode `texture(gNormal, uv).rgb` as if it were a simple RGB normal. That can produce wrong surfel normals, wrong object-space matching, and bad or zero gather weights even if transform IDs are fixed.

There are also stability issues after the input contracts are corrected:

- `TransformId == 0` is ambiguous if it means both "cleared/no object" and "command index 0".
- Reused surfels can be moved in `Spawn.comp` after `BuildGrid.comp` has already inserted them, and the shader intentionally does not reinsert them for same-frame shading.
- The pass is mono-only today and clears output in stereo.
- The current algorithm is intentionally first-pass and naive, so successful repair should target debuggability before visual quality.

## Phase 0: Branch and baseline capture

- [ ] Create a dedicated branch for this todo list.
- [ ] Capture the current Surfel GI failure mode in one repeatable editor scene.
- [ ] Confirm whether the failing scene is deferred-only, forward-only, or mixed deferred/forward.
- [ ] Capture screenshots of `DepthView`, `Normal`, `TransformId`, and `SurfelGITexture` debug views if available.
- [ ] Record whether `TransformId` appears blank, stale, or mismatched where surfels should spawn.
- [ ] Confirm whether Surfel GI should remain disabled by default until this todo is complete.

## Phase 1: Fix the shared forward prepass identity contract

- [ ] Fold or complete the relevant work from `docs/work/todo/forward-depth-normal-transform-id-todo.md`.
- [ ] Decide the compact forward prepass attachment layout for identity output.
- [ ] Attach the main `TransformId` texture to `CreateForwardDepthPrePassMergeFBO()` in `DefaultRenderPipeline`.
- [ ] Mirror the attachment change in `DefaultRenderPipeline2` while it remains present.
- [ ] Keep shared prepass color, depth, and stencil clears disabled so deferred IDs survive untouched pixels.
- [ ] Update `DepthNormalPrePass.fs` to declare `FragTransformId` and write `TransformId = floatBitsToUint(FragTransformId);` to match the deferred convention and the `R32UI` `TransformId` texture format.
- [ ] Update `ForwardDepthNormalVariantFactory` so generated prepass variants write both normal and transform ID using the same `floatBitsToUint` encoding.
- [ ] Update explicit `XRENGINE_DEPTH_NORMAL_PREPASS` shader branches so they write transform ID too.
- [ ] Ensure generated vertex programs emit `FragTransformId` whenever the effective prepass material consumes it.
- [ ] Add regression tests for generated depth-normal variants that include `TransformId` output and `FragTransformId` input.

## Phase 2: Make Surfel GI identity semantics unambiguous

- [ ] Audit what value `RenderCommandMesh3D` pushes through `PushTransformId`.
- [ ] Confirm that the value written into `TransformId` maps to `GPUScene.AllLoadedCommandsBuffer` indices.
- [ ] Decide whether `TransformId` should encode `commandIndex` directly or `commandIndex + 1`.
- [ ] Prefer reserving zero as "no transform/object" if this does not conflict with existing consumers.
- [ ] Update `Spawn.comp`, `BuildGrid.comp`, and `Shade.comp` to reject invalid identity values before indexing transform buffers.
- [ ] Add debug counters for invalid transform IDs, out-of-range command indices, and failed matrix loads.
- [ ] Add a CPU/GPU test that proves a known `TransformId` resolves to the expected world matrix.
- [ ] Document the final identity convention near the texture creation and shader decode code.

## Phase 3: Fix normal and depth decode contracts

- [ ] Replace the Surfel GI custom `DecodeNormal(vec3)` logic with octahedral decoding matching `NormalEncoding.glsl` (the Normal texture is `vec2` octahedral, populated via `XRENGINE_EncodeNormal`).
- [ ] Sample normal textures through `.rg`, not `.rgb`, in `Spawn.comp` and `Shade.comp`.
- [ ] Remove the extra `cameraToWorldMatrix` normal transform: the prepass and deferred shaders already write world-space normals, so re-rotating them by `cameraToWorldMatrix` rotates them out of world space.
- [ ] Audit `LightVolumes` and `RadianceCascades` compute shaders for the same normal decode bug and decide whether to repair them in the same GI input-contract pass.
- [ ] Confirm `DepthView` is a non-linear depth view compatible with the current inverse-projection reconstruction.
- [ ] Add a small shader/unit test that reconstructs normal and world position from synthetic G-buffer inputs.
- [ ] Update Surfel GI docs if the input texture contract changes.

## Phase 4: Repair surfel spawn, reuse, and grid update behavior

- [ ] Add a debug mode that counts candidate spawn tiles, rejected depth pixels, rejected normal pixels, invalid transform IDs, and allocated surfels.
- [ ] Revisit the spawn heuristic so it samples deterministic coverage-poor pixels rather than only one pseudo-random pixel per tile.
- [ ] Fix the dispatch ordering bug: `Execute()` runs `BuildGrid` before `Spawn`, so newly allocated surfels are not present in this frame's grid and cannot be gathered until next frame. Either move grid build after spawn, or have spawn insert new (and moved-reused) surfels into the grid with duplicate protection.
- [ ] Fix same-frame visibility for reused surfels that move across cells after `BuildGrid.comp`.
- [ ] Audit `Init.comp` and `Recycle.comp` so `meta.y` (active) and `meta.z` (transformId) start at 0, recycled slots clear `meta.z`, and recycle rejects surfels whose `TransformId` no longer resolves to a valid transform.
- [ ] Decide whether reused surfels should be reinserted into the grid during spawn with duplicate protection.
- [ ] Settle on the canonical pass order (recycle, reset grid, spawn/update, build grid) and document it next to `Execute()`.
- [ ] Keep surfel update and grid-build paths allocation-free on CPU.
- [ ] Add tests for reused surfel movement across grid cells.
- [ ] Add a test that spawns a surfel and confirms it is gatherable in the same frame.

## Phase 5: Verify compute barriers and composition

- [ ] Confirm every Surfel GI compute dispatch uses the correct memory barrier for the next pass.
- [ ] Confirm `SurfelGITexture` image writes are visible before `SurfelGICompositeFBO` samples it.
- [ ] Confirm the composite pass blends into the expected forward/HDR target in both standard and MSAA-deferred modes.
- [ ] Add clear or overwrite guarantees for every output pixel in `Shade.comp`.
- [ ] Add a render-graph description update if the synthetic pass is missing image-write or composite dependencies.
- [ ] Keep stereo behavior explicitly disabled or add a tracked follow-up for stereo Surfel GI.

## Phase 6: Add practical debug views

Existing surfaces to extend: `SurfelDebugRenderPipeline` (`ESurfelDebugVisualization`) and `VPRC_SurfelDebugVisualization` (`EVisualizationMode.SurfelCircles` / `GridHeatmap`). Today the disc visualization is a screen-space compute raster with a flat color; the goal of this phase is to render every live surfel as a real 3D oriented disc/quad in world space, with selectable color and orientation attributes, so spawn/reuse/normal/transform bugs are visible at a glance.

### 6.1 - Per-surfel 3D disc rendering primitive

- [ ] Add a `VPRC_SurfelDebugDrawPass` that draws one oriented disc per active surfel using instanced rendering, sourcing instance data directly from the `SurfelGI_Surfels` SSBO via `gl_InstanceID` (no CPU readback, no allocation per frame).
- [ ] Use `glDrawArraysIndirect` / `glMultiDrawArraysIndirect` with `stackTop` (clamped to `MaxSurfels`) as the instance count, so dispatch matches GPU-resident state without a CPU sync.
- [ ] Use a small disc/fan mesh (e.g. 16-segment triangle fan) as the base geometry; let radius come from `posRadius.w` and orientation come from `normal.xyz` rotated into world space via the per-surfel transform matrix (same `TryLoadWorldMatrix` path the compute shaders use).
- [ ] Reconstruct world position in the vertex shader as `(model * vec4(localPos, 1)).xyz`; reconstruct world normal as `normalize(transpose(inverse(mat3(model))) * localNormal)`.
- [ ] Build an orthonormal basis from the world normal in the vertex shader to orient the disc; do not rely on per-surfel tangent data.
- [ ] Skip inactive surfels (`meta.y == 0`) by emitting a degenerate triangle, not by branching the draw call.
- [ ] Add a uniform `radiusScale` (default 1.0) so users can shrink/inflate discs without re-spawning surfels.
- [ ] Add a uniform `solidAlpha` (default ~0.6) and an option to render as wireframe rings instead of filled discs for occlusion-heavy scenes.
- [ ] Render with depth test on, depth write off, and `GL_BLEND` enabled so discs read existing scene depth and overlay correctly on top of `HDRSceneTex` / forward output.

### 6.2 - Color-by attribute modes

Add a `ESurfelDebugColorMode` (mirrored as a uniform on the disc shader) that the ImGui panel can switch live, and surface each through `SurfelDebugRenderPipeline`:

- [ ] `Albedo` - sample stored `albedo.rgb` directly.
- [ ] `WorldNormal` - `normal * 0.5 + 0.5` after world-space reconstruction (lets users see normal-decode bugs immediately).
- [ ] `LocalNormal` - raw `normal.xyz * 0.5 + 0.5` before any matrix application (diff against `WorldNormal` to spot transform bugs).
- [ ] `TransformId` - hashed-color of `meta.z`, with a distinct sentinel color (e.g. magenta) for `meta.z == 0` so the "ambiguous zero" case lights up.
- [ ] `Age` - gradient over `frameIndex - meta.x` (green = fresh, red = stale, gray = beyond `MaxSurfelAgeFrames`).
- [ ] `RadiusHeat` - gradient over `posRadius.w` so radius-heuristic problems stand out.
- [ ] `CellOccupancy` - color by how full this surfel's grid cell is (`counts[cell] / maxPerCell`); useful for diagnosing grid saturation.
- [ ] `MatrixResolved` - solid green when `TryLoadWorldMatrix` succeeded for this surfel, red when it failed (renders the failed cases as discs sitting in local space at the world origin so they are visually obvious).
- [ ] `SurfelIndex` - hashed-color of `gl_InstanceID`, useful as a stable per-surfel identity unrelated to transform ID.

### 6.3 - Orientation and shape diagnostics

- [ ] Add an option to draw a short normal arrow (line segment from disc center along the world normal, length proportional to radius) so flipped or zero normals are visible without sampling the disc face.
- [ ] Add an option to render the disc as a half-disc plus a tangent tick so users can distinguish front-face from back-face when normals are nearly view-aligned.
- [ ] Add a "twin" mode that draws each surfel twice, once filled and once as a wireframe at 1.05x radius, to spot z-fighting against scene geometry.

### 6.4 - Filtering controls

- [ ] Add filter uniforms: by transform ID (single ID or all), by cell index/range, by minimum age, by activity flag, and by minimum/maximum radius.
- [ ] Add a "highlight" uniform that fades non-matching surfels to ~10% alpha instead of culling them, so context is preserved.
- [ ] Add a "freeze" toggle that snapshots the surfel buffer to a debug copy buffer, so the user can study one frame's surfels while spawn/recycle continues.

### 6.5 - Grid and counter overlays

- [ ] Keep the existing `GridHeatmap` mode but render it as 3D wireframe boxes per non-empty cell (instanced cubes sourcing `counts[cellIndex]`), not just a screen-space heat overlay.
- [ ] Color each cell box by `counts[cell] / maxPerCell`, with a saturated "overflow" color when `counts[cell] >= maxPerCell` to flag clipped cells.
- [ ] Render the active grid bounds (from `CurrentGridOrigin` + `CurrentCellSize * gridDim`) as a wireframe box so users can confirm the grid is camera-following correctly.

### 6.6 - Per-pixel input and scalar HUD

- [ ] Add a Surfel GI input validation overlay for depth, normal, albedo, and transform ID at the cursor pixel, including the decoded world position, decoded world normal, and the ID-resolved world matrix's translation.
- [ ] Add a surfel HUD panel showing: live surfel count (read from `stackTop`), free count, allocations this frame, recycled this frame, reused-on-spawn this frame, invalid-ID samples, failed `TryLoadWorldMatrix` counts, and average/max cell occupancy.
- [ ] Drive these counters from a small `SurfelGI_DebugCounters` SSBO incremented atomically inside Spawn/BuildGrid/Recycle/Shade; clear it once per frame on the GPU.
- [ ] Expose `SurfelGITexture` as a selectable output before composite (raw GI), and as a side-by-side with composited result.

### 6.7 - ImGui surfacing

- [ ] Add a "Surfel GI Debug" panel under the Global Editor Preferences / Render Debug section that toggles `SurfelDebugRenderPipeline`, picks `ESurfelDebugVisualization`, picks `ESurfelDebugColorMode`, and exposes the filter uniforms above.
- [ ] Persist the currently selected debug mode and color mode in editor preferences.
- [ ] Add MCP tool coverage for the same toggles so headless validation runs can capture screenshots in each mode (`SurfelGI.SetDebugMode`, `SurfelGI.SetColorMode`).

## Phase 7: Improve visual quality after correctness

- [ ] Tune surfel radius from camera projection or local geometric density instead of raw depth.
- [ ] Add distance and normal thresholds that reduce self-gathering artifacts.
- [ ] Add temporal smoothing only after identity and normal contracts are correct.
- [ ] Add basic emissive/direct-light contribution if the feature is intended to produce visible bounce rather than albedo-only tint.
- [ ] Add leakage and ghosting diagnostics for moving objects.
- [ ] Decide whether surfels should remain object-space for moving objects or use a hybrid static/dynamic storage policy.

## Phase 8: Targeted tests and validation scenes

- [ ] Update `ForwardDepthNormalVariantTests` for transform ID prepass output.
- [ ] Add Surfel GI compute tests for octahedral normal decode.
- [ ] Add Surfel GI compute tests for invalid transform ID rejection.
- [ ] Add Surfel GI compute tests for command-index matrix reconstruction.
- [ ] Add a mixed deferred/forward validation scene where a forward mesh occludes deferred geometry.
- [ ] Add a moving-object validation scene to check object-space surfel stability.
- [ ] Add a thin-wall or close-surface scene to reveal self-gathering and leak artifacts.
- [ ] Run targeted rendering tests after implementation work begins.

## Phase 9: Docs and final integration

- [ ] Update `docs/developer-guides/gi/surfel-gi.md` with the repaired input contracts and known limitations.
- [ ] Update `docs/architecture/rendering/default-render-pipeline-notes.md` if the prepass identity convention changes.
- [ ] Reconcile the C# `SurfelGPU.Meta` field comment (currently `Vector4 Meta; // x=frameIndex, y=reserved, z=reserved, w=reserved`) with the GLSL `uvec4 meta` layout (`x=lastUsedFrame, y=active, z=transformId`) so the storage convention does not drift.
- [ ] Link this todo from any relevant GI planning index if one exists.
- [ ] Record any remaining non-blocking quality issues as follow-up todos.
- [ ] Merge the completed Surfel GI repair branch back into `main` after implementation and validation are complete.

