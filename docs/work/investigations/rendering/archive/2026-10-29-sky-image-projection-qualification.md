# HDR sky image projection qualification

Date: 2026-10-29
Status: Live qualification and regression gates passed on a software adapter.

## Scope

Exercise equirectangular and octahedral sky images through the real
`SkyboxComponent`, `XRTexture2D`, cooked shaders, perspective camera, lit
foreground, and shared default pipeline. The panorama is 8 by 4 texels; the
octahedral image is 8 by 8 texels. Both use exact RGBA16F bytes. Red encodes
column, green encodes row, and blue combines both coordinates. Source alpha
is 0.25. The shader must produce opaque sky alpha.

Both use one authored mip and nearest sampling. The panorama repeats U and
clamps V. The octahedral image clamps both axes. The existing component's
authored 90-degree sky rotation and camera transforms drive the shared cases.
The implementation reuses cube/gradient lifecycle and sampling infrastructure.
Desktop shaders, the frozen reference runtime, and publisher manifest work
remain unchanged.

## First live observations

Each profile renders ten cases: all six axis-facing camera orientations,
translation, authored sky rotation, doubled intensity, and restoration.
Captured HDR regions retain the selected texel values and opaque sky alpha.
The foreground retains linear RGBA `(0.132, 0.066, 0.033, 0.7)`.
The actual engine resources are single-layer, single-sample RGBA16F images
with the authored extents. No replacement image or shader supplies the output.
Forward and upward panorama captures were viewed. Forward and downward
octahedral captures were viewed; the lower-hemisphere fold changes the pattern.

## Repeatable contract

The reference derives each camera ray at the pixel center. Equirectangular
mapping uses longitude and latitude. Octahedral mapping uses world Y as its
projection axis and folds the negative hemisphere. Each profile compares five
5-by-5 HDR regions and corresponding tonemapped canvas regions. HDR tolerance
is the larger of 0.002 and 0.2 percent; display tolerance is three byte values.

Even output extents keep the sampled pixel centers away from exact pole rays.
Regions near texel boundaries can contain both neighboring values; the
reference computes their minimum and maximum. This bounds sampled values; it
does not prove exact per-pixel placement of a discontinuity. The asymmetric authored image
intentionally has discontinuities. This proves coordinates and selected texels,
not seamless filtering of a continuous environment image.

Two sessions each run ten cases, resize to 640 by 384, and restore 512 by 512.
The exact authored texture handle must survive numeric changes and resize.
Native shader and pipeline objects must remain stable. Settled resource counts
must not grow, and retiring resources must drain before each sample.

The targeted qualification passes all 24 samples for each projection. Every
settled sample has 72 live resources and zero retiring resources. Authored
texture handles and native program objects remain unchanged across the resize
steps. The non-square captures for both projections were viewed.

## Indirect sampling failure and correction

The first full regression run passes all four sky profiles but fails the
existing indirect x4 GTAO profile after restoration to 512 by 512. The sampled
inventory contains pipeline handle 196887, labeled
`engine-unlit-color-depth-normal`, with no dependent command and no reported
managed owner. A retained work item reports pipeline zero. The old inventory
cannot prove an exact association between those objects.

The pipeline path publishes the browser handle before its managed async
continuation assigns the handle to its draw. Unlike buffers, this path has no
pending request identity in the ownership snapshot. The unchanged published
baseline passes a separate targeted run; the failure is timing-dependent, and
that pass does not prove ownership in the failed capture.

The diagnostic pause now requires every retained indexed draw's pipeline
preparation to finish before sampling. The check is allocation-free and keeps
frames progressing while it waits. Faults still propagate, the existing pause
timeout remains, and the ownership check still rejects every unowned resource.
It additionally requires `pipelinePreparationSettled` in the snapshot. This
changes the diagnostic sampling boundary, not rendering or retirement policy.

A runtime probe delays the return of real depth-normal pipeline handles by
200 milliseconds after physical creation. In two sessions, the new readiness
query reports all 31 delayed handoffs as pending. All six resize/restart sample
stages then report settled preparation and pass the unchanged ownership checks.
An earlier version of this probe required the frame pump itself to encounter a
pending handoff; that scheduling condition was not reached. The completed probe
observes the readiness result at the actual delayed return and does not count
those observations as frame-pump waits.

The initial failure report and both probe results are retained separately from
the final regression run. All 24 build gates are repeated after the correction.

## Final regression results

The final full BrowserSmoke run passes all 17 selected checks. All four sky
profiles pass 90 samples: 18 gradient, 24 cube, and 24 for each image projection.
All 24 indirect lifecycle/resize stages pass with settled pipeline preparation
and strict ownership. The bare host's shipping-world autostart, optional
published game, and separate Jolt spike are skipped explicitly.

All 24 repeated build and publish gates pass: four desktop builds, 19 portable
builds, and native-WASM browser publication. Builds report zero warnings and
errors. Fresh cooking packages 57 shader artifacts and 112 assets. The separate
publish bundle passes required-page, desktop-dependency exclusion, native-WASM,
Jolt notice and pin, and browser restore-graph checks.

## Limits

This is single-mip, nearest-filtered, single-sample RGBA16F software coverage.
Filtered seams and poles, mip chains, sRGB image decoding, procedural controls,
sky bloom, transparency over sky, authored world serialization, hardware, and
desktop parity remain separate acceptance work. No whole shader requirement
is closed by this bounded result.
