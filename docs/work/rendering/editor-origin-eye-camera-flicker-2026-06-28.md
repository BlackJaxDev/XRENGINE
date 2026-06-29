# Editor Origin / Eye Camera Flicker Investigation

Status: active

Run root: `Build/_AgentValidation/20260628-150741-editor-origin-flicker`

## Problem Statement

The main editor view flickers heavily when the editor camera is closest to the world origin and the two OpenXR eye cameras. The issue appears spatially dependent: it is much worse near the origin/eye rig than when the editor camera is farther away. This has been reported as an ongoing issue, and collect-visible instability is a suspected cause.

## Initial Hypotheses

- The desktop editor viewport and OpenXR eye viewports may still share mutable render or visibility state even though OpenXR owns separate eye viewports and pipeline instances.
- `CollectVisible` may be stable in isolation but unstable when desktop and OpenXR collection/rendering interleave around cameras that are spatially close.
- Some per-camera data may still be stored globally on scene components or renderer state. A known example already exists: OpenXR eye collection intentionally avoids `World.Lights.UpdateCameraLightIntersections()` because light intersection data is stored on light components and can flicker the desktop view.
- The combined stereo culling path may over-collect or under-collect near the rig if the HMD render matrix, eye inverse local matrices, or collection volume are stale for one frame.
- The main editor camera may be collecting/rendering the VR rig or eye-camera debug geometry in a way that aliases with OpenXR preview/mirror resources only when the editor camera is close.
- The repeated `DispatchCompute emitted with invalid render-graph pass index ... Falling back to pass 100018` warning may indicate context leakage or a command-chain fallback that is harmless for timing but harmful for frame-to-frame stability.

## Code Facts To Verify Against Evidence

- `OpenXrCollectVisible` consumes the pending predicted OpenXR frame on the engine collect-visible path.
- `OpenXrCollectVisible` resolves strict scene VR rig eye cameras from `RuntimeEngine.VRState.ViewInformation`; it should not fall back to the desktop/editor camera.
- OpenXR eye collection uses a shared `RenderCommandCollection` via `EnsureOpenXrSharedMeshRenderCommands`.
- `CollectOpenXrStereoVisible` combines left/right projection matrices and then calls `_openXrLeftViewport.CollectVisible(...)` once with `renderCommandsOverride: sharedMeshCommands`.
- `XRViewport.CollectVisible` updates global light/camera intersections only when `AssociatedPlayer` is non-null.
- `ApplyOpenXrEyePoseForRenderThread` writes late eye render matrices directly with `camera.Transform.SetRenderMatrix(...)`.
- Vulkan OpenXR eye recording still mutates renderer-wide target/resource-planner state; that is a performance architecture hazard and may also be worth ruling out for flicker if captures correlate with eye rendering.

## Investigation Plan

1. Build the editor so the running process matches current source.
2. Launch the Unit Testing World with Vulkan, Monado OpenXR, MCP, command chains, parallel packet build, and OpenXR eye-output capture enabled.
3. Use MCP to set the editor camera near the origin/eye rig with `duration=0`.
4. Capture multiple desktop viewport screenshots from the same near-origin pose.
5. Capture left and right OpenXR preview textures from the same period.
6. Move the editor camera farther from the origin while looking back at the same point.
7. Capture multiple desktop viewport screenshots plus left/right previews again.
8. Inspect saved PNGs visually, not just tool return values.
9. Close the editor and review the latest logs:
   - `log_vulkan.log`
   - `log_rendering.log`
   - `profiler-render-stalls.log`
   - `profiler-fps-drops.log`
   - `profiler-render-stats.ndjson`
10. Record whether the artifact is view-dependent, capture/readback-only, or visible in pipeline textures/eye previews.

## Evidence Log

### 2026-06-28 Setup

- Created run root: `Build/_AgentValidation/20260628-150741-editor-origin-flicker`
- Found MCP helper: `Tools/Invoke-Mcp.ps1`
- Current MCP docs are under `docs/user-guide/ai/mcp-server.md` and `docs/developer-guides/ai/mcp-server.md`; the older `docs/features/mcp-server.md` path from AGENTS is stale.
- Relevant MCP tools confirmed in source:
  - `set_editor_camera_view`
  - `capture_viewport_screenshot`
  - `capture_openxr_eye_preview_texture`
  - `list_render_pipeline_resources`
  - `capture_render_pipeline_texture`

Next action: build and launch the editor, then capture near-origin and far-view evidence.

### 2026-06-28 Explicit Launch vs F5-Equivalent

- The explicit MCP launch using the fuller Vulkan/OpenXR diagnostic environment did not reproduce visible flicker for the user.
- Moving the editor camera close to the OpenXR rig did not collapse the live desktop viewport in the control run:
  - local player viewport stayed `1653x930`
  - active render state stayed `DefaultRenderPipeline` / `Editor View` at `1653x930`
- `capture_viewport_screenshot` and `capture_render_pipeline_texture(FinalPostProcessOutputTexture)` returned black data in this Vulkan editor path even when the live viewport was visible. Treat desktop MCP texture screenshots as unreliable evidence for this investigation until fixed separately.
- OpenXR eye preview capture is only reliable when the diagnostic eye-copy path is enabled. With the F5-equivalent launch, left/right preview captures were full-size `896x1007` but all-zero because the copy path was not enabled; this matches the older performance note that black preview captures are expected with eye-output copy disabled.

### 2026-06-28 Left-Eye Flicker Under F5-Equivalent Launch

User report: after starting the unit-testing editor in an F5-shaped configuration, the left eye started flickering.

F5-equivalent launch differences observed:

- Started `XREngine.Editor.exe --unit-testing --mcp --mcp-allow-all --mcp-port 5467`
- Used the VS Code launch profile's lean env shape:
  - `XRE_WORLD_MODE=UnitTesting`
  - `XRE_ENABLE_OPENGL_COMPILE_LINK_WORKER_POOL=1`
  - no explicit `XRE_VULKAN_VALIDATION=0`
  - no explicit `XRE_VULKAN_CAPTURE_EYE_OUTPUTS=1`
  - no explicit command-chain/parallel-packet env overrides
- The run still used Vulkan + Monado OpenXR from `Assets/UnitTestingWorldSettings.jsonc`.
- The log loaded Vulkan validation, but recorded no `VUID` entries and no `ERROR` entries during the sampled period.

Key log evidence:

- F5-equivalent session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_15-19-05_pid28536`
- During the left-eye flicker period, `log_vulkan.log` repeatedly reported physical image handles changing for default-pipeline resources such as `Normal`, `DepthStencil`, `AlbedoOpacity`, `RMSE`, `TsrHistoryColor`, `PostProcessOutputTexture`, and `FinalPostProcessOutputTexture`.
- The same period repeatedly rebuilt most default-pipeline FBOs:
  - `DeferredGBufferFBO`
  - `AmbientOcclusionBlurFBO`
  - `GTAOBlurIntermediateFBO`
  - `GBufferFBO`
  - `LightingAccumFBO`
  - `ForwardPassFBO`
  - `VelocityFBO`
  - bloom down/up-sample FBOs
  - `PostProcessOutputFBO`
  - `FinalPostProcessOutputFBO`
- Warning counts from sampled runs:
  - explicit first run: `419` physical-handle changes, `551` FBO rebuilds
  - explicit control run: `2600` physical-handle changes, `3772` FBO rebuilds
  - F5-equivalent run: `4969` physical-handle changes, `7394` FBO rebuilds
- All sampled runs had one early `Command buffer for image ... was dirtied after recording and before submit` recovery and no Vulkan validation errors.

Interpretation:

- This does not look random. It looks like deterministic render-resource state churn whose visual expression is timing-dependent.
- The strongest current hypothesis is not `CollectVisible` instability. It is Vulkan resource planner / render-object cache instability across desktop, left-eye, and right-eye render contexts.
- OpenXR uses two eye resource-planner states (`OpenXrEyeResourcePlannerStateCount = 2`) and restores them around eye recording.
- `VkImageBackedTexture` resolves allocator-backed images by logical texture name from the renderer's currently active resource allocator. When the renderer switches planner state, the same Vulkan texture wrapper can observe a different physical image for the same logical resource name, destroy/recreate image views, and invalidate framebuffer attachment signatures.
- `VkFrameBuffer.EnsureCurrent()` then sees different attachment views and rebuilds framebuffers. The log shows this happening continuously.
- That explains why the issue can appear as "left eye only" or "only under F5": the underlying churn exists broadly, but the visible flicker depends on scheduling, validation/diagnostic overhead, command-buffer reuse state, and which planner state was last used.

Next fix direction:

- Treat resource/view/framebuffer identity as scoped to the active render context or resource-planner state, not only to the engine-side texture/FBO object plus logical resource name.
- Start by eliminating the per-eye physical-image/view ping-pong:
  - either make the Vulkan render-object cache resource-planner-state-aware for allocator-backed textures/FBOs, or
  - add a safe per-physical-image image-view cache plus matching cleanup on physical resource destruction, then add per-attachment-signature framebuffer caching.
- Do not continue performance tuning until this instability is fixed or intentionally isolated, because it can invalidate any framerate measurements.

## Active Questions

- Does flicker show up in saved viewport screenshots, or only live in the editor window?
- Do left/right OpenXR preview textures remain stable while the desktop view flickers?
- Does the desktop final post-process texture flicker along with the presented viewport?
- Are visible command counts or command-chain reuse/refresh counts oscillating when the camera is close to the eye rig?
- Are OpenXR collect-visible frame numbers, rig validation logs, or render-pose logs discontinuous during the flicker?
- Is the issue worse under `CollectVisibleThread` pacing than `DedicatedThread`, or is it independent of OpenXR pacing mode?
