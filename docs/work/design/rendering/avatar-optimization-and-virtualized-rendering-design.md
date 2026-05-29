# Avatar Optimization And Virtualized Avatar Rendering Design

Last Updated: 2026-05-29
Status: design proposal
Scope: automatic in-editor avatar optimization, generated avatar variants, cluster-virtualized skinned rendering, and Gaussian-splat distant-crowd LOD.

Related docs:

- [Avatar optimization roadmap](../../todo/avatar/avatar-optimization-roadmap.md)
- [Engine rendering optimization](engine-optimization-and-avatar-optimizer-design.md)
- [GPU meshlet zero-readback rendering design](gpu-meshlet-zero-readback-rendering-design.md)
- [Zero-readback GPU-driven rendering plan](zero-readback-gpu-driven-rendering-plan.md)
- [Dynamic indirect material bindings](dynamic-indirect-material-bindings.md)
- [Model import binary cache design](../assets/model-import-binary-cache-design.md)
- [Texture runtime streaming and virtual texturing design](../texturing/texture-runtime-streaming-virtual-texturing-design.md)
- [GPU-driven animation](gpu/gpu-driven-animation.md)
- [GPU skinning buffer compression](gpu/gpu-skinning-buffer-compression-plan.md)
- [GPU-accelerated modeling tools design](../modeling/gpu-accelerated-modeling-tools-design.md)

External references:

- Unreal Nanite virtualized geometry: <https://dev.epicgames.com/documentation/en-us/unreal-engine/nanite-virtualized-geometry-in-unreal-engine>
- Unreal Nanite GPU-driven materials: <https://www.unrealengine.com/blog/take-a-deep-dive-into-nanite-gpu-driven-materials>
- Nanite virtualized geometry deep dive (SIGGRAPH 2021): <https://advances.realtimerendering.com/s2021/Karis_Nanite_SIGGRAPH_Advances_2021_final.pdf>
- Visibility buffer rendering (Burns/Hunt): <https://jcgt.org/published/0002/02/04/>
- NVIDIA FLIP perceptual difference: <https://research.nvidia.com/publication/2020-07_flip>
- MikkTSpace tangent space: <https://github.com/mmikk/MikkTSpace>
- meshoptimizer simplification: <https://github.com/zeux/meshoptimizer>
- Garland-Heckbert QEM (1997): <https://www.cs.cmu.edu/~./garland/Papers/quadrics.pdf>
- Mohr & Gleicher deformation-sensitive decimation: <https://pages.cs.wisc.edu/~gleicher/Papers/deformation-sensitive-decimation.pdf>
- Hoppe appearance-preserving simplification: <https://hhoppe.com/proj/apsimp/>
- VRM blendshape (`BlendShapeClip`) spec: <https://github.com/vrm-c/vrm-specification/tree/master/specification>
- VRM spring bone spec: <https://github.com/vrm-c/vrm-specification/blob/master/specification/VRMC_springBone-1.0/README.md>
- ARKit `ARFaceAnchor` blendshape names: <https://developer.apple.com/documentation/arkit/arfaceanchor/blendshapelocation>
- Octahedral impostors (Ryan Brucks / Klemen Lozar): <https://shaderbits.com/blog/octahedral-impostors>
- Lozar IMP / impostor baker: <https://www.artstation.com/artwork/4XbylZ>
- 3D Gaussian Splatting (Kerbl et al., SIGGRAPH 2023): <https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/>
- Mip-Splatting / anti-aliased 3DGS: <https://niujinshuchong.github.io/mip-splatting/>
- Dynamic 3D Gaussians (animated splats): <https://dynamic3dgaussians.github.io/>
- GaussianAvatars (animatable head): <https://shenhanqian.github.io/gaussian-avatars>
- HUGS: Human Gaussian Splats: <https://machinelearning.apple.com/research/hugs>
- SplattingAvatar (mesh-embedded Gaussian splatting): <https://cislab.hkust-gz.edu.cn/publications/splattingavatar-realistic-real-time-human-avatars-with-mesh-embedded-gaussian-splatting/>

## 1. Summary

This document owns XRENGINE's avatar-side performance strategy. The renderer optimization design defines how CPU direct, zero-readback GPU-driven, meshlet, visibility-buffer, and stereo paths should behave. This document defines how imported character assets become renderer-friendly without requiring the user to leave the editor.

The avatar optimizer has three layers:

1. Automatic optimization: material consolidation, texture atlasing, mesh/submesh consolidation, constrained simplification, edge-loop removal, skin-weight reduction, blendshape compression, LOD generation, validation, and cooked variant publishing.
2. Cluster-virtualized avatar rendering: a Nanite-class path for close hero avatars and over-budget user content, built for skinned characters, VR stereo, customization slots, and visibility-buffer shading.
3. Distant-crowd representation: Gaussian-splat or octahedral-impostor LODs that preserve each user's appearance when dozens of unique avatars are visible.

The source avatar remains immutable. Every optimization produces deterministic generated variants, reports, remap tables, and validation artifacts so runtime behavior is fast without making user content impossible to debug.

## 2. Automatic Avatar Optimizer

### 2.1 Problem

Users will import avatars that are authored for offline rendering, social platforms, cinematic closeups, or DCC convenience. These assets often have:

- Too many material slots.
- Too many textures.
- Oversized textures.
- Excessive triangle density in flat regions.
- Edge loops that do not affect silhouette or deformation.
- Too many skin influences per vertex.
- Unused bones.
- Sparse or expensive blendshapes.
- Accessory meshes split into separate draw calls.
- High-resolution meshes without LODs.

The engine should not require users to leave the editor, open a modeling package, manually atlas textures, decimate meshes, prune weights, export again, and then debug import differences.

### 2.2 Goal

Provide an in-editor optimization system that can:

- Analyze an imported avatar.
- Explain where render cost comes from.
- Generate an optimization plan from user-selected budgets.
- Produce optimized asset variants without modifying the source asset.
- Preserve animation, skinning, materials, and visual identity within a measurable error budget.
- Generate LODs, atlas textures, consolidated materials, simplified meshes, compressed skinning data, and cooked renderer data.
- Feed optimized variants directly into GPUScene, material tables, texture streaming, and meshlet caches.

### 2.3 Non-goals

- This does not replace full DCC modeling tools.
- This does not promise perfect artistic results for every arbitrary mesh.
- This does not destructively edit source FBX/glTF/USD assets.
- This does not merge materials with incompatible render states unless the user explicitly accepts a visual change.
- This does not collapse facial rigs, visemes, or expression blendshapes by default.

## 3. Avatar Optimizer User Experience

### 3.1 Editor panel

Add an `Avatar Optimizer` panel for imported character models.

Primary actions:

- Analyze
- Generate Plan
- Preview
- Optimize Copy
- Compare
- Publish Variant

The panel shows:

- Current draw calls, materials, texture memory, vertices, triangles, bones, influences, blendshapes, and estimated render cost.
- Target budgets for desktop, VR, mobile, and custom profiles.
- Before/after metrics.
- Visual error estimate.
- Warnings for unsafe operations.
- LOD preview and scrub controls.
- Material atlas preview.
- Skin-weight heatmap.
- Edge-loop removal candidates.
- Per-operation savings and risk.

The system should always preserve the original import and produce a named optimized variant.

### 3.2 Profiles

Create optimization profiles:

```csharp
public sealed class AvatarOptimizationProfile
{
    public string Name { get; init; }
    public AvatarQualityTarget Target { get; init; }
    public int MaxDrawCalls { get; init; }
    public int MaxMaterials { get; init; }
    public int MaxTrianglesLod0 { get; init; }
    public int MaxTexturePixels { get; init; }
    public int MaxTextureArrayLayers { get; init; }
    public int MaxSkinInfluencesPerVertex { get; init; }
    public int MaxBonesPerMeshPalette { get; init; }
    public float MaxScreenSpaceErrorPixels { get; init; }
    public float MaxNormalErrorDegrees { get; init; }
    public float MaxSkinningErrorMeters { get; init; }
    public BlendshapeOptimizationPolicy BlendshapePolicy { get; init; }
}
```

Example profile targets:

| Profile | Intent |
| --- | --- |
| Desktop High | Keep facial quality, reduce worst material and texture fan-out. |
| VR Performance | Aggressive draw/material reduction, strict texture budget, conservative deformation preservation. |
| Crowd/NPC | Heavy LODs, reduced bones, few or no blendshapes beyond silhouette-critical shapes. |
| Mobile/Standalone | Strict material, texture, influence, and triangle budgets. |

### 3.3 Reports

Each optimization run writes:

- `AvatarOptimizationReport`
- before/after metrics
- operation list
- rejected operation candidates and reasons
- visual error summary
- generated asset references
- source asset hash and import settings hash
- optimizer version

Reports must be deterministic so cache invalidation and regression tests are meaningful.

## 4. Avatar Analysis

### 4.1 Metrics

The analyzer computes:

- Mesh count.
- Submesh count.
- Material count.
- Material compatibility groups.
- Texture count, dimensions, formats, color spaces, and memory.
- Vertex count and triangle count.
- Duplicate vertices caused by UV, normal, tangent, or color seams.
- Vertex cache efficiency and overdraw estimate.
- Edge count and loop/ring topology.
- Boundary, UV seam, hard normal, and material-border edges.
- Bone count and per-mesh bone palette.
- Influences per vertex distribution.
- Unused bones and near-zero weights.
- Blendshape count, sparse delta count, max delta, affected regions, and active animation bindings.
- Bounds, silhouette contribution, and screen-space error estimates.
- Current renderer-facing draw cost estimate.

### 4.2 Cost model

The optimizer ranks issues by expected engine impact:

1. Material slots and draw calls.
2. Texture residency and upload pressure.
3. Triangle and vertex cost.
4. Skinning and blendshape compute/upload cost.
5. Shader variant count and warmup cost.
6. Meshlet/LOD/cache generation cost.

The ranking is engine-specific. A 62-material avatar is often worse than a single-material avatar with the same triangle count because it fans out material state, shader variants, texture residency, and indirect buckets.

## 5. Material Consolidation

### 5.1 Compatibility

Materials may be consolidated only when their render-state contract is compatible:

- Same shading model or convertible shading model.
- Same transparency domain: opaque, masked, alpha blend, additive.
- Same culling mode or explicit user approval to change.
- Same depth write/test policy.
- Same shadow caster policy.
- Same required shader features after static pruning.
- Same vertex attribute requirements.

Do not automatically merge:

- Opaque with transparent.
- Masked with alpha blend.
- Double-sided with single-sided if silhouette changes.
- Materials that require different deformation or tessellation behavior.
- Materials with different pass participation unless a generated variant can cover both without excess cost.

### 5.2 Consolidation operations

Supported operations:

- Merge identical materials by content hash.
- Merge parameter-compatible materials into one material with atlas row offsets.
- Bake constant colors into atlas textures when that reduces shader feature count.
- Pack ORM-style textures into shared channels.
- Convert compatible small textures into array layers.
- Convert material-specific texture sets into atlases.
- Remap submesh material IDs.
- Merge submeshes that now share material, skeleton, transform, and pass behavior.

### 5.3 Atlas generation

Atlas rules:

- Group by texture semantic: albedo, normal, ORM, emissive, mask.
- Preserve color space: sRGB and linear textures cannot share a single sampled interpretation. ORM (linear) and albedo (sRGB) must never share an atlas texture; channel-packing ORM into albedo's alpha is also wrong because the sampler applies the sRGB curve to all channels.
- Preserve compression compatibility where possible.
- Allocate mip gutters and block-compression-safe padding (4-pixel for BC1–7).
- Expand UV islands by padding in atlas space, sized so the lowest-resolution mip used at runtime still has at least one valid texel of gutter.
- Track all UV transforms and source rectangles.
- Avoid packing high-frequency normal maps beside unrelated content without sufficient gutters.
- Preserve alpha coverage for masked materials (compute alpha-test coverage before and after; reject the merge if coverage deviates beyond profile threshold).
- Allow per-material resolution scaling by importance and screen coverage.
- Bin-packing must be deterministic: use a canonical sort key (material ID then descending area) and a deterministic packer (MaxRects with stable tie-breaking).

Atlas output:

- Generated `XRTexture2D` or texture-array assets.
- `MaterialAtlasManifest`.
- Remapped UVs or per-material atlas transform data.
- Updated material table rows.
- Debug preview texture with island labels.

### 5.4 Failure and fallback

If atlas generation cannot safely merge a material, the optimizer keeps that material separate and records the reason. Useful reasons include:

- incompatible render state
- unsupported texture transform
- UVs outside allowed wrap policy
- animated material parameter
- insufficient atlas space
- unacceptable alpha coverage error

## 6. Mesh And Submesh Consolidation

The optimizer should merge mesh sections when it reduces renderer cost without harming culling or animation:

Allowed:

- Merge submeshes with the same final material and skeleton.
- Merge accessory meshes that share skeleton and transform hierarchy.
- Merge tiny static decorative submeshes into a parent if culling loss is negligible.
- Reorder triangles by material and vertex-cache optimization.

Rejected:

- Meshes with incompatible skeletons unless a skeleton merge/remap is explicitly planned.
- Meshes with different blendshape sets when blendshape preservation is required.
- Meshes whose merge would greatly expand bounds and reduce culling.
- Transparent meshes where draw order would change incorrectly.

Output:

- Consolidated `XRMesh`.
- Updated submesh table.
- Original-to-optimized vertex and triangle remap.
- Material ID remap.
- Bone palette remap.
- Blendshape delta remap.

### 6.1 Special-case sections: hair, eyes, mouth, eyelashes

Generic consolidation rules break on a few standard avatar regions. These need explicit handling:

- **Hair**: typically authored as 10–40 layered transparent cards. This is the single most expensive part of most user avatars due to overdraw, alpha-to-coverage variants, depth-prepass interaction, and per-strand sort cost. The optimizer must:
  - Detect hair via material name patterns (`hair`, `Hair`, `_hair`), shader (alpha-blended or alpha-test threshold), and topology (long thin quads).
  - Offer alpha-to-coverage consolidation (alpha blend → masked) where the asset tolerates it.
  - Offer hair-card decimation: merge cards that overlap heavily in UV-projected screen space.
  - At lower LODs, allow hair-shell impostor (octahedral or fronto-parallel card) replacement.
  - Never merge hair into the body atlas without explicit user consent (sort behavior changes).
- **Eyes (cornea, sclera, iris)**: must stay separated when the material uses refraction, parallax, or normal-mapped iris. Optimizer never auto-merges eye submeshes; can downgrade lower-LOD eyes to a single flat material.
- **Inner mouth, teeth, tongue, eyelashes**: typically hidden 95% of the time. Optimizer should add a runtime cull flag (`HiddenWhenMouthClosed`, `HiddenAtDistance`) and a permanent removal at LOD2+. The cull predicate keys off the active viseme/blendshape state.
- **Accessories (glasses, earrings, hats, weapons)**: should be detectable as separate root-bone subtrees with their own material set; they're prime candidates for full LOD-tier removal.

## 7. Geometry Simplification

### 7.1 General simplification

The primary simplifier should use a constrained error metric:

- Geometric position error.
- Normal error.
- UV stretch and seam preservation.
- Material boundary preservation.
- Hard-edge preservation.
- Skinning deformation error over sampled animations.
- Blendshape deformation error.
- Silhouette error from representative cameras.

Named algorithms (use these, do not reinvent):

- Geometry-only error: Garland–Heckbert Quadric Error Metric (QEM, 1997). `meshoptimizer` is the production-grade open implementation.
- Appearance-preserving simplification (UVs, normals, colors as additional quadric dimensions): Hoppe's appearance-preserving QEM extension.
- Skinning-aware simplification: Mohr & Gleicher deformation-sensitive decimation — the quadric is summed across N representative skinned poses, not bind pose only. This is the gap that `meshoptimizer` alone does not close for avatars.

The avatar simplifier composes all three: a per-edge composite cost is the weighted sum of Hoppe-extended QEM + Mohr-Gleicher deformation term + blendshape-delta-gradient term + material-border penalty. Weights live in the profile.

### 7.2 Edge loop removal

Edge-loop removal is a separate, artist-friendly operation because many avatars have dense loops in flat or near-cylindrical regions.

Pipeline:

1. Build half-edge topology with vertex, edge, face, loop/corner, UV, normal, material, skin, and blendshape attributes.
2. Detect loop candidates:
   - closed loops
   - open edge rings
   - roughly parallel adjacent loop pairs
   - quad-dominant strips
3. Reject protected loops:
   - boundaries
   - UV seams
   - hard normal edges
   - material borders
   - silhouette-critical loops
   - eyelids, lips, fingers, joints, or named protected regions
   - high skin-weight gradient
   - high blendshape delta gradient
4. Estimate removal error:
   - angle between adjacent loop normals
   - dihedral angle change
   - curvature change
   - screen-space projected error
   - UV stretch
   - skinning error across sampled poses
   - blendshape error across active shapes
5. Remove the loop if all errors are inside profile thresholds.
6. Interpolate or reconstruct:
   - positions
   - normals and tangents
   - UVs
   - vertex colors
   - skin weights
   - blendshape deltas
7. Recalculate affected tangents and bounds.
8. Validate manifoldness and attribute consistency.

Angle-based rule:

```text
candidate is removable when:
    adjacent_loop_normal_angle <= profile.MaxLoopNormalAngle
and max_dihedral_change <= profile.MaxDihedralChange
and projected_screen_error <= profile.MaxScreenSpaceErrorPixels
and skinning_error <= profile.MaxSkinningErrorMeters
and blendshape_error <= profile.MaxBlendshapeErrorMeters
```

The angle test is necessary but not sufficient. Flat loops around a wrist or mouth may pass normal-angle checks while breaking deformation. Skin and blendshape gradients must participate in the decision.

Algorithm:

- Build a priority queue of edge-collapse candidates keyed by composite error (geometric quadric + skin error sampled across N poses + blendshape delta gradient + material-border penalty).
- Pop the cheapest collapse, apply it, mark all incident edges dirty.
- Use lazy invalidation: re-evaluate the cost on pop, skip if the stored cost is stale, otherwise execute. This avoids O(n log n) decrease-key per collapse.
- Continue until the queue is empty, the profile budget is met, or every remaining candidate exceeds the error threshold.
- After bulk collapse, run a cleanup pass: re-derive tangents (MikkTSpace), recompute bounds, validate manifoldness.

### 7.3 LOD-specific simplification

LOD0 should be conservative. Lower LODs can:

- Remove more loops.
- Reduce or remove blendshapes.
- Limit skin influences further.
- Merge accessories.
- Use smaller atlases.
- Use fewer bones.
- Switch transparent details to masked or baked texture detail if approved.
- Generate meshlets from the simplified result.

## 8. Skin Weight And Skeleton Optimization

### 8.1 Weight pruning

Operations:

- Remove weights below threshold.
- Keep top N influences per vertex.
- Renormalize weights.
- Quantize weights to 8-bit or 16-bit based on profile.
- Pack bone indices to the smallest safe format.
- Build per-mesh bone palettes.
- Remove unused bone references.

Default targets:

| Quality | Max influences | Weight precision |
| --- | ---: | --- |
| LOD0 high quality | 4 | 16-bit unorm, or three 10-bit weights plus implicit fourth |
| LOD1 | 4 or 3 | 8-bit unorm with vertex-shader renormalization |
| LOD2 | 2 | 8-bit unorm with vertex-shader renormalization |
| Crowd/mobile | 1 or 2 | 8-bit unorm, single influence may skip weight upload |

8-bit weight quantization without shader renormalization can produce visible error on high-deformation joints (shoulders, hips). The runtime skinning shader must rescale so per-vertex weights sum to 1.0 even after quantization.

Validation:

- Sample representative animations.
- Compare skinned vertex positions before and after pruning.
- Report max and average error.
- Reject or locally relax pruning near joints where error exceeds threshold.

The sample-animation set must include at minimum: bind pose, T-pose, A-pose, locomotion cycle (walk + run + jump apex), extreme reach (arms overhead, behind back), extreme crouch / sit, and facial range-of-motion sweep. Sampling only bind pose is the most common cause of "the avatar looked fine in the editor and broke in gameplay." The sample set is part of the profile and is itself versioned.

### 8.2 Bone palette optimization

Per mesh or per section:

- Build compact bone palettes.
- Remap bone indices.
- Split sections only if palette limits require it.
- Prefer preserving merged sections if splitting would increase draw calls more than the palette compression helps.

### 8.3 Skeleton pruning

Safe pruning:

- Remove bones with no vertex weights, no animation channels, no socket role, and no child dependency.
- Collapse helper bones only with explicit profile permission.
- Preserve named bones used by gameplay, attachments, IK, facial rigs, or scripts.

Protected-bone discovery is not just animation-channel scanning. The optimizer must traverse all runtime references to the skeleton before pruning:

- Bones referenced by any `PhysicsChainComponent` (per repo memory: spring/jiggle chains).
- Bones referenced by VRM spring-bone definitions (`VRMC_springBone`).
- Bones referenced by IK / look-at / aim constraints.
- Bones referenced by socket / attachment components (hand-held items, holster sockets, name-tag anchors).
- Bones referenced by gameplay scripts via string name lookup.
- **Twist / roll bones** (forearm twist, thigh twist): these drive deformation through helper constraints, often have zero direct weight at bind, and silently destroy elbow/knee deformation if pruned.
- **Humanoid mapping bones** (per repo `humanoid-audit.md`): the full Mecanim/Humanoid bone set must remain addressable by name.
- VRM/VRChat first-person mesh-flag layers: bones tagged as first-person-only or third-person-only.

Skeleton optimization must publish a mapping from original bone path to optimized bone path so animation, attachments, and gameplay references can survive. Lookups by bone name post-optimization fall through this remap table.

## 9. Blendshape Optimization

Blendshapes can dominate memory and compute even when they are visually small.

Operations:

- Remove blendshapes not referenced by animation, viseme, expression, or runtime controls.
- Drop deltas below a threshold.
- Store sparse delta lists instead of full vertex arrays.
- Quantize deltas by profile (16-bit signed for face, 8-bit signed for body/clothing acceptable at LOD1+).
- Merge near-duplicate shapes with explicit approval.
- Generate reduced-delta versions for lower LODs.
- Disable blendshape compute dispatch when all active weights are zero.
- Keep facial/viseme shapes at higher quality than body/clothing shapes by default.
- Apply PCA basis compression for large facial sets: decompose the N x vertex-delta matrix into a K-dimensional basis and per-shape K-coefficient vectors. Vertex shader reconstructs the per-frame delta from the active blendshape weights, summed against the basis. K, memory reduction, and reconstruction error are profile outputs, not constants; the optimizer must report them and preserve protected shapes when error exceeds the profile threshold.
- Eyelid, viseme, and lip-sync shapes must remain non-PCA at LOD0 (reconstruction error on the eyelid edge is visible during blink); apply PCA to brow/cheek/jaw shapes instead.

Protected blendshape allowlists (preserved unless user opts in):

- ARKit `ARFaceAnchor` blendshape names (`eyeBlink_L`, `jawOpen`, `mouthSmile_L`, etc.) for any face that uses ARKit-style tracking.
- VRM `BlendShapeClip` standard names (`Joy`, `Angry`, `Sorrow`, `Fun`, `A`, `I`, `U`, `E`, `O`, `Blink`, `Blink_L`, `Blink_R`, `LookUp`, `LookDown`, `LookLeft`, `LookRight`, `Neutral`).
- VRChat viseme set (`vrc.v_sil`, `vrc.v_pp`, `vrc.v_ff`, etc.) for VRChat-style avatars.
- Any blendshape referenced by an active `AnimationClip` channel.
- Any blendshape referenced by a runtime script via name.

Validation:

- Compare representative expression poses.
- Compute max position error, average position error, and normal error.
- Keep named protected blendshapes intact unless the user opts in.

## 10. Texture Optimization

Texture optimization is both a memory and render-thread problem.

Operations:

- Downscale textures by profile and measured screen coverage.
- Generate atlases or arrays for compatible materials.
- Convert compatible channels into packed textures.
- Generate mipmaps and streaming metadata.
- Convert to engine-preferred compression formats.
- Precompute texture residency hints.
- Split hero-face textures from clothing/accessory textures when needed.

Rules:

- Preserve color space.
- Preserve normal map encoding (DirectX +Y down vs OpenGL +Y up — the importer captures this; the optimizer must not silently change it).
- Preserve alpha coverage for masked materials.
- Use padding sufficient for mips and block compression (4-pixel for BC1–7).
- Prefer texture arrays when UVs rely on wrap modes that atlases cannot preserve.
- Prefer atlases when draw/material consolidation is the priority.
- After any UV remap (atlasing, simplification), regenerate tangent space with MikkTSpace to match the engine's runtime tangent convention. Mixing import tangents with regenerated tangents causes normal-map flipping on seams.

Default compression format selection:

| Texture role | Default format | Notes |
| --- | --- | --- |
| Albedo opaque | BC7 sRGB | BC1 sRGB only at LOD2+ or strict mobile profile |
| Albedo with alpha mask | BC7 sRGB | BC3 sRGB acceptable fallback |
| Albedo with alpha blend | BC7 sRGB | BC3 fallback; preserve alpha coverage |
| Normal | BC5 (RG) | Z reconstructed in shader; never BC1 |
| ORM packed | BC7 linear | BC1 linear at LOD2+ |
| Single-channel mask | BC4 | |
| HDR emissive / lightmap | BC6H | RGB9E5 only if BC6H unavailable |
| UI / icons | BC7 or uncompressed | per-asset opt-out from optimizer |

Integration:

- Generated textures must enter the texture streaming system with cooked metadata.
- The profiler must report optimized texture memory and upload savings.

## 11. LOD Generation

The optimizer should generate LODs as first-class assets, not as runtime guesses.

Each LOD contains:

- Mesh geometry.
- Material bindings.
- Texture atlas or array references.
- Skin weights and bone palette.
- Optional blendshape set.
- Bounds.
- Meshlets if supported.
- Error metric.
- Transition distance or screen-height threshold.

LOD policy:

- LOD0 preserves identity and close-up quality.
- LOD1 reduces material/triangle/texture cost while preserving animation.
- LOD2 and lower prioritize draw count, influence count, and texture memory.
- Crowd LODs may use octahedral impostors (Brucks / Lozar): 8×8 or 12×12 view samples baked into an octahedral atlas, sampled at runtime by view direction. Bake step lives in the optimizer pipeline.
- The preferred very-far LOD is a Gaussian splat representation (see §17) when the bake and runtime path are available; octahedral impostors remain the deterministic fallback.
- VR profiles use conservative transition distances to avoid stereo popping (per-eye view direction can differ; transition by head position not eye).
- For instanced identical avatars (NPC crowds with same skeleton), generate a GPU-instancing-ready bone palette layout: fixed stride, palette indexed by `gl_InstanceID`, allowing one indirect draw to render N copies with N skeletons.

## 12. Optimizer Pipeline

### 12.1 High-level pipeline

```text
Source model asset
    -> import
    -> AvatarAnalyzer
    -> AvatarOptimizationPlan
    -> user preview and budget adjustment
    -> staged optimization operations
    -> validation and report
    -> cooked optimized model variant
    -> GPUScene/material/texture/meshlet cache registration
```

### 12.2 Operation graph

Operations should form a dependency graph:

```text
Analyze
    -> material compatibility
    -> texture atlas planning
    -> mesh/submesh merge planning
    -> skin/blendshape analysis
    -> simplification and loop-removal planning
    -> LOD planning
    -> execute and validate
```

Order matters:

1. Analyze source.
2. Consolidate identical materials.
3. Plan texture atlases/arrays.
4. Remap materials and merge eligible submeshes.
5. Simplify geometry and remove loops.
6. Optimize skin weights and bone palettes.
7. Optimize blendshapes.
8. Generate LODs.
9. Generate meshlets.
10. Cook renderer-ready buffers.

Some operations need feedback loops. For example, material consolidation can enable submesh merging, and submesh merging can change simplification boundaries.

### 12.3 Data model

```csharp
public sealed class AvatarOptimizationAsset
{
    public Guid SourceAssetId { get; init; }
    public string SourceHash { get; init; }
    public AvatarOptimizationProfile Profile { get; init; }
    public AvatarOptimizationReport Report { get; init; }
    public IReadOnlyList<OptimizedAvatarLod> Lods { get; init; }
    public IReadOnlyList<GeneratedTextureAssetRef> Textures { get; init; }
    public IReadOnlyList<GeneratedMaterialAssetRef> Materials { get; init; }
    public AvatarRemapTables Remaps { get; init; }
}

public sealed class AvatarRemapTables
{
    public VertexRemap OriginalToOptimizedVertices { get; init; }
    public MaterialRemap Materials { get; init; }
    public BoneRemap Bones { get; init; }
    public BlendshapeRemap Blendshapes { get; init; }
}
```

The remap tables are not optional. They are needed for debugging, animation binding, editor selection, morph targets, attachments, and future re-optimization.

## 13. Validation

Every optimized variant must pass validation before it can become the default runtime variant.

Validation checks:

- Mesh is manifold enough for the selected operations.
- No missing materials or textures.
- No invalid UVs after atlas remap.
- No NaN or invalid tangent frames.
- Skin weights are normalized.
- Bone indices resolve through the optimized palette.
- Blendshape deltas point to valid vertices.
- Bounds contain animated and blendshape-extreme poses.
- Max geometric error is inside profile budget.
- Max normal error is inside profile budget.
- Max skinning error is inside profile budget.
- Protected named bones and blendshapes are preserved.
- Renderer draw count and material count meet or explain missed targets.

Visual validation:

- Render before/after thumbnails from fixed camera set.
- Render error heatmap.
- Render skinning error heatmap from sampled animation poses.
- Render atlas preview.
- Render LOD transition preview.

Perceptual accept/reject gate:

A geometric error number can pass while the result looks obviously broken (sub-millimeter geometric error can still flip a normal map on a seam). The validation gate requires a perceptual metric:

- Primary: NVIDIA **FLIP** (perceptual difference designed for rendered A/B images), computed on the fixed-camera thumbnail set, against both static pose and N sampled animation poses.
- Secondary: SSIM for tracking trend, LPIPS optional when an ML model is loaded.
- Profile defines a max FLIP threshold per LOD tier. Variants exceeding the threshold are flagged in the report and require explicit user accept.

Determinism contract:

Reports and asset hashes must be deterministic given the same inputs. Operations that have RNG or thread-order sensitivity must be pinned:

- Rectangle bin-packing: canonical sort + deterministic packer (§5.3).
- QEM tie-breaking: canonical vertex ID ordering.
- meshoptimizer: single-threaded mode for the cooking step.
- PCA basis: deterministic SVD seed (Jacobi or randomized with fixed seed).
- Multi-threaded analysis stages may use parallel reduction only when the reduction operator is associative and commutative.

## 14. Runtime Integration

Optimized avatars must be normal engine assets.

Integration points:

- Asset import writes source metrics and optimizer hints.
- Cooked model cache stores optimized variants and LOD payloads.
- Texture management owns generated atlases and streaming metadata.
- Material system owns generated consolidated materials.
- GPUScene registers optimized buffers and material table rows.
- Meshlet generation runs from optimized LODs.
- Animation system consumes bone and blendshape remaps.
- Editor inspector surfaces active source or optimized variant.

Runtime selection:

- Project default profile.
- Per-platform profile.
- Per-asset override.
- Runtime quality setting.
- Editor preview override.

The engine should be able to load the source avatar for editing and the optimized avatar for play/runtime without confusing asset identity.

## 15. Renderer Benefits From Avatar Optimization

Expected improvements:

- Fewer draw calls from material/submesh consolidation.
- Fewer shader variants from material feature pruning.
- Fewer texture binds or descriptor rows from atlases/arrays.
- Lower texture residency and upload pressure.
- Lower vertex and triangle cost.
- Lower skinning upload and compute cost from weight pruning and bone palettes.
- Lower blendshape memory and compute cost.
- Better zero-readback behavior because active material bucket count falls.
- Better meshlet path behavior because simplified LODs and meshlets are cooked offline.

For the observed avatar, the first targets should be:

1. Reduce 62 material slots through compatibility grouping and atlasing.
2. Merge submeshes after material consolidation.
3. Generate LODs and meshlets from the optimized mesh.
4. Limit skin influences and build global/per-mesh bone palettes.
5. Disable or compress inactive blendshapes.
6. Prewarm all shader variants needed by the optimized materials.


## 16. Cluster-Virtualized Avatar Pipeline (Nanite-class for skinned characters)

### 16.1 Why a dedicated path

The avatar optimizer (§2–15) handles assets that can be made cheap. Some avatars cannot: hero player characters, brought-from-outside user content with complex materials, scanned humans with millions of triangles, costume-customization rigs with combinatorial material variants. The engine must accept these without forcing the user to manually decimate, while still hitting the XR whole-frame budget.

The traditional LOD chain (LOD0…LODn) breaks for these because:

- LOD transitions pop visibly under VR head motion.
- Material count does not reduce linearly with LOD index (the user wants the same outfit at every distance).
- Discrete LODs cannot adapt per-cluster: an avatar's face at arm's length needs LOD0 while the same avatar's feet need LOD3.
- LOD0 itself can exceed budget for a single avatar in mirror view.

Unreal's Nanite addresses this with a streaming cluster DAG, screen-space-error per-cluster LOD selection, specialized rasterization for tiny clusters, and GPU-driven material/visibility-buffer style shading. Current Unreal documentation lists Nanite Skeletal Mesh support, but also documents limits that matter for XRENGINE's target use case, including unsupported morph targets, unsupported VR stereo rendering, and opaque/masked material constraints. XRENGINE needs the same broad architectural pattern, but built for VR stereo, user-authored avatar customization, skeletal animation, and blendshape-heavy faces from the start.

### 16.2 Cluster DAG construction

Offline, during the avatar optimizer's Phase 5 cook step:

1. Take the LOD0 optimized mesh.
2. Cluster triangles into groups of 64–128 with locality-preserving partitioning (METIS, or meshoptimizer's `meshopt_buildMeshlets`).
3. Group clusters into cluster-groups of ~32 neighbors.
4. Simplify each group as a unit (Mohr-Gleicher QEM, locking shared group boundaries).
5. Re-cluster the simplified result.
6. Repeat until a single root cluster covers the whole mesh.

Result: a directed acyclic graph where each cluster knows its parent group, its screen-space error if rendered instead of its parent, and its children. The DAG is monotone: rendering a parent is always cheaper and lower-quality than rendering its children.

### 16.3 Skinning extension

Each cluster stores:

- Per-vertex bone palette (cluster-local, indices into the avatar's compact palette).
- Per-vertex weights at the cluster's LOD-appropriate influence count.
- Optional per-cluster sparse blendshape deltas.
- Cluster bounding sphere AND deformation bounds (max displacement from bind sphere across the sampled animation set), so culling does not clip animated extremities.

Skinning runs per-cluster as a compute dispatch: input = cluster vertex stream + bone matrix palette + active blendshape weights; output = skinned position + skinned normal + previous-frame skinned position (for motion vectors).

### 16.4 Runtime selection (GPU-driven, zero-readback)

Each frame, on async compute:

1. Cull avatar instances against last-frame Hi-Z (two-phase outer loop matches the renderer Hi-Z design).
2. For each visible instance, traverse the cluster DAG: for each cluster, compute projected screen-space error of using *this* cluster vs *its children*. If parent error is within budget, emit parent; else recurse into children.
3. Compact selected cluster IDs via subgroup prefix sum into an active list.
4. Skin each active cluster (compute, async).
5. Cluster-cull active list against current-frame Hi-Z (phase 2).
6. Raster path:
   - Clusters with screen-space pixel area > T: hardware path via mesh shaders or indexed multi-draw indirect, writing visibility-buffer.
   - Clusters with screen-space pixel area ≤ T: software rasterizer compute dispatch (atomic 64-bit min over packed depth+payload), writing visibility-buffer.
7. Visibility-buffer shading runs as in the renderer visibility-buffer design with per-material tile dispatches.

No CPU readbacks; cluster selection, skinning, culling, raster, and shading are all GPU-resident.

### 16.5 Material customization layer

Deferred rendering trades material expressiveness for performance. The cluster-virtualized path keeps expressiveness via the visibility-buffer material shading pass, which is a general compute kernel and can implement any forward-like shading model. To preserve user customization:

- Each material defines named **customization slots**: tint colors, mask gradient stops, eye iris textures, outfit pattern selectors, decal layers, emissive masks, fabric type, hair color.
- Customization values live in a per-instance customization buffer indexed by instance ID, separate from material constants.
- Shading-pass kernel fetches both material constants (by material ID) and customization values (by instance ID).
- Customization can change every frame without re-cooking.
- Customization slots are declared in the material asset; the optimizer preserves them across consolidation.

This is the answer to "deferred rendering doesn't necessarily let us achieve high material customizability": the engine doesn't G-buffer-encode the customization, it shades it directly per-pixel in the visibility-buffer pass with full access to the customization buffer.

### 16.6 Streaming

The cluster DAG is a streaming asset:

- Root cluster is always resident (covers the whole avatar at minimum quality).
- Deeper clusters stream in as the avatar approaches the camera.
- A residency map tracks which cluster IDs are GPU-resident; selection picks the deepest resident cluster on the requested path, biasing toward parents that are present.
- Disocclusion / sudden camera-cut prefetch is triggered by the cull pass writing requested-but-missing cluster IDs to a feedback buffer, consumed by the streaming system on the next frame.

### 16.7 Cost model and budget

This is the avatar path intended to bound worst-case runtime cost: GPU time should scale primarily with visible pixels, active clusters, material shading cost, and active skinned vertices, rather than directly with source triangle count. Initial 90 Hz VR budget targets:

| Stage | Budget target |
| --- | ---: |
| Cluster cull + select + compact | 0.3 ms |
| Cluster skinning | 0.5 ms per ~50K active vertices |
| Cluster raster (visibility-buffer write) | 1.0 ms for typical close-up avatar |
| Material tile shading | 1.0 ms |
| Total avatar cost | ~3 ms per close hero avatar |

These are targets, not commitments; the profiler must report actual close-up hero-avatar cost per stereo frame and per eye-dependent pass.

### 16.8 Fallback

The cluster-virtualized path requires `KHR_shader_subgroup` (or DX12 wave ops) and indirect-count drawing. The optional software rasterizer path additionally needs 64-bit image atomics for packed depth+payload (`VK_KHR_shader_atomic_int64`, `GL_NV_shader_atomic_int64`) or a different payload/depth encoding. Hardware without those features falls back to the hardware-raster cluster path or the traditional optimized-LOD chain from §11.

## 17. Gaussian-Splat Distant-Crowd LOD

### 17.1 Problem statement

A populated VR social space may have dozens of unique avatars visible at once. At distance, each individual avatar covers a small screen area but the user must still see *who they are* — swapping in a generic mannequin playing a generic walk cycle defeats the social use case. Traditional triangle LODs cannot reach the per-avatar budget needed (≪ 0.5 ms each) while preserving identity.

### 17.2 Approach

Distant avatars are rendered as 3D Gaussian splats baked from the user's own optimized avatar. 3DGS (Kerbl et al. 2023) represents an object as a few thousand to a few hundred thousand anisotropic Gaussians with view-dependent color (spherical harmonics), composited front-to-back with alpha blending. Rendering is bounded by splat count, not triangle count; sorting and compositing are well-suited to compute.

### 17.3 Bake pipeline

During the avatar optimizer's Phase 5 cook step (or on first encounter at runtime if not pre-cooked):

1. Render the optimized avatar from N viewpoints (concentric ring, typically 64–128 views) at multiple representative poses sampled from the animation set.
2. Train or fit a 3DGS representation using the rendered views as supervision. Training time is hardware- and quality-dependent; first implementation should treat this as an offline cook step and record bake duration in the report rather than promising runtime generation.
3. Optionally apply Mip-Splatting anti-aliasing to control low-LOD aliasing.
4. Compress: prune low-opacity / low-contribution Gaussians, quantize SH coefficients, pack to a binary blob.
5. Store as a cooked asset alongside the avatar's triangle LODs.

### 17.4 Animation

Static splats are insufficient — distant avatars still move. Two options, in order of fidelity:

- **Skeleton-bound splats** (HUGS / SplattingAvatar-style pattern): each Gaussian is bound to one or more bones or to barycentric coordinates on the bind-pose triangle that produced it during baking. At runtime, the Gaussian's position follows the skinned bind primitive and its orientation follows the local frame. Skinning is a compute pre-pass over the splat list. This preserves identity within the sampled and validated pose envelope; extreme unseen poses can still artifact.
- **Pose-conditioned splats** (research path, optional): a small MLP predicts per-Gaussian delta from pose; higher quality on cloth and hair but expensive to bake. Treat as a future enhancement.

Blendshapes are not supported at this distance — facial expression is sub-pixel.

### 17.5 Runtime rendering

Per frame, on async compute:

1. Cull splat-avatar instances against frustum + far Hi-Z.
2. Skin active splat lists (per-Gaussian skinning compute).
3. Sort visible splats by camera-space depth using a GPU radix/merge sort or a tile-local approximate order. The exact sort strategy and timing are implementation results, not a design assumption.
4. Rasterize back-to-front via tile-based compute composite into the splat framebuffer (color + depth + velocity).
5. Composite splat framebuffer into the main framebuffer with depth-test against scene depth and alpha-blend.

Motion vectors come from current-skinned splat position vs previous-skinned splat position.

### 17.6 LOD transition

The splat representation is the avatar's lowest LOD on the chain. Transition between splats and the deepest triangle LOD must be artifact-free:

- Cross-fade over N frames based on instance distance (alpha blend triangle render and splat render in the same composite).
- Depth continuity: splat depth must match triangle depth within tolerance at the transition distance (achieved by sampling triangle depth during splat bake).
- Motion-vector continuity: both representations write velocity to the same buffer, so the upscaler sees one consistent object.
- VR-specific: the transition distance is per-instance, evaluated at the head position (not per-eye), to avoid one eye seeing splats while the other sees triangles.

### 17.7 Identity preservation

This is the key requirement vs traditional crowd impostors:

- The splat representation is baked from *this user's avatar*, not from a template.
- Outfit, hair color, accessories, body proportions, and skin tone are present in the bake.
- Customization slots (§16.5) modulate splat color in the composite shader, so runtime tint changes (changing shirt color, etc.) still apply at splat distance.
- The bake is invalidated and re-baked when significant customization changes occur.

### 17.8 Cost model

Initial target: a distant 50-avatar crowd should fit inside about 1 ms of the 90 Hz stereo frame on target desktop hardware after foveation/VRS, culling, and splat-count pruning. This is an engineering target that must be validated on hardware, not a literature guarantee.

| Stage | Budget target (full crowd) |
| --- | ---: |
| Per-instance frustum cull | measured |
| Splat skinning (all visible) | measured |
| Splat sort / binning | measured |
| Tile composite | measured |
| Total | target ~1 ms |

Splat counts per avatar are bounded by bake-time pruning to ~30K–80K visible at the target distance band; closer-than-bake-distance instances are promoted to the triangle / cluster pipeline.

### 17.9 Fallback

Hardware or profiles without a viable splat bake, GPU sort/binning, or composite path fall back to octahedral impostors (§11). Identity preservation is reduced (octahedral impostors only encode N baked views, not arbitrary novel views) but visible identity from typical distances is preserved.

## 18. Implementation Phases

### Phase 0: Instrumentation And Asset Budgets

- Add stable counters for material slots, active materials, submeshes, draw calls, texture memory, influences, blendshapes, shader variants, meshlets, cluster payloads, and splat payloads per model.
- Add profiler rows that correlate model assets with render cost.
- Define project, platform, and runtime avatar budget profiles.
- Wire deterministic report hashing so optimizer changes invalidate generated variants intentionally.

### Phase 1: Analyzer And Report

- Implement `AvatarAnalyzer`.
- Produce deterministic reports.
- Add editor panel read-only analysis view.
- Add warnings for material fan-out, texture memory, skin influences, and blendshape cost.

### Phase 2: Safe Material And Texture Consolidation

- Merge identical materials.
- Generate texture atlases for opaque compatible groups.
- Remap UVs.
- Merge submeshes with identical final material and compatible skeleton.
- Validate before/after render thumbnails.

### Phase 3: Skin And Blendshape Optimization

- Prune weights to profile limits.
- Build bone palettes and remaps.
- Compress sparse blendshape deltas.
- Disable zero-weight blendshape compute dispatch.
- Validate sampled animation deformation error.

### Phase 4: Geometry Simplification And Edge-Loop Removal

- Build half-edge topology.
- Add constrained simplification.
- Add edge-loop candidate detection and protected-region rejection.
- Add before/after error heatmaps.

### Phase 5: LOD, Meshlet, And Cooked Variant Publishing

- Generate LODs from optimized base.
- Generate meshlets for each LOD.
- Store optimized variants in model import binary cache.
- Register optimized material, texture, meshlet, and remap payloads with GPUScene and the asset system.

### Phase 6: Runtime Variant Selection

- Make optimized variants selectable by platform/runtime profile.
- Make zero-readback active-list generation benefit directly from reduced material slots.
- Add automated regression scenes with optimized and unoptimized avatars.
- Add Release performance targets for avatar scenes.

### Phase 7: Visibility-Buffer Hero-Avatar Path

- Implement depth/visibility prepass writing `{InstanceID, ClusterID, TriIndex}`.
- Implement material tile classification and per-material compute shading.
- Implement skinned-attribute analytical interpolation from barycentrics plus skinned vertex buffer.
- Validate against forward and deferred reference paths.

### Phase 8: Cluster-Virtualized Avatar Pipeline

- Cluster builder integrated with optimizer Phase 5.
- Streaming cluster cache.
- GPU cluster selection by screen-space error, per-cluster two-phase Hi-Z, software rasterizer for tiny clusters, and hardware path for larger clusters.
- Per-cluster skinning compute.
- Material-customization slot system layered on visibility-buffer shading.

### Phase 9: Gaussian-Splat Distant-Crowd LOD

- Avatar-to-splat baker over rotated views and sampled poses.
- Animated splat path with skeleton-bound or pose-conditioned splat displacement.
- Splat sort and composite render pass on async compute where supported.
- Cross-fade between splat LOD and triangle LOD with depth and motion-vector continuity.

## 19. Acceptance Criteria

- The editor can analyze an imported avatar and report material, draw, texture, geometry, skin, and blendshape cost.
- The editor can generate an optimized copy without modifying the source asset.
- The optimized avatar preserves animation and protected facial/body features within the selected profile error budget, with FLIP perceptual gating.
- Material consolidation and texture atlasing reduce draw/material/texture cost where compatible.
- Skin-weight and blendshape optimization reduce upload, compute, and memory cost.
- Generated LODs are valid, selectable, and cooked.
- Reports are deterministic and stored with generated variants.
- Any user-imported avatar within the engine asset-budget envelope can be displayed at close-up quality with bounded GPU cost after cooking; source triangle count should not linearly determine runtime cost, but material and shader complexity still participate in the budget.
- Per-cluster occlusion and screen-space-error LOD reduce sub-pixel triangle waste.
- Material customization slots remain editable at runtime without re-cooking.
- A populated social space with 50+ unique avatars has a measured distant-avatar budget and degrades gracefully by distance, splat count, and impostor fallback when the 90 Hz whole-frame budget would be missed.
- Each splat avatar retains its individual appearance rather than falling back to a generic placeholder.
- Cross-fade to triangle LOD is artifact-free under VR head motion.

## 20. Risks

- Automatic optimization can damage character identity if error metrics are too geometric and not semantic enough.
- Material consolidation can silently alter appearance if render-state compatibility is too permissive.
- Atlas generation can break wrap modes, mip behavior, alpha coverage, or color-space handling.
- Skin-weight pruning can look correct in bind pose and fail in animation.
- Edge-loop removal can damage deformation regions even when normal angles are small.
- Cluster-virtualized avatar rendering carries significant implementation risk: analytical attribute interpolation from skinned barycentrics is non-trivial, and software-rasterizer correctness vs hardware path requires regression coverage.
- Gaussian-splat distant LOD risks lighting-mismatch artifacts: splats are baked under fixed lighting and cross-fading to a relit triangle LOD can pop.
- Pose-conditioned animated splats are still an active research area; expect quality regressions on extreme poses outside the training set.
- VR reprojection can mask missed avatar budgets until users report discomfort.

Mitigation:

- Keep source assets immutable.
- Make optimized variants explicit and reversible.
- Keep operation reports and remap tables.
- Use conservative defaults.
- Validate with animation samples and fixed camera thumbnails plus FLIP perceptual gate.
- Keep renderer counters tied to asset IDs.
- Gate cluster-virtualized and splat paths behind feature flags with reference fallback to the traditional LOD chain.
- Bench against the full XR frame budget and explicitly report the stereo path, not just desktop mono frame time.

## 21. Design Decisions

- Avatar optimization is part of the asset pipeline, not an external manual workflow.
- Optimized avatars are generated variants with deterministic reports and source remap tables.
- Edge-loop removal must consider skin and blendshape gradients, not only adjacent normal angle.
- Meshlets, cluster-virtualized avatars, and Gaussian-splat impostors build on optimized cooked assets; they do not replace asset optimization.
- Users may bring avatars within the engine asset-budget envelope; over-budget avatars are routed through optimizer, cluster-virtualized, or distant-LOD fallback paths rather than being rejected by default.
- Visibility-buffer rendering is the preferred path for material-diverse avatars and dense opaque content; deferred and forward are retained for material classes incompatible with visibility-buffer reconstruction.
- Distant avatars in crowded social scenes are rendered as Gaussian splats baked from the avatar's own appearance, not as generic NPCs.

