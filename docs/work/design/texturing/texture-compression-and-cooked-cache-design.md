# Texture Compression And Cooked Texture Cache Design

Last Updated: 2026-06-04
Status: design proposal
Scope: role-aware GPU texture compression, compressed cooked texture cache payloads, metadata-only generated texture assets, OpenGL/Vulkan upload plumbing, and validation.

Related docs:

- [Texture Runtime, Streaming, And Virtual Texturing Design](texture-runtime-streaming-virtual-texturing-design.md)
- [Texture Runtime, Streaming, And Virtual Texturing TODO](../../todo/texturing/texture-runtime-streaming-virtual-texturing-todo.md)
- [Texture Compression And Cooked Texture Cache TODO](../../todo/texturing/texture-compression-and-cooked-cache-todo.md)
- [Texture Runtime Streaming Validation](../../testing/texture-runtime-streaming-validation.md)
- [Model Import Cooked Asset Cache Design](../assets/model-import-binary-cache-design.md)
- [Model import feature guide](../../../developer-guides/assets/model-import.md)

External references:

- [OpenGL `glCompressedTexImage2D`](https://wikis.khronos.org/opengl/GlCompressedTexImage2D)
- [Vulkan compressed image formats](https://docs.vulkan.org/spec/latest/appendices/compressedtex.html)
- [Vulkan format feature guide](https://docs.vulkan.org/guide/latest/formats.html)
- [KTX 2.0 file format specification](https://registry.khronos.org/KTX/specs/2.0/ktxspec.v2.html)
- [Microsoft Direct3D block compression guide](https://learn.microsoft.com/en-us/windows/win32/direct3d10/d3d10-graphics-programming-guide-resources-block-compression)
- [Arm ASTC Encoder](https://github.com/ARM-software/astc-encoder)
- [Khronos KTX-Software](https://github.com/KhronosGroup/KTX-Software)
- [Microsoft DirectXTex compression notes](https://github.com/microsoft/DirectXTex/wiki/Compress)

## 1. Summary

XRENGINE already has the important foundation for cooked texture data:

- third-party `XRTexture2D` cache writes can bypass YAML and produce a pure binary `XRTS` streaming payload;
- `XRTS` stores a mip-addressable texture payload with preview mip selection and per-mip offsets;
- runtime streaming can read selected resident mips directly from the payload without hydrating a full YAML asset;
- normal `XRTexture2D` `.asset` serialization still writes a YAML envelope containing a compressed cooked-binary `Payload`.

The next step is to make cooked texture data GPU-native instead of only CPU-native `Rgba8`. The cache should be able to store BC, ASTC, ETC2/EAC, and uncompressed fallback payloads, with role-aware color-space and mip-generation metadata. Runtime upload should use compressed texture upload APIs when the backend supports the cooked format.

This design also separates project authoring metadata from heavy texture bytes. Generated `.asset` files should remain useful, inspectable, and referenceable, but large texture byte payloads should live in disposable cooked cache files or payload sidecars. YAML should describe identity, import settings, source links, role, selected compression profile, cache key, and payload reference. It should not be the normal home for megabytes of mip data.

## 2. Current Baseline

### 2.1 Existing Binary Texture Cache

`AssetManager.Loading.SerializationAndCache.cs` already registers `TextureStreamingCacheCodec` for `XRTexture2D`. It uses a texture cache variant key named like:

```text
TextureStreaming_v3_preview<max>_rgba8_uncompressed_binary
```

The codec prepares a streamable texture cache asset and writes it with `XRTexture2D.WriteBinaryStreamingCacheFile(...)`. That path writes a pure binary payload and intentionally skips the legacy YAML path.

### 2.2 Existing `XRTS` Payload

`XRTexture2D.StreamingPayload.cs` defines:

- `StreamableMipSectionMagic = 0x58525453`, spelling `XRTS`;
- `StreamableMipSectionVersion = 1`;
- per-mip descriptors containing width, height, internal format, pixel format, pixel type, data offset, and byte length;
- a preview base mip index;
- direct resident-mip reads for streaming.

This is already the right structural shape for streaming. It needs a v2 descriptor model that can represent compressed blocks and richer metadata.

### 2.3 Current Payload Contents

The existing streaming cache is shaped as:

- `Rgba8`;
- linear filtering and repeat wraps;
- `AutoGenerateMipmaps = false`;
- full CPU or GPU-built mip chain;
- uncompressed `Mipmap2D.Data` bytes per mip.

This is better than raw PNG/JPG decode on warm load, but it still uploads uncompressed data and stores uncompressed mip bytes.

### 2.4 Current YAML Texture Asset Serialization

`XRTexture2DYamlTypeConverter` writes normal texture `.asset` files as:

```yaml
Format: CookedBinary
Payload:
  Encoding: ZstdHex
  Bytes: ...
```

The payload itself is cooked binary, but the file is still YAML text with a compressed hex byte string. This is acceptable for small assets and legacy compatibility. It is not ideal for generated or imported texture assets because it creates very large text files and requires YAML parsing before payload extraction.

### 2.5 Current Backend Gaps

Searches in the current renderer show no OpenGL compressed texture upload path yet. `GLTexture2D` uses `TexImage2D` and `TexSubImage2D`, not `CompressedTexImage2D` or `CompressedTexSubImage2D`.

The data enums already include many compressed internal formats:

- S3TC/DXT/BC1-BC3;
- RGTC/BC4-BC5;
- BPTC/BC6H-BC7;
- ETC2/EAC;
- ASTC LDR/HDR variants.

However, the Vulkan format conversion path does not appear to map compressed `EPixelInternalFormat` values yet.

## 3. Problems To Solve

1. Warm texture loads still upload uncompressed `Rgba8` payloads.
2. Texture cache data is not role-aware: albedo, normal, masks, and HDR data all collapse toward the same `Rgba8` storage.
3. Normal maps need special treatment: linear space, XY compression, Z reconstruction, and normal-preserving mip filtering.
4. Large generated texture `.asset` files can still carry big YAML cooked payloads.
5. OpenGL and Vulkan need explicit compressed upload support before compressed cooked payloads are useful.
6. Cache manifests do not yet carry complete color-space, texture-role, compression-format, block-layout, or quality metadata.
7. Existing sparse streaming is `Rgba8`-oriented. Compressed sparse residency needs separate validation and should not be enabled in the first compressed-cache milestone.

## 4. Goals

- Store third-party and generated texture payload bytes outside YAML by default.
- Preserve normal `.asset` metadata for identity, references, editor display, import settings, and source/cooked cache links.
- Add role-aware compression policy for common texture types.
- Cook desktop-first GPU-native formats for Windows/OpenGL 4.6:
  - BC7 for color and alpha color;
  - BC5 for normal maps;
  - BC4/BC5 for scalar masks;
  - BC6H for HDR textures.
- Prepare mobile/standalone targets:
  - ASTC for modern mobile;
  - ETC2/EAC fallback;
  - optional KTX2/Basis Universal as a portable interchange and distribution format.
- Keep `XRTS` mip-addressable and metadata-first.
- Upload compressed blocks directly on supported backends.
- Fall back visibly and diagnostically to uncompressed `Rgba8`/`R16`/`Rgba16f` when a format is unsupported.
- Keep generated caches disposable and deterministic from source + import options + compression settings.

## 5. Non-Goals

- This does not require replacing the stable YAML `.asset` format for every asset type.
- This does not remove source files or make cache files user-authored content.
- This does not enable page-level sparse residency for compressed textures in the first milestone.
- This does not implement neural texture compression.
- This does not require Vulkan parity before OpenGL desktop BC compression can land.
- This does not make all runtime render targets compressible. The target is imported/static sampled textures, not framebuffer attachments.
- This does not silently CPU-fallback when the user explicitly requested a GPU-native compressed format. Unsupported formats must report diagnostics and either use an explicit fallback policy or reject the cache.

## 6. Terminology

| Term | Meaning |
| --- | --- |
| Source texture | User-owned third-party file such as `.png`, `.jpg`, `.tga`, `.exr`, `.hdr`, `.ktx`, `.ktx2`, or `.dds`. |
| Generated texture asset | Project `.asset` that represents an imported `XRTexture2D` and is visible/referenceable in editor workflows. |
| Cooked texture cache | Disposable binary payload under cache storage, preferred for warm loads and streaming. |
| Payload sidecar | Optional project-local or cache-local binary file referenced by a generated `.asset`. |
| `XRTS` | XRENGINE texture streaming payload section. Current v1 is mip-addressable and uncompressed. This design extends it to compressed v2. |
| Texture role | Semantic use of the texture: albedo, normal, roughness, metallic, occlusion, mask, emission, height, HDR, UI, etc. |
| Color space | How source samples should be interpreted: sRGB, linear, HDR linear, or unknown. |
| GPU-native compression | Hardware-sampled fixed-rate block compression such as BC, ASTC, ETC2, or EAC. |
| Supercompression | Additional disk/transmission compression around GPU blocks, such as KTX2 BasisLZ or Zstd around payload bytes. |

## 7. Format Policy

### 7.1 Desktop First Policy

Windows/OpenGL 4.6 should prefer BC formats because they are broadly supported by desktop GPUs and map cleanly to Vulkan and Direct3D concepts.

| Texture role | Preferred desktop format | Fallback | Color space | Shader handling |
| --- | --- | --- | --- | --- |
| Albedo/base color, opaque | BC7 sRGB | BC1 sRGB, then RGBA8 sRGB | sRGB | sample normally |
| Albedo/base color, alpha | BC7 sRGB | BC3 sRGB, then RGBA8 sRGB | sRGB | preserve alpha mode |
| Cutout alpha only | BC7 sRGB or BC1 one-bit alpha where acceptable | RGBA8 sRGB | sRGB | respect material alpha test |
| Normal map | BC5 unsigned or signed normalized | RG8/RG16, then RGBA8 | linear | store XY, reconstruct Z |
| Height/displacement | BC4, R16, or R16F | R8/R16 | linear | role decides precision |
| Roughness | BC4 | R8 | linear | no sRGB |
| Metallic | BC4 | R8 | linear | no sRGB |
| AO/occlusion | BC4 | R8 | linear | no sRGB |
| Packed ORM/RMSE masks | BC7 or split BC4/BC5 by policy | RGBA8 | linear | preserve channel contract |
| Emissive color | BC7 sRGB or linear based on material model | RGBA8 | usually sRGB color, linear intensity if HDR | material-controlled |
| HDR skybox/IBL/EXR (see scope note) | BC6H unsigned float | RGB16F/RGBA16F | linear HDR | no sRGB |
| UI color | BC7 or RGBA8 | RGBA8 | usually sRGB | compression artifacts may disable |
| UI/font/SDF mask | BC4 or R8 | R8 | linear | preserve edge precision |

> Scope note (HDR environment maps): this document targets `XRTexture2D`. Skyboxes, IBL, and reflection probes are normally cubemaps or texture arrays, not 2D textures. Only equirectangular HDR stored as a single 2D texture is in scope here; cubemap/array BC6H cooking and upload is deferred to a follow-up design and must not be assumed to "just work" through the 2D path.

### 7.2 Mobile And Standalone Policy

Mobile support should be a separate target profile, not the desktop default.

| Target | Preferred formats | Notes |
| --- | --- | --- |
| Modern mobile | ASTC LDR | Use 4x4 or 5x5 for normals/UI, 5x5 or 6x6 for color, larger blocks only for low-frequency content. |
| Mobile fallback | ETC2/EAC | ETC2 RGB/RGBA for color, EAC R/RG for scalar/normal maps. |
| HDR mobile | ASTC HDR where supported, otherwise uncompressed half float | Must be capability-gated. |
| Cross-platform source | KTX2 Basis Universal | UASTC for quality/normal maps, ETC1S for small color/mask assets. |

### 7.3 Format Families

#### BC1-BC3 / S3TC

Use only as fallback or low-memory profile:

- BC1 is small and useful for opaque color, but visibly worse than BC7.
- BC3 can carry alpha, but BC7 generally gives better quality at the same 8 bpp.
- Avoid BC3/DXT5 normal-map hacks in new content; use BC5.

#### BC4-BC5 / RGTC

Use for scalar and two-channel data:

- BC4 is ideal for roughness, metallic, AO, height, and masks.
- BC5 is ideal for tangent-space normal XY. Reconstruct Z in shader:

```glsl
vec2 xy = texture(normalMap, uv).rg * 2.0 - 1.0;
float z = sqrt(max(0.0, 1.0 - dot(xy, xy)));
vec3 n = normalize(vec3(xy, z));
```

Signed BC5 may reduce decode remapping overhead, but unsigned BC5 is often easier to interop with standard texture tools. Pick one project-wide and record it in the manifest.

#### BC6H

Use for HDR RGB textures:

- skyboxes;
- reflection probes;
- prefiltered environment maps;
- high-range emissive/source maps when appropriate.

BC6H is not for LDR color or alpha.

#### BC7 / BPTC

Use as the default high-quality desktop color format:

- color with smooth gradients;
- color with alpha;
- packed multi-channel masks where preserving correlation matters;
- UI only when artifacts are acceptable.

Use sRGB BC7 variants for color data and unorm BC7 variants for linear data.

#### ASTC

Use for mobile or optional high-quality cross-platform profiles:

- 4x4: 8 bpp, high quality;
- 5x5: 5.12 bpp, good quality default;
- 6x6: 3.56 bpp, good memory saving for color;
- 8x8 and larger: only low-frequency or distant content.

ASTC is flexible but must be capability-gated on desktop; do not assume support on the Windows/OpenGL path.

#### ETC2/EAC

Use for mobile fallback:

- ETC2 RGB/RGBA for color;
- EAC R11/RG11 for scalar and normal maps.

Do not make ETC2 the Windows desktop default.

#### KTX2 / Basis Universal

KTX2 is best treated as:

- an importable source/container format;
- a cross-platform distribution cache format;
- a future optional payload source that can transcode to BC/ASTC/ETC at cook time or load time.

For engine-native warm caches, `XRTS` should remain the direct runtime format because it can be tailored to XRENGINE streaming and metadata needs. KTX2 support should complement `XRTS`, not replace it.

#### Build vs. Adopt `XRTS` (Explicit Decision)

KTX2 already provides much of what `XRTS` v2 proposes: mip-addressable layout, a metadata key/value block, Basis Universal transcoding, and standardized supercompression. Building a bespoke `XRTS` v2 container is therefore a conscious build-vs-adopt tradeoff that reviewers must accept, not a default.

Reasons to keep a custom `XRTS`:

- direct control over streaming-oriented mip residency and preview-mip selection;
- ability to embed XRENGINE-specific freshness, role, and cache-key metadata without overloading KTX2 KV semantics;
- avoidance of a hard runtime dependency on KTX-Software for warm-load reads, whose license has special cases (see §13.1, §23).

Reasons that could favor adopting KTX2 instead:

- mature, tested container with broad tooling;
- built-in Basis/supercompression;
- reduced long-term maintenance of a hand-rolled binary format.

Decision for this milestone: keep `XRTS` as the runtime warm-cache format, but constrain its design so a KTX2 import/transcode path (§7.3, Phase 6) can populate `XRTS` without schema conflicts. If `XRTS` maintenance cost grows, revisit adopting KTX2 directly as the cache payload.

## 8. Asset And Cache Authority Model

### 8.1 Desired Split

Generated texture `.asset` files should become metadata-first:

```yaml
__assetType: XREngine.Rendering.XRTexture2D
ID: ...
Name: BrickWall_Albedo
OriginalPath: D:\Project\Assets\Textures\BrickWall.png
OriginalLastWriteTimeUtc: 2026-06-03T...
TextureRole: Albedo
ColorSpace: SRgb
CompressionProfile: DesktopHighQuality
CookedPayload:
  Format: XRTS
  Version: 2
  CachePath: Cache/...
  SourceHash: ...
  VariantKey: TextureStreaming_v4_desktop_bc7_srgb_roleAlbedo_...
```

The heavy bytes live in a binary cache file:

```text
Cache/.../BrickWall.XRTexture2D.asset
```

or an explicitly named payload sidecar:

```text
Cache/.../BrickWall.xrtx
```

The existing cache path machinery already writes `.asset` cache files. Keeping `.asset` for cache paths is acceptable if the contents are binary and the reader already knows how to detect them. A clearer future extension such as `.xrtx` can reduce confusion, but it is not required for v1.

> Decision: prefer committing to a dedicated `.xrtx` extension for binary cooked payloads now rather than reusing `.asset` for binary cache files. Reusing `.asset` for binary content is acknowledged as confusing (Open Question #1), and changing the extension later forces a cache migration. Standardizing on `.xrtx` up front avoids that migration and makes binary payloads unambiguous to tooling and source control.

### 8.2 Authority Rules

1. Source textures remain the user-owned authority for reimport.
2. Generated `.asset` metadata is the project-facing reference target.
3. Fresh cooked payload files are the runtime and warm-load data authority.
4. If the payload is missing, stale, or unsupported, editor load should fall back to source import when possible.
5. Runtime packaged builds may choose stricter behavior and fail if required cooked payloads are missing.
6. Manual reimport skips cache read, decodes source, rewrites metadata, and atomically replaces the payload.

### 8.3 Normal YAML Compatibility

Keep `XRTexture2DYamlTypeConverter` support for `Format: CookedBinary` payloads:

- for existing assets;
- for small hand-authored textures;
- for cache files generated before the binary path;
- for tests.

New generated third-party texture assets should prefer metadata + payload reference instead of inline cooked payload.

### 8.4 Cache Freshness Inputs

The compressed texture cache variant must include:

- source normalized path identity or existing cache directory identity;
- source length;
- source timestamp;
- optional source hash;
- import options timestamp;
- texture role;
- color space;
- compression profile;
- backend target profile, such as `DesktopBC`, `MobileASTC`, `MobileETC2`;
- encoder name and encoder version;
- encoder settings hash;
- mip generation policy version;
- `XRTS` schema version;
- runtime renderer compatibility version.

Cache miss diagnostics should report one reason:

- `Missing`
- `SourceNewer`
- `ImportOptionsNewer`
- `UnsupportedSchema`
- `UnsupportedBackendFormat`
- `SourceHashMismatch`
- `EncoderVersionMismatch`
- `RoleOrColorSpaceMismatch`
- `PayloadCorrupt`
- `UserForcedReimport`

## 9. `XRTS` v2 Payload Design

### 9.1 Versioning

Keep the magic value `XRTS`, but bump the streamable mip section version:

```csharp
private const int StreamableMipSectionVersion = 2;
```

The v2 reader must keep v1 compatibility. v1 means uncompressed mip descriptors. v2 means explicit texture metadata and block-aware mip descriptors.

### 9.2 Header

`XRTS` v2 should begin with fixed metadata after the version:

| Field | Type | Meaning |
| --- | --- | --- |
| `PayloadVersion` | `int` | v2 payload layout version. |
| `ByteOrderSentinel` | `uint` | Fixed little-endian sentinel value used to detect byte order and gross corruption. |
| `Flags` | `uint` | HasCompressedBlocks, HasSupercompression, HasChecksum, etc. |
| `TextureRole` | stable numeric enum | Albedo, Normal, Roughness, etc. Reserved numeric ranges; no strings in the runtime payload. |
| `ColorSpace` | enum | SRgb, Linear, HDRLinear, Unknown. |
| `StorageFormat` | enum | Canonical cooked format (BC7Srgb, BC5Unorm, BC4Unorm, BC6HUfloat, Rgba8, etc.). Single source of truth for backend formats. |
| `BlockWidth` | `byte` | 4 for BC, variable for ASTC, 1 for uncompressed. |
| `BlockHeight` | `byte` | 4 for BC, variable for ASTC, 1 for uncompressed. |
| `BlockDepth` | `byte` | 1 for 2D. |
| `BlockBytes` | `byte` | 8 or 16 for most compressed formats. |
| `MipCount` | `int` | Number of mip records. |
| `PreviewBaseMipIndex` | `int` | First resident mip used for preview streaming. |
| `SourceWidth` | `uint` | Logical source width. |
| `SourceHeight` | `uint` | Logical source height. |
| `SourceHash` | `ulong` | Optional fast hash for diagnostics/freshness. |
| `EncoderId` | string ID | Encoder/tool name. |
| `EncoderVersion` | string ID | Encoder/tool version. |

> `QualityMetric` (PSNR/SSIM and friends) is intentionally **not** stored in the runtime payload header. It is diagnostic data and belongs in the generated `.asset` metadata or a sidecar manifest, keeping the GPU-consumed payload lean.

#### Canonical Format Rule

The payload serializes only the canonical `StorageFormat`. Backend-specific formats (`ESizedInternalFormat`, `EPixelInternalFormat`, and the Vulkan `VkFormat`) are **derived at load time** through lookup tables, not serialized into `XRTS`. Storing one canonical format avoids four redundant representations drifting out of sync and reduces what must be validated on read.

> Enum reuse: prefer reusing the existing `EPixelInternalFormat` family (which already enumerates BC/ASTC/ETC2 formats per §2.5) as the basis for `StorageFormat`, or maintain a single authoritative mapping table plus a unit test asserting full coverage. Do not let a new `CookedTextureStorageFormat` enum (§10.1) silently diverge from the existing renderer enums.

### 9.3 Mip Descriptor v2

Each mip descriptor should include:

| Field | Type | Meaning |
| --- | --- | --- |
| `MipIndex` | `int` | Source mip index. |
| `Width` | `uint` | Logical mip width. |
| `Height` | `uint` | Logical mip height. |
| `StorageFormat` | enum | Allows future mixed fallback, usually same as header. |
| `DataEncoding` | enum | RawPixels, GpuBlocks, Ktx2Basis, ExternalRef. |
| `BlockWidth` | `byte` | Repeated for random access/self-contained descriptors. |
| `BlockHeight` | `byte` | Repeated for random access/self-contained descriptors. |
| `BlockBytes` | `byte` | Repeated for random access/self-contained descriptors. |
| `RowPitch` | `int` | Byte distance between compressed block rows or pixel rows. |
| `SlicePitch` | `int` | Total byte size for the mip. |
| `DataOffset` | `long` | Offset from `XRTS` section start. |
| `DataLength` | `int` | Stored byte count. |
| `UncompressedLength` | `int` | Expected decoded byte count if applicable. |
| `Checksum` | `uint` | Optional corruption check. |

For BC formats:

```text
blocksWide = max(1, (width + 3) / 4)
blocksHigh = max(1, (height + 3) / 4)
dataLength = blocksWide * blocksHigh * blockBytes
```

For ASTC:

```text
blocksWide = max(1, ceil(width / astcBlockWidth))
blocksHigh = max(1, ceil(height / astcBlockHeight))
dataLength = blocksWide * blocksHigh * 16
```

For uncompressed:

```text
blockWidth = 1
blockHeight = 1
blockBytes = bytesPerPixel
rowPitch = width * bytesPerPixel
```

### 9.3.1 Mip Chain Floor And Padded Tail Blocks

The block-count formulas already round up, but the payload must state two policies explicitly so GL/Vulkan `imageSize` expectations and tiny-preview mips agree:

- The mip chain runs to a 1x1 logical mip by default. Mips whose logical dimensions are smaller than the block footprint (2x2, 1x1 for BC/ASTC) still occupy exactly one fully padded block. Their `DataLength` therefore equals one block (`blockBytes`), never a partial block.
- Encoders may optionally truncate the chain at the smallest mip whose dimensions are still `>= blockWidth/blockHeight`. When the chain is truncated, the truncation is recorded in the header (`MipCount`) and the smallest stored mip becomes the residency floor. The reader must never synthesize missing tail mips.

Preview-mip selection (§16.2) must pick a base mip that actually exists under this floor; it must not request a tail mip the encoder truncated.

### 9.4 Supercompression

Do not add supercompression in the first compressed-cache milestone. GPU block formats are already fixed-rate and fast to sample; the first win is reduced VRAM and upload bytes.

Later, add per-payload or per-mip supercompression:

- Zstd around BC/ASTC payloads for disk size;
- KTX2 BasisLZ for ETC1S;
- KTX2 UASTC with optional Zstd-like supercompression.

If supercompression is enabled, streaming reads must account for decompression cost and should avoid decompressing full mip chains when a single resident mip is requested.

### 9.5 Binary Safety And Validation

`XRTS` payloads are read directly from disposable, potentially crafted or corrupt files. Validation is a hard correctness/security requirement, not just a diagnostic source. Treat untrusted cache bytes as a boundary.

Required rules:

- **Byte order:** the binary layout is little-endian. Readers verify `ByteOrderSentinel` before trusting any field. A mismatch is rejected as `PayloadCorrupt`; readers do not byte-swap silently.
- **Bounds checks:** every `DataOffset` and `DataLength` must be validated against the actual file/section length *before* any read. Reject if `DataOffset < sectionStart`, `DataOffset + DataLength` overflows, or `DataOffset + DataLength > fileLength`. This closes an out-of-bounds read vector.
- **Length agreement:** for compressed mips, `DataLength` must equal the block-formula-derived size (`blocksWide * blocksHigh * blockBytes`) for the declared `StorageFormat` and dimensions before any GPU upload. For uncompressed mips it must equal `rowPitch * height`. Mismatch is rejected, never clamped.
- **Descriptor sanity:** `MipCount`, `BlockWidth/Height`, `BlockBytes`, and dimensions must be within sane fixed limits; reject absurd values rather than allocating from attacker-controlled sizes.
- **Alignment:** each mip `DataOffset` is aligned to `max(4, BlockBytes)` so Vulkan staging copies (§15.3) are valid without re-packing.
- **Optional checksum:** when `HasChecksum` is set, verify the per-mip `Checksum` after the bounds/length checks pass.

Any failure maps to a single diagnostic reason (§8.4, e.g. `PayloadCorrupt` / `UnsupportedSchema`) and triggers the §22 failure policy (reimport in editor, fail in packaged builds). The reader must never upload unvalidated bytes to the GPU.

## 10. Runtime Data Model Changes

### 10.1 New Metadata Types

Add texture metadata under the rendering/runtime texture namespace:

```csharp
public enum TextureRole
{
    Unknown,
    Albedo,
    Normal,
    Roughness,
    Metallic,
    Occlusion,
    PackedMask,
    Emissive,
    Height,
    HdrEnvironment,
    UiColor,
    UiMask
}

public enum TextureColorSpace
{
    Unknown,
    Linear,
    SRgb,
    HdrLinear
}

public enum CookedTextureStorageFormat
{
    Rgba8,
    R8,
    Rg8,
    Rgba16f,
    Rgb16f,
    BC1Srgb,
    BC1Unorm,
    BC3Srgb,
    BC3Unorm,
    BC4Unorm,
    BC4Snorm,
    BC5Unorm,
    BC5Snorm,
    BC6HUfloat,
    BC6HSfloat,
    BC7Srgb,
    BC7Unorm,
    Etc2RgbSrgb,
    Etc2RgbUnorm,
    Etc2RgbaSrgb,
    Etc2RgbaUnorm,
    EacR11Unorm,
    EacRg11Unorm,
    Astc4x4Srgb,
    Astc4x4Unorm,
    Astc5x5Srgb,
    Astc5x5Unorm,
    Astc6x6Srgb,
    Astc6x6Unorm
}

public enum TextureDataEncoding
{
    RawPixels,
    GpuCompressedBlocks,
    SupercompressedGpuBlocks,
    BasisUniversal,
    ExternalPayloadReference
}
```

The exact enum names can be shorter, but the model needs to distinguish role, color space, GPU storage format, and data encoding.

> Avoid enum divergence: `CookedTextureStorageFormat` overlaps the existing `EPixelInternalFormat` family, which already enumerates BC/ASTC/ETC2 formats (§2.5). Either base `CookedTextureStorageFormat` directly on the existing enum values or maintain one authoritative mapping table with a unit test asserting every cooked format maps to a valid renderer format (and back). Two independently edited enums will drift.

### 10.2 `Mipmap2D` Options

There are two viable approaches.

Option A: extend `Mipmap2D`.

- Add `TextureDataEncoding`.
- Add block dimensions and block bytes.
- Allow `Data` to contain GPU compressed blocks when `TextureDataEncoding == GpuCompressedBlocks`.
- Treat `PixelFormat` and `PixelType` as ignored for compressed upload.

Option B: introduce a separate payload descriptor.

- Keep `Mipmap2D` as CPU pixel data.
- Add `TextureMipPayload` or `CookedTextureMip` for cache/read/upload paths.
- Convert to `Mipmap2D` only for preview/decompression/editor fallback.

Recommended: Option B for v1 compressed cache. `Mipmap2D` currently means "raw image data for a 2D texture mipmap" and many call sites assume `TexSubImage2D`-style uploads. A distinct payload type avoids overloading `Mipmap2D.Data` with bytes that are not pixels.

### 10.3 `XRTexture2D` Metadata

Add optional properties:

- `TextureRole Role`
- `TextureColorSpace ColorSpace`
- `CookedTextureStorageFormat CookedStorageFormat`
- `TextureDataEncoding CookedDataEncoding`
- `string? CookedPayloadPath`
- `string? CookedPayloadVariantKey`
- `string? CompressionProfile`
- `bool PreferCookedPayload`

For `XRBase`-derived types, property setters must use `SetField(...)`.

## 11. Import And Role Detection

### 11.1 Explicit Import Options

Texture import settings should expose:

- texture role;
- color space;
- compression profile;
- normal-map convention: OpenGL/Y+ or DirectX/Y-;
- alpha mode: none, transparency, cutout, coverage-preserving alpha;
- mip generation policy;
- platform target profile;
- quality/speed preset;
- force uncompressed;
- preserve source dimensions;
- generate preview mip max dimension.

The ImGui third-party import inspector should show:

- resolved role;
- selected cooked format;
- original source format and dimensions;
- cooked mip count;
- payload path;
- estimated source bytes, cooked disk bytes, upload bytes, and GPU bytes;
- unsupported-format warnings.

### 11.2 Auto Detection

Auto role detection should combine:

- material slot name: `_MainTex`, `_BaseMap`, `_BumpMap`, `_Normal`, `_MetallicGlossMap`, etc.;
- sampler name: "normal", "bump", "rough", "metal", "occlusion", "ao", "height";
- filename suffix: `_n`, `_normal`, `_bump`, `_r`, `_roughness`, `_m`, `_metallic`, `_ao`, `_orm`, `_mask`, `_height`, `_emissive`;
- source format: `.exr` and `.hdr` suggest HDR linear unless overridden;
- user override from import settings.

User override always wins.

### 11.3 Normal Map Convention

Normal maps need a convention flag:

- DirectX style usually stores +Y down/green inverted relative to OpenGL tangent basis.
- OpenGL style usually stores +Y up.

The importer should allow flipping green before compression. The cooked payload manifest must record the convention after cooking, not just the source convention.

## 12. Mip Generation Policy

### 12.1 Color Textures

Generate mips in linear light:

1. Decode source sRGB to linear.
2. Filter/downsample.
3. Encode to target color space.
4. Compress target mip.

This avoids dark or overly bright mips for albedo textures.

### 12.2 Alpha Textures

Alpha handling depends on material alpha mode:

- Blend alpha: filter alpha normally.
- Cutout alpha: use coverage-preserving alpha mips so foliage/fences do not disappear too aggressively.
- Premultiplied alpha: filter premultiplied color and alpha together.

### 12.3 Normal Maps

Normal mip generation must not average encoded RGB naively. Use:

1. Decode XY/Z to normal vectors.
2. Average vectors over the mip footprint.
3. Renormalize or store length/variance when using roughness compensation.
4. Encode XY to BC5.
5. Reconstruct Z in shader.

Future improvement: normal variance can feed Toksvig-style roughness adjustment or a companion roughness mip policy.

### 12.4 Roughness And Masks

Roughness should use linear filtering. Packed masks should not be gamma corrected. If channel semantics conflict, allow splitting into separate BC4/BC5 payloads instead of preserving a packed RGBA map.

### 12.5 HDR

HDR mips stay linear. Compress to BC6H where supported. Environment maps should preserve prefiltered radiance pipelines separately from ordinary texture mip generation.

## 13. Encoder Tooling

### 13.1 Candidate Tools

| Tool | Best use | License notes |
| --- | --- | --- |
| DirectXTex / `texconv` | BC1-BC7, BC6H, DDS, Windows desktop path | MIT. Good first candidate for Windows-first BC cooking. |
| Arm `astcenc` | ASTC LDR/HDR encoding | Apache 2.0. Good standalone tool candidate. |
| Khronos KTX-Software | KTX/KTX2 load/write/transcode, Basis Universal | Mostly Apache 2.0, but includes special license cases that must be reviewed before vendoring. |
| Compressonator | Broad BC/ETC/ASTC support and analysis | Useful candidate, but license and dependency review must happen before integration. |

This design does not add dependencies. Any dependency addition must follow repository license policy and regenerate dependency docs.

### 13.2 Recommended Initial Tool Path

For the first implementation:

1. Use DirectXTex or `texconv` for Windows BC cooking.
2. Keep ASTC/KTX2 as planned future profiles.
3. Allow an external-tool path before embedding libraries, because it avoids immediate native SDK integration risk.
4. Record tool executable path, version, command-line settings, and output format in cache metadata.

> Encoder speed: DirectXTex/`texconv` CPU BC7 encoding is slow and is acceptable only for correctness bring-up. For the bounded background cook job in §21, plan to move to a fast SIMD/GPU encoder such as Intel ISPC Texture Compressor or `bc7enc_rdo`. RDO-capable encoders additionally produce output that compresses better under the §9.4 supercompression phase, so they pay off twice.

> Determinism: some multithreaded/GPU encoders are non-deterministic. This does not break cache-key correctness (the key still matches the source + settings), but reproducible builds that compare cooked bytes across machines need a deterministic encoder mode or must compare by source/settings identity rather than payload bytes. Record the chosen mode in cache metadata.

### 13.3 In-Process Encoder Path

After the external-tool path is proven:

- wrap DirectXTex or a vetted BC encoder for in-process cooking;
- expose progress/cancel support for editor imports;
- add parallel encode scheduling with concurrency caps;
- cache encode jobs by source hash + settings.

## 14. OpenGL Backend Design

### 14.1 Capability Detection

At renderer init, record support for:

- S3TC / DXT / BC1-BC3;
- RGTC / BC4-BC5;
- BPTC / BC6H-BC7;
- ETC2/EAC;
- ASTC LDR/HDR;
- `glCompressedTexImage2D`;
- `glCompressedTexSubImage2D`;
- compressed immutable storage compatibility.

OpenGL 4.6 includes many modern features, but extension and driver support still need explicit diagnostics because XRENGINE runs on real Windows driver stacks, not paper hardware.

### 14.2 Upload Path

Add compressed upload handling to `GLTexture2D`:

```text
if mip payload is GPU compressed:
    allocate storage with compressed sized internal format
    upload using CompressedTexImage2D or CompressedTexSubImage2D
else:
    existing TexImage2D/TexSubImage2D path
```

The upload path must:

- validate block dimensions;
- validate data length;
- handle NPOT final mips correctly;
- set unpack state safely;
- avoid row-chunk progressive upload for compressed blocks until block-row chunking is explicitly implemented;
- emit `Texture.UploadValidationFailed` diagnostics on mismatch;
- record compressed upload bytes separately from decoded logical bytes.

### 14.3 Storage

For dense textures, use immutable storage where possible:

- `TextureStorage2D` with the compressed sized internal format;
- then `CompressedTextureSubImage2D` if available;
- otherwise bind and use `CompressedTexSubImage2D`/`CompressedTexImage2D`.

If the Silk.NET binding layer lacks direct DSA compressed functions, use bind-to-target fallback first and add DSA wrappers later.

### 14.4 Sparse Textures

Do not enable compressed sparse residency in the first milestone.

Reasons:

- current sparse implementation queries page sizes for `Rgba8`;
- compressed page alignment and block alignment need separate validation;
- partial page residency is already intentionally disabled by policy;
- debugging dense compressed upload first reduces moving parts.

Phase it later:

1. dense compressed upload;
2. tiered compressed residency;
3. sparse compressed full-mip residency;
4. page-level compressed residency only after feedback/page-table work exists.

## 15. Vulkan Backend Design

### 15.1 Feature Detection

Vulkan exposes compressed texture support through physical-device features:

- `textureCompressionBC`;
- `textureCompressionETC2`;
- `textureCompressionASTC_LDR`;
- `textureCompressionASTC_HDR` (separate feature/extension; required for the ASTC HDR mobile profile in §7.2 and must not be assumed from `_LDR`).

The Vulkan backend should record these in a texture capability profile and expose them to the import/cook policy.

### 15.2 Format Mapping

Extend `VkFormatConversions` to map:

- BC1/BC2/BC3 sRGB/unorm;
- BC4/BC5 unorm/snorm;
- BC6H unsigned/signed float;
- BC7 sRGB/unorm;
- ETC2/EAC formats;
- ASTC block sizes.

Each mapping must be validated with `vkGetPhysicalDeviceFormatProperties` for sampled image support and linear filtering when needed.

### 15.3 Upload Path

Compressed Vulkan uploads use the same staging-buffer-to-image flow, but `VkBufferImageCopy` extents and offsets must respect compressed block layout rules.

The runtime should:

- allocate `VkImage` with compressed `VkFormat`;
- copy each mip's compressed block bytes;
- ensure each `VkBufferImageCopy.bufferOffset` is a multiple of both 4 and the compressed texel-block byte size (`BlockBytes`); the `XRTS` per-mip `DataOffset` alignment in §9.5 guarantees this without re-packing the staging buffer;
- set `bufferRowLength`/`bufferImageHeight` in texels aligned to the block footprint (or 0 to use tightly packed image extents);
- transition layouts as normal;
- record upload bytes as stored bytes, not decoded logical bytes;
- reject unsupported format/feature combinations before queue submission.

## 16. Cache Read And Streaming Behavior

### 16.1 Metadata-First Reads

Warm texture streaming should keep the current metadata-first shape:

1. Read compact manifest.
2. Decide whether the cache is usable for this backend.
3. Select resident mip range.
4. Read only needed mip byte ranges.
5. Upload selected payloads.

Every byte range read in steps 4-5 must pass the §9.5 binary safety checks (bounds, length agreement, alignment, optional checksum) before upload. Streaming reads of untrusted cache files are a boundary; validation cannot be skipped for performance.

For compressed mips, resident-data structures should carry `CookedTextureMipPayload` instead of forcing `Mipmap2D`.

### 16.2 Preview Mip

Preview mip policy remains:

- select a base mip whose largest dimension is at or below the configured preview max;
- ensure the preview mip exists in every cooked payload;
- upload preview quickly before higher-detail promotion.

Compressed previews should be stored in the same compression family as the full chain unless the format cannot represent very small mips correctly. If a fallback is needed for tiny mips, record mixed-format descriptors explicitly.

### 16.3 Budgeting

Texture streaming budgets need separate counters:

- logical decoded bytes;
- cooked stored bytes;
- upload bytes;
- committed GPU bytes;
- fallback decoded bytes.

Compressed textures reduce stored/upload/GPU bytes. Logical decoded bytes remain useful for comparing quality and source scale.

## 17. Generated Asset UX

### 17.1 Inspector

The ImGui inspector for generated texture assets should show:

- preview;
- source path;
- generated asset path;
- cooked payload/cache path;
- role;
- color space;
- compression profile;
- selected GPU format;
- mip count;
- source dimensions;
- preview resident dimensions;
- cooked disk bytes;
- estimated GPU bytes;
- backend support state;
- warnings/fallback reason.

Byte estimates shown here must use the same counter definitions as the §16.3 streaming budget counters (logical decoded, cooked stored, upload, committed GPU, fallback decoded). The inspector and the budget system must not maintain two divergent byte accountings.

The existing `Info` category at the bottom of third-party import selection is a good place for path and cache metadata.

### 17.2 Reimport Controls

Controls should include:

- Save & Reimport;
- Force recook payload;
- Clear cooked cache;
- Compression profile dropdown;
- Role dropdown;
- Color space dropdown;
- Normal Y flip toggle for normal maps;
- "Show cache diagnostics" foldout.

### 17.3 Preview

Preview must display decoded or sampled texture data correctly:

- sRGB color previews should be displayed color-managed;
- normal maps should have an option to preview raw XY/normal color and lit sphere;
- single-channel masks should show channel swatches or grayscale;
- compressed payload preview can decode CPU-side through the encoder library/tool if available, otherwise use the uploaded GPU texture handle.

## 18. Shader And Material Contract

### 18.1 Normal Maps

Materials sampling BC5 normals must know that only XY are stored. The material shader path should reconstruct Z and apply normal scale after reconstruction.

### 18.2 sRGB

The cooked format controls hardware sRGB decode. Do not double-apply gamma correction:

- albedo/emissive color can use sRGB formats;
- normals, roughness, metallic, AO, height, and packed masks use linear formats.

### 18.3 Packed Masks

Packed mask semantics must be material-defined. The importer should record channel meanings where known:

- R = occlusion;
- G = roughness;
- B = metallic;
- A = optional smoothness/height/mask.

If an engine material expects a different packing, the import settings should define remap rules before compression.

## 19. Migration Plan

### Phase 0: Design And Diagnostics

- Add this design.
- Add docs links.
- Add diagnostics that clearly distinguish:
  - inline YAML cooked payload;
  - raw binary `XRTS` cache;
  - future compressed `XRTS` cache.

### Phase 1: Metadata And Cache Contract

- Add role/color-space metadata.
- Add compression import settings.
- Add metadata-only generated texture asset support.
- Add `CookedPayloadRef` or equivalent.
- Keep existing inline YAML support.

### Phase 2: `XRTS` v2 Uncompressed Compatibility

- Implement v2 manifest and descriptors while still writing uncompressed payloads.
- Add v1 reader compatibility.
- Add tests for manifest parse, resident mip reads, offset validation, and corruption rejection.

### Phase 3: Desktop BC Cooking

- Add external `texconv`/DirectXTex cooking path.
- Cook:
  - albedo to BC7 sRGB;
  - normals to BC5;
  - masks to BC4/BC5 or BC7;
  - HDR to BC6H.
- Add cache variant keys for compression settings.
- Add UI diagnostics.

### Phase 4: OpenGL Compressed Upload

- Add GL capability profile.
- Add compressed upload path.
- Validate dense compressed textures in editor preview and runtime materials.
- Keep sparse compressed textures disabled.

### Phase 5: Vulkan Mapping And Upload

- Add Vulkan compressed feature detection.
- Add `VkFormat` mapping.
- Add compressed staging upload.
- Validate with non-sparse sampled textures first.

### Phase 6: Mobile And KTX2 Profiles

- Add ASTC external-tool path through `astcenc`.
- Add KTX2 load/import path through KTX tooling.
- Add ETC2/EAC fallback profile.
- Decide whether KTX2 is stored as source, cache payload, or only an intermediate.

### Phase 7: Advanced Quality

- Add normal-aware mips.
- Add coverage-preserving alpha mips.
- Add roughness/normal variance coupling.
- Add per-role quality metrics and import warnings.
- Add optional supercompression.

## 20. Testing And Validation

### 20.1 Unit Tests

Add deterministic tests for:

- role detection from file/sampler/material slot names;
- color-space defaults;
- cache variant key stability;
- `XRTS` v1 read compatibility;
- `XRTS` v2 manifest parsing;
- per-mip offset and length validation;
- resident mip selection;
- corrupted payload rejection;
- unsupported backend format fallback;
- metadata-only `.asset` roundtrip;
- generated asset reference to payload path.

### 20.2 Renderer Tests

OpenGL:

- upload BC7 albedo and compare rendered preview against reference;
- upload BC5 normal and validate lit surface orientation;
- upload BC4 roughness/mask and validate channel sampling;
- upload BC6H HDR texture and validate non-clamped values if shader path supports it;
- confirm no `GL_INVALID_ENUM`, `GL_INVALID_VALUE`, or upload validation failures.

Vulkan:

- feature detection reports expected support;
- unsupported formats are rejected before image creation;
- compressed staging upload copies correct byte counts;
- sampled image renders expected preview.

### 20.3 Visual Validation

Use a small validation scene with:

- high-gradient color texture;
- alpha foliage/cutout texture;
- tangent-space normal map with obvious directionality;
- roughness/metallic/AO masks;
- HDR skybox or probe.

Record:

- source decode count;
- cache hit/miss count;
- cache read milliseconds;
- cache parse milliseconds;
- upload bytes;
- GPU committed bytes;
- first visible preview time;
- promotion time;
- visual screenshots.

### 20.4 Quality Metrics

For cook-time diagnostics:

- PSNR/SSIM for color where useful;
- angular error for normal maps;
- max/mean scalar error for masks;
- alpha coverage delta for cutout textures;
- HDR relative error for BC6H.

Quality metrics are diagnostic, not asset-authoring blockers unless the user enables strict validation.

## 21. Performance Expectations

Expected wins:

- lower disk bytes than uncompressed `Rgba8` mip chains;
- lower upload bytes;
- lower VRAM use;
- faster warm material visibility because less data must be read and uploaded;
- lower memory pressure in texture streaming.

Expected costs:

- slower first import due to compression;
- larger and more complex cache keys;
- more backend-specific validation;
- possible preview decode complexity;
- quality tuning work per texture role.

First-import compression should run as a bounded background editor job. It must not monopolize all CPU cores while the editor is interactive.

## 22. Failure Policy

Failure behavior should be explicit:

| Scenario | Editor behavior | Runtime/cooked build behavior |
| --- | --- | --- |
| Cache missing | Reimport source if available. | Fail or use packaged fallback based on build policy. |
| Cache stale | Reimport source and rewrite cache. | Fail unless source is packaged for recook. |
| Format unsupported | Use configured fallback profile and log. | Fail if fallback not packaged. |
| Payload corrupt | Delete/ignore cache, reimport source. | Fail with asset path and cache path. |
| Encoder missing | Fall back to uncompressed only if policy allows. | Build/cook fails. |
| Normal-map role conflict | Warn and use explicit import setting. | Use serialized import setting. |

Do not hide explicitly requested GPU compression behind silent CPU/uncompressed fallback.

## 23. Risks

- BC7 encoding can be slow without GPU acceleration or good threading.
- Some source textures need artist-specific role/channel choices; auto detection will be wrong sometimes.
- Normal-map quality depends heavily on correct convention, filtering, and shader reconstruction.
- KTX-Software has license special cases that must be reviewed before vendoring.
- Compressonator is attractive but must pass dependency and license review before use.
- Compressed sparse residency could create alignment bugs if enabled too early.
- Mixed inline YAML and external payload modes can confuse asset debugging unless the inspector is clear.

## 24. Open Questions

1. Should generated texture payload sidecars use `.asset`, `.xrtx`, or a cache-only extension? Resolved (§8.1): standardize on `.xrtx` for binary cooked payloads.
2. Should project-generated `.asset` files under `Assets/` ever point to payload files under `Cache/`, or should they have durable sidecars beside the asset for source-control workflows?
3. Should BC5 normals use signed or unsigned normalized storage by default?
4. Should packed ORM maps stay packed in BC7 or split to BC4/BC5 payloads when runtime material bindings allow it?
5. Should `XRTexture2D` hold compressed payload descriptors directly, or should streaming source objects own them until upload?
6. Should KTX2 be supported as a first-class imported source before engine-native compressed `XRTS` writing? See the build-vs-adopt decision in §7.3 (keep `XRTS` for the warm cache; allow KTX2 as an import/transcode source).
7. How strict should packaged runtime be about missing or unsupported payloads?

## 25. Recommended First Implementation Slice

The smallest useful slice is:

1. Add role/color-space metadata and import settings.
2. Add metadata-only generated texture asset support that references the existing binary `XRTS` cache.
3. Bump cache variant from `TextureStreaming_v3_..._rgba8_uncompressed_binary` to a v4 key that includes role/color-space, but still writes uncompressed `Rgba8`.
4. Add `XRTS` v2 descriptor support while reading v1.
5. Add DirectXTex/`texconv` BC cooking behind an import setting.
6. Add OpenGL dense compressed upload for BC7, BC5, BC4, and BC6H.
7. Validate editor preview, material sampling, and warm-cache streaming.

This gives XRENGINE the highest-value desktop path first while keeping existing uncompressed `XRTS` caches and YAML cooked payloads readable.
