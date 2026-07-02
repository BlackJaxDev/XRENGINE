# VR Mirror Cyclopean Reconstruction TODO

Source design: `docs/work/design/rendering/cyclopean-reconstruction.md`

## Goal

Make `Engine.Rendering.Settings.VrMirrorComposeFromEyeTextures=true` produce a stable cyclopean desktop mirror from the rendered left/right eye color and depth textures instead of blitting or stretching one eye.

This is separate from the current `VR.AllowDesktopEditing=false` third-render camera path. The third-render path remains useful for validation and for modes where a full desktop camera render is desired. This TODO is for the lower-cost eye-texture composition path.

## Backend Priority

Vulkan is the primary implementation target; OpenGL follows after Vulkan is validated.

- Vulkan already has true per-eye mirror FBOs/colors (`_vulkanEyeMirrorFbos`, `_vulkanEyeMirrorColors`) and an explicit publish path with layout handling (`TryRenderAndPublishOpenXrEyeMirrorFrameBuffers`), so it is closest to ready.
- OpenGL currently renders both eyes through the same shared `_viewportMirrorFbo` sequentially, so it needs per-eye target restructuring first and lands second.
- All backend-neutral work (shader contract, camera model, publish metadata) must not hard-code Vulkan assumptions.

## Architecture Gap Summary (from 2026-07-01 readiness review)

Covered by the tasks in sections 2–3 below:

1. No per-eye sampleable depth on either backend: GL mirror depth is a shared `Depth24Stencil8` renderbuffer; Vulkan `_vulkanEyeMirrorDepths` are also renderbuffers. Nothing publishes depth.
2. OpenGL renders both eyes into the same `_viewportMirrorFbo`, so mirror color holds only the last-rendered eye; per-eye color survives only via ImGui preview textures with unvalidated format/color space.
3. No out-of-pipeline fullscreen material pass at mirror-composition/present time; `TryRenderDesktopMirrorComposition` can only `Blit`.
4. No per-eye matrix/frame-id snapshot publishing; eye poses can move between eye render and mirror composition.
5. Render graph/resource planner tracks mirror publish copies but not sampled reads of eye color/depth by a composition pass; no persistent middle color/depth history targets exist for temporal accumulation.

## Acceptance Criteria

- [ ] Desktop mirror composition uses both left and right eye color/depth inputs.
- [ ] The output is a single middle/cyclopean view, not one stretched eye.
- [ ] The composition path preserves aspect ratio and letterboxes/crops intentionally when the desktop window shape differs from the eye textures.
- [ ] Works on the Vulkan OpenXR path (primary target, validated first).
- [ ] Works on the OpenGL OpenXR path (follow-up after Vulkan validation).
- [ ] Handles normal and reversed depth conventions without per-backend guessing.
- [ ] Consumes linear view-space depth resolved per eye; the reconstruction shader has no device-depth convention branches.
- [ ] Produces a temporally stable resolve with no hard per-pixel L/R switching flicker.
- [ ] Does not require eye tracking for the base reconstruction path.
- [ ] Does not mutate eye swapchain textures or rely on undefined image layouts.
- [ ] Does not render editor ImGui or editor-only scene UI in runtime mirror mode.
- [ ] Emits clear diagnostics when required eye color/depth inputs are unavailable.
- [ ] Has targeted tests or source-contract coverage for settings, resource wiring, and fallback behavior.
- [ ] Has visual validation evidence from at least one OpenXR run.

## Non-Goals

- [ ] Do not replace the full third desktop render path.
- [ ] Do not add a CPU readback composition path.
- [ ] Do not hide missing depth inputs behind a silent one-eye fallback.
- [ ] Do not require eye tracking for the base reconstruction; gaze/fixation is an optional later bias.
- [ ] Do not use a raw 50/50 left+right average as the invalid-pixel fallback; it double-images exactly at disocclusions.

## Tasks

### 0. Branch And Tracking

- [ ] Create a dedicated implementation branch for this TODO.
- [ ] Link this TODO from the related OpenXR/VR rendering work notes.
- [ ] Record the current one-eye stretch behavior with a screenshot or short log note before changing the compositor.

### 1. Contract Cleanup

- [ ] Document the two desktop VR presentation modes:
  - `VrMirrorComposeFromEyeTextures=false`: render a real desktop/cyclopean viewport.
  - `VrMirrorComposeFromEyeTextures=true`: reconstruct a cyclopean mirror from eye textures.
- [ ] Wire `EVrMirrorMode` dispatch in `TryRenderDesktopMirrorComposition`: the enum already includes `CyclopeanReconstruct`, but the OpenGL and Vulkan composition paths currently ignore the mode and always blit, so `CyclopeanReconstruct` behaves as `BlitSubmittedEye`.
- [ ] Keep `BlitSubmittedEye` behavior as the loud-diagnostic fallback when reconstruction inputs are unavailable, never as a silent substitute for `CyclopeanReconstruct`.
- [ ] Rename or comment any code that still describes `VrMirrorComposeFromEyeTextures=true` as a simple one-eye blit.
- [ ] Add diagnostics that state which path is active: third render, cyclopean reconstruction, blit fallback, or disabled desktop window.
- [ ] Depth input contract (decided): a per-eye resolve pass produces linear view-space Z in `R32_SFLOAT` at the end of each eye render; the reconstruction shader consumes only linear depth. Sampling hardware depth directly is a documented fallback that requires explicit per-eye convention metadata (NDC z range including OpenGL `glClipControl(GL_ZERO_TO_ONE)`, reversed-Z, Y-flip) — never per-backend guessing.

### 2. Architecture Prerequisites — Per-Eye Resources And Publish Contract (Vulkan first)

Gap 1: per-eye sampleable depth.

- [ ] Vulkan: replace the `_vulkanEyeMirrorDepths` renderbuffers with sampleable depth attachments, or source depth from each eye viewport's `RenderPipelineInstance` depth texture (each eye viewport already owns a separate pipeline instance).
- [ ] Vulkan: add the per-eye linear depth resolve pass (`R32_SFLOAT`, section 3) and register its output with the resource planner as a sampled-read resource.
- [ ] OpenGL (follow-up): replace the shared `Depth24Stencil8` renderbuffer in `_viewportMirrorFbo` with per-eye sampleable depth, then add the same resolve pass.

Gap 2: per-eye color on OpenGL.

- [ ] OpenGL (follow-up): restructure the eye render so each eye has its own mirror color target instead of both eyes rendering sequentially into the shared `_viewportMirrorFbo` (mirror color currently holds only the last-rendered eye).
- [ ] Audit the existing preview textures (`_previewLeftEyeTexture`/`_previewRightEyeTexture`) before reusing them as reconstruction inputs: they were built for ImGui preview; validate format, sRGB handling, and lifetime, or introduce dedicated composition color targets.

Gap 3: out-of-pipeline fullscreen material pass.

- [ ] Add a renderer facility to draw a fullscreen material (multiple sampled texture inputs + UBO/push constants) into an arbitrary target at mirror-composition/present time, outside pipeline command chains. `TryRenderDesktopMirrorComposition` currently supports only `Blit`.
- [ ] Vulkan: build it on the existing pipeline-override + render-pass-index scope pattern already used by the Vulkan mirror composition, with resource planner declarations for its sampled inputs and output.
- [ ] OpenGL (follow-up): provide the equivalent using the existing fullscreen-triangle material infrastructure, honoring current-context FBO rules (see the `_blitFboHglrc` context guard).

Gap 4: per-eye frame snapshot metadata.

- [ ] Snapshot per-eye view/projection/inverse matrices, near/far, frame id, and Y-flip state at eye-render time (poses are applied per render via `ApplyOpenXrEyePoseForRenderThread` and can move before composition) and publish them alongside the eye textures.
- [ ] Reject or hold composition when left/right snapshots carry different frame ids (ties into the mixed-frame handling in section 3).

Gap 5: history targets for temporal accumulation.

- [ ] Add persistent middle color + middle depth history targets owned by the composition path, resized with the output, and declared to the resource planner.
- [ ] Define history invalidation rules: resize, mirror mode change, VR session restart, and large pose discontinuities.

### 3. Eye Output Publishing (Vulkan first)

- [ ] Publish left/right eye color textures after the eye render is complete and before swapchain submission/release (Vulkan: extend the existing `TryRenderAndPublishOpenXrEyeMirrorFrameBuffers` publish path).
- [ ] Add a per-eye depth resolve pass that converts hardware depth to linear view-space Z (`R32_SFLOAT`) at the end of each eye render; this pass is also the MSAA depth resolve point (min / nearest-to-camera or sample 0 — never bilinear).
- [ ] Publish matching left/right resolved linear depth textures from the same frame.
- [ ] Tag each published texture with eye index, frame id, backend, dimensions, color/depth format, clip-space Y direction, reversed-Z state, near/far, view matrix, projection matrix, and inverse matrices (use the section 2 snapshot contract).
- [ ] Ensure Vulkan resources have explicit sampled-read layouts before the mirror pass samples them.
- [ ] Ensure OpenGL resources are complete and bound through normal texture handles/views before the mirror pass samples them (follow-up phase).
- [ ] Bind eye color through an sRGB view so blending happens in linear space; sample resolved depth with nearest filtering and color with linear filtering.
- [ ] Reject composition for mixed-frame left/right inputs and hold the last composed mirror image instead of composing from mixed frames or dropping to a stretched eye.

### 4. Cyclopean Camera Model

- [ ] Build the cyclopean view pose from the midpoint between left/right eye poses.
- [ ] Use HMD orientation for the initial implementation.
- [ ] Reuse the existing smoothed `CyclopeanDesktop` camera pose so reconstruction matches the pose already used for combined visibility and mirror cadence.
- [ ] Add optional fixation/gaze support after the base midpoint reconstruction is stable (gaze only biases the iteration seed depth; the base path must not require it).
- [ ] Define the middle projection/FOV policy for spectator output.
- [ ] Preserve aspect ratio when output dimensions differ from the eye render target.

### 5. Reconstruction Shader

- [ ] Add a backend-neutral shader contract for:
  - left/right color (sRGB views, linear filtering)
  - left/right resolved linear depth (`R32_SFLOAT`, nearest filtering)
  - inverse eye view-projection matrices
  - middle view-projection matrix
  - eye-to-middle transforms
  - depth clamp range and iteration seed depth
  - output size and per-eye texel size
- [ ] Implement screen-space gather from the middle pixel into both eyes.
- [ ] Implement per-pixel iterative refinement (2–3 fixed-point steps) that closes the loop through the middle camera: project guess into eye, sample depth, reconstruct, update the guess along the middle ray, repeat. Do not reproject the reconstructed point back into the same eye — that returns the same UV and refines nothing.
- [ ] Seed the iteration from previous-frame middle depth when available; otherwise a mid-range constant (or fixation depth when gaze exists). Do not use a single global fixation-plane gather; it smears everything not at fixation depth.
- [ ] Add an optional short epipolar march seed (8–16 taps + one secant refinement, bounded by ±IPD/2 lateral reprojection) for depth discontinuities where fixed-point iteration fails to converge.
- [ ] Validate each candidate by reprojection error into the middle camera (reject above a pixel threshold, e.g. 1.5 px at full res).
- [ ] Resolve L/R with continuous soft weights: depth agreement scaled by the nearer candidate's own depth (~2% relative tolerance) × lateral eye-dominance weight (left half of the output prefers the left eye) × reprojection confidence. No hard per-pixel L/R switch.
- [ ] Write middle view-space depth to a second target for temporal reprojection and later DOF.
- [ ] Add temporal accumulation (reproject last middle color via middle depth, ~0.9/0.1 blend) to fill disocclusion holes and stabilize the resolve.
- [ ] Fill remaining invalid pixels with conservative nearest-neighbor dilation when history is unavailable; never a raw 50/50 eye average at the output UV.
- [ ] Add debug modes for left contribution, right contribution, reprojection error heatmap, invalid pixels, iteration count, and final color.

### 6. Backend Integration (Vulkan first, then OpenGL)

Phase A — Vulkan (primary):

- [ ] Wire `EVrMirrorMode.CyclopeanReconstruct` dispatch in the Vulkan composition path and replace the current blit/copy with the reconstruction pass (fullscreen material facility from section 2).
- [ ] Add render graph/resource planner declarations for sampled eye color/depth inputs, history targets, and the desktop mirror output.
- [ ] Keep a loud diagnostic fallback to `BlitSubmittedEye` behavior when reconstruction inputs are missing; never silent.
- [ ] Validate Vulkan end-to-end (section 7) before starting the OpenGL phase.

Phase B — OpenGL (after Vulkan validation):

- [ ] Complete the OpenGL per-eye color/depth prerequisites from section 2.
- [ ] Replace the OpenGL one-eye blit in `TryRenderDesktopMirrorComposition` with the reconstruction pass when `EVrMirrorMode.CyclopeanReconstruct` is active.

Both phases:

- [ ] Use the existing fullscreen quad/blit infrastructure where it can express sampled texture inputs correctly.
- [ ] Make the output target size follow the window framebuffer size.
- [ ] Compose at reduced cost: support half-resolution reconstruction with a linear upscale blit, honor `VrCyclopeanDesktopTargetRateHz` cadence, and hold the last composed image between updates (`HeldLastImage`).
- [ ] Account for active foveated rendering (fragment shading rate / fragment density map): the mirror exposes low-detail eye periphery; document it as expected preview quality and optionally reduce peripheral foveation strength while the mirror is active.

### 7. Tests And Validation

- [ ] Add source-contract tests that `VrMirrorComposeFromEyeTextures=true` routes through cyclopean reconstruction, not one-eye blit.
- [ ] Add source-contract tests that `EVrMirrorMode.CyclopeanReconstruct` dispatches to the reconstruction pass and `BlitSubmittedEye` remains the explicit blit path.
- [ ] Add tests for the linear depth resolve contract and missing-depth diagnostics.
- [ ] Add tests for the per-eye matrix/frame-id snapshot contract (section 2, gap 4).
- [ ] Add tests for mixed-frame rejection holding the last composed image.
- [ ] Add tests that `VR.AllowDesktopEditing=false` can still use the full third-render camera path when mirror composition is disabled.
- [ ] Build `XREngine.Editor`.
- [ ] Run the targeted OpenXR timing/rendering tests.
- [ ] Vulkan first: validate with MCP screenshots from at least two HMD/head poses on the Vulkan path.
- [ ] Validate the debug modes (contribution, reprojection error, invalid mask) with MCP screenshots.
- [ ] Validate with RenderDoc for Vulkan resource layouts and sampled inputs (new sampled reads of eye color/depth are exactly the uncertain case).
- [ ] OpenGL follow-up: repeat MCP screenshot validation on the OpenGL path after Phase B.
- [ ] Capture logs showing no repeated mirror-composition failures, invalid layout warnings, or one-eye fallback warnings.

### 8. Documentation

- [ ] Update `docs/architecture/rendering/openxr-vr-rendering.md` with the new mirror reconstruction path, the per-eye publish contract, and the fullscreen composition pass facility.
- [ ] Update unit-testing world docs if launch settings or defaults change.
- [ ] Add a short troubleshooting section for missing depth, invalid depth convention, aspect-ratio mismatch, foveation artifacts in the mirror periphery, and temporal-accumulation ghosting.

### 9. Completion

- [ ] Review performance against the third-render desktop camera path.
- [ ] Decide whether `VrMirrorComposeFromEyeTextures` should remain opt-in or become the default for runtime spectator mirrors.
- [ ] Merge the dedicated implementation branch back into `main` after validation.
