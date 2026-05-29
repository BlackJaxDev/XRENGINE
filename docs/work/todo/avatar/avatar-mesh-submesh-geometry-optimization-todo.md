# Avatar Mesh, Submesh, And Geometry Optimization TODO

Last Updated: 2026-05-29
Owner: Assets / Modeling / Rendering
Status: Active
Target Branch: `avatar-mesh-geometry-optimization`

Design source:

- [Avatar Optimization And Virtualized Avatar Rendering Design](../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [Avatar Optimization Roadmap](avatar-optimization-roadmap.md)
- [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md)

## Goal

Reduce avatar geometry and submesh overhead while preserving silhouette,
materials, UVs, normals, tangents, deformation, blendshape behavior, culling
granularity, and remap/debug identity.

## Scope

- Mesh and submesh consolidation.
- Accessory merge/removal candidates.
- Half-edge topology analysis.
- Constrained simplification.
- Edge-loop removal.
- Tangent/bounds/manifold validation.
- Geometry remap tables.

## Non-Goals

- Do not simplify only in bind pose.
- Do not remove loops based only on adjacent normal angle.
- Do not merge transparent sections when draw order would change incorrectly.
- Do not remove protected facial, hand, joint, or silhouette-critical regions
  without explicit user approval.

## Phase 0 - Branch, Baseline, And Topology Audit

- [ ] Create dedicated branch `avatar-mesh-geometry-optimization`.
- [ ] Capture source mesh, submesh, vertex, triangle, boundary, seam, hard-edge,
  material-border, and topology metrics for corpus avatars.
- [ ] Build or integrate half-edge topology with vertex, edge, face,
  loop/corner, UV, normal, material, skin, and blendshape attributes.
- [ ] Mark protected topology regions: boundaries, UV seams, hard normal edges,
  material borders, eyelids, lips, fingers, joints, named regions, high
  skin-weight gradient, high blendshape delta gradient, and silhouette-critical
  loops.
- [ ] Define deterministic vertex, edge, face, and candidate ordering.

Acceptance criteria:

- [ ] Topology analysis can explain which areas are protected before
  simplification runs.

## Phase 1 - Mesh And Submesh Consolidation

- [ ] Merge submeshes with same final material and skeleton where culling loss
  is acceptable.
- [ ] Merge accessory meshes that share skeleton and transform hierarchy when
  profile allows.
- [ ] Merge tiny static decorative submeshes into parent bounds when culling
  loss is negligible.
- [ ] Reorder triangles by material and vertex-cache optimization.
- [ ] Reject incompatible skeletons unless a skeleton remap plan exists.
- [ ] Reject meshes with different blendshape sets when preservation is
  required.
- [ ] Reject merges that greatly expand bounds or reduce culling too much.
- [ ] Reject transparent merges that would change draw order.
- [ ] Generate submesh, vertex, triangle, material, bone palette, and blendshape
  delta remaps.

Acceptance criteria:

- [ ] Draw/submesh count can fall without losing animation, culling, or remap
  identity.

## Phase 2 - General Simplification Cost Metric

- [ ] Use Garland-Heckbert QEM or meshoptimizer as the geometry-only base.
- [ ] Extend cost with appearance-preserving terms for UVs, normals, tangents,
  colors, and material boundaries.
- [ ] Add skinning-aware deformation error over sampled animations.
- [ ] Add blendshape-delta-gradient error.
- [ ] Add material-border, UV seam, hard-edge, and silhouette penalties.
- [ ] Add profile weights for each cost term.
- [ ] Use deterministic tie-breaking by canonical vertex/edge IDs.
- [ ] Add debug heatmaps for simplification error.

Acceptance criteria:

- [ ] Simplification candidates are ranked by engine/profile error, not just
  geometric distance.

## Phase 3 - Edge-Loop Detection

- [ ] Detect closed loops.
- [ ] Detect open edge rings.
- [ ] Detect roughly parallel adjacent loop pairs.
- [ ] Detect quad-dominant strips.
- [ ] Compute adjacent loop normal angle.
- [ ] Compute dihedral angle change.
- [ ] Compute curvature change.
- [ ] Compute screen-space projected error.
- [ ] Compute UV stretch.
- [ ] Compute skinning error across sampled poses.
- [ ] Compute blendshape error across active shapes.
- [ ] Reject protected loops before presenting candidates.

Acceptance criteria:

- [ ] Flat loops around wrists, mouth, eyelids, and joints are rejected when
  deformation or blendshape gradients make them unsafe.

## Phase 4 - Edge-Loop Removal Execution

- [ ] Build priority queue of edge-collapse/removal candidates keyed by
  composite error.
- [ ] Pop cheapest candidate, re-evaluate cost, skip if stale.
- [ ] Use lazy invalidation instead of expensive decrease-key updates.
- [ ] Remove loop only when all profile thresholds pass:
  - [ ] adjacent loop normal angle
  - [ ] max dihedral change
  - [ ] projected screen-space error
  - [ ] skinning error
  - [ ] blendshape error
  - [ ] UV stretch
- [ ] Interpolate or reconstruct positions, normals, tangents, UVs, vertex
  colors, skin weights, and blendshape deltas.
- [ ] Mark incident edges dirty.
- [ ] Continue until budget is met or all candidates exceed thresholds.

Acceptance criteria:

- [ ] Edge-loop removal produces deterministic results and preserves protected
  deformation regions.

## Phase 5 - Cleanup And Validation

- [ ] Regenerate affected tangents with MikkTSpace.
- [ ] Recompute static and animated bounds.
- [ ] Validate manifoldness enough for selected operations.
- [ ] Validate no invalid UVs, NaNs, or degenerate tangent frames.
- [ ] Validate material IDs and remap tables.
- [ ] Validate skin and blendshape references after topology changes.
- [ ] Render before/after thumbnails and error heatmaps.
- [ ] Reject or require explicit user acceptance when geometric or perceptual
  thresholds fail.

Acceptance criteria:

- [ ] Simplified meshes are renderer-ready and animation-safe for selected
  profiles.

## Phase 6 - LOD-Specific Simplification

- [ ] Keep LOD0 conservative.
- [ ] Allow lower LODs to remove more loops.
- [ ] Allow lower LODs to reduce or remove blendshapes where profile permits.
- [ ] Allow lower LODs to limit skin influences further.
- [ ] Allow lower LODs to merge accessories.
- [ ] Allow lower LODs to use smaller atlases.
- [ ] Allow lower LODs to use fewer bones.
- [ ] Allow lower LODs to switch transparent detail to masked or baked texture
  detail when approved.
- [ ] Generate meshlets from simplified LOD results.

Acceptance criteria:

- [ ] LOD chain has measured error and transition thresholds, not runtime
  guesses.

## Final Validation And Merge

- [ ] Run topology, simplification, remap, tangent, and validation tests.
- [ ] Run visual before/after validation on corpus avatars.
- [ ] Update linked docs if simplification contracts change.
- [ ] Merge branch `avatar-mesh-geometry-optimization` back into `main` after
  implementation, validation, and documentation updates are complete.
