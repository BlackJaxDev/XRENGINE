# OpenXR SteamVR Eye Culling And Directional Cascades - 2026-07-07

## Problem

After the OpenXR SteamVR Vulkan path started submitting headset frames again, the user reported two rendering issues:

- One eye could cull meshes differently from the other eye.
- Directional light cascades appeared unaffected by the editor toggle, and changing away from sequential cascade rendering did not appear to change behavior.

## Findings

- SteamVR OpenXR Vulkan was resolving `VrViewRenderMode=SinglePassStereo` to `OpenXrSinglePassCompatibility`, not true multiview. In that mode, `SequentialViews`, `ParallelCommandBufferRecording`, and compatibility single-pass all render per-eye external swapchains, so they should look visually identical.
- OpenXR visibility collection builds one shared stereo command set with a combined left/right frustum. Each external eye pass then rendered that shared list separately.
- The GPU mesh pass could run a second per-eye frustum/BVH cull using only the currently rendered eye camera. That is stricter than the shared stereo collect step and can reject commands visible to the other eye before the per-view indirect draw stream can use them.
- Vulkan directional cascades were disabled by a backend guard in `DirectionalLightComponent.CascadeShadows.cs`, so the editor cascade toggle could update light settings without reaching the cascade render path.
- The cascade render-mode selector still falls back to sequential on Vulkan when grouped/layered cascade rendering is unavailable. The diagnostic reason is `VulkanLayeredRenderingDisabled`; this is separate from whether cascades are enabled at all.
- The validation smoke still emitted primary directional shadow descriptor warnings for `PrimaryRasterDepth`, but not for cascade `RasterDepthArray` textures. Treat primary shadow descriptor readiness as a follow-up if non-cascade primary shadows still look wrong.

## Changes

- External OpenXR eye passes now use the shared stereo-visible command list as a GPU pass filter instead of running a second strict per-eye cull. This preserves the combined stereo visibility set and prevents left/right eye command loss from the extra culling stage.
- Single-eye GPU view descriptors now label view slot 0 as right-eye when the active camera is the right-eye camera. The slot remains 0 for a one-eye external swapchain pass, but the descriptor no longer advertises a left-eye identity for right-eye work.
- Vulkan directional cascades are enabled by default for SteamVR and ordinary Vulkan sessions. Known Monado OpenXR runtime paths remain guarded by default.
- Added `XRE_VULKAN_DIRECTIONAL_CASCADES`:
  - `1`, `true`, `yes`, `on`, or `force`: force Vulkan directional cascades on.
  - `0`, `false`, `no`, `off`, `disable`, or `disabled`: force them off.
- Documented the OpenXR external-swapchain mode behavior, shared visibility rule, and Vulkan cascade override.

## Validation

Build:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
```

Result: build passed with 0 errors. Existing warnings remained, including NuGet vulnerability advisories and a pre-existing nullable warning in `VulkanRenderer.CommandChainLowering.cs`.

OpenXR SteamVR smoke:

```powershell
$env:XRE_WORLD_MODE='UnitTesting'
$env:XRE_UNIT_TEST_WORLD_KIND='Default'
$env:XRE_UNIT_TEST_VR_MODE='OpenXR'
$env:XRE_UNIT_TEST_VR_VIEW_RENDER_MODE='SinglePassStereo'
$env:XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS='1'
$env:XRE_OCCLUSION_CULLING_MODE='Disabled'
$env:XRE_VULKAN_CAPTURE_EYE_OUTPUTS='1'
$env:XRE_OPENXR_VULKAN_TRACE='1'
$env:XRE_DIRECTIONAL_SHADOW_AUDIT='1'
dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --smoke-frames 20 --smoke-timeout-seconds 90 --openxr-smoke-summary Build\_AgentValidation\20260707-210743-openxr-vr-culling-cascades\reports\openxr-smoke-summary.json
```

Summary:

- Log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-07_21-07-44_pid43472`
- Runtime: SteamVR
- Renderer: Vulkan
- `viewRenderImplementationPath=OpenXrSinglePassCompatibility`
- `submittedFrameCount=20`
- per-eye acquire/wait/release counts: 20/20 for left and right
- `noLayerFrameCount=0`
- `endFrameFailureCount=0`
- `failures=[]`
- `warnings=[]`
- `missedDeadlineCount=20` remained as a pacing/performance issue.

Directional shadow audit evidence from that run:

- `CascadeUpdate` reported `activeCascades=4 requestedCascades=4`.
- `LegacyRender` reported `renderCascades=True cascadeRenderCount=4 activeCascades=4`.
- `ForwardBind` and `DeferredBind` reported `cascades=True activeCascades=4 cascadeRasterDepthTex=True`.
- The cascade render-mode fallback reported `requested=Auto, effective=Sequential, reason=VulkanLayeredRenderingDisabled`.

## Follow-Ups

- If primary, non-cascade directional shadows still fail to sample, investigate `PrimaryRasterDepth` descriptor readiness. The smoke log showed repeated warnings where the wrapper was active/uploaded but not generated.
- If SteamVR pacing remains poor, continue from the missed-deadline/frame timing logs rather than the culling/cascade path.

## 2026-07-07 21:13 Regression Report

Latest user run:

- Log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-07_21-13-57_pid43740`
- Sponza import started and texture cache hits were logged, but `log_meshes.log` never reached `Import completed`.
- OpenXR never submitted headset frames. `log_vulkan.log` reported 65 repeated session-start deferrals and zero `xrEndFrame` or OpenXR eye batch submit completions.
- The repeated deferral reason was `imported texture streaming is still active (imports=1, pendingTransitions=0, activeGpuUploads=0, activeDecodes=0, queuedDecodes=0)`.
- This was an over-conservative session gate: an active model-import scope with no concrete texture decode/upload/transition work kept Vulkan OpenXR session creation blocked while Sponza mesh processing continued on workers.

Additional change:

- The startup texture gate now blocks OpenXR Vulkan session creation only when imported texture streaming has render-affecting work: pending residency transitions, active/queued decodes, active GPU uploads, or Vulkan upload-service work. A model import scope by itself no longer prevents session creation once those queues are idle.

Validation after the change:

- Build passed with 0 errors and the existing warning set.
- Smoke summary: `Build/_AgentValidation/20260707-2113-openxr-startup-texture-gate/reports/openxr-smoke-summary.json`.
- Log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-07_21-19-34_pid31696`.
- SteamVR OpenXR Vulkan reached `SessionCreated` and `SessionRunning`, created two 2688x2688 swapchains, and submitted 20 frames.
- `noLayerFrameCount=0`, `endFrameFailureCount=0`, `failures=[]`, and `warnings=[]`.
- The old stale import deferral did not recur. The new log had only one session deferral, `desktop renderer has completed too few startup frames (3/4)`, and `log_meshes.log` reached `Import completed` for Sponza.

## 2026-07-07 21:40 Right-Eye Flicker And Cascade Follow-Up

User report after the startup gate fix:

- The right eye flickered between the proper render and black.
- On frames where the eye was not fully black, the skybox could still flicker black.
- Directional cascades still did not appear to honor atlas on/off or non-sequential render modes.

Additional findings:

- OpenXR per-eye acquire, wait, release, and submit paths were not logging failures. The latest user symptom looked like a rendered-target finalization problem rather than an OpenXR acquire/release problem.
- Vulkan command-buffer finalization counted swapchain writes only for direct draw-operation paths. When a swapchain eye render completed through the mesh secondary command-chain path, the finalizer could still see zero actual scene writes and treat the external eye target as unwritten. That explains intermittent black eye/skybox output when the secondary-chain path was selected.
- Batched OpenXR acquisition rendered left then right, but released acquired images right then left. There was no logged failure, but this was unnecessarily asymmetric with per-eye acquire/render order.
- The directional cascade render planner still had a legacy hard Vulkan fallback to sequential with reason `VulkanLayeredRenderingDisabled`, even when Vulkan capabilities reported `multiViewport=True`, `shaderOutputViewportIndex=True`, and `shaderOutputLayer=True`.
- Primary directional shadow-map depth textures were constructed as ordinary `XRTexture2D` framebuffer attachments. The Vulkan image-backed descriptor readiness helper asked the base texture readiness path first, so image-backed/framebuffer textures could report `generated=false` before refreshing the physical image group.

Additional changes:

- Swapchain mesh secondary command-chain runs now count as actual swapchain writes when their draw operation targets the current swapchain. This keeps OpenXR eye targets out of the unwritten-target cleanup path after the scene was rendered by secondary mesh commands.
- Batched OpenXR eye images are now released in the same left-to-right order used for acquisition and rendering.
- Removed the unconditional Vulkan directional cascade fallback. Vulkan now uses the selected directional cascade render mode when the layered-rendering planner reports support.
- Vulkan directional atlas eligibility now uses the same Monado-only guard as the general cascade path, so SteamVR and ordinary Vulkan sessions honor the atlas toggle.
- Grouped directional cascade atlas allocation/rendering is enabled for SteamVR and ordinary Vulkan sessions when the renderer exposes indexed viewport/scissor and shader viewport-output support. Known Monado OpenXR Vulkan sessions remain on the guarded sequential atlas path.
- Directional primary shadow-map raster depth/color attachments now use framebuffer-texture construction.
- Vulkan image-backed texture descriptor readiness now refreshes framebuffer/image-backed physical groups before deciding readiness failed.
- Added focused coverage for the swapchain secondary-command-chain write-count case, Vulkan atlas eligibility, and a source guard against reintroducing the unconditional Vulkan cascade fallback.

Validation after the changes:

- Focused tests passed:

  ```powershell
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name~DirectionalAtlas_VulkanUsesDepthOnlyContractWithoutChangingOpenGl|Name~DirectionalCascadeRenderMode_DefaultsToAutoPath|Name~DirectionalCascadeLayeredModes_AreExposedInRuntimeAndDocs|Name~DirectionalLight_UsesSelectedDepthShadowStorageFormat|Name~DirectionalLight_Evsm2ShadowMaterialKeepsDepthAndAddsMomentColorTarget|Name~DirectionalDepthCascadeAtlasFallbacks_KeepReceiverArrayBound|Name~SwapchainCommandChainSecondaryRuns_CountAsActualWrites" -v:minimal
  ```

- Editor build passed:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal
  ```

- SteamVR OpenXR Vulkan smoke passed:

  ```powershell
  dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --smoke-frames 20 --smoke-timeout-seconds 90 --openxr-smoke-summary Build\_AgentValidation\20260707-210743-openxr-vr-culling-cascades\reports\openxr-smoke-summary-after-grouped-atlas-fix.json
  ```

  Summary:

  - Log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-07_21-46-53_pid45708`
  - Runtime: SteamVR
  - Renderer: Vulkan
  - `viewRenderImplementationPath=OpenXrSinglePassCompatibility`
  - `submittedFrameCount=20`
  - per-eye acquire/wait/release counts: 20/20 for left and right
  - `noLayerFrameCount=0`
  - `endFrameFailureCount=0`
  - `failures=[]`
  - `warnings=[]`
  - No `NoEyeFrameOps`, `MissingSceneSwapchainWrites`, `NoSwapchainWrites`, OpenXR eye render failures, or shadow descriptor readiness failures were found in the log session.
  - `missedDeadlineCount=20` remains a separate SteamVR pacing/performance issue.

Directional shadow audit evidence after the changes:

- `CascadeUpdate` reports `activeCascades=4 requestedCascades=4`.
- `AtlasGroupedRender` reports `mode=InstancedLayered backend=AtlasPage`.
- `AtlasLight` reports `groupedAttempted=True groupedSucceeded=True selectedBackend=AtlasPage fallbackReason=None`.
- `ForwardBind` and `DeferredBind` report `source=Hmd`, `requestedAtlas=True`, `shaderAtlasEnabled=True`, `cascades=True`, `activeCascades=4`, `atlasRequests=8`, `atlasPages=1`, and HMD cascade atlas records.
- `AtlasRenderSummary` reports grouped frames with `ShadowAtlas.Directional.GroupedFrames=1` and no steady-state sequential fallback.
- No `Directional cascade render mode fallback` warning was emitted in the final smoke run.

Remaining note:

- `missedDeadlineCount=20` remains a separate SteamVR frame-pacing/performance issue. The final smoke did not show eye-submit failures, unwritten swapchain markers, or shadow descriptor readiness failures.

## 2026-07-07 21:50 Editor Resize Black / Vulkan OOM

User report:

- Resizing the editor window turned the window black.
- Visual Studio reported repeated `XREngine.Rendering.Vulkan.VulkanOutOfMemoryException` and a couple of `System.InvalidOperationException` throws from `XREngine.Runtime.Rendering.dll`.

Log evidence:

- Log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-07_21-50-17_pid45204`.
- The editor used the interactive resize modal-loop timer path.
- The desktop pipeline resized 1920x1080 -> 2560x1369 -> 1920x1080, then later down to smaller window sizes.
- Immediately after the 2560x1369 -> 1920x1080 generation commit, framebuffer creation started throwing image-allocation deferrals such as:
  - `requested=8847360, allocated=21415454096, projected=21424301456, largestHeap=25503465472, deferLimit=21422910996, activeVkAllocations=2234`.
- The failure stack was Vulkan image allocation for FBO attachments:
  - `AllocateImageMemoryWithFallback`
  - `VkImageBackedTexture.CreateDedicatedImage`
  - `VkFrameBuffer.ResolveTextureAttachment`
  - `VPRC_BindFBOByName` / `VPRC_RenderQuadToFBO`.
- `log_vulkan.log` showed the allocator byte counter near the OpenXR image preflight limit while tracked render VRAM was only around 0.58 GB. The pressure was mostly aggregate allocator usage from VMA/host-visible resources, not render-target texture VRAM.

Root cause:

- The OpenXR Vulkan image allocation preflight compared `MemoryAllocator.TotalAllocatedBytes` against the largest Vulkan heap, then used that result to defer device-local image creation.
- That mixed unrelated memory classes. Under SteamVR/OpenXR with the editor running, aggregate allocator bytes could reach the defer limit even when tracked render VRAM had ample budget, causing resize-created FBO attachments to fail and the window to go black.

Change:

- `VulkanRenderer.Initialization` now evaluates image byte pressure from `RuntimeRenderingHostServices.Current.TrackedVramBytes` and `TrackedVramBudgetBytes`, with the existing image pressure ratio/reserve applied to that render-VRAM budget.
- The allocation-count guard remains in place, so Vulkan still defers image allocation when approaching `maxMemoryAllocationCount`.
- The expected image-allocation deferral classifier now recognizes the stable `Vulkan image allocation deferred under` diagnostic prefix, including tracked render-VRAM and allocation-count deferrals, while keeping the older allocator-pressure phrase for compatibility.
- The generic OpenXR allocator-pressure throttle remains separate for startup/upload scheduling; only device-local image creation stopped using aggregate allocator bytes as its byte-pressure source.
- Added a source-level contract test so the image preflight keeps using tracked render VRAM and does not regress to aggregate allocator bytes.

Validation:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=OpenXrVulkanImagePressure_UsesTrackedRenderVramInsteadOfAggregateAllocatorBytes" -v:minimal
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal
```

Results:

- Focused test passed: 1 passed, 0 failed.
- Editor build passed: 0 errors.
- Editor build emitted existing `NU1902`/`NU1903` Magick.NET advisories. The focused test build also surfaced the pre-existing `CS8602` in `VulkanRenderer.CommandChainLowering.cs:417`.
- Validation output is under `Build/_AgentValidation/20260707-215017-resize-vulkan-oom/logs/`.

## 2026-07-07 22:06 Interactive Resize, Right-Eye Flicker, Skybox Toggle Lag

User report:

- Dynamic framebuffer resize during a window drag still stopped rendering until mouseup.
- The OpenXR right eye could still flicker to black.
- Toggling off the skybox scene node caused editor lag.

Log and profiler evidence:

- Log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-07_22-06-09_pid29880`.
- The Win32 modal-loop resize timer was active, but timer renders were repeatedly suppressed as reentrant while full framebuffer resizes only committed after `win32-exit-size-move`.
- Render-thread upload jobs waited for about 2.4 seconds during the drag, matching the visual "stops until mouseup" symptom.
- The Vulkan frame loop saw the size mismatch only through the normal settled-resize path, not the interactive-resize path.
- No OpenXR acquire/wait/release failures were logged for the right eye. The remaining black-eye symptom was consistent with a batched Vulkan eye render returning false after it had already marked the frame as handled, which could skip the sequential fallback and end the frame with no layers.
- The skybox component itself was not the expensive profiler node. The large hitch was in `ShadowAtlasManager.PublishFrameData.BuildDirectionalDiagnostics`, where directional shadow audit diagnostics linearly searched the per-frame diagnostic list for every directional request/event/failure.

Root causes:

- `XRWindow.RenderInteractiveResizeFrame` used a short-circuit guard that could set `_interactiveResizeRenderActive` and then return early when `_normalRenderActive` was set, without clearing `_interactiveResizeRenderActive`. After that, later timer ticks looked permanently reentrant until the normal path recovered.
- The interactive resize inline-render gate only allowed OpenGL current-thread rendering. On the Vulkan render-owner thread this could enqueue instead of rendering directly from the modal timer.
- `TryRenderVulkanEyesBatch` set `handled=true` before acquiring and rendering both eye swapchains. If the batched path then returned false, the frame lifecycle treated the batch as handled and did not try the sequential per-eye Vulkan path.
- Directional atlas diagnostics used an O(n) light lookup inside loops over directional cascade requests, render events, and reservation failures.

Changes:

- Split the interactive resize active guard into separate checks. If a normal render is active after claiming the interactive flag, the code now clears `_interactiveResizeRenderActive` before returning and records `normal-render-active` instead of generic `reentrant`.
- Allowed interactive resize rendering inline when the caller is the render-owner thread, including Vulkan.
- Batched Vulkan OpenXR eye rendering now releases any acquired swapchain images and clears `handled` when acquire/wait/render returns false or throws a recoverable exception, allowing the existing sequential per-eye fallback to run for that frame.
- Directional atlas light diagnostics now maintain a per-frame `Dictionary<Guid, int>` index keyed by light id and clear it anywhere the diagnostic list is cleared.
- Updated the stale default-pipeline source contract to the current stereo-pass condition while adding focused coverage for the resize guard, OpenXR batch fallback, and diagnostic index.

Validation:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=XRWindow_InteractiveResizeGuardClearsActiveFlagWhenNormalRenderIsActive|Name=VulkanOpenXr_EyeSubmitRecordsBothEyesBeforeOneFenceWait|Name=DirectionalCascadeAtlasGroupedPath_UsesAtlasBackendAndLayeredRendering" -v:minimal
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal
```

Results:

- Focused tests passed: 3 passed, 0 failed.
- Editor build passed: 0 errors.
- Existing warning set remained, dominated by Magick.NET `NU1902`/`NU1903` advisories.
- Validation output is under `Build/_AgentValidation/20260707-220609-resize-eye-skybox/logs/`.

## 2026-07-07 22:32 Editor AA Toggle Demotes Textures

User report:

- No anti-aliasing mode appeared to affect the editor scene view.
- Switching AA mode caused imported textures to stream down to their lowest preview mip and stay there, followed by editor lag.

Log evidence:

- Log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-07_22-28-08_pid33364`.
- `FrameProfileChanged` entries show AA toggles were reaching the render pipeline: `Tsr -> Fxaa`, `Fxaa -> Smaa`, and `Smaa -> Tsr`.
- Before the first AA toggle, texture streaming reported healthy promotion state: all 39 tracked textures visible, none at preview, promotions allowed.
- Immediately after the AA render-resource rebuilds, `Texture.VramPressure` diagnostics began demoting Sponza textures.
- Later streaming frames reported `budget=0MB`, `pressure=True`, `allowPromotions=False`, `atPreview=39`, and `promoted=0`, matching the visual "lowest mip forever" failure.

Root causes:

- The Vulkan texture-streaming budget path used aggregate allocator bytes from `MemoryAllocator.TotalAllocatedBytes` and compared them to a synthetic largest-heap limit. With VMA this over-counted unrelated allocation classes, so AA render-target churn could make the streamer think no managed texture budget remained.
- The editor scene-panel render path supplied a destination FBO, but `VPRC_RenderToWindow` only honored bound output FBOs for external swapchain/stereo/offscreen cases. A normal docked editor view could therefore miss the post-AA final output path even though the AA resources and passes were rebuilt.

Changes:

- VMA now exposes a reusable, allocation-free device-local heap budget snapshot from `vmaGetHeapBudgets`.
- `TryGetVulkanAllocatorBudgetSnapshot` uses that VMA device-local heap usage/budget when available and only falls back to aggregate allocator bytes for non-VMA allocators.
- `VPRC_RenderToWindow` now treats any explicit `RenderState.OutputFBO` as the final output target, which includes the editor scene panel FBO.

Validation:

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj --no-restore
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore
```

Results:

- Runtime rendering build passed: 0 errors.
- Editor build passed: 0 errors.
- Existing warning set remained, dominated by Magick.NET `NU1902`/`NU1903` advisories.

## 2026-07-07 22:41 Random Texture Demotion and AA Safe-Path Bypass

User report:

- Imported textures dropped to the lowest-res mip randomly, without changing AA or other visible settings.
- Editor AA still did not visibly affect the scene view.

Log evidence:

- Log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-07_22-41-58_pid46348`.
- Startup settings included `EffectiveAntiAliasingMode=Tsr`, Vulkan rendering, OpenXR active/requested, and `RenderWindowsWhileInVR=True`.
- Early texture streaming frames reported a healthy VMA budget and allowed promotions: about 15.7 GB budget, about 520-649 MB allocated, and `allowPromotions=True`.
- Texture demotions began around frame 1459 before the later AA profile change. The AA switch in `log_rendering.log` happened later at `22:43:41` with `aa:Tsr->Fxaa`.
- By frame 1800, texture streaming reported `budget=0MB`, `allowPromotions=False`, all 39 tracked imported textures at preview, and VMA device-local pressure from `allocated=17789976064, budget=17138328796`.

Root causes:

- The prior VMA integration correctly exposed global device-local heap pressure, but the texture streamer still converted that global pressure into the imported-texture managed budget. Non-texture render resources could therefore make imported textures look as if they had 0 MB available, causing visible demotion to preview mips even when imported texture residency was only about 519 MB.
- The OpenXR+Vulkan desktop-safe final-output branch forced presentation from `FinalPostProcessOutputFBOName`. That bypassed the normal AA output selector, so FXAA/SMAA/TSR could rebuild and run without their output becoming the scene-panel presentation source.

Changes:

- Replaced the streaming budget mutation helper with a pressure query. Vulkan allocator pressure now suppresses new promotions while preserving current imported-texture residency.
- Kept real managed texture-budget pressure behavior intact, so genuine imported texture over-budget cases can still demote.
- Removed the stale `OpenXrVulkanSafeFinalOutput` branch from standard viewport final-output selection. The direct-present safe path still applies inside final blit creation, but it now receives the selected AA output source.
- Added contract coverage for Vulkan heap pressure not zeroing managed texture budget and for the OpenXR+Vulkan final-output path retaining FXAA/SMAA/TSR source selection.

Validation:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=ImportedTextureStreaming_VulkanHeapPressureSuppressesPromotionsWithoutZeroingManagedBudget|Name=VulkanOpenXr_EyeSubmitRecordsBothEyesBeforeOneFenceWait" -v:minimal
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal
```

Results:

- Focused tests passed: 2 passed, 0 failed.
- Editor build passed: 0 errors.
- Existing warning set remained, dominated by Magick.NET `NU1902`/`NU1903` advisories.

## 2026-07-07 22:51 AA Black Output and Right-Eye Flicker

User report:

- AA still did not visibly affect the editor view.
- Selecting FXAA or SMAA made the 3D scene render black, although it made the VR eye-preview panels appear again.
- TAA/TSR did not render the eye-preview panels.
- Selecting MSAA lagged the editor.
- The right eye still flickered black randomly, possibly from collect-visible or occlusion.

Log evidence:

- Log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-07_22-51-37_pid51040`.
- Startup used `EffectiveAntiAliasingMode=Tsr`, Vulkan, OpenXR, and `RenderWindowsWhileInVR=True`.
- FXAA selection rebuilt the frame profile, but the active safe-path layout still had only 18 textures and 7 FBOs. Rendering then repeatedly warned that `FxaaOutputTexture` was missing.
- SMAA selection showed the same presentation failure with `SmaaOutputTexture` missing.
- MSAA selection rebuilt a much larger profile, increasing the layout to 27 textures and 9 FBOs with a `BuildMs` around 2697 ms.
- After AA/MSAA resource churn, Vulkan logs showed allocator pressure, deferred eye mirror copies, failed physical allocation backoff, and batched eye rendering falling back to sequential. That matches right-eye black frames more closely than collect-visible or occlusion evidence.
- Texture streaming stayed healthy in this run: imported textures remained promoted and did not collapse to preview mips.

Root causes:

- The OpenXR+Vulkan desktop-safe profile selected FXAA/SMAA final outputs, but FXAA resources were excluded from the safe-path declarative resource layout. That made the scene-panel final blit sample a missing output texture.
- SMAA still owns temporary resources inside `VPRC_SMAA` instead of declaring them through the render-resource layout, so Vulkan can select an output that the declarative planner cannot validate or materialize.
- TAA/TSR remain suppressed in OpenXR/VR by `DisableHistoryBasedVrEffects`, so they should not be expected to drive editor eye-preview output in this path yet.
- Deferred MSAA was still allowed to allocate and branch in the OpenXR+Vulkan desktop-safe profile, causing a large resource rebuild and enough Vulkan allocation pressure to correlate with right-eye flicker/fallback.

Changes:

- Declared FXAA output texture/FBO resources for the OpenXR+Vulkan desktop-safe profile.
- Added `RuntimeEnableDeclaredSmaa` so Vulkan does not select SMAA final-output resources until SMAA is declarative.
- Kept the final-output selector on the normal FXAA/SMAA/TSR path, but routed it through the declared-SMAA guard.
- Disabled deferred MSAA branches and declarative MSAA deferred resources for the OpenXR+Vulkan desktop-safe profile while preserving normal deferred MSAA resources outside that path.
- Added contract coverage for FXAA safe-path resources, Vulkan SMAA exclusion, and OpenXR-safe MSAA resource gating.

Validation:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=VulkanOpenXr_EyeSubmitRecordsBothEyesBeforeOneFenceWait|Name=AlphaToCoverageTransparency_RoutesToMaskedPass_AndRequestsA2CState|Name=DefaultRenderPipeline_MsaaProfile_DeclaresDeferredMsaaResources|Name=DefaultRenderPipeline_OpenXrVulkanSafeMsaaProfile_DoesNotDeclareDeferredMsaaResources" -v:minimal
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal
```

Results:

- Focused tests passed: 4 passed, 0 failed.
- Editor build passed: 0 errors.
- Existing warning set remained: Magick.NET `NU1902`/`NU1903` advisories plus the pre-existing nullable warning in `VulkanRenderer.CommandChainLowering.cs` during the focused test build.

## 2026-07-07 23:24 Declarative SMAA, Temporal Editor AA, and MSAA Safe-Graph Gating

User report:

- FXAA works in the editor view now.
- SMAA/TAA/TSR still appear to do nothing.
- Selecting MSAA still lags the editor, likely because the deferred MSAA path is being reached.

Log evidence:

- Latest AA-toggle log set showed the OpenXR+Vulkan safe profile still committing only 18 textures and 7 FBOs for TAA/TSR, so temporal AA resources were not present.
- SMAA still had no declared edge/blend/output resources, so it could not be a planner-visible Vulkan post-AA output.
- MSAA selection still made the command planner walk `MsaaGBufferFBO`, `MsaaLightingFBO`, `ForwardPassMSAAFBO`, `DepthPreloadFBO`, and `MsaaLightCombineFBO` references even when those resources were gated out of the safe-path layout. That explains the lag and missing-resource warnings.

Root causes:

- `ShouldUseTemporalAccumulationResources()` returned false for every OpenXR+Vulkan desktop-safe frame, even for the editor panel where a mono/shared temporal history is valid.
- `VPRC_TemporalAccumulationPass.ResolveHistoryIsolationPolicy()` treated an active OpenXR+Vulkan runtime as enough to disable non-stereo temporal history, even when rendering the desktop/editor view rather than an external per-eye swapchain.
- `VPRC_SMAA` owned render targets internally, which made SMAA invisible to the declarative resource layout.
- MSAA branch resources were gated, but the branch graph itself still existed in the safe path.

Changes:

- Declared SMAA edge, blend, and output textures/FBOs in `DefaultRenderPipeline.Resources.cs`.
- Updated `VPRC_SMAA` to reuse pipeline-owned declared SMAA resources when present and only create/remove its own resources as a fallback.
- Re-enabled SMAA final-output selection by changing `RuntimeEnableDeclaredSmaa` to follow `RuntimeEnableSmaa`.
- Included temporal resources in the OpenXR+Vulkan desktop-safe resource profile and allowed velocity/temporal begin/accumulate/commit branches to run when TAA/TSR/DLAA need them.
- Changed temporal history isolation so non-external, non-stereo editor rendering uses `HeadsetShared`, while external OpenXR Vulkan swapchain targets still disable history.
- Omitted deferred/forward MSAA command-graph branches from the OpenXR+Vulkan desktop-safe graph. In this profile, selecting MSAA should no longer build the heavy/missing MSAA graph; the complex-pixel deferred MSAA path remains available outside the safe profile.

Validation:

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "Name=OpenXrVulkanSafeFinalOutput_AllowsDeclaredPostAaOutputs|Name=DefaultRenderPipeline_SmaaProfile_DeclaresSmaaResources|Name=DefaultRenderPipeline_OpenXrVulkanSafeMsaaProfile_DoesNotDeclareDeferredMsaaResources|Name=DefaultRenderPipeline_MsaaProfile_DeclaresDeferredMsaaResources|Name=OpenXrExternalSwapchainTargets_DisableHistoryBasedAaAndTsrScaling" -v:minimal
```

Results:

- Editor build passed: 0 errors.
- Focused tests that matched passed: 4 passed, 0 failed.
- A broader fixture-name test run hit unrelated existing source-contract failures in Monado/Vulkan/GTAO/editor-state tests; those failures were not caused by this AA patch and remain outside this pass.
- Existing warning set remained: Magick.NET `NU1902`/`NU1903` advisories plus the pre-existing nullable warning in `VulkanRenderer.CommandChainLowering.cs`.
