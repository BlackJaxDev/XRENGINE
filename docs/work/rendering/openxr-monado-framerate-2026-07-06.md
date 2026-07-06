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

## Remaining Work

- Fix primary command-buffer signature churn by separating structural binding shape from mutable per-frame descriptor identity. Known frame-source and pipeline-resource textures should update descriptor sets without creating a new primary command-buffer variant every frame.
- Add instrumentation around OpenXR Vulkan submit to split queue submit time, fence wait time, acquire/release swapchain image time, and any desktop-queue backlog.
- Re-run the live OpenXR + Monado editor loop after primary command-buffer reuse works and compare collect-visible wait, OpenXR predicted-to-late pose delta, OpenXR submit time, desktop present timing, and per-frame allocation totals against the current dumps.
- If desktop present still dominates after command-buffer churn is fixed, the next deeper fix is a true output scheduler: OpenXR render/submit and desktop window present should have separate clocks, with shared scene data published through explicit frame snapshots rather than a single desktop-window render callback owning both outputs.
