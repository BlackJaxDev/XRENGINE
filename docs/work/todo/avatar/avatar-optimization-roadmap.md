# Avatar Optimization Roadmap

Last Updated: 2026-10-06
Status: Planned. No avatar optimizer code exists.
Design: [Avatar Optimization And Virtualized Avatar Rendering](../../design/rendering/avatar-optimization-and-virtualized-rendering-design.md), [Engine Optimization And Avatar Optimizer](../../design/rendering/engine-optimization-and-avatar-optimizer-design.md), [Model Import Binary Cache](../../design/assets/model-import-binary-cache-design.md), [Texture Runtime Streaming](../../design/texturing/texture-runtime-streaming-virtual-texturing-design.md), [GPU Skinning Buffer Compression](../../design/rendering/gpu/gpu-skinning-buffer-compression-plan.md), [GPU-Accelerated Modeling Tools](../../design/modeling/gpu-accelerated-modeling-tools-design.md), [GPU Meshlet Zero-Readback Rendering](../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md), [GPU Skinned BVH Proxy LOD](../../design/rendering/gpu/gpu-skinned-bvh-proxy-lod-design.md)
Architecture: [Mesh Submission Strategies](../../../architecture/rendering/mesh-submission-strategies.md). Write `docs/architecture/avatar/avatar-optimization.md` when the optimizer code lands.
Validation: [Avatar Validation](../../testing/avatar/avatar-validation.md#avatar-optimization)

## Current State

No `AvatarAnalyzer`, optimization profile, report, plan, consolidation, simplification, LOD publishing, cluster, or splat-avatar code exists. Related foundations exist: the model import binary cache with `ModelBinaryMeshletSectionCodec` and `ModelBinaryMeshletSectionService`, GPUScene meshlet submission, and generic Gaussian splat rendering (`GaussianSplatCloud`, `GaussianSplatComponent`).

## Shared Invariants

- Source FBX, glTF, and USD files are never edited. The engine generates deterministic optimized variants.
- Every generated variant has a deterministic report, source hash, import settings hash, optimizer version, and remap tables.
- Protected bones, blendshapes, runtime references, facial features, visemes, and first-person and third-person flags are kept unless the user opts in to a risky operation.
- Geometry simplification considers deformation, skin weights, blendshape deltas, material borders, UV seams, hard normals, and silhouette error. Validation never uses bind pose alone.
- Material consolidation keeps render-state compatibility, color space, alpha coverage, and sampling semantics.
- Optimized avatars are normal engine assets for CPU direct, zero-readback, meshlet, visibility-buffer, cluster, and distant-LOD paths. Runtime reports the active representation per avatar instance.
- Reports explain rejected operations. Cost is never estimated by triangle count alone.

Dependency order: analyzer, then material and texture, then geometry and skin, then LOD and cooked publishing, then cluster and distant crowd.

## Open Code Items

### Analyzer, profiles, and reports

- [ ] Add `AvatarAnalyzer` metrics: mesh and submesh counts; material count and compatibility groups; texture count, dimensions, formats, color spaces, mips, compression, and memory; vertex and triangle counts; seam-duplicated vertices; vertex cache and overdraw estimates; edge, loop, boundary, UV seam, hard normal, and material-border summaries; bone count, per-mesh palette, influence distribution, unused bones, and near-zero weights; blendshape count, sparse deltas, max delta, regions, and bindings; bounds and screen-space error; draw cost (draws, material slots, shader variants, residency, skinning, blendshapes). Done when: the analyzer runs without changing any asset.
- [ ] Rank issues by engine impact (material slots and draws, texture residency, triangles and vertices, skinning and blendshape cost, shader variants, meshlet, LOD, and cache cost), with observed renderer counters where available. Flag 50+ material slots, dominant textures, dominant blendshape sets, over-target influences, and hair-card, eye, inner-mouth, eyelash, and accessory regions. Done when: the report ranks these items.
- [ ] Add `AvatarOptimizationProfile` (target, max draws, materials, LOD0 triangles, texture pixels, texture array layers, skin influences, bone palette size, screen-space error, normal error, skinning error, blendshape policy) with defaults Desktop High, VR Performance, Crowd/NPC, and Mobile/Standalone. Done when: the profiles serialize.
- [ ] Generate an `AvatarOptimizationPlan` with proposed operations, expected savings, risk, required validation, and rejection reasons, without executing operations. Done when: two runs on one asset and profile give byte-identical report and plan output in a unit test.
- [ ] Persist `AvatarOptimizationReport` with schema and optimizer versions, deterministic collection order, before and after metrics, operations, rejected candidates, visual error summary, generated asset references, remap references, source hash, import settings hash, and profile hash, next to generated variants or in the model import cache manifest. Done when: serialization and invalidation unit tests (source hash, import settings, profile, optimizer version) pass.
- [ ] Add an `Avatar Optimizer` editor panel with Analyze, Generate Plan, Preview, Optimize Copy, Compare, and Publish Variant; summary rows; profile selector and custom budgets; before and after slots; unsafe-operation warnings; LOD preview and scrub; placeholders for atlas preview, skin-weight heatmap, and edge-loop preview. Done when: the panel shows source and generated variants as distinct.
- [ ] Write `docs/architecture/avatar/avatar-optimization.md` when the first optimizer slice lands. Done when: the doc describes the landed contracts.

### Material and texture consolidation

- [ ] Inventory materials, render states, shader features, textures, samplers, UV transforms, and pass participation. Define compatibility keys, deterministic material IDs, and canonical order. Done when: every material is in a group or rejected with a reason in the report.
- [ ] Enforce compatibility rules: same or convertible shading model, same transparency domain, culling, depth policy, shadow caster policy, shader features after pruning, vertex attributes, and pass participation. Done when: unit tests reject opaque and transparent, masked and alpha, double- and single-sided, and pass mismatches.
- [ ] Merge identical materials by content hash and parameter-compatible materials into one generated material with atlas row offsets. Bake constant colors only when it reduces shader features. Keep customization slots and a material remap table. Done when: unit tests cover identical merge, parameter merge, customization preservation, and rejection reporting.
- [ ] Plan atlases by semantic (albedo, normal, ORM, emissive, mask) with color-space separation, compression compatibility, mip gutters, block-safe padding, UV island expansion, source rectangles, and rejection of unsupported wrap modes and animated UVs. Use deterministic packing (canonical material ID, then descending area, stable tie-breaks). Done when: packing is deterministic in a unit test and an atlas preview with island labels exists.
- [ ] Generate atlas or array textures and a `MaterialAtlasManifest`. Remap UVs or emit atlas transforms. Keep masked alpha coverage and split or reject over threshold. Regenerate tangents with MikkTSpace and keep importer normal-map handedness. Done when: the generated assets load.
- [ ] Add texture arrays for compatible small textures and linear-only ORM packing. Use default formats: BC7 sRGB albedo (BC3 fallback for alpha), BC5 normal, BC7 linear ORM (BC1 at lower LODs), BC4 masks, BC6H HDR. Generate mips, streaming metadata, and residency hints. Done when: unit tests cover sRGB and linear separation, normal compression, ORM packing, and array compatibility.
- [ ] Detect hair (names, alpha behavior, long thin quads), eyes (cornea, sclera, iris), inner mouth, teeth, tongue, eyelashes, and accessories. Offer hair alpha-to-coverage conversion and card decimation only with preview. Keep hair out of body atlases without consent. Add cull hints, lower-LOD removal candidates, warnings, and overlays. Done when: special regions are excluded from generic rules.
- [ ] Register generated materials with material tables, textures with streaming, and shader prewarm requirements. Publish material and UV remaps. Report before and after slots, textures, memory, variants, and draw fan-out. Keep source materials when consolidation fails. Done when: the renderer uses generated materials.

### Mesh, submesh, and geometry

- [ ] Build half-edge topology with vertex, edge, face, corner, UV, normal, material, skin, and blendshape attributes. Mark protected regions (boundaries, seams, hard edges, material borders, eyelids, lips, fingers, joints, named regions, high weight or delta gradients, silhouette loops). Use deterministic element order. Done when: the report shows protected areas.
- [ ] Merge submeshes with the same final material and skeleton, compatible accessories, and tiny static decorative submeshes. Reorder triangles by material and vertex cache. Reject incompatible skeletons without a remap plan, different required blendshape sets, large bounds growth, and transparent merges that change draw order. Generate submesh, vertex, triangle, material, palette, and blendshape remaps. Done when: unit tests cover each rejection.
- [ ] Add a simplification cost on QEM or meshoptimizer with UV, normal, tangent, color, and material terms, skinning error over sampled animations, blendshape gradient error, border, seam, hard-edge, and silhouette penalties, profile weights, deterministic tie-breaks, and error heatmaps. Done when: candidates rank by profile error.
- [ ] Detect closed loops, open rings, parallel loop pairs, and quad strips, and compute normal angle, dihedral change, curvature change, screen error, UV stretch, skinning error, and blendshape error. Reject protected loops. Done when: unit tests reject flat loops at wrists, mouth, eyelids, and joints with unsafe gradients.
- [ ] Remove edge loops through a lazy-invalidation priority queue that removes a loop only when all profile thresholds pass, and interpolates positions, normals, tangents, UVs, colors, weights, and deltas. Done when: removal is deterministic in a unit test.
- [ ] After topology changes, regenerate tangents, recompute static and animated bounds, and check manifoldness, UVs, NaNs, tangent frames, material IDs, remaps, and skin and blendshape references. Reject or require acceptance when thresholds fail. Done when: the checks run in the optimizer pipeline.
- [ ] Add LOD-specific rules: conservative LOD0; lower LODs remove more loops, reduce blendshapes, influences, and bones, merge accessories, use smaller atlases, and switch transparent detail to masked or baked detail when approved. Done when: the profile drives each rule.

### Skin, skeleton, and blendshapes

- [ ] Version the animation sample set (bind, T-pose, A-pose, walk, run, jump apex, arms overhead, arms behind back, crouch or sit, facial range-of-motion sweep) in the profile and report. Done when: validation cannot pass on bind pose alone.
- [ ] Prune weights below threshold, keep top N, renormalize, and relax pruning near joints over threshold. Support 16-bit unorm, three 10-bit plus implicit fourth, 8-bit unorm with shader renormalization, and 1 to 2 influence profiles. Pack bone indices to the smallest safe format. Done when: unit tests compare skinned positions across the sample set.
- [ ] Build compact per-mesh or per-section bone palettes, split only when limits require and the split saves more than it costs in draws, and report palette diagnostics. Done when: unit tests cover remap correctness and invalid index rejection.
- [ ] Discover protected bones: weighted, animated, `PhysicsChainComponent` and VRM spring bones, IK, look-at, and aim targets, sockets and attachments, script string lookups, twist and roll bones, humanoid mapping bones, and first-person flags. Publish an original-to-optimized bone path remap. Done when: the report lists each protection reason.
- [ ] Prune bones with no weights, channels, socket role, runtime reference, or child dependency. Collapse helper bones only with profile permission. Keep removed names addressable through remaps. Report pruned and rejected bones. Done when: clips and attachments resolve after pruning in a unit test.
- [ ] Find blendshapes used by animations, visemes, expressions, runtime controls, and scripts. Store sparse deltas, drop deltas below threshold, quantize face deltas to 16-bit signed by default, and allow lower precision for body and clothing at LOD1+. Skip blendshape compute when all weights are zero. Add counters for active shapes, delta bytes, and dispatches. Done when: the counters report.
- [ ] Protect ARKit face shapes, VRM blendshape clips, VRChat visemes, and animation- and script-referenced shapes by default. Keep eyelid, viseme, and lip-sync shapes out of PCA at LOD0. Warn when an opted-in operation touches a protected shape. Done when: default runs keep these shapes.
- [ ] Add deterministic PCA basis compression (fixed SVD or seed policy) for non-protected shape groups with basis vectors, coefficients, per-frame reconstruction, reported basis size and errors, and rejection over threshold. Done when: a unit test gives the same basis for repeated runs.
- [ ] Persist bone and blendshape remaps. Make bounds contain animated and blendshape-extreme poses. Make renderer buffers use optimized palettes and sparse deltas. Done when: the renderer consumes the optimized data.

### LOD, meshlet, and cooked variants

- [ ] Define `AvatarOptimizationAsset` (source ID, source hash, profile, report, LODs, generated textures and materials, remaps) and `OptimizedAvatarLod` schemas with naming, identity, dependency, invalidation, and deterministic order rules. Done when: a generated variant has an identity separate from its source.
- [ ] Generate LOD0 from the optimized base, LOD1 and lower with stricter budgets, and crowd LODs with fewer bones and optional blendshape removal. Store geometry, bindings, atlas references, weights, palette, blendshapes, bounds, error, and transition threshold per LOD. Use head-position transition distances in VR. Done when: each LOD has a measured error and transition policy.
- [ ] Generate meshlets for eligible LODs with CPU and GPU descriptors, vertex-reference and triangle-local indices, bounds, cones, settings, meshoptimizer version and stats, source identity, and freshness hash. Represent disabled generation explicitly. Repair stale meshlets from cached optimized chunks. Register payloads with GPUScene on load. Done when: warm loads do not rebuild meshlets.
- [ ] Generate octahedral impostor payloads (8x8 or 12x12 views, atlas, optional depth and normal, bounds, transition metadata) as the far fallback. Done when: a far LOD exists without Gaussian splats.
- [ ] Persist vertex, triangle, material, bone, and blendshape remaps and generated asset references. Make them available to selection, animation binding, attachments, debugging, re-optimization, and reports. Done when: a unit test proves remap presence and validity.
- [ ] Store variants in the model import binary cache or an adjacent generated-asset authority with source, import settings, profile, optimizer, meshlet settings, material and texture manifest, and schema hashes. Hydrate manifests separately from heavy payloads. Repair stale LOD or meshlet chunks without a source parse. Add cache telemetry (time, bytes, slow I/O, mesh, LOD, and meshlet counts, repair count). Done when: warm loads skip optimizer work.
- [ ] Select source or optimized variant by project profile, platform profile, per-asset override, runtime quality setting, and editor preview. Report the active representation per instance. Keep the source loadable for editing. Done when: every submission path consumes the selected variant.

### Cluster-virtualized close avatars

- [ ] Add a feature flag and a capability check (subgroup operations, indirect-count draws, visibility buffer, optional 64-bit image atomics for software raster). Done when: unsupported hardware falls back before selection.
- [ ] Build a cluster DAG from optimized LOD0: 64 to 128 triangle clusters, neighbor groups, skinning-aware group simplification with locked shared boundaries, re-clustering to a root, and parent and child links with error, bounds, and material references. Done when: unit tests prove coverage without gaps or duplicate ownership and parent-child monotonicity.
- [ ] Store cluster-local palettes, LOD-appropriate weights, optional sparse blendshape deltas, and deformation bounds from the sample set. Add active-cluster compute skinning that writes position, normal, and previous position. Done when: motion vectors use the previous position.
- [ ] Add GPU selection: instance cull against last-frame Hi-Z, DAG traversal with projected error, subgroup prefix-sum compaction, overflow clamp, counter, and fallback, and phase-2 cull against current Hi-Z, with counters and timing. Done when: selection needs no current-frame readback.
- [ ] Route large clusters to mesh shader or indexed multi-draw indirect and tiny clusters to an optional software raster with a defined packed depth and payload format, with fallback when capabilities are missing. Write visibility-buffer payload, depth, and velocity. Done when: cluster output shades through the visibility-buffer path.
- [ ] Add a per-instance customization buffer (tints, mask gradient stops, iris textures, pattern selectors, decals, emissive masks, fabric type, hair color) separate from material constants, fetched by instance ID in visibility-buffer shading and kept across consolidation. Done when: unit tests cover slot preservation and runtime update without re-cooking.
- [ ] Add cluster streaming: a resident root, deeper clusters on approach or mirror view, a residency map, deepest-resident selection with parent bias, a requested-but-missing feedback buffer consumed on a later frame, and prefetch for disocclusion and cuts. Done when: missing data degrades without stalls.
- [ ] Report cull, select, and compact time, active skinned vertices and skinning time, raster time, material tile shading time, and total cost per stereo frame and per eye pass. Done when: the profiler shows these values.

### Gaussian-splat distant crowds

- [ ] Add a feature flag and distance bands with splat count ranges. Fall back to octahedral impostors. Done when: disabling splats keeps the LOD and impostor paths working.
- [ ] Capture bake input: 64 to 128 views in concentric rings, poses from the sample set, color, depth, normal, mask, and optional material ID, and the customization values used. Store a bake manifest (variant hash, profile hash, poses, view count, resolution, lighting, bake time). Done when: bake input is deterministic and invalidates on appearance changes.
- [ ] Fit 3D Gaussians (position, scale, rotation, opacity, color, optional spherical harmonics, optional mip anti-aliasing), prune low contribution, and quantize into a cooked payload. Report bake time, splat count, bytes, and error. Bound visible splats per band. Done when: the payload loads.
- [ ] Bind each Gaussian to bones or a bind-pose triangle with a local frame, skin it at runtime, and store the previous position. Warn for poses outside the sampled envelope. Done when: distant avatars animate.
- [ ] Cull splat avatars against the frustum and far Hi-Z, skin active splats on the GPU, sort (selectable radix, merge, tile-approximate, or hybrid, reported in the profiler), and bin into tiles with overflow handling and counters. Done when: cost scales with visible splats.
- [ ] Composite splats into a splat framebuffer with color, scene-compatible depth, and velocity, then into the main framebuffer with depth test and alpha blend. Add a fallback when the GPU path is unavailable. Done when: splats take part in reprojection.
- [ ] Cross-fade between the deepest triangle or cluster LOD and splats over N frames, with per-instance head-position distance, matched depth in the band, consistent velocity, and a debug view. Done when: both eyes always see the same representation.
- [ ] Bake per user avatar, modulate color through safe customization slots, rebake on significant changes, and report whether the payload is current or modulated. Done when: the report states the bake freshness.
- [ ] Add degradation rungs (fewer splats by distance, lower color complexity, lower distant animation rate, impostor fallback, hidden sub-threshold accessories where allowed) and report the active rung per avatar or batch. Done when: the rung appears in diagnostics.

### Tests

- [ ] Add contract tests for deterministic reports, remap presence, protected bones and blendshapes, atlas color-space rules, validation failures, cluster capability fallback routing, and splat bake determinism. Done when: the tests exist.

## Decisions Needed

- [ ] Choose the avatar validation corpus assets and confirm redistribution rights. Owner: assets.
- [ ] Decide whether reports live next to generated variants or in the model import cache manifest. Owner: assets.

## Out Of Scope

- Destructive source edits.
- Merging opaque and transparent materials, incompatible transparency domains, or linear data into sRGB channels.
- Replacing ordinary LODs with clusters for avatars that already meet budget.
- Software rasterization on hardware without the required atomics.
- Runtime splat generation and facial blendshapes at splat distance.
- A generic mannequin for all far avatars.
