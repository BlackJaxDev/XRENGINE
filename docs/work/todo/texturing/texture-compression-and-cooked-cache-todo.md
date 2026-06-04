# Texture Compression And Cooked Texture Cache TODO

Last Updated: 2026-06-04
Status: active phased roadmap
Source design: [Texture Compression And Cooked Texture Cache Design](../../design/texturing/texture-compression-and-cooked-cache-design.md)
Parent roadmap: [Texture Runtime, Streaming, And Virtual Texturing TODO](texture-runtime-streaming-virtual-texturing-todo.md)
Validation ledger: [Texture Runtime Streaming Validation](../../testing/texture-runtime-streaming-validation.md)

## Goal

Move imported and generated `XRTexture2D` content from uncompressed `Rgba8` cooked payloads toward role-aware, GPU-native cooked texture payloads while keeping existing YAML assets and `XRTS` v1 caches readable.

The first production target is Windows desktop with OpenGL 4.6:

- BC7 for albedo/color textures.
- BC5 for normal maps.
- BC4/BC5 or BC7 for masks.
- BC6H for HDR textures.
- Existing uncompressed `Rgba8`/`R16`/`Rgba16f` fallback when explicitly configured or required.

## Current Baseline

Implemented today:

- [x] Third-party `XRTexture2D` cache writes can use a pure binary `XRTS` payload instead of a YAML envelope.
- [x] `XRTS` payloads are mip-addressable and include preview mip selection plus per-mip offsets.
- [x] Runtime texture streaming can read selected resident mip ranges from cooked texture cache bytes.
- [x] Normal `XRTexture2D` `.asset` serialization still supports YAML `Format: CookedBinary` envelopes.
- [x] `EPixelInternalFormat` already contains BC/S3TC, RGTC, BPTC, ETC2/EAC, and ASTC enum values.
- [x] Texture streaming diagnostics and ImGui texture streaming panels already exist.

Known current limits:

- [ ] Current `XRTS` streaming cache payloads store uncompressed `Rgba8` mip bytes.
- [ ] Texture role and color-space metadata are incomplete.
- [ ] Normal map convention, green-channel flip, XY storage, and Z reconstruction are not a formal import contract.
- [ ] Generated texture `.asset` files can still carry large YAML cooked payloads.
- [ ] OpenGL compressed texture upload is not implemented.
- [ ] Vulkan compressed texture format mapping and upload are not implemented.
- [ ] Compressed sparse residency is not validated and must remain disabled until dense compressed upload is proven.
- [ ] KTX2/Basis, ASTC, and ETC2/EAC target profiles are not implemented.

## Phase 0: Branch, Baseline, And Tracker Setup

**Goal:** create a clean implementation lane and capture the current behavior before changing cache formats.

- [ ] Create a dedicated branch, for example `texture-compression-cooked-cache`.
- [ ] Confirm no unrelated dirty work will be touched by this tracker.
- [ ] Run and record baseline builds:
  - [ ] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
  - [ ] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
- [ ] Run the narrowest existing texture tests that compile in the current tree.
- [ ] Capture a baseline third-party PNG import:
  - [ ] generated `.asset` path
  - [ ] cooked cache path
  - [ ] `XRTS` cache hit/miss logs
  - [ ] preview behavior in ImGui
  - [ ] warm-cache load timing
- [ ] Capture a baseline normal-map import and material preview if an existing sample is available.
- [ ] Add this TODO to any active planning surface that needs it.
- [ ] Keep the source design linked from this tracker and update the design if implementation decisions change.

## Phase 1: Texture Metadata Contract

**Goal:** add stable metadata needed for role-aware cooking without changing payload bytes yet.

- [ ] Add texture role metadata:
  - [ ] unknown
  - [ ] albedo/base color
  - [ ] normal/bump
  - [ ] roughness
  - [ ] metallic
  - [ ] occlusion/AO
  - [ ] packed mask/ORM/RMSE
  - [ ] emissive
  - [ ] height/displacement
  - [ ] HDR environment
  - [ ] UI color
  - [ ] UI mask/font/SDF
- [ ] Add texture color-space metadata:
  - [ ] unknown
  - [ ] linear
  - [ ] sRGB
  - [ ] HDR linear
- [ ] Add compression profile metadata:
  - [ ] none/uncompressed
  - [ ] desktop high quality BC
  - [ ] desktop memory saver BC
  - [ ] mobile ASTC
  - [ ] mobile ETC2/EAC
  - [ ] KTX2/Basis source/interchange
- [ ] Add normal-map convention metadata:
  - [ ] OpenGL/Y+
  - [ ] DirectX/Y-
  - [ ] explicit green flip
  - [ ] unknown
- [ ] Add import-option fields for role, color space, compression profile, normal convention, alpha mode, and mip policy.
- [ ] Implement role auto-detection from:
  - [ ] material sampler name
  - [ ] material slot name
  - [ ] filename suffix
  - [ ] source extension such as `.exr`/`.hdr`
- [ ] Ensure explicit user import settings always override auto-detection.
- [ ] Add deterministic unit tests for role and color-space detection.
- [ ] Update ImGui third-party texture import selection to show resolved role, color space, and compression profile.
- [ ] Update texture diagnostics to log role and color space when loading or writing caches.

## Phase 2: Metadata-Only Generated Texture Assets

**Goal:** make generated texture `.asset` files describe the imported texture while heavy texture bytes live in a cooked payload/cache file.

- [ ] Define a generated texture asset metadata shape that records:
  - [ ] source path
  - [ ] source timestamp
  - [ ] optional source hash
  - [ ] texture role
  - [ ] color space
  - [ ] compression profile
  - [ ] cooked payload format
  - [ ] cooked payload version
  - [ ] cooked payload/cache path
  - [ ] cache variant key
  - [ ] selected GPU format
  - [ ] source dimensions
  - [ ] mip count
- [ ] Decide whether the initial payload reference is cache-root-relative or project-root-relative.
- [ ] Keep existing `XRTexture2DYamlTypeConverter` cooked payload reading for legacy assets.
- [ ] Ensure generated `.asset` loading prefers the cooked payload when fresh.
- [ ] Ensure generated `.asset` loading falls back to source import in editor when payload is missing/stale and the source exists.
- [ ] Add a runtime/published-build policy for missing payloads:
  - [ ] fail with diagnostics
  - [ ] use packaged fallback only if explicitly configured
- [ ] Add tests for metadata-only texture asset roundtrip.
- [ ] Add tests for missing/stale payload fallback.
- [ ] Add ImGui inspector fields for payload path, cache key, and freshness status.
- [ ] Ensure the texture preview still works when the `.asset` carries metadata only.

## Phase 3: `XRTS` v2 Manifest And Descriptor Format

**Goal:** add a block-aware `XRTS` format while keeping v1 cache compatibility.

- [ ] Add `XRTS` v2 constants and version dispatch.
- [ ] Add a v2 header containing:
  - [ ] payload version
  - [ ] flags
  - [ ] texture role
  - [ ] color space
  - [ ] storage format
  - [ ] data encoding
  - [ ] block width/height/depth
  - [ ] block bytes
  - [ ] source dimensions
  - [ ] mip count
  - [ ] preview base mip index
  - [ ] encoder id/version
  - [ ] optional quality metrics
- [ ] Add v2 mip descriptors containing:
  - [ ] mip index
  - [ ] logical width/height
  - [ ] storage format
  - [ ] data encoding
  - [ ] block dimensions
  - [ ] row pitch
  - [ ] slice pitch
  - [ ] data offset
  - [ ] data length
  - [ ] optional checksum
- [ ] Implement v2 writer for uncompressed payloads first.
- [ ] Implement v2 reader for uncompressed payloads first.
- [ ] Keep v1 reader compatibility.
- [ ] Add metadata-first manifest read support for v2.
- [ ] Add resident mip range reads for v2.
- [ ] Validate NPOT and final 1x1 mip descriptors.
- [ ] Add corruption tests:
  - [ ] bad magic
  - [ ] unsupported version
  - [ ] invalid offset
  - [ ] invalid length
  - [ ] truncated payload
  - [ ] unsupported storage format
- [ ] Add cache freshness tests for v2 variant keys.
- [ ] Update cache logging to distinguish `XRTS v1`, `XRTS v2 uncompressed`, and future `XRTS v2 compressed`.

## Phase 4: Cache Variant Keys And Diagnostics

**Goal:** ensure cache identity changes whenever import, compression, or backend-relevant settings change.

- [ ] Define the v4 texture cache variant key.
- [ ] Include in the variant key:
  - [ ] `XRTS` schema version
  - [ ] source dimensions or source hash mode
  - [ ] texture role
  - [ ] color space
  - [ ] compression profile
  - [ ] target backend profile
  - [ ] selected storage format
  - [ ] encoder id/version
  - [ ] encoder settings hash
  - [ ] mip policy version
  - [ ] normal convention
  - [ ] alpha mode
- [ ] Add cache miss reason enum values:
  - [ ] missing
  - [ ] source newer
  - [ ] import options newer
  - [ ] unsupported schema
  - [ ] unsupported backend format
  - [ ] source hash mismatch
  - [ ] encoder version mismatch
  - [ ] role/color-space mismatch
  - [ ] payload corrupt
  - [ ] user forced reimport
- [ ] Log exactly one primary miss reason per cache miss.
- [ ] Add ImGui diagnostics for miss reason and selected fallback.
- [ ] Add tests that changing role/color space/compression profile changes the cache key.
- [ ] Add tests that unchanged settings keep cache keys stable.

## Phase 5: Desktop BC Encoder Integration

**Goal:** produce desktop GPU-native BC payload bytes at cook/import time.

- [ ] Choose initial encoder path:
  - [ ] external `texconv`/DirectXTex executable, or
  - [ ] in-process DirectXTex wrapper.
- [ ] If adding or vendoring any dependency, complete dependency/license review first.
- [ ] After adding dependencies, run:
  - [ ] `pwsh Tools/Generate-Dependencies.ps1`
  - [ ] review `docs/DEPENDENCIES.md`
  - [ ] review generated license files
- [ ] Add encoder configuration:
  - [ ] executable path or integration mode
  - [ ] quality preset
  - [ ] thread/concurrency cap
  - [ ] timeout
  - [ ] temporary output directory
- [ ] Add BC cooking for albedo/base color:
  - [ ] BC7 sRGB default
  - [ ] BC1 sRGB memory-saver fallback for opaque textures
  - [ ] BC3 sRGB fallback if BC7 unsupported
- [ ] Add BC cooking for normal maps:
  - [ ] convert to XY storage
  - [ ] apply optional green flip
  - [ ] cook to BC5
  - [ ] record unsigned/signed convention
- [ ] Add BC cooking for masks:
  - [ ] BC4 single-channel
  - [ ] BC5 two-channel
  - [ ] BC7 packed RGBA fallback
- [ ] Add BC6H cooking for HDR textures.
- [ ] Ensure source alpha mode affects selected format.
- [ ] Ensure sRGB formats are used only for color data.
- [ ] Ensure linear formats are used for normals, masks, roughness, metallic, AO, and height.
- [ ] Add first-import progress and cancellation support.
- [ ] Bound compression concurrency so editor responsiveness is preserved.
- [ ] Add cook output validation:
  - [ ] block dimensions
  - [ ] mip count
  - [ ] data length
  - [ ] selected format
- [ ] Add import tests using tiny fixture textures where possible.

## Phase 6: OpenGL Dense Compressed Upload

**Goal:** make compressed BC payloads render in the OpenGL backend without sparse residency.

- [ ] Add OpenGL texture compression capability detection:
  - [ ] S3TC / BC1-BC3
  - [ ] RGTC / BC4-BC5
  - [ ] BPTC / BC6H-BC7
  - [ ] ETC2/EAC
  - [ ] ASTC if exposed
- [ ] Add capability diagnostics to renderer logs.
- [ ] Map cooked storage formats to GL compressed internal formats.
- [ ] Add a compressed upload branch for `XRTexture2D` dense textures.
- [ ] Use immutable storage where compatible.
- [ ] Use `glCompressedTexImage2D` or `glCompressedTexSubImage2D` for compressed mips.
- [ ] Validate block-aligned byte lengths before GL calls.
- [ ] Handle NPOT final mips correctly.
- [ ] Disable row-chunk progressive upload for compressed blocks until block-row chunking is implemented.
- [ ] Record compressed upload bytes separately from logical decoded bytes.
- [ ] Keep existing uncompressed upload path unchanged for uncompressed textures.
- [ ] Add fallback diagnostics when selected compression is unsupported.
- [ ] Validate editor previews:
  - [ ] BC7 color texture
  - [ ] BC5 normal map
  - [ ] BC4 scalar mask
  - [ ] BC6H HDR texture if sample exists
- [ ] Confirm no texture upload validation failures in normal compressed texture runs.
- [ ] Confirm no `GL_INVALID_ENUM` or `GL_INVALID_VALUE` in `log_opengl.txt`.

## Phase 7: Material And Shader Sampling Contract

**Goal:** make materials interpret compressed role-specific textures correctly.

- [ ] Add shader/material metadata for normal-map XY storage.
- [ ] Reconstruct BC5 normal Z in shader.
- [ ] Apply normal scale after reconstruction.
- [ ] Ensure normal maps are sampled as linear textures.
- [ ] Ensure albedo/emissive color textures use hardware sRGB decode when stored as sRGB formats.
- [ ] Ensure roughness/metallic/AO/height/masks never use sRGB formats.
- [ ] Add material import remap support for packed masks where needed.
- [ ] Add fallback textures by role:
  - [ ] albedo visible fallback
  - [ ] flat normal
  - [ ] neutral roughness/AO
  - [ ] zero metallic/emissive
- [ ] Add a validation material set that exercises each role.
- [ ] Add screenshots or rendered comparisons for compressed versus uncompressed samples.

## Phase 8: Quality Mip Generation

**Goal:** improve mip quality before enabling compression broadly.

- [ ] Generate color mips in linear light.
- [ ] Add coverage-preserving alpha mips for cutout textures.
- [ ] Add premultiplied-alpha mip handling where material alpha mode requires it.
- [ ] Add normal-aware mip generation:
  - [ ] decode normal vectors
  - [ ] average vectors
  - [ ] renormalize
  - [ ] encode XY
- [ ] Add roughness/mask linear mip generation.
- [ ] Add HDR linear mip generation.
- [ ] Record mip-generation policy version in the cache key.
- [ ] Add tests for normal mip orientation.
- [ ] Add tests for alpha coverage preservation.
- [ ] Add cook-time quality metrics where practical:
  - [ ] color PSNR/SSIM
  - [ ] normal angular error
  - [ ] scalar max/mean error
  - [ ] alpha coverage delta

## Phase 9: Vulkan Compressed Texture Support

**Goal:** add Vulkan parity for dense compressed sampled textures.

- [ ] Add Vulkan texture compression feature detection:
  - [ ] `textureCompressionBC`
  - [ ] `textureCompressionETC2`
  - [ ] `textureCompressionASTC_LDR`
- [ ] Add renderer-visible texture capability profile.
- [ ] Extend `VkFormatConversions` with:
  - [ ] BC1/BC2/BC3 sRGB/unorm
  - [ ] BC4/BC5 unorm/snorm
  - [ ] BC6H unsigned/signed float
  - [ ] BC7 sRGB/unorm
  - [ ] ETC2/EAC formats
  - [ ] ASTC block-size formats
- [ ] Validate sampled image and filtering support with physical-device format properties.
- [ ] Allocate `VkImage` using compressed `VkFormat`.
- [ ] Add staging-buffer upload for compressed mip byte ranges.
- [ ] Ensure `VkBufferImageCopy` extents and offsets respect compressed block rules.
- [ ] Reject unsupported compressed formats before queue submission.
- [ ] Add Vulkan diagnostics for selected format, feature support, and upload byte counts.
- [ ] Validate a BC7 color texture on Vulkan if the backend path is stable enough.
- [ ] Validate unsupported-format fallback/error behavior.

## Phase 10: Mobile, KTX2, And Cross-Platform Profiles

**Goal:** prepare non-desktop targets after desktop BC is stable.

- [ ] Add ASTC profile definitions:
  - [ ] 4x4 high quality
  - [ ] 5x5 balanced
  - [ ] 6x6 memory saver
  - [ ] larger blocks for low-frequency textures only
- [ ] Add `astcenc` external-tool support after dependency/license review.
- [ ] Add ETC2/EAC profile definitions.
- [ ] Add KTX2 import support plan:
  - [ ] load KTX2 metadata
  - [ ] read Basis Universal payloads
  - [ ] transcode or cook to platform target profile
  - [ ] preserve source KTX2 as source authority
- [ ] Decide whether KTX2 is an import source only or an alternate cooked payload.
- [ ] Add cache keys for ASTC/ETC2/KTX2 profile selection.
- [ ] Add backend capability gates for ASTC and ETC2/EAC.
- [ ] Keep Windows desktop BC as the default profile.

## Phase 11: Streaming, Sparse, And Virtual Texturing Follow-Up

**Goal:** integrate compressed payloads with the broader texture streaming roadmap without destabilizing v1.

- [ ] Keep compressed sparse residency disabled until dense compressed upload is validated.
- [ ] Add compressed byte estimates to texture streaming budgets.
- [ ] Track logical decoded bytes, cooked stored bytes, upload bytes, and committed GPU bytes separately.
- [ ] Update resident-data reuse cache keys to include storage format and color space.
- [ ] Update texture streaming logs with compressed/uncompressed payload state.
- [ ] Validate tiered residency with compressed dense textures.
- [ ] Design compressed sparse full-mip residency separately from this TODO if needed.
- [ ] Defer page-addressable compressed payloads to the full SVT phase.
- [ ] Ensure compressed payload work does not regress existing `Rgba8` sparse streaming.

## Phase 12: Packaging, Documentation, And Closeout

**Goal:** make the feature maintainable and safe for users.

- [ ] Update feature docs for model/texture import behavior.
- [ ] Update texture streaming validation docs with compressed-cache scenarios.
- [ ] Document import settings and compression profiles.
- [ ] Document cache invalidation and manual recook behavior.
- [ ] Document dependency/tool setup if external encoders are used.
- [ ] Add troubleshooting notes:
  - [ ] missing encoder
  - [ ] unsupported GPU format
  - [ ] stale/corrupt cache
  - [ ] wrong normal-map convention
  - [ ] unexpected sRGB/linear result
- [ ] Run targeted builds:
  - [ ] `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore`
  - [ ] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
- [ ] Run targeted texture/import tests.
- [ ] Run editor smoke validation:
  - [ ] import PNG albedo
  - [ ] import PNG normal
  - [ ] import EXR/HDR if available
  - [ ] warm-cache reload
  - [ ] preview generated `.asset`
  - [ ] render material using compressed textures
- [ ] Record validation logs and screenshots in the validation ledger.
- [ ] Review remaining risks and create follow-up TODOs for unresolved mobile/KTX2/sparse/SVT work.
- [ ] Merge the dedicated branch back into `main` after implementation and validation are complete.

