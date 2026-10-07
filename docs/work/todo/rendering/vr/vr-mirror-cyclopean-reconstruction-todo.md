# VR Mirror Cyclopean Reconstruction TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [VR Output Pacing And Mirror Policy](../../../../architecture/rendering/vr-output-pacing-and-mirror-policy.md#mirror-policy), [OpenXR VR Rendering](../../../../architecture/rendering/openxr-vr-rendering.md)
Design: [Cyclopean Reconstruction](../../../design/rendering/cyclopean-reconstruction.md)
Validation: [OpenXR Validation](../../../testing/xr/openxr-validation.md)

## Current State

`EVrMirrorMode.CyclopeanReconstruct` exists as a policy value, but the renderer still needs the composition path. `VrMirrorComposeFromEyeTextures=true` is a compatibility input for submitted-eye mirror behavior. Vulkan has per-eye mirror color publication infrastructure. Per-eye sampleable depth, per-eye metadata, a fullscreen sampled composition pass, history targets, and reconstruction shaders remain open.

## Open Code Items

### Mirror contract and diagnostics

- [ ] Document and enforce the two desktop VR presentation modes. Update `RuntimeEngine.Rendering.EngineSettings`, `XRWindow`, `XRViewport`, and docs. Done when: `FullIndependentRender` renders a real desktop or cyclopean viewport, and `CyclopeanReconstruct` composes from eye textures.
- [ ] Wire `EVrMirrorMode.CyclopeanReconstruct` dispatch in desktop mirror composition. Update OpenGL and Vulkan mirror composition paths. Done when: `CyclopeanReconstruct` no longer behaves as `BlitSubmittedEye`.
- [ ] Keep `BlitSubmittedEye` as an explicit diagnostic fallback when reconstruction inputs are unavailable. Done when: missing inputs never silently select one-eye stretch behavior.
- [ ] Rename or rewrite code that describes eye-texture mirror composition as a simple one-eye blit. Done when: names and diagnostics distinguish third render, cyclopean reconstruction, blit fallback, and disabled desktop output.
- [ ] Add active mirror-path diagnostics. Done when: each frame can report third render, cyclopean reconstruction, blit fallback, held last image, or disabled desktop window.

### Per-eye resources and publish contract

- [ ] Provide Vulkan per-eye sampleable depth. Update Vulkan OpenXR eye target resources or source per-eye depth from each eye `RenderPipelineInstance`. Done when: each eye exposes a sampled depth source without mutating runtime swapchain textures.
- [ ] Add a Vulkan per-eye linear depth resolve pass. Use `R32_SFLOAT` and register the output with the resource planner as sampled-read. Done when: reconstruction consumes linear view-space depth and does not branch on device-depth conventions.
- [ ] Add OpenGL per-eye sampleable depth after Vulkan validation. Update `_viewportMirrorFbo` ownership. Done when: OpenGL exposes per-eye linear depth with the same shader contract.
- [ ] Restructure OpenGL eye mirror color targets. Done when: left and right OpenGL eye colors survive separately instead of both eyes rendering sequentially into one shared mirror FBO.
- [ ] Audit or replace `_previewLeftEyeTexture` and `_previewRightEyeTexture` before using them as reconstruction inputs. Done when: format, sRGB handling, lifetime, and ownership are valid for sampled composition.
- [ ] Publish per-eye color and resolved linear depth from the same frame. Done when: published inputs carry eye index, frame id, backend, dimensions, format, clip-space Y direction, reversed-Z state, near/far, view matrix, projection matrix, and inverse matrices.
- [ ] Reject mixed-frame left and right inputs. Done when: composition holds the last composed mirror image instead of composing from mismatched frames or dropping to a stretched eye.

### Composition pass and history

- [ ] Add a renderer facility for a fullscreen material pass at mirror-composition time. Support multiple sampled inputs and constants outside pipeline command chains. Done when: `TryRenderDesktopMirrorComposition` can draw reconstruction output instead of only blitting.
- [ ] Implement the Vulkan reconstruction pass with pipeline-override and render-pass-index scopes. Done when: sampled inputs and output are declared to the resource planner.
- [ ] Implement the OpenGL reconstruction pass after Vulkan validation. Done when: it uses existing fullscreen-triangle infrastructure and honors current-context FBO rules.
- [ ] Add persistent middle color and middle depth history targets. Done when: targets resize with output, are declared to the resource planner, and invalidate on resize, mirror-mode change, VR session restart, and large pose discontinuity.

### Cyclopean camera and shader

- [ ] Build the cyclopean view pose from the midpoint between left and right eye poses. Use HMD orientation for the first implementation and reuse the smoothed `CyclopeanDesktop` camera pose. Done when: reconstruction matches combined visibility and mirror cadence.
- [ ] Define middle projection and FOV policy for spectator output. Done when: aspect ratio is preserved when output dimensions differ from eye render targets.
- [ ] Add optional fixation or gaze support after the base midpoint path is stable. Done when: gaze only biases seed depth and the base path does not require eye tracking.
- [ ] Add the backend-neutral reconstruction shader contract. Done when: it binds left/right sRGB color, left/right linear depth, inverse eye view-projection matrices, middle view-projection matrix, eye-to-middle transforms, depth clamp range, seed depth, output size, and per-eye texel size.
- [ ] Implement screen-space gather and fixed-point reconstruction. Done when: each middle pixel samples both eyes, iterates two or three times, and does not reproject a point back into the same eye as a false refinement.
- [ ] Add discontinuity handling, reprojection validation, soft eye weights, temporal accumulation, invalid-pixel fill, and debug modes. Done when: output avoids hard left/right switching flicker and provides contribution, error, invalid, iteration, and final-color debug views.

### Tests and documentation

- [ ] Add source-contract tests for mirror mode dispatch. Done when: `VrMirrorComposeFromEyeTextures=true` and `EVrMirrorMode.CyclopeanReconstruct` route to reconstruction, while `BlitSubmittedEye` remains explicit.
- [ ] Add tests for linear depth resolve, missing-depth diagnostics, per-eye matrix/frame-id snapshots, mixed-frame rejection, held-last-image behavior, and `VR.AllowDesktopEditing=false` third-render behavior. Done when: each contract is covered without reading Markdown docs.
- [ ] Update `docs/architecture/rendering/openxr-vr-rendering.md` and `docs/architecture/rendering/vr-output-pacing-and-mirror-policy.md` when implementation lands. Done when: the docs describe the reconstruction path, per-eye publish contract, fullscreen pass facility, and troubleshooting limits.
- [ ] Update Unit Testing World docs if launch settings or defaults change. Done when: all settings and env vars used by validation are documented.

## Decisions Needed

- [ ] Should `CyclopeanReconstruct` stay opt-in or become the default runtime spectator mirror after validation? Owner: Rendering / XR.
- [ ] Which quality/cost target is acceptable compared with the full third-render desktop camera path? Owner: Rendering.

## Out Of Scope

- Replacing the full third desktop render path.
- Adding CPU readback composition.
- Hiding missing depth inputs behind a silent one-eye fallback.
- Requiring eye tracking for the base reconstruction path.
- Using a raw 50/50 left-plus-right average as invalid-pixel fallback.
