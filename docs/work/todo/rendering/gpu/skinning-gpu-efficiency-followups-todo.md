# Skinning GPU Efficiency Follow-Ups TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Skinning guide](../../../../developer-guides/rendering/skinning.md)  Design: [Skinning Deferred GPU Efficiency Design](../../../design/rendering/gpu/skinning-deferred-gpu-efficiency-design.md), [GPU Skinning Buffer Compression Plan](../../../design/rendering/gpu/gpu-skinning-buffer-compression-plan.md)
Validation: [GPU Deformation Validation](../../../testing/rendering/gpu-deformation-validation.md#skinning)

## Current State

Compact `Core4 + Spill` skinning runs on the direct vertex path (`DefaultVertexShaderGenerator`) and the compute path. `SkinPaletteMatrix` stores FP32 affine rows (48 bytes per bone). `XRMeshRenderer` reuses compute skinning output when inputs are clean. `GlobalSkinPaletteBuffers` dedupes identical same-mesh palette slices for upload, but renderers with the same palette still dispatch separately. `SkinningLodProfile` and `SkinningLodTier` (with `AllowRigidFallback`) exist, and `XRMeshRenderer.ActiveSkinningLodTier` selects a tier; full and reduced-influence tiers are implemented. Change rules are in the [Skinning guide](../../../../developer-guides/rendering/skinning.md#change-rules).

## Open Code Items

### Mixed-Precision Palettes

Blocked by: the GPU physics-chain palette writer must support the chosen FP16 row format ([GPU Physics Chain Zero-Readback Skinned Mesh Plan](../../../design/transforms/gpu-physics-chain-zero-readback-skinned-mesh-plan.md)).

- [ ] Define the packed FP16 affine row format (24 bytes per bone) and its OpenGL and Vulkan alignment. Done when: one shared layout constant set exists for both backends.
- [ ] Add opt-in FP16 palette storage selected by a renderer setting or avatar profile flag. Keep FP32 for large-world, high-precision, and validation modes. Add a shader variant key bit and bump the shader cache schema version. Done when: both precisions build on the direct vertex and compute paths.
- [ ] Make the GPU physics-chain palette writer emit the FP16 format, or disable FP16 when chain-driven palettes are present. Done when: chain-driven content never reads a format it did not write.

### Skinning LOD

- [ ] Complete reduced bone-palette LOD tiers through `BoneRemap` consumption. `SkinningLodProfile`, `XRMeshRenderer`. Done when: a tier with a smaller palette skins correctly on both paths.
- [ ] Implement the rigid or near-rigid fallback for `SkinningLodTier.AllowRigidFallback`. Done when: a tier with the flag skips per-vertex skinning for distant or crowd meshes.
- [ ] Add deterministic error metrics for reduced palettes and influence caps. Done when: the metric is computed per tier and exposed to the avatar optimizer.

### Palette Dedupe Dispatch Reuse

- [ ] Share one compute skinning dispatch result among renderers with the same mesh and palette hash. Fall back to per-renderer dispatch on hash collision or pose divergence. Skip the dedupe path below a measured renderer-count threshold. `GlobalSkinPaletteBuffers`, `XRMeshRenderer`. Done when: N identical-pose renderers record one dispatch.

### Tests (Owner Clearance Required)

- [ ] Add FP32 versus FP16 palette tests (CPU and shader) for identity, long chains, non-uniform scale, large translations, tiny bones, inverted and mirrored scale, and chain-driven palettes, on both skinning paths. Done when: each case has a deterministic test.
- [ ] Add dispatch-reuse invalidation tests for animator pose, IK, GPU physics chain, blendshape weight, mesh rebuild, LOD swap, world matrix change on non-pretransformed paths, palette precision or layout switch, shader variant change, and static pose reuse. Done when: each source has a test.
- [ ] Add dedupe dispatch tests for hash collision and pose divergence after a matched frame. Done when: both fall back to per-renderer dispatch in tests.

## Decisions Needed

- [ ] Decide whether FP16 palettes stay opt-in or become profile-selected after the error checks. Owner: rendering lead.

## Out Of Scope

- The old mesh-wide fixed-4 versus variable skinning path.
- Full GPU animation evaluation ([GPU-Driven Animation TODO](gpu-driven-animation-todo.md)).
- Blendshape storage ([Blendshape Compression And GPU Efficiency TODO](blendshape-compression-and-gpu-efficiency-todo.md)).
- An editor UI for bone selection (avatar optimizer work).
