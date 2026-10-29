# Published image sky loading and decoder ownership

Date: 2026-10-29
Status: Implemented and qualified on software WebGPU; broader platform acceptance remains open.

## Gap and reproduction

Cube sky diagnostics loaded cooked package assets, but image profiles still
created pixels inside the browser. Four image fixtures now move to the existing
metadata cooker: HDR equirectangular and octahedral images, and their filtered
sRGB variants. Stable identities and canonical `BinaryV2` envelopes preserve
the existing texture payload. No new image format is introduced.

The browser selects each asset by its exact virtual path, installs verified
metadata and asset services, and loads it through
`Engine.Assets.LoadFromRuntimeSourceAsync` in `Published` interpreter mode.
The shared sky fixture borrows the asset from the source. The browser no longer
generates the image pixels. Diagnostic GPU labels follow the production loader's
file-derived names.

The first live HDR panorama rendered all ten camera and intensity cases. It
retained one asset but two objects and 259 pixel bytes; the authored image has
only 256 bytes. The other profiles also rendered: ten HDR octahedral cases and
fifteen cases per filtered profile. Each retained two objects and three extra
bytes: 515, 175, and 343. HDR and filtered-mip captures were viewed.
Review identified unconditional construction in canonical
`ReadCustomObject`: it instantiated a runtime-custom type to check the legacy
interface, then called the runtime reader to instantiate it again. The unused
texture's default RGB mip added one owned object and three bytes.

The reader now checks legacy interface assignability before construction. Both
legacy and runtime wire branches remain unchanged. This removes the unused
object without changing the ownership ledger to hide it.

## Validation and limits

The intended retained image pixels are 256 and 512 bytes for the HDR profiles,
and 172 and 340 bytes for the four-level sRGB profiles. All 50 corrected live
cases passed with one retained object and those exact byte counts. The
regression suite now checks each published path, drained staging, source teardown,
and the unchanged numeric/mip contracts. Two decoder tests cover single images
and shared image references. All 60 focused texture tests pass, including those
two cases and the existing storage/sampling tests. Repeated cooking produces
byte-identical payloads for all six textures; cube payload hashes are unchanged.
All 24 build/publication gates pass with zero compiler warnings/errors. The
fixture cooker and headless test builds also pass. The final targeted image
suite passes 116 samples: 24 for each HDR profile and 34 for each filtered
profile. Every sample retains 72 live GPU resources and zero retiring resources.
Each profile leaves zero asset sources after both stops. Final HDR direction and
filtered-mip captures were also viewed from the qualified bundle.

Full browser regression passes all 23 selected checks in Chromium 154.0.8037.57
with Playwright 1.63.0 and explicit SwiftShader. Ten sky profiles produce 324
samples with their expected live resources and zero retiring resources. All 24
strict indirect lifecycle/resize stages pass. Three optional checks are skipped
explicitly: published engine autostart, the optional game package, and the
standalone Jolt spike. Final host and page bytes match the tested source.

Fresh official Slang cooking produces 57 shader artifacts. The final package
contains 118 assets and fourteen pipeline passes. Metadata verification, the
compiled closure scan, and bundle checks pass. The scan finds seventeen
assemblies, 8,474 types, 42 transforms, fifteen redirects, no replication entries,
and twelve registered asset types. This remains software-adapter evidence. Source-retention estimates
do not measure allocator overhead or immediate native heap reclamation.

The first targeted scratch runner completed both HDR profiles, then stopped on
a missing import for the filtered checks. The runner import was corrected;
production code and render assertions did not change.

This route uses canonical reflection loading in the browser interpreter. It does
not establish an AOT image codec, packaged Editor export, platform compression,
path-only external sky dependencies, imported-image quality, or hardware
acceptance. Shader limits for fractional/implicit LOD, anisotropy, procedural
terms, and complete coverage remain open. The reference runtime and separate
unpublished manifest work remain unchanged.

Disposable evidence: `Build/_AgentValidation/20261029-172305-cooked-images/`.
