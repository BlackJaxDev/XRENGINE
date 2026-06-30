# VR Mirror Cyclopean Reconstruction TODO

Source design: `docs/work/design/rendering/cyclopean-reconstruction.md`

## Goal

Make `Engine.Rendering.Settings.VrMirrorComposeFromEyeTextures=true` produce a stable cyclopean desktop mirror from the rendered left/right eye color and depth textures instead of blitting or stretching one eye.

This is separate from the current `VR.AllowDesktopEditing=false` third-render camera path. The third-render path remains useful for validation and for modes where a full desktop camera render is desired. This TODO is for the lower-cost eye-texture composition path.

## Acceptance Criteria

- [ ] Desktop mirror composition uses both left and right eye color/depth inputs.
- [ ] The output is a single middle/cyclopean view, not one stretched eye.
- [ ] The composition path preserves aspect ratio and letterboxes/crops intentionally when the desktop window shape differs from the eye textures.
- [ ] Works on OpenGL and Vulkan OpenXR paths.
- [ ] Handles normal and reversed depth conventions without per-backend guessing.
- [ ] Does not mutate eye swapchain textures or rely on undefined image layouts.
- [ ] Does not render editor ImGui or editor-only scene UI in runtime mirror mode.
- [ ] Emits clear diagnostics when required eye color/depth inputs are unavailable.
- [ ] Has targeted tests or source-contract coverage for settings, resource wiring, and fallback behavior.
- [ ] Has visual validation evidence from at least one OpenXR run.

## Non-Goals

- [ ] Do not replace the full third desktop render path.
- [ ] Do not add a CPU readback composition path.
- [ ] Do not hide missing depth inputs behind a silent one-eye fallback.

## Tasks

### 0. Branch And Tracking

- [ ] Create a dedicated implementation branch for this TODO.
- [ ] Link this TODO from the related OpenXR/VR rendering work notes.
- [ ] Record the current one-eye stretch behavior with a screenshot or short log note before changing the compositor.

### 1. Contract Cleanup

- [ ] Document the two desktop VR presentation modes:
  - `VrMirrorComposeFromEyeTextures=false`: render a real desktop/cyclopean viewport.
  - `VrMirrorComposeFromEyeTextures=true`: reconstruct a cyclopean mirror from eye textures.
- [ ] Rename or comment any code that still describes `VrMirrorComposeFromEyeTextures=true` as a simple one-eye blit.
- [ ] Add diagnostics that state which path is active: third render, cyclopean reconstruction, or disabled desktop window.
- [ ] Decide and document the required depth input format. Prefer a linear `R32_SFLOAT` eye depth texture when available; otherwise require explicit metadata for hardware depth reconstruction.

### 2. Eye Output Publishing

- [ ] Publish left/right eye color textures after the eye render is complete and before swapchain submission/release.
- [ ] Publish matching left/right eye depth textures from the same frame.
- [ ] Tag each published texture with eye index, frame id, backend, dimensions, color/depth format, clip-space Y direction, reversed-Z state, near/far, view matrix, projection matrix, and inverse matrices.
- [ ] Ensure Vulkan resources have explicit sampled-read layouts before the mirror pass samples them.
- [ ] Ensure OpenGL resources are complete and bound through normal texture handles/views before the mirror pass samples them.
- [ ] Reject composition for mixed-frame left/right inputs.

### 3. Cyclopean Camera Model

- [ ] Build the cyclopean view pose from the midpoint between left/right eye poses.
- [ ] Use HMD orientation for the initial implementation.
- [ ] Add optional fixation/gaze support after the base midpoint reconstruction is stable.
- [ ] Define the middle projection/FOV policy for spectator output.
- [ ] Preserve aspect ratio when output dimensions differ from the eye render target.

### 4. Reconstruction Shader

- [ ] Add a backend-neutral shader contract for:
  - left/right color
  - left/right depth
  - inverse eye view-projection matrices
  - middle view-projection matrix
  - eye-to-middle transforms
  - depth convention metadata
  - output size and per-eye texel size
- [ ] Implement screen-space gather from the middle pixel into both eyes.
- [ ] Reconstruct eye-space/world position from sampled depth.
- [ ] Reproject candidate positions into the middle view.
- [ ] Choose the best candidate by reprojection error, depth consistency, and eye agreement.
- [ ] Fill small disocclusion holes with conservative nearest-neighbor or dilation logic.
- [ ] Add debug modes for left contribution, right contribution, reprojection error, invalid pixels, and final color.

### 5. Backend Integration

- [ ] Replace the current OpenGL desktop mirror one-eye blit in `TryRenderDesktopMirrorComposition` with the reconstruction pass.
- [ ] Replace the current Vulkan desktop mirror one-eye blit/copy with the reconstruction pass.
- [ ] Keep a loud diagnostic fallback when depth textures are missing; do not silently stretch an eye.
- [ ] Use the existing fullscreen quad/blit infrastructure where it can express sampled texture inputs correctly.
- [ ] Add render graph/resource planner declarations for sampled eye color/depth inputs and desktop mirror output.
- [ ] Make the output target size follow the window framebuffer size.

### 6. Tests And Validation

- [ ] Add source-contract tests that `VrMirrorComposeFromEyeTextures=true` routes through cyclopean reconstruction, not one-eye blit.
- [ ] Add tests for depth metadata selection and missing-depth diagnostics.
- [ ] Add tests that `VR.AllowDesktopEditing=false` can still use the full third-render camera path when mirror composition is disabled.
- [ ] Build `XREngine.Editor`.
- [ ] Run the targeted OpenXR timing/rendering tests.
- [ ] Validate with MCP screenshots from at least two HMD/head poses.
- [ ] Validate with RenderDoc when Vulkan resource layouts or sampled inputs are uncertain.
- [ ] Capture logs showing no repeated mirror-composition failures, invalid layout warnings, or one-eye fallback warnings.

### 7. Documentation

- [ ] Update `docs/architecture/rendering/openxr-vr-rendering.md` with the new mirror reconstruction path.
- [ ] Update unit-testing world docs if launch settings or defaults change.
- [ ] Add a short troubleshooting section for missing depth, invalid depth convention, and aspect-ratio mismatch.

### 8. Completion

- [ ] Review performance against the third-render desktop camera path.
- [ ] Decide whether `VrMirrorComposeFromEyeTextures` should remain opt-in or become the default for runtime spectator mirrors.
- [ ] Merge the dedicated implementation branch back into `main` after validation.
