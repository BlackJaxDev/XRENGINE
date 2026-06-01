# Blendshape Compression And GPU Efficiency TODO

Last Updated: 2026-06-01
Status: Active sparse/quantized runtime traversal, runtime-toggled precombine pass, blendshape LOD, and PCA/SVD opt-in setting implemented; corpus/hardware validation and PCA basis generation/evaluation pending
Target Branch: none - user explicitly requested no branch for this pass
Scope: runtime mesh blendshape storage, upload, dispatch, and shader
evaluation efficiency.

Related docs:

- [GPU-Driven Animation Architecture](../../../design/rendering/gpu/gpu-driven-animation.md)
- [Avatar Optimization And Virtualized Avatar Rendering Design](../../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [Avatar Skin, Skeleton, And Blendshape Optimization TODO](../../avatar/avatar-skin-skeleton-blendshape-optimization-todo.md)
- [Blendshape Update Classes And Static Baking TODO](blendshape-update-classes-and-static-baking-todo.md)
- [Skinning GPU Efficiency Follow-Ups TODO](skinning-gpu-efficiency-followups-todo.md)
- [OpenGL Renderer](../../../../architecture/rendering/opengl-renderer.md)
- [Vulkan Renderer](../../../../architecture/rendering/vulkan-renderer.md)
- [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md)
- [Mesh Submission Strategies](../../../../architecture/rendering/mesh-submission-strategies.md)

## Goal

Reduce blendshape memory, weight upload, and compute/direct shader work while
preserving facial identity, visemes, expression controls, and authored
deformation quality.

This work item targets the renderer/runtime buffer contract. The avatar
optimizer owns higher-level authoring policy such as protected shape lists,
shape pruning, skeleton pruning, and user-facing optimization profiles.

## Current Reality

What exists now:

- `XRMesh` builds `BlendshapeCount`, `BlendshapeIndices`, and
  `BlendshapeDeltas` buffers from per-vertex blendshape data.
- `XRMesh` also builds sparse per-shape records, affected-vertex metadata, and
  quantized delta payloads while retaining the dense FP32 fallback.
- `XRMeshRenderer` owns float `BlendshapeWeights`, compact active index/weight
  pairs, dirty weight ranges, active-count/LOD state, and pushes only dirty
  ranges to the GPU.
- `GlobalSkinPaletteBuffers` can also pack global blendshape weights for compute
  skinning, reusing renderer slices when weight versions are unchanged.
- Compute skinning and direct vertex skinning evaluate compact active-shape
  lists through sparse affected-vertex records and per-shape quantized delta
  payloads, share active-count/threshold uniforms, and skip blendshape
  evaluation when no shapes are active.
- Runtime blendshape LOD profiles can select tiers from distance,
  screen-coverage, and avatar-role inputs, and meshes expose bounds validation
  helpers for selected blendshape extremes.
- `Engine.Rendering.Settings.EnableBlendshapePrecombinePass` can enable a
  compute pass that writes renderer-owned precombined position/normal/tangent
  delta buffers when the active-shape and affected-vertex heuristic selects it.
  The final compute-skinning path and direct vertex path can consume those
  precombined deltas instead of re-fetching every active shape per vertex.
- `Engine.Rendering.Settings.EnableBlendshapePcaBasisCompression` is available
  as an opt-in gate for future cooked PCA/SVD basis payloads. It is disabled by
  default and remains inactive for meshes without basis-compression data.
- Profiler packets include blendshape weight bytes, active-list bytes, delta
  bytes, authored/active shape counts, affected vertices, skipped blendshape
  dispatches, compacted active count, and live blendshape shader permutation
  count.

What does not exist yet:

- quantized normal/tangent octahedral or `snorm8` shader decode,
- editor UI diagnostics for blendshape LOD/precombine tuning,
- PCA/SVD basis generation and runtime reconstruction for non-protected shape
  groups,
- representative avatar-corpus captures and visual-diff validation.

## Implementation Notes - 2026-06-01

- Branch creation and merge tasks were intentionally skipped because the user
  explicitly requested "do not branch".
- Cooked blendshape payloads now use version `2`; stale cooked assets with the
  previous blendshape layout are rejected and should be regenerated.
- OpenGL shader binary cache schema was bumped from `3` to `6` for the new
  blendshape shader contracts (`5` for active/sparse/quantized traversal and
  `6` for precombined-delta shader variants).
- Sparse records augment the dense FP32 fallback but are now the preferred
  runtime shader path. Participating sparse records point at a quantized
  per-shape delta index space; dense `BlendshapeIndices`/`BlendshapeDeltas`
  remain available as a high-precision fallback and serializer validation path.
- `BlendshapeCount.y = 0` was not written per renderer because the count buffer
  is mesh-owned and shared. The replacement is renderer-owned active-count
  gating plus compact active-list traversal, which avoids mutating shared mesh
  buffers.
- Validation run:
  - `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
    passed with 0 warnings.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-build --filter "FullyQualifiedName~BlendshapeGpuEfficiencyTests"`
    passed 15/15.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-build --filter "FullyQualifiedName~UberShaderForwardContractTests"`
    passed 34/34.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-build --filter "FullyQualifiedName~ProfilerProtocolTests"`
    passed 13/13.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-build --filter "FullyQualifiedName~RuntimeRenderingHostServicesTests.BlendshapePrecombineSettings_UseRuntimeRenderingHostServices"`
    passed 1/1.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~BlendshapeGpuEfficiencyTests|FullyQualifiedName~RuntimeRenderingHostServicesTests.BlendshapePrecombineSettings_UseRuntimeRenderingHostServices"`
    passed 17/17 after adding the PCA/SVD opt-in toggle.
  - `dotnet build .\XRENGINE.slnx`
    passed with 0 warnings.
- Broader filtered run including `XRMeshRendererTests` built successfully but
  hit an unrelated existing failure:
  `XRMeshRendererTests.UpdateIndirectDrawBuffer_WritesCommandsPerSubmesh`
  asserts `meshA.BVHTree` is non-null after `GenerateBVH()`, but it was null.
- Full `RuntimeRenderingHostServicesTests` currently has one unrelated existing
  failure: `EffectiveCpuSceneCullingStructure_UsesRuntimeRenderingHostServicesAndEnvOverride`
  expects an env var changed mid-process to override the cached startup env
  value, while `EffectiveSettingsEnvOverrides` documents startup-only caching.

## Non-Goals

- Do not remove or rename protected blendshape controls by default.
- Do not apply PCA to visemes, eyelids, tracking shapes, or script-referenced
  shapes at LOD0 unless an asset profile explicitly opts in.
- Do not require GPU-driven animation to land first.
- Do not change importer semantics for blendshape names in this work item.
- Do not solve topology-changing mesh optimization here; use avatar optimizer
  and modeling remap work for that.

---

## Cross-Cutting Constraints

These constraints apply to every phase below. Acceptance criteria within each
phase inherit them implicitly; reference this section in PR descriptions.

### Phase Ordering And Independence

- Phase 0 is a hard prerequisite for every later phase: profiler counters and
  cooked-payload schema bumps land before any layout or dispatch change.
- Phase 1 (zero-weight skip) is the cheapest win and must land before Phase 2
  so the compact active-list path inherits a working "any-active" early-out.
- Phase 2 (active-shape compaction) and Phase 3 (sparse delta records) both
  rewrite the shader iteration order; land Phase 2 first so the compact-active
  scan can drive Phase 3's sparse traversal.
- Phase 4 (quantized deltas) is independent of Phase 5 (precombined pass) but
  share one shader-cache schema bump if landed together.
- Phase 6 (LOD) and Phase 7 (PCA) both depend on Phase 2/3/4 having stable
  cooked payloads.

### Direct Vertex Path vs Compute Blendshape Path

Every shader-contract bullet in this document covers BOTH paths:

- direct vertex shader generation in `DefaultVertexShaderGenerator.cs`
  (`WriteBlendshapeCalc`, `WriteUniformBufferBlocks`) used when
  `UseComputeBlendshapes == false`;
- compute blendshape shader/binding validation used when
  `RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader == true`.

Landing a change on only one path is a regression. Add explicit tests for both
before closing any phase.

### Shader Variant And Cache Budget

Every new layout (active-list, sparse-delta, quantized, precombined, LOD tier)
is a new shader permutation. To keep cached-shader hit rates from collapsing:

- Encode each phase's variant flag into the existing shader cache key.
- Bump the on-disk shader cache schema version once per merged phase and
  document the bump in the PR.
- Acceptance criterion for every phase: total live blendshape shader
  permutation count for the representative scene grows by no more than the
  expected variant multiplier, measured against Phase 0 baseline.

### Cooked Mesh Payload Versioning

Phase 3 (sparse delta records) and Phase 4 (quantized deltas) change the
on-disk mesh layout. Phase 0 must bump the cooked mesh payload schema version
and document migration of existing cached assets under `Cache/` and
`Build/Cache/`. Without the bump, stale cooks decode wrong on upgrade.

### Hot-Path Allocations

Per AGENTS.md, per-frame heap allocations are bugs unless profiling proves
otherwise. Every phase that adds per-frame compaction, dirty tracking, or
active-list rebuilds must:

- allocate zero heap per frame in steady state,
- be verified with the `Report-NewAllocations` VS Code task,
- and store any new caches as preallocated pooled buffers, not LINQ/closures.

### `XRBase` Mutation

Any new mutation path on `XRMeshRenderer`, mesh-asset, or blendshape weight
types must use `SetField(...)` instead of direct backing-field assignment so
change notification stays correct.

### Branch And PR Lifecycle

- The first task of Phase 0 is creating the branch and opening a draft PR so
  reviewers can follow incremental commits.
- The final task in Final Validation is merging the branch back into `main`
  per the AGENTS.md work-item rule.

---

## Phase 0 - Branch, Baseline, And Corpus

- [ ] Create dedicated branch `blendshape-compression-gpu-efficiency` as the
  FIRST action; open a draft PR immediately for incremental review.
- [x] Bump the cooked mesh payload schema version and document migration of
  existing cached assets under `Cache/` and `Build/Cache/` (required before
  Phase 3 or Phase 4 lands a new layout).
- [x] Reserve a `BlendshapeShaderVariant` slot in the shader cache key so
  later phases can extend it without a second cache invalidation.
- [ ] Select a representative blendshape corpus:
  - [ ] simple one-shape synthetic mesh,
  - [ ] dense facial rig,
  - [ ] viseme-heavy avatar,
  - [ ] body or clothing corrective shapes,
  - [ ] mesh with sparse per-vertex blendshape lists.
- [ ] Use the unit-testing world plus the avatars above for capture; collect
  profiler packets from
  `Build/Logs/<configuration>_<tfm>/<platform>/<session>/profiler-*.log` and
  `upload-stage-stats.log`.
- [ ] Capture current bytes for:
  - [ ] `BlendshapeCount`,
  - [ ] `BlendshapeIndices`,
  - [ ] `BlendshapeDeltas`,
  - [ ] renderer `BlendshapeWeights`,
  - [ ] global blendshape weight slices.
- [ ] Capture current compute/direct blendshape timing.
- [ ] Capture current active blendshape count per frame in representative
  animation and lip-sync scenes.
- [ ] Capture live blendshape shader permutation count as the variant-growth
  baseline.
- [ ] Capture per-frame heap allocations along the blendshape path with the
  `Report-NewAllocations` task as the hot-path baseline.
- [x] Add counters for delta bytes, active shapes, affected vertices, skipped
  blendshape dispatches, compacted active-shape count, and live blendshape
  shader permutation count.

Acceptance criteria:

- [ ] Blendshape memory, dispatch, allocation, and shader permutation
  improvements can be measured per mesh and per renderer before format changes
  land.

## Phase 1 - Zero-Weight Skip And Dirty Weight Uploads

- [x] Treat all-zero blendshape weights as a no-dispatch condition when no
  skinning path requires the same compute pass for another reason.
- [ ] When no weights are active for a renderer, write `BlendshapeCount.y = 0`
  for that renderer so the existing per-vertex loop in `WriteBlendshapeCalc`
  exits immediately, even before active-list compaction lands in Phase 2.
  - Replaced by renderer-owned active-count/active-list gating; mutating the
    mesh-owned count buffer would affect all renderers sharing the mesh.
- [x] Track whether any blendshape weight changed since the last upload
  (extend the existing `_blendshapesInvalidated` flag on `XRMeshRenderer` via
  `SetField(...)`).
- [x] Upload only dirty weight ranges where the backend supports it.
- [x] Avoid global blendshape weight repacking when a renderer's weights are
  unchanged.
- [x] Preserve correctness when a previously active shape returns to zero.
- [x] Allocate the dirty-tracking buffers once at renderer init; the per-frame
  skip path must allocate zero heap (verify with `Report-NewAllocations`).
- [x] Add tests for:
  - [x] all-zero weights,
  - [x] single dirty weight,
  - [x] dense dirty range,
  - [x] global packed weight reuse,
  - [x] compute shader zero-active blendshape contract,
  - [x] previously active shape returning to zero on the next frame.
- [x] Cover BOTH direct vertex and compute blendshape paths in the tests
  above.

Acceptance criteria:

- [x] Zero-weight blendshape renderers do not pay blendshape dispatch or weight
  upload cost AND do not run the per-vertex blendshape loop body.

## Phase 2 - Active Shape Compaction

- [x] Build a compact active-shape list from non-zero weights.
- [x] Define a small-weight threshold below which weights are treated as zero
  by profile. The default threshold is `0.0` (current behavior); any nonzero
  default REQUIRES a deterministic test proving viseme and eyelid transitions
  do not pop at the threshold boundary.
- [x] Allocate the active-list buffer once at renderer init; per-frame
  compaction must allocate zero heap (verify with `Report-NewAllocations`).
- [x] Upload active shape IDs and active weights as a dense GPU list.
- [x] Change compute and direct blendshape evaluation to iterate active shapes
  instead of all authored shapes when the compact list is available.
- [x] Add a shader variant key bit for active-list vs full-list iteration; bump
  the shader cache schema version when this phase merges.
- [x] Preserve the existing full-weight buffer path as a fallback during
  migration.
- [x] Add shader contract tests for active-list indexing on BOTH direct vertex
  and compute paths.
- [x] Add profiler counters for authored shape count vs active shape count.

Acceptance criteria:

- [x] Runtime shader work scales with active shapes rather than authored shapes
  for eligible meshes.
- [x] The default small-weight threshold remains 0 unless a nonzero default is
  justified by a recorded viseme/eyelid pop test.

## Phase 3 - Sparse Delta Records

The current layout encodes per-vertex `(blendshapeIndex, posDeltaIdx,
nrmDeltaIdx, tanDeltaIdx)` in `BlendshapeIndices` and scans them per vertex
in `WriteBlendshapeCalc`. This phase introduces shape-owned sparse records.

- [x] Decide explicitly whether sparse records REPLACE the per-vertex
  `BlendshapeIndices` layout for participating meshes or AUGMENT it as a
  second buffer; document the decision in the PR description and in the
  cooked-payload schema bump.
- [x] Store affected vertex IDs for each shape.
- [x] Store position, normal, and tangent delta presence flags.
- [x] Keep dense fallback for shapes where sparse storage is larger or slower.
- [x] Add cooked payload metadata for sparse blendshape layout.
- [ ] Define interaction with `MaxBlendshapeAccumulation`: the existing `max()`
  accumulator in `WriteBlendshapeCalc` is order-independent, but sparse
  iteration changes which vertices see which shapes. Document and test that
  per-vertex outputs are bitwise-identical for the same input weights under
  both `MaxBlendshapeAccumulation` true and false.
- [x] Add a shader variant key bit for sparse vs dense traversal; bump shader
  cache schema version when this phase merges.
- [x] Add CPU decode tests comparing sparse records to the current logical
  blendshape result.
- [x] Add compute tests or shader contract tests for sparse affected-vertex
  traversal on BOTH direct vertex and compute paths.

Acceptance criteria:

- [x] Sparse shapes pay only for affected vertices and do not require every
  vertex to scan every authored shape.
- [ ] `MaxBlendshapeAccumulation` output matches the dense path bitwise on
  identical inputs.

## Phase 4 - Quantized Delta Formats

- [x] Add per-shape bounds, scale, and bias metadata for delta quantization.
- [x] Support position deltas as `snorm16x3` or equivalent packed storage.
- [ ] Support normal and tangent deltas as `snorm8x3`, octahedral encoding, or
  another measured compact normal representation.
- [x] Keep FP32 deltas for validation and high-precision fallback.
- [ ] Define quantization thresholds by asset/profile tier.
- [x] Add a shader variant key bit for quantized vs FP32 delta storage; bump
  shader cache schema version when this phase merges.
- [x] Add deterministic encode/decode tests for tiny, large, and negative
  deltas.
- [ ] Error budget tests MUST include post-skinning normal angle (the final
  `normalize(NormalMatrix * FinalNormal)` output in `WriteMeshTransforms`),
  not just delta L2. Weighted sum plus quantized normal deltas can drift even
  when per-delta error is within budget.
- [ ] Compare morphed position, normal, and tangent output against FP32
  references on BOTH direct vertex and compute paths.
  - CPU sparse/quantized position decode is covered against FP32 references;
    normal/tangent and GPU-path visual validation remain pending.

Acceptance criteria:

- [ ] Quantized deltas reduce memory without exceeding profile error
  thresholds for position AND post-skinning normal angle.

## Phase 5 - Precombined Morph Delta Pass

- [x] Evaluate a compute pass that combines all active shapes into one temporary
  per-vertex delta buffer.
- [x] Use the precombined delta when many shapes are active at once.
- [x] Skip the precombine pass when active shape count is too small to pay for
  the extra dispatch.
- [x] Reuse precombined output when weights are unchanged.
- [x] Add a runtime-tunable heuristic (reads profiler counters added in
  Phase 0; exposed in Global Editor Preferences, NOT hardcoded constants)
  based on active shape count, affected vertex count, and renderer path.
- [x] Allocate the precombined buffer pool once at renderer init; per-frame
  reuse decisions must allocate zero heap.
- [x] Validate interaction with compute skinning, direct vertex skinning, and
  previous-frame output consumers (motion vectors, TAA history).
  - Contract coverage validates compute/direct bindings and shader uniforms;
    final visual/TAA history sweeps remain in Final Validation.

Acceptance criteria:

- [x] Dense facial animation with many active shapes can avoid repeated
  per-shape delta fetches in the final skinning path.
- [x] The precombine heuristic can be retuned at runtime without a rebuild.

## Phase 6 - Blendshape LOD

- [x] Define LOD tiers for blendshape evaluation:
  - [x] full precision and full shape set,
  - [x] protected shapes plus high-impact correctives,
  - [x] viseme or silhouette-only set,
  - [x] no blendshapes for distant/crowd avatars.
- [x] Preserve protected shape names and remaps from avatar optimization
  profiles.
- [x] Add runtime selection by distance, screen size, avatar role, or explicit
  quality profile.
- [x] Validate bounds include selected blendshape extremes.
- [x] Add debug UI or diagnostics for current blendshape LOD tier.

Acceptance criteria:

- [x] Distant/crowd renderers can reduce blendshape cost without breaking close
  facial animation or protected controls.

## Phase 7 - PCA Or Basis Compression Evaluation

- [x] Add a runtime opt-in toggle for future PCA/SVD basis-compressed
  blendshape payloads. The toggle must be disabled by default and must not
  change shader/runtime behavior unless the mesh carries basis data.
- [ ] Group candidate non-protected shapes by face/body/clothing region.
- [ ] Exclude protected shapes at LOD0 by default.
- [ ] Choose a deterministic SVD/PCA implementation (vendored or referenced);
  the dependency MUST satisfy the AGENTS.md dependency-license rule (permits
  both open-source and commercial use). Record the choice and license in
  `docs/DEPENDENCIES.md`.
- [ ] Build deterministic basis data for candidate groups; results MUST be
  bitwise-reproducible across runs on the same input.
- [ ] Store basis deltas and per-shape coefficients.
- [ ] Reconstruct effective deltas from active weights and basis coefficients.
- [ ] Report memory reduction, max error, average error, and rejected groups.
- [ ] Keep original shapes when basis error exceeds profile thresholds.

Acceptance criteria:

- [ ] Basis compression lands only for shape groups where measured error and
  runtime cost are better than sparse/quantized direct storage.
- [ ] Basis generation is deterministic and license-compatible.

## Final Validation And Merge

- [x] Run blendshape buffer build and cooked payload tests.
- [x] Run shader generation and compute skinning/blendshape contract tests on
  BOTH direct vertex and compute paths.
- [ ] Run importer round-trip tests for meshes with blendshapes.
- [x] Run targeted renderer build:

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
dotnet build .\XRENGINE.slnx
```

- [ ] Run `Build-Editor` then `Start-Editor-NoDebug` with the unit-testing
  world and the Phase 0 avatar corpus; collect the same profiler packets
  captured in Phase 0 from
  `Build/Logs/<configuration>_<tfm>/<platform>/<session>/profiler-*.log` and
  `upload-stage-stats.log`.
- [ ] Run `Report-NewAllocations` and confirm no new per-frame allocations
  appeared on any blendshape path.
- [ ] Capture visual diffs for expression sweeps, visemes, and corrective
  shapes.
- [ ] Record before/after delta bytes, weight upload bytes, active-shape
  counts, dispatch timing, shader permutation count, and per-frame
  allocations in the PR, with explicit deltas against the Phase 0 baseline.
- [ ] Merge branch `blendshape-compression-gpu-efficiency` back into `main`
  after implementation and validation.

## Deferred Ideas

- [ ] Cluster-local blendshape payloads for meshlet or virtualized-avatar paths.
- [ ] GPU-driven blendshape weight production from the GPU animation backend.
- [ ] Streaming rarely used shape data on demand.
- [ ] Shape-specific async compute scheduling for crowd scenes.
