# Published cube sky loading

Date: 2026-10-29
Status: Implemented and qualified on software WebGPU; broader platform acceptance remains open.

## Gap and implementation

The prior storage-admission change retained cube pixels through canonical
serialization. Its browser sky fixtures still generated their cubes locally.
That left package delivery, published type resolution, async hydration, and
rendering of those cooked bytes unverified.

`BrowserSkyTextureFixtures` now creates the same HDR and sRGB cube data in the
metadata fixture cooker. It assigns stable asset identities and writes canonical
`BinaryV2` envelopes with MemoryPack suppressed. The engine-assets recipe adds
`/game/SkyCubeHdr.asset` and `/game/SkyCubeFiltered.asset`. The packager retains
its existing hash and dependency checks.

The cube diagnostic installs hash-verified metadata and selects `Published`
mode. It installs catalog providers before asset services so the lazy asset
manager adopts the correct virtual roots. It then loads the selected cube with
`Engine.Assets.LoadFromRuntimeSourceAsync` and passes it to the real sky component.
The browser fixture no longer generates cube pixels. It borrows the loaded asset;
the asset source owns its lifetime. Cancellation is checked after each await.

A repeated cook produced byte-identical payloads: 4,428 bytes for the HDR cube
and 7,858 bytes for the filtered cube. Their stable identities and content hashes
remain fixed across those two runs.

No new cube wire format or codec is needed for this interpreter path. Canonical
`BinaryV2` reflection is available in `Published` mode; it is rejected by
`PublishedAot`. Nested mip pixels use the existing explicit `Mipmap2D` payload.
The frozen reference runtime and separate unpublished manifest-validator work
are unchanged.

## First live evidence

Before changing regression assertions, the HDR cube rendered all ten camera,
rotation, and intensity cases. The report identified `cooked-asset`, `Published`,
and `Cubemap`. Captures from negative Z and positive Y were viewed. The source
retained one asset/object and 768 pixel bytes: six 4-by-4 half-float RGBA faces.

The sRGB cube then rendered all 41 cases. These include face interiors, twelve
two-face edges at two mip levels, final-mip selection, camera changes, and HDR
intensity. Final-mip and edge captures were viewed. It retained 504 pixel bytes:
six faces times (16 + 4 + 1) texels times four bytes.

The native estimate previously counted 2D texture and buffer sources but omitted
cube faces. It now counts distinct owned cube-face buffers across every mip.
Absent mip data adds no bytes; this accounting does not replace cook validation.
These values describe retained source buffers, not allocator overhead or immediate
native heap reclamation. Existing pixel-buffer finalization behavior is unchanged.

## Regression contract

The two existing cube checks retain their independent HDR/canvas references,
format/extent checks, GPU resource identities, and tolerances. New assertions
require the exact asset path, `Published` mode, one retained asset/object, exact
native estimates, drained staging, and zero surviving asset sources after stop.
The final targeted run passes 24 HDR samples and 86 filtered samples across two
sessions and resize. Both sessions leave zero asset sources after stop. Final
HDR-face and filtered-edge captures were also viewed from the qualified bundle.

The first fixture-cooker build tried to assign the read-only identity property.
It was corrected to use `AdoptPersistentID`. An early recipe preparation ran
before shader cooking finished; it was rerun after all 57 artifacts were ready.
Review caught asset-service installation before provider installation. That
ordering was fixed and republished before the first live probe. No runtime
failure was concealed by generating fallback cube data. The first regression run
then found the old runtime-only texture labels. `AssetManager.PostLoaded` names
loaded assets from their paths. The checks now expect `SkyCubeHdr` and
`SkyCubeFiltered`, with the same format, mip, numeric, and resource checks.

All 24 final gates pass with zero compiler warnings/errors: four desktop Release
builds, nineteen portable BrowserWasm builds, and native-WASM publication. The
fixture-cooker build passes. Fresh official Slang cooking produces 57 artifacts;
the package contains 114 assets and fourteen pipeline passes. World metadata
verification and the compiled browser-closure scan pass. The closure scan finds
seventeen assemblies, 8,474 types, 42 transforms, fifteen redirects, no replication
entries, and twelve registered asset types. Bundle checks pass.

Full regression passes all 23 selected checks in Chromium 154.0.8037.57 with
Playwright 1.63.0 and explicit SwiftShader. Ten sky profiles produce 324 samples;
24 strict indirect lifecycle/resize stages pass. Cube samples retain 72 live GPU
resources for HDR and 30 for filtered cubes, with zero retiring resources. The
other sky profiles also retain their expected counts. Both cube sessions release
their asset sources. Three optional checks are skipped explicitly: published
engine autostart, the optional game package, and the standalone Jolt spike.
The tested host and page bytes match the final source. This is software-adapter
evidence, not hardware qualification.

Disposable evidence is under
`Build/_AgentValidation/20261029-164615-cooked-cube/`.

## Limits

This establishes an authored fixture through canonical cooking, immutable package
delivery, production async loading, and the shared renderer on a software adapter.
It does not establish a packaged Editor export, external sky-path dependency
resolution, imported image quality, ASTC/ETC2 encoding, AOT decoding, or hardware
acceptance. Other sky images still use runtime-authored fixtures. Fractional and
implicit LOD, three-face cube corners, remaining procedural terms, and complete
shader and platform cooking coverage remain open. Requirement totals are unchanged.
