# Surfel GI Repair TODO

Last Updated: 2026-10-06
Status: Active (provider unavailable)
Architecture: [Surfel GI guide](../../../../developer-guides/gi/surfel-gi.md), [Global Illumination Ownership And Selection](../../../../architecture/rendering/global-illumination-ownership.md)
Validation: [Global Illumination Validation](../../../testing/rendering/global-illumination-validation.md#surfel-gi)

## Current State

`SurfelGI` is an unavailable descriptor in `GlobalIlluminationProviderRegistry`. Only `SurfelDebugRenderPipeline` adds `VPRC_SurfelGIPass`. The pass runs Init, Recycle, ResetGrid, BuildGrid, Spawn, then Shade, so new surfels are not in the grid until the next frame. It clears its output and returns in stereo. `Spawn.comp` reads `gTransformId` as a command index, but `DepthNormalPrePass.fs` writes only `Normal`, so forward pixels have no identity. `Spawn.comp` and `Shade.comp` decode `texture(gNormal, uv).rgb` and rotate it by `cameraToWorldMatrix`, but the G-buffer stores an octahedral world-space `vec2`. `SurfelGPU.Meta` says `y` and `z` are reserved; GLSL uses `y` = active and `z` = transform ID. `VPRC_SurfelDebugVisualization` draws screen-space circles and a grid heatmap only.

## Open Code Items

### Forward Prepass Identity

Shared work: [Forward depth-normal TransformId TODO](../../forward-depth-normal-transform-id-todo.md).

- [ ] Write `TransformId` from the forward depth-normal prepass. Attach the main `TransformId` texture in the forward prepass merge FBO of both pipelines, keep prepass clears disabled, and write `TransformId = floatBitsToUint(FragTransformId)` in `DepthNormalPrePass.fs`, `ForwardDepthNormalVariantFactory` variants, and explicit `XRENGINE_DEPTH_NORMAL_PREPASS` branches. Emit `FragTransformId` from generated vertex programs when the prepass needs it. Done when: forward prepass pixels hold the same identity as deferred pixels.

### Identity Semantics

- [ ] Fix the identity convention. Confirm that the value from `PushTransformId` maps to `GPUScene.AllLoadedCommandsBuffer` indices, then reserve zero as "no object" (`commandIndex + 1`) if no consumer conflicts. Document the rule next to the texture creation and the shader decode. Done when: zero is never a valid command index.
- [ ] Reject invalid identities in `Spawn.comp`, `BuildGrid.comp`, and `Shade.comp` before they index transform buffers. Done when: an invalid or out-of-range ID skips the surfel.
- [ ] Align the `SurfelGPU.Meta` comment with the GLSL `uvec4 meta` layout (`x` = last used frame, `y` = active, `z` = transform ID). `VPRC_SurfelGIPass.cs`. Done when: the C# and GLSL layouts match.

### Normal And Depth Decode

- [ ] Decode normals with the octahedral decode from `NormalEncoding.glsl` through `.rg`, and remove the `cameraToWorldMatrix` normal rotation in `Spawn.comp` and `Shade.comp`. Done when: both shaders use the shared decode and treat the result as world space.
- [ ] Apply the same decode fix to the `LightVolumes` and `RadianceCascades` compute shaders if they use the same pattern. Done when: no GI compute shader decodes the normal texture as RGB.

### Spawn, Reuse, And Grid

- [ ] Fix the pass order so a surfel spawned or moved this frame is gatherable this frame: build the grid after spawn, or insert new and moved surfels during spawn with duplicate protection. Document the final order next to `Execute()`. Done when: the order is recycle, reset grid, spawn or update, build grid (or the documented equivalent).
- [ ] Make `Init.comp` start `meta.y` and `meta.z` at zero, make `Recycle.comp` clear `meta.z`, and recycle surfels whose transform ID no longer resolves. Done when: recycled slots hold no stale identity.
- [ ] Replace the single pseudo-random pixel per tile with deterministic sampling of coverage-poor pixels. Done when: spawn selection is deterministic for a given frame input.
- [ ] Set the correct memory barrier after every compute dispatch, make `SurfelGITexture` writes visible before the composite samples it, write every output pixel in `Shade.comp`, and declare image-write and composite dependencies in the render graph. Done when: each dispatch names its barrier and the graph declares the dependencies.
- [ ] Keep the CPU surfel update and grid-build paths allocation-free. Done when: no per-frame `new`, LINQ, or closures remain in `VPRC_SurfelGIPass`.

### Debug Tooling

- [ ] Add `SurfelGI_DebugCounters`, an SSBO cleared on the GPU each frame and incremented atomically in Spawn, BuildGrid, Recycle, and Shade: candidate tiles, rejected depth and normal pixels, invalid IDs, failed matrix loads, allocations, recycles, reuses. Done when: a HUD panel shows the counters and the live count from `stackTop`.
- [ ] Add `VPRC_SurfelDebugDrawPass`: one instanced, world-space oriented disc per surfel from the `SurfelGI_Surfels` SSBO, indirect instance count from `stackTop`, degenerate output for inactive surfels, depth test on and depth write off, `radiusScale`, `solidAlpha`, and a wireframe option. Done when: the pass draws with no CPU readback or per-frame allocation.
- [ ] Add `ESurfelDebugColorMode` with `Albedo`, `WorldNormal`, `LocalNormal`, `TransformId` (magenta for zero), `Age`, `RadiusHeat`, `CellOccupancy`, `MatrixResolved`, and `SurfelIndex`. Done when: each mode is selectable live.
- [ ] Add normal arrows, a half-disc front-face marker, and a twin filled-plus-wireframe mode. Done when: each option is a toggle.
- [ ] Add filters (transform ID, cell range, minimum age, active flag, radius range), a highlight mode that fades non-matching surfels, and a freeze toggle that snapshots the surfel buffer. Done when: each control changes the debug draw.
- [ ] Draw the grid heatmap as instanced 3D cell boxes colored by occupancy with an overflow color, plus the active grid bounds. Done when: the boxes replace the screen-space heatmap option.
- [ ] Add a cursor-pixel input overlay (depth, normal, albedo, transform ID, decoded world position and normal, resolved matrix translation) and a raw `SurfelGITexture` view beside the composite. Done when: both views exist.
- [ ] Add a "Surfel GI Debug" editor panel, persist the selected modes in editor preferences, and add MCP tools for the debug and color modes. Run `pwsh Tools/Reports/generate_mcp_docs.ps1` after adding the tools. Done when: the panel and tools change the same settings.

### Quality (After Correctness)

- [ ] Set surfel radius from camera projection or local geometric density. Add distance and normal thresholds against self-gathering. Done when: radius no longer comes from raw depth.
- [ ] Add temporal smoothing and emissive and direct-light contribution. Done when: both are behind settings.
- [ ] Add stereo support, or keep stereo disabled with an explicit diagnostic instead of a silent clear. Done when: stereo either renders or logs why it does not.

### Validation Scenes

- [ ] Add mixed deferred and forward, moving-object, and thin-wall Surfel GI scenes to Unit Testing World settings. Done when: each scene loads from a setting.

### Tests (Owner Clearance Required)

- [ ] Update `ForwardDepthNormalVariantTests` for the prepass `TransformId` output and `FragTransformId` input. Done when: the tests cover both.
- [ ] Add tests for octahedral normal decode, invalid ID rejection, command-index matrix lookup, synthetic G-buffer reconstruction, reused surfel movement across cells, and same-frame gather of a new surfel. `XREngine.UnitTests/Rendering/SurfelGiComputeIntegrationTests.cs`. Done when: each behavior has a deterministic test.

## Decisions Needed

- [ ] Keep surfels object-space for moving objects, or use a hybrid static and dynamic storage policy. Owner: rendering lead.
- [ ] Decide when Surfel GI becomes a registered module (after the identity and decode fixes, or after quality work). Owner: rendering lead.

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/rendering/global-illumination/surfel-gi-repair-todo.md`

- [ ] Audit what value `RenderCommandMesh3D` pushes through `PushTransformId`.
- [ ] Replace the Surfel GI custom `DecodeNormal(vec3)` logic with octahedral decoding matching `NormalEncoding.glsl` (the Normal texture is `vec2` octahedral, populated via `XRENGINE_EncodeNormal`).
- [ ] Fix the dispatch ordering bug: `Execute()` runs `BuildGrid` before `Spawn`, so newly allocated surfels are not present in this frame's grid and cannot be gathered until next frame. Either move grid build after spawn, or have spawn insert new (and moved-reused) surfels into the grid with duplicate protection.
- [ ] Add a test that spawns a surfel and confirms it is gatherable in the same frame.
- [ ] Use `glDrawArraysIndirect` / `glMultiDrawArraysIndirect` with `stackTop` (clamped to `MaxSurfels`) as the instance count, so dispatch matches GPU-resident state without a CPU sync.
- [ ] Skip inactive surfels (`meta.y == 0`) by emitting a degenerate triangle, not by branching the draw call.
- [ ] Add a uniform `radiusScale` (default 1.0) so users can shrink/inflate discs without re-spawning surfels.
- [ ] `RadiusHeat` - gradient over `posRadius.w` so radius-heuristic problems stand out.
- [ ] `CellOccupancy` - color by how full this surfel's grid cell is (`counts[cell] / maxPerCell`); useful for diagnosing grid saturation.
- [ ] `MatrixResolved` - solid green when `TryLoadWorldMatrix` succeeded for this surfel, red when it failed (renders the failed cases as discs sitting in local space at the world origin so they are visually obvious).
- [ ] `SurfelIndex` - hashed-color of `gl_InstanceID`, useful as a stable per-surfel identity unrelated to transform ID.
- [ ] Keep the existing `GridHeatmap` mode but render it as 3D wireframe boxes per non-empty cell (instanced cubes sourcing `counts[cellIndex]`), not just a screen-space heat overlay.
- [ ] Color each cell box by `counts[cell] / maxPerCell`, with a saturated "overflow" color when `counts[cell] >= maxPerCell` to flag clipped cells.
- [ ] Render the active grid bounds (from `CurrentGridOrigin` + `CurrentCellSize * gridDim`) as a wireframe box so users can confirm the grid is camera-following correctly.
- [ ] Expose `SurfelGITexture` as a selectable output before composite (raw GI), and as a side-by-side with composited result.
- [ ] Add a "Surfel GI Debug" panel under the Global Editor Preferences / Render Debug section that toggles `SurfelDebugRenderPipeline`, picks `ESurfelDebugVisualization`, picks `ESurfelDebugColorMode`, and exposes the filter uniforms above.
- [ ] Add MCP tool coverage for the same toggles so headless validation runs can capture screenshots in each mode (`SurfelGI.SetDebugMode`, `SurfelGI.SetColorMode`).
- [ ] Reconcile the C# `SurfelGPU.Meta` field comment (currently `Vector4 Meta; // x=frameIndex, y=reserved, z=reserved, w=reserved`) with the GLSL `uvec4 meta` layout (`x=lastUsedFrame, y=active, z=transformId`) so the storage convention does not drift.
