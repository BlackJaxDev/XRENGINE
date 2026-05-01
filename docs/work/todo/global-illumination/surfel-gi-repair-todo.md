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
- [ ] Update `DepthNormalPrePass.fs` to declare `FragTransformId` and write `TransformId`.
- [ ] Update `ForwardDepthNormalVariantFactory` so generated prepass variants write both normal and transform ID.
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

- [ ] Replace the Surfel GI custom `DecodeNormal(vec3)` logic with octahedral decoding matching `NormalEncoding.glsl`.
- [ ] Sample normal textures through `.rg`, not `.rgb`, in `Spawn.comp` and `Shade.comp`.
- [ ] Remove the extra `cameraToWorldMatrix` normal transform if sampled G-buffer normals are already world-space.
- [ ] Audit `LightVolumes` and `RadianceCascades` compute shaders for the same normal decode bug and decide whether to repair them in the same GI input-contract pass.
- [ ] Confirm `DepthView` is a non-linear depth view compatible with the current inverse-projection reconstruction.
- [ ] Add a small shader/unit test that reconstructs normal and world position from synthetic G-buffer inputs.
- [ ] Update Surfel GI docs if the input texture contract changes.

## Phase 4: Repair surfel spawn, reuse, and grid update behavior

- [ ] Add a debug mode that counts candidate spawn tiles, rejected depth pixels, rejected normal pixels, invalid transform IDs, and allocated surfels.
- [ ] Revisit the spawn heuristic so it samples deterministic coverage-poor pixels rather than only one pseudo-random pixel per tile.
- [ ] Fix same-frame visibility for reused surfels that move after `BuildGrid.comp`.
- [ ] Decide whether reused surfels should be reinserted into the grid during spawn with duplicate protection.
- [ ] Add an alternative order of recycle, reset grid, spawn/update, then build grid if that is cleaner.
- [ ] Keep surfel update and grid-build paths allocation-free on CPU.
- [ ] Add tests for reused surfel movement across grid cells.

## Phase 5: Verify compute barriers and composition

- [ ] Confirm every Surfel GI compute dispatch uses the correct memory barrier for the next pass.
- [ ] Confirm `SurfelGITexture` image writes are visible before `SurfelGICompositeFBO` samples it.
- [ ] Confirm the composite pass blends into the expected forward/HDR target in both standard and MSAA-deferred modes.
- [ ] Add clear or overwrite guarantees for every output pixel in `Shade.comp`.
- [ ] Add a render-graph description update if the synthetic pass is missing image-write or composite dependencies.
- [ ] Keep stereo behavior explicitly disabled or add a tracked follow-up for stereo Surfel GI.

## Phase 6: Add practical debug views

- [ ] Add or expose `TransformId` debug output in the ImGui editor while Surfel GI is active.
- [ ] Add a Surfel GI input validation overlay for depth, normal, albedo, and transform ID at the sampled pixel.
- [ ] Add a surfel allocation/count panel.
- [ ] Add surfel grid occupancy visualization.
- [ ] Add invalid-ID and failed-matrix-load counters.
- [ ] Add a mode to visualize surfels colored by transform ID.
- [ ] Add a mode to visualize surfels colored by normal direction.
- [ ] Add a mode to visualize `SurfelGITexture` before composite.

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

- [ ] Update `docs/features/gi/surfel-gi.md` with the repaired input contracts and known limitations.
- [ ] Update `docs/architecture/rendering/default-render-pipeline-notes.md` if the prepass identity convention changes.
- [ ] Link this todo from any relevant GI planning index if one exists.
- [ ] Record any remaining non-blocking quality issues as follow-up todos.
- [ ] Merge the completed Surfel GI repair branch back into `main` after implementation and validation are complete.

