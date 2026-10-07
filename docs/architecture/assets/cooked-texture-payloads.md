# Cooked Texture Payloads

This document describes cooked texture payloads for imported `XRTexture2D` content. It covers the `XRTS` payload, cache authority, cache keys, format support, and known limits.

Related:

- [Texture Streaming](../rendering/texture-streaming.md)
- [Texture Validation](../../work/testing/texturing/texture-validation.md)
- [Texture Compression And Cooked Texture Cache Design](../../work/design/texturing/texture-compression-and-cooked-cache-design.md)
- [Texture Runtime, Streaming, And Virtual Texturing Design](../../work/design/texturing/texture-runtime-streaming-virtual-texturing-design.md)

## Scope

The current payload is a mip-addressable texture streaming cache. It is not page-addressable. It stores uncompressed `Rgba8` mip data for streaming use. GPU-native compressed payloads, page-addressable payloads, and KTX2/Basis interchange are planned but not implemented.

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

The current cache codec uses a v3 preview key. `TextureStreamingCacheCodec` contains the string pattern `TextureStreaming_v3_preview<preview-size>_rgba8_uncompressed_binary`.

The next key must change when an import choice affects bytes, GPU format, or sampling. Include schema version, source dimensions or hash mode, role, color space, compression profile, backend profile, storage format, encoder id and version, encoder settings hash, mip policy, normal convention, and alpha mode.

Cache miss diagnostics must log one primary reason. Valid reasons include missing payload, source newer, import options newer, unsupported schema, unsupported backend format, source hash mismatch, encoder version mismatch, role or color-space mismatch, corrupt payload, and user-forced recook.

## Format Support

`EPixelInternalFormat` already contains constants for common compressed formats. The enum includes S3TC/BC1-BC3, RGTC/BC4-BC5, BPTC/BC6H-BC7, ETC2/EAC, and ASTC variants.

Current runtime streaming still uses uncompressed `Rgba8` payloads. Dense compressed upload is not implemented for OpenGL or Vulkan. Sparse compressed residency must stay disabled until dense compressed upload is validated.

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

## Compression Tooling

No compression encoder is selected. If the project adds `texconv`, DirectXTex, `astcenc`, KTX2/Basis tooling, or another encoder, complete dependency and license review first. Then run the dependency report and update dependency documents.

Encoder configuration must include executable path or integration mode, quality preset, concurrency cap, timeout, and output directory policy. Compression must remain cancelable and bounded so the editor stays responsive.

## Diagnostics

The texture cache path logs cache hits, misses, stale caches, writes, fallback to source, cache reads, slow cache reads, resident-data reuse, and upload or validation failures through the texture diagnostics channel.

Diagnostics must distinguish logical decoded bytes, cooked stored bytes, upload bytes, dense physical GPU bytes, estimated OpenGL sparse physical bytes, exact Vulkan bound sparse bytes when Vulkan sparse residency exists, and staging or transfer bytes in flight.

## Known Limits

- `XRTS` v1 is uncompressed and mip-addressable only.
- Texture role and color-space metadata are incomplete.
- Normal map convention metadata is incomplete.
- OpenGL compressed dense upload is not implemented.
- Vulkan compressed dense upload is not implemented.
- Page-addressable payloads are future SVT work.
- KTX2/Basis, ASTC, and ETC2/EAC profiles are not implemented.
