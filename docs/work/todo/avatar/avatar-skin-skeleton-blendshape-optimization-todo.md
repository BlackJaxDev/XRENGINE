# Avatar Skin, Skeleton, And Blendshape Optimization TODO

Last Updated: 2026-05-29
Owner: Animation / Assets / Rendering
Status: Active
Target Branch: `avatar-skin-skeleton-blendshape-optimization`

Design source:

- [Avatar Optimization And Virtualized Avatar Rendering Design](../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [Avatar Optimization Roadmap](avatar-optimization-roadmap.md)
- [GPU Skinning Buffer Compression](../../design/rendering/gpu/gpu-skinning-buffer-compression-plan.md)
- [GPU-Driven Animation](../../design/rendering/gpu/gpu-driven-animation.md)

## Goal

Reduce skinning and blendshape memory, upload, and compute cost while
preserving animation, runtime references, facial identity, visemes, and
deformation quality.

## Scope

- Skin influence pruning and quantization.
- Bone palette optimization.
- Skeleton pruning with runtime-reference discovery.
- Bone and blendshape remap tables.
- Sparse and quantized blendshape deltas.
- PCA basis compression for non-protected facial/body shapes.
- Animation-sample validation.

## Non-Goals

- Do not prune bones by weight/animation channels alone.
- Do not alter protected visemes, eyelid shapes, or runtime-controlled
  expressions by default.
- Do not assume bind-pose validation is enough.
- Do not change runtime bone or blendshape names without remap fallback.

## Phase 0 - Branch, Baseline, And Sample Set

- [ ] Create dedicated branch
  `avatar-skin-skeleton-blendshape-optimization`.
- [ ] Define the required animation sample set:
  - [ ] bind pose
  - [ ] T-pose
  - [ ] A-pose
  - [ ] walk cycle
  - [ ] run cycle
  - [ ] jump apex
  - [ ] arms overhead
  - [ ] arms behind back
  - [ ] extreme crouch or sit
  - [ ] facial range-of-motion sweep
- [ ] Version the sample set inside the optimization profile/report.
- [ ] Capture baseline skinning error and blendshape error for corpus avatars.
- [ ] Inventory runtime systems that reference bones or blendshapes.

Acceptance criteria:

- [ ] Validation cannot pass by checking bind pose only.

## Phase 1 - Skin Weight Pruning And Quantization

- [ ] Remove weights below profile threshold.
- [ ] Keep top N influences per vertex by profile.
- [ ] Renormalize weights after pruning.
- [ ] Support 16-bit unorm weights for high-quality LOD0.
- [ ] Support three 10-bit weights plus implicit fourth where layout uses it.
- [ ] Support 8-bit unorm weights with vertex-shader renormalization.
- [ ] Support 1-2 influence crowd/mobile profiles.
- [ ] Pack bone indices to the smallest safe format.
- [ ] Compare skinned positions before/after pruning across the sample set.
- [ ] Locally relax pruning near joints where error exceeds threshold.

Acceptance criteria:

- [ ] Weight pruning reduces upload/compute cost without visible deformation
  error beyond profile limits.

## Phase 2 - Bone Palette Optimization

- [ ] Build compact per-mesh or per-section bone palettes.
- [ ] Remap bone indices through palette tables.
- [ ] Split sections only if palette limits require it.
- [ ] Prefer preserving merged sections when splitting would increase draw calls
  more than palette compression helps.
- [ ] Add palette size, remap, and split diagnostics to reports.
- [ ] Add tests for palette remap correctness and invalid bone index rejection.

Acceptance criteria:

- [ ] Bone palettes reduce data size while preserving renderer and animation
  correctness.

## Phase 3 - Protected Bone Discovery

- [ ] Preserve bones referenced by vertex weights.
- [ ] Preserve bones referenced by animation channels.
- [ ] Preserve bones referenced by `PhysicsChainComponent` or equivalent spring
  chain components.
- [ ] Preserve bones referenced by VRM spring-bone definitions.
- [ ] Preserve bones referenced by IK, look-at, or aim constraints.
- [ ] Preserve bones referenced by sockets and attachments.
- [ ] Preserve bones referenced by gameplay scripts via string name lookup.
- [ ] Preserve twist and roll bones even when they have zero direct bind
  weights.
- [ ] Preserve humanoid mapping bones.
- [ ] Preserve bones tagged by first-person/third-person mesh flags.
- [ ] Publish original bone path to optimized bone path remap.

Acceptance criteria:

- [ ] Skeleton pruning cannot silently break runtime references or helper bones.

## Phase 4 - Skeleton Pruning

- [ ] Remove bones with no weights, no animation channels, no socket role, no
  runtime reference, and no child dependency.
- [ ] Collapse helper bones only with explicit profile permission.
- [ ] Keep gameplay, attachment, IK, facial, humanoid, and script-visible names
  addressable through remap tables.
- [ ] Validate animation clips after pruning.
- [ ] Validate attachments and sockets after pruning.
- [ ] Add report fields for pruned bones and rejected prune candidates.

Acceptance criteria:

- [ ] Pruned skeletons animate correctly and preserve runtime lookup behavior.

## Phase 5 - Blendshape Analysis And Sparse Storage

- [ ] Identify blendshapes referenced by animations, visemes, expressions, and
  runtime controls.
- [ ] Identify blendshapes referenced by scripts via name.
- [ ] Generate sparse delta lists instead of full vertex arrays where useful.
- [ ] Drop deltas below threshold by profile.
- [ ] Quantize face deltas conservatively, e.g. 16-bit signed by default.
- [ ] Allow body/clothing deltas to use lower precision at LOD1+ where
  validated.
- [ ] Disable blendshape compute dispatch when all active weights are zero.
- [ ] Add counters for active shapes, delta bytes, and compute dispatches.

Acceptance criteria:

- [ ] Sparse/quantized blendshapes reduce memory and compute cost without
  breaking referenced shapes.

## Phase 6 - Protected Blendshape Allowlists

- [ ] Preserve ARKit-style face tracking shapes unless user opts in.
- [ ] Preserve VRM standard blendshape clips.
- [ ] Preserve VRChat viseme set names.
- [ ] Preserve any blendshape referenced by active animation channels.
- [ ] Preserve any blendshape referenced by runtime scripts.
- [ ] Keep eyelid, viseme, and lip-sync shapes non-PCA at LOD0 by default.
- [ ] Add report warnings for any protected shape touched by an opted-in risky
  operation.

Acceptance criteria:

- [ ] Facial tracking, blink, viseme, and expression controls survive default
  optimization.

## Phase 7 - PCA Blendshape Compression

- [ ] Build deterministic matrix of candidate blendshape deltas.
- [ ] Exclude protected shapes from PCA at LOD0.
- [ ] Apply PCA basis compression to suitable brow, cheek, jaw, body, or
  clothing shape groups.
- [ ] Store basis vectors and per-shape coefficients.
- [ ] Reconstruct per-frame delta from active weights and basis data.
- [ ] Report chosen basis size, memory reduction, max error, and average error.
- [ ] Reject PCA for groups that exceed profile error thresholds.
- [ ] Add deterministic SVD or fixed-seed algorithm policy.

Acceptance criteria:

- [ ] PCA compression is measured and reversible by profile; it is not applied
  blindly to protected facial shapes.

## Phase 8 - Validation And Remaps

- [ ] Compare representative expression poses before/after.
- [ ] Compare sampled animation poses before/after.
- [ ] Compute max/average position error and normal error.
- [ ] Compute skinning error heatmaps.
- [ ] Compute blendshape error heatmaps.
- [ ] Persist bone remap and blendshape remap tables.
- [ ] Validate bounds contain animated and blendshape-extreme poses.
- [ ] Validate renderer buffers consume optimized palettes and sparse deltas.

Acceptance criteria:

- [ ] Optimized skin/skeleton/blendshape data is smaller and still animates
  within profile thresholds.

## Final Validation And Merge

- [ ] Run skinning, animation, blendshape, remap, and bounds tests.
- [ ] Run visual validation on blendshape-heavy and spring-bone corpus avatars.
- [ ] Update roadmap and docs if runtime reference discovery rules change.
- [ ] Merge branch `avatar-skin-skeleton-blendshape-optimization` back into
  `main` after implementation, validation, and documentation updates are
  complete.
