# GPU Skinning Buffer Compression Plan

Last Updated: 2026-05-06
Status: implemented on branch `gpu-skinning-buffer-compression` as of 2026-05-29
Scope: renderer-level refactor of XRMesh and XRMeshRenderer skinning influence and palette buffers across direct vertex skinning, compute skinning, OpenGL, Vulkan, and cooked mesh payloads.

Related docs:

- [Production GPU-driven rendering roadmap](../../../todo/rendering/gpu/production-rendering-pipeline-roadmap.md)
- [Zero-Readback GPU-Driven Rendering Plan](zero-readback-gpu-driven-rendering-plan.md)
- [GPU Physics Chain Zero-Readback Skinned Mesh Plan](gpu-physics-chain-zero-readback-skinned-mesh-plan.md)
- [OpenGL Renderer](../../architecture/rendering/opengl-renderer.md)
- [Vulkan Renderer](../../architecture/rendering/vulkan-renderer.md)
- [Rendering Code Map](../../architecture/rendering/RenderingCodeMap.md)

---

## 2026-05-29 Implementation Update

The implementation landed the planned `Core4 + Spill` mesh influence encoding
and the final affine skin-palette contract in one cleanup pass:

- `XRMesh` now owns `BoneInfluenceCoreIndices`,
  `BoneInfluenceCoreWeights`, `BoneInfluenceSpillHeaders`, and
  `BoneInfluenceSpillEntries`.
- `Core4x8` and `Core4x16` replace the old mesh-wide fixed-4 vs variable
  branch. `MaxWeightCount` remains descriptive data, not a layout selector.
- Compute and direct vertex skinning consume the same packed core/spill bit
  layout.
- `XRMeshRenderer` exposes `ActiveSkinPaletteBuffer`,
  `ActivePreviousSkinPaletteBuffer`, `ActiveSkinPaletteBase`, and
  `ActiveSkinPaletteCount` as the hot-path palette surface.
- Skin palettes are precomposed affine matrices stored as three `vec4` rows per
  bone, reducing the active palette cost from 128 B/bone to 48 B/bone.
- `GlobalAnimationInputBuffers` has been replaced by
  `GlobalSkinPaletteBuffers`, and the compute setting is now
  `UseGlobalSkinPaletteBufferForComputeSkinning`.
- GPU physics-chain palette output writes final skin palette rows directly.

The original design text below is retained as architectural rationale. For the
task completion record and validation notes, see
[GPU Skinning Buffer Compression Completion](../../../todo/rendering/gpu/gpu-skinning-buffer-compression-todo.md).

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
- renderer bone palette bandwidth drops from 128 bytes per bone to 48 bytes per bone (62.5% reduction) when no previous-frame palette is required, or to 96 bytes per bone (~25% reduction) when temporal motion vectors require a previous-frame companion stream,
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

### 3.5 Cost Summary

This is the headline motivation for the refactor and is repeated here for visibility. The detailed derivations live in [§5.5](#55-storage-cost-comparison) and [§7.2](#72-affine-matrix-layout).

Mesh influence storage (bytes per vertex):

| Influences / vertex | Current fixed | Current variable | Proposed `Core4x8` | Proposed `Core4x16` |
|---|---:|---:|---:|---:|
| 1 | 32 | 16 | 12 | 16 |
| 4 | 32 | 40 | 12 | 16 |
| 5 | N/A | 48 | 16 | 20 |
| 8 | N/A | 72 | 28 | 32 |

Renderer palette storage (bytes per bone):

| Mode | Current dual-`mat4` | Proposed affine 3x4 |
|---|---:|---:|
| No temporal companion | 128 | 48 (62.5% reduction) |
| With previous-frame companion | 128 | 96 (~25% reduction) |

Proposed `Core4x8` and `Core4x16` rows always include the universal 4-byte spill header; see [§6.6](#66-overflow-header-allocation-strategy) for that trade-off.

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

Because indices store `boneIndex + 1` with `0` reserved, `Core4x8` addresses up to 255 real bones and `Core4x16` addresses up to 65535 real bones; the table conditions are written in those terms.

Byte map per vertex (little-endian, written in this order in cooked payloads and uploaded as such on all platforms):

```text
Core4x8 (12 bytes / vertex):
  bytes  0..3  : core indices  (u8 i0, u8 i1, u8 i2, u8 i3)
  bytes  4..7  : core weights  (UNorm8 w0, w1, w2, w3)
  bytes  8..11 : spill header  (u32, see §5.4)

Core4x16 (16 bytes / vertex):
  bytes  0..7  : core indices  (u16 i0, i1, i2, i3)
  bytes  8..11 : core weights  (UNorm8 w0, w1, w2, w3)
  bytes 12..15 : spill header  (u32, see §5.4)
```

Core attributes are aligned on a 4-byte boundary and the per-vertex stride is the value listed above. The core indices and core weights buffers are exposed both as integer/normalized vertex attributes (direct path) and as raw `uint` SSBO words (compute path); compute consumes them via `unpackUnorm4x8` and equivalent integer unpacks, which assume little-endian byte order matching the layout above.

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
uint SpillHeader (little-endian within the uint)
bits  0..23 : spillOffset           (entry index into BoneInfluenceSpillEntries)
bits 24..31 : extraInfluenceCount   (count of spill entries; 0 ⇒ no spill)
```

Recommended spill entry layout:

```text
uint SpillEntry (little-endian within the uint)
bits  0..15 : boneIndexPlusOne   (u16; 0 = sentinel)
bits 16..23 : weightUNorm8       (matches core weight precision)
bits 24..31 : reserved (must be 0)
```

Notes:

- `boneIndexPlusOne` preserves the existing sentinel contract.
- Spill weights use `UNorm8` to match the core lanes. This keeps a single weight precision across the whole format, allows a shared decode helper, and avoids growing the entry to 6 bytes (and the resulting alignment padding to 8 bytes). Tail influences whose weight rounds below 1/255 are dropped during packing rather than carried at higher precision.
- One spill entry occupies a full 4-byte word for `std430` alignment and to keep simple `uint` indexing on both compute and vertex paths. The reserved high byte is available for a future flag if needed.
- 24 bits of offset permits over 16 million spill entries in one mesh payload, which is far beyond expected usage.

### 5.5 Storage Cost Comparison

Under the proposed design, vertex cost becomes:

- `Core4x8`: `12 + 4e` bytes per vertex,
- `Core4x16`: `16 + 4e` bytes per vertex,

where `e = max(0, influenceCount - 4)`. The `12` and `16` constants both already include the universal 4-byte spill header (see [§6.6](#66-overflow-header-allocation-strategy)); they are not headerless variants.

Representative comparisons (bytes per vertex):

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

The rename is deferred to Phase 4 (Cleanup And Removal); see [§10](#10-recommended-implementation-order). Phases 1–3 keep the existing `ECommonBufferType` names so the influence and palette refactors can land without dragging a workspace-wide rename through every dependent backend, tool, and test. Phase 4 then performs a single clean rename with no aliases retained: callers that referenced the old names are updated in the same change, and the old enum values are removed rather than aliased.

### 6.2 Build Rules

When rebuilding skinning buffers from imported or author-authored vertex weights:

1. Normalize the full weight set.
2. Sort influences by descending weight, breaking ties by ascending bone index for determinism.
3. Drop trailing influences whose weight rounds to zero at the target quantization (`UNorm8` for both core and spill).
4. Write the first four into core lanes.
5. Write any remaining influences into the spill list.
6. Quantize all surviving weights to `UNorm8`.
7. Renormalize on the CPU by absorbing the quantization residual `(255 - Σ quantizedWeights)` into the lane with the largest quantized weight, breaking ties toward the first lane. This is the binding renormalization rule for the engine: shaders do **not** renormalize at decode time, and packed weight sums are guaranteed to equal `255` per vertex (i.e. exactly 1.0 in normalized space) after this step.

Important detail:

- sorting by descending weight (with the bone-index tiebreak above) must become explicit and deterministic.

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

Concrete vertex attribute formats:

| Variant | Indices format | Weights format |
|---|---|---|
| `Core4x8` | `R8G8B8A8_UINT` (GL: `GL_UNSIGNED_BYTE` + `glVertexAttribIPointer`) | `R8G8B8A8_UNORM` |
| `Core4x16` | `R16G16B16A16_UINT` (GL: `GL_UNSIGNED_SHORT` + `glVertexAttribIPointer`) | `R8G8B8A8_UNORM` |

Both formats are core in OpenGL 4.6 and Vulkan 1.0; no extension or feature-bit gating is required. Spill SSBO bindings use `std430` and consume the same `BoneInfluenceSpillHeaders` and `BoneInfluenceSpillEntries` buffers used by the compute path, so backends do not need duplicate GPU views in the steady state. During migration, a temporary duplicate view is acceptable per [§12.2](#122-backend-binding-details).

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

### 6.7 Zero-Influence Vertices

Vertices with zero bone influences (rigid attachments, unweighted geometry) are represented as:

- core indices: `(0, 0, 0, 0)` — all sentinel,
- core weights: `(0, 0, 0, 0)`,
- spill header: `0` — zero offset, zero extra count.

Decode treats sentinel-0 lanes as no-op contributions, so a fully zero-influence vertex emits the input position unchanged. This matches the current behavior and is preserved as a design invariant in [§4.3](#43-design-invariants).

### 6.8 Endianness And Bit Order

All multi-byte fields in influence buffers (core indices `u16`, core weights `UNorm8`, spill headers, spill entries) are written and read in little-endian byte order. This matches:

- the GLSL packed-uint helpers used by compute (`unpackUnorm4x8`, `bitfieldExtract`),
- both supported backends (OpenGL on x64 Windows, Vulkan on x64 Windows),
- the cooked payload format on disk.

No platform in the current support matrix requires byte-swapping at load time. If a future big-endian platform is added, the loader — not the runtime decode — must convert.

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

- three `vec4` rows encoding the affine transform needed for skinning, declared in GLSL as `vec4 skin[3]` (three independent `vec4`s, not a `mat3x4`). This avoids GLSL’s implicit `column_major` packing rules for matrix types under `std430` and keeps the on-disk byte order identical to the in-shader load order.

Each row stores `(linear.row_i.xyz, translation_i)`, so a position is reconstructed as:

```glsl
vec3 transformPosition(vec4 skin[3], vec3 p) {
    return vec3(
        dot(skin[0], vec4(p, 1.0)),
        dot(skin[1], vec4(p, 1.0)),
        dot(skin[2], vec4(p, 1.0)));
}
```

This costs:

- `3 * 16 = 48` bytes per bone.

Compared to the current 128-byte dual-`mat4` palette, that is:

- a **62.5% reduction** when no previous-frame palette is required (current palette: 128 B/bone → proposed: 48 B/bone),
- a **~25% reduction** for temporal paths that retain a previous-frame companion stream (current palette: 128 B/bone → proposed: 96 B/bone for current + previous combined).

The headline number depends on whether the consuming render path needs motion vectors. Both numbers should be cited together when summarizing this change.

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

`GlobalAnimationInputBuffers` should eventually become a skin-palette packer rather than a pair-of-`mat4` packers. The renamed/replacement type should be `GlobalSkinPaletteBuffers` (parallel naming to the `ActiveSkinPaletteBuffer` surface in [§7.4](#74-new-renderer-palette-abstraction)); the rename lands together with the rest of the buffer rename in Phase 4.

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
- retain `MaxWeightCount` as descriptive metadata only — it is no longer consulted by any code path that selects buffer layout, shader variant, or upload format. It remains exposed on `XRMesh` for inspection, telemetry, and import diagnostics. Any new runtime branch that depends on `MaxWeightCount` is a regression and should fail review.
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

1. bump the `XRMesh.CookedBinary` skinning payload version field,
2. invalidate and recook existing cooked meshes,
3. remove the legacy runtime skinning buffer layout after validation.

This is cleaner than carrying long-lived dual decode paths for old cooked assets.

Concrete versioning rule:

- the existing cooked-mesh header carries a payload version integer; the skinning refactor increments that integer by one,
- on load, a mismatch between the on-disk version and the runtime version causes the loader to **reject** the cooked payload and force a reimport from source assets,
- there is no in-place cooked-to-cooked translator; legacy payloads are not silently upgraded.

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

### 9.4 Rollback Strategy

The refactor introduces a `SkinningInfluenceEncoding` enum on `XRMesh` (see [§8.1](#81-xrmesh)). The legacy fixed-4 and variable-length runtime layouts are retained behind the enum value `SkinningInfluenceEncoding.Legacy` through the end of Phase 3, gated by a single engine setting:

- if the new format triggers a content regression that cannot be resolved before Phase 4, the engine can be flipped back to `Legacy` for a release without reverting code,
- Phase 4 removes the `Legacy` enum value and the corresponding code paths,
- after Phase 4, rollback is a code revert rather than a runtime toggle.

The rollback path is explicitly **temporary**. No long-lived dual-encoding support is planned.

---

## 10. Recommended Implementation Order

### Phase 0: Measurement And Guard Rails

- Add instrumentation to report bytes per vertex for current skinning buffers.
- Add unit tests that compare CPU logical weights against packed decode results.
- Add deterministic influence ordering before any packing changes.

### Phase 1: Influence And Shader Refactor (combined)

The original plan split mesh influence work (compute) from direct-path migration into two phases. They are merged here so the engine never runs in a state where compute and direct paths disagree on the influence format.

- Implement `Core4x8` and `Core4x16` packing on `XRMesh`.
- Introduce spill headers and spill entries.
- Update compute skinning shaders (`SkinningPrepass.comp`, `SkinningPrepassInterleaved.comp`) to decode the new format.
- Update `DefaultVertexShaderGenerator` to declare compact core attributes and bind spill SSBOs for overflow-capable meshes.
- Remove the mesh-wide fixed-vs-variable skinning branch from both compute and direct paths in the same change.
- The `SkinningInfluenceEncoding.Legacy` runtime fallback (see [§9.4](#94-rollback-strategy)) provides the only supported escape hatch during this phase.

### Phase 2: Palette Compression

(Renumbered from the original Phase 3.)

- Add final skin palette buffers to `XRMeshRenderer`.
- Compose dirty bone matrices directly into the new format.
- Update compute and direct paths to consume the new palette abstraction.
- Refactor global palette packing (`GlobalAnimationInputBuffers` → `GlobalSkinPaletteBuffers`, target rename in Phase 3 cleanup).

### Phase 3: Cleanup And Removal

(Renumbered from the original Phase 4.)

- Rename mesh influence buffers (`BoneMatrixOffset` → `BoneInfluenceCoreIndices`, etc.; see [§6.1](#61-recommended-logical-buffers)). No `ECommonBufferType` aliases are retained.
- Rename `GlobalAnimationInputBuffers` → `GlobalSkinPaletteBuffers`.
- Remove the `SkinningInfluenceEncoding.Legacy` enum value and the corresponding fallback paths.
- Remove legacy `BoneInvBindMatricesBuffer` hot-path usage.
- Remove float-backed skinning integer decode.
- Remove or repurpose obsolete engine settings (`OptimizeSkinningTo4Weights`, `OptimizeSkinningWeightsIfPossible`, skinning-specific use of `UseIntegerUniformsInShaders`).
- Regenerate cooked assets and remove legacy payload support.

---

## 11. Validation Plan

### 11.1 Unit Tests

Add targeted tests in `XREngine.UnitTests/Rendering` for:

- deterministic packing order (descending weight, ascending bone-index tiebreak),
- correct decode for `Core4x8` with 0 to 4 influences,
- correct decode for spill vertices with 5+ influences,
- weight sum preservation after quantization — packed `Σ weight == 255` exactly per vertex (the absorb-into-largest renormalization rule from [§6.2](#62-build-rules)),
- sentinel handling for unused lanes,
- zero-influence vertex contract from [§6.7](#67-zero-influence-vertices) — input position passes through unchanged,
- equivalence between legacy logical weights and new decode results within tolerance.

Numerical tolerances (binding for these tests):

- per-lane weight delta: `|w_logical − w_decoded| <= 1.5 / 255` (one quantization step plus rounding margin),
- skinned position delta: `|p_legacy − p_new| <= 1e-3 * meshBoundsDiagonal`,
- skinned normal delta: `dot(n_legacy, n_new) >= cos(0.5°)`.

Also add a transition test for the boundary between core-only and spill vertices: a vertex authored with 5 influences whose 5th influence rounds below `1/255` must produce a packed result with `extraInfluenceCount == 0` and pure core lanes — the spill list must not contain a zero-weight tail entry.

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

For each bucket, capture **golden-image visual diffs** of a posed character at a fixed set of animation keyframes (idle T-pose, mid-walk, extreme facial pose if applicable). Diffs should be produced from the unit-testing world via the existing screenshot harness; the acceptance threshold is mean per-channel pixel delta `<= 1` in 8-bit color space and no individual pixel delta `> 4`. Larger deltas are treated as regressions until explicitly waived.

### 11.4 Performance Validation

Measure at minimum:

- mesh skinning buffer bytes per vertex,
- palette bytes per bone,
- compute skinning dispatch time,
- direct vertex path frame time on skinned scenes,
- global palette copy volume,
- shader instruction impact from spill decode.

Binding regression budgets:

- mesh skinning buffer bytes per vertex: **strictly lower** than baseline for every measured asset,
- palette bytes per bone: **strictly lower** than baseline,
- compute skinning dispatch time at equivalent vertex count: within **±5%** of baseline,
- direct vertex path frame time on the skinned regression scene: within **±5%** of baseline,
- global palette copy volume per frame: **strictly lower** than baseline.

A regression that exceeds these budgets blocks Phase advance until either fixed or explicitly waived in the PR with measured numbers.

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

### 13.5 Dual-Quaternion Skinning Palette

Out of scope. Dual-quaternion skinning is orthogonal to influence and palette **storage** compression: the same `Core4 + Spill` influence format would still be applicable, and the palette format change is independent of whether the underlying transform is stored as an affine matrix or a dual-quaternion. Evaluating dual-quaternion skinning as a deformation-quality choice is a separate decision and is not blocked or precluded by anything in this plan.

---

## 14. Recommended Direction

If the refactor is executed in only one pass, the recommended target state is:

1. Mesh influences use a unified `Core4 + Spill` format.
2. Core weights use `UNorm8`.
3. Spill entries use `u16 boneIndexPlusOne + UNorm8 weight + 1 reserved byte`, packed one per `uint`.
4. Meshes choose only the core **index width** (`u8` or `u16`), not a completely different layout.
5. Shaders always decode four core lanes, then optional spill, with no per-vertex renormalization (the packer guarantees `Σ weight == 1.0`).
6. Renderers publish final affine skin matrices (declared as `vec4 skin[3]`, not `mat3x4`) instead of separate world and inverse-bind matrices.
7. Global packed animation buffers become packed skin palettes (`GlobalSkinPaletteBuffers`).

That design is substantially smaller, simpler, and more compatible with the engine's broader zero-readback rendering direction than the current format split.
