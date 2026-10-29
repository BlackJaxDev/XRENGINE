# Gradient sky qualification

Date: 2026-10-29
Status: Live qualification and regression gates passed on a software adapter.

## Scope

Add a repeatable engine gradient and solid sky diagnostic. Use the real
`SkyboxComponent`, cooked material catalog, perspective camera, foreground
`ModelComponent`, and shared `DefaultRenderPipeline`. Preserve the frozen
reference runtime and desktop shader behavior.

## Observations

The first live software WebGPU run renders seven cases at 512 by 512. Viewed
captures and real RGBA16F reads show the asymmetric gradient, camera translation
invariance, pitch and yaw behavior, intensity above one, solid color, and
restoration. The foreground retains linear RGBA `(0.132, 0.066, 0.033, 0.7)`.

`SkyboxComponent.Mode` intentionally rebuilds its material. Gradient-to-solid
and solid-to-gradient changes therefore replace the sky program handles. The
qualification allows these explicit replacements but checks settled resource
counts. Numeric and camera-only changes retain exact handles in the first run.

The first repeatable check stops at non-square resize because it compares all
managed resource handles across surface generations. The retained before/after
data shows that the sky shader handle stays unchanged while the tonemap shader
handle and pipeline handles change. The existing unlit qualification checks
native GPU object identity across resize, while it compares managed handles
within a target generation. The sky check now uses that same contract.

No renderer change was needed. The host shares its existing native-object
identity tracker between the unlit and sky diagnostics. It retains the
prior-device rejection and resets IDs only at new session startup. Numeric and
camera changes still require exact managed handles. Target resize requires the
same native shader modules and pipelines. Explicit mode changes can replace
the material; retained resources must still drain.

The targeted run passes all 18 samples: seven authored cases and two resize
steps in each of two fresh sessions. Sizes are 512 by 512 and 640 by 384. Every
sample checks five 5-by-5 HDR regions and matching display pixels. HDR bounds
allow the larger of 0.002 and 0.2 percent; display bounds allow three byte values.
All settled samples retain 69 live resources, with no retiring resources. The
native sky pipeline keeps the same object identity through both resizes.

## Regression results

The full BrowserSmoke run passes all 14 selected checks. The sky check repeats
all 18 samples successfully. Existing unlit material, MSAA, and indirect checks
also pass with the shared native-object identity helper. The run excludes
shipping-world autostart on the bare host, the optional published game, and the
separate native Jolt spike.

All 24 build and publish gates pass: four desktop project builds, 19 portable
project builds, and native-WASM browser publication. The builds report no
warnings or errors. Fresh cooking packages 57 shader artifacts. The separate
publish bundle passes required-page, desktop-dependency exclusion, native-WASM,
Jolt notice and pin, and browser restore-graph checks.

## Limits

This is static, single-sample, software WebGPU qualification. Other sky image
projections, procedural controls, sky bloom, transparency over sky, authored
world serialization, hardware performance, and desktop parity remain separate.
No whole shader requirement is closed by this bounded result.
