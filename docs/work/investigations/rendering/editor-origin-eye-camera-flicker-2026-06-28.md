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

### 2026-06-29 Desktop Deferred Flicker With Occlusion Disabled

User report: the current artifact is desktop/editor-view only. Deferred meshes disappear while the desktop camera is moving, leaving forward meshes and the skybox, then reappear when camera motion settles. Disabling occlusion culling does not change the symptom.

Latest log evidence:

- Session inspected: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-29_12-31-14_pid29776`
- Rendering profile line reported:
  - `MeshStrategy=CpuDirect`
  - `Occlusion=Disabled->Disabled`
  - `GpuDispatch=False(requested=False)`
- `log_vulkan.log` did not show the earlier continuous physical-image handle changes or FBO rebuild churn during this repro.
- The remaining repeated render-graph warning was Forward+ compute dispatch running without an active declared pass, not deferred mesh visibility itself.

Interpretation:

- Occlusion is ruled out for this current desktop deferred-mesh flicker.
- GPU indirect / GPU dispatch is also ruled out for this repro because the renderer is in `CpuDirect`.
- The older resource-planner/FBO-churn hypothesis does not explain this specific current symptom because the latest run has stable FBO/resource identity.
- The stronger current hypothesis is mutable shared render-command sort state:
  - `RenderCommand3D.CollectedForRender(camera)` updates `RenderDistance` on the shared `RenderCommand` instance.
  - `RenderCommandCollection.AddCPU()` updates `SortOrderKey` on that same shared instance.
  - Sorted CPU passes previously stored the live command object in a `SortedSet<RenderCommand>`.
  - Desktop, OpenXR eye, shadow, and capture viewports can collect or render overlapping shared commands at different times.
  - Mutating comparison keys after insertion violates `SortedSet` ordering invariants and can produce view/motion-dependent pass instability, especially in sorted deferred passes.

Implemented isolation/fix:

- Replaced sorted CPU pass storage with a snapshot-sorted collection that captures `RenderDistance` and `SortOrderKey` at collection time instead of comparing live mutable command fields during render.
- Preserved reference-based membership so the same command is only inserted once per pass collection.
- Added ordering tests covering live `RenderDistance` mutation after collection.
- Corrected GPU view-set mirror inclusion to treat `VrMirrorComposeFromEyeTextures=true` as mirror composition and `false` as an independent desktop render.
- Added Forward+ pass-index scoping so the remaining Vulkan warning identifies a real pass context instead of falling back to a synthetic invalid-pass path.

Validation:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj -v:minimal` passed with 0 warnings/errors.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~RenderCommandCollectionOrderingTests|FullyQualifiedName~GpuIndirectPhase7ZeroReadbackTests.ForwardPlusLightCulling_ExecutesInsideDeclaredRenderGraphPass"` passed: 6 tests.
- Test restore/build still reports existing Magick.NET advisory warnings; these are unrelated to the rendering changes.

Next validation:

- Re-run the editor with occlusion disabled and the same desktop camera-motion repro.
- If flicker persists, capture per-pass desktop command counts for `OpaqueDeferred` and compare `RenderCPU(pass)` invocation counts against visible draw counts while moving vs stationary.
- Also inspect whether any deferred-only material bucket is being skipped by render-pass state transitions rather than command collection.

### 2026-06-29 AA-Off Baseline

User report after the snapshot-sort fix: no visible change; the desktop deferred meshes still flicker/disappear in the same way.

Isolation change:

- Set `Assets/UnitTestingWorldSettings.jsonc` `CameraAntiAliasingModeOverride` to `"None"`.
- This forces the constructed unit-testing camera to bypass the saved/global AA mode for the next editor launch.
- Treat this as an isolation baseline, not a root-cause claim: if the symptom persists with AA off, TSR/TAA/motion-blur history paths are ruled out for this repro and the next pass should focus on deferred GBuffer/light-combine/ForwardPassFBO ordering and clear/load state.

### 2026-06-29 Desktop VR Command-Sharing Diagnosis

User report after AA-off testing: occlusion culling and temporal AA were not the cause.

New source finding:

- `VR.AllowDesktopEditing=false` creates a smoothed first-person desktop camera parented under the HMD node, which is the intended real third-render path.
- However, `Engine.VRState.ConfigureDesktopViewportForVrWindow` still routed the desktop viewport to `_sharedMeshRenderCommands` whenever `RenderWindowsWhileInVR=false`, even when `VrMirrorComposeFromEyeTextures=false`.
- `UnitTestingWorld` detected `usesRuntimeDesktopCamera` but did not force `RenderWindowsWhileInVR=true`, so the runtime desktop camera mode could still enter the shared stereo-command path.

Interpretation:

- This explains why occlusion and AA changes did not affect the symptom.
- The desktop viewport was able to render with a stereo-eye/shared visible command buffer rather than collecting for the smoothed HMD-parented desktop camera.
- Forward meshes and skybox can remain visible while deferred meshes blink because the artifact is pass/content dependent on which commands survive the shared stereo collection and render timing.

Implemented fix:

- Force `RenderWindowsWhileInVR=true` for `usesRuntimeDesktopCamera` in Unit Testing World settings normalization.
- Make desktop viewport command sharing depend only on `VrMirrorComposeFromEyeTextures=true`.
- When mirror composition is false, clear `MeshRenderCommandsOverride` and re-enable automatic desktop `CollectVisible`/`SwapBuffers`, so the runtime cyclopean camera uses its own third-render command collection.
- Tightened source-contract coverage to prevent reintroducing `!RenderWindowsWhileInVR` as a command-sharing condition.

### 2026-06-29 OpenXR HMD Child Render-Matrix Propagation

User report after the independent desktop command-buffer fix: no luck; the desktop deferred meshes still flicker/disappear in the same way.

Additional evidence:

- Latest logs show the current repro already uses `RenderWindowsWhileInVR=True`, `VrMirrorComposeFromEyeTextures=False`, `MeshStrategy=CpuDirect`, `GpuDispatch=False`, `Occlusion=Disabled`, and AA off.
- That rules out the earlier shared-eye command buffer path, GPU culling/dispatch, occlusion, and temporal AA for the current symptom.
- OpenXR `Window_RenderViewportsCallback` late-updates tracked poses and invokes `RuntimeEngine.VRState.InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming.Late)` immediately before the normal desktop viewport render.
- `VRDeviceTransformBase.VRState_RecalcMatrixOnDraw` called `SetRenderMatrix(..., recalcAllChildRenderMatrices: true)` for the HMD.
- The runtime desktop camera is a `SmoothedParentConstraintTransform` child of the HMD. Its `LocalMatrix` is intentionally identity because its actual world matrix is `_currentMatrix`.
- Parent render-matrix propagation therefore recomposed the desktop camera child as `Identity * HmdLateRenderMatrix`, overwriting the smoothed desktop camera render matrix after desktop collect-visible had already built commands from a different/smoothed pose.

Interpretation:

- The current flicker is most likely a collect/render pose mismatch in the runtime desktop HMD camera.
- During camera/HMD motion, desktop `CollectVisible` can build deferred commands for the smoothed camera pose, then OpenXR late pose propagation can replace the desktop camera render matrix with the raw late HMD pose before desktop render.
- That explains the velocity correlation: while moving, the mismatch persists and deferred meshes can disappear; when motion slows, the smoothed and raw HMD poses converge and deferred meshes reappear.

Implemented fix:

- Prevent OpenXR HMD `RecalcMatrixOnDraw` from propagating the HMD render matrix into arbitrary child transforms.
- Set OpenXR eye camera render matrices directly for predicted collection, matching the existing direct late eye render-matrix update.
- Fix the bootstrap `RenderWindowsWhileInVR` expression so `usesRuntimeDesktopCamera` forces independent desktop rendering even when the JSON omits the setting.
- Updated OpenXR timing contract coverage for the HMD child-propagation guard and direct predicted eye render-matrix update.

Validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests.RuntimeVrDesktopView_DoesNotReuseEyeCommandsOrEditorImGuiWhenDesktopEditingDisabled|FullyQualifiedName~OpenXrTimingPipelineContractTests.UnitTestingWorld_OpenXrLaneOverridesAndMixedModeWarningAreExplicit" --no-restore -v:minimal` passed: 2 tests.
- Restore/build still reports existing Magick.NET advisory warnings; these are unrelated.

### 2026-06-29 Vulkan Scheduled Secondary Frame-Data Refresh

User report after the HMD child-propagation fix: no luck; the desktop deferred meshes are still broken in the same way. Treat the HMD child-propagation theory as ruled out for this symptom.

New evidence:

- Latest inspected session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-29_13-42-44_pid17452`.
- The runtime desktop path is still independent (`RenderWindowsWhileInVR=True`, `VrMirrorComposeFromEyeTextures=False`) and uses `CpuDirect` with occlusion disabled.
- The latest log still reported `aa=Tsr` despite `CameraAntiAliasingModeOverride=None`, meaning the AA-off isolation did not actually reach the runtime-created VR desktop/eye cameras.
- Vulkan logs show the renderer switching resource plans between the 1920x1080 desktop pipeline and 896x1007 eye pipelines, with command buffers repeatedly dirtied by resource-plan/descriptor churn.

Source finding:

- `RecordCommandBuffer` can re-record a primary command buffer while reusing scheduled mesh secondary command buffers.
- In `TryExecuteScheduledMeshCommandChainSecondaryRun`, the reused-secondary branch only advanced the per-renderer uniform-slot counter with `GetMeshDrawUniformSlot(...)`.
- It did not refresh the reused secondary's frame data through `VkMeshRenderer.TryRefreshReusableCommandBufferFrameData(...)`.
- If visible packets or resource plans changed while the camera was moving, deferred mesh secondaries could execute with stale uniform buffers or descriptor-set contents. If refresh cannot safely reuse descriptor sets, the secondary must be re-recorded.

Implemented fix under test:

- Apply `CameraAntiAliasingModeOverride` as a launch-scoped `GameSettings.AntiAliasingModeOverride` during Unit Testing World bootstrap, so runtime-created VR desktop/eye cameras inherit the AA-off test baseline.
- Added `EffectiveAntiAliasingMode` to the bootstrap render-settings log line so future logs prove whether AA isolation is active.
- Updated `RecordMeshDrawIntoCommandBuffer` to accept an optional uniform-slot override.
- In the scheduled-secondary reuse branch, refresh frame data for the exact consumed uniform slot before executing the secondary. If refresh fails, re-record that secondary using the same slot instead of executing stale data.

Validation:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` passed.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VrViewRenderModeContractTests|FullyQualifiedName~AntiAliasingOverrideTests" --no-restore -v:minimal` failed in two existing source-contract tests:
  - `OpenXrSmokeRun_UsesStableExitCodesAndSummaryContract` still expects smoke-run constants in `Program.cs`.
  - `VulkanOpenXr_EyeSubmitRecordsBothEyesBeforeOneFenceWait` could not find its old method end marker.
- The failures appear related to earlier OpenXR source movement, not the scheduled-secondary refresh patch.

### 2026-06-29 Deferred Light-Combine Camera Fallback And Pass-Count Split

User report after the scheduled-secondary frame-data refresh: no luck; the desktop deferred meshes are still broken in the same way. Treat scheduled secondary frame-data staleness as ruled out for this symptom.

Current source findings:

- Forward mesh submission resolves the active camera as `SceneCamera ?? RenderingCamera ?? LastSceneCamera ?? LastRenderingCamera`.
- `VPRC_LightCombinePass` was the odd one out: it pushed only `RenderState.SceneCamera` while rendering light volumes, and spot-light volume culling also read only `SceneCamera`.
- That can make deferred lighting behave differently from forward rendering when Vulkan command replay or nested quad/FBO rendering temporarily lacks a live `SceneCamera`.
- The deferred-to-forward boundary is now the strongest split point: `ForwardPassFBO` is cleared, `LightCombineFBO` is rendered into it, then background and forward meshes are drawn afterward. If the `LightCombineFBO` quad samples bad/stale data or is skipped, the frame can show skybox/forward meshes while all deferred meshes vanish.
- Motion blur has the right symptom shape, but its settings default to disabled and AA-off should disable the velocity/temporal path unless another effect explicitly enables it. Do not return to TSR as the primary explanation without fresh evidence.
- Vulkan resource-planner switching is keyed by pipeline/viewport/resource registry, so duplicate resource names across desktop and eye pipelines should be protected. The remaining planner concern is the fallback path where recording keeps a pre-recorded plan during a context change.

Implemented changes:

- `VPRC_LightCombinePass` now resolves its effective camera with the same fallback pattern used by mesh rendering before pushing `RenderingCamera`.
- Spot-light light-volume culling now uses that same effective camera fallback.
- Added `RenderCommandCollection.GetUpdatingPassCommandCount(int renderPass)`.
- Added `XRViewport` pass-count diagnostics behind `DiagDeferredLighting`; when enabled, `vulkan-deferred-lighting-diagnostics.log` records collect/swap counts for background, opaque deferred, deferred decals, opaque forward, masked forward, and transparent forward per viewport.

Next split:

- If bad frames show `opaqueDeferred=0` after desktop swap, the issue is still command collection/publish for the desktop viewport.
- If bad frames show stable `opaqueDeferred` counts, focus on `LightCombineFBO -> ForwardPassFBO` rendering, descriptor/resource layout state, or the Vulkan frame-op planner context around that quad.

Validation:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` passed.
- Restore/build still reports existing Magick.NET advisory warnings; these are unrelated.

### 2026-06-29 Collection-Local Dirty Publish Queue

User report after the light-combine camera fallback: no luck; the desktop deferred meshes are still completely unfixed.

Updated interpretation:

- The current repro still sits on the CPU-direct path, so the relevant visible set is the CPU `RenderCommandCollection` rendering buffer, not GPU indirect visibility.
- The desktop viewport and OpenXR stereo path no longer intentionally share one command collection when `VrMirrorComposeFromEyeTextures=false`, but they still collect the same scene-owned `RenderCommand` instances into different command collections during the same frame.
- `RenderCommandCollection.AddCPU()` used a global `RenderCommand._swapQueued` bit to dedupe dirty-publish queue entries before any collection had actually swapped.
- If one collection collected a dirty command first, a second collection could add that command to its pass list without adding it to its dirty-publish queue. If the second collection swapped first, it rendered with stale render-side snapshots until the first collection eventually published.
- That fits the camera-velocity correlation better than occlusion/AA: moving the camera/VR rig keeps commands and derived render snapshots changing, while stationary frames converge.

Implemented fix:

- Removed the global `RenderCommand._swapQueued` pre-publish claim.
- Added collection-local dirty queue membership sets to `RenderCommandCollection`, so repeated adds in one collection are still deduped but another viewport's collection can enqueue the same dirty command independently.
- `SwapBuffers()` still publishes the shared command snapshot only if the command remains dirty or has never been swapped, so the first collection to swap publishes and later collections in the same frame no-op safely.
- Added `RenderCommandCollectionOrderingTests.DirtyPublishQueue_IsCollectionLocal_ForSharedCommands`, which reproduces the old starvation order by collecting into two collections and swapping the second one first.

Validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~RenderCommandCollectionOrderingTests" --no-restore -v:minimal` passed: 6 tests.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` passed.
- Both commands still report the existing Magick.NET advisory warnings; these are unrelated.

Next split:

- Re-test the live editor camera-motion repro.
- If it persists, treat the shared command dirty-publish race as ruled out and enable `DiagDeferredLighting` for the next run to compare desktop `OpaqueDeferred` pass counts against bad/good frames.

### 2026-06-29 Swapchain Primary Command Buffer Temporal Reuse

User report after the collection-local dirty publish queue fix: the deferred-mesh flicker is fixed, but the editor view now appears to render some frames out of order. The transform seems to jump backward in time every few frames and then resume normal motion.

Interpretation:

- This is unlikely to be Vulkan presentation literally reordering submitted frames.
- The symptom matches per-swapchain-image command-buffer reuse: image 0/1/2 can be acquired in a rotating pattern, and a cached primary command buffer for one image can represent scene work recorded against an older camera/transform state.
- `EnsureCommandBufferRecorded` had two primary reuse paths:
  - an early `TryReuseCleanCommandChainPrimaryVariant(...)` fast path,
  - and a later clean-reuse path that refreshes frame data but returns the existing primary.
- The cache keys covered structural frame-op/schedule/resource-plan changes, but they did not require the primary command buffer to have been recorded for the current logical frame.
- OpenXR primary reuse was already explicitly opt-in through `XRE_OPENXR_VULKAN_PRIMARY_REUSE`; the normal desktop swapchain path did not have the same conservative default.

Implemented fix:

- Added explicit opt-in env flag `XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE`.
- Disabled clean primary command-buffer reuse for normal swapchain frame-op rendering unless that flag is set.
- The renderer still builds command-chain schedules and can reuse/refresh lower-level command-chain frame data, but the outer primary is re-recorded for the current editor-view frame by default.
- Added a source contract so primary reuse remains an intentional opt-in until the cache key includes a complete frame-state/pose contract.

Validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanP1ValidationTests.SwapchainPrimaryCommandBufferReuse_IsExplicitOptInForFrameOps" --no-restore -v:minimal` passed: 1 test.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal` passed.
- An initial parallel test/build attempt failed because both processes touched generated `obj` files at the same time; rerunning the test by itself passed.
- Restore/build still reports existing Magick.NET advisory warnings; these are unrelated.

### 2026-06-29 Desktop Smoothed Camera Pose Domain

User report after disabling normal swapchain primary reuse: no luck; the editor/desktop view still appears to render transforms out of order, and the issue appears desktop-view only.

Updated interpretation:

- Treat normal Vulkan primary reuse as ruled out for this symptom.
- The desktop runtime VR view is the only view driven by `SmoothedParentConstraintTransform`.
- `SmoothedParentConstraintTransform` initialized from `ParentWorldMatrix` but smoothed toward `ParentRenderMatrix`.
- For an OpenXR HMD parent, `WorldMatrix` is the normal predicted app/update pose, while `RenderMatrix` is deliberately rewritten during OpenXR predicted and late timing hooks.
- That means the desktop camera smoothing target could alternate between pose domains as OpenXR prepared predicted visibility and then late-updated render poses.
- The eye views want render-time pose rewrites; the independent desktop/cyclopean third view should follow one stable parent pose timeline.

Implemented fix under test:

- `SmoothedParentConstraintTransform` now follows `ParentWorldMatrix` by default.
- Added explicit `UseParentRenderMatrix` opt-in for callers that really need render-space parent tracking.
- The first-person desktop VR camera now uses the stable world/predicted parent pose unless explicitly configured otherwise.
- Extended the runtime VR desktop source contract to guard against accidentally returning the smoothed desktop camera to `ParentRenderMatrix` tracking.

Validation:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal` passed.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests.RuntimeVrDesktopView_DoesNotReuseEyeCommandsOrEditorImGuiWhenDesktopEditingDisabled" --no-restore -v:minimal` passed: 1 test.
- An initial parallel test/build attempt failed because both processes touched generated `obj` files at the same time; rerunning the test by itself passed.
- Restore/build still reports existing Magick.NET advisory warnings; these are unrelated.

### 2026-06-29 Unsmooth Runtime Desktop VR Camera Baseline

User report after switching `SmoothedParentConstraintTransform` to world-pose tracking: no luck; the desktop/editor view still appears to jump backward in time every few frames.

Isolation change under test:

- Replaced the HMD-parented `FirstPersonViewNode` `SmoothedParentConstraintTransform` with a plain `Transform`.
- This keeps the runtime desktop VR view as a normal third render parented to the HMD, but removes smoothing, tick timing, and interpolation overshoot as variables.
- Added a source-contract assertion so the Unit Testing World runtime VR desktop camera stays unsmoothed while this repro is being isolated.

Expected split:

- If the backward stepping disappears, the remaining issue is inside the smoothing/interpolation path.
- If it persists, the issue is below the camera transform and the next pass should focus on desktop-only render timing, frame-data refresh, or swap/present ordering.

### 2026-06-29 OpenXR Pacing Thread Predicted-Pose Race

User report after replacing the HMD-parented runtime desktop camera with a plain `Transform`: the back-and-forth desktop jitter still occurs. Treat smoothing and interpolation overshoot as ruled out for this symptom.

New source finding:

- The default OpenXR pacing mode is `DedicatedThread`.
- After `RenderFrame` calls `xrEndFrame`, it signals the XR pacing thread immediately.
- The XR pacing thread can then run `PrepareNextFrameForPacingOwner()` while the render thread is still inside `XRWindow.RenderViewportsCallback`, before `XRWindow.RenderWindowViewports(...)` draws the independent desktop viewport.
- `PrepareNextFrameForPacingOwner()` was invoking `RuntimeEngine.VRState.InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming.Predicted)`, which publishes next-XR-frame predicted render matrices from the pacing thread.
- That creates exactly the remaining symptom shape: the desktop viewport can render with app-visible VR rig render matrices advanced by a different thread between desktop collect/swap and desktop render/present.

Implemented fix under test:

- Removed app-visible predicted rig recalc from `PrepareNextFrameForPacingOwner()`.
- Added `OpenXR.CollectVisible.ApplyPredictedVrRigPose` inside `OpenXrCollectVisible()` after it claims the pending XR frame, immediately before building eye visibility.
- This keeps predicted HMD/eye rig pose publication aligned with OpenXR visibility collection, while preventing the pacing thread from advancing desktop-visible pose state during desktop rendering.
- Updated the timing source contract so predicted recalc must stay in collect-visible and must not return to the pacing-owner prep helper.

### 2026-06-29 Vulkan Retired Image Backlog / Allocation Failures

User report after the pacing-thread predicted-pose change: the desktop view still appears to show one older transform frame every ~10 frames. The latest run also contained `VulkanOutOfMemoryException` / image allocation warnings.

Last-run log evidence:

- Session inspected: `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-29_14-53-31_pid35460`.
- `log_vulkan.log` shows repeated `Image allocation failed for DeviceLocalBit; no host-visible fallback is attempted for Vulkan images.`
- The pending physical resource planner then logs `Pending physical resource plan failed. Keeping active plan revision=0. Reason=Vulkan image allocation failed with no viable fallback.`
- Retired image backlog grows before the failures:
  - frame slot 1 reaches `390` retired images around frame 8,
  - then `493` retired images around frame 10.
- OpenXR eye rendering repeatedly logs `Vulkan skipped retired-resource drain before eye rendering because frame slot ... is still pending at timeline value ...`.
- This means the eye renderer was attempting to build or reuse eye resource plans while old Vulkan images were still queued for retirement. When a pending desktop slot was encountered, the old cleanup helper returned early and did not drain other completed slots.

Source finding:

- `DrainRetiredResourcesIfSubmittedFrameSlotsCompleted()` had two bad properties for this repro:
  - it returned as soon as any desktop frame slot was pending, so a different completed slot could keep hundreds of retired images alive,
  - when all submitted slots were clear, it only called `ForceFlushCompletedNonImageRetiredResources()`, so retired images still stayed on the normal desktop frame-loop drain path.
- OpenXR records into its own command-buffer frame-data slots, but resource retirement is still queued against the two desktop frame slots via `currentFrame`.
- That makes the old helper especially sensitive to desktop/OpenXR interleaving: a pending desktop slot could starve image cleanup while eye resource plans continued to churn.

Implemented fix under test:

- Replaced the all-or-nothing OpenXR cleanup helper with `DrainRetiredResourcesFromCompletedSubmittedFrameSlots()`.
- The new helper walks each submitted desktop frame slot independently.
- Pending slots are still skipped with the existing diagnostic, but the helper continues on and drains any completed slots.
- Completed slots now drain descriptor pools, pipelines, buffers, framebuffers, and images with `int.MaxValue`, then restore the saved `currentFrame`.
- OpenXR eye swapchain, eye mirror, batch submit, prewarm, and publish paths now use this completed-slot cleanup instead of the non-image-only flush.
- Refreshed stale OpenXR timing source-contract markers that were already out of sync with the current source layout.

Validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests" -v:minimal` passed: 29 tests.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal` passed.
- A parallel test/build attempt briefly failed because both processes wrote `XREngine\obj\Debug\net10.0-windows7.0\Generated\AotFactoryRegistrations.g.cs`; rerunning build alone passed.
- Restore/build still report existing Magick.NET advisory warnings; these are unrelated.

Expected next live-run signal:

- If this was the cause of the backward-step jitter, the latest run should stop showing large retired image backlogs and Vulkan image allocation failures.
- If the visual jitter remains but allocation/backlog warnings disappear, the memory/resource-plan churn was a confounding issue and the next split should focus on desktop swapchain acquire/present timing or stale frame-data refresh.

### 2026-06-29 Desktop Frame State Reentry / OpenXR `currentFrame` Mutation

User report after the retired-image cleanup fix: the desktop view still steps backward periodically.

Latest log signal:

- Latest inspected run no longer showed the same Vulkan image allocation failure / pending physical plan failure pattern.
- Desktop Vulkan frame diagnostics still showed internally inconsistent frame numbering: a frame could log acquire/size as frame `N`, then submit/present as frame `N+1`.
- That is not proof that the GPU presented images out of order. It means mutable renderer diagnostics/state advanced while a desktop `WindowRenderCallback` was still in progress.

Source finding:

- `WindowRenderCallback` used mutable renderer-wide `_vkDebugFrameCounter` and `currentFrame` through a long acquire/record/submit/present path.
- The OpenXR completed-slot cleanup helper also temporarily assigned `currentFrame = i` to drain frame-slot retirement queues, then restored the saved value.
- If OpenXR cleanup runs while the desktop frame is active, the desktop path can observe a borrowed frame slot. That matches a desktop-only symptom where the eye views remain sane but the mirror/presenter appears to sample or submit adjacent-frame state.

Implemented fix under test:

- Added a non-reentrant desktop `WindowRenderCallback` guard using `_windowRenderCallbackInProgress`.
- Captured `frameNumber` once at desktop frame start and used that local in size/acquire/submit/present diagnostics, so the next log can prove whether one callback stayed coherent.
- Made `DrainRetiredResourcesFromCompletedSubmittedFrameSlots()` defer its `currentFrame`-borrowing cleanup while a desktop frame is active.
- Added source-contract tests for both the desktop callback guard and OpenXR deferral.

Validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests.VulkanOpenXr_RetiredResourceDrainCleansCompletedSlotsIncludingImages|FullyQualifiedName~VulkanP1ValidationTests.DesktopWindowRenderCallback_IsNonReentrantAndUsesCapturedFrameNumber" -v:minimal` passed: 2 tests.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal` passed.
- Restore/build still report existing Magick.NET advisory warnings; these are unrelated.

Expected next live-run signal:

- Size/acquire/submit/present logs for a single desktop frame should now report the same captured `Frame=N`.
- If reentry was occurring, logs may show `Skipping reentrant desktop window render callback...`.
- If OpenXR cleanup was colliding with the desktop frame, logs may show `deferred completed-slot retired-resource drain because desktop frame...`.
- If the jitter persists with coherent desktop frame numbers and no reentrant/deferred cleanup logs, the next split should inspect swapchain image age / command-buffer reuse or desktop camera render-matrix publication rather than OpenXR resource cleanup.

### 2026-06-29 Command-Chain Primary Reuse / Frame-Op Signature Mismatch

User report after the desktop callback guard and OpenXR drain deferral: deferred meshes are flickering in and out again, and the backward-step jitter remains.

Latest log evidence:

- Session inspected: `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-29_15-16-18_pid25904`.
- No `Skipping reentrant desktop window render callback` diagnostics were logged.
- No `deferred completed-slot retired-resource drain because desktop frame...` diagnostics were logged.
- No Vulkan image allocation failures or pending physical resource plan failures were logged.
- The Vulkan frame-op diagnostics did show the command stream oscillating between visibly different sets:
  - `FrameOps: total=184 clears=11 draws=164...`
  - `FrameOps: total=86 clears=7 draws=70...`
  - later variants around `80-88` total ops.
- This matches the deferred-mesh symptom better than presentation ordering: some recorded/submitted command sets simply do not contain the deferred draw population.

Source finding:

- The pre-lowering `TryReuseCleanCommandChainPrimaryVariant(...)` path already required exact `FrameOpsSignature` and `PlannerRevision`.
- The normal post-lowering command-buffer variant path did not. For command-chain schedules, `GetOrCreateCommandBufferVariant(...)` matched variants by command-chain structural signature/group signature and overlay state, but not by `FrameOpsSignature`.
- Dirty evaluation also only treated command-chain frame-op signature changes as exact-invalidating when texture-upload frame ops were present.
- OpenXR primary reuse had the same old policy: exact frame-op matching was only required for non-command-chain frames or texture-upload frames.

Implemented fix under test:

- Command-chain primary variants now include `FrameOpsSignature` and dynamic UI signature in the variant match.
- Post-lowering dirty evaluation now marks command-chain primary command buffers dirty whenever `variant.FrameOpsSignature != frameOpsSignature`, not only for texture uploads.
- OpenXR direct and mirror primary reuse now always require exact frame-op signatures.
- Updated source contracts to make the stricter behavior durable.

Validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests.VulkanOpenXr_EyeSubmitRecordsBothEyesBeforeOneFenceWait|FullyQualifiedName~ImportedTextureStreamingContractTests.VulkanCommandChains_TreatImportedTextureUploadsAsPrimaryCommandWork|FullyQualifiedName~VulkanP1ValidationTests.SwapchainPrimaryCommandBufferReuse_IsExplicitOptInForFrameOps" -v:minimal` passed: 3 tests.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal` passed.
- Restore/build still report existing Magick.NET advisory warnings; these are unrelated.

Expected next live-run signal:

- The desktop deferred mesh flicker should stop if stale primary command-buffer reuse was the cause.
- The frame-op count diagnostics may still vary between desktop and eye render paths, but a primary command buffer should no longer be reused across different `FrameOpsSignature` values.
- If the deferred flicker persists, enable frame-op signature diff diagnostics and command recording diagnostics next to determine whether the low-op set is being freshly recorded for the desktop viewport itself.

### 2026-06-29 Parallel Collect / Shared Sort Key Race

User report after the command-chain exact-signature fix: the deferred-mesh flicker is now literally every other frame.

Latest log/source split:

- The alternating high/low `FrameOps` counts in `log_vulkan.log` are partly expected because the log stream interleaves desktop and OpenXR eye submissions.
- The latest inspected session still showed desktop and eye rendering using distinct viewport/pipeline identities, so the issue is less likely to be a simple desktop/eye command collection mix-up.
- `EngineTimer.DispatchCollectVisible()` still invokes collect-visible listeners with `InvokeParallel(minParallelListeners: 2)`, so the desktop viewport collector and OpenXR stereo collector can collect the same scene/render-command objects concurrently.
- `RenderCommandCollection.AddCPU()` assigned `item.SortOrderKey = GetSortOrderKey(pass)` on the shared `RenderCommand` before adding it to the pass collection.
- The sorted pass collection snapshots `RenderDistance` and `SortOrderKey` at collection time, but the sort key was read back from the shared command object. Another concurrently collecting viewport could overwrite that field before the snapshot captured it.

Why this matches the symptom:

- Deferred passes use sorted render command collections.
- Desktop and OpenXR collect different views of the same render commands.
- A shared mutable collection-time sort key makes pass membership/order dependent on cross-viewport timing.
- In a double-buffered publish loop, that can present as one published buffer containing a coherent deferred pass and the next containing a corrupted or incomplete-looking deferred pass, especially when collect/render cadence locks into a stable alternation.

Implemented fix under test:

- `SnapshotSortedRenderCommandCollection` now accepts the collection-local sort key directly via `Add(item, sortOrderKey)`.
- `Entry.Capture(...)` records that passed key instead of reading `command.SortOrderKey`.
- `RenderCommandCollection.AddCPU()` now routes sorted pass additions through that overload.
- The old global `SortOrderKey` assignment remains only for unsorted/pass-list compatibility and diagnostics; sorted pass ordering no longer depends on it.
- Updated `RenderCommandCollectionOrderingTests` source contracts so future changes keep sorted pass sort keys collection-local.

Validation:

- `dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~RenderCommandCollectionOrderingTests --no-restore` passed: 6 tests.
- `dotnet build XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal` passed.
- Restore/build still report existing Magick.NET advisory warnings; these are unrelated.

Expected next live-run signal:

- If this was the every-other-frame deferred pass corruption, desktop deferred meshes should stop alternating in/out while OpenXR is active.
- If flicker persists, the next split should instrument actual desktop `XRViewport` pass counts at `CollectVisible`, `SwapBuffers`, and `Render` with `DeferredLightingDiagnostics.Enabled`, because the frame-op totals alone conflate desktop and eye submissions.

### 2026-06-29 Active Desktop Slot Drain Without Global Deferral

User report after the desktop callback guard and OpenXR drain deferral: the deferred mesh disappearance returned, and the user identified the global OpenXR cleanup deferral while a desktop frame is active as the change that caused it to come back.

Updated interpretation:

- The non-reentrant desktop callback and captured frame-number diagnostics are still useful.
- The bad part was deferring all OpenXR completed-slot retired-resource cleanup while the desktop callback was active.
- That full deferral can starve retired image cleanup whenever OpenXR eye rendering overlaps desktop rendering, recreating the image/backlog/resource-plan churn that previously correlated with disappearing deferred meshes.
- The original reason for deferral was narrower: do not let OpenXR cleanup borrow/mutate `currentFrame` while a desktop frame is recording.

Implemented fix under test:

- Added slot-explicit retirement drain overloads for pipelines, framebuffers, buffers, and images.
- `ForceFlushAllRetiredResources()` and `ForceFlushCompletedNonImageRetiredResources()` now drain explicit slots without assigning `currentFrame`.
- `DrainRetiredResourcesFromCompletedSubmittedFrameSlots()` no longer globally defers while a desktop frame is active.
- When desktop rendering is active, OpenXR cleanup skips only the currently active desktop frame slot, drains any other completed slot, and never assigns `currentFrame = i`.
- Updated the source contract so the old `deferred completed-slot retired-resource drain because desktop frame...` behavior cannot quietly return.

Validation:

- `dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests.VulkanOpenXr_RetiredResourceDrainCleansCompletedSlotsIncludingImages|FullyQualifiedName~VulkanP1ValidationTests.DesktopWindowRenderCallback_IsNonReentrantAndUsesCapturedFrameNumber" -v:minimal` passed: 2 tests.
- `dotnet build XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal` passed.
- Restore/build still report existing Magick.NET advisory warnings; these are unrelated.

Expected next live-run signal:

- The log should no longer contain `deferred completed-slot retired-resource drain because desktop frame`.
- It may contain `skipped retired-resource drain for active desktop frame slot`, but other completed slots should continue draining.
- Retired image backlog and Vulkan image allocation failures should not reappear from desktop/OpenXR overlap alone.
- If deferred meshes still disappear with this patch, the next investigation should instrument desktop-only pass populations at collect/swap/render rather than treating resource cleanup as the main suspect.

### 2026-06-29 Shared RenderCommand Snapshot Ownership

User report after disabling occlusion/AA and after the resource-drain fix: desktop deferred meshes still disappear every other frame, and the desktop camera still occasionally appears to sample an older transform. The user also confirmed the latest checkout now uses sequential `CollectVisible`, so the earlier collect-thread race theory does not explain the current state.

Updated source finding:

- Desktop/cyclopean rendering is a real third view when `RenderWindowsWhileInVR=true` and `VrMirrorComposeFromEyeTextures=false`.
- OpenXR eye rendering and the desktop viewport collect into separate `RenderCommandCollection` instances, but both collections still contain the same scene `RenderCommand` objects.
- `RenderCommandCollection` queue membership is collection-local, which prevents one collection from starving another at enqueue time.
- The published render snapshots are still stored globally on `RenderCommand` (`RenderEnabled`, render mesh/material/world-matrix snapshots, previous model matrices, and derived command state).
- `RenderCommand.SwapBuffers()` clears the single global `_dirty` bit. If the OpenXR shared eye collection swaps first, it can publish and clear that snapshot; the desktop collection then swaps its own pass membership but skips the per-command publish. The desktop frame can then render desktop pass membership against snapshots last claimed by the eye path.

Why this matches the symptom:

- The failure is desktop-view only.
- Deferred pass membership can alternate while forward/skybox still render because the view-local pass lists and global command snapshots can be out of phase.
- Camera movement can keep deferred meshes absent until movement stops because the shared command publish cadence is being claimed by the eye path while the desktop view is trying to render a different camera.
- The one-frame backward transform sample is consistent with the desktop render path observing a global command snapshot published for a different view/cadence.

Implemented fix under test:

- Added `RenderCommandCollection.IsRenderCommandSnapshotAuthority`, default `true`.
- Authoritative collections mark dirty/unpublished commands as claimed for shared snapshot publishing when they collect them.
- Non-authoritative collections still swap their view-local pass membership, but yield `RenderCommand.SwapBuffers()` when an authoritative collection collected the same command this frame.
- Non-authoritative collections can still publish commands that no authoritative view collected, so eye-only objects are not forced to rely on desktop visibility.
- OpenXR shared eye command collection sets `IsRenderCommandSnapshotAuthority=false` only when an independent desktop VR view exists (`RenderWindowsWhileInVR=true` and `VrMirrorComposeFromEyeTextures=false`).
- The older OpenVR two-pass shared command collection applies the same rule.
- Mirror-from-eye-texture mode still publishes through the eye collection because there is no independent desktop scene view in that mode.
- Added tests covering non-authoritative collections and source contracts for the OpenXR/desktop wiring.

Expected next live-run signal:

- In the current Unit Testing World settings, desktop deferred meshes should no longer alternate in/out because the eye collection cannot claim the global render snapshot before the desktop collection publishes it.
- If flicker persists, the next split should log desktop-only `IsRenderCommandSnapshotAuthority`, pass counts, and which collection actually calls `RenderCommand.SwapBuffers()` for a representative deferred mesh.

### 2026-06-29 Shared Vulkan Mesh Buffer Cache Mutation

User report after the shared snapshot ownership patch: still not fixed.

Latest run evidence:

- Session inspected: `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-29_16-09-45_pid51436`.
- `log_rendering.log` recorded `VPRC_RenderMeshesPass` throwing during the desktop render path:
  - `System.InvalidOperationException: Collection was modified; enumeration operation may not execute.`
  - Throw site: `VkMeshRenderer.EnsureBuffers(Boolean skipIndexBuffers)` while enumerating `_bufferCache.Values`.
  - Stack path: `RenderCommandMesh3D.Render()` -> `RenderCommandCollection.RenderCPU()` -> `VPRC_RenderMeshesPassTraditional.RenderCPU()` -> `XRViewport.Render()` -> `XRWindow.RenderWindowViewports(...)`.
- This is consistent with deferred meshes disappearing while forward/skybox continue: the mesh pass can be aborted/partially skipped for a frame when a shared Vulkan mesh renderer mutates its cached buffer dictionary during draw preparation.

Source finding:

- The same `VkMeshRenderer` instance can be used by the independent desktop view and OpenXR eye rendering.
- `VkMeshRenderer` had mutable dictionaries/arrays shared across prepare and recording paths:
  - `_bufferCache`
  - `_vertexBuffersByBinding`
  - `_vertexBindings` / `_vertexAttributes`
  - index-buffer binding fields
- `EnsureRuntimeDeformationBuffersCurrent()` can call `CollectBuffers()`, which clears and repopulates `_bufferCache`.
- Other paths enumerate or read that same cache while preparing or recording draw state.

Implemented fix under test:

- Added a local `_bufferStateSync` lock to `VkMeshRenderer`.
- Serialized buffer collection, runtime deformation refresh, buffer readiness checks, vertex input layout rebuild, vertex buffer binding/snapshot reads, index-buffer state changes, and descriptor cache scans.
- This is a local mesh-wrapper fix; it does not change render scheduling, occlusion, TSR, or desktop/eye command ownership.

Validation:

- `dotnet build XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -v:minimal` passed.

Expected next live-run signal:

- `log_rendering.log` should no longer contain `Collection was modified; enumeration operation may not execute` from `VkMeshRenderer.EnsureBuffers`.
- If the flicker persists after this exception is gone, the next step is desktop-only pass-count instrumentation at `CollectVisible`, `SwapBuffers`, and `Render`, because the frame-op totals still conflate desktop and OpenXR eye submissions.

### 2026-06-29 OpenXR HMD Render-Matrix Propagation Regression

User report after the shared Vulkan mesh buffer-state fix: still literally the same. Both desktop deferred meshes and the desktop camera view still misbehave.

Latest run evidence:

- Session inspected: `Build\Logs\Debug_net10.0-windows7.0\windows_x64\xrengine_2026-06-29_16-32-59_pid25612`.
- The previous `VkMeshRenderer.EnsureBuffers` `Collection was modified` exception is gone, so that was a real bug but not the visible root cause.
- The run is still an independent desktop VR view:
  - `RenderWindowsWhileInVR=True`
  - `VrMirrorComposeFromEyeTextures=False`
  - `VRState callbacks: CollectVisible=CollectVisibleTwoPass, SwapBuffers=SwapBuffersTwoPass, Stereo=False, Runtime=OpenXR`
- `OpenXRRenderPacingMode=DedicatedThread` means the pacing thread can prepare the next predicted OpenXR frame after `xrEndFrame` while the render thread continues into the independent desktop viewport render.

Source finding:

- `VRDeviceTransformBase.VRState_RecalcMatrixOnDraw` correctly avoided the normal child-recursive `SetRenderMatrix(..., recalcAllChildRenderMatrices: true)` path for the OpenXR headset.
- However, the current source then explicitly called `PropagateOpenXrHeadsetRenderMatrixToNonEyeChildren(renderMatrix)`.
- A desktop/cyclopean camera parented under the HMD is a non-eye child, so OpenXR predicted/late pose updates could overwrite the desktop camera child's render matrix with the raw HMD-timed render matrix.
- That creates a collect/render camera mismatch:
  - desktop `CollectVisible` can build deferred visibility using the desktop camera's own transform path,
  - OpenXR predicted/late pose code can then overwrite the same desktop camera render matrix before desktop rendering,
  - while moving, the mismatch persists and deferred meshes can vanish; when stationary, the poses converge and the content can reappear.
- The existing source contract claimed to protect the HMD child-propagation guard, but it actually asserted that non-eye child propagation existed.

Implemented fix under test:

- Removed `PropagateOpenXrHeadsetRenderMatrixToNonEyeChildren`.
- OpenXR headset `RecalcMatrixOnDraw` now sets only the headset render matrix with `recalcAllChildRenderMatrices: false`.
- OpenXR eye render matrices are still updated directly by `ApplyOpenXrEyePoseForRenderThread`, so eye latency correction does not depend on recursive HMD child propagation.
- Updated the source contract so OpenXR HMD render updates cannot recursively overwrite non-eye children such as the desktop/cyclopean camera.

Validation:

- `dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter FullyQualifiedName~OpenXrTimingPipelineContractTests.RuntimeVrDesktopView_DoesNotReuseEyeCommandsOrEditorImGuiWhenDesktopEditingDisabled -v:minimal` passed: 1 test.
- `dotnet build XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal` passed.
- A parallel validation attempt hit a transient file write lock on `XREngine.Runtime.InputIntegration.dll`; the sequential editor build passed.
- Restore/build still report existing Magick.NET advisory warnings; these are unrelated.

Expected next live-run signal:

- The desktop camera should no longer step backward when OpenXR predicted/late poses are sampled.
- If deferred meshes still vanish during camera motion, enable or force `DiagDeferredLighting` next and compare desktop viewport `OpaqueDeferred` pass counts across collect/swap/render. A stable count would move the investigation from visibility/camera state to G-buffer/light-combine presentation.

### 2026-06-29 Remove Render-Thread Late Global Rig Publish

User report after the HMD child-propagation fix: still the same. The issue occurs with `AllowDesktopEditing` both on and off. The symptom sometimes clears temporarily when render/input timing changes, then returns, which points back to contention between desktop rendering and OpenXR eye rendering rather than the desktop camera mode alone.

Refined source finding:

- `XRWindow` invokes `RenderViewportsCallback` before it renders the desktop window viewports.
- OpenXR's `Window_RenderViewportsCallback` sampled late poses, then called `RuntimeEngine.VRState.InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming.Late)` immediately before the independent desktop viewport rendered.
- The collect-visible thread is allowed to build next-frame desktop visibility while the render thread is drawing the current frame.
- Therefore, the render thread could publish late OpenXR HMD/controller render matrices globally while desktop visibility collection or desktop rendering was using the same transform graph.
- The OpenXR Vulkan eye path does not need this global late publish for eye camera latency correction: `ApplyOpenXrEyePoseForRenderThread` already composes each eye camera render matrix directly from the late eye pose and the locomotion-root render matrix.

Implemented isolation fix under test:

- Removed the render-thread `InvokeRecalcMatrixOnDraw(RuntimeVrPoseTiming.Late)` call from `Window_RenderViewportsCallback`.
- Kept late pose sampling/caching and late per-eye `SetRenderMatrix` application.
- Kept predicted pose publication on the collect-visible thread before OpenXR eye visibility collection.
- Updated the timing contract test so global late rig publication cannot be reintroduced accidentally.

Expected next live-run signal:

- Desktop camera should stop stepping backward due to render-thread OpenXR late pose publication.
- If deferred meshes still disappear, the next split is not pose publication; force `DiagDeferredLighting` on for the unit-testing launch and compare desktop `OpaqueDeferred` counts at collect/swap/render against good and bad frames.

### 2026-06-29 Directional Deferred Shadow Uniform Scratch Isolation

User report after the global late-pose publish removal: still happening, including with `AllowDesktopEditing` on or off. New isolation clue: disabling the directional light component stops the deferred mesh flicker.

Source finding:

- The visible symptom matches the deferred-to-forward lighting boundary, not mesh collection alone:
  - forward meshes and skybox still render,
  - deferred meshes disappear,
  - disabling the directional light stabilizes the view.
- The Vulkan log for the latest run shows `DeferredLightingDir.fs` and then `DeferredLightCombine.fs` on the affected path.
- `VPRC_LightCombinePass` used process-wide static scratch arrays for directional shadow atlas uniforms:
  - `DirectionalShadowAtlasPacked0`
  - `DirectionalShadowAtlasUvScaleBias`
  - `DirectionalShadowAtlasDepthParams`
- In VR, the independent desktop viewport and OpenXR eye viewports can render close together. Static scratch means one view's directional light bind can clear or refill the arrays while another view is binding or capturing uniforms for its deferred directional pass.
- The same pattern existed for point shadow atlas arrays, so those were isolated as part of the same fix even though the current repro points at the directional light.

Implemented isolation fix under test:

- Moved deferred shadow-atlas uniform scratch arrays into `VPRC_LightCombinePass.LightRendererCache`, which is keyed per `XRRenderPipelineInstance`.
- Updated directional and point atlas bind/disabled paths to pass the cache-owned arrays explicitly instead of touching static arrays.
- This keeps desktop and eye deferred light uniform capture from sharing mutable process-wide atlas data.

Validation:

- `dotnet build XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -v:minimal` passed with 0 warnings/errors.
- `dotnet build XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal` passed.
- Restore/build still report existing Magick.NET advisory warnings; these are unrelated.

Expected next live-run signal:

- With the directional light enabled, desktop deferred meshes should no longer alternate between lit/visible and missing due to cross-view directional shadow uniform data.
- If flicker persists, the next target is directional shadow atlas publication itself: compare `CopyPublishedDirectionalAtlasUniformData`, atlas allocation `LastRenderedFrame`, and `ShadowAtlas.PublishedFrameData` between desktop and eye render timing.

### 2026-06-30 Directional Cascade POV Source Fix

User narrowed the remaining symptom: directional cascades need explicit desktop and HMD POV support. Instanced and geometry-shader cascade paths appeared stable, but sequential cascade rendering made desktop deferred meshes flicker in and out; with directional atlas enabled, shadow application looked like it toggled off on some frames.

Root-cause finding:

- Directional cascade preparation still resolved one global "shadow source camera".
- In VR, `ResolveDirectionalShadowSourceCamera` considered eye viewports before active desktop/editor viewports, so a left/right eye could become the primary cascade source even while the desktop deferred pass was the view sampling the cascades.
- `DirectionalLightComponent.CopyCascadeSourceFrusta` then only built cascade bounds from that primary camera plus the other eye. It did not include the active desktop/editor frustum and did not publish a combined HMD frustum.
- Sequential cascade rendering is the most visible failure mode because each cascade renders from its own published viewport/cull volume. When those volumes are HMD-biased while the desktop deferred pass samples them with desktop view depth, desktop receivers and casters can fall outside the expected cascade/cull coverage from frame to frame.
- The layered instanced and geometry-shader paths looked better because they render from the shared published cascade set in one grouped pass and can hide some per-cascade cull timing, but they were still using the wrong source contract.
- With atlas enabled, the same bad source contract can make the desktop receiver path observe cascade atlas data that is not resident/current for the desktop view's required cascade coverage, which presents as shadows toggling off rather than as only caster/receiver mismatch.

Implemented fix:

- Changed directional shadow primary selection so active cascaded viewports are considered before VR eyes. A desktop/editor cascaded viewport now wins over left/right eye fallback, while VR-only rendering still falls back to the eye viewports.
- Expanded cascade source frusta from the old primary-plus-eyes model to a bounded source set:
  - selected primary camera,
  - every active viewport that targets this world and prefers cascaded directional shadows,
  - left and right HMD eye viewports,
  - a combined HMD frustum built from both eye projection/view matrices and transformed by the HMD render matrix.
- Added duplicate camera filtering so the primary/desktop/eye passes do not add the same camera twice.
- Added `ProjectionMatrixCombiner.TryCombineProjectionMatrices(...)`, a span/stackalloc two-frustum combiner for the HMD combined frustum path so cascade preparation does not use the existing allocating point-cloud API.
- This updates the common published cascade cameras, matrices, cull volumes, and atlas requests before render, so the same POV contract feeds sequential, single-pass stereo layered modes, and parallel recording.

Validation:

- `dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~DirectionalCascadeSourceCamera_PrefersPlayerAssociatedCascadedViewport|FullyQualifiedName~DirectionalCascadeSourceFrusta_IncludeDesktopEyesAndCombinedHmd|FullyQualifiedName~TryCombineProjectionMatrices_StereoPair_EnclosesSourceFrusta" -v:minimal` passed: 3 tests.
- `dotnet test XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ProjectionMatrixCombinerTests" -v:minimal` passed: 6 tests.
- The broader `CascadedShadowDefaultsAndForwardShaderTests` class still has unrelated pre-existing source-contract failures in forward/shadow-atlas assertions plus a constructor-time services failure. It did compile and run.
- Restore/build still report existing Magick.NET advisory warnings; these are unrelated.

Expected next live-run signal:

- Desktop deferred meshes should remain visible when directional cascades are enabled because the desktop frustum now contributes to cascade bounds and primary split selection.
- Directional atlas shadows should stop toggling off due to mismatched desktop-vs-eye cascade source data.
- If a flicker remains, the next check is the atlas residency/publish path itself: compare directional cascade request keys, `AreRequiredDirectionalAtlasTilesSampleable`, and `DirectionalCascadeAtlasSlot.LastRenderedFrame` across desktop and eye render timing.

### 2026-07-01 Directional Cascade Source-Family Guard

User report after the mixed desktop/HMD cascade source patch: not fixed; most combinations of directional atlas usage and cascade render mode became worse.

Correction:

- The previous fix mixed desktop, left-eye, right-eye, and combined HMD frusta into one published cascade set.
- That is wrong for the current architecture because directional cascade state is still light-owned: `_cascadeShadowSlices`, `_cascadeAabbs`, `_cascadeAtlasSlots`, `ActiveCascadeCount`, and the cascade uniform copy path publish one set for all receivers.
- Directional atlas request keys are also light/cascade keyed rather than receiver-POV keyed, so one frame's HMD cascade publication can replace the desktop cascade atlas slots, and the desktop deferred pass can then sample the wrong family of cascade matrices/tiles.
- This explains why instanced and geometry paths could look less broken while sequential got worse: the grouped paths reduce some per-cascade timing exposure, but all paths were still sharing the same non-POV-keyed publication.

Implemented correction under test:

- Added a published cascade source family (`Desktop` or `Hmd`) to `DirectionalLightComponent`.
- `CopyCascadeSourceFrusta` no longer mixes desktop and HMD frusta. Desktop cascade publication stays desktop-only; HMD publication includes the selected eye, both HMD eye viewports, and the cyclopean combined HMD frustum.
- Deferred and forward receivers now only enable cascaded directional shadows when their active rendering camera matches the published cascade source family.
- If desktop and HMD cascaded receivers coexist, `NeedsPrimaryDirectionalShadowMap()` requests a primary directional shadow fallback. That prevents the non-published family from sampling mismatched cascades while preserving shadows until a future source-keyed cascade resource model exists.

Validation:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -v:minimal` succeeded.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~DirectionalCascadeSourceCamera_PrefersPlayerAssociatedCascadedViewport|FullyQualifiedName~DirectionalCascadeSourceFrusta_DoNotMixDesktopAndHmdSources|FullyQualifiedName~TryCombineProjectionMatrices_StereoPair_EnclosesSourceFrusta" -v:minimal` passed: 3 tests.
- `DirectionalPrimaryShadowAtlas_IsSubmittedRenderedBoundAndPreviewed` compiled, but the current harness skipped it under the targeted filter.
- Restore/build still report existing Magick.NET advisory warnings; these are unrelated.

Remaining architectural follow-up:

- True simultaneous desktop and HMD cascaded shadows require source-keyed directional cascade state and source-keyed atlas requests/slots. The current correction is the stable v1 boundary for the existing single-publication design: one cascade family is published, matching receivers use it, and the other family falls back to the primary directional shadow map instead of sampling stale or wrong cascades.

### 2026-07-01 True Simultaneous Directional Cascade Sources

User direction: keep working on true simultaneous desktop and HMD cascades, not a fallback patch.

Root-cause refinement:

- The source-family guard proved the failure was cross-POV publication, but it was still architecturally incomplete.
- A true fix must allow desktop and HMD directional cascade families to be prepared, atlas-allocated, rendered, and sampled in the same frame without sharing cascade slots or matrices.
- Sequential rendering exposed the bug most severely because every cascade tile or legacy layer reads the currently published per-cascade viewport/camera. When desktop and HMD alternated through one light-owned cascade state, sequential passes could render or sample the wrong cascade family. Atlas mode then looked like shadows toggled off because receiver uniforms could point at non-resident or stale slots for the active POV.

Implemented:

- Added `ShadowRequestSource` (`Default`, `Desktop`, `Hmd`) to directional shadow request keys and grouped directional cascade allocation metadata.
- Split `DirectionalLightComponent` directional cascade state into desktop and HMD source states, each with independent:
  - published cascade slices and AABBs,
  - atlas slots,
  - color/moment and raster-depth texture arrays,
  - layered and per-cascade framebuffers,
  - shadow cameras and viewports.
- Directional cascade preparation now publishes both source families when both are active:
  - desktop source stays desktop-only,
  - HMD source includes the selected eye, both eye viewport frusta, and the cyclopean combined HMD frustum.
- Directional atlas requests are now source-keyed, so desktop cascade 0 and HMD cascade 0 no longer alias in `ShadowAtlasFrameData`.
- Directional atlas grouped reservations, sequential fallback rendering, and individual tile rendering all carry `request.Key.Source` into the render call.
- Legacy texture-array cascade rendering now renders desktop and HMD source arrays separately, including sequential, instanced-layered, and geometry-shader plans.
- Forward and deferred receivers select cascade matrices, receiver textures, atlas slots, and active cascade counts from the active rendering camera's source family.
- `VPRC_ForEachCascade` now has a `CascadeSource` selector. `Default` infers from the active rendering camera; explicit `Desktop`/`Hmd` lets render graphs iterate one family intentionally.
- Removed the primary-shadow fallback requirement for mere desktop/HMD coexistence. Primary directional shadows are still requested only when a relevant viewport actually wants primary shadows or no cascade family is published.

Validation:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -v:minimal` succeeded.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=ClearingCascadeBounds_LeavesPrimaryDirectionalAtlasSlotIntact|Name=GroupedDirectionalCascadeAtlasFailure_RendersSequentialAtlasTiles|Name=DirectionalDepthCascadeAtlasFallbacks_KeepReceiverArrayBound|Name=DirectionalMomentAtlas_UsesResolvedEncodingAndKeepsCascades|Name=DirectionalCascadeMomentPath_UsesColorArrayDepthAttachmentAndScalarBlend|Name=DirectionalCascadeAtlasGroupedPath_UsesAtlasBackendAndLayeredRendering" -v:minimal` passed: 5 tests.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=ShadowRequestKey_IncludesProjectionFaceAndEncoding|Name=DirectionalLight_UsesSelectedDepthShadowStorageFormat" -v:minimal` passed: 2 tests.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=DirectionalCascadeSourceCamera_PrefersPlayerAssociatedCascadedViewport|Name=DirectionalCascadeSourceFrusta_DoNotMixDesktopAndHmdSources" -v:minimal` passed: 2 tests.
- A broader rendering-filter test run still includes unrelated pre-existing shader/source-contract failures and Magick.NET advisory warnings. The targeted tests above cover the touched directional-cascade source and atlas contracts.

Expected next live-run signal:

- Desktop deferred meshes should stay visible while HMD eye cascades are also active because desktop receivers no longer sample HMD-published matrices or atlas slots.
- Directional atlas shadows should stop toggling off between frames because desktop and HMD cascade requests no longer collide under the same light/cascade key.
- If flicker remains, the next target is live GPU validation of atlas residency and render-pass ordering per source family: compare `ShadowRequestKey.Source`, `DirectionalCascadeAtlasSlot.LastRenderedFrame`, and the final bound atlas metadata for desktop vs HMD receivers in one frame.

### 2026-07-01 Desktop Mesh Pass Abort From Render-Command Swap Race

User report after the true simultaneous cascade source work: neither forward nor deferred meshes render in the desktop editor view, even when the directional light is deactivated.

Latest log evidence:

- Session inspected before the fix: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_00-26-30_pid40504`.
- `log_rendering.log` recorded `VPRC_RenderMeshesPass` throwing from the desktop render path:
  - `System.InvalidOperationException: Collection was modified; enumeration operation may not execute.`
  - Throw site: `RenderCommandCollection.RenderCPU(...)`.
- This explains why disabling the directional light did not help: the mesh pass was aborting before light/shadow state was relevant.

Source finding:

- `RenderCommandCollection.RenderCPU`, mesh-only render, filtered CPU render, GPU render, and mesh enumeration read the published `_renderingPasses`.
- `RenderCommandCollection.SwapBuffers()` can run concurrently for another desktop/HMD collection cadence, swap the updating/rendering dictionaries, and clear the old rendering collections.
- That means a desktop render could enumerate a published pass list while another path clears or swaps that same list. True simultaneous desktop/HMD rendering made the timing window much easier to hit.

Implemented fix:

- Added a rendering-buffer reader/writer gate to `RenderCommandCollection`.
- Render-side enumeration and query paths now hold a read scope while inspecting `_renderingPasses`.
- `SwapBuffers()` and pass-layout mutation hold a write scope around the publish/clear/reset sequence.
- Collection into the updating buffer still uses the existing update-side lock, so the normal collect/render overlap remains; only the publish boundary is serialized against active render enumeration.
- Inactive directional lights now call `ClearCascadeShadows()` during directional shadow preparation so disabling a light cannot leave stale desktop/HMD cascade state published.

Validation:

- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore -v:minimal` succeeded.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal` succeeded.
- `git diff --check` succeeded with line-ending warnings only.
- Editor smoke run with Unit Testing World, Vulkan, OpenXR, MCP:
  - Session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_00-34-00_pid34800`.
  - `log_rendering.log` had no `VPRC_RenderMeshesPass threw`, no `Collection was modified`, and no `InvalidOperationException`.
- Deferred-lighting diagnostic smoke run:
  - Session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_00-37-08_pid37824`.
  - `log_rendering.log` again had no mesh-pass collection exception.
  - Desktop `Editor View` pass counts stayed stable and nonzero at collect/swap: `total=18`, `opaqueDeferred=15`, `opaqueForward=1`.

Interpretation:

- The immediate "no forward or deferred meshes" regression was a render-command publication race, not a directional-light cascade bug.
- If a live viewport still appears blank after this fix, command collection is no longer the first suspect. The next split should be Vulkan dynamic-rendering pipeline state or the G-buffer/light-combine/present path, because the desktop viewport is collecting and publishing both deferred and forward mesh commands.

### 2026-07-01 Desktop G-Buffer Late Clear / FBO Re-Entry

User report after the render-command publication fix: no luck; the desktop editor view still failed in practice.

New live evidence:

- Run root: `Build/_AgentValidation/20260701-022920-vulkan-fbo-reentry`.
- Before this fix, MCP captures showed the failure shape directly:
  - viewport screenshot: sky only,
  - `AlbedoOpacity`: all RGB zero,
  - `Normal`: all RGB zero with alpha 1,
  - `LightingAccumTexture`: all RGB zero,
  - `HDRSceneTex`: sky content.
- The same run's Vulkan log showed a standalone desktop `DeferredGBufferFBO` clear landing after useful desktop G-buffer work had already been recorded. That late `CmdClearAttachments` wiped deferred outputs, which explains why the desktop view could show sky/post output while deferred and forward scene content vanished.
- HMD single-pass stereo G-buffer draws still recorded successfully, which is why the issue looked desktop-specific.

Root cause:

- `VulkanRenderGraphCompiler.SortFrameOps` sorted by compiled pass order, canonical opaque mesh draw order, and original index.
- Canonical mesh sorting can move same-pass G-buffer mesh draws ahead of their target clear when desktop, HMD, shadow, and post-process contexts enqueue overlapping work.
- Separately, Vulkan FBO re-entry used tracked image layouts but could still preserve an attachment signature with `LoadOp.Clear`. A later same-command-buffer render pass for the same FBO could therefore clear data that had already been explicitly cleared and drawn.

Implemented fix:

- Kept the main frame-op sort conservative, then added a post-sort normalization pass that moves a `ClearOp` just far enough to precede earlier uses of the same pass, same scheduling context, and exact render target.
- This avoids a non-transitive comparator while still fixing clear-before-use for sequential, single-pass stereo, and parallel/secondary recording because all three paths consume `SortFrameOps`.
- Added `preserveTrackedClearLoads` through `VkFrameBuffer.ResolveAttachmentSignatureForPass`, `ResolveRenderPassForPass`, and `UsesReadOnlyDepthStencilForPass`.
- Command-buffer recording now detects FBO re-entry in the same command buffer and preserves tracked `Clear` load ops as `Load` after the explicit clear has defined contents.
- Removed the Vulkan front-face viewport flip from captured mesh state. The desktop Vulkan viewport is negative-height, but the pipeline front-face should remain the engine/material front-face; flipping it made culling dependent on viewport orientation.
- Scheduled mesh secondary recording now preallocates and reuses the exact uniform slot that primary scheduling consumed, so refreshed or re-recorded secondaries do not drift from the primary slot accounting.

Validation:

- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=VulkanFrameOpSort_LiftsSameTargetClearBeforeFirstUse|Name=VulkanFboReentry_PreservesTrackedClearLoadsAfterFirstUse|Name=VulkanFrameOpSort_UsesRenderGraphPassOrderBeforeDependencySafeOriginalOrder" --logger "console;verbosity=normal"` passed: 3 tests.
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal` passed after closing the live MCP editor process; it still reports the existing Magick.NET advisory warnings.
- Live Vulkan/OpenXR/MCP validation with the rebuilt editor:
  - Session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-01_02-39-14_pid26712`.
  - Desktop viewport capture `Screenshot_20260701_024010.png` showed Sponza meshes again.
  - `AlbedoOpacity` stats changed from all-zero to `minRgb=0`, `maxRgb=0.99215686`, `averageRgb=0.65593934`.
  - `Normal` stats changed from all-zero RGB to `maxRgb=1`, `averageRgb=0.413769`.
  - `LightingAccumTexture` changed from all-zero RGB to `maxRgb=1.796875`, `averageRgb=0.05058589`.
  - Three additional captures spaced over several seconds stayed stable:
    - `Screenshot_20260701_024115.png`
    - `Screenshot_20260701_024119.png`
    - `Screenshot_20260701_024123.png`
    - albedo averages stayed around `0.656`.
- Vulkan validation still emits startup/layer-header warnings for newer extension structs and existing Magick.NET advisories during build/test; these are unrelated to this render-order fix.

Interpretation:

- The "no luck" failure at this stage was no longer directional-cascade source selection. It was Vulkan frame-op target ordering plus FBO re-entry load semantics.
- Desktop meshes were being drawn, then erased. The HMD path survived because its grouped stereo draw order happened not to hit the same late desktop clear window.
- The current live evidence shows desktop deferred outputs are present and stable across repeated captures. The remaining visual issue in the capture is exposure/lighting brightness, not the blank/sky-only mesh disappearance.

### 2026-07-02 Directional Shadow Atlas Vulkan Back-Frame Flicker Diagnostic

User report: with directional-light shadow map atlasing enabled, the main view camera sometimes flickers back to an older displayed image by a few frames on Vulkan. The reported symptom only occurs with atlasing enabled, raising the question of swapchain/display starvation.

Diagnostic run:

- Run root: `Build/_AgentValidation/20260702-104157-dir-atlas-vulkan-flicker`.
- Build: `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal` succeeded with the existing Magick.NET advisory warnings.
- Editor launch used Unit Testing World, Vulkan, MCP, directional shadow audit, shadow audit, swap/draw tracing, frame-op signature diffing, frame-data reuse diagnostics, and recording diagnostics.
- Primary command buffer reuse was forced off for the run with `XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE=0` and `XRE_OPENXR_VULKAN_PRIMARY_REUSE=0`.
- Session inspected: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_10-47-41_pid9120`.
- Captures were saved under `mcp-captures/`, including fixed-camera viewport/final-post captures and moving-camera captures.

Swapchain and presentation evidence:

- The steady-state sampled frames do not look swapchain-starved. Acquire and swapchain-image wait timings were tiny, usually around hundredths or thousandths of a millisecond.
- Every sampled moving-camera profiler frame recorded fresh Vulkan work: `cleanReuse=0`, `recorded=1`, `swapW=2`, `sceneW=2`, `missingScene=0`, and `dropped=0`.
- `log_vulkan.log` repeatedly reported live frame ops and two pre-overlay swapchain scene writers. There were no recurring acquire timeout/not-ready/out-of-date errors and no missing scene swapchain writer frames.
- The moving-camera MCP captures advanced normally during the diagnostic window and did not show a visible backstep in the captured output.
- There was one startup present skip while resources settled:
  - `Skipping present tick while resize/presentation resources settle. Reason=VP[0] has no active resource generation; pending=<none> DroppedFrameOps=11 ...`
  - The preceding render-resource logs show a stale pending TSR generation being discarded after the camera resolved from global `aa=Tsr` to current `aa=None`.
  - That can explain an early startup old-frame/blank-frame style blip, but it does not match a recurring atlas-only runtime flicker.

Directional atlas evidence:

- Directional atlas was enabled: `[DirectionalShadowAudit][Setting] frame=0 UseDirectionalShadowAtlas=True`.
- The atlas path submitted desktop cascade requests, allocated four resident cascade tiles on one page, and deferred bind enabled shader atlas sampling with four active cascades.
- Early startup frames had expected warm-up/stale tile fallback records before all cascades became rendered/resident.
- After warm-up, atlas summaries stayed coherent: four requests, four allocations, four resident tiles, no skipped/not-relevant tiles, and deferred bind kept using atlas records for all four cascades.
- During camera movement, atlas tiles were dirtied by `ContentChanged`, `ProjectionOrCameraFitChanged`, `ReuseDisabled`, and `DynamicLight`. That is heavy, but in this run it did not correlate with present starvation, dropped frame ops, or command-buffer clean reuse.

Static source audit:

- Directional shadow atlas work is driven from `XRWorldInstance.GlobalPreRender()` into `Lights3DCollection.RenderShadowMapsInternal()`, then `UpdateShadowAtlasRequests()`, `SubmitDirectionalShadowAtlasRequests()`, and `PublishShadowAtlasDiagnostics()`.
- Directional-light cascade state is source-keyed for desktop/HMD, and `VPRC_LightCombinePass.BindDirectionalAtlasShadows()` selects atlas state from the active rendering camera source and requires sampleable atlas tiles before enabling atlas sampling.
- Vulkan frame-loop code waits per in-flight slot and per acquired swapchain image, which would have shown up as acquire/wait/present stalls if ordinary swapchain starvation were the active failure.
- Desktop primary command-buffer reuse was disabled in this run, but `TryReuseLastSwapchainWriterVariant()` remains an always-reachable fallback when a present tick has no static frame ops, no dynamic UI ops, and no overlay-preserve work. This was not observed in the run, but it remains a plausible place to add a kill switch or diagnostic if the live flicker can be caught.

Current interpretation:

- The captured diagnostic run does not support swapchain starvation as the root cause.
- The only observed present skip was a startup resource-generation settle caused by stale TSR resources being discarded, not a recurring atlas/render starvation loop.
- If the user's live symptom still looks exactly like "the main view jumps back a few frames," the next isolation target is a rare no-frame-op present path or temporal-history path, not directional atlas allocation itself.
- If the symptom is actually a shadow/lighting flash that reads like an old image, the next target is atlas stale-tile publication and cascade dirtying cadence, especially the camera-fit invalidation path while the light is dynamic.

Recommended next isolation:

- Catch a visible flicker with `XRE_VULKAN_FRAME_DATA_REUSE_DIAG=1`, `XRE_VK_TRACE_SWAPDRAW=1`, and primary reuse disabled, then check for any frame with `missingScene>0`, `dropped>0`, `cleanReuse>0`, or no `FrameOps`/swapchain writer entry.
- Temporarily force temporal AA off for the active camera and global settings in the same user repro. In this diagnostic run the camera settled to `aa=None`, so active TSR did not explain steady-state behavior here.
- Add a diagnostic guard or env kill switch around `TryReuseLastSwapchainWriterVariant()` so any no-frame-op present that reuses an older swapchain writer becomes visible in logs instead of silently presenting a previous writer variant.
- During a visible flicker, capture both the viewport and `FinalPostProcessOutputTexture`. If the render target is stable but the window backsteps, the issue is present/command reuse. If the render target also backsteps, it is upstream in the render graph or temporal/shadow inputs.

### 2026-07-02 Directional Atlas Dirty/Clean Record-Index Fix

User follow-up: the flicker is easy to reproduce by moving the camera, setting directional atlas dirty flags, then stopping so the dirty flags clear. User also suspected this was not temporal history.

New focused evidence:

- Run root: `Build/_AgentValidation/20260702-121500-dir-atlas-stop-flicker`.
- Pre-fix captures showed the final post-process texture changing with the flicker, so the problem was upstream of presentation/swapchain reuse.
- The pre-fix lighting audit showed dirty directional cascade frames could publish a different record ordering than the following clean frame:
  - dirty frame example: `c0=(1,0,0,2) c1=(1,0,0,3) c2=(1,0,0,0) c3=(1,0,0,1)`,
  - clean frame after stop: `c0=(1,0,0,0) c1=(1,0,0,1) c2=(1,0,0,2) c3=(1,0,0,3)`.
- The physical atlas tile layout also depended on sorted request order. A dirty subset can move ahead of clean reusable cascades, so the 2x2 directional cascade group could be assigned in dirty-priority order instead of cascade-index order.

Root cause:

- Directional cascade atlasing was letting the per-frame dirty-priority request sort leak into two published identities:
  - physical 2x2 atlas tile placement,
  - public `ShadowAtlasFrameData` record index used by directional cascade atlas slots and `DirectionalShadowAtlasPacked0`.
- When camera motion stopped and dirty flags cleared, those identities snapped back to normal cascade order. Vulkan then observed a transient atlas metadata mismatch that looked like the main view jumped to an old/wrong frame.
- This is not swapchain starvation and is not temporal history.

Implemented fix:

- `TryAllocateDirectionalCascadeGroups` now assigns the grouped directional 2x2 cells by `FaceOrCascadeIndex` instead of by sorted request order.
- After allocation solving, `ShadowAtlasManager` now sorts the frame allocation record table deterministically by `ShadowRequestKey` and rebuilds `_currentAllocationIndices` before directional/point group publication.
- Render scheduling still uses the dirty-priority request order, but the published atlas record slots are stable across dirty and clean frames.

Validation:

- Added `SolveAllocations_DirectionalCascadeGroupLayoutIgnoresDirtyRequestOrdering`, which verifies both atlas tile layout and record indices remain stable when only one cascade is dirty and the other cascades reuse prior resident tiles.
- Passed:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=SolveAllocations_DirectionalCascadeGroupLayoutIgnoresDirtyRequestOrdering" --logger "console;verbosity=normal"`.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=SolveAllocations_BalancesDirectionalCascadesIntoOneAtlasPage|Name=SolveAllocations_DirectionalCascadeGroupLayoutIgnoresDirtyRequestOrdering|Name=SolveAllocations_PublishesGroupedDirectionalCascadeRecord|Name=SolveAllocations_PublishesGroupedDirectionalCascadeRecordForResidentSubset|Name=SolveAllocations_DirectionalCameraFitRefreshKeepsStaleCascadeGroupUntilRenderCompletes|Name=RenderScheduledTiles_RendersNeverRenderedDirectionalCascadeGroupPastTileBudget" --logger "console;verbosity=normal"`.
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal`.
  - `git diff --check` with line-ending warnings only.
- Live post-fix Vulkan/MCP run:
  - Session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-02_11-28-42_pid19920`.
  - Captures: `Build/_AgentValidation/20260702-121500-dir-atlas-stop-flicker/mcp-captures/postrecord/`.
  - Dirty frame `351` and `353` kept stable desktop slots and deferred bind records: `c0=0`, `c1=1`, `c2=2`, `c3=3`.
  - Clean frame `360` after stopping had `dirtyCascades=0`, `renderedCascades=0`, and still bound `c0=(1,0,0,0) c1=(1,0,0,1) c2=(1,0,0,2) c3=(1,0,0,3)`.
  - A log sweep found zero reordered enabled deferred binds and zero reordered published desktop cascade slots in the post-fix run.
  - Vulkan logs during the same window continued to show successful presents and scene swapchain writers; no swapchain starvation evidence appeared.

### 2026-07-02 Directional Atlas Grouped-Render FPS Fix

User follow-up: flicker still reproduces, and the Vulkan editor frame rate is very low. Asked whether shadow-map validation logging is the cause.

New diagnosis:

- Shadow audit logging can materially reduce FPS when `XRE_DIRECTIONAL_SHADOW_AUDIT=1`, `XRE_SHADOW_AUDIT=1`, `XRE_VK_TRACE_SWAPDRAW=1`, `XRE_VK_TRACE_DRAW=1`, or `XRE_VULKAN_FRAME_DATA_REUSE_DIAG=1` are enabled.
- In the last agent shell, no `XRE_*` diagnostic env vars were set, so logging was not the only plausible explanation for the low frame rate observed there.
- The post-fix Vulkan logs showed a larger engine-side cost: dirty directional atlas frames attempted a grouped cascade atlas render, failed it, then rendered all four 1024 cascades sequentially. Example audit frames reported `groupedAttempted=True`, `groupedSucceeded=False`, `fallbackReason=GroupedAtlasRenderFailed`, and shadow render time around `42-49ms`.
- That means the grouped allocation was being treated as a grouped render unit even when the grouped draw path could not actually execute for the current light/backend/published cascade state.

Implemented fix:

- `DirectionalLightComponent.CanRenderGroupedCascadeShadowAtlasTiles(...)` now preflights the same hard prerequisites the grouped renderer needs: active cascades, layered atlas render plan, shadow render pipeline, valid published matrices, and valid indexed tile rects.
- `ShadowAtlasManager.RenderScheduledTiles()` now enters the grouped directional render scheduling branch only when that preflight succeeds.
- If the grouped allocation exists but the grouped draw path is unavailable, render scheduling falls through to normal per-tile rendering. Clean cascades in the same 2x2 directional group are skipped instead of being forced through the four-cascade sequential fallback.
- The sequential fallback still remains inside `TryRenderDirectionalCascadeGroup()` for unexpected grouped-render failures after the preflight succeeds.

Validation:

- Passed:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=DirectionalCascadeAtlasGroupedPath_UsesAtlasBackendAndLayeredRendering" --logger "console;verbosity=normal"`.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name~DirectionalCascadeAtlasGroupedPath_UsesAtlasBackendAndLayeredRendering|Name~GroupedDirectionalCascadeAtlasFailure_RendersSequentialAtlasTiles|Name~SolveAllocations_DirectionalCascadeGroupLayoutIgnoresDirtyRequestOrdering|Name~RenderScheduledTiles_RendersNeverRenderedDirectionalCascadeGroupPastTileBudget" --logger "console;verbosity=minimal"`.
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal`.
- The first editor build attempt was invalid because it ran in parallel with a test build and hit a compiler-server file lock on `XREngine.dll`; rerunning serially after `dotnet build-server shutdown` passed.
- Existing Magick.NET vulnerability warnings remain unrelated.

## Active Questions

- Does flicker show up in saved viewport screenshots, or only live in the editor window?
- Do left/right OpenXR preview textures remain stable while the desktop view flickers?
- Does the desktop final post-process texture flicker along with the presented viewport?
- Are visible command counts or command-chain reuse/refresh counts oscillating when the camera is close to the eye rig?
- Are OpenXR collect-visible frame numbers, rig validation logs, or render-pose logs discontinuous during the flicker?
- Is the issue worse under `CollectVisibleThread` pacing than `DedicatedThread`, or is it independent of OpenXR pacing mode?
