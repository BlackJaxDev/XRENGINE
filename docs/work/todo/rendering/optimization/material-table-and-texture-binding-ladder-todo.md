# Material Table And Texture Binding Ladder TODO

Last Updated: 2026-05-29
Owner: Rendering
Status: Active
Target Branch: `rendering-material-table-texture-ladder`

Design source:

- [Engine Rendering Optimization Design](../../../design/rendering/engine-optimization-and-avatar-optimizer-design.md)
- [Dynamic Indirect Material Bindings](../../../design/rendering/dynamic-indirect-material-bindings.md)
- [Texture Runtime Streaming And Virtual Texturing Design](../../../design/texturing/texture-runtime-streaming-virtual-texturing-design.md)

## Goal

Make material diversity a data problem instead of a CPU binding problem. The
renderer should use stable material IDs, generated material row layouts,
texture indirection, and a deterministic fallback ladder so every backend can
render correctly while the profiler reports the active rung.

## Scope

- Runtime backend capability probing.
- Texture binding rung selection and reporting.
- Texture arrays for homogeneous groups.
- Bindless handle tables where supported.
- Sparse or virtual texture handle interface.
- Coarse bucket fallback.
- Material table prewarm, caching, and dirty-range updates.

## Non-Goals

- Do not duplicate pass-declared row layout generation already tracked by
  [Dynamic Indirect Material Bindings](../../../design/rendering/dynamic-indirect-material-bindings.md).
- Do not treat texture arrays as the generic solution for arbitrary material
  diversity.
- Do not assume `ARB_bindless_texture` is fast or available on every OpenGL
  driver.
- Do not silently change material render state to fit a rung.

## Phase 0 - Branch, Baseline, And Audit

- [ ] Create dedicated branch `rendering-material-table-texture-ladder`.
- [ ] Capture material-table baselines for `MaterialTable`,
  `BindlessMaterialTable`, and coarse per-material/per-bucket fallback paths.
- [ ] Inventory all material table buffers, texture handle buffers, row packers,
  shader includes, and generated shader variants.
- [ ] Inventory all backend feature probes for texture arrays, bindless,
  sparse textures, descriptor indexing, and virtual texturing.
- [ ] Inventory every profiler field that reports material or texture binding
  mode.
- [ ] Confirm overlap and ownership boundaries with
  [Dynamic Indirect Material Bindings](../../../design/rendering/dynamic-indirect-material-bindings.md).

Acceptance criteria:

- [ ] Current material row and texture handle ownership is known before
  changing runtime selection.

## Phase 1 - Rung Model And Capability Probe

- [ ] Add an explicit enum for active texture binding rung:
  `TextureArray`, `Bindless`, `Sparse`, `CoarseBucket`, and `Unsupported`.
- [ ] Probe OpenGL texture array limits, bindless support, sparse texture
  support, residency behavior, and known vendor/driver limitations.
- [ ] Probe Vulkan descriptor indexing and sparse residency capabilities.
- [ ] Add backend preference settings and environment overrides for rung
  selection.
- [ ] Validate overrides at launch; invalid values fail loud.
- [ ] Report active rung in frame stats, profile capture JSON, and editor
  diagnostics.
- [ ] Record rung selection reason: highest supported, user override,
  driver denylist, validation failure, or fallback.

Acceptance criteria:

- [ ] Every frame capture states which texture binding rung rendered it and why.

## Phase 2 - Texture Array Rung

- [ ] Restrict texture arrays to groups with identical dimensions, format,
  mip count, sampler behavior, and compatible color space.
- [ ] Add deterministic grouping by semantic and compatibility key.
- [ ] Add array layer allocation and dirty-layer upload tracking.
- [ ] Reject incompatible wrap modes, animated UV transforms, mixed sRGB/linear
  sampling, and mixed compression requirements.
- [ ] Integrate with avatar/material consolidation output where atlas or array
  manifests declare compatible groups.
- [ ] Add counters for array count, layer count, layer uploads, and fallback
  reasons.
- [ ] Add tests for compatible groups, incompatible formats, sRGB/linear
  rejection, missing mips, and wrap-mode rejection.

Acceptance criteria:

- [ ] Texture arrays never silently sample incompatible textures through a
  shared sampler interpretation.

## Phase 3 - Bindless Rung

- [ ] Create 64-bit OpenGL texture handles when `ARB_bindless_texture` is
  supported and allowed.
- [ ] Make handles resident and track residency lifetime.
- [ ] Store handles in a GPU buffer indexed by material row texture indices.
- [ ] Retire unused handles only after GPU-safe lifetime has passed.
- [ ] Add a driver/vendor capability denylist or warning table if runtime
  validation exposes broken behavior.
- [ ] Add Vulkan descriptor-indexed equivalent through the dynamic material
  binding path.
- [ ] Add counters for handles created, resident, retired, failed, and fallback
  events.
- [ ] Add tests for handle table indexing, lifetime, material dirty updates,
  and fallback when bindless is unavailable.

Acceptance criteria:

- [ ] Bindless is never assumed. It is runtime-probed, reported, and reversible.

## Phase 4 - Sparse And Virtual Texture Interface

- [ ] Define the boundary between material table texture references and the
  texture streaming/virtual texturing page table.
- [ ] Add material row fields for sparse/virtual texture page table references
  where the pass layout declares them.
- [ ] Ensure the renderer can report `Sparse` as active without requiring full
  virtual texturing completion for all materials.
- [ ] Route page residency feedback through the texturing roadmap, not ad hoc
  render-submission code.
- [ ] Add fallback to bindless, texture array, or coarse bucket when sparse
  support is unavailable.
- [ ] Add counters for sparse page references, feedback writes, fallback events,
  and page misses.

Acceptance criteria:

- [ ] Sparse/virtual texture integration has a stable API boundary and does not
  force another material row refactor later.

## Phase 5 - Coarse Bucket Fallback

- [ ] Define a deterministic texture-set identity key for fallback buckets.
- [ ] Group draws by state class and texture-set identity.
- [ ] Bind each bucket once and draw all compatible active work inside it.
- [ ] Ensure fallback bucket generation consumes compact active work, not full
  material table capacity.
- [ ] Preserve pass semantics and transparent ordering.
- [ ] Report bucket count, empty bucket skips, and fallback reason.
- [ ] Add tests for deterministic grouping and transparent-order preservation.

Acceptance criteria:

- [ ] Coarse bucket fallback is correct on every backend and cheap enough after
  material consolidation reduces texture-set fan-out.

## Phase 6 - Material Table Dirty Updates And Prewarm

- [ ] Ensure material rows are updated by dirty ranges.
- [ ] Ensure texture handle tables update separately from material rows.
- [ ] Cache generated shader sources by source hash, pass layout hash, backend
  feature mask, and static property hash.
- [ ] Include active texture rung in shader/program cache keys where it changes
  source or bindings.
- [ ] Persist OpenGL program binaries and Vulkan/DX12 pipeline caches where
  supported.
- [ ] Ensure material table rows are ready before measured render frames.
- [ ] Add counters for material row bytes uploaded, dirty row ranges, generated
  variants, cache hits, and cache misses.

Acceptance criteria:

- [ ] Editing one material updates only affected rows and texture handles.
- [ ] Warm-start frames do not generate/link material-table shader variants for
  already-known layouts.

## Final Validation And Merge

- [ ] Run targeted material binding, shader generation, texture table, and
  profile capture tests.
- [ ] Run editor smoke with each available rung and fallback.
- [ ] Validate material-diverse avatar and Sponza-like static scene.
- [ ] Update linked material binding docs if runtime ladder behavior changes.
- [ ] Merge branch `rendering-material-table-texture-ladder` back into `main`
  after implementation, validation, and documentation updates are complete.
