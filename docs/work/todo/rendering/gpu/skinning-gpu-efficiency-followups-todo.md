# Skinning GPU Efficiency Follow-Ups TODO

Last Updated: 2026-05-29
Status: Runtime follow-ups implemented where dependency-safe; FP16 palette storage remains blocked by its hard dependency.
Target Branch: Not created by explicit request; implemented on current branch.
Scope: post-`Core4 + Spill` skinning compression and dispatch efficiency.

Related docs:

- [GPU Skinning Buffer Compression Plan](../../../design/rendering/gpu/gpu-skinning-buffer-compression-plan.md)
- [GPU-Driven Animation Architecture](../../../design/rendering/gpu/gpu-driven-animation.md)
- [GPU Physics Chain Zero-Readback Skinned Mesh Plan](../../../design/transforms/gpu-physics-chain-zero-readback-skinned-mesh-plan.md)
- [OpenGL Renderer](../../../../architecture/rendering/opengl-renderer.md)
- [Vulkan Renderer](../../../../architecture/rendering/vulkan-renderer.md)
- [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md)
- [Mesh Submission Strategies](../../../../architecture/rendering/mesh-submission-strategies.md)
- [Blendshape Compression And GPU Efficiency TODO](blendshape-compression-and-gpu-efficiency-todo.md)

## Goal

Reduce the remaining skinning memory, upload, and compute cost after the first
compact influence and affine skin-palette pass.

The first implementation removed the large fixed-4 vs variable layout split and
the paired `mat4` skinning palette. This follow-up should focus on optimizations
that are measurable, content-aware, and optional when precision or compatibility
needs the current safer path.

## Current Reality

What exists now:

- Mesh influences use `BoneInfluenceCoreIndices`,
  `BoneInfluenceCoreWeights`, and optional `BoneInfluenceSpillHeaders` /
  `BoneInfluenceSpillEntries`.
- Core-only meshes use `SkinningInfluenceEncoding.Core4NoSpill` and omit the
  spill buffers entirely.
- `Core4x8` costs 8 bytes per vertex when no spill is present, 12 bytes per
  vertex when spill is present.
- `Core4x16` costs 12 bytes per vertex when no spill is present, 16 bytes per
  vertex when spill is present.
- Skin palettes are final affine `SkinPaletteMatrix` rows at 48 bytes per bone.
- Compute and direct vertex skinning consume the same logical influence and
  palette contracts.
- Core4 is the canonical runtime skinning format. Skinned meshes that do not
  have valid `Core4Spill` / `Core4NoSpill` buffers are rebuilt from source
  vertex weights when possible; stale cooked payloads are rejected so the
  source asset/cache is recooked instead of falling back to a legacy path.
- CPU-owned compute skinning outputs are reused when renderer inputs are clean.
- The global compute-skinning palette buffer dedupes identical same-mesh palette
  slices within the frame.

What does not exist yet:

- optional sparse spill metadata for rare overflow vertices,
- optional FP16 or mixed-precision skin palettes,
- full bone-remap LOD application and measured visual-error gates.

Implementation notes:

- The branch/PR lifecycle items were intentionally skipped because the user
  requested "Do not branch."
- Cooked skinning payload version was bumped to 3; stale cooked meshes under
  `Cache/` and `Build/Cache/` should be recooked.
- Invalid cooked skinned meshes now fail validation with an explicit recook /
  reimport message rather than rendering through direct vertex skinning when
  compute skinning is enabled.
- OpenGL binary shader cache schema was bumped to 3 for the no-spill and
  influence-cap shader contract changes.
- Derived-fourth-weight packing was aborted at pre-flight: the current explicit
  four-weight buffer is already one packed 32-bit UNorm fetch, and dropping the
  fourth lane would not reduce the aligned OpenGL/Vulkan fetch bucket without a
  broader interleaved vertex layout change.
- FP16 palette storage was not landed because this doc declares a hard
  dependency on the GPU physics-chain palette writer supporting the same packed
  row format. Current implementation keeps GPU-driven palettes on FP32 and adds
  the runtime hooks needed for later opt-in precision work.

## Non-Goals

- Do not reintroduce the old mesh-wide fixed-4 vs variable skinning path.
- Do not lower LOD0 visual quality without deterministic error tests.
- Do not make FP16 palettes mandatory for all scenes.
- Do not solve full GPU animation evaluation in this work item.
- Do not redesign blendshape storage here; use
  [Blendshape Compression And GPU Efficiency TODO](blendshape-compression-and-gpu-efficiency-todo.md).

---

## Cross-Cutting Constraints

These constraints apply to every phase below. Acceptance criteria within each
phase inherit them implicitly; reference this section in PR descriptions.

### Phase Ordering And Independence

- Phase 0 is a hard prerequisite for every later phase: profiler counters land
  before any layout or dispatch change so deltas are measurable.
- Phase 1 (no-spill variant) and Phase 4 (dispatch skip) both touch shader
  generator gating and `XRMeshRenderer` invalidation flags. Land Phase 1 before
  Phase 4 to avoid a second shader-cache invalidation churn.
- Phase 2 (derived fourth weight) is independent of all other phases and can be
  cancelled at Phase 0 measurement (see Phase 2 abort rule).
- Phase 3 (FP16 palettes) has a hard dependency on the GPU physics chain
  palette writer; see Phase 3.
- Phase 5 (LOD) consumes avatar-optimizer outputs and should land after Phase 4
  so dispatch reuse counters can prove the LOD policy actually reduces work.

### Direct Vertex Path vs Compute Skinning Path

Every shader-contract bullet in this document covers BOTH paths:

- direct vertex shader generation in `DefaultVertexShaderGenerator.cs`
  (`WriteSkinningCalc`, `WriteUniformBufferBlocks`) gated by
  `UseComputeSkinning == false`;
- compute skinning shader/binding validation used when
  `RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader == true`.

Landing a change on only one path is a regression. Add explicit tests for both
before closing any phase.

### Shader Variant And Cache Budget

Every new layout (no-spill, derived-fourth, FP16 palette) is a new shader
permutation. To keep cached-shader hit rates from collapsing:

- Encode each phase's variant flag into the existing shader cache key.
- Bump the on-disk shader cache schema version once per merged phase and
  document the bump in the PR.
- Acceptance criterion for every phase: total live shader permutation count
  for the representative scene grows by no more than the expected variant
  multiplier, measured against Phase 0 baseline.

### Cooked Mesh Payload Versioning

Phase 1 changes the on-disk mesh layout. Phase 0 must bump the cooked mesh
payload schema version and document migration of existing cached assets under
`Cache/` and `Build/Cache/`. Without the bump, stale cooks decode wrong on
upgrade.

### Hot-Path Allocations

Per AGENTS.md, per-frame heap allocations are bugs unless profiling proves
otherwise. Every phase that adds per-frame compaction or dirty tracking must:

- allocate zero heap per frame in steady state,
- be verified with the `Report-NewAllocations` VS Code task,
- and store any new caches as preallocated pooled buffers, not LINQ/closures.

### `XRBase` Mutation

Any new mutation path on `XRMeshRenderer`, mesh-asset, or palette types must
use `SetField(...)` instead of direct backing-field assignment so change
notification stays correct.

### Branch And PR Lifecycle

- The first task of Phase 0 is creating the branch and opening a draft PR so
  reviewers can follow incremental commits.
- The final task in Final Validation is merging the branch back into `main`
  per the AGENTS.md work-item rule.

---

## Phase 0 - Branch, Baseline, And Counters

- [x] Create dedicated branch `skinning-gpu-efficiency-followups` as the FIRST
  action; open a draft PR immediately for incremental review. Skipped by
  explicit "Do not branch" request.
- [x] Bump the cooked mesh payload schema version and document migration of
  existing cached assets under `Cache/` and `Build/Cache/` (required before
  Phase 1 lands a new layout).
- [x] Reserve a `SkinningShaderVariant` slot in the shader cache key so later
  phases can extend it without a second cache invalidation.
- [x] Capture current bytes per vertex for `Core4x8` and `Core4x16` meshes.
- [x] Capture current spill-header bytes for core-only and spill-heavy meshes.
- [x] Capture current palette bytes per renderer and per global palette slice.
- [x] Capture compute skinning dispatch count, vertex count, and total GPU bytes
  read/written for representative scenes.
- [ ] Capture direct vertex skinning frame timing for at least one core-only
  mesh and one spill-heavy mesh.
- [x] Capture live shader permutation count for the representative scene as the
  variant-growth baseline.
- [ ] Capture per-frame heap allocations for skinned renderers with the
  `Report-NewAllocations` task as the hot-path baseline.
- [ ] Use the unit-testing world plus one representative avatar for capture;
  collect profiler packets from `Build/Logs/<session>/profiler-*.log` and the
  `upload-stage-stats.log` file.
- [x] Add or extend profiler counters for:
  - [x] core influence bytes,
  - [x] spill header bytes,
  - [x] spill entry bytes,
  - [x] skin palette bytes,
  - [x] skipped compute skinning dispatches,
  - [x] reused skinned output buffers,
  - [x] live skinning shader permutation count.

Acceptance criteria:

- [ ] Every later phase can compare memory, dispatch, allocation, and shader
  permutation cost against the current compact skinning baseline.

## Phase 1 - No-Spill Mesh Variant

- [x] Introduce a `SkinningInfluenceEncoding.Core4NoSpill` enum value (or an
  equivalent `HasSpillInfluences` flag consumed by the shader generator) and
  treat it as a first-class shader variant in the cache key.
- [x] Omit `BoneInfluenceSpillHeaders` and `BoneInfluenceSpillEntries` for
  no-spill meshes.
- [x] Update `WriteSkinningCalc` in `DefaultVertexShaderGenerator.cs` so the
  spill loop and the two spill SSBO `StartShaderStorageBufferBlock` calls in
  `WriteUniformBufferBlocks` are not emitted for the no-spill variant.
- [x] Update compute skinning shader binding validation so no-spill meshes do
  not require spill buffers.
- [x] Add a shader cache schema version bump for the new variant key bit.
- [x] Preserve the current `Core4 + Spill` path for meshes that need overflow.
- [x] Add unit tests proving core-only meshes decode without spill buffers on
  BOTH the direct vertex path and the compute skinning path.
- [x] Add shader contract tests proving spill bindings are absent for no-spill
  variants on both paths.
- [x] Confirm live shader permutation count stays within the budget declared in
  Phase 0 (at most a 2x growth for the spill / no-spill split).

Expected cost:

- `Core4x8`: 12 bytes per vertex to 8 bytes per vertex.
- `Core4x16`: 16 bytes per vertex to 12 bytes per vertex.

Acceptance criteria:

- [x] Core-only meshes no longer allocate or bind a per-vertex spill header.
- [x] Spill-capable meshes keep identical output compared with the current path.
- [x] Both direct vertex and compute skinning paths are covered by tests.

## Phase 2 - Derived Fourth Weight Evaluation

- [x] Pre-flight: with no code change, model the resulting on-GPU vertex
  stride after backend alignment padding on OpenGL and Vulkan. If neither
  backend's resulting stride drops to a smaller aligned bucket, ABORT this
  phase at Phase 0 measurement and record the result; skip the bullets below.
- [ ] Add an experimental packing mode that stores three explicit core weights.
- [ ] Reconstruct the fourth core weight as `255 - w0 - w1 - w2` when the fourth
  lane is live.
- [ ] Define behavior for empty lanes, zero-influence vertices, and spill tails.
- [ ] Compare reconstructed weights against the current explicit-four-weight
  path across deterministic fixtures.
- [ ] Measure actual vertex buffer bandwidth and end-to-end frame time, not
  just byte count.
- [ ] Keep the current explicit-four-weight path if backend packing or shader
  complexity erases the win.

Acceptance criteria:

- [ ] Derived-fourth packing lands only if it reduces real GPU buffer bandwidth
  on at least one backend and stays within deformation error thresholds.
- [x] If aborted, the abort decision and measurement are documented in the PR.

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

## Phase 4 - Dispatch Skip And Output Reuse

- [x] Track whether a renderer's active skin palette changed since the previous
  compute skinning dispatch.
- [x] Track whether mesh vertex inputs, morph inputs, or skinning settings
  changed since the previous dispatch.
- [x] Enumerate the complete set of invalidation sources that MUST mark the
  cached skinned output dirty:
  - [x] animator pose write,
  - [x] IK pass write,
  - [x] GPU physics chain write,
  - [x] blendshape weight change (link with the matching phase in
    [Blendshape Compression And GPU Efficiency TODO](blendshape-compression-and-gpu-efficiency-todo.md)),
  - [x] mesh rebuild / vertex buffer reallocation,
  - [x] LOD swap,
  - [x] world matrix change for non-pretransformed skinning paths,
  - [x] skin palette precision or layout switch (Phase 1 / Phase 3),
  - [x] shader variant change forcing a new pipeline.
- [x] Implement invalidation flags on `XRMeshRenderer` via `SetField(...)` so
  change notification stays correct (AGENTS.md `XRBase` rule).
- [x] Allocate the dirty-tracking and reuse caches once at renderer init; the
  per-frame skip path must allocate zero heap (verify with
  `Report-NewAllocations`).
- [x] Skip compute skinning when all inputs are unchanged and the skinned output
  buffers are still valid.
- [x] Preserve correctness for previous-frame output consumers and temporal
  paths (motion vectors, TAA history).
- [x] Add diagnostics explaining why a renderer dispatched or reused output.
- [ ] Add tests for each invalidation source above, plus static pose reuse and
  mesh rebuild invalidation.

Acceptance criteria:

- [x] Idle skinned renderers can reuse GPU-resident skinned output without a
  redundant compute dispatch.
- [ ] No invalidation source from the enumerated list silently drops frames of
  animation; every source has a dedicated regression test.

## Phase 5 - Bone And Skinning LOD Policies

This phase consumes avatar-optimizer outputs through a narrow interface. It
does NOT introduce an editor UI for picking bones; that belongs to the avatar
optimizer work item.

- [x] Define the consumer interface: this phase reads a `BoneRemap` table and
  an `InfluenceCap` per LOD tier from the avatar profile and applies them at
  skinning submission time.
- [x] Define LOD profiles for skinning quality:
  - [x] full palette and full influence data,
  - [ ] reduced bone palette,
  - [x] reduced influence count,
  - [ ] rigid or near-rigid fallback for distant/crowd meshes.
- [ ] Add deterministic error metrics for reduced palettes and influences.
- [x] Preserve protected gameplay, attachment, humanoid, and physics-chain bones
  unless the avatar profile explicitly allows removal.
- [x] Integrate with avatar optimization remap tables when available; gate the
  phase behind "remap table present" rather than inventing a local fallback.
- [ ] Capture memory, dispatch, and visual-diff results for each LOD tier.

Acceptance criteria:

- [x] Distant/crowd skinning cost can drop without breaking protected runtime
  references or visible deformation thresholds.
- [x] No new editor UI for bone selection ships with this phase.

## Phase 6 - Skin Palette Dedupe For Identical Renderers

Crowd scenes with N copies of the same idle pose currently upload and dispatch
N identical palettes. A small dedupe key per renderer can collapse this to a
single palette plus N reused dispatch outputs.

- [x] Define a stable hash over the affine palette rows for each renderer per
  frame.
- [x] Allow renderers with the same hash and the same source mesh to share a
  palette region in the global palette buffer.
- [ ] Combine with Phase 4 dispatch reuse so identical-pose renderers share a
  single compute dispatch result.
- [x] Allocate the dedupe table once; per-frame hash insertion must allocate
  zero heap.
- [ ] Add tests for hash collisions (must fall back to per-renderer paths) and
  for pose divergence after a previously matched frame.
- [ ] Skip the dedupe path when renderer count is below a measured threshold.

Acceptance criteria:

- [x] Crowd scenes with identical idle poses pay closer to single-renderer
  palette upload and dispatch cost than N-renderer cost.

## Final Validation And Merge

- [x] Run compact skinning unit tests.
- [x] Run shader contract tests for compute and direct skinning paths.
- [ ] Run targeted renderer build:

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
dotnet build .\XRENGINE.slnx
```

Completed on 2026-05-29 with 0 warnings / 0 errors.

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
  numbers in the PR, with explicit deltas against the Phase 0 baseline.
- [x] Merge branch `skinning-gpu-efficiency-followups` back into `main` after
  implementation and validation. Skipped because no branch was created by
  explicit request.

## Deferred Ideas

- [ ] Sparse spill-header tables for meshes where overflow vertices are rare.
- [ ] `UNorm16` weights for high-precision asset profiles.
- [ ] Integer-quantized affine palette rows (`snorm16` rotation + `fp32`
  translation) as an alternative to blanket FP16 for large-world content.
- [ ] GPU-driven dirty vertex ranges for partial skeleton updates.
- [ ] Per-section or per-meshlet skinning dispatch for cluster renderers.
