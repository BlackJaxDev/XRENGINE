# Saved sky texture limits

Date: 2026-10-29
Status: Implemented and qualified on software WebGPU; broader platform acceptance remains open.

## Gap and reproduction

The six published sky texture profiles previously cooked constructed objects
directly. They did not exercise native `.asset` saving and loading before cooking.
The fixture cooker now writes all six objects with `AssetManager.Serializer`,
releases them, and reads the saved files through `AssetManager.DeserializeAssetFile`
as the abstract `XRTexture` type. The saved header must restore the concrete type
and persistent identity. Canonical bytes must match the original object before
the cooker writes the `BinaryV2` envelope. Authored files stay in the cook output;
the browser package contains only the existing cooked payloads.

The first native round trip ruled out a suspected cube type loss: the saved
discriminator restored `XRTextureCube` and all six distinct face values. It did
expose lost zero LOD limits. The native serializer omits default values. Without
explicit property defaults, it treated integer zero as the default and omitted
`MinLOD: 0` and `MaxLOD: 0`. Reloading used the construction defaults of -1000 and
1000. Both cube fixtures changed six canonical payload bytes. The four image
fixtures use their existing explicit streamable payload and were unchanged.

`XRTexture` now declares the actual property defaults: -1000 for `MinLOD`, 1000
for `MaxLOD`, and 1000 for `SmallestAllowedMipmapLevel`. The last property has the
same omission risk; zero is now included in its regression cases. Constructor
defaults and binary formats do not change. Old files that omitted authored zeros
remain ambiguous. Correct values must be reauthored and saved; this change does
not recover lost values.

## Evidence

Before-fix cooked payloads decode to -1000/1000. Corrected saved payloads decode
to 0/0, and all six match the prior direct-cook bytes. The cooker now enforces
this equality for every run. Six new deterministic YAML cases cover defaults,
all zeros, each zero LOD clamp, and nonzero limits with all six face byte checks.
The focused suite passes all 66 cases, with no failures or skips. The native
cooker, probe, and headless test builds pass with zero warnings/errors.

All six saved files repeat byte-identically. Their cooked payloads repeat
identically and match the prior direct-cook bytes. All 24 build/publication gates
pass with zero compiler warnings/errors: four desktop Release builds, nineteen
portable BrowserWasm builds, and native WebAssembly publication. Fresh official
Slang cooking produces 57 shader artifacts; the package has fourteen passes and
118 assets. Metadata verification, the compiled closure scan, and bundle checks
pass.

All 226 targeted texture samples pass: 24 HDR cube, 86 filtered cube, 24 per HDR
image, and 34 per filtered image. Each profile retains one asset and one object,
with exact native pixel estimates of 768/504/256/512/172/340 bytes respectively.
Both stop cycles leave no asset sources. Selected HDR cube, final filtered mip,
and octahedral captures were viewed.

The full run passes all 23 selected browser checks in Chromium 154.0.8037.57 with
Playwright 1.63.0 and explicit software SwiftShader. Ten sky profiles produce 324
samples with their expected live GPU resources and zero retiring resources.
All 24 strict indirect lifecycle/resize stages pass. Three optional checks are
skipped explicitly: authored engine autostart, the optional game package, and
the standalone Jolt spike. Final host and page bytes match the tested source.
The metadata scan finds seventeen assemblies, 8,474 types, 42 transforms,
fifteen redirects, no replication entries, and twelve registered asset types.
Source-retention estimates do not measure immediate native heap reclamation.

## Remaining boundaries

The saved global `WorldSettings.SkyboxTexturePath` is not bound to a runtime
`SkyboxComponent.Texture` by this change. Inspection found no shared renderer
reader that defines this binding. Adding an arbitrary component binding would
invent semantics. External world environment dependencies remain separate work.

This fixture route does not establish packaged Editor publication, AOT texture
decoding, platform texture compression, hardware acceptance, complete shader
coverage, implicit/fractional LOD, anisotropy, or imported-image quality. The
reference runtime and separate unpublished manifest work remain unchanged.

Disposable evidence: `Build/_AgentValidation/20261029-175406-saved-sky/`.
