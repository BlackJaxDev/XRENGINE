# Sky texture storage admission

Date: 2026-10-29
Status: Live admission, targeted tests, build gates, and software browser regression passed.

## Defect and live reproduction

The Editor browser world audit checked the sky projection, texture topology,
format, and automatic mip flag. It did not check whether the authored CPU pixels
could be published and uploaded. A live call to `BrowserWorldCapabilityAudit`
accepted both a valid 2-by-2 image and the same image with no pixel payload.
The latter can fail later during upload or leave image contents undefined.

`BrowserSkyTextureCookProfile` now checks static CPU storage before the audit
looks up the sky shader variant. A live call to the updated Editor assembly
still accepts the valid image and rejects the missing payload with the node
path, texture identity, and `BrowserCook.SkyTextureStorageUnsupported` detail.
This validation ran before the regression tests were added.

## Admission rules

- Accept exact 2D or cube texture sources and the existing admitted sky formats.
- Require a nonempty natural mip chain with resident pixels. Partial chains are
  valid; repeated terminal mips are not.
- Require six square cube faces at every level, with matching dimensions.
- Require RGBA unsigned-byte data for RGBA8 and sRGB RGBA8, and RGBA half-float
  data for RGBA16F. Float RGBA source data is valid only for cubes, whose layered
  upload backend performs the float-to-half conversion.
- Reject a wrong byte length, null address, streaming buffer, GPU-written source,
  external-memory source, and sRGB storage-image usage.
- Reuse `PublishedStandardLitTextureSettings.Capture` for static 2D source rules,
  including sparse, grab-pass, active-upload, and settings restrictions.

The check reads metadata only. It does not copy or mutate source pixels, generate
mips, or add work to the render loop. Linear storage-image usage remains valid
when the source otherwise meets the static CPU contract.

## Validation and failed attempts

The first scratch harness used an unsuitable Windows bootstrap reference and an
incorrect command argument shape. The portable host reference and array-valued
build arguments fixed those harness errors. A later Editor build found a C#
pattern-variable name collision; the binding names were corrected.

The first regression run omitted the WebGPU material-construction target, so sky
setters attempted to load desktop shaders. The fixture now installs and restores
the same cooking target used by the live audit. Review also corrected the cube
round-trip test to use the canonical publisher serializer with MemoryPack
suppressed. The simple runtime texture serializer does not admit cubes.

The corrected run passed 39 of 40 cases and exposed a production serialization
defect. Canonical cube cooking suppresses MemoryPack. Its reflection path skips
`Data` because of `MemoryPackIgnore` and skips `DataBytes` because of `YamlIgnore`.
The cloned cube therefore had dimensions but no face pixels. The 2D round trip
passed because `XRTexture2D` owns a separate custom mip carrier.

`Mipmap2D.CookedBinary.cs` now uses the existing runtime custom-value interface.
Its versioned payload stores width, height, three pixel-format fields, and a
nullable length-prefixed byte array. It rejects an unknown version or invalid
length before mutation. It preserves null and empty data separately and imposes
no sky-only format restrictions. Rendering serialization registers both required
constructors. Legacy reflection reads, YAML, MemoryPack, and the existing 2D
streamable wire remain unchanged; lost pixels in old payloads cannot be restored.
A live Editor audit accepts a canonically cooked-and-loaded cube after this fix.

The final targeted run passes 58 tests: 47 sky-storage/mip-payload cases and
11 existing 2D cooked-sampling cases. The new cases include canonical image and
cube round trips, byte identity, partial chains, malformed face data, GPU/external
state, format-specific storage usage, null/empty data, unknown payload versions,
invalid lengths, and canonical decoding in `PublishedAot` mode with explicit
constructor/type registrations. The published-mode test runs on the desktop CLR;
it does not establish a native AOT device or browser cube-asset draw.

The first published test used an uppercase metadata hash, then tried to reset an
immutable browser table. Those were harness errors. The final test uses the
stricter published constructor/type-registration path without installing a
process-wide browser table. No metadata checks were changed.

All 24 final build/publication gates pass: four desktop Release builds, 19
portable BrowserWasm builds, and native-WASM publication. Three refreshed cooking
and audit tool builds also pass. They report zero compiler warnings and errors.
Metadata verification and the compiled browser-closure scan pass. Fresh official
Slang cooking emits 57 shaders; the final recipe contains 112 assets and 14 passes.
Bundle checks pass and the published diagnostic host/page bytes match source.

The final Chromium/SwiftShader regression passes all 23 selected checks with three
explicit skips: published-engine autostart, the optional game, and the Jolt spike.
Ten sky profiles pass 324 samples with stable live resources and no retiring
resources. All 24 indirect lifecycle stages retain settled preparation and strict
ownership checks. The filtered cube resize capture was viewed. This is a software
adapter result; the existing runtime sky fixtures do not prove browser loading
of the newly cooked cube asset. No comparison or ownership check was weakened.

Disposable logs, build output, and test reports are under
`Build/_AgentValidation/20261029-155520-sky-cook-storage/`.

## Remaining scope

This change does not supply ASTC/ETC2 encoders, platform target selection, device
limit qualification, path-only sky dependency resolution, complete packaged
Editor publication, or full shader coverage. It does not establish exact moon
noise parity, other procedural sky terms, or hardware acceptance. The integrated
cooking and shader requirements remain open. Separate unpublished manifest work,
frozen reference runtime, other worktrees, and existing setup files are preserved.
