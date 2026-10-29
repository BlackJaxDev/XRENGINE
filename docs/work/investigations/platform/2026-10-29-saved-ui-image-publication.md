# Saved UI image publication

## Problem and correction

The BrowserUiParity image component created both images during play. This
bypassed saved image cooking. Replacing it with normal saved image materials
reproduced `BrowserCook.UiImageProjectionUnsupported` in the Editor graph cook.
The desktop image factory and quad setup discover four uniforms from inactive
transparency snippets. The image projection previously required `MatColor`
alone, so it rejected this normal authored material.

`BrowserMaterialCookProjection` now verifies the canonical desktop shader and
snippet closure before accepting the exact four typed defaults with `MatColor`.
It retains only the tint in the existing cooked carrier. Active values, unknown
parameters, duplicates, incorrect types and source-free extras still reject.
The borrowed source array and image remain unchanged. Desktop shaders and the
runtime image carrier are unchanged.

Both image nodes now save a material and embedded texture in the ordinary world
graph. The sample image-creation component and its factory registration are
removed. The final fixture retains the previous red, green, blue and half-alpha
yellow pixels, white tint and nearest/clamp sampling. All existing browser
pixel, glyph, input and lifecycle assertions remain unchanged.

## Runtime evidence

The source-checkout Editor builder published the unchanged baseline and the
saved-image world. Each bundle contains 366 assets, 181 shader artifacts and
84 pipeline artifacts. The source guards preserve all 344 baseline inputs and
343 saved-fixture inputs. The native reader verifies both saved image and
material IDs, sixteen pixel bytes per image, tint, sampler settings, sRGB roles
and the restored shared reference between each material role and texture list.
Each `PublishedUiImageMaterial` has exactly one parameter after cooking.

An exploratory non-primary-color fixture also passed publication and native
payload reading. It was not retained in the sample because visual acceptance
is blocked. Its result proves payload preservation, not shader color accuracy.

The baseline and saved fixture both reach `running`, with 17 controls and
17 native accessibility proxies, then fail the initial five-second animation
frame check. The page capture also times out. No UI PNG or known-value result
was obtained. The baseline observer records a ready renderer, a complete
offscreen package, two submitted frames and 93 preparation-only frames. This
does not identify the stall cause. The separately consumed frame-trace
authorization was not used. No deadline or assertion was weakened.

The baseline and final snapshots remain unchanged for approximately five
seconds, apart from timestamps. Both keep a pending animation-frame request,
two active receipts out of 64, and no admission deferral. The first rendered
receipt has one unresolved observer and its sequence has not completed. The
receipt callbacks in `gpu-engine-frame-scopes.js` identify queue completion as
still pending. `EngineCanvasHost.frame` schedules ordinary frames without
awaiting GPU completion. The independent harness frame callback also fails,
while a JavaScript timer and subsequent page evaluation respond. Thus the
evidence does not show a wholly blocked JavaScript thread or a full receipt
pool. It does not distinguish GPU execution from browser presentation failure.
The next diagnostic should inspect the first two submitted command graphs and
their completion, rather than repeat the same frame-clock check. No renderer
change is justified by the current counts alone.

The 21 focused parameter-admission tests pass. Their first run incorrectly
compared the material constructor's copied array to its input. The corrected
test snapshots the material's own array before projection. No production code
changed for that test correction.

The final build and regression results are recorded in
[platform validation](../../testing/platform/platform-validation.md#saved-ui-image-publication--2026-10-29).
Disposable logs, payload checks and before/after browser reports are under
`Build/_AgentValidation/20261029-195227-saved-ui/`.

## Native font prerequisites

The first local baseline publication failed to load `freetype6`. The existing
SharpFont dependency selects FreeType 2.6.0. The official FreeType mirror's
`VER-2-6` source built a shared library without changing source or dependency
pins. The source archive SHA-256 is
`21f9052484c9e1d716f9acd5f9e75ef58c3df36ad2f62ce432ef26670d0c2247`.
The native version call returned 2.6.0. The build used the CMake compatibility
minimum for the old project and emitted a CMake deprecation warning.

The next attempt passed FreeType loading but could not find the pinned Magick
native library. The publisher's process-local library path now includes that
already-installed package's Linux native directory and the temporary FreeType
build. Publication then passed. No repository setup file, dependency pin or
managed supply path changed. This local prerequisite work does not qualify an
installed Editor or a new redistribution package.

## Remaining work

Find the existing initial UI frame stall through ordinary runtime evidence.
Then run the unchanged pixel, glyph, input, resize and fresh-context checks.
Non-primary sRGB/tint witnesses, broader shader coverage, complete platform
cooking, physical-device and desktop comparison, and packaged Editor publishing
remain open. This increment adds no completed requirement.
