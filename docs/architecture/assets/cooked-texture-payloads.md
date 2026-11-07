# Cooked Texture Payloads

This document describes cooked texture payloads for imported `XRTexture2D` content. It covers the `XRTS` payload, cache authority, cache keys, format support, and known limits.

Related:

- [Texture Streaming](../rendering/texture-streaming.md)

## Scope

The current payload is a mip-addressable texture streaming cache. It is not page-addressable. It stores BC7 mip data when block compression is active (see [Block Compression](#block-compression)), and uncompressed `Rgba8` mip data otherwise. Page-addressable payloads and KTX2/Basis interchange are planned but not implemented.

## Authority And File Flow

`TextureStreamingSourceFactory` selects the resident data authority for a requested texture path.

- `XRTexture2D.ResolveTextureStreamingAuthorityPathInternal` maps a source path to a cache authority path.
- When the authority path is a texture asset, `AssetTextureStreamingSource` reads resident mips from the cooked payload. It keeps the original source as a fallback.
- When no usable texture asset exists, `ThirdPartyTextureStreamingSource` decodes the original image.
- `DeferredAuthorityTextureStreamingSource` delays cache lookup until a residency request arrives. Placeholder registration does not read or cook cache bytes.
- `TextureStreamingResidentDataReuseCache` stores a short-lived deep copy of resident data. A compatible canceled transition can reuse it instead of decoding again.

Fallbacks must log why they happen. A missing, stale, unreadable, or incompatible cache is not silent.

## `XRTS` Payload

`XRTexture2D.StreamingPayload.cs` owns the binary payload.

| Field | Current value or rule |
|---|---|
| Magic | `0x58525453` (`XRTS`) |
| Version | `1` |
| Payload data | Selected mip descriptors and mip byte ranges |
| Preview | A preview mip selected for startup streaming |
| Mip addressing | Width, height, format, data offset, and data length per mip |
| Resident read path | `TryReadResidentDataFromTextureAssetFileBytes` |
| Manifest path | `TryReadTextureStreamingManifestFromTextureAssetFileBytes` |
| Write path | `CreateTextureStreamingPayloadFromTexture`, `WriteTextureStreamingPayload`, `WriteBinaryStreamingCacheFile` |
| Usability check | `IsTextureStreamingAssetUsable` reads streamability without hydrating all mip bytes |

`WriteBinaryStreamingCacheFile` writes to a temporary sibling file and then moves it into place. It stamps the cache file with the source timestamp so the asset manager can compare freshness.

## Cache Keys

`TextureStreamingCacheCodec` selects the key from the effective cook compression:

- BC7: `TextureStreaming_v4_preview<preview-size>_bc7_binary`.
- Uncompressed: `TextureStreaming_v3_preview<preview-size>_rgba8_uncompressed_binary` (the earlier key, so existing RGBA8 caches stay valid).

A cache cooked in one mode is never reused in the other mode.

The next key must change when an import choice affects bytes, GPU format, or sampling. Include schema version, source dimensions or hash mode, role, color space, compression profile, backend profile, storage format, encoder id and version, encoder settings hash, mip policy, normal convention, and alpha mode.

Cache miss diagnostics must log one primary reason. Valid reasons include missing payload, source newer, import options newer, unsupported schema, unsupported backend format, source hash mismatch, encoder version mismatch, role or color-space mismatch, corrupt payload, and user-forced recook.

## Format Support

`EPixelInternalFormat` already contains constants for common compressed formats. The enum includes S3TC/BC1-BC3, RGTC/BC4-BC5, BPTC/BC6H-BC7, ETC2/EAC, and ASTC variants.

`ESizedInternalFormat` has `Bc7Unorm`, `Bc7Srgb`, `Bc5Unorm`, and `Bc4Unorm`, appended after the older values so that stored ordinals do not change. `TextureBlockFormats` (in `XREngine.Data`) gives the block size, the byte counts, and the mapping to the `EPixelInternalFormat` constants.

Dense compressed upload works on both renderers. Vulkan enables `textureCompressionBC`, maps the formats to `VK_FORMAT_BC*_BLOCK`, copies data in rows of 4x4 blocks, removes the attachment and storage usage bits, and never generates mips with blits. OpenGL allocates the BPTC and RGTC formats and uploads each mip with `glCompressedTexSubImage2D` (or `glCompressedTexImage2D`). Sparse residency stays disabled for compressed textures: OpenGL uses the dense backend, and the Vulkan sparse transition returns an unsupported result.

Planned role defaults:

| Role | Desktop target | Notes |
|---|---|---|
| Albedo or base color | BC7 sRGB | BC1 or BC3 can be a fallback by alpha mode and quality setting. |
| Normal or bump | BC5 linear | Store XY. Record normal convention and green-channel flip. Reconstruct Z in the shader. |
| Roughness, metallic, ambient occlusion, and masks | BC4, BC5, or BC7 linear | Never use sRGB formats. |
| HDR environment | BC6H | Use only when HDR sample support is present. |
| UI color | Profile-specific | Use sRGB only for color data. |

Mobile ASTC, ETC2/EAC, and KTX2/Basis profiles are future work.

## Generated Texture Assets

Generated texture `.asset` files can currently carry large payloads. The target shape is metadata-only. Heavy texture bytes must live in a cooked payload or cache file.

Metadata-only assets need source path and timestamp, optional hash, texture role, color space, compression profile, payload format and version, payload or cache path, cache variant key, selected GPU format, source dimensions, and mip count.

Editor loads can fall back to source import when a payload is missing or stale and the source exists. Runtime and published builds must fail with diagnostics unless an explicit packaged fallback is configured.

## Normal Map Contract

The normal-map contract is not complete. The stable contract must record OpenGL/Y+ or DirectX/Y- convention, green-channel flip, XY-only storage, Z reconstruction, signedness, and normal scale policy. Normal maps are linear data. They must not use sRGB decode.

## Block Compression

The texture cook encodes the RGBA8 mip chain as BC7 before the cache stores it (`TextureCookBlockCompressor`). The cook stores BC7 UNORM bits for color, data, and normal maps, so all four channels and the current shader sampling stay the same. The resident format then applies the texture color space: an sRGB texture resolves to `Bc7Srgb`, the same way `Rgba8` resolves to `Srgb8Alpha8`.

- Setting: `RuntimeEngine.Rendering.Settings.CookedTextureCompression` (`Bc7` by default, or `None`). The environment variable `XRE_TEXTURE_COOK_COMPRESSION` (`Bc7` or `None`) overrides it.
- Encoder: `ITextureBlockEncoder`, installed through `TextureBlockEncoderServices`. The desktop bootstrap installs the bc7enc_rdo encoder from `XREngine.Runtime.Imaging.Bc7Enc`. When no encoder is installed, the effective mode is `None`, and one warning says why.
- Sources that are not tightly packed 8-bit RGBA stay RGBA8, and an encoder failure keeps the texture RGBA8. Both cases are logged.
- BC5 (two-channel normal maps) and BC4 (single-channel masks) are encoded by the shim but not used yet. They need the shaders to rebuild normal Z, a view swizzle for one-channel data, and the texture role in the cook and cache key.
- A device without `textureCompressionBC` cannot show BC7 caches. The upload fails with a message that tells the user to set the compression to `None` and cook again; there is no silent decode.

## Compression Tooling

The cook uses bc7enc_rdo at a pinned commit (`Tools/Dependencies/Bc7Enc.lock.json`). `Tools/Dependencies/Build-Bc7Enc.ps1` builds the `xre_bcn` shim from `bc7enc.cpp` and `rgbcx.cpp` only (MIT or Unlicense); see [Native Dependencies](../../developer-guides/runtime/native-dependencies.md). Any other encoder needs dependency and license review first. Then run the dependency report and update dependency documents.

Encoder configuration must include executable path or integration mode, quality preset, concurrency cap, timeout, and output directory policy. Compression must remain cancelable and bounded so the editor stays responsive.

## Diagnostics

The texture cache path logs cache hits, misses, stale caches, writes, fallback to source, cache reads, slow cache reads, resident-data reuse, and upload or validation failures through the texture diagnostics channel.

Diagnostics must distinguish logical decoded bytes, cooked stored bytes, upload bytes, dense physical GPU bytes, estimated OpenGL sparse physical bytes, exact Vulkan bound sparse bytes when Vulkan sparse residency exists, and staging or transfer bytes in flight.

## Known Limits

- `XRTS` v1 is mip-addressable only. Each mip records its own format, so the same layout holds BC7 or RGBA8 data.
- The cook stores BC7 for every texture role. BC5 normal maps and BC4 masks are not used yet.
- Texture role and color-space metadata are incomplete.
- Normal map convention metadata is incomplete.
- Compressed textures have no sparse residency and no CPU decode (for example, `Mipmap2D.GetImage` rejects BC data).
- Page-addressable payloads are future SVT work.
- KTX2/Basis, ASTC, and ETC2/EAC profiles are not implemented.
