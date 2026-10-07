# Texture Compression And Cooked Texture Cache TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Cooked Texture Payloads](../../../architecture/assets/cooked-texture-payloads.md)  Design: [Texture Compression And Cooked Texture Cache Design](../../design/texturing/texture-compression-and-cooked-cache-design.md)  Validation: [Texture Validation](../../testing/texturing/texture-validation.md)

## Current State

`XRTexture2D` can write and read binary `XRTS` payloads through `XRTexture2D.StreamingPayload.cs`, and `TextureStreamingCacheCodec` uses a v3 uncompressed `Rgba8` preview cache key. `EPixelInternalFormat` has BC, ETC2/EAC, and ASTC enum values, but cooked payloads still store uncompressed mip bytes. Texture role, color-space, compression-profile, and normal-convention metadata remain incomplete. OpenGL and Vulkan dense compressed texture upload are not implemented.

## Open Code Items

### Metadata and import contract

- [ ] Add texture role metadata for unknown, albedo/base color, normal/bump, roughness, metallic, occlusion/AO, packed mask/ORM/RMSE, emissive, height/displacement, HDR environment, UI color, and UI mask/font/SDF. Files or types: `XRTexture`, texture import settings, material import code. Done when import and diagnostics carry a stable role value.
- [ ] Add color-space metadata for unknown, linear, sRGB, and HDR linear. Files or types: `XRTexture.ImportedColorSpace`, import settings, cache manifest. Done when cache keys and imports preserve the selected color space.
- [ ] Add compression profile metadata for none/uncompressed, desktop high quality BC, desktop memory saver BC, mobile ASTC, mobile ETC2/EAC, and KTX2/Basis source or interchange. Files or types: texture import settings, cache manifest, diagnostics. Done when profile changes affect the cache key.
- [ ] Add normal-map convention metadata for OpenGL/Y+, DirectX/Y-, explicit green flip, and unknown. Files or types: import settings, material import code, shader sampling metadata. Done when normal maps record storage and reconstruction semantics.
- [ ] Add import-option fields for role, color space, compression profile, normal convention, alpha mode, and mip policy. Files or types: import UI and texture importer. Done when explicit user settings override auto-detection.
- [ ] Implement role auto-detection from material sampler name, material slot name, filename suffix, and `.exr` or `.hdr` source extension. Files or types: material and texture importers. Done when deterministic unit tests cover role and color-space detection.
- [ ] Update the ImGui third-party texture import selection to show resolved role, color space, and compression profile. Files or types: editor ImGui texture import UI. Done when a user can see and override the resolved values.
- [ ] Update texture diagnostics to log role and color space when loading or writing caches. Files or types: `TextureRuntimeDiagnostics`, cache write path. Done when `log_textures.*` rows include these fields.

### Metadata-only generated texture assets

- [ ] Define a generated texture asset metadata shape with source identity, role, color space, compression profile, payload format/version/path, cache key, GPU format, dimensions, and mip count. Files or types: `XRTexture2D`, generated asset serializers. Done when generated assets no longer need large inline payloads.
- [ ] Decide whether payload references are cache-root-relative or project-root-relative. Files or types: generated asset serializer, asset loader. Done when the path policy is documented and tests cover moved project roots.
- [ ] Keep `XRTexture2DYamlTypeConverter` cooked payload reading for legacy assets. Files or types: YAML converter. Done when legacy YAML `Format: CookedBinary` assets still load.
- [ ] Prefer a fresh cooked payload when loading a generated texture asset. Files or types: asset loader, `AssetTextureStreamingSource`. Done when fresh metadata assets load from the payload without source decode.
- [ ] Fall back to source import in the editor when a payload is missing or stale and the source exists. Files or types: asset loader, diagnostics. Done when the fallback logs the reason.
- [ ] Add a runtime and published-build policy for missing payloads. Files or types: asset loader, runtime diagnostics. Done when runtime fails with diagnostics unless an explicit fallback is configured.
- [ ] Add metadata-only texture asset round-trip tests and missing/stale payload fallback tests. Files or types: `XREngine.UnitTests`. Done when metadata, editor fallback, and runtime failure behavior are covered.
- [ ] Add ImGui inspector fields for payload path, cache key, and freshness status. Files or types: editor inspector. Done when the inspector shows payload state.
- [ ] Preserve texture preview for metadata-only assets. Files or types: preview generation and editor UI. Done when preview works without inline mip bytes.

### `XRTS` payload schema and cache keys

- [ ] Add `XRTS` v2 constants and version dispatch. Files or types: `XRTexture2D.StreamingPayload.cs`. Done when v1 and v2 readers dispatch by version.
- [ ] Add a v2 header with payload version, flags, texture role, color space, storage format, data encoding, block geometry, source dimensions, mip count, preview base mip index, encoder id/version, and optional quality metrics. Files or types: payload structs. Done when the header can be read without hydrating mips.
- [ ] Add v2 mip descriptors with mip index, logical dimensions, storage format, data encoding, block dimensions, row pitch, slice pitch, data offset, data length, and optional checksum. Files or types: payload structs. Done when descriptor validation catches invalid block layout.
- [ ] Implement v2 uncompressed writer and reader before compressed bytes. Files or types: `XRTexture2D.StreamingPayload.cs`. Done when runtime streaming can read resident mip ranges from v2.
- [ ] Keep v1 reader compatibility. Files or types: `XRTexture2D.StreamingPayload.cs`. Done when old cache payloads still load.
- [ ] Add metadata-first manifest reads and resident mip range reads for v2. Files or types: streamability check and payload reader. Done when preview and promotion requests read only needed data.
- [ ] Add unit tests for NPOT descriptors, final 1x1 mips, bad magic, unsupported version, invalid offset, invalid length, truncated payload, unsupported storage format, and cache freshness. Files or types: `XREngine.UnitTests`. Done when the reader rejects corrupt payloads and key freshness is deterministic.
- [ ] Update cache logging to distinguish `XRTS v1`, `XRTS v2 uncompressed`, and future `XRTS v2 compressed`. Files or types: `TextureRuntimeDiagnostics`. Done when logs name the payload schema.
- [ ] Define the v4 texture cache variant key with schema version, source dimensions/hash mode, role, color space, compression profile, backend profile, storage format, encoder id/version, encoder settings hash, mip policy, normal convention, and alpha mode. Files or types: `TextureStreamingCacheCodec`. Done when all inputs are present in a stable key builder.
- [ ] Add cache miss reasons for missing, source newer, import options newer, unsupported schema, unsupported backend format, source hash mismatch, encoder version mismatch, role/color-space mismatch, corrupt payload, and user-forced reimport. Files or types: cache diagnostics. Done when exactly one primary reason logs per miss.
- [ ] Add ImGui diagnostics and tests for cache miss reason, selected fallback, changed settings, and unchanged stable keys. Files or types: texture streaming panel, import UI, `XREngine.UnitTests`. Done when key behavior is visible and covered.

### Desktop BC cooking

- [ ] Choose the initial encoder path. Decide between an external `texconv`/DirectXTex executable and an in-process DirectXTex wrapper. Owner: Rendering.
- [ ] Complete dependency and license review before adding or vendoring an encoder. Files or types: dependency manifests and legal docs. Done when the selected encoder is approved.
- [ ] Add encoder configuration for executable path or integration mode, quality preset, concurrency cap, timeout, and output directory policy. Files or types: settings, import UI, cooker. Done when compression is bounded and cancelable.
- [ ] Add BC cooking for albedo/base color. Files or types: texture cooker. Done when BC7 sRGB is default, BC1 sRGB is available for opaque memory-saver output, and BC3 sRGB is available when BC7 is unsupported.
- [ ] Add BC cooking for normal maps. Files or types: texture cooker and shader metadata. Done when normals store XY, optional green flip applies, output uses BC5, and convention is recorded.
- [ ] Add BC cooking for masks and HDR textures. Files or types: texture cooker. Done when BC4, BC5, BC7 packed RGBA, and BC6H choices are supported.
- [ ] Make source alpha mode, sRGB rules, and linear scalar rules affect selected format. Files or types: format selection code. Done when color data uses sRGB and normal/scalar data uses linear formats.
- [ ] Add first-import progress, cancellation, and bounded compression concurrency. Files or types: import UI and cooker scheduler. Done when the editor stays responsive during compression.
- [ ] Validate cook output in code. Files or types: cooker validation. Done when block dimensions, mip count, data length, and selected format are checked before cache write.
- [ ] Add import tests using tiny fixture textures where possible. Files or types: `XREngine.UnitTests`. Done when fixtures exercise albedo, normal, mask, and HDR paths where samples exist.

### OpenGL and Vulkan compressed upload

- [ ] Add OpenGL compression capability detection for S3TC/BC1-BC3, RGTC/BC4-BC5, BPTC/BC6H-BC7, ETC2/EAC, and ASTC. Files or types: OpenGL renderer capabilities. Done when renderer logs supported formats.
- [ ] Map cooked storage formats to GL compressed internal formats. Files or types: OpenGL texture format conversion. Done when each supported payload format maps to a GL format.
- [ ] Add a compressed upload branch for `XRTexture2D` dense textures. Files or types: `GLTexture2D`. Done when compressed mips use `glCompressedTexImage2D` or `glCompressedTexSubImage2D`.
- [ ] Use immutable storage where compatible and validate block-aligned byte lengths before GL calls. Files or types: `GLTexture2D`. Done when invalid lengths fail before driver calls.
- [ ] Handle NPOT final mips correctly. Files or types: OpenGL upload validation. Done when final block extents are correct.
- [ ] Disable row-chunk progressive upload for compressed blocks until block-row chunking exists. Files or types: upload scheduler. Done when compressed upload does not split on invalid row boundaries.
- [ ] Record compressed upload bytes separately from logical decoded bytes. Files or types: texture telemetry. Done when diagnostics show both values.
- [ ] Keep the uncompressed upload path unchanged and add fallback diagnostics when selected compression is unsupported. Files or types: `GLTexture2D`, diagnostics. Done when existing `Rgba8` tests still pass.
- [ ] Add Vulkan texture compression feature detection for `textureCompressionBC`, `textureCompressionETC2`, and `textureCompressionASTC_LDR`. Files or types: Vulkan capability probing. Done when backend-visible capabilities are exposed.
- [ ] Add a renderer-visible texture capability profile. Files or types: renderer capability interfaces. Done when import and streaming can query supported compression.
- [ ] Extend `VkFormatConversions` with BC, ETC2/EAC, and ASTC formats. Done when conversions are complete.
- [ ] Validate sampled image and filtering support with physical-device format properties. Files or types: Vulkan format selection. Done when unsupported sampled formats are rejected before use.
- [ ] Allocate `VkImage` with compressed `VkFormat` and add staging-buffer upload for compressed mip byte ranges. Files or types: Vulkan texture creation and upload service. Done when compressed bytes upload without decoding.
- [ ] Make `VkBufferImageCopy` extents and offsets follow compressed block rules. Files or types: Vulkan upload validation. Done when NPOT final mips and block extents are valid.
- [ ] Reject unsupported compressed formats before queue submission and log selected format, feature support, and upload byte counts. Files or types: Vulkan diagnostics. Done when failures are diagnostic and no bad command is submitted.

### Material sampling and mip quality

- [ ] Add shader and material metadata for normal-map XY storage, BC5 normal Z reconstruction, and normal scale after reconstruction. Files or types: material descriptors and shaders. Done when compressed normal maps shade correctly.
- [ ] Enforce sampler color space by role. Files or types: material import and sampler setup. Done when normals and scalar data are linear and color/emissive data uses sRGB where appropriate.
- [ ] Add material import remap support for packed masks. Files or types: material importer. Done when packed channels map to expected material fields.
- [ ] Add role-aware fallback textures for albedo, flat normal, neutral roughness/AO, zero metallic, and zero emissive. Files or types: default textures and material binding. Done when missing textures bind role-correct fallbacks.
- [ ] Add a validation material set that exercises each role. Files or types: assets or test scene. Done when validation can compare compressed and uncompressed output.
- [ ] Generate color mips in linear light. Files or types: mip generator. Done when color mip generation converts through linear space.
- [ ] Add coverage-preserving alpha mips and premultiplied-alpha mip handling where material alpha mode requires it. Files or types: mip generator. Done when alpha coverage stays stable.
- [ ] Add normal-aware mip generation that decodes normal vectors, averages vectors, renormalizes, and encodes XY. Files or types: mip generator. Done when normal mips preserve orientation.
- [ ] Add roughness, mask, and HDR linear mip generation. Files or types: mip generator. Done when scalar and HDR mips use the correct math.
- [ ] Record the mip-generation policy version in the cache key. Files or types: `TextureStreamingCacheCodec`. Done when policy changes invalidate caches.
- [ ] Add tests for normal mip orientation and alpha coverage preservation. Files or types: `XREngine.UnitTests`. Done when known fixtures stay within tolerance.
- [ ] Add cook-time quality metrics where practical. Files or types: cooker diagnostics. Done when color, normal, scalar, and alpha metrics are recorded.

### Mobile, KTX2, streaming integration, and docs

- [ ] Add ASTC profile definitions for 4x4 high quality, 5x5 balanced, 6x6 memory saver, and larger low-frequency blocks. Files or types: compression profile settings. Done when ASTC profiles are selectable.
- [ ] Add `astcenc` external-tool support after dependency and license review. Files or types: cooker tooling. Done when ASTC payloads can be cooked.
- [ ] Add ETC2/EAC profile definitions. Files or types: compression profile settings. Done when ETC2/EAC profiles are selectable.
- [ ] Add a KTX2 import support plan in code-facing design. Cover metadata load, Basis payload read, transcode or cook to platform target, and source authority. Done when the implementation plan is ready for code.
- [ ] Decide whether KTX2 is an import source only or an alternate cooked payload. Owner: Assets and Rendering.
- [ ] Add cache keys and backend capability gates for ASTC, ETC2/EAC, and KTX2 profile selection. Files or types: `TextureStreamingCacheCodec`, OpenGL and Vulkan capability profiles. Done when profile changes invalidate the cache and unsupported targets diagnose or fall back.
- [ ] Keep Windows desktop BC as the default profile. Files or types: profile selection. Done when desktop imports choose BC unless overridden.
- [ ] Keep compressed sparse residency disabled until dense compressed upload is validated. Files or types: residency policy and capability gates. Done when compressed sparse paths cannot activate accidentally.
- [ ] Add compressed byte estimates and separate logical decoded, cooked stored, upload, and committed GPU bytes. Files or types: texture telemetry and policy. Done when each byte class is named.
- [ ] Add storage format and color space to resident-data reuse cache keys. Files or types: `TextureStreamingResidentDataReuseCache`. Done when incompatible prepared data cannot be reused.
- [ ] Update texture streaming logs with compressed or uncompressed payload state. Files or types: `TextureRuntimeDiagnostics`. Done when logs identify payload encoding.
- [ ] Validate tiered residency with compressed dense textures. Files or types: OpenGL and Vulkan dense residency backends. Done when compressed dense textures stream without sparse residency.
- [ ] Design compressed sparse full-mip residency separately if needed. Owner: Rendering.
- [ ] Defer page-addressable compressed payloads to full streaming virtual texture work. Files or types: architecture docs. Done when no v1 code depends on page-addressable payloads.
- [ ] Prevent compressed payload work from regressing existing `Rgba8` sparse streaming. Files or types: source-contract tests. Done when existing sparse streaming tests remain valid.
- [ ] Update feature docs for model and texture import behavior, settings, compression profiles, cache invalidation, manual recook, dependency/tool setup, and troubleshooting. Files or types: feature, user, or developer docs. Done when users can diagnose missing encoder, unsupported GPU format, stale/corrupt cache, wrong normal-map convention, and unexpected sRGB/linear results.
- [ ] Create follow-up code items for unresolved mobile, KTX2, sparse, or SVT work that remains after desktop compressed payload support lands. Files or types: texturing TODOs. Done when deferred work has clear owners.

## Decisions Needed

- [ ] Choose the first desktop BC encoder path. Owner: Rendering.
- [ ] Choose cache-root-relative or project-root-relative payload references. Owner: Assets.
- [ ] Decide whether KTX2 is an import source only or an alternate cooked payload. Owner: Assets and Rendering.
- [ ] Decide whether compressed sparse full-mip residency belongs in this TODO or a separate backend TODO. Owner: Rendering.

## Out Of Scope

- Running editor smoke checks, hardware validation, screenshots, profiler runs, and build logs. These belong in [Texture Validation](../../testing/texturing/texture-validation.md).
- Adding unreviewed compression or training dependencies.
- Full page-addressable SVT payloads.
- Neural material compression shader decode.
