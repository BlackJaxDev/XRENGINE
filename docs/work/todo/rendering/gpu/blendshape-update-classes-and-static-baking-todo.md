# Blendshape Update Classes And Static Baking TODO

Last Updated: 2026-06-01
Status: Draft
Target Branch: none - user explicitly requested no branch for this pass
Scope: per-blendshape update policy, runtime upload routing, and static
blendshape baking.

Related docs:

- [Blendshape Compression And GPU Efficiency TODO](blendshape-compression-and-gpu-efficiency-todo.md)
- [Avatar Skin, Skeleton, And Blendshape Optimization TODO](../../avatar/avatar-skin-skeleton-blendshape-optimization-todo.md)
- [Avatar Optimization And Virtualized Avatar Rendering Design](../../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [GPU-Driven Animation Architecture](../../../design/rendering/gpu/gpu-driven-animation.md)

## Goal

Let assets classify each blendshape by update behavior so the renderer can use
the cheapest safe path:

- `Streamed`: weights are expected to change every frame, such as face tracking,
  visemes, and live mocap shapes.
- `Dynamic`: weights change occasionally from gameplay, emotes, toggles, or
  scripts.
- `Static`: weights are authored or configured once and can be baked into the
  mesh rest data.

The main win is making static customization shapes disappear from runtime
buffers and shader loops, while keeping face-tracking shapes on a fast
per-frame upload path.

## Non-Goals

- Do not silently bake script-controlled, tracking, viseme, eyelid, or protected
  shapes.
- Do not change imported blendshape names or external animation bindings.
- Do not require PCA/SVD basis compression to land first.
- Do not remove the existing dense/sparse/quantized fallback paths.

## Phase 0 - Policy Metadata

- [ ] Add `BlendshapeUpdateClass`:
  - [ ] `Streamed`,
  - [ ] `Dynamic`,
  - [ ] `Static`.
- [ ] Store per-shape update class on `XRMesh` cooked/imported metadata.
- [ ] Default all imported shapes to `Dynamic` unless an importer/profile
  explicitly marks them otherwise.
- [ ] Add protected-name/profile rules that can force `Streamed` or `Dynamic`
  and block accidental `Static` baking.
- [ ] Add editor diagnostics showing each shape's update class.

Acceptance criteria:

- [ ] Imported assets preserve stable shape identity and can report/update a
  class per blendshape without changing current runtime behavior.

## Phase 1 - Static Shape Baking

- [ ] Add a bake path that applies static shape weights into base
  position/normal/tangent data.
- [ ] Rebase remaining non-static blendshape deltas against the baked rest mesh
  so dynamic/streamed shapes do not double-apply static offsets.
- [ ] Generate a deterministic cooked variant key from source mesh identity,
  static shape indices, static weights, and bake settings.
- [ ] Remove baked static shapes from runtime active lists, sparse records,
  quantized payloads, precombine eligibility, and shader permutation counts.
- [ ] Preserve editor ability to change static weights by invalidating and
  regenerating the cooked variant.
- [ ] Add tests for:
  - [ ] position-only static baking,
  - [ ] normal/tangent static baking,
  - [ ] remaining dynamic deltas after rebasing,
  - [ ] protected shapes refusing static bake,
  - [ ] deterministic variant keys.

Acceptance criteria:

- [ ] Static customization shapes produce identical visible output to runtime
  evaluation at the same weights, while paying zero runtime blendshape upload or
  shader cost.

## Phase 2 - Streamed Weight Fast Path

- [ ] Store streamed shape indices in a contiguous renderer-owned list.
- [ ] Upload streamed weights through a compact per-frame slice rather than the
  full authored shape range.
- [ ] Keep dynamic dirty-range uploads separate from streamed uploads.
- [ ] Ensure streamed shape active-list rebuilds allocate zero heap in steady
  state.
- [ ] Bias precombine heuristics toward streamed shapes only when the active
  streamed count and affected vertex count justify the extra dispatch.
- [ ] Add profiler counters for streamed vs dynamic upload bytes and active
  counts.

Acceptance criteria:

- [ ] Face-tracking-style shapes can update every frame without forcing every
  dynamic/static shape through the same hot path.

## Phase 3 - Dynamic Shape Event Path

- [ ] Keep dynamic shapes on the existing dirty range and active-list path.
- [ ] Avoid active-list rebuilds for dynamic shapes when only streamed weights
  changed.
- [ ] Add optional per-shape dirty events for animation systems that know a
  dynamic toggle changed.
- [ ] Validate interactions with LOD tiers, precombine, and global blendshape
  weight packing.

Acceptance criteria:

- [ ] Dynamic toggles and emotes remain cheap when unchanged and continue to
  compose correctly with streamed shapes.

## Phase 4 - Validation

- [ ] Run expression/viseme sweeps before and after static baking.
- [ ] Capture profiler deltas for:
  - [ ] static shapes removed from runtime payloads,
  - [ ] streamed upload bytes,
  - [ ] dynamic upload bytes,
  - [ ] active-list rebuild counts,
  - [ ] precombine dispatch counts.
- [ ] Run importer round-trip tests for authored update classes.
- [ ] Run cooked payload compatibility tests.
- [ ] Run `Report-NewAllocations` on streamed and dynamic update paths.

Acceptance criteria:

- [ ] The update-class system reduces runtime cost without changing protected
  facial identity, visemes, eyelids, or script-addressable controls.
