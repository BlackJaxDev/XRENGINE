# Avatar Material And Texture Consolidation TODO

Last Updated: 2026-05-29
Owner: Assets / Rendering / Texturing
Status: Active
Target Branch: `avatar-material-texture-consolidation`

Design source:

- [Avatar Optimization And Virtualized Avatar Rendering Design](../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md)
- [Avatar Optimization Roadmap](avatar-optimization-roadmap.md)
- [Texture Runtime Streaming And Virtual Texturing Design](../../design/texturing/texture-runtime-streaming-virtual-texturing-design.md)
- [Material Table And Texture Binding Ladder TODO](../rendering/optimization/material-table-and-texture-binding-ladder-todo.md)

## Goal

Reduce avatar material slots, texture residency, shader variants, and draw
fan-out without silently changing appearance. Consolidation must preserve
render-state compatibility, color space, alpha coverage, mip behavior,
compression safety, UV behavior, and special avatar regions.

## Scope

- Material compatibility analysis.
- Identical material merge.
- Parameter-compatible material merge.
- Texture atlas and texture array planning.
- ORM/channel packing where valid.
- UV remap and atlas manifests.
- Submesh material remap handoff.
- Hair, eyes, mouth, eyelashes, and accessories special cases.

## Non-Goals

- Do not merge incompatible transparency domains by default.
- Do not merge opaque and transparent materials.
- Do not pack linear ORM data into sRGB albedo channels.
- Do not merge hair into body atlases without explicit user consent.
- Do not destructively edit source textures or source material assets.

## Phase 0 - Branch, Baseline, And Compatibility Audit

- [ ] Create dedicated branch `avatar-material-texture-consolidation`.
- [ ] Capture analyzer reports for corpus avatars.
- [ ] Inventory source materials, render states, shader features, textures,
  samplers, UV transforms, and pass participation.
- [ ] Define material compatibility keys.
- [ ] Define deterministic material IDs and canonical sort order.
- [ ] Add report fields for consolidation candidates and rejection reasons.

Acceptance criteria:

- [ ] Every source material is assigned to a compatibility group or rejected
  with a specific reason.

## Phase 1 - Material Compatibility Rules

- [ ] Require same shading model or explicitly convertible shading model.
- [ ] Require same transparency domain: opaque, masked, alpha blend, additive.
- [ ] Require same culling mode unless user explicitly approves change.
- [ ] Require compatible depth write/test policy.
- [ ] Require compatible shadow caster policy.
- [ ] Require compatible shader features after static pruning.
- [ ] Require compatible vertex attribute requirements.
- [ ] Reject incompatible pass participation unless generated variants can cover
  both without excess cost.
- [ ] Add tests for opaque/transparent rejection, masked/alpha rejection,
  double-sided/single-sided rejection, and pass mismatch rejection.

Acceptance criteria:

- [ ] Compatibility rules reject unsafe merges before atlas or submesh changes.

## Phase 2 - Safe Material Merge

- [ ] Merge identical materials by content hash.
- [ ] Merge parameter-compatible materials into one generated material with
  atlas row offsets.
- [ ] Bake constant colors into atlas textures only when it reduces shader
  feature count and passes visual validation.
- [ ] Preserve customization slots declared by material assets.
- [ ] Preserve material IDs through a material remap table.
- [ ] Generate material remap diagnostics for renderer counters.
- [ ] Add tests for identical merge, parameter-compatible merge, customization
  preservation, and rejected merge reporting.

Acceptance criteria:

- [ ] Material count falls where safe without losing remap/debug identity.

## Phase 3 - Atlas Planning

- [ ] Group atlas candidates by texture semantic: albedo, normal, ORM,
  emissive, and mask.
- [ ] Preserve color space: sRGB and linear textures cannot share a sampled
  interpretation.
- [ ] Preserve compression compatibility where possible.
- [ ] Allocate mip gutters and block-compression-safe padding.
- [ ] Expand UV islands by mip-safe atlas padding.
- [ ] Track source rectangles and UV transforms.
- [ ] Reject unsupported wrap modes or animated UV transforms.
- [ ] Use deterministic bin packing: canonical material ID then descending area,
  stable MaxRects tie-breaking or equivalent.
- [ ] Add atlas preview texture with island labels.

Acceptance criteria:

- [ ] Atlas packing is deterministic and safe for mips and block compression.

## Phase 4 - Atlas Execution And UV Remap

- [ ] Generate `XRTexture2D` or texture-array assets for planned outputs.
- [ ] Generate `MaterialAtlasManifest`.
- [ ] Remap UVs or emit per-material atlas transform data.
- [ ] Preserve alpha coverage for masked materials by comparing alpha-test
  coverage before and after.
- [ ] Reject or split atlas if alpha coverage deviates beyond profile threshold.
- [ ] Avoid placing high-frequency normal maps beside unrelated content without
  sufficient gutters.
- [ ] Regenerate tangents with MikkTSpace after UV remap.
- [ ] Preserve normal map handedness captured by importer.

Acceptance criteria:

- [ ] Rendered before/after thumbnails pass profile visual thresholds for
  atlas-consolidated materials.

## Phase 5 - Texture Arrays And Channel Packing

- [ ] Convert compatible small textures into texture array layers where arrays
  preserve sampler behavior better than atlases.
- [ ] Pack ORM-style textures into shared linear channels only.
- [ ] Choose default compression formats:
  - [ ] albedo opaque: BC7 sRGB
  - [ ] albedo alpha mask: BC7 sRGB, BC3 fallback
  - [ ] albedo alpha blend: BC7 sRGB, BC3 fallback
  - [ ] normal: BC5
  - [ ] ORM: BC7 linear, BC1 linear at lower LODs
  - [ ] single-channel mask: BC4
  - [ ] HDR emissive/lightmap: BC6H
- [ ] Generate mipmaps and streaming metadata.
- [ ] Add texture residency hints.
- [ ] Add tests for sRGB/linear separation, normal compression, ORM packing,
  and texture-array compatibility.

Acceptance criteria:

- [ ] Texture memory and bind pressure fall without invalid color-space or
  sampler behavior.

## Phase 6 - Special Avatar Regions

- [ ] Detect hair via material names, transparency/alpha-test behavior, and long
  thin quad topology.
- [ ] Offer alpha blend to masked/alpha-to-coverage conversion for hair only
  when profile and preview allow it.
- [ ] Offer hair-card decimation candidates.
- [ ] Keep hair out of body atlases unless user explicitly consents.
- [ ] Detect eyes: cornea, sclera, iris; avoid auto-merging when refraction,
  parallax, or normal-mapped iris is used.
- [ ] Detect inner mouth, teeth, tongue, and eyelashes; add runtime cull hints
  and lower-LOD removal candidates.
- [ ] Detect accessories as separate root-bone subtrees or attachment-like
  material groups.
- [ ] Add special-region warnings and preview overlays.

Acceptance criteria:

- [ ] Special regions are not broken by generic consolidation rules.

## Phase 7 - Runtime And Renderer Integration

- [ ] Register generated materials with material tables.
- [ ] Register generated textures with texture streaming.
- [ ] Publish material and UV remap tables for mesh/submesh consolidation.
- [ ] Publish shader prewarm requirements for generated materials.
- [ ] Report before/after material slots, texture count, resident texture
  memory, shader variants, and renderer-facing draw fan-out.
- [ ] Ensure fallback keeps source materials when consolidation fails.

Acceptance criteria:

- [ ] Optimized variants reduce renderer material fan-out where compatible and
  keep detailed reasons where they cannot.

## Final Validation And Merge

- [ ] Run material compatibility, atlas packing, texture generation, and remap
  tests.
- [ ] Run visual before/after validation on corpus avatars.
- [ ] Update roadmap and design docs if compatibility contracts change.
- [ ] Merge branch `avatar-material-texture-consolidation` back into `main`
  after implementation, validation, and documentation updates are complete.
