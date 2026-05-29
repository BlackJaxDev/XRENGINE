# Avatar Analyzer, Reporting, And UX TODO

Last Updated: 2026-05-29
Owner: Assets / Editor
Status: Active
Target Branch: `avatar-analyzer-reporting-ux`

Design source:

- [Avatar Optimization And Virtualized Avatar Rendering Design](../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [Avatar Optimization Roadmap](avatar-optimization-roadmap.md)
- [Model Import Binary Cache Design](../../design/assets/model-import-binary-cache-design.md)

## Goal

Build the read-only analysis and reporting foundation for avatar optimization.
Users should be able to inspect an imported avatar, understand what makes it
expensive, choose a profile, preview an optimization plan, and preserve
deterministic reports for validation and cache invalidation.

## Scope

- `AvatarAnalyzer`.
- `AvatarOptimizationProfile`.
- `AvatarOptimizationReport`.
- Editor `Avatar Optimizer` panel.
- Cost ranking and warnings.
- Deterministic report hashing.
- Preview-only optimization plans.

## Non-Goals

- Do not mutate source assets.
- Do not execute destructive optimization operations in this TODO.
- Do not hide rejected operations; reports must explain why a candidate was not
  safe.
- Do not estimate cost only by triangle count.

## Phase 0 - Branch, Corpus, And Baseline

- [ ] Create dedicated branch `avatar-analyzer-reporting-ux`.
- [ ] Select the initial avatar corpus from
  [Avatar Optimization Roadmap](avatar-optimization-roadmap.md).
- [ ] Capture source asset hashes, import settings hashes, and current import
  metadata for each corpus asset.
- [ ] Define report schema version and optimizer version fields.
- [ ] Define deterministic ordering for all report collections.

Acceptance criteria:

- [ ] The analyzer can run on the corpus without changing any source or
  generated asset.

## Phase 1 - Metrics Collection

- [ ] Compute mesh count and submesh count.
- [ ] Compute material count and material compatibility groups.
- [ ] Compute texture count, dimensions, formats, color spaces, mip counts,
  compression formats, and estimated memory.
- [ ] Compute vertex count and triangle count.
- [ ] Compute duplicate vertices caused by UV, normal, tangent, color, or
  material seams.
- [ ] Compute vertex cache efficiency and overdraw estimates where practical.
- [ ] Build edge, loop/ring, boundary, UV seam, hard normal, and material-border
  topology summaries.
- [ ] Compute bone count, per-mesh bone palette, influence distribution, unused
  bones, and near-zero weights.
- [ ] Compute blendshape count, sparse delta count, max delta, affected regions,
  and active animation bindings.
- [ ] Compute bounds, silhouette contribution, and screen-space error estimates.
- [ ] Compute current renderer-facing draw cost estimate, including draw calls,
  material slots, shader variants, texture residency, skinning, and blendshape
  cost.

Acceptance criteria:

- [ ] Analyzer metrics match imported asset data and renderer-facing counts for
  representative avatars.

## Phase 2 - Engine-Specific Cost Ranking

- [ ] Rank issues by expected engine impact:
  - [ ] material slots and draw calls
  - [ ] texture residency and upload pressure
  - [ ] triangle and vertex cost
  - [ ] skinning and blendshape compute/upload cost
  - [ ] shader variant count and warmup cost
  - [ ] meshlet, LOD, and cache generation cost
- [ ] Include observed renderer counters where available.
- [ ] Flag 50+ material-slot avatars as high risk even when triangle count is
  acceptable.
- [ ] Flag high-resolution textures that dominate residency.
- [ ] Flag blendshape sets that dominate memory or compute.
- [ ] Flag skin influence distributions above profile targets.
- [ ] Flag hair-card-heavy, eye, inner-mouth, eyelash, and accessory regions
  for special handling.

Acceptance criteria:

- [ ] The report explains why the observed 62-material avatar is expensive even
  with lights disabled.

## Phase 3 - Profiles And Plans

- [ ] Add `AvatarOptimizationProfile` with target, max draw calls, max
  materials, max LOD0 triangles, max texture pixels, max texture array layers,
  max skin influences, max bone palette size, max screen-space error, max
  normal error, max skinning error, and blendshape policy.
- [ ] Define default profiles: Desktop High, VR Performance, Crowd/NPC, and
  Mobile/Standalone.
- [ ] Generate an `AvatarOptimizationPlan` from profile budgets and analyzer
  metrics.
- [ ] Include proposed operations, expected savings, risk, validation required,
  and rejection reasons.
- [ ] Keep plan generation deterministic.
- [ ] Do not execute operations during plan generation.

Acceptance criteria:

- [ ] Two runs on the same asset/profile produce byte-identical report and plan
  output.

## Phase 4 - Editor UX

- [ ] Add an `Avatar Optimizer` panel for imported character models.
- [ ] Add primary actions: Analyze, Generate Plan, Preview, Optimize Copy,
  Compare, and Publish Variant.
- [ ] Add summary rows for draw calls, materials, textures, memory, vertices,
  triangles, bones, influences, blendshapes, and estimated render cost.
- [ ] Add profile selector and custom budget controls.
- [ ] Add before/after metric slots for preview plans.
- [ ] Add warning surface for unsafe operations.
- [ ] Add LOD preview and scrub controls.
- [ ] Add material atlas preview placeholder.
- [ ] Add skin-weight heatmap placeholder.
- [ ] Add edge-loop candidate preview placeholder.
- [ ] Ensure source asset remains visibly distinct from generated variants.

Acceptance criteria:

- [ ] Users can analyze and plan optimization from the editor without creating
  an optimized copy yet.

## Phase 5 - Report Persistence

- [ ] Persist `AvatarOptimizationReport` with before/after metrics, operation
  list, rejected candidates, visual error summary, generated asset references,
  source asset hash, import settings hash, optimizer version, and profile hash.
- [ ] Store report next to generated variants or in the model import cache
  manifest.
- [ ] Include remap table references once generated by later TODOs.
- [ ] Add deterministic serialization tests.
- [ ] Add cache invalidation tests for source hash, import settings, profile,
  and optimizer version changes.

Acceptance criteria:

- [ ] Reports are stable enough for cache invalidation and regression tests.

## Final Validation And Merge

- [ ] Run analyzer/report serialization tests.
- [ ] Run editor panel smoke with at least one corpus avatar.
- [ ] Update roadmap links with validation evidence.
- [ ] Merge branch `avatar-analyzer-reporting-ux` back into `main` after
  implementation, validation, and documentation updates are complete.
