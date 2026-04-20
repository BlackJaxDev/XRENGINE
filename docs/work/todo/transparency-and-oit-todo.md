# Transparency and OIT Implementation TODO

Last Updated: 2026-04-19
Current Status: Core transparency taxonomy, GPU-driven scaffolding, masked-path plumbing, alpha-to-coverage state support, weighted blended OIT, and exact-mode prototypes are implemented. Remaining work is validation, diagnostics hardening, quality comparison, experimental follow-up, and documentation.
Scope: track only unfinished work from the [design plan](../design/transparency-and-oit-implementation-plan.md).

## Non-Goals

- Do not ship stochastic transparency or per-triangle sorting until their prerequisites are proven.
- Do not redesign the render graph for transparency alone; integrate into the existing pipeline structure.
- Do not optimize GPU sort kernels before the first technique (weighted blended OIT) is visually correct.
- Do not add exact OIT to all backends simultaneously; start with the backend that has the best atomics and storage support.

---

## Phase 1 — Masked Path Hardening

Outcome: masked cutout materials render cleanly with depth writes, never entering the blended transparency pipeline.

### 1.2 Content Validation

- [ ] Validate Sponza foliage: leaves and vines render with depth and no blended artifacts.
- [ ] Validate chain-link and fence geometry.
- [ ] Validate lace, curtain, and other fabric cutout content.
- [ ] Confirm no regression in opaque content rendering.

### 1.3 Import Hardening

- [ ] Add warning diagnostic when diffuse-alpha-only materials are auto-classified as `AlphaBlend`.

Acceptance criteria:

- [ ] Sponza-class foliage, fences, and fabric render without transparency artifacts.

---

## Phase 2 — Alpha-To-Coverage

Outcome: masked material edges are smooth under MSAA without sacrificing depth correctness.

### 2.2 Material Integration

- [ ] Ensure fragment shader outputs alpha correctly for coverage conversion.

### 2.3 Validation

- [ ] Start editor with MSAA 4x, load Sponza, and compare foliage edge quality vs. hard cutoff.
- [ ] Test camera motion for shimmer or temporal instability under A2C.
- [ ] Test at MSAA 2x and 8x for sample-count sensitivity.
- [ ] Confirm no performance cliff vs. standard masked rendering.

Acceptance criteria:

- [ ] Foliage and cutout edges are visibly smoother under MSAA with A2C vs. hard cutoff.
- [ ] Fallback to hard cutoff when MSAA is off works correctly.
- [ ] No measurable perf regression vs. standard masked pass.

---

## Phase 3 — Weighted Blended OIT

Outcome: general translucent materials (glass, particles, decals) render without object-sort artifacts.

### 3.8 Validation

- [ ] Validate intersecting glass panes: no order-dependent banding.
- [ ] Validate layered particles: smooth blending without pop.
- [ ] Validate translucent foliage over bright backgrounds: no washout.
- [ ] Validate VR stereo consistency: both eyes match.
- [ ] A/B compare against the old `TransparentForward` path.
- [ ] Measure performance of accumulate + resolve vs. old sorted blend.

Acceptance criteria:

- [ ] Intersecting transparent objects render without order-dependent artifacts.
- [ ] Particles and glass look correct in the standard Sponza + glass test scene.
- [ ] Build and run with no new warnings.

---

## Phase 4 — Exact Transparency Evaluation

Outcome: decide whether weighted blended OIT is sufficient, or if an exact mode is worth shipping.

### 4.4 Quality Comparison

- [ ] Compare exact mode vs. weighted blended on intersecting glass, dense particles, and layered transparents.
- [ ] Measure memory overhead of exact mode.
- [ ] Measure GPU time overhead.
- [ ] Document findings and recommendation.

### 4.5 GPU-Driven Validation

- [ ] Test exact mode with GPU-driven culling active.

Acceptance criteria:

- [ ] Quality and performance comparison is documented.
- [ ] A go or no-go decision is made on whether to ship an exact mode.

---

## Phase 5 — Specialized and Experimental Modes

Outcome: optional niche techniques exist only if their prerequisites are met and content justifies them.

### 5.1 Stochastic Transparency (conditional)

Prerequisites: TAA or TSR temporal stability is mature, and motion vectors are reliable for transparent content.

- [ ] Validate prerequisite: temporal filter rejects ghosting aggressively.
- [ ] Validate prerequisite: camera jitter and reconstruction are stable.
- [ ] Add stochastic threshold generation (blue noise or hash).
- [ ] Integrate with TAA or TSR history.
- [ ] Test noise convergence over 4-16 frames on transparent content.
- [ ] Compare visual quality vs. weighted blended OIT.

### 5.2 Per-Triangle Sorting (conditional)

Prerequisites: a specific content class (for example ribbon meshes or shell models) is not adequately served by weighted blended OIT.

- [ ] Identify the content class that motivates this.
- [ ] Add mesh or submesh flag for triangle-sorted transparency.
- [ ] Implement GPU compute sort of triangle indices for flagged meshes.
- [ ] Test on the identified content class.
- [ ] Confirm GPU-only reordering with no CPU index buffer rebuilds.

Acceptance criteria:

- [ ] Each experimental mode is gated behind prerequisite validation.
- [ ] No experimental mode ships as a default path.
- [ ] Each mode works under GPU-driven rendering without CPU fallback.

---

## Cross-Phase Work

These items remain open across all phases.

### Shader Variant Management

- [ ] Define systematic variant axis for transparency: opaque, masked, OIT, and exact.
- [ ] Prevent ad-hoc transparency permutation growth across material families.
- [ ] Document shader variant strategy in rendering docs.

### Profiling and Telemetry

- [ ] Add transparent draw calls by mode counter.
- [ ] Add screen-space transparent overdraw estimate.
- [ ] Add weighted OIT resolve cost GPU timer.
- [ ] Add linked-list fragment count and overflow telemetry if Phase 4 ships.
- [ ] Add depth peel pass count per frame telemetry if Phase 4 ships.
- [ ] Add masked vs. blended material counts.

### Documentation

- [ ] Update `docs/architecture/` rendering docs when pass structure changes.
- [ ] Update material API docs for `ETransparencyMode`.
- [ ] Update import docs for the new import options.
- [ ] Keep the design plan in sync with implementation decisions.
