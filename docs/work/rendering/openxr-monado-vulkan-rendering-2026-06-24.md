# OpenXR Monado Vulkan Rendering Investigation

## Problem

Monado's windowed OpenXR eyes can render while the main editor window remains black or red. In longer editor runs the eye images redraw slowly/stale, then Vulkan reports device loss and the Monado window closes/reopens.

## Evidence

- Failing run: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-24_23-00-26_pid40020`.
- Primary failure: `Logical device lost. Reason=OpenXR Vulkan eye fence wait returned ErrorDeviceLost.`
- Validation errors in that run appear after device loss and look like teardown fallout.
- Before device loss, OpenXR eye/mirror blits were emitted without a valid render pipeline/pass:
  - invalid render-graph pass index `int.MinValue`
  - `CurrentPipeline=null`
  - dropped `BlitOp`
  - resource planner switching between the eye pipeline registry and a null registry
- Live editor validation after the pass-context fix exposed a second issue:
  - `vkCmdBlitImage` wrote into OpenXR Vulkan swapchain images that were created without `VK_IMAGE_USAGE_TRANSFER_DST_BIT`
  - validation reported `VUID-vkCmdBlitImage-dstImage-00224`
  - this matched the stale/inconsistent Monado-eye redraw path because the command stream was invalid before the eventual device loss

## Fixes Applied

- Wrap Vulkan OpenXR eye swapchain blits in the active eye viewport render pipeline and `PostRender` pass.
- Wrap Vulkan desktop mirror composition blits in an OpenXR eye pipeline and `PostRender` pass.
- Create Vulkan OpenXR swapchains with `ColorAttachment | TransferDst`, preferring `Sampled` as an additional usage when the runtime accepts it.
- Remove the OpenXR Monado VS Code task's hardcoded `XRE_UNIT_TEST_RENDER_API=OpenGL` so the task honors `Assets/UnitTestingWorldSettings.jsonc`.

## Validation

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -v:minimal`: passed with 0 warnings, 0 errors.
- `Tools\OpenXR\Run-OpenXrMonadoSmoke.ps1 -Renderer Vulkan -SmokeFrames 90 -TimeoutSeconds 120 -NoBuild -SkipAllocationAudit`: passed.
- Smoke run: `Build/_AgentValidation/20260624-232747-openxr-monado-smoke`.
- Smoke summary reported 88 submitted frames, 0 end-frame failures, 0 per-frame allocations, and completed teardown.
- Fresh smoke logs had none of the earlier invalid-pass blit, null-pipeline blit, resource-registry-to-zero, Vulkan layout VUID, or device-loss signatures.
- `Tools\OpenXR\Run-OpenXrMonadoSmoke.ps1 -Renderer Vulkan -SmokeFrames 120 -TimeoutSeconds 150 -NoBuild -SkipAllocationAudit`: passed after the `TransferDst` usage fix.
- Smoke run: `Build/_AgentValidation/20260624-233218-openxr-monado-smoke`.
- The 120-frame smoke summary reported 118 submitted frames, 2 no-layer frames, 0 end-frame failures, and completed teardown.
- A live editor MCP validation ran for roughly 60 seconds with JSONC-selected Vulkan and Monado OpenXR:
  - run root: `Build/_AgentValidation/20260624-233443-live-openxr-editor-60s`
  - log directory: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-24_23-34-43_pid32088`
  - three delayed viewport screenshots succeeded from two camera angles
  - pre-stop log scan found no device loss, transfer-dst VUIDs, dropped blits, null-pipeline blits, or invalid render-graph pass signatures
- The first long-run capture looked bright/washed out from the chosen sky/horizon view, but later captures showed the scene and avatar silhouette and changed with the camera.

## Next Check

If the user still observes visual issues, capture the Monado popup and editor window with RenderDoc or a window capture while leaving the editor running longer than one minute. Current local evidence no longer reproduces the black/red editor window or Vulkan device-loss path.

## 2026-06-25 Follow-up

The issue reproduced again after longer runs. The eye renderer no longer failed on invalid blits, but the first device-loss stack moved into synchronous resource upload during OpenXR eye rendering.

New failing evidence:

- Run root: `Build/_AgentValidation/20260625-011213-openxr-no-sync-upload`.
- Log directory: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_01-12-14_pid35212`.
- The first failure was `Logical device lost. Reason=QueueSubmit returned ErrorDeviceLost`.
- The stack showed a one-shot `VkDataBuffer` upload via `ExecuteTransferBufferUpload` while an OpenXR eye frame was being emitted.
- The previous texture-upload failure had already moved away after descriptor image resolution was changed to avoid synchronous texture uploads during external swapchain rendering.

Additional fixes applied:

- Added non-mutating descriptor readiness checks for Vulkan image descriptor sources.
- Prevented mesh, material, bindless, compute, and ImGui descriptor resolution from generating/uploading textures while rendering an external OpenXR swapchain image.
- Added non-mutating readiness checks for Vulkan data buffers.
- Prevented mesh/index buffer preparation, descriptor buffer resolution, compute descriptor buffers, vertex binding, and transform feedback buffer resolution from synchronously generating/uploading buffers while rendering an external OpenXR swapchain image.
- Added descriptor readiness/generation to mesh and material resource fingerprints so placeholder descriptor sets do not get cached under stale real-texture keys.

Validation after buffer guard:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -v:minimal`: passed with 0 warnings, 0 errors.
- Smoke run: `Build/_AgentValidation/20260625-011830-openxr-no-sync-buffer-upload`.
- Log directory: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_01-18-31_pid40936`.
- Summary: 238 submitted OpenXR frames, 2 no-layer frames, 0 end-frame failures, 0 per-frame allocations, all acquired eye images released, exit code 0.
- Log scan found no `Logical device lost`, `ErrorDeviceLost`, `ErrorRuntimeFailure`, OpenXR eye submit failure, or one-shot submit failure before requested shutdown.
- `BootstrapRenderSettings` confirmed `RenderWindowsWhileInVR=True` and `VrMirrorComposeFromEyeTextures=False`, matching the JSONC request for desktop editing while VR is active.

Remaining cleanup:

- The successful smoke still reports shutdown-only Vulkan validation errors for imported texture upload views/samplers not destroyed before device teardown, and one stale pipeline layout destroy. These happen after the smoke target requests OpenXR session exit and are not the Monado restart cause, but they should be cleaned up separately.

## 2026-06-25 Black Frame Follow-up

The user reported a new state after the device-loss fixes: no crash, but the desktop scene flashed between black and partially rendered meshes, the skybox was unreliable, the Monado window stayed black, and disabling `AllowDesktopEditing` made both windows black.

Findings:

- `AllowDesktopEditing=false` intentionally changes the unit-testing world to mirror XR eye textures on the desktop window (`VrMirrorComposeFromEyeTextures=true`) instead of rendering an independent editor view. That explains why the main window could show an eye/mirror view rather than the desktop editor when desktop editing is disabled.
- The black/flashing frames were still rooted in external OpenXR swapchain rendering, but the failure mode moved from device loss to resource readiness. OpenXR eye rendering suppresses synchronous uploads, so first-use mesh/material descriptors could be recorded before shadow, texture, and mesh-buffer resources became descriptor-ready.
- The previous retained smoke sessions (`xrengine_2026-06-25_01-53-23_pid40992` and `xrengine_2026-06-25_01-53-59_pid34872`) no longer contained live device-loss or descriptor-bind failure signatures. They did still show startup-only mesh readiness warnings and shutdown-only imported texture view/sampler leaks.

Additional fixes applied:

- Added an OpenXR Vulkan prewarm path that runs before acquiring eye swapchain images. It prepares render-graph resources, mesh buffers, draw uniforms, captured material/program bindings, and descriptor sets while synchronous uploads are temporarily allowed.
- Split Vulkan OpenXR resource-planner state per eye to stop left and right eyes from invalidating each other's physical resource plans.
- Published rendered Vulkan eye images into `OpenXRViewportMirrorColor` and the desktop mirror target so Monado preview and desktop mirror composition use the same rendered eye image path.
- Marked Vulkan image-backed render-target/framebuffer descriptor sources ready once their view/sampler handles exist, avoiding false "descriptor not ready" black-frame fallbacks for planner-owned textures.
- Created dummy shadow fallback data eagerly so shadow descriptors have actual storage on the first eye frames.

Validation after the latest fix:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj -v:minimal`: passed with 0 warnings, 0 errors.
- Direct Monado/Vulkan editor smoke: `Build/_AgentValidation/20260625-015610-openxr-vulkan-direct-smoke`.
- Log directory: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_01-56-10_pid37784`.
- Smoke summary: 118 submitted frames, 2 no-layer frames during state transitions, 0 end-frame failures, per-eye acquire/wait/release counts `[118, 118]`, `desktopMirrorComposed=true`, 0 per-frame allocation bytes.
- Vulkan log scan found 0 `ErrorDeviceLost`, 0 queue-submit failures, 0 skipped descriptor binds, 0 texture-descriptor-not-ready warnings, 0 shadow-descriptor-not-ready warnings, 0 OpenXR prewarm failures, and 0 missing swapchain writes.
- Remaining validation noise is still teardown-only `vkDestroyDevice` child-object leaks for imported texture upload views/samplers. It should be fixed, but it is not the live black Monado/editor rendering path.

Current interpretation:

- With `AllowDesktopEditing=true`, the desktop window should remain the editor view while Monado receives the XR eye views.
- With `AllowDesktopEditing=false`, the desktop window mirrors the XR eye output by design. If the user wants the desktop editor while VR is running, leave `AllowDesktopEditing=true`.
