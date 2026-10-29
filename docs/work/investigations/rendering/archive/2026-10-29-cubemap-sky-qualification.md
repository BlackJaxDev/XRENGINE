# HDR cubemap sky qualification

Date: 2026-10-29
Status: Live qualification and regression gates passed on a software adapter.

## Scope

Exercise the real `SkyboxComponent`, `XRTextureCube`, cooked cubemap shader,
perspective camera, lit foreground, and shared default pipeline. Each of the
six 4-by-4 RGBA16F faces encodes its face index in red, column in green, and row
in blue. Source alpha is 0.25. Nearest sampling and one authored mip isolate
face selection, orientation, raw half-float upload, and opaque sky output.

The shader and texture backend already implement this profile. This change
adds its live qualification and explicit fixture ownership. It does not change
desktop shaders or the frozen reference runtime.

## First live result

All ten cases render: negative Z, positive X, negative X, positive Y, negative
Y, positive Z, camera translation, authored sky rotation, doubled intensity,
and restoration. The six face red values are 0.25, 0.5, 0.75, 1, 1.25, and 1.5.
HDR reads preserve those values, including 3.0 at doubled intensity. Sky alpha
is one. The foreground retains linear RGBA `(0.132, 0.066, 0.033, 0.7)`.
Forward and upward camera captures were viewed; their row and column patterns
match the expected face orientation.

The first build reports a fixture cleanup delegate conversion error because
`XRTexture.Destroy` has an optional argument. An explicit zero-argument lambda
fixes the call. The next native-WASM publication succeeds.

## Repeatable checks

The gradient and cube profiles share frame settling, paused HDR reads, canvas
sampling, native-program retention, resize, and device restart checks. Each
profile keeps its own independent reference values and authored state.
The cube reference uses a dominant-axis face selection and face-local texel
coordinates. Each sample checks five 5-by-5 HDR regions and the corresponding
canvas regions. Non-square resize adds directions that cross into adjacent
faces. HDR tolerance is the larger of 0.002 and 0.2 percent. Canvas tolerance
is three byte values.

The cube checks also require one six-layer RGBA16F texture and one cube view,
with one mip and one sample. Numeric and camera changes retain managed program
handles. Resize retains native GPU programs and the authored texture/view.
Every settled case must drain retiring resources without live-count growth.

The first storage assertion counted all six-layer resources. The foreground's
standard lit material also owns a one-texel disabled point-shadow cube. The
captured resource records distinguish that `r16float` placeholder from the
4-by-4 `rgba16float` authored sky. The assertion now selects the sky's explicit
resource label and retains its exact count, format, topology, mip, and identity
requirements. No renderer change was needed.

The targeted run passes 24 cubemap samples over two sessions, including
640-by-384 and restored 512-by-512 output. All settled cubemap samples have
72 live resources and zero retiring resources. The authored texture/view and
native shader/pipeline identities remain unchanged through both resizes.
The same shared qualification passes all 18 existing gradient samples with 69 live
resources and zero retiring resources. The non-square cube capture was viewed.

## Regression results

The full BrowserSmoke run passes all 15 selected checks, including all 24 cube
and 18 gradient samples. Existing unlit, MSAA, indirect, GTAO, bloom, shadow,
overlay, world lifecycle, audio, and asset-delivery checks also pass. The bare
host's shipping-world autostart, optional published game, and separate Jolt
spike are skipped explicitly.

All 24 build and publish gates pass: four desktop builds, 19 portable builds,
and native-WASM browser publication. Builds report zero warnings and errors.
Fresh shader cooking packages 57 artifacts. Metadata and content cooking
package 112 assets, 57 shaders, and 14 pipeline passes. The separate published
bundle passes required-page, desktop-dependency exclusion, native-WASM, Jolt
license and pin, and browser restore-graph checks.

## Limits

This is software WebGPU qualification of a single-mip, nearest-filtered,
single-sample HDR cube. Filtered seams, authored mip chains, sRGB cube decoding,
panorama and octahedral projections, procedural controls, sky bloom,
transparency over sky, authored world serialization, and hardware/desktop
parity remain separate acceptance work. No whole shader requirement is closed.
