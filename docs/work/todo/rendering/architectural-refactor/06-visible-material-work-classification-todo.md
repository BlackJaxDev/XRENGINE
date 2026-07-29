# 06 - Visible Material Work Classification TODO

Last Updated: 2026-07-28
Owner: Rendering
Status: Proposed
Depends On: [05 - Attribute Reconstruction](05-attribute-reconstruction-todo.md)
Next: [07 - Native Material, Lighting, Decal, And GI Shading](07-native-material-lighting-decals-and-gi-todo.md)

## Goal

Convert final visibility into bounded GPU work grouped by compatible material
kernel and visible screen coverage. Work counts and dispatch arguments remain on
the GPU.

## Classification Key

The canonical key begins with:

```text
shading kernel
  + material layout
  + material state/coverage class
  + required attribute/derivative mode
  + view mode
```

Material-row ID is data within a compatible kernel unless a measured backend
requires a narrower grouping. Descriptor-set object identity is never part of
the logical key.

## TODO

### 1. Work Domain And Tile Policy

- [ ] Select initial tile dimensions from measured occupancy and subgroup
  behavior.
- [ ] Define mono and per-eye/layer addressing.
- [ ] Define active-tile, kernel-tile, and optional compact pixel-list records.
- [ ] Reserve capacities from screen size and documented worst-case material
  diversity.
- [ ] Define empty-pixel and background exclusion.

### 2. Classification Kernels

- [ ] Read final visibility and resolve the material/kernel key from GPU tables.
- [ ] Build active tiles and per-kernel tile membership.
- [ ] Add a compact pixel-list path for sparse or highly mixed tiles when it
  wins measured workloads.
- [ ] Use subgroup ballot/scan where available.
- [ ] Provide a deterministic bounded shared-memory fallback when subgroup
  operations are unavailable.
- [ ] Avoid atomics proportional to total registered material count.
- [ ] Skip empty tiles and kernels without CPU involvement.

### 3. GPU Dispatch Construction

- [ ] Prefix-sum or otherwise compact kernel/tile/pixel ranges.
- [ ] Build indirect dispatch arguments entirely on GPU.
- [ ] Keep a bounded fixed command topology over kernel families or a
  backend-supported indirect execution mechanism.
- [ ] Ensure data-only count changes do not rerecord reusable primary command
  packets.
- [ ] Insert the minimum resource-specific barriers before native shading.
- [ ] Keep delayed stats readback outside the frame dependency chain.

### 4. Capacity And Overflow

- [ ] Define overflow independently for active tiles, kernel memberships,
  pixel lists, and indirect argument ranges.
- [ ] Never drop pixels silently.
- [ ] In automatic mode, use a bounded conservative full-tile kernel fallback
  only when it preserves correctness.
- [ ] In required mode, expose an error surface and structured failure if
  correctness cannot be preserved.
- [ ] Record first overflow cause, required capacity, selected recovery, and
  affected pixels through delayed diagnostics.
- [ ] Grow persistent capacity only at safe frame boundaries.

### 5. Material Diversity And Kernel Scheduling

- [ ] Prove many material rows sharing one kernel do not create one dispatch
  per material.
- [ ] Order kernel work to reduce pipeline changes without changing visibility
  correctness.
- [ ] Prewarm engine-owned kernel families and backend variants.
- [ ] Define handling for rare kernels, shader compilation pending, and
  nonresident textures.
- [ ] Add editor diagnostics for material eligibility, kernel ID, and selected
  recovery.

### 6. Debugging And Telemetry

- [ ] Add views for active tiles, kernel IDs, material IDs, mixed-tile density,
  pixel-list density, dispatch ranges, and overflow.
- [ ] Add counters for visible pixels, active tiles, kernel-tile pairs,
  compacted pixels, active kernels, dispatches, overflows, and GPU time.
- [ ] Report work per eye for stereo resources.
- [ ] Make all classification buffers inspectable with stable capture names.

### 7. Validation

- [ ] Test empty, background-only, one-kernel, many-material-one-kernel,
  many-kernel, checkerboard, tiny-triangle, masked-edge, invalid-payload, and
  overflow scenes.
- [ ] Prove dispatch count follows visible kernel coverage rather than source
  material slot count.
- [ ] Prove zero same-frame readback under production strategies.
- [ ] Compare subgroup and fallback output deterministically.
- [ ] Benchmark tile-only and pixel-list paths before enabling adaptive
  selection.

## Acceptance Criteria

- [ ] Every valid visibility pixel is assigned exactly once to correct native
  material work.
- [ ] Material diversity within a kernel does not create CPU or GPU submission
  fan-out per material instance.
- [ ] Empty and offscreen materials produce no shading work.
- [ ] Overflow is correct, bounded, and observable.
- [ ] Production classification has zero same-frame readback and preserves
  reusable command topology.

