# Filtered sRGB cubemap qualification

Date: 2026-10-29
Status: Live qualification and regression gates passed on a software adapter.

## Authored data and scope

The new `engine-sky-cubemap-filtered` diagnostic uses the real `SkyboxComponent`,
`XRTextureCube`, cooked `SkyboxCubemap` shader, and shared `DefaultRenderPipeline`.
It supplies six RGBA8 sRGB faces at each of three authored mip levels: 4 by 4,
2 by 2, and 1 by 1. Automatic mip generation is disabled. Minification,
magnification, and mip filtering are linear. Each case pins `MinLOD` and
`MaxLOD` to the same integer.

The first two mip levels use a distinct constant RGB value on each face.
The final mip uses one color across all faces. Source alpha is 96. This layout
isolates face selection, sRGB decoding, two-face interpolation, and mip selection.
The existing HDR cube profile retains its row/column orientation checks.
These raw authored bytes do not qualify imported images or integrated asset cooking.

Forty-one cases cover all six face interiors at levels zero and one, the final
mip, all twelve two-face edges at levels zero and one, edge intensity, authored
sky rotation, camera translation, and restoration. Axis views use a 30-degree
vertical field of view; edge views use 10 degrees. The camera stays away from
the foreground quad so each sample measures the sky. Foreground depth remains
covered by the existing sky profiles. Two device sessions each run the cases,
resize to 640 by 384, and restore 512 by 512: 86 samples in total.

## First live result

All 41 cases rendered before numeric checks were added. Face-edge captures at
both mip levels and an authored rotation were viewed. Actual GPU storage reports
one 4-by-4, six-layer `rgba8unorm-srgb` texture with three mip levels and one
sample. Its cube view spans all six layers and all three levels. Both handles
remain fixed through the entire run.

LOD changes replace the sampler and the selected sky draw's managed pipeline
handle. Native shader and pipeline identities remain fixed. This matches the
existing layered-texture ownership contract. No production shader or texture
backend change was needed.

## Independent reference and targeted result

The reference derives camera rays from independent forward/right/up vectors.
It selects faces by ray-component magnitude and sign. For an extent `n`, largest
magnitude `a`, and second-largest magnitude `b`, the adjacent face has weight
`max(0, (b/a - (1 - 1/n)) * n/2)`. This follows from the texel-center coordinate
`n*(1+b/a)/2 - 0.5`. Both face colors are decoded from sRGB before interpolation.
Authored intensity then scales the linear result. Sky alpha must be one.

The reference rejects rays whose filter footprint reaches a third face. The
uniform 1-by-1 level returns its decoded constant instead; it does not qualify
corner weights. At an exact two-face tie, both faces have weight one half.
Uniform faces do not test neighboring texel orientation within a face.

All 86 targeted samples pass. Each compares five 5-by-5 HDR regions and their
tonemapped canvas regions. HDR tolerance remains the larger of 0.002 and 0.2
percent; canvas tolerance remains three byte values. The largest HDR endpoint
error is about 0.128 times its allowed tolerance. Measured maximum HDR radiance
is 1.4482421875.

Each settled sample has 30 live resources and zero retiring resources. The
full native program inventory, texture storage, and cube view remain stable.
Only the selected sky's managed pipeline handle may change at a sampler LOD
transition. Shader handles and unrelated pipeline handles remain exact.
The original nearest-filtered cube retains the same storage requirements.

## Build and publication result

All 24 build and publication gates pass: four desktop Release builds, 19 portable
browser-wasm builds, and native-WASM publication. Builds report zero warnings
and errors; publication has no compiler warning/error diagnostics. Fresh official
Slang 2026.8 cooking produces 57 selected shader artifacts. The content recipe
packages 112 assets and 14 pipeline passes. Runtime metadata verification passes.

The final bundle passes required-page, native-WASM, Jolt notice and pin,
desktop-dependency exclusion, and browser restore-graph checks. Published
diagnostic host assets match source. Full browser regression ran against that
exact bundle with Chromium 154.0.8037.57, Playwright 1.63.0, and explicit
SwiftShader WebGPU.

All 22 selected checks pass. Nine sky profiles pass 296 samples, including all
86 filtered cube samples. The original nearest-filtered cube storage and
orientation checks also pass. All 24 indirect lifecycle/resize stages retain
settled preparation and strict ownership. Bare-host shipping-world autostart,
the optional published game, and the separate Jolt spike are skipped explicitly.
HDR intensity and non-square restoration captures were also viewed.

## Remaining acceptance

Three-face corner weights, implicit and fractional LOD, automatic mip generation,
anisotropy, imported environment images, serialization, physical devices, and
desktop pixel parity remain unqualified. Filtering across octahedral seams,
procedural clouds, stars, moon shading, automatic cycling and lighting,
sky bloom, and sorted transparency remain open. Complete shader and integrated
platform-cooking requirements remain open. The reference runtime and separate
unpublished manifest work remain unchanged.
