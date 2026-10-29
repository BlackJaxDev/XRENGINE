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

## Bitmap text frame stall correction

Focused comparisons used disposable copies of the saved-image publication and
ordinary browser logs, snapshots and PNGs. No frame trace was recorded. The
browser was Chromium 154.0.8037.57 with Playwright 1.63.0 and explicit
SwiftShader execution. The five-second frame deadline and all UI assertions
remained unchanged.

Suppressing only `engine-ui-text-batched-bitmap` draws restored animation and
produced the initial PNG. The expected glyph-ink assertion then failed. A
constant fragment output also restored animation with the original vertex
shader and storage inputs. Changing only gradient sampling to explicit mip-zero
sampling retained the stall. Disabling the outline branch restored animation.
Replacing only the glyph bounds early return with vector comparisons and a
`select` restored animation with the full outline loop and gradient sampling.
These comparisons isolate the failing source form to the bounds control flow
repeated by outline taps. They do not establish a browser or driver defect.

`UITextBatchedBitmap.slang` now samples each tap before it applies the inclusive
glyph bounds mask. For finite glyph coordinates, samples outside the glyph
remain zero and samples on either boundary remain included. The change retains
explicit gradients, sampler behavior, outline radius and taps, fill and outline
composition, linear offscreen output, and the vertex and storage contracts.
It adds no fallback and removes no text feature.

The focused canonical ShaderCooker run passed with the pinned Slang compiler.
Its WGSL contains one `textureSampleGrad` and `select` in the tap helper, without
conditional branches. The disposable comparison PNG shows readable screen and
world text, saved image colors, clipping and transparency. The unchanged UI
harness passed both fresh browser contexts in this comparison: 72 checkpoints
in the first context and 14 in the second, including pixel, glyph, input,
resize and lifecycle assertions. The complete run took approximately 157 seconds.
This comparison isolates the source change. The normal publication check below
qualifies the complete saved UI path without a shader substitution.

Evidence is under
`Build/_AgentValidation/20261029-205003-implementation/reports/phase2/ui-stall/`,
with the focused cook log in `logs/phase2/ui-stall-canonical-cook.log`.
The first `no-text` comparison used an exact label match that did not match the
operation suffix. It changed no draws and is not suppression evidence.
`no-text-corrected` uses the verified prefix match. All diagnostic substitutions
remain in ignored copied sites; production JavaScript is unchanged.

## Normal publication after the correction

The complete canonical cook passed for 184 non-probe recipes and produced
184 shader artifacts and 84 pipeline artifacts. The staged sample uses the
current saved images and the new catalog. Hash checks preserved all twelve
original sample files. The current Editor and native publisher then completed
normal publication with 366 assets, 181 selected shaders and 84 pipeline passes.
All 343 staged canonical input files remained unchanged.

The published bitmap-text descriptor and WGSL exactly match the canonical cook:

- Descriptor: `d473311133febcb9e78290ffb8226b1aac78d6eae25a0dbcbe309aae76d678d4`.
- WGSL: `a4ec9e3dc1052e80b16b8882391df19a7f8d2a2fd5f8a069044ee83ed0edc83a`.

The unchanged `Tools/BrowserSmoke` UI harness passed on this normal bundle with
Chromium 154.0.8037.57, Playwright 1.63.0 and explicit SwiftShader execution.
The run completed in approximately 103 seconds. Both fresh browser contexts
passed: 72 checkpoints in the first context and 14 in the second. Each initial
checkpoint passed 21 pixel samples and two glyph checks. Keyboard, mouse,
touch, text editing, synthetic composition, clipping, target lifecycle, resize
and fresh-context checks passed. Physical OS IME and device acceptance remain
separate from these software browser checks.

The initial PNG, resized PNG and second-context initial PNG were visually
inspected. Screen and world text remain readable, with saved image colors,
alpha blending and clipping intact after resize. The harness files and selected
published runtime JavaScript retain their before-run hashes. No runtime edit,
shader substitution, trace or assertion change was used for this run.

Final evidence is under
`Build/_AgentValidation/20261029-205003-implementation/reports/phase2/ui-final/`.
`smoke-report.json` records the passing checks. `published-ui-identity.json`
records the shader and runtime hashes. The staging report and before/after
hash snapshots record preservation of source files and harness files.

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

Non-primary sRGB/tint witnesses, broader shader coverage, complete platform
cooking, physical-device and desktop comparison, and packaged Editor publishing
remain open beyond the saved UI publication and software browser checks above.
