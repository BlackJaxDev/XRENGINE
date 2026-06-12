# Blendshape Compression And GPU Efficiency TODO

Last Updated: 2026-06-12
Status: Remaining validation, normal/tangent compression, `MaxBlendshapeAccumulation`
parity, and PCA/SVD basis compression.
Target Branch: none - user explicitly requested no branch for this pass
Scope: unfinished runtime mesh blendshape storage, upload, dispatch, shader
evaluation, and validation work.

Implemented blendshaping behavior has moved to
[Blendshaping](../../../../developer-guides/rendering/blendshaping.md).
Longer-horizon ideas have moved to
[Blendshape Deferred GPU Efficiency Design](../../../design/rendering/gpu/blendshape-deferred-gpu-efficiency-design.md).

Related docs:

- [Blendshaping](../../../../developer-guides/rendering/blendshaping.md)
- [Blendshape Deferred GPU Efficiency Design](../../../design/rendering/gpu/blendshape-deferred-gpu-efficiency-design.md)
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

Close the remaining correctness and validation gaps for the current blendshape
runtime, then evaluate optional PCA/SVD basis compression.

## Non-Goals

- Do not remove or rename protected blendshape controls by default.
- Do not apply PCA to visemes, eyelids, tracking shapes, or script-referenced
  shapes at LOD0 unless an asset profile explicitly opts in.
- Do not require GPU-driven animation to land first.
- Do not change importer semantics for blendshape names in this work item.
- Do not solve topology-changing mesh optimization here; use avatar optimizer
  and modeling remap work for that.

## Cross-Cutting Constraints

### Direct Vertex Path vs Compute Blendshape Path

Remaining shader-contract work must cover BOTH paths:

- direct vertex shader generation in `DefaultVertexShaderGenerator.cs`
  (`WriteBlendshapeCalc`, `WriteUniformBufferBlocks`) used when
  `UseComputeBlendshapes == false`;
- compute blendshape shader/binding validation used when
  `RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader == true`.

Landing a change on only one path is a regression. Add explicit tests for both
before closing relevant tasks.

### Shader Variant And Cache Budget

Every new layout or runtime branch is a shader permutation. To keep cached
shader hit rates from collapsing:

- Encode any new variant flag into the existing shader cache key.
- Bump the on-disk shader cache schema version when a new shader contract lands.
- Measure live blendshape shader permutation count for representative scenes.

### Cooked Mesh Payload Versioning

Any future basis-compressed, normal/tangent-compressed, or otherwise incompatible
cooked payload layout must bump the cooked mesh payload schema and document
migration of existing cached assets under `Cache/` and `Build/Cache/`.

### Hot-Path Allocations

Per AGENTS.md, per-frame heap allocations are bugs unless profiling proves
otherwise. Remaining per-frame blendshape work must:

- allocate zero heap per frame in steady state,
- be verified with the `Report-NewAllocations` VS Code task,
- and store any new caches as preallocated pooled buffers, not LINQ/closures.

### `XRBase` Mutation

Any new mutation path on `XRMeshRenderer`, mesh-asset, or blendshape weight
types must use `SetField(...)` instead of direct backing-field assignment so
change notification stays correct.

## Phase 0 - Corpus And Baseline Capture

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

Acceptance criteria:

- [ ] Blendshape memory, dispatch, allocation, and shader permutation
  improvements can be measured per mesh and per renderer before additional
  format changes land.

## Phase 3 - Sparse Delta Parity

- [ ] Define interaction with `MaxBlendshapeAccumulation`: the existing `max()`
  accumulator in `WriteBlendshapeCalc` is order-independent, but sparse
  iteration changes which vertices see which shapes. Document and test that
  per-vertex outputs are bitwise-identical for the same input weights under
  both `MaxBlendshapeAccumulation` true and false.

Acceptance criteria:

- [ ] `MaxBlendshapeAccumulation` output matches the dense path bitwise on
  identical inputs.

## Phase 4 - Normal/Tangent Quantization And Error Budgets

- [ ] Support normal and tangent deltas as `snorm8x3`, octahedral encoding, or
  another measured compact normal representation.
- [ ] Define quantization thresholds by asset/profile tier.
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

## Phase 7 - PCA Or Basis Compression Evaluation

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

## Final Validation

- [ ] Run importer round-trip tests for meshes with blendshapes.
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
  counts, dispatch timing, shader permutation count, and per-frame allocations,
  with explicit deltas against the Phase 0 baseline.
