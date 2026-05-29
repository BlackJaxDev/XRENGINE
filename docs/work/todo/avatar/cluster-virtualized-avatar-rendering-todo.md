# Cluster-Virtualized Avatar Rendering TODO

Last Updated: 2026-05-29
Owner: Rendering / Assets
Status: Active
Target Branch: `avatar-cluster-virtualized-rendering`

Design source:

- [Avatar Optimization And Virtualized Avatar Rendering Design](../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [Avatar Optimization Roadmap](avatar-optimization-roadmap.md)
- [Visibility Buffer Rendering TODO](../rendering/optimization/visibility-buffer-rendering-todo.md)
- [Compact Zero-Readback Rendering TODO](../rendering/optimization/compact-zero-readback-rendering-todo.md)

## Goal

Provide a Nanite-class path for over-budget close-up avatars: a streaming
cluster DAG with screen-space-error selection, per-cluster skinning,
zero-readback GPU selection, visibility-buffer shading, material customization,
and optimized-LOD fallback.

## Scope

- Offline cluster DAG construction.
- Cluster-local skinning and optional blendshape payloads.
- GPU cluster selection and compaction.
- Per-cluster two-phase Hi-Z.
- Hardware raster and optional software raster path.
- Visibility-buffer material customization layer.
- Cluster residency and streaming feedback.
- Runtime cost reporting and fallback.

## Non-Goals

- Do not replace ordinary optimized LODs for avatars that already meet budget.
- Do not require software rasterization on hardware lacking required atomics.
- Do not claim source triangle count is irrelevant; material/shader complexity,
  active clusters, visible pixels, and skinned vertices still matter.
- Do not support every blendshape-heavy face case in the first cluster path.

## Phase 0 - Branch, Feature Gate, And Baseline

- [ ] Create dedicated branch `avatar-cluster-virtualized-rendering`.
- [ ] Add feature flag for cluster-virtualized avatars.
- [ ] Select over-budget hero-avatar corpus assets.
- [ ] Capture optimized LOD baseline for those assets.
- [ ] Capture visibility-buffer baseline if available.
- [ ] Define required backend capabilities: subgroup/wave operations,
  indirect-count drawing, visibility-buffer support, and optional 64-bit image
  atomics for software raster path.

Acceptance criteria:

- [ ] Unsupported hardware falls back explicitly before any cluster path is
  selected.

## Phase 1 - Cluster DAG Construction

- [ ] Take optimized LOD0 mesh as input.
- [ ] Cluster triangles into groups of 64-128 with locality-preserving
  partitioning or meshoptimizer meshlets.
- [ ] Group clusters into neighbor cluster-groups.
- [ ] Simplify each group as a unit using skinning-aware and
  appearance-preserving cost metrics.
- [ ] Lock shared group boundaries where needed.
- [ ] Re-cluster simplified output.
- [ ] Repeat until a root cluster covers the mesh.
- [ ] Store parent/child DAG links, screen-space error, bounds, and material
  references.
- [ ] Validate monotonicity: parent is cheaper and lower quality than children.

Acceptance criteria:

- [ ] Cluster DAG can reconstruct valid coverage from root to leaf without gaps
  or duplicate ownership.

## Phase 2 - Skinning Extension

- [ ] Store cluster-local bone palettes.
- [ ] Store per-vertex weights at LOD-appropriate influence count.
- [ ] Store optional sparse per-cluster blendshape deltas.
- [ ] Store cluster bounding sphere and deformation bounds from sampled
  animation set.
- [ ] Add compute skinning dispatch for active clusters.
- [ ] Output skinned position, skinned normal, and previous-frame skinned
  position for motion vectors.
- [ ] Validate deformation bounds do not clip animated extremities.

Acceptance criteria:

- [ ] Cluster culling and rasterization use animation-safe bounds and motion
  vector data.

## Phase 3 - GPU Selection And Compaction

- [ ] Cull avatar instances against last-frame Hi-Z.
- [ ] Traverse cluster DAG on GPU.
- [ ] Compute projected screen-space error for parent vs children.
- [ ] Emit parent when error is within budget; recurse into children when not.
- [ ] Compact selected cluster IDs with subgroup prefix sum.
- [ ] Handle active-list overflow with clamp, counter, and fallback.
- [ ] Cull selected clusters against current-frame Hi-Z in phase 2.
- [ ] Report selected clusters, phase 1/2 counts, overflow, and selection time.

Acceptance criteria:

- [ ] Cluster selection is GPU-resident and performs no current-frame readback.

## Phase 4 - Raster Path

- [ ] Route large clusters through hardware raster path: mesh shader or indexed
  multi-draw indirect.
- [ ] Route tiny clusters through optional software raster path only when
  required capabilities exist.
- [ ] For software raster path, define packed depth/payload format and atomic
  behavior.
- [ ] Provide fallback to hardware-raster cluster path or optimized LOD chain
  when software raster requirements are missing.
- [ ] Write visibility-buffer payload compatible with renderer visibility
  shading.
- [ ] Write valid depth and velocity.

Acceptance criteria:

- [ ] Cluster raster output can shade through the visibility-buffer path and can
  fall back safely where optional features are unavailable.

## Phase 5 - Material Customization Layer

- [ ] Define material customization slots: tint colors, mask gradient stops,
  eye iris textures, outfit pattern selectors, decal layers, emissive masks,
  fabric type, and hair color.
- [ ] Store customization values in a per-instance buffer.
- [ ] Keep customization separate from immutable material constants.
- [ ] Fetch material constants by material ID and customization values by
  instance ID in visibility-buffer material shading.
- [ ] Preserve customization slots across material consolidation.
- [ ] Allow customization values to change without re-cooking cluster payloads.
- [ ] Add tests for slot preservation and runtime update.

Acceptance criteria:

- [ ] Deferred/visibility-buffer rendering does not prevent high material
  customizability for avatars.

## Phase 6 - Streaming And Residency

- [ ] Store root cluster as always resident.
- [ ] Stream deeper clusters as avatar approaches camera or mirror view.
- [ ] Track cluster residency map.
- [ ] Select deepest resident cluster on requested path.
- [ ] Bias toward resident parents when children are missing.
- [ ] Write requested-but-missing cluster IDs to feedback buffer.
- [ ] Consume feedback on a later frame through streaming system.
- [ ] Add prefetch for disocclusion and sudden camera cuts.

Acceptance criteria:

- [ ] Missing cluster data degrades quality conservatively instead of stalling or
  popping to invalid geometry.

## Phase 7 - Cost Model And Profiling

- [ ] Report cluster cull/select/compact time.
- [ ] Report active skinned vertices and skinning time.
- [ ] Report cluster raster time.
- [ ] Report material tile shading time.
- [ ] Report total close hero-avatar cost per stereo frame and per
  eye-dependent pass.
- [ ] Compare against initial 90 Hz target budget:
  - [ ] cluster cull/select/compact: about 0.3 ms target
  - [ ] skinning: about 0.5 ms per 50K active vertices target
  - [ ] raster: about 1.0 ms typical close-up target
  - [ ] material tile shading: about 1.0 ms target
  - [ ] total close hero avatar: about 3 ms target
- [ ] Treat targets as engineering goals, not literature guarantees.

Acceptance criteria:

- [ ] Profiler output explains whether cost scales with visible pixels, active
  clusters, material shading, or active skinned vertices.

## Phase 8 - Validation And Fallback

- [ ] Validate close-up quality against optimized LOD reference.
- [ ] Validate VR stereo transition and head-motion stability.
- [ ] Validate material customization changes.
- [ ] Validate cluster streaming under fast camera movement.
- [ ] Validate fallback to optimized LOD chain.
- [ ] Validate no current-frame readbacks.
- [ ] Add source-contract tests for required capabilities and fallback routing.

Acceptance criteria:

- [ ] Cluster-virtualized avatars can be enabled per profile and disabled
  without breaking runtime avatar rendering.

## Final Validation And Merge

- [ ] Run rendering, visibility-buffer, zero-readback, asset, and VR smoke tests
  relevant to cluster avatars.
- [ ] Update roadmap/design docs if cluster payload contracts change.
- [ ] Merge branch `avatar-cluster-virtualized-rendering` back into `main`
  after implementation, validation, and documentation updates are complete.
