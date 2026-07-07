# OpenXR Monado VR Framerate Investigation - 2026-07-06

## Problem Statement

VR through OpenXR + Monado is not holding the expected frame pacing. VR framerate should take priority over desktop preview framerate, while both should remain healthy.

## Baseline Context

- Validation run root: `Build/_AgentValidation/20260706-103608-openxr-monado-framerate/`
- Unit Testing World settings at investigation start:
  - Render backend: Vulkan, dynamic rendering.
  - VR mode: `MonadoOpenXR`.
  - VR view render mode: `SinglePassStereo`.
  - Desktop preview while in VR: enabled.
  - VSync override: off.
  - Active heavy scene asset: `Sponza2/sponza.obj`.

## Evidence To Collect

- Live editor viewport capture through MCP.
- CPU frame log dump.
- GPU frame dump for desktop `DefaultRenderPipeline`.
- GPU frame dump for single-pass stereo VR `DefaultRenderPipeline`.
- Render/profiler logs from the matching `Build/Logs/...` session.

## Findings

- 2026-07-06 red/black desktop regression follow-up:
  - MCP viewport screenshots were not representative of the user's native window view. Native `PrintWindow` capture reproduced the bug: the desktop scene under ImGui was a red clear, then black after preserving overlay contents.
  - The red clear came from overlay-only swapchain frames. The Vulkan frame-op census counted ImGui overlay mesh draws as scene swapchain writers, and overlay dynamic rendering passes used `AttachmentLoadOp.Clear` when no scene writer ran in the same command buffer. If the active clear color came from the shadow pipeline, the desktop under ImGui became red.
  - After fixing overlay writer classification and overlay load ops, the red clear disappeared, but the window was still black because steady-state desktop frames had `scene=0 overlay=2`.
  - Runtime settings were correct (`RenderWindowsWhileInVR=True`, `VrMirrorMode=FullIndependentRender`, `VrMirrorComposeFromEyeTextures=False`), so the remaining starvation was not mirror mode. The independent desktop editor scene was still allowed to enter VR over-budget auto-skip, which let ImGui keep presenting while the desktop scene did not write a valid swapchain image.
  - Single-pass VR also missed the desktop viewport reconfiguration that two-pass VR already performed, so the desktop viewport was not explicitly restored to independent collect/swap setup when `SinglePassStereo` was active.
- The VR GPU work is not the primary bottleneck. The single-pass stereo VR `DefaultRenderPipeline#36` GPU dump is about 4.7-5.0 ms after warmup.
- The slowest VR GPU nodes are stable across runs:
  - Light combine: about 1.7-1.8 ms.
  - GTAO blur: about 0.65 ms.
  - Opaque deferred geometry: about 0.5 ms.
  - Masked forward leaf draws: about 0.4 ms.
  - Motion vectors: about 0.2-0.25 ms.
- The desktop `DefaultRenderPipeline#28` GPU work is small when it renders: about 2.3 ms in the final run.
- The actual baseline stall was render-thread/window presentation pressure:
  - Baseline steady tail: p50 175.609 ms, p90 205.092 ms.
  - Baseline desktop present averaged 100.407 ms and rendered every frame.
  - Collect-visible waited on the render thread for 159.714 ms on average.
- Skipping desktop scene work under VR budget pressure helped, but was not enough:
  - Steady tail after scene budget skip: p50 106.333 ms, p90 120.523 ms.
  - Desktop scene was budget-skipped, but window present still ran every frame and averaged 58.568 ms.
- A naive budget skip for window present protected VR but starved the desktop swapchain before it had a valid image, leaving a red clear in MCP screenshots. This was a regression and has been reverted.
- A 30 Hz desktop-editor cap reduced pressure by making the desktop output worse. This was a non-solution and has been reverted.
- The real frame-loop coupling is in the render/collect fence:
  - OpenXR rendering runs from `XRWindow.RenderViewportsCallback`.
  - The collect-visible thread can collect the next frame while render is active, but it could not publish/swap the next command buffers until the whole desktop `Window.DoRender()` returned.
  - On OpenGL, `Window.DoRender()` can still block in the native desktop backbuffer swap after the engine render callback has finished issuing render commands.
  - That means desktop present can age the OpenXR frame and delay the next VR collect/swap even when the VR GPU pipeline itself is around 5 ms.
- 2026-07-06 frame-dump follow-up after the desktop red regression was fixed:
  - Validation run root: `Build/_AgentValidation/20260706-1405-openxr-framerate-viewport-dirty-removed/`.
  - Signature-diff run root: `Build/_AgentValidation/20260706-1410-openxr-framerate-signature-diff/`.
  - The GPU dumps still show the render pipelines are not the frame-rate bottleneck:
    - Desktop `DefaultRenderPipeline#28`: p50 about 2.2 ms, p95 about 2.4 ms.
    - Single-pass stereo VR `DefaultRenderPipeline#36`: p50 about 4.9-5.0 ms, p95 about 5.3 ms.
    - UI and shadow GPU pipeline time were negligible in the sampled dumps.
  - The CPU/render-loop stream is still severely stalled:
    - `frame_output_whole_frame_ms` stayed around p50 148-158 ms and p95 236-255 ms in the follow-up runs.
    - `collect_wait_for_render_ms` stayed around p50 144 ms and p95 250 ms in the 14:02 run.
    - Desktop `Window present` / `XRWindow.Renderer.RenderWindow` samples remained around 80-145 ms, while raw `vulkan_frame_present_ms` was only about 0.06-0.2 ms.
    - OpenXR render submit remained about 20-37 ms in sampled frames, but `xrEndFrame` itself was about 0.09-0.13 ms.
    - `vulkan_frame_gpu_command_buffer_ms` was only about 4-5 ms, matching the VR GPU dump.
  - Removing indexed viewport/scissor changes from the command-buffer dirty path eliminated the previous steady dirty reasons (`SetIndexedViewportScissors` / `ClearIndexedViewportScissors`), but cached primary reuse still did not happen:
    - MCP render stats reported `clean_reuse_count=0`.
    - `vulkan_command_buffer_dirty_summary=forced:variant:variant eviction`.
    - `vulkan_record_command_buffer_allocated_bytes` stayed around 24-28 MB per frame.
  - With `XRE_VULKAN_FRAMEOP_SIGNATURE_DIFF=1` and primary reuse enabled, `log_vulkan.log` showed repeated variant evictions from `MeshDrawOp.program.samplerUnits`:
    - The sampler unit shape was stable (`count=29`, same main keys such as `[0,1,2,6,7,8]`), but the hashed sampler binding signature changed every frame.
    - This strongly points at mutable per-frame render-target or pipeline-resource texture descriptors being included in the primary command-buffer structural signature.
  - Current conclusion: both VR and desktop framerate are being crushed by CPU-side frame-loop synchronization and command-buffer recording/cache churn, not by the GPU cost of the desktop or VR default render pipelines.
- 2026-07-06 run-issue follow-up from session `xrengine_2026-07-06_17-26-34_pid3412`:
  - Startup descriptor heap probing called buffer allocation diagnostics before the Vulkan memory allocator was initialized. The fix now reports descriptor heap fallback as a reason instead of throwing `InvalidOperationException`.
  - Grouped shadow atlas completion read member ranges from a double-buffered render plan that could be republished while render still held spans into it. The fix serializes published/pending plan access and validates grouped member ranges before completion.
  - The repeated `VulkanOutOfMemoryException` reports were not tracked texture VRAM pressure. The failing run reached `activeVkAllocations=4873` and `allocatorBytes=25201798480`, while tracked VRAM was still far below budget. Thousands of tiny live host-visible uniform buffers were the dominant allocator pressure.
  - Mesh renderer engine/auto uniform buffers now use one persistently mapped buffer per uniform block, sliced by aligned frame/draw-slot offsets, instead of one Vulkan allocation per frame/draw-slot entry.
  - Texture descriptor readiness and descriptor-resource fingerprinting now use non-throwing descriptor snapshot probes. Failed physical-image allocation is logged as descriptor-not-ready for that pass instead of throwing through primary command recording.
  - A short validation run after the change (`xrengine_2026-07-06_17-43-14_pid29528`) had no hits for `ArgumentOutOfRangeException`, `VulkanOutOfMemoryException`, `Memory allocator not initialized`, or render exception patterns. Its allocator peak was `activeVkAllocations=327` and `allocatorBytes=3217777152`.
  - Remaining short-run stalls moved to startup/editor work, especially `EditorUnitTests.UserInterface.RefreshVRStereoPreviewOverlay > XRMesh Constructor > InitMeshBuffers`, rather than the earlier exception-heavy primary command recording path.
- 2026-07-06 late run-issue follow-up from sessions `xrengine_2026-07-06_22-18-57_pid7276` through `xrengine_2026-07-06_22-45-41_pid37504`:
  - A startup `InvalidOperationException` came from `VisualScene3D.ApplyRenderDispatchPreference` enumerating renderables while another thread modified the collection. `VisualScene3D` now serializes renderable mutation/enumeration and snapshots renderables for public enumeration.
  - The remaining Vulkan device-loss signature initially occurred while waiting for a one-shot staging upload fence for a tiny 48-byte `Position` buffer. Small static buffers now use host-visible coherent backing unless they are large enough to justify device-local staging, so tiny startup buffers no longer add blocking transfer submits.
  - After that, device loss moved to the per-frame acquire semaphore bridge submit. The normal frame path now waits on the acquired binary semaphore directly in the real graphics submit and only emits an acquire bridge if a frame aborts after acquisition but before draw submit.
  - Validation session `xrengine_2026-07-06_22-39-30_pid32452` had no `InvalidOperationException`, `ArgumentOutOfRangeException`, `VulkanOutOfMemoryException`, or Vulkan device-loss log hits.
  - The next CPU hot path was `RefreshVRStereoPreviewOverlay > VkRenderProgram.OnLinkRequested`. Vulkan does not need eager separable material shader-pipeline programs for normal combined mesh rendering, so `XRMaterial` now skips eager shader-pipeline program creation when the active backend is Vulkan.
  - Validation session `xrengine_2026-07-06_22-43-36_pid37228` removed `VkRenderProgram.OnLinkRequested` from the render-stall hot-path counts, with no startup exception or device-loss hits.
  - The VR stereo preview overlay now has a no-op binding fast path for stable preview textures, array mode, and UV orientation. Session `xrengine_2026-07-06_22-45-41_pid37504` remained clean on exceptions/device loss; remaining preview attribution appears to be stall sampling and/or active OpenXR preview copy/descriptor publication rather than shader relinking.
- 2026-07-07 run-issue follow-up from sessions `xrengine_2026-07-07_00-37-54_pid29232` through `xrengine_2026-07-07_01-05-01_pid3412`:
  - Resource-planner state is now reset/restored around frame-op context switches so stale active planner state cannot leak across desktop/OpenXR recording scopes. This removed the prior dirty-primary abort/device-loss path.
  - Vulkan command-buffer debug labels are now opt-in with `XRE_VULKAN_COMMAND_BUFFER_LABELS=1`. Object names still use debug utils when validation is active, but per-command labels no longer allocate/marshal strings on the hot path by default.
  - Uniform draw-slot growth now dirties legacy mesh-state command buffers only; command-chain/OpenXR external paths can refresh per-frame uniform/descriptor data without broad primary invalidation.
  - Detailed recording profiling showed the remaining main-loop cost moved to mesh draw recording, first under `Vulkan.MeshDraw.Prepare`.
  - Captured mesh draw preparation now has a stable-state fast path, vertex input state is cached behind a dirty flag, and successful prepare no longer builds layout debug strings unless `XRE_VULKAN_RECORDING_DIAG=1`.
  - Latest detailed run shifted the promoted hot leaf from `Vulkan.MeshDraw.Prepare` to `Vulkan.MeshDraw.BindDescriptors`; the next target is descriptor binding/fingerprint/update work, not pass transitions or vertex input rebuilds.
- 2026-07-07 desktop magenta/black flicker regression follow-up from session `xrengine_2026-07-07_04-39-15_pid12204`:
  - MCP viewport screenshots and render-pipeline texture readbacks can be false-black in this Vulkan/OpenXR path while the native desktop window is visibly rendering. For this regression, native window screen-copy capture and OpenXR eye preview capture are the trustworthy validation sources.
  - `PrintWindow` can also return a white client-area artifact for the hardware-accelerated editor window after focus/input probing, so it is not reliable for final visual validation unless cross-checked against screen copy.
  - The independent desktop command-chain path was the immediate cause of the desktop magenta/black flicker after the command-chain fast-path changes. The desktop window now bypasses command chains by default while OpenXR is active in `FullIndependentRender`; the external OpenXR swapchain path still uses command chains.
  - Desktop command chains for this mode are still available behind explicit opt-in `XRE_VULKAN_COMMAND_CHAINS_ALLOW_INDEPENDENT_DESKTOP=1` for future root-cause isolation.
  - The OpenXR command-chain cache now supports out-of-range external swapchain image keys without clamping them into the indexed desktop cache. This avoids cache pollution and `ArgumentOutOfRangeException` paths for synthetic/external OpenXR image indices.
  - Held-key editor camera movement had a stale render-matrix publish gap in the Vulkan/OpenXR windowed editor path. The editor flying camera now immediately publishes its render matrix only when Vulkan, OpenXR, VR, and `RenderWindowsWhileInVR` are all active, reducing the "renders resume after key release" starvation symptom without changing the normal editor camera path.
  - MCP request tasks now observe fire-and-forget handler failures and suppress expected client disconnects, removing the startup `HttpListenerException`/unobserved-task noise seen during readiness probes.
- 2026-07-07 culling/Monado window follow-up:
  - The inverted mesh culling in the desktop view was caused by a viewport-height-based Vulkan front-face flip added during the command-chain work. The engine already resolves Vulkan/OpenXR winding through render state and projection conventions, so that extra flip double-inverted normal mesh winding. The fix removes the viewport-corrected front-face adjustment.
  - The requested Monado window is the patched simulated-HMD VR eye preview window, not `monado-gui.exe`. The temporary `monado-gui.exe` launcher attempt was rejected and removed; the remaining investigation is why the service/compositor preview window is not appearing with the custom title edits.
  - The desired preview is the `monado-service.exe` windowed compositor target. It appears once the OpenXR session creates the Monado compositor and uses the custom title metadata (`Monado | preset ... internal eye ... preview eye ...`). A service process with no active OpenXR session can have no main window, so process existence alone is not evidence that the preview is missing.
  - `XRT_WINDOW_PEEK=both` was tested as a possible fix and rejected. It enables Monado's separate peek target, not the normal Windows compositor preview, and in the local Vulkan run it caused Monado `VK_ERROR_DEVICE_LOST`/descriptor-pool errors followed by service restart.

## Attempts

1. Baseline OpenXR + Monado profiling run.
   - Captured CPU frame dump, GPU pipeline dumps, MCP screenshots, and render profiler stream.
   - Confirmed both desktop and VR pipelines were active in `FullIndependentRender`.

2. Rejected desktop throttling attempt.
   - Tried making desktop-facing outputs budget-skippable and setting the VR desktop editor target rate to 30 Hz.
   - Result: it hid the stall by lowering desktop quality/cadence instead of fixing the frame loop.
   - Status: reverted.

3. Rejected desktop-present pacing attempt.
   - Tried pacing/skipping `XRWindow.Renderer.RenderWindow` present while VR was active.
   - Result: the first version left the desktop red because the swapchain was not guaranteed to have a valid presented image. The seeded/frame-count variant kept the image valid but was still a throttle and left large present stalls.
   - Status: reverted.

4. Frame-loop publish decoupling.
   - Added `EngineTimer.MarkRenderFrameReadyForCollect()` as a one-shot signal for the active render dispatch.
   - Exposed it through `IRuntimeRenderingHostServices.MarkRenderFrameReadyForCollect(window)`.
   - `XRWindow.RenderCallback` now signals after scene/VR/desktop command consumption and render-thread jobs, but before the native window layer can continue to a blocking OpenGL backbuffer swap.
   - The engine host only honors the early signal when exactly one window is tick-linked; multi-window rendering falls back to the existing end-of-dispatch signal.
   - Expected effect: desktop and VR remain independently paced by their real work; desktop present can block without preventing collect-visible from publishing the next VR frame.

5. Native-window desktop red/black regression fix.
   - Bound Vulkan `VPRC_BindFBO` writes through draw-framebuffer state so shadow/offscreen passes no longer leave write binding ambiguous.
   - Kept all shadow mesh passes inside the shadow output FBO bind scope.
   - Split Vulkan swapchain writer accounting into scene writers and UI overlay writers.
   - Changed overlay swapchain passes to load the previously presented image instead of clearing when only the overlay writes.
   - Restored single-pass VR desktop viewport configuration by calling `ConfigureDesktopViewportForVrWindow(window)` from `InitSinglePass`.
   - Disabled VR over-budget auto-skip for `FullIndependentRender` `DesktopEditor` scene output so the editor viewport has its own cadence instead of starving behind the VR budget hold path.

6. Indexed viewport/scissor cache-dirty reduction.
   - Changed `VulkanStateTracker.SetIndexedViewportScissors` / `ClearIndexedViewportScissors` to report whether state actually changed.
   - Removed unconditional `MarkCommandBuffersDirty()` calls from `VulkanRenderer.SetIndexedViewportScissors` and `ClearIndexedViewportScissors`.
   - Result: steady viewport/scissor dirty reasons disappeared from render stats, but primary reuse was still blocked by variant eviction.

7. Primary command-buffer signature-diff run.
   - Enabled `XRE_VULKAN_FRAMEOP_SIGNATURE_DIFF=1`, `XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE=1`, and `XRE_OPENXR_VULKAN_PRIMARY_REUSE=1`.
   - Captured the follow-up session under `Build/_AgentValidation/20260706-1410-openxr-framerate-signature-diff/`.
   - Result: repeated variant cache evictions were traced to `MeshDrawOp.program.samplerUnits` signature churn, even though the sampler binding shape stayed stable.

8. Vulkan run-issue and allocator-pressure fix.
   - Guarded descriptor heap storage initialization until the Vulkan allocator exists.
   - Serialized shadow atlas render-plan publish/write/read and added non-throwing grouped member-range validation.
   - Converted physical image descriptor probes to the existing `TryEnsureAllocated` path and snapshot-based descriptor fingerprinting.
   - Coalesced per-renderer uniform buffer frame/draw slots into aligned offsets inside one mapped buffer per uniform block.
   - Result: targeted exceptions disappeared in the short run, and allocator pressure dropped sharply in the available log comparison.

9. Vulkan startup/device-loss follow-up and preview hot-path cleanup.
   - Serialized `VisualScene3D` renderable collection access to remove the startup collection-modified exception.
   - Routed tiny static buffers to host-visible coherent memory and made transfer-copy failures non-successful uploads instead of stamping buffers ready.
   - Removed the normal zero-command-buffer acquire semaphore bridge submit from the frame loop; the acquired binary semaphore is consumed by the real graphics submit, with an abort-only bridge fallback.
   - Reused shared UI quad meshes for `UIMaterialComponent` and made OpenXR Vulkan preview material/program handling avoid eager Vulkan shader-pipeline links.
   - Added a no-op fast path for steady-state VR stereo preview texture bindings.
   - Result: three 45-second editor validation runs after the synchronization fix had no startup exception, Vulkan OOM, out-of-range, or device-loss log hits. The program-link hot path disappeared from the profiler after backend-aware material shader-pipeline gating.

10. Primary-recording hot-path cleanup after startup was stable.
   - Fixed frame-op resource-planner activation so recording context switches reset active planner state before switching/restoring cached frame-op states.
   - Gated Vulkan command-buffer debug labels behind `XRE_VULKAN_COMMAND_BUFFER_LABELS=1` while keeping debug object names available with validation/debug-utils.
   - Changed uniform draw-slot capacity growth to call `MarkCommandBuffersDirtyForLegacyMeshState()` instead of broad dirtying.
   - Added detailed `Vulkan.MeshDraw.*` scopes under `XRE_VULKAN_RECORDING_PROFILE_DETAIL=1`.
   - Cached Vulkan vertex input state until buffers/program identity change and added a captured-program prepare fast path for stable draw packets.
   - Result: startup/runtime failure signatures stayed at zero; detailed profiling now points at descriptor binding as the next CPU hot path.

11. Desktop/OpenXR flicker and held-key starvation regression fix.
   - Added external command-chain caches for OpenXR swapchain image keys that are not part of the renderer's indexed desktop frame-data cache.
   - Bypassed command chains for the independent desktop mirror while OpenXR full-independent desktop rendering is active, unless `XRE_VULKAN_COMMAND_CHAINS_ALLOW_INDEPENDENT_DESKTOP=1` is set.
   - Kept command chains enabled for external OpenXR swapchain targets so the VR fast path remains active.
   - Added scoped immediate render-matrix publication for held-key editor flying camera movement only in Vulkan/OpenXR window rendering.
   - Observed MCP request tasks and suppressed expected client disconnect exceptions from readiness probes.
   - Result: native desktop capture shows the Sponza scene under ImGui instead of magenta/black, OpenXR eye preview capture is non-black, and the latest run has no matching startup/runtime exception signatures.

12. Culling and Monado preview-window regression fix.
   - Removed the negative-viewport front-face correction from Vulkan mesh draw snapshots.
   - Rejected the `monado-gui.exe` launcher approach after clarifying the desired window is the custom simulated-HMD eye preview from the patched Monado service/compositor path.
   - Rejected `XRT_WINDOW_PEEK=both` after it created Monado's separate peek target and triggered local Vulkan device-loss/descriptor-pool errors.
   - Result: culling fix is kept; the correct Monado preview is the `monado-service.exe` windowed compositor target and it is visible in the latest editor validation run with the custom title metadata.

## Validation

- Build:
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -p:Platform=AnyCPU /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary`
  - Passed. Existing Magick.NET advisory warnings only.
- Focused test:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VrViewRenderModeContractTests.SourceContracts_FrameOutputPacingManifestAndMirrorPolicy"`
  - Passed before the rejected present-throttle revert.
- Focused test after frame-loop publish decoupling:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VrViewRenderModeContractTests" /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary`
  - Passed: 28 passed.
- Last MCP screenshot from the reverted present-cadence experiment:
  - `Build/_AgentValidation/20260706-103608-openxr-monado-framerate/mcp-captures/Screenshot_20260706_110543.png`
  - Shows a valid Sponza desktop frame, not the red clear from the naive present-skip attempt. This capture is not evidence for the final frame-loop publish-decoupling change.
- Profiler artifacts from the investigation:
  - Render stream: `Build/_AgentValidation/20260706-103608-openxr-monado-framerate/logs/profiler-render-stats-after-frame-count-present-cadence.ndjson`
  - Comparison: `Build/_AgentValidation/20260706-103608-openxr-monado-framerate/reports/frame-stream-comparison-frame-count-final.json`
  - VR GPU dump: `Build/_AgentValidation/20260706-103608-openxr-monado-framerate/logs/profiler-gpu-pipeline-defaultrenderpipeline-36-2026-07-06-11-05-44-685-16c3fc73.log`
  - Desktop GPU dump: `Build/_AgentValidation/20260706-103608-openxr-monado-framerate/logs/profiler-gpu-pipeline-defaultrenderpipeline-28-2026-07-06-11-05-44-685-485add5b.log`
- Native-window desktop regression validation:
  - Reproduced red desktop under ImGui with `PrintWindow`: `Build/_AgentValidation/20260706-1125-openxr-monado-device-lost/mcp-captures/NativeWindow_PrintWindow_20260706_130739.png`.
  - Intermediate overlay-preserve fix removed red but left black because only overlay writers were active: `Build/_AgentValidation/20260706-1125-openxr-monado-device-lost/mcp-captures/NativeWindow_PrintWindow_20260706_131711.png`; window title showed `scene=0 overlay=2`.
  - Final native capture: `Build/_AgentValidation/20260706-1125-openxr-monado-device-lost/mcp-captures/NativeWindow_PrintWindow_20260706_132259.png`; desktop scene is visible under ImGui and the title shows `DefaultRenderPipeline` with `scene=2 overlay=2`.
  - Focused tests passed:
    - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanDynamicRenderingMigrationTests.VulkanFramebuffer_WriteBindingTracksDrawFramebufferState|FullyQualifiedName~VulkanDynamicRenderingMigrationTests.VulkanSwapchainOverlayPasses_LoadPresentedImageInsteadOfClearing|FullyQualifiedName~CascadedShadowDefaultsAndForwardShaderTests.ShadowRenderPipeline|FullyQualifiedName~OpenXrTimingPipelineContractTests.RuntimeVrDesktopView_DoesNotReuseEyeCommandsOrEditorImGuiWhenDesktopEditingDisabled|FullyQualifiedName~VrViewRenderModeContractTests.SourceContracts_SurfaceViewModeAndFoveationSettings" /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary`
    - Result: 6 passed. Existing Magick.NET advisory warnings only.
  - Fresh log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-06_13-22-25_pid48088`.
    - No `VK_ERROR_DEVICE_LOST` or render exception hits were found.
    - Steady writer logs show `DefaultRenderPipeline` and `UserInterfaceRenderPipeline` both writing the swapchain.
- Latest framerate/frame-dump validation:
  - Build:
    - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -p:Platform=AnyCPU /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary`
    - Passed. Existing Magick.NET advisory warnings only.
  - Focused test:
    - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests.VulkanIndexedViewportScissor_StateChangesDoNotForceDirtyCachedPrimaries" /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary`
    - Passed: 1 passed.
  - GPU/CPU dump artifacts:
    - Viewport/scissor dirty removal run: `Build/_AgentValidation/20260706-1405-openxr-framerate-viewport-dirty-removed/logs/`.
    - Signature-diff run: `Build/_AgentValidation/20260706-1410-openxr-framerate-signature-diff/logs/`.
- Run-issue fix validation:
  - Build:
    - `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
    - Passed. Existing Magick.NET advisory warnings only.
  - Editor build:
    - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
    - Passed. Existing Magick.NET advisory warnings only.
  - Short editor run:
    - Started `Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467` and force-stopped it after 20 seconds.
    - Session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-06_17-43-14_pid29528`.
    - No matching `ArgumentOutOfRangeException`, `VulkanOutOfMemoryException`, `InvalidOperationException`, `Memory allocator not initialized`, or render exception log hits.
    - Allocator comparison: old `xrengine_2026-07-06_17-26-34_pid3412` peaked at `activeVkAllocations=4873`, `allocatorBytes=25201798480`; short validation peaked at `activeVkAllocations=327`, `allocatorBytes=3217777152`.
    - Scratch evidence copied to `Build/_AgentValidation/20260706-173011-openxr-vulkan-hotpath/logs/`.
- Late run-issue validation:
  - Build:
    - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
    - Passed. Existing Magick.NET advisory warnings only.
  - Editor run after acquire-submit synchronization change:
    - Session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-06_22-39-30_pid32452`.
    - No matching `InvalidOperationException`, `ArgumentOutOfRangeException`, `VulkanOutOfMemoryException`, `DeviceLost`, `ErrorDeviceLost`, validation, or queue-submit failure hits.
    - `VkRenderProgram.OnLinkRequested` remained the top preview-related CPU hot path before the material pipeline fix.
  - Editor run after Vulkan material shader-pipeline gating:
    - Session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-06_22-43-36_pid37228`.
    - No matching startup exception/device-loss hits.
    - `VkRenderProgram.OnLinkRequested` no longer appeared in the render-stall hot-path counts.
  - Editor run after VR stereo preview no-op binding fast path:
    - Session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-06_22-45-41_pid37504`.
    - No matching startup exception/device-loss hits.
    - Remaining profiler attribution included `RefreshVRStereoPreviewOverlay`, `XRWindow.Timer.DoEvents`, texture streaming, and ImGui overlay recording; the shader-link leaf stayed gone.
  - Focused/broad source-contract test attempt:
    - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanP0ValidationTests|FullyQualifiedName~VulkanDynamicRenderingMigrationTests" --no-build`
    - Result: 107 passed, 14 failed. The failures were existing source-string contract drift in OpenXR/Vulkan contract tests (for example Monado installer URL, old frame-output snippets, old viewport/scissor/source-file expectations), not runtime failures from the latest editor sessions.
- 2026-07-07 startup/hot-path validation:
  - Build:
    - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal -clp:ErrorsOnly`
    - Passed: 216 existing warnings, 0 errors.
  - Focused test:
    - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests.VulkanCommandChains_DoNotBroadDirtyForRepeatedSkippedMeshPreparation" -v:minimal -clp:ErrorsOnly`
    - Passed: 1 passed.
  - Clean editor sessions:
    - `xrengine_2026-07-07_00-37-54_pid29232`: resource-planner state isolation validation.
    - `xrengine_2026-07-07_00-44-29_pid10800`: command-buffer label gating validation.
    - `xrengine_2026-07-07_00-51-34_pid38148`: uniform-slot legacy dirty validation.
    - `xrengine_2026-07-07_01-05-01_pid3412`: captured-prepare fast-path validation.
  - The latest session had zero hits for `Exception thrown`, `InvalidOperationException`, `ArgumentOutOfRangeException`, `VulkanOutOfMemoryException`, device loss, queue-submit failure, dirty-before-submit, invalid layout, and tracked Vulkan VUID signatures.
  - Detail profiling before the captured-prepare fast path promoted `Vulkan.MeshDraw.Prepare` hundreds of times; after the fast path, the latest run promoted `Vulkan.MeshDraw.BindDescriptors` 769 times, `NotifyUniforms` 39 times, `EnsurePipeline` 18 times, and `Prepare` 14 times.
- 2026-07-07 desktop/OpenXR flicker validation:
  - Build:
    - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal -clp:ErrorsOnly`
    - Passed: 216 existing warnings, 0 errors.
  - Editor run:
    - Session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-07_04-39-15_pid12204`.
    - Launched with `--unit-testing --mcp --mcp-allow-all --mcp-port 5467`, `XRE_VULKAN_RECORDING_DIAG=1`, and command-chain override environment variables unset/disabled.
  - Native desktop capture:
    - `Build/_AgentValidation/20260707-011756-openxr-flicker-empty-publish/mcp-captures/NativeWindow_12204_validation.png`.
    - Shows Sponza visible under ImGui with no magenta overlay or black swapchain frame; title overlay reported scene and overlay writers active with `dropDraw=0` and `dropOps=0`.
  - Held-key desktop capture:
    - `Build/_AgentValidation/20260707-011756-openxr-flicker-empty-publish/mcp-captures/NativeWindow_12204_W_hold_1250ms_screencopy.png`.
    - Shows Sponza still visible during a synthetic held `W` key with no black starvation; title overlay reported `scene=2`, `dropDraw=0`, `dropOps=0`, and desktop around 94 Hz.
  - OpenXR eye preview capture:
    - `Build/_AgentValidation/20260707-011756-openxr-flicker-empty-publish/mcp-captures/OpenXRPreview_LeftEye_20260707_043939.png`.
    - Non-black left-eye preview: 896x1007, average RGB about 0.267, max RGB 1.
  - MCP viewport screenshot caveat:
    - `Build/_AgentValidation/20260707-011756-openxr-flicker-empty-publish/mcp-captures/Screenshot_20260707_043358.png` remained black while the native desktop window was visible. Treat MCP viewport screenshots/render-pipeline texture readbacks as unreliable for this specific Vulkan/OpenXR desktop validation until the capture target mismatch is fixed.
  - Logs:
    - No hits in `editor_bootstrap.log`, `log_vulkan.log`, or `log_rendering.log` for `Exception thrown`, `InvalidOperationException`, `ArgumentOutOfRangeException`, `VulkanOutOfMemoryException`, device loss, Vulkan VUIDs, queue-submit failure, dirty-before-submit, invalid layout, `HttpListener`, or unobserved-task signatures.
  - MCP profiler snapshots after settling:
    - `record_command_buffer_ms` about 0.12-0.13 ms.
    - `submit_ms` about 0.046-0.048 ms.
    - Dropped operations/draws and Vulkan validation errors reported as zero.
- 2026-07-07 culling/Monado preview-window validation:
  - Build:
    - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore -v:minimal -clp:ErrorsOnly`
    - Passed: 217 existing warnings, 0 errors.
  - Focused tests:
    - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests.OpenXrRuntimeRecovery_RestartsHostServiceAndKeepsStrongestLossReason|FullyQualifiedName~OpenXrTimingPipelineContractTests.OpenXrMonadoTooling_DoesNotMutateMachineRuntimeAndStagesLoader|FullyQualifiedName~VrViewRenderModeContractTests.SourceContracts_SurfaceViewModeAndFoveationSettings" -v:minimal -clp:ErrorsOnly`
    - Passed: 2 matched tests passed. The initial attempt exposed two stale source-contract/test-stub issues, which were updated.
  - Local process check:
    - An earlier process-only check saw `monado-service.exe` with no main window after the editor/OpenXR session had already disconnected. That is not valid evidence that the preview is missing during an active session.
    - The visible `monado-gui.exe` window was the wrong target and is not considered valid validation for the requested VR eye preview window.
  - Rejected peek-target validation:
    - Session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-07_09-27-44_pid22408`.
    - `XRT_WINDOW_PEEK=both` created the extra `comp_window_peek` path but Monado logged `VK_ERROR_DEVICE_LOST`, `VK_ERROR_OUT_OF_POOL_MEMORY`, and restarted the service.
  - Correct preview validation:
    - Session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-07_09-30-14_pid22464`.
    - `monado-service.exe` pid `5032` exposed a visible top-level window titled `Monado | preset RuntimeRecommended @ 1x | internal eye 896x1007 | window 1264x681 | preview eye 605x681 0.68x`.
    - Screenshot: `Build/_AgentValidation/20260707-0925-culling-monado-gui/mcp-captures/MonadoPreviewWindow_20260707_0930.png`.
    - Logs copied to `Build/_AgentValidation/20260707-0925-culling-monado-gui/logs/`.
    - No hits in the latest session logs for `Exception thrown`, `InvalidOperationException`, `ArgumentOutOfRangeException`, `VulkanOutOfMemoryException`, Vulkan device loss, `VK_ERROR_OUT_OF_POOL_MEMORY`, validation VUIDs, queue-submit failure, dirty-before-submit, `HttpListener`, or unobserved task signatures.

## Remaining Work

- Re-run the full live OpenXR + Monado VR editor loop with the user's headset/runtime and compare the exception count, allocator peaks, primary command-buffer reuse, collect-visible wait, and OpenXR submit timing against the 17:26 failing run.
- Root-cause and safely re-enable independent desktop command chains behind `XRE_VULKAN_COMMAND_CHAINS_ALLOW_INDEPENDENT_DESKTOP=1`. Current default keeps the desktop on the known-correct legacy recording path while preserving OpenXR external command chains.
- Fix the MCP Vulkan viewport/render-pipeline capture mismatch that returns false-black desktop captures while the native window is rendering correctly.
- Continue fixing primary command-buffer signature churn by separating structural binding shape from mutable per-frame descriptor identity. Known frame-source and pipeline-resource textures should update descriptor sets without creating a new primary command-buffer variant every frame.
- Reduce `Vulkan.MeshDraw.BindDescriptors`. The current evidence says repeated descriptor binding/fingerprint/update work is the dominant CPU leaf after captured prepare reuse; avoid unsafe no-fingerprint shortcuts for texture-backed materials unless descriptor resource changes are explicitly versioned or dirty-tracked.
- Continue reducing VR stereo preview CPU cost. Mesh construction and shader-linking are no longer the hot leaves; the next target is splitting preview texture copy/descriptor publication timing from profiler stall sampling so the remaining `RefreshVRStereoPreviewOverlay` attribution can be separated into real UI work versus work that merely samples while the hook is active.
- Add instrumentation around OpenXR Vulkan submit to split queue submit time, fence wait time, acquire/release swapchain image time, and any desktop-queue backlog.
- Re-run the live OpenXR + Monado editor loop after primary command-buffer reuse works and compare collect-visible wait, OpenXR predicted-to-late pose delta, OpenXR submit time, desktop present timing, and per-frame allocation totals against the current dumps.
- If desktop present still dominates after command-buffer churn is fixed, the next deeper fix is a true output scheduler: OpenXR render/submit and desktop window present should have separate clocks, with shared scene data published through explicit frame snapshots rather than a single desktop-window render callback owning both outputs.
