# Transparency and OIT Implementation TODO

Last Updated: 2026-03-11
Current Status: Phase 0 and Phase 0.5 implemented, Phase 1 masked-pass plumbing implemented, Phase 2 alpha-to-coverage state path implemented, scene validation pending
Scope: implement the transparency architecture defined in the [design plan](../design/transparency-and-oit-implementation-plan.md).

## Current Reality

What exists now:

- `ModelImporter.cs` separates masked cutout from blended transparency during import (fixed this session).
- `XRMaterial.EnableTransparency()` disables depth writes for blended materials (fixed this session).
- `TransparentForward` pass renders after opaque with far-to-near object sorting.
- No `ETransparencyMode` enum — transparency is inferred from scattered material flags.
- No OIT buffers, accumulation passes, or resolve passes.
- No alpha-to-coverage backend state path.
- GPU-driven transparent domain classification now exists via per-command metadata, domain counters, and visible-index lists.
- Dormant GPU sort code exists in `GPURenderPassCollection.Sorting.cs` (bitonic, radix, merge — all commented out).

What Phase 0.5 now adds:

- `GPUScene` uploads per-command transparency metadata in a dedicated SSBO parallel to the command buffer.
- `GPURenderPassCollection` classifies visible commands into `Masked`, `TransparentApproximate`, and `TransparentExact` GPU-visible lists.
- Transparent domain counters are published into engine render stats and shown in the editor Render Stats panel.
- The existing GPU radix/insertion sort path in `GPURenderBuildBatches.comp` remains the active transparent-sort scaffold, now covered by focused transparency scaffold tests.

What the bug fixes this session already accomplished:

- Masked materials (with explicit opacity maps) now route to opaque-like passes instead of `TransparentForward`.
- Blended materials now have `DepthTest.UpdateDepth = false`.
- `ModelImporter.HasTransparentBlendHint()` separates blend-hinted imports from masked imports.

## Target Outcome

At the end of this work:

- The engine has a formal `ETransparencyMode` classification on every material.
- Masked materials render with depth writes and optional alpha-to-coverage under MSAA.
- Weighted blended OIT is the primary transparency mode for glass, particles, and general translucents.
- All transparency modes work under the GPU-driven render path without CPU sorting.
- Debug views exist for every active transparency mode.
- Exact OIT modes (linked lists, depth peeling) are available behind quality settings if content demands them.

## Non-Goals

- Do not ship stochastic transparency or per-triangle sorting until their prerequisites are proven.
- Do not redesign the render graph for transparency alone — integrate into the existing pipeline structure.
- Do not optimize GPU sort kernels before the first technique (weighted blended OIT) is visually correct.
- Do not add exact OIT to all backends simultaneously — start with the backend that has the best atomics/storage support.

---

## Phase 0 — Transparency Taxonomy Cleanup

Outcome: the engine can distinguish masked from blended content reliably using a formal enum.

### 0.1 Add `ETransparencyMode` Enum

- [x] Create `ETransparencyMode` enum with values: `Opaque`, `Masked`, `AlphaBlend`, `PremultipliedAlpha`, `Additive`, `WeightedBlendedOit`, `PerPixelLinkedList`, `DepthPeeling`, `Stochastic`, `AlphaToCoverage`, `TriangleSorted`
- [x] Decide placement: likely `XRENGINE/Models/Materials/` or `XRENGINE/Rendering/`

### 0.2 Add Transparency Properties to `XRMaterial`

- [x] Add `TransparencyMode` property to `XRMaterial`
- [x] Add `AlphaCutoff` property (float, default 0.5)
- [x] Add `TransparentSortPriority` property (int, default 0)
- [x] Add `TransparentTechniqueOverride` property (nullable, for debug forcing)
- [x] Wire properties through serialization (MemoryPack)
- [x] Ensure `SetField(...)` usage for all new properties (XRBase rule)

### 0.3 Update Material Inspector

- [x] Expose `TransparencyMode` in ImGui material inspector
- [x] Expose `AlphaCutoff` slider for `Masked` and `AlphaToCoverage` modes
- [x] Show current inferred mode vs. explicit mode when they differ

### 0.4 Update Import Inference Rules

- [x] `ModelImporter`: set `TransparencyMode` based on existing `useTransparentBlend` / `hasAlphaMask` logic
- [x] Add import option `DiffuseAlphaMode`: `Opaque`, `Masked`, `Blended`, `Auto`
- [x] Add import option `OpacityMapMode`: `Masked`, `Blended`, `Auto`
- [x] Ensure particles/VFX explicitly opt into `AlphaBlend`, `PremultipliedAlpha`, or `Additive`

### 0.5 Add Debug Overlay

- [x] Add debug view: transparency mode overlay (color-coded per `ETransparencyMode`)
- [x] Add debug view: masked vs. blended classification overlay

Acceptance criteria:

- [x] Every material has an explicit `TransparencyMode` value after import.
- [x] The inspector correctly displays and allows editing the mode.
- [x] The debug overlay shows the classification across a test scene (Sponza).
- [x] Build passes with no new warnings.

---

## Phase 0.5 — GPU-Driven Transparency Scaffold

Outcome: the GPU-driven render path has the shared infrastructure all future transparency modes need.

### 0.5.1 Transparent Draw Metadata Buffer

- [x] Add per-draw transparency metadata to `GPUScene` (transparency mode, blend family, alpha cutoff, bounds, sort priority, flags)
- [x] Upload metadata alongside existing draw command registration
- [x] Incremental update on material/instance change

### 0.5.2 Transparent Visible-List Compaction

- [x] Add transparent visible-list output to `GPURenderPassCollection` culling pass
- [x] Compact visible transparent draws into a separate list per sort domain
- [x] Support domains: `Masked`, `TransparentApproximate`, `TransparentExact`

### 0.5.3 Sort-Key Infrastructure

- [x] Define packed sort-key layout: `[domain | priority | material bucket | depth]`
- [x] Add key generation compute pass writing per-draw sort keys for transparent draws
- [x] Add permutation buffer for indirect command reordering

### 0.5.4 Revive GPU Sort Primitives

- [x] Uncomment and formalize one GPU sort kernel in `GPURenderPassCollection.Sorting.cs` (radix or bitonic)
- [x] Validate sort correctness on synthetic draw key data
- [x] Add sort dispatch path callable from `HybridRenderingManager`

### 0.5.5 GPU Diagnostics

- [x] Add transparency counters: visible transparent draws, per-domain counts, sort domain overflow
- [x] Expose counters in the GPU dispatch diagnostics logger
- [x] Add ImGui overlay showing transparent draw stats per frame

Acceptance criteria:

- [x] `GPUScene` holds per-draw transparency metadata for all registered draws.
- [x] Culling output separates transparent draws by domain.
- [x] One GPU sort kernel passes correctness tests on synthetic data.
- [x] Diagnostics overlay shows domain counts in a test scene.

---

## Phase 1 — Masked Path Hardening

Outcome: masked cutout materials render cleanly with depth writes, never entering the blended transparency pipeline.

### 1.1 Dedicated Masked Route

- [x] Add `MaskedForward` pass concept to `DefaultRenderPipeline` (may share FBO with opaque forward)
- [x] Route all `ETransparencyMode.Masked` materials to `MaskedForward`
- [x] Confirm depth test on, depth write on for all masked content
- [x] Confirm `discard` / `AlphaCutoff` logic in masked shaders

### 1.2 Content Validation

- [ ] Validate Sponza foliage: leaves, vines render with depth and no blended artifacts
- [ ] Validate chain-link / fence geometry
- [ ] Validate lace / curtain / fabric cutout content
- [ ] Confirm no regression in opaque content rendering

### 1.3 Import Hardening

- [x] Verify `ModelImporter` routes opacity-map materials to `Masked` by default
- [ ] Add warning diagnostic when diffuse-alpha-only materials are auto-classified as `AlphaBlend`

Acceptance criteria:

- [x] Masked materials never appear in `TransparentForward`.
- Sponza-class foliage, fences, and fabric render without transparency artifacts.
- [x] Depth buffer contains contributions from masked content.

---

## Phase 2 — Alpha-To-Coverage

Outcome: masked material edges are smooth under MSAA without sacrificing depth correctness.

### 2.1 Backend State Support

- [x] Add alpha-to-coverage enable/disable to OpenGL rendering state
- [x] Add alpha-to-coverage enable/disable to Vulkan rendering state and pipeline sample-count parity for masked/A2C draws
- [x] Add `AlphaToCoverage` mode to `RenderingParameters` or equivalent state object

### 2.2 Material Integration

- [x] When `TransparencyMode == AlphaToCoverage` and MSAA is active, enable A2C state on draw
- [ ] Ensure fragment shader outputs alpha correctly for coverage conversion
- [x] When MSAA is disabled, fall back to `Masked` with `AlphaCutoff`

### 2.3 Validation

- [ ] Start editor with MSAA 4x, load Sponza, compare foliage edge quality vs. hard cutoff
- [ ] Test camera motion for shimmer / temporal instability under A2C
- [ ] Test at MSAA 2x and 8x for sample-count sensitivity
- [ ] Confirm no performance cliff vs. standard masked rendering

Acceptance criteria:

- Foliage and cutout edges are visibly smoother under MSAA with A2C vs. hard cutoff.
- Fallback to hard cutoff when MSAA is off works correctly.
- No measurable perf regression vs. standard masked pass.

---

## Phase 3 — Weighted Blended OIT

Outcome: general translucent materials (glass, particles, decals) render without object-sort artifacts.

### 3.1 Pipeline Resources

- [ ] Allocate `TransparentAccumTexture` (`RGBA16F`) in `DefaultRenderPipeline`
- [ ] Allocate `TransparentRevealageTexture` (`R16F` or `R8`)
- [ ] Add clear logic for both textures each frame
- [ ] Add FBO or attachment setup for accumulation pass

### 3.2 Accumulation Pass

- [ ] Add `TransparentAccumulation` pass after masked/opaque forward
- [ ] Set blend state: additive on accumulation target, multiplicative on revealage target
- [ ] Set depth test on, depth write off
- [ ] Route all `WeightedBlendedOit` materials through this pass

### 3.3 Resolve Pass

- [ ] Add `TransparentResolve` fullscreen pass
- [ ] Sample accumulation and revealage textures
- [ ] Composite approximate transparent color over scene color
- [ ] Position resolve after accumulation, before on-top/post-render

### 3.4 Shader Variants

- [ ] Create OIT fragment output variant for standard forward-plus material
- [ ] Output premultiplied weighted color + revealage instead of direct scene color
- [ ] Resolve shader: compute final transparent contribution from accumulators
- [ ] Choose and document the weight function (depth-based or constant)

### 3.5 Content Migration

- [ ] Set glass materials to `WeightedBlendedOit`
- [ ] Set particle materials to `WeightedBlendedOit` (or `Additive` where appropriate)
- [ ] Set decal materials with soft opacity to `WeightedBlendedOit`
- [ ] Keep UI overlays on existing path (no OIT)

### 3.6 GPU-Driven Integration

- [ ] Verify accumulation pass uses indirect transparent draw list from Phase 0.5
- [ ] Confirm no CPU-side sorting in the OIT path
- [ ] Verify material batching is preserved (order-independent accumulation)

### 3.7 Debug Views

- [ ] Add debug view: accumulation buffer visualization
- [ ] Add debug view: revealage buffer visualization
- [ ] Add debug view: transparent overdraw heatmap

### 3.8 Validation

- [ ] Intersecting glass panes: no order-dependent banding
- [ ] Layered particles: smooth blending without pop
- [ ] Translucent foliage over bright backgrounds: no washout
- [ ] VR stereo consistency: both eyes match
- [ ] A/B compare against old `TransparentForward` path
- [ ] Performance comparison: accumulate + resolve vs. old sorted blend

Acceptance criteria:

- Intersecting transparent objects render without order-dependent artifacts.
- Particles and glass look correct in the standard Sponza + glass test scene.
- No CPU sorting in the transparency pipeline.
- Debug overlays for accumulation and revealage work.
- Build and run with no new warnings.

---

## Phase 4 — Exact Transparency Evaluation

Outcome: decide whether weighted blended OIT is sufficient, or if an exact mode is worth shipping.

### 4.1 Prototype Selection

- [ ] Choose prototype: per-pixel linked lists or depth peeling (linked lists recommended for coverage)
- [ ] Implement behind a renderer debug setting (not default)
- [ ] Verify backend support: storage buffers, atomics, image writes for chosen technique

### 4.2 Per-Pixel Linked Lists (if chosen)

- [ ] Add per-pixel head-pointer image/buffer
- [ ] Add fragment node storage buffer with atomic allocator
- [ ] Add per-frame clear/reset
- [ ] Add compute resolve pass: walk lists, sort by depth, composite
- [ ] Add overflow detection and debug heatmap
- [ ] Define max fragment count policy

### 4.3 Depth Peeling (if chosen)

- [ ] Add peel depth and layer color targets
- [ ] Add repeated peel passes with depth comparison against prior peel
- [ ] Add layer compositing pass
- [ ] Add configurable max peel count

### 4.4 Quality Comparison

- [ ] Compare exact mode vs. weighted blended on intersecting glass, dense particles, layered transparents
- [ ] Measure memory overhead of exact mode
- [ ] Measure GPU time overhead
- [ ] Document findings and recommendation

### 4.5 GPU-Driven Validation

- [ ] Confirm exact mode uses indirect draw dispatch (no CPU sorting)
- [ ] Confirm correctness comes from fragment storage / peel passes, not draw order
- [ ] Test with GPU-driven culling active

Acceptance criteria:

- At least one exact mode runs behind a debug setting.
- Quality and performance comparison is documented.
- A go/no-go decision is made on whether to ship an exact mode.

---

## Phase 5 — Specialized and Experimental Modes

Outcome: optional niche techniques exist only if their prerequisites are met and content justifies them.

### 5.1 Stochastic Transparency (conditional)

Prerequisites: TAA/TSR temporal stability is mature; motion vectors are reliable for transparent content.

- [ ] Validate prerequisite: temporal filter rejects ghosting aggressively
- [ ] Validate prerequisite: camera jitter and reconstruction are stable
- [ ] Add stochastic threshold generation (blue noise or hash)
- [ ] Integrate with TAA/TSR history
- [ ] Test noise convergence over 4–16 frames on transparent content
- [ ] Compare visual quality vs. weighted blended OIT

### 5.2 Per-Triangle Sorting (conditional)

Prerequisites: a specific content class (e.g., ribbon meshes, shell models) is not adequately served by weighted blended OIT.

- [ ] Identify the content class that motivates this
- [ ] Add mesh/submesh flag for triangle-sorted transparency
- [ ] Implement GPU compute sort of triangle indices for flagged meshes
- [ ] Test on identified content class
- [ ] Confirm GPU-only reordering (no CPU index buffer rebuilds)

Acceptance criteria:

- Each experimental mode is gated behind prerequisite validation.
- No experimental mode ships as a default path.
- Each mode works under GPU-driven rendering without CPU fallback.

---

## Cross-Phase Work

These items should be addressed continuously across all phases.

### Shader Variant Management

- [ ] Define systematic variant axis for transparency: opaque / masked / OIT / exact
- [ ] Prevent ad-hoc transparency permutation growth across material families
- [ ] Document shader variant strategy in rendering docs

### Profiling and Telemetry

- [ ] Transparent draw calls by mode (counter)
- [ ] Screen-space transparent overdraw estimate
- [ ] Weighted OIT resolve cost (GPU timer)
- [ ] Linked-list fragment count and overflow (if Phase 4 ships)
- [ ] Depth peel pass count per frame (if Phase 4 ships)
- [ ] Masked vs. blended material counts

### Documentation

- [ ] Update `docs/architecture/` rendering docs when pass structure changes
- [ ] Update material API docs when `ETransparencyMode` ships
- [ ] Update import docs when new import options are added
- [ ] Keep design plan in sync with implementation decisions
