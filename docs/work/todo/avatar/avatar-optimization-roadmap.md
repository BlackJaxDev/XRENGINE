# Avatar Optimization Roadmap

Last Updated: 2026-05-29
Owner: Assets / Rendering / Editor
Status: Active roadmap
Target Branch: `avatar-optimization-roadmap`

Design source:

- [Avatar Optimization And Virtualized Avatar Rendering Design](../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [Engine Rendering Optimization Design](../../design/rendering/engine-optimization-and-avatar-optimizer-design.md)
- [Model Import Binary Cache Design](../../design/assets/model-import-binary-cache-design.md)
- [Texture Runtime Streaming And Virtual Texturing Design](../../design/texturing/texture-runtime-streaming-virtual-texturing-design.md)
- [GPU Skinning Buffer Compression](../../design/rendering/gpu/gpu-skinning-buffer-compression-plan.md)
- [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md)

## Goal

Let users bring expensive avatars into XRENGINE and optimize them inside the
editor without round-tripping through Blender, Maya, or another DCC tool. The
source asset stays immutable; the engine generates deterministic optimized
variants, reports, remap tables, LODs, meshlets, cluster payloads, and distant
LOD representations.

This roadmap coordinates the focused avatar TODOs:

- [Avatar Analyzer, Reporting, And UX TODO](avatar-analyzer-reporting-and-ux-todo.md)
- [Avatar Material And Texture Consolidation TODO](avatar-material-texture-consolidation-todo.md)
- [Avatar Mesh, Submesh, And Geometry Optimization TODO](avatar-mesh-submesh-geometry-optimization-todo.md)
- [Avatar Skin, Skeleton, And Blendshape Optimization TODO](avatar-skin-skeleton-blendshape-optimization-todo.md)
- [Avatar LOD, Meshlet, And Cooked Variant Publishing TODO](avatar-lod-meshlet-cooked-variant-todo.md)
- [Cluster-Virtualized Avatar Rendering TODO](cluster-virtualized-avatar-rendering-todo.md)
- [Gaussian-Splat Distant-Crowd LOD TODO](gaussian-splat-distant-crowd-lod-todo.md)

## Global Invariants

- Source FBX/glTF/USD files are never destructively edited.
- Every generated variant has a deterministic report, source hash, import
  settings hash, optimizer version, and remap tables.
- Protected bones, blendshapes, runtime references, facial features, visemes,
  and first-person/third-person flags are preserved unless the user explicitly
  opts in to a risky operation.
- Geometry simplification must consider deformation, skin weights, blendshape
  deltas, material borders, UV seams, hard normals, and silhouette error.
- Material consolidation must preserve render-state compatibility, color space,
  alpha coverage, and texture sampling semantics.
- Validation includes fixed-camera thumbnails, animation samples, heatmaps, and
  a perceptual gate such as FLIP.
- Optimized avatars are normal engine assets consumed by CPU direct,
  zero-readback, meshlet, visibility-buffer, cluster, and distant-LOD paths.
- Runtime selection must report active representation per avatar instance.

## Dependency Map

| Workstream | Blocks | Depends On |
| --- | --- | --- |
| Analyzer/reporting/UX | Every optimizer operation | Model import metadata, editor UI |
| Material/texture consolidation | Draw/material reduction, submesh merge | Texture system, material compatibility rules |
| Mesh/submesh/geometry optimization | LODs, cluster DAG, cooked variants | Analyzer, material remaps, topology tools |
| Skin/skeleton/blendshape optimization | LODs, runtime animation correctness | Animation system, runtime reference scanner |
| LOD/meshlet/cooked publishing | Runtime selection | Model import binary cache, GPUScene |
| Cluster-virtualized avatars | Over-budget close hero avatars | Optimized base mesh, visibility buffer, GPU compaction |
| Gaussian-splat distant crowds | Large social crowds | Optimized avatar variants, animation samples |

## Phase 0 - Branch, Baseline, And Asset Corpus

- [ ] Create dedicated branch `avatar-optimization-roadmap`.
- [ ] Choose an initial avatar validation corpus:
  - [ ] observed 1.08M triangle / 62 material avatar
  - [ ] simple humanoid with clean materials
  - [ ] hair-card-heavy avatar
  - [ ] blendshape-heavy face avatar
  - [ ] VRM avatar with spring bones
  - [ ] accessory-heavy avatar
  - [ ] intentionally broken import for validation failures
- [ ] Record baseline metrics for each corpus asset: draw calls, submeshes,
  material slots, textures, texture memory, vertices, triangles, bones,
  influences, blendshapes, shader variants, and current renderer cost.
- [ ] Record baseline visual captures and sampled animation poses.
- [ ] Link every focused TODO from this roadmap and from `docs/work/README.md`.

Acceptance criteria:

- [ ] The initial corpus exposes material fan-out, geometry density, skinning,
  blendshape, hair, special-region, and runtime-reference cases.

## Phase 1 - Analyzer And Read-Only Report

- [ ] Complete Phase 0 through Phase 3 of
  [Avatar Analyzer, Reporting, And UX TODO](avatar-analyzer-reporting-and-ux-todo.md).
- [ ] Produce deterministic read-only reports for every corpus asset.
- [ ] Show cost ranking by engine impact, not just raw triangle count.
- [ ] Surface warnings for material fan-out, texture memory, skin influences,
  blendshape cost, hidden mouth parts, hair cards, and missing LODs.

Acceptance criteria:

- [ ] Users can understand why an avatar is expensive before any optimization
  mutates generated copies.

## Phase 2 - Safe Material And Texture Reduction

- [ ] Complete compatibility, atlas planning, texture array planning, and
  material/submesh remap work in
  [Avatar Material And Texture Consolidation TODO](avatar-material-texture-consolidation-todo.md).
- [ ] Preserve sRGB/linear sampling semantics.
- [ ] Preserve alpha coverage for masked materials.
- [ ] Keep hair, eyes, mouth, eyelashes, and accessories under special rules.
- [ ] Generate deterministic atlas manifests and preview textures.

Acceptance criteria:

- [ ] Compatible opaque material groups can reduce material slots and texture
  binds without changing visible appearance beyond profile thresholds.

## Phase 3 - Geometry, Skin, Skeleton, And Blendshapes

- [ ] Complete mesh/submesh merge, constrained simplification, and edge-loop
  removal in
  [Avatar Mesh, Submesh, And Geometry Optimization TODO](avatar-mesh-submesh-geometry-optimization-todo.md).
- [ ] Complete skin influence, skeleton pruning, bone remap, and blendshape
  optimization in
  [Avatar Skin, Skeleton, And Blendshape Optimization TODO](avatar-skin-skeleton-blendshape-optimization-todo.md).
- [ ] Validate deformation over bind pose, T-pose, A-pose, locomotion, extreme
  reach, crouch/sit, and facial range-of-motion samples.
- [ ] Preserve protected bones and blendshapes by default.

Acceptance criteria:

- [ ] Optimized avatars preserve identity and animation within the selected
  profile error budget.

## Phase 4 - LODs, Meshlets, And Cooked Variants

- [ ] Complete LOD generation, meshlet generation, remap persistence, and cooked
  variant publishing in
  [Avatar LOD, Meshlet, And Cooked Variant Publishing TODO](avatar-lod-meshlet-cooked-variant-todo.md).
- [ ] Integrate generated variants with model import binary cache.
- [ ] Integrate generated texture/material/meshlet payloads with GPUScene,
  material tables, texture streaming, and shader prewarm.
- [ ] Add runtime selection by project, platform, asset override, quality
  setting, and editor preview.

Acceptance criteria:

- [ ] The engine can load the source avatar for editing and an optimized variant
  for runtime without confusing asset identity or remaps.

## Phase 5 - Cluster-Virtualized Close Avatar Path

- [ ] Complete cluster DAG construction, cluster-local skinning payloads, GPU
  selection, streaming, material customization, and fallback work in
  [Cluster-Virtualized Avatar Rendering TODO](cluster-virtualized-avatar-rendering-todo.md).
- [ ] Validate with visibility-buffer shading and optimized LOD fallback.
- [ ] Report actual cost against the 90 Hz whole-frame XR budget.

Acceptance criteria:

- [ ] Over-budget hero avatars can route to a bounded close-up rendering path
  after cooking, with visible profiler reporting and fallback.

## Phase 6 - Distant Crowd LOD

- [ ] Complete bake, skeleton-bound animation, sort/composite, transition, and
  fallback work in
  [Gaussian-Splat Distant-Crowd LOD TODO](gaussian-splat-distant-crowd-lod-todo.md).
- [ ] Preserve visible identity for each unique user's avatar at distance.
- [ ] Validate against octahedral impostor fallback.

Acceptance criteria:

- [ ] A 50+ unique-avatar distant crowd has a measured budget and graceful
  degradation path.

## Phase 7 - Regression And Publishing

- [ ] Add automated regression scenes with unoptimized and optimized avatars.
- [ ] Add source-contract tests for deterministic reports, remap table
  presence, protected-bone preservation, protected-blendshape preservation,
  atlas color-space rules, and validation failures.
- [ ] Add visual comparison captures for the corpus.
- [ ] Add performance targets for the observed high-material avatar.
- [ ] Add editor workflows for Analyze, Generate Plan, Preview, Optimize Copy,
  Compare, and Publish Variant.

Acceptance criteria:

- [ ] Users can produce optimized variants from the editor and runtime can select
  them without manual DCC work.

## Final Validation And Merge

- [ ] Run targeted asset, rendering, material, texture, skinning, and editor UI
  tests.
- [ ] Run Release performance captures for source and optimized variants.
- [ ] Update design docs if implementation changes core contracts.
- [ ] Merge branch `avatar-optimization-roadmap` back into `main` after
  implementation, validation, and documentation updates are complete.
