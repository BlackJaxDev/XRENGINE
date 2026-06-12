# Skinning GPU Efficiency Follow-Ups TODO

Last Updated: 2026-06-12
Status: Remaining baseline capture, mixed-precision palette work, dispatch reuse
tests, skinning LOD completion, palette-dedupe completion, and final validation.
Target Branch: Not created by explicit request; implemented on current branch.
Scope: unfinished post-`Core4 + Spill` skinning compression and dispatch
efficiency work.

Implemented skinning behavior has moved to
[Skinning](../../../../developer-guides/rendering/skinning.md). Longer-horizon ideas have moved
to [Skinning Deferred GPU Efficiency Design](../../../design/rendering/gpu/skinning-deferred-gpu-efficiency-design.md).

Related docs:

- [Skinning](../../../../developer-guides/rendering/skinning.md)
- [Skinning Deferred GPU Efficiency Design](../../../design/rendering/gpu/skinning-deferred-gpu-efficiency-design.md)
- [GPU Skinning Buffer Compression Plan](../../../design/rendering/gpu/gpu-skinning-buffer-compression-plan.md)
- [GPU-Driven Animation Architecture](../../../design/rendering/gpu/gpu-driven-animation.md)
- [GPU Physics Chain Zero-Readback Skinned Mesh Plan](../../../design/transforms/gpu-physics-chain-zero-readback-skinned-mesh-plan.md)
- [OpenGL Renderer](../../../../architecture/rendering/opengl-renderer.md)
- [Vulkan Renderer](../../../../architecture/rendering/vulkan-renderer.md)
- [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md)
- [Mesh Submission Strategies](../../../../architecture/rendering/mesh-submission-strategies.md)
- [Blendshape Compression And GPU Efficiency TODO](blendshape-compression-and-gpu-efficiency-todo.md)

## Goal

Close remaining correctness and validation gaps for the current compact
skinning runtime, then land optional mixed-precision palettes, measured skinning
LOD improvements, and complete palette-dedupe dispatch reuse.

## Non-Goals

- Do not reintroduce the old mesh-wide fixed-4 vs variable skinning path.
- Do not lower LOD0 visual quality without deterministic error tests.
- Do not make FP16 palettes mandatory for all scenes.
- Do not solve full GPU animation evaluation in this work item.
- Do not redesign blendshape storage here; use
  [Blendshape Compression And GPU Efficiency TODO](blendshape-compression-and-gpu-efficiency-todo.md).

## Cross-Cutting Constraints

### Direct Vertex Path vs Compute Skinning Path

Remaining shader-contract work must cover BOTH paths:

- direct vertex shader generation in `DefaultVertexShaderGenerator.cs`
  (`WriteSkinningCalc`, `WriteUniformBufferBlocks`) gated by
  `UseComputeSkinning == false`;
- compute skinning shader/binding validation used when
  `RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader == true`.

Landing a change on only one path is a regression. Add explicit tests for both
before closing relevant tasks.

### Shader Variant And Cache Budget

Every new layout or precision mode is a shader permutation. To keep cached
shader hit rates from collapsing:

- Encode any new variant flag into the existing shader cache key.
- Bump the on-disk shader cache schema version when a new shader contract lands.
- Measure live skinning shader permutation count for representative scenes.

### Cooked Mesh Payload Versioning

Any future incompatible cooked skinning layout must bump the cooked mesh payload
schema and document migration of existing cached assets under `Cache/` and
`Build/Cache/`.

### Hot-Path Allocations

Per AGENTS.md, per-frame heap allocations are bugs unless profiling proves
otherwise. Remaining per-frame skinning work must:

- allocate zero heap per frame in steady state,
- be verified with the `Report-NewAllocations` VS Code task,
- and store any new caches as preallocated pooled buffers, not LINQ/closures.

### `XRBase` Mutation

Any new mutation path on `XRMeshRenderer`, mesh-asset, or palette types must
use `SetField(...)` instead of direct backing-field assignment so change
notification stays correct.

## Phase 0 - Remaining Baseline Capture

- [ ] Capture direct vertex skinning frame timing for at least one core-only
  mesh and one spill-heavy mesh.
- [ ] Capture per-frame heap allocations for skinned renderers with the
  `Report-NewAllocations` task as the hot-path baseline.
- [ ] Use the unit-testing world plus one representative avatar for capture;
  collect profiler packets from `Build/Logs/<session>/profiler-*.log` and the
  `upload-stage-stats.log` file.

Acceptance criteria:

- [ ] Every later phase can compare memory, dispatch, allocation, and shader
  permutation cost against the current compact skinning baseline.

## Phase 3 - Mixed-Precision Skin Palettes

HARD DEPENDENCY: this phase cannot land before the GPU physics chain palette
writer referenced in `gpu-physics-chain-zero-readback-skinned-mesh-plan.md`
supports the chosen FP16 row format. Confirm and link the relevant commit
before opening this phase's PR.

- [ ] Add optional FP16 affine skin palette storage. Blocked by hard dependency
  on the GPU physics-chain palette writer.
- [ ] Define the exact packed row format and alignment for OpenGL and Vulkan.
- [ ] Update the GPU physics chain palette writer to emit the same FP16 row
  format, or gate the FP16 path off when chain-driven palettes are present.
- [ ] Keep FP32 palettes available for large-world, high-precision, and
  validation modes.
- [ ] Add CPU tests comparing FP32 and FP16 palette transform output.
- [ ] Add shader tests for position, normal, and tangent transforms under:
  - [ ] identity transforms,
  - [ ] long bone chains,
  - [ ] non-uniform scale,
  - [ ] large translations,
  - [ ] tiny bones (fingers, eyes, jaw, teeth) where small-scale FP16
    underflow distorts the cofactor normal transform,
  - [ ] inverted, negative, and mirrored scale (the cofactor path in
    `WriteSkinningCalc` is sensitive to sign and small magnitude),
  - [ ] GPU physics-chain-driven palettes.
- [ ] Add a renderer setting or asset profile flag to choose palette precision.
- [ ] Add a new shader variant key bit for palette precision; bump shader cache
  schema version when this phase merges.
- [ ] Capture visual diffs on representative skinned characters.
- [ ] Cover BOTH direct vertex and compute skinning paths in every test above.

Expected cost:

- FP32 affine palette: 48 bytes per bone.
- FP16 affine palette: 24 bytes per bone.

Acceptance criteria:

- [ ] FP16 palettes are opt-in or profile-selected until visual and numeric
  error is proven acceptable for the target content.
- [ ] GPU-physics-chain content does not silently regress under FP16.

## Phase 4 - Dispatch Reuse Regression Tests

- [ ] Add tests for every compute-skinning invalidation source:
  - [ ] animator pose write,
  - [ ] IK pass write,
  - [ ] GPU physics chain write,
  - [ ] blendshape weight change,
  - [ ] mesh rebuild / vertex buffer reallocation,
  - [ ] LOD swap,
  - [ ] world matrix change for non-pretransformed skinning paths,
  - [ ] skin palette precision or layout switch,
  - [ ] shader variant change forcing a new pipeline.
- [ ] Add static pose reuse tests.
- [ ] Add mesh rebuild invalidation tests.

Acceptance criteria:

- [ ] No invalidation source silently drops frames of animation; every source
  has a dedicated regression test.

## Phase 5 - Bone And Skinning LOD Completion

This phase consumes avatar-optimizer outputs through a narrow interface. It
does NOT introduce an editor UI for picking bones; that belongs to the avatar
optimizer work item.

- [ ] Complete reduced bone palette LOD tier behavior.
- [ ] Add rigid or near-rigid fallback for distant/crowd meshes.
- [ ] Add deterministic error metrics for reduced palettes and influences.
- [ ] Capture memory, dispatch, and visual-diff results for each LOD tier.

Acceptance criteria:

- [ ] Distant/crowd skinning cost drops without breaking protected runtime
  references or visible deformation thresholds.

## Phase 6 - Palette Dedupe Dispatch Reuse

- [ ] Combine palette dedupe with Phase 4 dispatch reuse so identical-pose
  renderers share a single compute dispatch result.
- [ ] Add tests for hash collisions; collisions must fall back to per-renderer
  paths.
- [ ] Add tests for pose divergence after a previously matched frame.
- [ ] Skip the dedupe path when renderer count is below a measured threshold.

Acceptance criteria:

- [ ] Crowd scenes with identical idle poses pay closer to single-renderer
  palette upload and dispatch cost than N-renderer cost.

## Final Validation

- [ ] Run `Build-Editor` then `Start-Editor-NoDebug` with the unit-testing
  world and at least one representative avatar; collect the same profiler
  packets captured in Phase 0 from
  `Build/Logs/<configuration>_<tfm>/<platform>/<session>/profiler-*.log` and
  `upload-stage-stats.log`.
- [ ] Run `Report-NewAllocations` and confirm no new per-frame allocations
  appeared in any skinning path.
- [ ] Run visual validation for core-only, spill-heavy, FP16 palette, crowd
  dedupe, and GPU-driven palette scenes.
- [ ] Record before/after memory, timing, allocation, and shader permutation
  numbers, with explicit deltas against the Phase 0 baseline.
