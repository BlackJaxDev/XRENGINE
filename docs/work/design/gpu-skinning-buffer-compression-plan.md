# GPU Skinning Buffer Compression Plan

Last Updated: 2026-04-20
Status: design
Scope: renderer-level refactor of XRMesh and XRMeshRenderer skinning influence and palette buffers across direct vertex skinning, compute skinning, OpenGL, Vulkan, and cooked mesh payloads.

Related docs:

- [GPU-Driven Rendering Pipeline TODO](../todo/gpu-rendering.md)
- [Zero-Readback GPU-Driven Rendering Plan](zero-readback-gpu-driven-rendering-plan.md)
- [GPU Physics Chain Zero-Readback Skinned Mesh Plan](gpu-physics-chain-zero-readback-skinned-mesh-plan.md)
- [OpenGL Renderer](../../architecture/rendering/opengl-renderer.md)
- [Vulkan Renderer](../../architecture/rendering/vulkan-renderer.md)
- [Rendering Code Map](../../architecture/rendering/RenderingCodeMap.md)

---

## 1. Executive Summary

XRENGINE's current GPU skinning data model is functional but materially over-allocates memory and bandwidth in two places:

1. mesh-owned bone influence buffers,
2. renderer-owned bone palette buffers.

The influence side currently chooses one of two mutually exclusive layouts per mesh:

- a fixed 4-weight layout that consumes 32 bytes per vertex,
- a variable-length layout that consumes 8 bytes per vertex plus 8 bytes per influence.

That design has a cliff: one vertex with more than 4 influences forces the entire mesh onto the variable path.

The palette side currently uploads two full `mat4` streams per bone:

- animated bone world matrices,
- inverse bind matrices pre-adjusted into root-local space.

That costs 128 bytes per bone before any global repacking and repeats a matrix multiplication inside every skinning evaluation.

This plan recommends a two-layer refactor:

1. Replace the mesh-wide fixed-vs-variable influence split with a unified **Core4 + Spill** encoding that supports both 4-max-bones weighting and unbounded-bones weighting in one format.
2. Replace the renderer's dual-`mat4` palette contract with a single precomposed **affine skin matrix palette**.

Recommended target outcomes:

- dominant meshes with `<= 4` influences per vertex drop from 32 bytes per vertex to 12 or 16 bytes per vertex depending on palette size,
- overflow vertices pay only for extra influences instead of penalizing the whole mesh,
- renderer bone palette bandwidth drops from 128 bytes per bone to 48 bytes per bone,
- compute skinning and direct vertex skinning consume one logical decode contract,
- GPU-produced animation sources fit the same palette abstraction instead of special-casing CPU-owned `BoneMatricesBuffer`.

This document is intentionally design-oriented rather than implementation-oriented. It defines the target formats, API surface, migration order, validation plan, and rejection criteria for alternative encodings.

---

## 2. Current State

### 2.1 Relevant Code

| Area | Files |
|------|-------|
| Mesh skinning buffer construction | `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.Skinning.cs` |
| Mesh core state / cached references | `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.Core.cs` |
| Cooked binary payload for skinning buffers | `XREngine.Runtime.Rendering/Objects/Meshes/XRMesh.CookedBinary.cs` |
| Renderer-owned bone palette buffers | `XRENGINE/Rendering/XRMeshRenderer.cs` |
| Compute skinning dispatch and bindings | `XRENGINE/Rendering/Compute/SkinningPrepassDispatcher.cs` |
| Global packed animation inputs | `XRENGINE/Rendering/Compute/GlobalAnimationInputBuffers.cs` |
| Direct vertex shader generation | `XRENGINE/Rendering/Generator/DefaultVertexShaderGenerator.cs` |
| Compute skinning shader | `Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepass.comp` |
| Interleaved compute skinning shader | `Build/CommonAssets/Shaders/Compute/Animation/SkinningPrepassInterleaved.comp` |
| Buffer storage / format capabilities | `XREngine.Runtime.Rendering/Buffers/XRDataBuffer.cs`, `XREngine.Data/Rendering/Enums/EComponentType.cs` |

### 2.2 Current Mesh-Owned Influence Encoding

`XRMesh.Skinning.cs` currently picks a single layout per mesh based on:

- `OptimizeSkinningTo4Weights`,
- `OptimizeSkinningWeightsIfPossible`,
- `MaxWeightCount`.

The current outcomes are:

#### A. Fixed 4-weight path

CPU build path:

- `PopulateSkinningBuffers(...)`
- `PopulateOptWeightsParallel(...)`

Buffers:

- `BoneWeightOffsets`: 4 x 32-bit indices per vertex
- `BoneWeightCounts`: 4 x 32-bit float weights per vertex

Runtime interpretation:

- direct vertex skinning treats these as `ivec4` / `vec4` vertex attributes,
- compute skinning reads them as raw `uint[]` and reconstructs `ivec4` / `vec4`.

Current cost:

- `4 * 4 = 16` bytes of indices per vertex,
- `4 * 4 = 16` bytes of weights per vertex,
- total: **32 bytes per vertex**.

#### B. Variable-length path

CPU build path:

- `PopulateSkinningBuffers(...)`
- `PopulateUnoptWeightsParallel(...)`

Buffers:

- `BoneWeightOffsets`: 1 x 32-bit offset per vertex
- `BoneWeightCounts`: 1 x 32-bit count per vertex
- `BoneWeightIndices`: contiguous 32-bit list entries
- `BoneWeightValues`: contiguous 32-bit float list entries

Current cost:

- `4 + 4 = 8` bytes per vertex for offset/count,
- `4 + 4 = 8` bytes per influence in the spill lists,
- total: **8 + 8n bytes per vertex**, where `n` is the number of influences.

#### C. Structural weakness

The current design uses `MaxWeightCount` as a mesh-wide format switch. That means:

- a mesh where 99.9% of vertices have `<= 4` influences still falls back to the variable path if one vertex has 5 influences,
- compute and direct paths both need two decode branches,
- the engine keeps old naming (`BoneMatrixOffset`, `BoneMatrixCount`) even when those buffers mean either four indices/four weights or one offset/one count.

### 2.3 Current Renderer-Owned Palette Encoding

`XRMeshRenderer` currently creates:

- `BoneMatricesBuffer`
- `BoneInvBindMatricesBuffer`

Both are `mat4` SSBO streams with a reserved identity element at index 0.

Current semantics:

- mesh influence data stores `boneIndex + 1`, where `0` means no bone,
- the shader reconstructs the skinning transform per influence as `BoneInvBindMatrices[idx] * BoneMatrices[idx]`,
- the current palette count is `UtilizedBones.Length + 1`.

Current cost:

- `mat4` world matrix: 64 bytes per bone,
- `mat4` inverse-bind matrix: 64 bytes per bone,
- total: **128 bytes per bone**.

This cost appears in:

- per-renderer CPU-visible buffers,
- global packed animation buffers when compute skinning uses global slices,
- shader bandwidth on every skinning evaluation.

### 2.4 Current Shader Contract

The current shader contract is split along the same mesh-wide boundary:

- `applySkinningOptimized4(...)` for the fixed path,
- `applySkinningVariable(...)` for the variable path.

This exists in both:

- `SkinningPrepass.comp`,
- `SkinningPrepassInterleaved.comp`,
- generated direct vertex skinning in `DefaultVertexShaderGenerator.cs`.

Additional complexity:

- integer-like skinning metadata can still be stored as floats under `UseIntegerUniformsInShaders == false`,
- compute shaders contain decode helpers for integer-vs-float metadata,
- direct vertex skinning still depends on mesh format-specific input declarations.

Given XRENGINE's current baseline targets, that compatibility path is no longer a good trade.

### 2.5 Current Cooked Mesh Contract

`XRMesh.CookedBinary.cs` currently persists:

- `MaxWeightCount`,
- `BoneWeightOffsets`,
- `BoneWeightCounts`,
- `BoneWeightIndices`,
- `BoneWeightValues`,
- `UtilizedBones`.

That means the skinning refactor is not only a runtime change. It affects:

- cooked payload format,
- cache invalidation behavior,
- any tooling that inspects these buffers directly.

---

## 3. Problem Statement

The engine's current GPU skinning buffers are overbuilt for the actual data they carry.

### 3.1 The mesh-wide format cliff wastes memory on the common case

Most practical skinned meshes are dominated by vertices with 1 to 4 influences. The current `MaxWeightCount` switch turns a local outlier into a whole-mesh penalty.

Examples:

| Vertex influence count | Current fixed path | Current variable path |
|---|---:|---:|
| 1 | 32 B | 16 B |
| 4 | 32 B | 40 B |
| 5 | N/A | 48 B |
| 8 | N/A | 72 B |

For a mesh with only a small spill tail above 4, the current design is worse than necessary in both directions:

- fixed path wastes space on the majority of vertices,
- variable path wastes space on all vertices because it abandons the compact dominant case.

### 3.2 The influence buffers still assume 32-bit storage everywhere

The engine currently uses 32-bit values for:

- indices in the fixed path,
- offsets and counts in the variable path,
- bone list indices,
- weights.

That is not justified for the common case.

Observations:

- the current palette already reserves `0` as a sentinel and stores `boneIndex + 1`,
- most skinned meshes will fit in 255 or 65535 utilized bones,
- fixed-path weights are normalized and already rounded aggressively before upload,
- spill weights do not need IEEE float precision to remain useful.

### 3.3 The palette contract duplicates both data and work

The shader currently performs the same conceptual operation repeatedly:

1. load inverse bind matrix,
2. load current world matrix,
3. multiply them,
4. apply the result to a position/normal/tangent.

That means:

- two matrix streams are uploaded,
- the same multiply is repeated per influence instead of per dirty bone,
- global packing duplicates both streams again.

### 3.4 The current settings expose an implementation detail rather than a real product choice

`OptimizeSkinningTo4Weights` and `OptimizeSkinningWeightsIfPossible` control a storage distinction rather than a meaningful user-facing rendering mode.

That is a sign the buffer model is not clean enough. A better format should support both 4-max-bones weighting and unbounded-bones weighting without forcing a mesh-wide storage mode toggle.

---

## 4. Goals And Non-Goals

### 4.1 Goals

- Support both 4-max-bones weighting and unbounded-bones weighting in one coherent data model.
- Reduce mesh skinning buffer memory and bandwidth materially on both compute and direct paths.
- Remove the mesh-wide `MaxWeightCount` format cliff.
- Keep the dominant `<= 4` influence case compact.
- Preserve deterministic CPU-side packing during import, rebuild, and cook.
- Give compute and direct vertex skinning one logical decode contract.
- Reduce renderer palette upload bandwidth and repeated shader work.
- Preserve or improve compatibility with GPU-generated animation sources and palette base offsets.
- Keep hot paths allocation-free during runtime dispatch.

### 4.2 Non-Goals For Initial Delivery

- No attempt to support more than 65535 utilized bones in the compressed path. Extremely large skeletons may use a fallback path.
- No attempt to compress skinned output buffers (`SkinnedPositionsBuffer`, `SkinnedNormalsBuffer`, `SkinnedTangentsBuffer`).
- No attempt to redesign blendshape buffer layouts in this refactor.
- No attempt to solve skinned bounds readback in the same change, though the new design must not block that work.
- No attempt to preserve legacy cooked mesh compatibility indefinitely. The repository is pre-ship, so a clean recook is acceptable.

### 4.3 Design Invariants

The target design must preserve these behaviors:

- `0` remains the no-bone sentinel.
- Weights remain normalized per vertex after packing.
- Both compute and direct skinning continue to support `boneMatrixBase` or its replacement.
- Unused influence lanes continue to decode to `(bone=0, weight=0)`.
- Vertex rebuilds from raw `Vertices[i].Weights` remain deterministic.

---

## 5. Proposed Architecture

### 5.1 Split The Refactor Into Two Independent Compression Layers

The current implementation mixes two separate concerns:

1. how a mesh stores vertex-to-bone influences,
2. how a renderer stores the dynamic palette used by those influences.

The refactor should treat them independently:

- **Layer A: influence encoding** lives on `XRMesh` and is static or cook-time generated.
- **Layer B: palette encoding** lives on `XRMeshRenderer` or another animation source and is dynamic.

That separation keeps the migration understandable and makes it possible to ship the influence refactor before the palette refactor if needed.

### 5.2 Unified Influence Format: Core4 + Spill

The recommended influence model is:

- every vertex always stores four compact "core" influences,
- any influences beyond the first four spill into a compact auxiliary list,
- the vertex stores one compact header describing where its spill tail begins and how many extra influences it has.

This replaces the current fixed-path/variable-path split with one representation that still treats the dominant 4-lane case efficiently.

### 5.3 Core Format Variants

The design should support two core index variants:

| Variant | Condition | Core indices | Core weights | Spill header | Base bytes / vertex |
|---|---|---|---|---|---:|
| `Core4x8` | `UtilizedBones.Length <= 255` | 4 x u8 | 4 x UNorm8 | 1 x u32 | 12 |
| `Core4x16` | `256 <= UtilizedBones.Length <= 65535` | 4 x u16 | 4 x UNorm8 | 1 x u32 | 16 |

The core weights remain `UNorm8` in both variants.

Rationale:

- fixed-4 weights are already normalized and aggressively cleaned during build,
- 8-bit normalized weights are usually adequate for the dominant-case lanes,
- keeping weights constant while changing index width avoids an unnecessary explosion of format combinations.

The spill list uses a separate format described below.

### 5.4 Spill Format

The spill contract is:

- vertices with `<= 4` influences store a zero spill header,
- vertices with `> 4` influences store the first four influences in core lanes and the remainder in the spill list.

Recommended spill header layout:

```text
uint SpillHeader
bits  0..23 : spillOffset
bits 24..31 : extraInfluenceCount
```

Recommended spill entry layout:

```text
uint SpillEntry
bits  0..15 : boneIndexPlusOne
bits 16..31 : weightUNorm16
```

Notes:

- `boneIndexPlusOne` preserves the existing sentinel contract.
- `weightUNorm16` is chosen instead of `UNorm8` because the spill tail is where very small weights are more likely to appear.
- 24 bits of offset permits over 16 million spill entries in one mesh payload, which is far beyond expected usage.

### 5.5 Storage Cost Comparison

Under the proposed design, vertex cost becomes:

- `Core4x8`: `12 + 4e` bytes per vertex,
- `Core4x16`: `16 + 4e` bytes per vertex,

where `e = max(0, influenceCount - 4)`.

Representative comparisons:

| Influences / vertex | Current fixed | Current variable | Proposed `Core4x8` | Proposed `Core4x16` |
|---|---:|---:|---:|---:|
| 1 | 32 | 16 | 12 | 16 |
| 4 | 32 | 40 | 12 | 16 |
| 5 | N/A | 48 | 16 | 20 |
| 8 | N/A | 72 | 28 | 32 |

This is the main expected win from the refactor.

### 5.6 Why Not Store Only CSR-Style Offset/Count Lists?

A pure offset/count + dense-list representation can also be compressed by using:

- `u16` or `u24` offsets,
- `u8` counts,
- packed list entries.

That is simpler than the hybrid design but still throws away the fact that the overwhelming majority of vertices fit inside four lanes. It also preserves the per-vertex list indirection even when no spill exists.

This design explicitly rejects that option as the primary direction.

### 5.7 Why Not Derive The Fourth Weight?

An even smaller core format could store only three explicit weights and reconstruct the fourth as:

```text
w3 = max(0, 1 - (w0 + w1 + w2))
```

That looks attractive on paper but complicates mixed-count vertices and interacts poorly with sentinel lanes and quantized zero-weight cleanup.

This design treats derived-last-weight packing as a future optional micro-optimization, not the first implementation target.

---

## 6. Detailed Influence Buffer Contract

### 6.1 Recommended Logical Buffers

The current names `BoneMatrixOffset` and `BoneMatrixCount` become misleading once the mesh no longer toggles between two unrelated meanings.

Recommended replacement names:

- `BoneInfluenceCoreIndices`
- `BoneInfluenceCoreWeights`
- `BoneInfluenceSpillHeaders`
- `BoneInfluenceSpillEntries`

If the engine wants to minimize churn during migration, aliases can be maintained temporarily in `ECommonBufferType`, but the target state should use explicit names.

### 6.2 Build Rules

When rebuilding skinning buffers from imported or author-authored vertex weights:

1. Normalize the full weight set.
2. Sort influences by descending weight.
3. Write the first four into core lanes.
4. Write any remaining influences into the spill list.
5. Renormalize using the final packed weights if exact packed-sum consistency is required for validation.

Important detail:

- sorting by descending weight must become explicit and deterministic.

The current code iterates dictionaries after normalization. That is sufficient for the current loose formats but not ideal for a tightly packed binary contract where lane ordering should be stable.

### 6.3 Shader Decode Rules

All skinning shaders should conceptually perform:

1. decode four core lanes,
2. apply any non-zero core influences,
3. decode spill header,
4. loop spill entries if `extraInfluenceCount > 0`.

That gives one decode contract for:

- compute skinning,
- interleaved compute skinning,
- direct vertex skinning.

The current `optimized4` branch becomes unnecessary as a mesh-storage concept.

### 6.4 Direct Vertex Path Contract

For the direct vertex path:

- core indices should be exposed as integer vertex attributes using `u8` or `u16`,
- core weights should be exposed as normalized `u8` vertex attributes,
- spill headers and spill entries should be available through SSBO bindings when a mesh has any overflow influences.

That means the direct path continues to benefit from cheap fixed-function attribute fetch for the dominant case while still supporting unbounded-bones weighting through the spill path.

### 6.5 Compute Path Contract

For compute skinning:

- core lanes should be read from packed `uint` words,
- spill headers and spill entries should be read from SSBOs,
- integer-like skinning metadata should no longer support float-backed decode.

This is the right point to remove skinning-specific dependence on `UseIntegerUniformsInShaders`.

### 6.6 Overflow Header Allocation Strategy

The recommended first implementation stores one 32-bit spill header per vertex even if the vertex has no spill.

This is not the minimum theoretical memory cost, but it is the best first trade:

- O(1) overflow access in both compute and direct paths,
- no per-vertex lookup structure or bitset indirection,
- simple cooking and runtime decode,
- still much smaller than the current layouts.

A later optimization may replace the universal header with a sparse overflow table if profiling proves it matters.

---

## 7. Palette Compression Plan

### 7.1 Replace Dual `mat4` Inputs With A Single Precomposed Skin Palette

The current palette contract stores:

- current world matrix,
- static inverse bind matrix.

The proposed contract stores:

- current final skin matrix,
- optional previous final skin matrix when temporal rendering requires it.

For CPU-driven bones, the renderer should compose the final skin matrix once per dirty bone on the CPU when uploading dirty state.

For GPU-driven sources, the producer should write final skin matrices directly.

### 7.2 Affine Matrix Layout

Recommended skin matrix layout per bone:

- three `vec4` columns encoding the affine transform needed by row-vector skinning.

This costs:

- `3 * 16 = 48` bytes per bone.

Compared to the current 128-byte dual-`mat4` palette, that is a **62.5% reduction** before any secondary effects.

### 7.3 Why Affine 3x4 Is Enough

Skinning only needs:

- transformed position,
- transformed normal / tangent basis.

That means the shader needs:

- the 3x3 linear part,
- the translation terms.

It does not need a full general 4x4 projective matrix. The current use of `mat4` is a convenient storage choice, not a real data requirement.

### 7.4 New Renderer Palette Abstraction

The renderer-side contract should move toward:

- `ActiveSkinPaletteBuffer`
- `ActivePreviousSkinPaletteBuffer` optional
- `ActiveSkinPaletteBase`
- `ActiveSkinPaletteCount`

This is more future-proof than exposing raw `BoneMatricesBuffer` and `BoneInvBindMatricesBuffer` as the primary abstraction.

It also aligns with the zero-readback GPU animation work where the actual palette source may not be CPU-owned.

### 7.5 Global Packing Changes

`GlobalAnimationInputBuffers` should eventually become a skin-palette packer rather than a pair-of-`mat4` packers.

Recommended target direction:

- replace global bone world + global inverse bind streams with one global final-skin palette stream,
- optionally maintain a previous-frame companion stream only when temporal paths require it,
- keep palette base semantics unchanged.

This reduces:

- global packing memory,
- CPU-side copy volume,
- shader fetch count per influence.

---

## 8. API And Type Changes

### 8.1 XRMesh

Add explicit format metadata for cooked and runtime mesh skinning payloads.

Recommended metadata:

- `SkinningInfluenceEncoding`
- `SkinningCoreIndexFormat`
- `HasSpillInfluences`
- `MaxSpillInfluenceCount`

Recommended internal state changes:

- replace `MaxWeightCount` as the primary runtime layout selector,
- retain `MaxWeightCount` only as descriptive metadata for inspection/debugging if still useful,
- store stable sorted influence order before packing.

### 8.2 XRMeshRenderer

Recommended runtime surface:

- renderer exposes a unified skin palette abstraction rather than separate raw source buffers,
- dirty bone uploads write final skin matrices directly in the CPU-driven path,
- `BoneInvBindMatricesBuffer` stops being a hot-path runtime requirement once the palette refactor lands.

### 8.3 Shader Generator

`DefaultVertexShaderGenerator` should evolve from:

- fixed4 vs variable input declaration,

to:

- one direct core-lane declaration,
- optional spill SSBO declaration,
- one shared decode sequence.

### 8.4 Rendering Settings

The following settings should be reevaluated:

- `OptimizeSkinningTo4Weights`
- `OptimizeSkinningWeightsIfPossible`
- `UseIntegerUniformsInShaders`

Recommended direction:

- keep legacy toggles only as short-lived migration fallbacks,
- move the new influence packing format to a deterministic engine default,
- stop using float-backed integer skinning metadata in the new path.

---

## 9. Cooked Asset And Serialization Plan

### 9.1 Preferred Migration Strategy

Because the repository is pre-ship, the preferred migration is:

1. introduce a new cooked skinning payload version,
2. invalidate and recook existing cooked meshes,
3. remove the legacy runtime skinning buffer layout after validation.

This is cleaner than carrying long-lived dual decode paths for old cooked assets.

### 9.2 Payload Changes

Cooked payloads should store:

- new influence encoding metadata,
- core indices buffer plan,
- core weights buffer plan,
- spill header buffer plan,
- spill entry buffer plan,
- bone utilization metadata.

The legacy fields:

- `Offsets`,
- `Counts`,
- `Indices`,
- `Values`,

should be replaced or versioned accordingly.

### 9.3 Import-Time Rebuild Policy

When a mesh still has source vertex weights available, rebuilding the compressed payload from source data is preferable to format-to-format translation.

If a direct cooked-to-cooked migration tool is later needed, it should translate through a canonical logical influence list rather than relying on implicit meaning from legacy buffer names.

---

## 10. Recommended Implementation Order

### Phase 0: Measurement And Guard Rails

- Add instrumentation to report bytes per vertex for current skinning buffers.
- Add unit tests that compare CPU logical weights against packed decode results.
- Add deterministic influence ordering before any packing changes.

### Phase 1: Mesh Influence Refactor

- Implement `Core4x8` and `Core4x16` packing.
- Introduce spill headers and spill entries.
- Update compute skinning shaders to decode the new format.
- Keep legacy direct vertex skinning behind a temporary fallback if needed.

### Phase 2: Direct Vertex Path Migration

- Update `DefaultVertexShaderGenerator` to declare compact core attributes.
- Bind spill buffers for overflow-capable meshes.
- Remove the mesh-wide fixed-vs-variable skinning branch.

### Phase 3: Palette Compression

- Add final skin palette buffers to `XRMeshRenderer`.
- Compose dirty bone matrices directly into the new format.
- Update compute and direct paths to consume the new palette abstraction.
- Refactor global palette packing.

### Phase 4: Cleanup And Removal

- Remove legacy `BoneInvBindMatricesBuffer` hot-path usage.
- Remove float-backed skinning integer decode.
- Remove or repurpose obsolete engine settings.
- Regenerate cooked assets and remove legacy payload support.

---

## 11. Validation Plan

### 11.1 Unit Tests

Add targeted tests in `XREngine.UnitTests/Rendering` for:

- deterministic packing order,
- correct decode for `Core4x8` with 0 to 4 influences,
- correct decode for spill vertices with 5+ influences,
- weight sum preservation after quantization,
- sentinel handling for unused lanes,
- equivalence between legacy logical weights and new decode results within tolerance.

### 11.2 Shader Contract Tests

Add or extend source-generation tests that assert:

- direct vertex shaders declare the new compact attributes,
- direct vertex shaders bind spill buffers when required,
- compute shaders no longer branch on the old mesh-wide storage modes,
- skinning-related shader code no longer depends on float-backed integer decode.

### 11.3 Runtime Validation

Validate representative content buckets:

- rigid-ish characters with 1 to 2 influences,
- typical character rigs with 4 influences,
- dense facial or cloth-like rigs with 5+ influences,
- GPU-driven bone sources such as physics-chain-driven skinning.

### 11.4 Performance Validation

Measure at minimum:

- mesh skinning buffer bytes per vertex,
- palette bytes per bone,
- compute skinning dispatch time,
- direct vertex path frame time on skinned scenes,
- global palette copy volume,
- shader instruction impact from spill decode.

Expected outcome:

- reduced upload size,
- reduced SSBO fetch bandwidth,
- no visible regression in skin deformation quality on typical assets.

---

## 12. Risks And Open Questions

### 12.1 Quantization Artifacts

Risk:

- `UNorm8` core weights may be too coarse for some facial or high-density rigs.

Mitigation:

- validate on demanding assets early,
- keep `Core4x16Indices + UNorm8Weights` as the baseline,
- reserve an emergency fallback to `UNorm16` core weights if real content disproves the assumption.

### 12.2 Backend Binding Details

Risk:

- the engine may need cleaner explicit support for buffers consumed both as vertex attributes and SSBOs.

Mitigation:

- treat this as a backend binding task, not a reason to keep the legacy format,
- allow temporary duplicate GPU views if necessary during migration.

### 12.3 Spill Loop Cost

Risk:

- a unified decode path adds one spill-header read even for non-overflow vertices.

Mitigation:

- the cost is small relative to the bandwidth saved,
- profile after Phase 1 before considering a sparse-header optimization.

### 12.4 Very Large Skeletons

Risk:

- some exotic assets may exceed 65535 utilized bones.

Mitigation:

- explicitly fall back to a legacy or widened path for that rare case,
- do not let that edge case dictate the common-case format.

### 12.5 Interaction With Future GPU Animation Sources

Risk:

- the influence refactor and palette refactor could drift apart if implemented independently.

Mitigation:

- keep the final target abstraction explicit: mesh-owned compact influences plus renderer-owned final skin palette.

---

## 13. Rejected Or Deferred Alternatives

### 13.1 Keep The Current Two Layouts And Only Narrow Scalar Types

Rejected because it preserves:

- the mesh-wide format cliff,
- duplicate shader decode paths,
- settings that expose a storage detail instead of a real feature.

### 13.2 Pure CSR / Offset-Count Encoding For Everything

Rejected because it throws away the dominant 4-lane case and pays list indirection for every vertex.

### 13.3 Derived Fourth Weight In Phase 1

Deferred because it complicates mixed-count vertex semantics and is not required to get most of the benefit.

### 13.4 Half-Float Dual-`mat4` Palettes

Rejected as the primary palette strategy because it still keeps two palette streams and preserves per-influence matrix composition in shader. A single precomposed affine palette is a cleaner win.

---

## 14. Recommended Direction

If the refactor is executed in only one pass, the recommended target state is:

1. Mesh influences use a unified `Core4 + Spill` format.
2. Core weights use `UNorm8`.
3. Spill entries use `u16 boneIndexPlusOne + UNorm16 weight`.
4. Meshes choose only the core **index width** (`u8` or `u16`), not a completely different layout.
5. Shaders always decode four core lanes, then optional spill.
6. Renderers publish final affine skin matrices instead of separate world and inverse-bind matrices.
7. Global packed animation buffers become packed skin palettes.

That design is substantially smaller, simpler, and more compatible with the engine's broader zero-readback rendering direction than the current format split.
