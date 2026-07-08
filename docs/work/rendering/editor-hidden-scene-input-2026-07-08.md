# Editor Hidden Scene And Camera Input Regression - 2026-07-08

## Problem

The unit-testing editor started with the `Editor View` scene node visible in the normal world hierarchy instead of the hidden editor scene, and the editor camera no longer responded to mouse, WASD, or arrow-key input.

Active reproducing settings:

- `EditorType`: `IMGUI`
- `Rendering.RenderBackend`: `Vulkan`
- `VR.Mode`: `OpenXR`
- `VR.AllowDesktopEditing`: `true`
- `AddCameraVRPickup`: `true`
- `Locomotion`: `false`

## Findings

- The live repro was caused by `AddCameraVRPickup = true` in `Assets/UnitTestingWorldSettings.jsonc`.
- The old VR pawn path treated `AddCameraVRPickup` as a reason to create and possess the desktop camera. If `VR.AllowDesktopEditing` was false, that camera became a plain `PawnComponent`, which cannot be moved by the ImGui editor camera controls.
- The pickup setting had two unrelated responsibilities mixed together: it both added a grabbable camera body and influenced the main desktop editor camera.
- The speculative hidden-scene and ImGui input changes tried during the investigation were not the root cause and were reverted.
- The eye previews and pickup camera preview later disappeared because their display quads were created on the UI `Background` pass with very low Z values. The native editor and Dear ImGui surfaces render on later/on-top UI passes, so the preview widgets were being covered even though the preview gates and textures were active.
- The local settings file currently has `EditorType = IMGUI`, not `Native`; that does not disable the native preview widgets, but it does mean the full native editor component is not selected by the active JSONC.
- Editor camera drag/zoom-to-depth was blocked by an OpenXR-specific guard in `EditorFlyingCameraPawnComponent.PostRender`. When a depth query was requested during OpenXR desktop/VR rendering, the external-swapchain guard could return before calling `GetDepthHit(...)`, leaving `_depthQueryRequested` set and causing pending scroll deltas to wait indefinitely.
- The VR eye previews and pickup camera preview were texture-backed UI quads using the default transparent UI blend state. OpenXR preview textures and offscreen viewport render targets may carry zero alpha even when their RGB data is valid, so the native UI blended them as fully transparent.

## Fix

- `VR.AllowDesktopEditing` now exclusively controls whether a possessed desktop editor camera is created.
- `AddCameraVRPickup` now creates a separate scene camera named `VR Pickup Camera` with its own `DynamicRigidBodyComponent`.
- The pickup camera is not possessed and does not replace the editor fly camera.
- The editor bridge gained `CreateCameraPreviewUi(...)`.
- The native editor UI now creates a bottom-of-screen `UIViewportComponent` preview bound to the pickup camera, using the same texture-backed native UI approach as the VR eye preview overlay.
- The preview viewport disables automatic collect/swap subscriptions and lets `UIViewportComponent` drive its own offscreen render, avoiding duplicate per-frame viewport work.
- The VR eye previews and pickup camera preview now use the UI `OnTopForward` overlay pass and are created after the Dear ImGui/native editor surface, so they render over the editor instead of behind it.
- A later live log still showed the preview components being submitted, but `UserInterfaceRenderPipeline` left `OnTopForward` unsorted. This meant the high preview `ZIndex` values were ignored and the previews could still be covered by Dear ImGui/native UI commands.
- The same run logged 77,077 `VkBufferUploadQueue` rows in `log_vulkan.log`. That trace was being enabled automatically by `Debugger.IsAttached`, producing heavy file I/O during ordinary debug sessions.
- `UserInterfaceRenderPipeline` now sorts `OnTopForward` with the near-to-far sorter so high-Z preview overlays draw after lower-Z editor UI.
- `RenderDiagnosticsFlags.UploadStageLogging` is now explicit opt-in through `XRE_UPLOAD_STAGE_LOGGING` or the editor preference, not automatic under the debugger.
- Local global editor preferences at `%LOCALAPPDATA%\XREngine\Global\Config\editor_preferences_global.asset` also had `UploadStageLogging: true`; this was changed to `false` for the current machine so the next debug launch is not still noisy through persisted settings.
- A subsequent selection-lag run showed upload tracing was gone, but selection still hit the Vulkan GPU-BVH-picking fallback path repeatedly. The fallback logged that GPU BVH raycast is OpenGL-only, then used exact CPU mesh picking. On Sponza-size meshes this can freeze the editor during hover/click selection.
- Unsupported GPU-BVH picking now returns a cheap coarse bounds hit for selection instead of falling through to exact CPU triangle traversal. This preserves scene-node selection while avoiding the worst CPU path until Vulkan BVH readback is implemented.
- The lower-level BVH dispatcher warning now reports unsupported-backend request rejection instead of saying it is falling back to CPU mesh picking; selection fallback policy lives in `XRWorldInstance`.
- The same local global editor preferences had additional heavy diagnostics enabled (`RenderTransformDebugInfo`, `RenderTransformLines`, `GLSubmitTraceLevel`, `EnableGpuRenderPipelineProfiling`, `ProfilerPanelShowBvhMetrics`, plus hover/selection outlines). These were disabled locally to keep the editor responsive during Vulkan+VR debugging.
- `EditorFlyingCameraPawnComponent.PostRender` now resolves and clears depth-hit requests against the editor viewport FBO even while OpenXR is active, instead of early-returning on the thread-local external-swapchain flag.
- `UIMaterialComponent` gained `SetBlendModeAllDrawBuffers(...)`, and render-target preview widgets were first moved to opaque-factor blending so preview texture alpha could not hide the image.
- A live MCP/OpenXR run after that change showed the hidden-scene nodes were present and active (`VR Stereo Preview`, `Left Eye Preview`, `Right Eye Preview`, `VR Pickup Camera Preview`, `Editor View`), and direct MCP captures of both OpenXR eye preview textures were nonblack with alpha 1.0. The native window screenshot still did not show the eye or pickup preview quads, so the alpha-only explanation was insufficient.
- The same run showed `VR Pickup Camera Preview` being accepted by `ShouldRender2D`, but not visible in the native window capture. This narrowed the remaining issue to UI presentation/composition or texture sampling at draw time, not missing scene nodes.
- The Vulkan log later emitted `Deferring Vulkan eye mirror copy to 'preview eye 0/1'` because allocator pressure was high (`allocated` about 19.6-19.8 GB). This can make eye preview textures stale or absent even when the UI quads are fixed, so validation needs to watch both screenshot output and allocator-pressure warnings.
- Preview overlays now force a stable topmost presentation path: the eye and pickup preview components disable batching, use disabled blending instead of transparent/opaque-factor blending, render at `PreviewOverlayZIndex = int.MaxValue`, and the FPS overlay is lowered to `int.MaxValue - 100` so it cannot cover the bottom pickup preview.
- Bounded `[PreviewOverlayDiag]` logs were added to report each preview quad's render-time active state, actual bounds, bottom-left position, world translation, render pass, Z index, and bound texture during the next live screenshot pass.

## Validation

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests.UnitTestingWorld_DesktopEditingCameraRemainsFlyableWhenVrPickupIsEnabled" -p:OutDir=Build\_AgentValidation\20260708-010000-editor-input-still-broken\temp-build\tests\` passed.
- The same focused test passed again after the overlay layering fix.
- `dotnet build .\XREngine.Runtime.Bootstrap\XREngine.Runtime.Bootstrap.csproj --no-restore -p:OutDir=Build\_AgentValidation\20260708-010000-editor-input-still-broken\temp-build\bootstrap\` passed.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests.UnitTestingWorld_DesktopEditingCameraRemainsFlyableWhenVrPickupIsEnabled|FullyQualifiedName~OpenXrTimingPipelineContractTests.HeavyUploadStageLogging_IsExplicitOptIn|FullyQualifiedName~OpenXrTimingPipelineContractTests.UnsupportedGpuMeshBvhPicking_UsesCoarseBoundsInsteadOfExactCpuTriangleWalk" -p:OutDir=Build\_AgentValidation\20260708-010000-editor-input-still-broken\temp-build\tests\` passed.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests.UnitTestingWorld_DesktopEditingCameraRemainsFlyableWhenVrPickupIsEnabled|FullyQualifiedName~OpenXrTimingPipelineContractTests.EditorDepthHitAndPreviewRenderTargets_DoNotDependOnOpenXrSwapchainAlpha|FullyQualifiedName~OpenXrTimingPipelineContractTests.HeavyUploadStageLogging_IsExplicitOptIn|FullyQualifiedName~OpenXrTimingPipelineContractTests.UnsupportedGpuMeshBvhPicking_UsesCoarseBoundsInsteadOfExactCpuTriangleWalk" -p:OutDir=Build\_AgentValidation\20260708-010000-editor-input-still-broken\temp-build\tests\` passed.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -p:OutDir=Build\_AgentValidation\20260708-010000-editor-input-still-broken\temp-build\editor\` passed.
- Validation still reports existing Magick.NET advisory warnings and an existing nullable warning in `VulkanRenderer.CommandChainLowering.cs`.
- Pending: run a live editor session after the coarse Vulkan GPU-BVH selection fallback and confirm scene-node selection no longer stalls.
- Pending: run a live OpenXR editor session and confirm drag/zoom-to-depth, top eye previews, and bottom pickup camera preview all behave on the native UI surface.
- Pending: run a live screenshot after the topmost/unbatched preview overlay fix; inspect `[PreviewOverlayDiag]` and Vulkan allocator-pressure warnings if previews still do not appear.

## Follow-Up

- Run a live editor pass with `AddCameraVRPickup = true` and confirm the hierarchy contains `VR Pickup Camera`, the main possessed pawn remains `EditorFlyingCameraPawnComponent` when desktop editing is enabled, and the bottom native preview renders from the pickup camera.
