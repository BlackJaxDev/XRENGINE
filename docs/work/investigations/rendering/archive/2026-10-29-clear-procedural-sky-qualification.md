# Clear procedural sky qualification

Date: 2026-10-29
Status: Live qualification and regression gates passed on a software adapter.

## Scope

The diagnostic uses the real `SkyboxComponent`, its cooked
`SkyboxDynamicProcedural` material, a perspective camera, lit foreground, and
the shared `DefaultRenderPipeline`. It disables cloud coverage, cloud drift,
stars, automatic cycling, and sun/moon/ambient synchronization. It preserves
the component's remaining atmospheric parameters.

Ten cases cover noon, sunrise, sunset, midnight, camera pitch, camera
translation, camera yaw, authored sky rotation, fourfold HDR intensity, and
restoration. Two device sessions each run those cases, resize to 640 by 384,
and restore 512 by 512. This gives 24 samples of five 5-by-5 regions in both
the RGBA16F scene target and the tonemapped canvas.

Desktop GLSL, the frozen reference runtime, and separate manifest work remain
unchanged. The wider platform cook still needs integrated compressed texture
payloads and encoder selection; the existing reference content format does not
establish that shared asset contract.

## First live result and shader boundary

The first live run rendered all ten cases. HDR reads retained the foreground
at approximately `(0.132, 0.066, 0.033, 0.7)` while sky alpha remained one.
The intensity case reached 6.33203125 in a sampled blue channel. Sunrise,
midnight, noon, and pitched views were inspected. The first repeatable numeric
run passed all 24 samples against the initial shader.

No zero-coverage rendering failure was reproduced on SwiftShader. Source
inspection found that zero coverage evaluates `smoothstep(1, 1, density)`.
The Slang shader now initializes cloud density to zero and evaluates cloud
noise and density only when coverage is positive. This defines the clear-sky
boundary without changing any positive-coverage formula. The desktop shader
is unchanged. This is a boundary correction, not a claim of a reproduced
driver failure or measured performance improvement.

The first attempt to run the rebuilt shader used a stale generated content
recipe. The runtime rejected it with `EngineMeshDiagnostic.EffectCatalogMismatch`.
The recipe was regenerated from the current shader catalog before packaging.
No production validator was changed. That failed run remains separate from
the accepted evidence.

After recipe regeneration, the changed shader passed all 24 targeted samples
with the tighter HDR tolerance. Each settled sample has 69 live resources and
zero retiring resources. Native programs remain stable through resize. The
changed shader's midnight and non-square restored captures were viewed.

## Numeric and resource contract

The reference evaluates the clear atmosphere in double precision from authored
parameters and pixel-center camera rays. It does not read shader source or GPU
colors. It includes Rayleigh/Mie scattering, warm horizon and anti-sun terms,
day/night blending, night base, sun aureole, and off-disc moon corona.
It rejects samples that enter either celestial disc. The largest sampled moon
dot product is 0.760683, below the 0.99965 disc threshold.

The reference uses the same documented artistic atmosphere model, not a
physical radiometric ground truth or a desktop pixel capture. Cloud, star,
and moon-surface noise are excluded. HDR comparisons use the larger of
0.00002 and 0.2 percent of the expected value; canvas tolerance is three byte
values. The first run's largest endpoint error was less than 0.223 times this
tolerance, so the tighter absolute floor also applies to dark night channels.

The real semantic and authored time, intensity, rotation, and zero cloud/star
settings must match each case. Managed shader and pipeline handles stay stable
through numeric and camera changes. Native program objects stay stable through
resize. Resource counts cannot grow, retiring resources must drain, and stop
must remove the diagnostic session. The test retains the shared foreground
depth and HDR presentation checks.

## Build and packaging results

All 24 build and publish gates pass: four desktop Release builds, 19 portable
browser-wasm builds, and native-WASM browser publication. Project builds report
zero warnings and errors. Publication has no compiler warning/error diagnostics.
The pinned official Slang 2026.8 toolchain cooks all 57 selected shader artifacts.
The generated content recipe packages 112 assets and 14 pipeline passes.
Runtime metadata verification passes.

The final bundle passes required-page, native-WASM, Jolt notice and pin,
desktop-dependency exclusion, and browser restore-graph checks. Published
diagnostic host assets match the current source. The full browser suite runs
against this final bundle with Chromium 154.0.8037.57, Playwright 1.63.0, and
explicit SwiftShader WebGPU.

All 18 selected browser checks pass. All five sky profiles pass 114 samples:
18 gradient, 24 procedural, 24 cube, and 24 for each image projection. All 24
indirect lifecycle/resize stages pass with settled pipeline preparation and
strict resource ownership. Bare-host shipping-world autostart, the optional
published game, and the separate Jolt spike are skipped explicitly.

## Remaining acceptance

This is bounded clear-sky, single-sample software coverage. It does not qualify
clouds, stars, celestial discs, automatic cycling, light/ambient synchronization,
sky bloom, sorted transparency over sky, authored world serialization, physical
devices, or desktop pixel parity. Filtered image seams/poles, mip chains, and
sRGB sky decoding also remain open. The full shader and cooking requirements
remain open.
