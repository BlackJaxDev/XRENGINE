# Procedural moon radiance bounds

Date: 2026-10-29
Status: Live bounded qualification and regression gates passed on a software adapter.

## Fixture and first live result

`engine-sky-moon` uses the real `SkyboxComponent`, the cooked
`SkyboxDynamicProcedural` shader, and the shared `DefaultRenderPipeline`.
Cloud coverage and star intensity are zero. Automatic cycling and lighting
synchronization are disabled. The camera points at `GetMoonDirection()` with a
10-degree field of view. It stays away from the foreground so each region samples
the sky; foreground depth remains covered by the existing profiles.

Twelve cases cover midnight, evening, predawn, sunset, sunrise, daytime,
enlarged and narrow discs, intensity four, translation, authored sky rotation,
and restoration. The rotation case compensates the camera aim so the rotated
moon stays visible. Two sessions each run those cases, resize to 640 by 384,
and restore 512 by 512: 28 samples in total.

All twelve cases rendered before numeric checks were added. Default, enlarged,
and sunset captures were viewed. Actual HDR radiance exceeds one in the
intensity case. No production shader change was needed.

## Failed exact noise model

The first reference used one fixed, unfused float32 implementation of the
shader's sine hash and value-noise octaves. It failed at midnight, sample five,
red channel. The measured 5-by-5 region ranged from 0.390380859375 to
0.392822265625. The reference range was approximately 0.369689585 to 0.373786370,
which fails the existing 0.002 tolerance.

The hash multiplies a sine result by about 43758 before taking its fractional
part. Small arithmetic or sine differences can change the resulting pattern.
The failed comparison does not establish an exact cause or a production shader
regression. Neither coefficients nor rounding rules were tuned to match pixels.
The failed model, report, and image remain in this run's disposable evidence.
Exact maria/crater-pattern parity is still open.

## Analytic acceptance contract

The accepted reference derives camera rays and the celestial orbit without GPU
input. Off-disc corona and daytime values have exact expected values. On-disc
regions use bounds for the combined surface and corona; they do not isolate the
on-disc corona as a separate exact measurement.

Value noise is in `[0,1]`. The six detail amplitudes sum to
`0.5 * (1 - 0.55^6) / (1 - 0.55) = 1.08035484375`. Thus the shader's unclamped
crater interpolation lies in `[0.86, 1.011249678125]`. Maria interpolation lies
in `[0,1]`, so each albedo channel lies between its two authored endpoints.
The reference combines those bounds with the analytic surface normal, phase
factor, disc edge, corona, day/night weight, and intensity. These bounds require
a nonzero surface contribution where the authored shader requires one. They
also constrain its color and scale. They do not prove the noise pattern.

The shared checker admits bounds only for a profile that explicitly opts in.
Each bound must contain four finite, nonnegative, ordered channels. The report
marks bounded regions. Existing profiles retain their exact references; HDR and
canvas comparisons, tolerances, and resource ownership checks are unchanged.

All 28 targeted samples pass. Eleven 5-by-5 regions per sample produce 208 exact
regions and 100 bounded regions. HDR tolerance remains the larger of 0.002 and
0.2 percent; canvas tolerance remains three byte values. The largest exact-region
HDR endpoint error is about 0.330 times its allowed tolerance. Maximum measured
HDR radiance is 2.255859375. Each settled sample has 27 live resources and zero
retiring resources. Numeric changes, camera changes, resize, and restart retain
the required native programs and ownership. HDR intensity and non-square
restoration captures were also viewed.

## Phase and edge limits

These cases do not exercise nonzero phase lighting or soft-edge falloff.
For the selected sizes, the on-disc normalized radius squared is at most
`1 / (size^2 * 1.15^2)`, which is below 0.760. The soft edge starts at 0.85.
The selected sun and moon directions are sufficiently opposed that the phase
factor clamps to zero throughout the visible disc. This records existing
shader behavior; it does not change the authored orbit or desktop GLSL.

## Build and publication result

All 24 build and publication gates pass: four desktop Release builds, 19 portable
browser-wasm builds, and native-WASM publication. Builds report zero warnings
and errors; publication has no compiler warning/error diagnostics. Fresh official
Slang 2026.8 cooking produces 57 selected shader artifacts. The content recipe
packages 112 assets and 14 pipeline passes. Runtime metadata verification passes.

The final bundle passes required-page, native-WASM, Jolt notice and pin,
desktop-dependency exclusion, and browser restore-graph checks. Published
diagnostic host assets match source. Full browser regression passes all 23
selected checks with Chromium 154.0.8037.57, Playwright 1.63.0, and explicit
SwiftShader WebGPU. Ten sky profiles pass 324 samples. All 24 indirect
lifecycle/resize stages retain settled preparation and strict ownership.
Bare-host shipping-world autostart, the optional published game, and the separate
Jolt spike are skipped explicitly.

Final review corrected the manual moon page's inherited gradient help text and
republished the bundle. A complete file/hash comparison shows that only the HTML
page and its gzip/Brotli copies changed. Every binary, host module, shader,
content payload, and other page is byte-identical to the full-suite bundle.
The final publication and bundle checks pass. All 28 moon samples pass again
against that final output. A separate live check verifies the page title, help
text, and rendered moon; its capture was viewed.

## Remaining acceptance

Exact maria/crater-pattern parity, nonzero phase lighting, soft-edge falloff,
clouds and celestial occlusion, stars and motion twinkle, automatic cycling,
lighting synchronization, bloom, sorted transparency, serialization, hardware,
and desktop parity remain open. Full shader and integrated platform-cooking
requirements remain open. The frozen reference runtime and separate unpublished
manifest work remain unchanged.
