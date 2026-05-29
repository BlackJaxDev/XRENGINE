# Avatar LOD, Meshlet, And Cooked Variant Publishing TODO

Last Updated: 2026-05-29
Owner: Assets / Rendering
Status: Active
Target Branch: `avatar-lod-meshlet-cooked-variant`

Design source:

- [Avatar Optimization And Virtualized Avatar Rendering Design](../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [Avatar Optimization Roadmap](avatar-optimization-roadmap.md)
- [Model Import Binary Cache Design](../../design/assets/model-import-binary-cache-design.md)
- [GPU Meshlet Zero-Readback Rendering Design](../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [GPU Meshlet Zero-Readback Rendering TODO](../rendering/gpu/gpu-meshlet-zero-readback-rendering-todo.md)

## Goal

Publish optimized avatar outputs as normal engine assets: LODs, generated
materials/textures, remap tables, meshlet payloads, validation reports, and
runtime selection metadata. Warm runtime loads should not rebuild optimization
results.

## Scope

- LOD chain generation.
- Meshlet generation from optimized LODs.
- Octahedral impostor fallback payloads.
- Cooked optimized avatar variant assets.
- Remap table persistence.
- Model import binary cache integration.
- Runtime selection and editor preview.

## Non-Goals

- Do not train Gaussian splats in this TODO; that has its own distant-crowd
  tracker.
- Do not implement cluster DAG virtualized rendering in this TODO.
- Do not use runtime guesses for LODs when cooked LODs are available.
- Do not confuse source asset identity with generated variant identity.

## Phase 0 - Branch, Schema, And Publishing Contract

- [ ] Create dedicated branch `avatar-lod-meshlet-cooked-variant`.
- [ ] Define `AvatarOptimizationAsset` schema with source asset ID, source
  hash, profile, report, LODs, generated textures, generated materials, and
  remap tables.
- [ ] Define `OptimizedAvatarLod` payload schema.
- [ ] Define generated asset naming and identity rules.
- [ ] Define source-to-generated dependency and invalidation rules.
- [ ] Define deterministic serialization ordering.

Acceptance criteria:

- [ ] A generated avatar variant can be identified, invalidated, and debugged
  separately from its source import.

## Phase 1 - LOD Chain Generation

- [ ] Generate LOD0 from the optimized base mesh.
- [ ] Generate LOD1 with reduced material/triangle/texture cost while
  preserving animation.
- [ ] Generate LOD2 and lower with stricter draw count, influence count, and
  texture budgets.
- [ ] Generate crowd LODs with reduced bones and optional blendshape removal
  where profile permits.
- [ ] Store mesh geometry, material bindings, texture atlas/array references,
  skin weights, bone palette, optional blendshape set, bounds, error metric,
  and transition threshold per LOD.
- [ ] Use conservative VR transition distances based on head position, not
  per-eye disagreement.
- [ ] Add LOD preview and scrub data for editor UI.

Acceptance criteria:

- [ ] Each generated LOD has a measured error and deterministic transition
  policy.

## Phase 2 - Meshlet Generation

- [ ] Generate meshlets for each eligible optimized LOD.
- [ ] Store CPU meshlet descriptors distinct from GPU descriptors.
- [ ] Store vertex-reference indices, triangle-local indices, bounds, cones,
  settings, and meshoptimizer stats.
- [ ] Include meshoptimizer version, generation settings, LOD settings, source
  mesh identity, and freshness hash.
- [ ] Represent disabled meshlet generation explicitly.
- [ ] Support stale or missing meshlet repair from cached optimized mesh chunks
  without parsing source model.
- [ ] Register meshlet payloads with GPUScene on load.

Acceptance criteria:

- [ ] Warm-cache avatar load consumes cooked meshlets and does not rebuild them
  during render startup.

## Phase 3 - Octahedral Impostor Fallback

- [ ] Generate octahedral impostor payloads for very-far fallback profiles.
- [ ] Bake 8x8 or 12x12 view samples where profile requests it.
- [ ] Store impostor atlas, depth/normal if needed, bounds, and transition
  metadata.
- [ ] Preserve identity enough for typical far-distance viewing.
- [ ] Use impostors as fallback when Gaussian splat bake/runtime path is not
  available.
- [ ] Validate VR transition by head position.

Acceptance criteria:

- [ ] Distant LOD has a deterministic fallback even before Gaussian splats are
  available.

## Phase 4 - Remap Tables

- [ ] Persist original-to-optimized vertex remap.
- [ ] Persist original-to-optimized triangle remap.
- [ ] Persist material remap.
- [ ] Persist bone remap.
- [ ] Persist blendshape remap.
- [ ] Persist generated texture/material references.
- [ ] Make remaps available to editor selection, animation binding, attachments,
  debugging, re-optimization, and reports.
- [ ] Add tests proving remap tables are present and valid for generated
  variants.

Acceptance criteria:

- [ ] Generated variants remain explainable and editable through source remaps.

## Phase 5 - Cooked Cache Integration

- [ ] Store optimized variants in the model import binary cache or adjacent
  generated-asset authority.
- [ ] Include source hash, import settings hash, profile hash, optimizer
  version, meshlet settings hash, material/texture manifest hashes, and schema
  version in freshness checks.
- [ ] Hydrate hierarchy/manifest data separately from heavy mesh, morph,
  skeleton, LOD, texture, and meshlet payloads where useful.
- [ ] Repair stale LOD or meshlet chunks without source parse when cached
  optimized mesh data is sufficient.
- [ ] Add cache telemetry: read/write time, bytes, slow reads/writes, mesh
  count, LOD count, meshlet count, and stale repair count.

Acceptance criteria:

- [ ] Warm runtime loads use generated optimized assets without repeating
  optimizer work.

## Phase 6 - Runtime Selection

- [ ] Select source or optimized variant by project default profile.
- [ ] Select by platform profile.
- [ ] Allow per-asset override.
- [ ] Allow runtime quality setting override.
- [ ] Allow editor preview override.
- [ ] Report active representation per avatar instance.
- [ ] Ensure CPU direct, zero-readback, meshlet, visibility-buffer, cluster, and
  distant-LOD paths can consume the selected variant.
- [ ] Keep source avatar loadable for editing.

Acceptance criteria:

- [ ] Runtime can switch between source and optimized variants without asset
  identity confusion.

## Phase 7 - Validation

- [ ] Validate mesh topology and bounds for every LOD.
- [ ] Validate materials, textures, UVs, tangents, skin weights, bone indices,
  and blendshape references.
- [ ] Validate LOD transitions in mono and VR.
- [ ] Validate profiler counters report source vs optimized variant.
- [ ] Validate shader prewarm includes generated materials and LOD paths.
- [ ] Validate generated meshlets render through production meshlet path where
  supported and fallback where not.

Acceptance criteria:

- [ ] Generated LOD and meshlet variants are valid, selectable, cooked, and
  renderer-ready.

## Final Validation And Merge

- [ ] Run model-cache, meshlet, remap, LOD, and renderer integration tests.
- [ ] Run editor smoke for publishing and selecting optimized variants.
- [ ] Update docs if generated asset schema changes.
- [ ] Merge branch `avatar-lod-meshlet-cooked-variant` back into `main` after
  implementation, validation, and documentation updates are complete.
