# GPU Skinning Buffer Compression Completion

Last Updated: 2026-05-29
Current Status: implemented on branch `gpu-skinning-buffer-compression`
Scope: implementation of
[GPU Skinning Buffer Compression Plan](../../../design/rendering/gpu/gpu-skinning-buffer-compression-plan.md).

## Completed Outcome

The skinning hot path now uses one compact influence contract and one final
skin-palette contract across compute skinning, direct vertex skinning, cooked
mesh payloads, and GPU-driven palette producers.

Implemented changes:

- `XRMesh` builds `Core4 + Spill` influence buffers for skinned meshes.
- Meshes choose `Core4x8` when utilized bones fit in 255 entries and
  `Core4x16` when they fit in 65535 entries.
- Vertices always carry four core lanes and one spill header; only vertices
  with more than four retained influences append spill entries.
- Weights are packed as `UNorm8` and normalized so retained weights sum to
  exactly 255 per non-empty vertex.
- Cooked mesh payloads were versioned for the compressed skinning layout.
- Compute and direct vertex skinning decode the same compact buffer layout.
- Renderer skinning now publishes precomposed affine skin palettes as three
  `vec4` rows per bone.
- `GlobalAnimationInputBuffers` was replaced by `GlobalSkinPaletteBuffers`.
- GPU physics-chain palette publication writes final skin palettes directly.
- Legacy fixed-vs-variable runtime skinning settings and buffer names were
  removed from the active API surface.

## Final Influence Buffer Contract

Mesh-owned buffers:

- `BoneInfluenceCoreIndices`: four sentinel-based bone lanes per vertex.
  - `Core4x8`: four `u8` lanes.
  - `Core4x16`: four `u16` lanes.
  - `0` means no bone; live bones are stored as `boneIndex + 1`.
- `BoneInfluenceCoreWeights`: four `UNorm8` lanes per vertex.
- `BoneInfluenceSpillHeaders`: one `uint` per vertex.
  - bits `0..23`: spill entry offset.
  - bits `24..31`: extra influence count.
- `BoneInfluenceSpillEntries`: one `uint` per extra influence.
  - bits `0..15`: `boneIndexPlusOne`.
  - bits `16..23`: `weightUNorm8`.
  - bits `24..31`: reserved, zero.

Packing rules:

- Sort source influences by descending weight with ascending bone-index
  tiebreaks.
- Drop zero and negative weights before packing.
- Drop tail influences that quantize to zero.
- Absorb quantization residue into the largest retained influence.
- Encode zero-influence vertices as zero core indices, zero core weights, and a
  zero spill header.

## Final Palette Contract

Renderer-owned and global palette buffers now expose final skin matrices, not
paired animated-world and inverse-bind `mat4` streams.

- `ActiveSkinPaletteBuffer` is the current renderer palette.
- `ActivePreviousSkinPaletteBuffer` remains optional for temporal consumers.
- `ActiveSkinPaletteBase` and `ActiveSkinPaletteCount` describe slices in
  shared palette storage.
- `GlobalSkinPaletteBuffers` packs renderer palettes into a global skin-palette
  stream when requested by `UseGlobalSkinPaletteBufferForComputeSkinning`.
- Each bone record is three independent `vec4` rows, represented by
  `SkinPaletteMatrix`.
- CPU-driven renderers compose the final palette once when source bone matrices
  are dirty.
- GPU-driven sources can publish final affine palette rows directly.

## Memory Contract

The final cost formulas are:

| Data | Old cost | New cost |
|------|----------|----------|
| Fixed four-influence mesh vertex | 32 B/vertex | `12 B/vertex` for `Core4x8`, `16 B/vertex` for `Core4x16` |
| Legacy variable mesh vertex | `8 + 8n B/vertex` | `12 + 4e B/vertex` for `Core4x8`, `16 + 4e B/vertex` for `Core4x16` |
| Renderer skin palette | 128 B/bone | 48 B/bone |

Where `n` is the old total influence count and `e = max(0, retainedInfluences -
4)` is the compact spill count.

## Validation Performed

Passed:

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
dotnet build .\XRENGINE\XRENGINE.csproj
dotnet build .\XREngine.UnitTests\XREngine.UnitTests.csproj
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~GpuSkinningBufferCompressionTests --no-build
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~UberShaderForwardContractTests --no-build -- NUnit.NumberOfTestWorkers=1
```

Additional targeted skinning runs reached unrelated pre-existing failures:

- `NativeFbxImporterTests.*` fail in the Assimp FBX fallback with
  `FBX-DOM no FBXHeaderExtension dictionary found`.
- `XRMeshRendererTests.UpdateIndirectDrawBuffer_WritesCommandsPerSubmesh`
  fails because the test mesh has a null `BVHTree`.
- A broader filtered run also exposed
  `PrefabModelSerializationTests.MaterialYaml_UnchangedShaderSource_UsesBackingPathInsteadOfInliningText`,
  where the expected shader source path is absolute but the actual YAML uses a
  relative shader path.

## Remaining Runtime Validation

This Codex pass could not capture GPU visual diffs or frame-time measurements
from a representative editor scene. Those should be captured during PR/runtime
review for:

- one core-only skinned character,
- one spill-heavy skinned character,
- one GPU physics-chain-driven palette source,
- compute skinning dispatch time,
- direct vertex skinning frame time,
- global palette copy volume.

## Finalization Notes

- The dedicated implementation branch exists: `gpu-skinning-buffer-compression`.
- The branch has not been merged back into `main`.
- Legacy cooked skinning payloads are intentionally rejected by versioning and
  should be recooked from source assets.
- No dependency, launch flag, task, or generated workflow changes were required.
