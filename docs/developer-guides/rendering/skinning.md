# Skinning

XREngine skinning uses a compact `Core4 + Spill` influence layout and affine
skin-palette rows for both direct vertex rendering and compute skinning. The
runtime goal is to reduce mesh influence bandwidth, palette upload cost, and
redundant compute dispatches while preserving authored deformation quality.

Open implementation work is tracked in
[Skinning GPU Efficiency Follow-Ups TODO](../../work/todo/rendering/gpu/skinning-gpu-efficiency-followups-todo.md).
Longer-horizon ideas live in
[Skinning Deferred GPU Efficiency Design](../../work/design/rendering/gpu/skinning-deferred-gpu-efficiency-design.md).

## Runtime Contract

- Mesh influences use `BoneInfluenceCoreIndices`,
  `BoneInfluenceCoreWeights`, and optional `BoneInfluenceSpillHeaders` /
  `BoneInfluenceSpillEntries`.
- Core-only meshes use `SkinningInfluenceEncoding.Core4NoSpill` and omit spill
  buffers entirely.
- Spill-capable meshes use `SkinningInfluenceEncoding.Core4Spill` and preserve
  the overflow path for vertices with more than four influences.
- `Core4x8` costs 8 bytes per vertex when no spill is present and 12 bytes per
  vertex when spill is present.
- `Core4x16` costs 12 bytes per vertex when no spill is present and 16 bytes
  per vertex when spill is present.
- Skin palettes are final affine `SkinPaletteMatrix` rows at 48 bytes per bone.
- Compute skinning and direct vertex skinning consume the same logical
  influence and palette contracts.
- Core4 is the canonical runtime skinning format. Skinned meshes without valid
  `Core4Spill` or `Core4NoSpill` buffers are rebuilt from source vertex weights
  when possible; stale cooked payloads are rejected so source assets or caches
  are recooked instead of silently falling back to a legacy path.

## Payloads And Shader Variants

- Cooked skinning payloads use version `3`; stale cooked meshes under `Cache/`
  and `Build/Cache/` should be recooked.
- Invalid cooked skinned meshes fail validation with an explicit recook or
  reimport message rather than rendering through direct vertex skinning when
  compute skinning is enabled.
- `SkinningShaderVariant` reserves shader-cache space for skinning layout
  variants.
- OpenGL binary shader cache schema version `3` covers the no-spill and
  influence-cap shader contracts.
- The no-spill variant omits `BoneInfluenceSpillHeaders` and
  `BoneInfluenceSpillEntries`.
- `DefaultVertexShaderGenerator.WriteSkinningCalc` omits the spill loop for
  no-spill meshes, and `WriteUniformBufferBlocks` omits the two spill SSBOs.
- Compute skinning binding validation does not require spill buffers for
  no-spill meshes.
- Spill-capable meshes keep identical output compared with the existing
  Core4+spill path.

## Affine Skin Palettes

- `SkinPaletteMatrix` stores three `vec4` rows per bone.
- `XRMeshRenderer` exposes `ActiveSkinPaletteBuffer`,
  `ActivePreviousSkinPaletteBuffer`, `ActiveSkinPaletteBase`, and
  `ActiveSkinPaletteCount` as the hot-path palette surface.
- `GlobalSkinPaletteBuffers` packs global skin palettes for compute skinning.
- GPU physics-chain palette output writes final skin-palette rows directly.
- FP16 palette storage has not landed. The current implementation keeps
  GPU-driven palettes on FP32 and leaves the runtime hooks needed for later
  opt-in precision work.

## Dispatch Reuse

- CPU-owned compute skinning outputs are reused when renderer inputs are clean.
- `XRMeshRenderer` tracks whether its active skin palette changed since the
  previous compute skinning dispatch.
- `XRMeshRenderer` tracks whether mesh vertex inputs, morph inputs, or skinning
  settings changed since the previous dispatch.
- Invalidation flags use `SetField(...)` so change notification remains
  correct.
- Dirty-tracking and reuse caches are allocated once at renderer initialization.
- Compute skinning is skipped when all inputs are unchanged and the skinned
  output buffers are still valid.
- Previous-frame output consumers and temporal paths such as motion vectors and
  TAA history remain part of the reuse contract.
- Diagnostics explain why a renderer dispatched or reused output.

The invalidation model covers animator pose writes, IK writes, GPU physics
chain writes, blendshape weight changes, mesh rebuilds, LOD swaps, world matrix
changes for non-pretransformed skinning paths, skin palette precision/layout
switches, and shader variant changes.

## Skinning LOD

- Skinning LOD consumes avatar-optimizer outputs through a narrow runtime
  interface instead of adding an editor bone-selection UI.
- The consumer interface reads a `BoneRemap` table and an `InfluenceCap` per
  LOD tier from the avatar profile and applies them at skinning submission
  time.
- Implemented LOD tiers include full palette/full influence data and reduced
  influence count.
- Protected gameplay, attachment, humanoid, and physics-chain bones are
  preserved unless the avatar profile explicitly allows removal.
- Avatar optimization remap tables are consumed when present; the runtime gates
  the remap path behind "remap table present" instead of inventing a local
  fallback.
- No new editor UI for bone selection ships with this runtime phase.

## Palette Dedupe

- The global compute-skinning palette buffer dedupes identical same-mesh
  palette slices within the frame.
- Each renderer can produce a stable hash over affine palette rows per frame.
- Renderers with the same hash and the same source mesh can share a palette
  region in the global palette buffer.
- The dedupe table is allocated once and reused by per-frame hash insertion.
- Crowd scenes with identical idle poses can pay closer to single-renderer
  palette upload cost than N-renderer cost.

## Derived Fourth Weight Evaluation

Derived-fourth-weight packing was evaluated and aborted before implementation.
The current explicit four-weight buffer is already one packed 32-bit UNorm
fetch, and dropping the fourth lane would not reduce the aligned OpenGL/Vulkan
fetch bucket without a broader interleaved vertex layout change.

## Profiler Counters

Profiler packets include:

- core influence bytes,
- spill header bytes,
- spill entry bytes,
- skin palette bytes,
- skipped compute skinning dispatches,
- reused skinned output buffers,
- live skinning shader permutation count.

## Completed Test Coverage

Implemented coverage includes:

- compact skinning unit tests,
- shader contract tests for compute and direct skinning paths,
- core-only mesh decode without spill buffers on direct vertex and compute
  paths,
- shader contract tests proving spill bindings are absent for no-spill variants
  on direct vertex and compute paths,
- no-spill live shader permutation budget coverage,
- `SkinPaletteMatrix` row layout coverage,
- profiler protocol coverage for skinning counters.

## Validation Recorded

The skinning runtime work has recorded the following targeted validation:

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
dotnet build .\XRENGINE.slnx
```

The targeted renderer build completed on 2026-05-29 with 0 warnings and 0
errors.
