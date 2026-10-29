# Procedural sun disc qualification

Date: 2026-10-29
Status: Live qualification and regression gates passed on a software adapter.

## Scope and first live result

The diagnostic uses the real `SkyboxComponent`, cooked
`SkyboxDynamicProcedural` shader, perspective camera, foreground mesh, and shared
`DefaultRenderPipeline`. The camera aims at the component's sun direction with
a 10-degree vertical field of view. Clouds, stars, automatic cycling, and
sun/moon/ambient synchronization remain disabled.

Ten cases cover noon, sunrise, sunset, a sun below the horizon, wide and narrow
discs, dimmed dawn, camera translation, foreground depth, and restoration.
The default disc threshold is 0.9994; wide and narrow cases use 0.998 and 0.9999.
The dim dawn case uses intensity 0.01. The foreground case uses a 90-degree
field of view and a 90-degree authored sky rotation. Its mesh fully covers the
dawn disc, including its edge. It does not hide the wider atmospheric glow.

All ten cases rendered in the first live run. The sampled noon center reaches
19.171875 in linear HDR. Dawn retains the warm channel ratios, and intensity
0.01 lowers its center to approximately `(0.183, 0.075, 0.0277, 1)` before
tonemapping. The time 0.98 case retains a partially weighted sun below the
horizon, as the authored shader specifies. Foreground HDR remains approximately
`(0.132, 0.066, 0.033, 0.7)`. Noon and dim dawn captures were viewed.

The existing shader passed these checks without a shader change. This increment
adds the real-camera fixture and repeatable known-value coverage. Desktop GLSL,
the frozen reference runtime, and separate manifest work remain unchanged.

## Independent numeric reference

The shared clear-atmosphere reference now also evaluates the optional sun disc.
The earlier clear-sky profile retains its exclusion of disc samples. The sun
profile evaluates the disc smoothstep, limb darkening, aureole, atmosphere,
day-cycle color and final intensity in double precision. It rounds authored
time, threshold and intensity values to their uniform float representation.
It never reads shader source or rendered colors. Moon-disc samples remain
outside its admitted scope.

Camera rays use an independently constructed right/up basis around the sun
direction. Foreground checks intersect the unrotated camera ray with the real
quad plane, before applying sky rotation. The reference includes the viewport
aspect ratio and each case's field of view.

Ten 5-by-5 regions sample the center, inner disc, edge, exterior glow, and
corners. HDR comparisons use the larger of 0.00002 and 0.2 percent of the
expected value. Tonemapped canvas comparisons allow three byte values.
The narrow disc passed without a tolerance increase. In the targeted run,
the largest endpoint error was about 0.825 times the allowed tolerance.
These checks verify the authored artistic model, not physical solar radiometry.

## First-use and resize schedule

The initial sun-facing view has no visible foreground. Its shader and pipeline
are first created when the foreground enters view. The qualification warms
that real draw before it records the resource baseline.

The first numeric run passed all ten cases but failed the program-inventory
comparison at its first resize. The resized sun-facing view did not draw the
foreground, so its pipeline handle was absent from the new inventory. The sky
and tonemap native identities remained stable, and the foreground shader
remained present. This was not evidence that a native sky program was replaced.

The revised schedule renders and samples foreground and sun at each resized
extent. Two sessions each run ten cases, then both views at 640 by 384 and again
at 512 by 512. All 28 targeted samples pass. Native program identities remain
stable, and each settled sample has 69 live resources and zero retiring
resources. Stop removes the diagnostic session. No program identity or resource
assertion was relaxed. Other sky profiles retain their existing schedules.
The dim dawn and non-square foreground captures were viewed.

## Build and packaging results

All 24 build and publish gates pass: four desktop Release builds, 19 portable
browser-wasm builds, and native-WASM browser publication. Project builds report
zero warnings and errors. Publication has no compiler warning/error diagnostics.
The pinned official Slang 2026.8 toolchain cooks all 57 selected shader artifacts.
The generated recipe packages 112 assets and 14 pipeline passes; runtime metadata
verification passes.

The final bundle passes required-page, native-WASM, Jolt notice and pin,
desktop-dependency exclusion, and browser restore-graph checks. Published
diagnostic host assets match source. Full browser regression runs against that
exact bundle with Chromium 154.0.8037.57, Playwright 1.63.0, and explicit
SwiftShader WebGPU.

All 19 selected checks pass. The six sky profiles pass 142 samples: 18 gradient,
24 clear procedural, 28 sun disc, 24 cube, and 24 for each image projection.
The shared reference refactor passes the existing clear-sky checks. All 24
indirect lifecycle/resize stages pass with settled preparation and strict
resource ownership. Bare-host shipping-world autostart, the optional published
game, and the separate Jolt spike are skipped explicitly.

## Remaining acceptance

This is clear-sky, single-sample software coverage. Moon surface and phase
shading, clouds and their sun occlusion, stars, automatic cycling, light/ambient
synchronization, sky bloom, sorted transparency, serialized-world coverage,
physical devices, and desktop pixel parity remain open. Filtered image seams
and poles, mip chains, and sRGB sky decoding also remain unqualified. The full
shader and integrated platform-cooking requirements remain open.
