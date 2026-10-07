# Blendshape Compression And GPU Efficiency TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Blendshaping guide](../../../../developer-guides/rendering/blendshaping.md)  Design: [Blendshape Deferred GPU Efficiency Design](../../../design/rendering/gpu/blendshape-deferred-gpu-efficiency-design.md)
Validation: [GPU Deformation Validation](../../../testing/rendering/gpu-deformation-validation.md#blendshapes)

## Current State

Blendshapes evaluate on the direct vertex path (`DefaultVertexShaderGenerator.WriteBlendshapeCalc`) or the compute path (`SkinningPrepassDispatcher`) when `CalculateBlendshapesInComputeShader` is on. `XRMesh` builds sparse per-shape records and quantized delta buffers (`PopulateQuantizedBlendshapeBuffers`, `BlendshapeQuantizationMetadata`). Quantization uses one per-shape scale and bias for position, normal, and tangent deltas; there is no compact normal encoding. `XRMesh.MaxBlendshapeAccumulation` selects a `max()` accumulator on both paths. `BlendshapeShaderVariant.BasisCompression`, `XRMesh.HasBlendshapeBasisCompressionPayload`, and the `EnableBlendshapePcaBasisCompression` setting (default off) exist, but no code generates a basis payload. No per-shape update class exists. Change rules (both paths, shader cache key, cooked payload version, zero steady-state allocation, `SetField`) are in the [Blendshaping guide](../../../../developer-guides/rendering/blendshaping.md).

## Open Code Items

### Sparse Delta Parity

- [ ] Make sparse iteration give bitwise-identical per-vertex output to the dense path for the same weights, with `MaxBlendshapeAccumulation` on and off, on both paths. `DefaultVertexShaderGenerator.WriteBlendshapeCalc`, compute blendshape shader. Done when: the parity test in "Tests" passes.

### Normal And Tangent Quantization

- [ ] Add a compact normal and tangent delta encoding (`snorm8x3`, octahedral, or another measured format) with a shader variant bit and a cooked mesh payload schema bump. `XRMesh.Blendshapes.cs`, `DefaultVertexShaderGenerator`, compute blendshape shader. Done when: both paths decode the new format.
- [ ] Define quantization thresholds per asset or profile tier for position error and post-skinning normal angle (the `normalize(NormalMatrix * FinalNormal)` output in `WriteMeshTransforms`). Done when: the bake rejects a shape that exceeds its tier threshold and keeps the uncompressed data.

### PCA Or Basis Compression

- [ ] Group non-protected shapes by face, body, and clothing region. Exclude protected shapes (visemes, eyelids, tracking, script-referenced) at LOD0 unless a profile opts in. Done when: the grouping is deterministic for one input.
- [ ] Choose a deterministic SVD or PCA implementation that meets the dependency license rule. Ask before you add a dependency. Done when: the choice and license are in `docs/DEPENDENCIES.md`.
- [ ] Generate basis deltas and per-shape coefficients into the cooked payload and set `BlendshapeShaderVariant.BasisCompression`. Keep the original shapes for a group whose error exceeds the profile threshold. Report memory reduction, maximum and average error, and rejected groups. Done when: `HasBlendshapeBasisCompressionPayload` is true for an accepted mesh and output is bitwise-reproducible across runs.
- [ ] Reconstruct effective deltas from active weights and basis coefficients on both paths. Done when: a basis-compressed mesh deforms with `EnableBlendshapePcaBasisCompression` on.

### Update Classes

- [ ] Add `BlendshapeUpdateClass` (`Streamed`, `Dynamic`, `Static`) and store one class per shape in `XRMesh` imported and cooked metadata. Default imported shapes to `Dynamic`. Done when: an imported asset reports a class per shape and runtime behavior is unchanged.
- [ ] Add protected-name and profile rules that force `Streamed` or `Dynamic` and block `Static` for script-controlled, tracking, viseme, eyelid, and protected shapes. Done when: a protected shape refuses `Static`.
- [ ] Show each shape's update class in the editor mesh diagnostics. `ModelComponentEditor`. Done when: the inspector lists the class.

### Static Shape Baking

- [ ] Bake static shape weights into base position, normal, and tangent data. Rebase the remaining deltas against the baked rest mesh. Done when: dynamic and streamed shapes do not double-apply static offsets.
- [ ] Generate a deterministic cooked variant key from source mesh identity, static shape indices, static weights, and bake settings. Regenerate the variant when an editor changes a static weight. Done when: equal inputs give equal keys.
- [ ] Remove baked static shapes from active lists, sparse records, quantized payloads, precombine eligibility, and shader permutation counts. Done when: a fully static mesh has no runtime blendshape buffers.

### Streamed And Dynamic Upload Paths

- [ ] Keep streamed shape indices in a contiguous renderer-owned list and upload streamed weights as a compact per-frame slice. Keep dynamic dirty-range uploads separate. `XRMeshRenderer`. Done when: a streamed-weight change does not upload the full authored range.
- [ ] Do not rebuild the dynamic active list when only streamed weights change. Add optional per-shape dirty events for dynamic toggles. Done when: a streamed-only frame records no dynamic active-list rebuild.
- [ ] Bias precombine heuristics toward streamed shapes only when the active streamed count and affected vertex count justify the extra dispatch. Done when: the heuristic reads both counts.
- [ ] Add profiler counters for streamed and dynamic upload bytes, active counts, active-list rebuilds, and precombine dispatches. Done when: the counters appear in the profiler packet.

### Tests (Owner Clearance Required)

- [ ] Add a dense versus sparse bitwise parity test with `MaxBlendshapeAccumulation` on and off. Done when: the test covers both paths.
- [ ] Add FP32 reference tests for quantized position, normal, and tangent output on both paths, including post-skinning normal angle. CPU sparse and quantized position decode already has coverage. Done when: normal and tangent cases exist.
- [ ] Add static bake tests: position only, normal and tangent, rebased dynamic deltas, protected shapes that refuse baking, and deterministic variant keys. Done when: each case has a test.
- [ ] Add importer round-trip and cooked payload compatibility tests for update classes and the new payload layouts. Done when: both tests exist.

## Decisions Needed

- [ ] Choose the compact normal encoding after the error measurements. Owner: rendering lead.

## Out Of Scope

- Removing or renaming protected blendshape controls.
- Changes to importer blendshape names or external animation bindings.
- Topology-changing mesh optimization (avatar optimizer and modeling work).
- A dependency on GPU-driven animation ([GPU-Driven Animation TODO](gpu-driven-animation-todo.md)).
- Avatar-level blendshape policy ([Avatar Optimization Roadmap](../../avatar/avatar-optimization-roadmap.md#skin-skeleton-and-blendshapes)).

## Recovered Items To Triage

The 2026-10-06 todo cleanup removed these items, and no match was found in other docs. Classify each item as code, check, decision, or done. Then move it to the correct doc or delete it.

### From `todo/rendering/gpu/blendshape-compression-and-gpu-efficiency-todo.md`

- [ ] body or clothing corrective shapes,
- [ ] Capture visual diffs for expression sweeps, visemes, and corrective
  shapes.
