# Filtered sRGB sky image qualification

Date: 2026-10-29
Status: Live qualification and regression gates passed on a software adapter.

## Scope and authored data

The equirectangular and octahedral profiles use the real `SkyboxComponent`,
`XRTexture2D`, cooked shaders, and shared `DefaultRenderPipeline`. The panorama
starts at 8 by 4 texels; the octahedral image starts at 8 by 8. Both supply four
authored RGBA8 mip levels down to 1 by 1, stored as `rgba8unorm-srgb`.
Automatic mip generation is disabled.

Each mip encodes its column, row, and level in distinct byte values. Source
alpha is 96. Minification, magnification, and mip filtering are linear, with
anisotropy one. The panorama repeats U and clamps V; the octahedral image clamps
both axes. Each case pins `MinLOD` and `MaxLOD` to the same integer level.
This tests exact mip selection without assumptions about implicit derivatives.

Fifteen cases cover all six camera directions, translation, authored rotation,
intensity, restoration, each mip level, combined level/rotation/intensity, and
restoration to level zero. Two sessions each run those cases, resize to 640 by
384, and restore 512 by 512. Each projection has 34 samples.

The images deliberately contain discontinuities. This checks the authored
repeat/clamp sampling rules; it does not construct a continuous environment
image or remap neighbors across octahedral edges. These are programmatically
authored raw mip bytes, not Editor-published texture assets. The integrated
platform cook remains a separate open requirement.

## First live result

Both projections rendered all fifteen cases. Their actual texture resources
report the authored extents, sRGB format, four mip levels, one layer, and one
sample. The same texture handle remains through every mip change. Panorama
seam and lower-hemisphere octahedral captures were viewed.

Changing the authored LOD replaces the sampler. The backend releases dependent
draws, so the selected sky's managed pipeline handle changes. Its native pipeline
object remains the same. The sky shader and unrelated pipeline handles also
remain the same. This is the existing ownership contract, not a new renderer
lifetime policy or a shader change.

## Numeric and resource contract

The reference reuses the independent camera-ray projection equations from the
nearest-filtered sky profiles. It computes texel-center coordinates, wraps both
panorama U neighbors, and clamps the other neighbor indices. It decodes each
sRGB texel into linear RGB before bilinear interpolation. The shader's authored
intensity then scales that linear result. Sky alpha must be one despite source
alpha 96. Foreground depth and color retain their existing known values.

Each sample compares five 5-by-5 HDR and tonemapped canvas regions. HDR tolerance
is the larger of 0.002 and 0.2 percent; canvas tolerance is three byte values.
Both profiles pass all 34 targeted samples without a tolerance increase.
The largest endpoint error is about 0.124 times the allowed tolerance for the
panorama and 0.220 for the octahedral image. The measured HDR maxima are
0.806640625 and 1.1943359375 respectively.

Each settled sample has 72 live resources and zero retiring resources. Texture
storage and all native program identities remain stable through sampler changes
and resize. At an authored LOD transition, only the selected sky's managed
pipeline handle may change. Full shader handles and unrelated pipeline handles
still have to match. Ordinary camera and intensity changes keep the original
exact handle check. Other sky profiles keep their existing checks and schedules.
Sampled-view identity is not separately measured by this texture inventory.

Panorama pole and non-square octahedral restoration captures were viewed.
The existing shaders and shared texture backend pass without a production
rendering change. Desktop GLSL, the frozen reference runtime, and separate
manifest work remain unchanged.

## Build and packaging results

All 24 build and publish gates pass: four desktop Release builds, 19 portable
browser-wasm builds, and native-WASM publication. Project builds report zero
warnings and errors; publication has no compiler warning/error diagnostics.
The pinned official Slang 2026.8 toolchain cooks all 57 selected shader artifacts.
The content recipe packages 112 assets and 14 pipeline passes. Runtime metadata
verification passes.

The final bundle passes required-page, native-WASM, Jolt notice and pin,
desktop-dependency exclusion, and browser restore-graph checks. Published
diagnostic host assets match source. Full browser regression runs against that
exact bundle with Chromium 154.0.8037.57, Playwright 1.63.0, and explicit
SwiftShader WebGPU.

All 21 selected checks pass. Eight sky profiles pass 210 samples, including
34 samples for each filtered image projection. The extracted coordinate reference
also passes both existing nearest-filtered image profiles. All 24 indirect
lifecycle/resize stages pass with settled preparation and strict ownership.
Bare-host shipping-world autostart, the optional published game, and the separate
Jolt spike are skipped explicitly.

## Remaining acceptance

Implicit LOD selection, fractional trilinear blends, automatic mip generation,
anisotropic filtering, filtering across octahedral seams, filtered cubemap
edges and cube mip/color-space cases remain unqualified. These small synthetic
images do not establish imported environment-image quality, serialization,
physical-device behavior, or desktop pixel parity. Procedural clouds, stars,
moon-disc shading, automatic cycling and lighting synchronization, sky bloom,
and sorted transparency also remain open. Full shader and platform-cooking
requirements remain open.
