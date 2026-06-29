# OpenXR Monado Vulkan 120 Hz Performance Iteration

Status: active

Run root: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz`

## Goal

Get Monado OpenXR + Vulkan rendering consistently above 120 Hz for both eyes and the editor view using the default render pipeline. Do not disable render features to hit the target. Use CPU/GPU frame logs, Vulkan diagnostics, MCP captures, and RenderDoc if needed to identify and optimize bottlenecks.

## Current Focus

1. Keep the accepted frame-source descriptor mutability fixes and do not reintroduce the allocator-identity regression.
2. Optimize the remaining CPU recording bottleneck:
   - current warm guard checkpoint: p50 19.99 ms total / 19.23 ms command recording
   - GPU default pipeline is near budget: warm p50 about 7.11 ms, p95 about 7.98 ms after first 50 captured frames
   - OpenXR eye recording still serializes through two primary `MainOpLoop` passes of about 6 ms each in the sampled CPU dump
   - primary command-buffer reuse is still effectively 0 because schedule chains do not own executable secondary command buffers yet
3. Parallel eye recording remains the preferred path; the latest `SinglePassStereoVR=true` comparison is slower.
4. Fix regressions before continuing performance work.

## Non-Goals

- Do not hide failures behind CPU-direct rendering.
- Do not disable editor rendering, stereo preview, shadows, post-processing, sky, or the default render pipeline to improve the number.
- Do not treat shutdown-only Vulkan validation warnings as steady-state render regressions.

## Baseline State

- Existing local working copy already has unrelated untracked submodule/dependency directories:
  - `Build/Submodules/OscCore-NET9`
  - `Build/Dependencies/vcpkg/`
  - `Build/Submodules/Flyleaf/`
  - `Build/Submodules/MagicPhysX/`
  - `Build/Submodules/monado/`
- Previous recovery doc reports a no-desktop/no-preview profile at about 172.3 fps, but that profile disabled work this request needs to keep.
- Previous remaining hotspots were `OpenXR.Vulkan.SubmitFenceWait` and `Vulkan.RecordPrimary.MainOpLoop`.

## Iteration Log

### 2026-06-27 Initial Scope

- Created bounded validation run root after pruning the oldest immediate run folder under `Build/_AgentValidation`.
- Read the prior OpenXR/Monado/Vulkan recovery and performance notes.
- Current hypothesis: the full requested profile is bottlenecked by redundant per-view command recording/submission waits and main primary recording work, not by scene correctness.

Next action: build the editor, run the current full profile with detailed frame logs, inspect the logs, then choose the first optimization target.

### 2026-06-28 Baseline A - Full Parallel Profile, Diagnostic-Heavy

- Built `XREngine.Editor` successfully with existing NuGet advisory warnings only.
- Launched Unit Testing World with:
  - Monado OpenXR runtime from `Build/Deps/Monado/openxr_monado.json`
  - Vulkan backend
  - stereo eye preview enabled
  - desktop editing enabled
  - desktop window rendering while in VR enabled
  - `SinglePassStereoVR=false`
  - command chains and parallel packet build enabled
  - GPU timestamp dense mode enabled for diagnostic capture
- MCP evidence captured:
  - desktop viewport: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/mcp-captures/Screenshot_20260628_000633.png`
  - left eye: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/mcp-captures/OpenXRPreview_LeftEye_20260628_000633.png`
  - right eye: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/mcp-captures/OpenXRPreview_RightEye_20260628_000634.png`
  - CPU dump: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_00-06-17_pid6072/profiler-cpu-frame-2026-06-28-00-06-33-392-4b0a15d0.log`
  - GPU dump: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_00-06-17_pid6072/profiler-gpu-pipeline-defaultrenderpipeline-28-2026-06-28-00-06-33-405-999d4bda.log`
  - render stats: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_00-06-17_pid6072/profiler-render-stats.ndjson`
- Visual result: desktop, left-eye, and right-eye captures are nonblank and finite. No black-eye regression observed.
- Measurement caveat: this pass ran with Vulkan validation layers enabled and dense GPU timestamps on. Treat absolute frame time as diagnostic-heavy, not final performance.
- Baseline A steady-state after 60s:
  - active stereo mode: `vr-desktop-mirror`
  - median render dispatch: 63.6 ms
  - median Vulkan frame total: 30.4 ms
  - median Vulkan command-buffer recording: 29.4 ms
  - median Vulkan GPU command-buffer time: 11.9 ms
  - median GPU pipeline frame timing: 54.8 ms
  - primary command-buffer reuse: 0
  - primary command buffers recorded: median 2/frame
  - record allocation: median 3.4 MB/frame
- Finding: the main bottleneck is CPU-side Vulkan command recording and per-frame command-buffer invalidation, not fence wait (`wait_fence_ms` median was ~0.03 ms).
- Finding: command-chain parallelism is not helping steady state in this pass. `vulkan_command_chains_scheduled` is usually 0 for the desktop mirror frame, and OpenXR primary reuse is disabled unless `XRE_OPENXR_VULKAN_PRIMARY_REUSE=1`.
- Finding: command buffers are marked dirty continuously during this run. Early causes are render resource changes and pipeline compiles; later causes are imported texture publications for Sponza. Need a cleaner warm-cache pass before changing code.

Next action: run Baseline B with validation layers off and dense timestamp mode off while keeping rendering features enabled. Then compare parallel eye recording with `SinglePassStereoVR=true`.

### 2026-06-28 Baseline B - Warm Parallel Profile, Validation Off

- Launched the same full feature profile as Baseline A, but with Vulkan validation disabled and dense GPU timestamp mode off.
- Let the run warm longer before collecting steady-state stats.
- MCP/engine evidence:
  - render stats: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_00-11-54_pid18828/profiler-render-stats.ndjson`
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/baseline-warm-parallel-ndjson-summary.txt`
  - CPU dump: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_00-11-54_pid18828/profiler-cpu-frame-2026-06-28-00-12-30-205-*.log`
  - eye previews: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/mcp-captures/OpenXRPreview_LeftEye_20260628_001229.png`, `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/mcp-captures/OpenXRPreview_RightEye_20260628_001230.png`
- Visual result: eye previews are nonblank and finite. The desktop screenshot from this run was black, but the next run produced a nonblack desktop screenshot and a nonblack final post-process texture, so this is currently tracked as a readback/capture artifact rather than a persistent render regression.
- Baseline B steady-state after 60s:
  - active stereo mode: `vr-desktop-mirror`
  - median render dispatch: 53.6 ms
  - median Vulkan frame total: 22.6 ms
  - median Vulkan command-buffer recording: 22.2 ms
  - median Vulkan GPU command-buffer time: 10.3 ms
  - median GPU pipeline frame timing: 57.5 ms
  - primary command-buffer reuse: 0
  - forced dirty command buffers: median 1/frame
  - record allocation: median 3.6 MB/frame
- Finding: turning off validation and dense timestamps helps, but the frame remains far below the 8.33 ms budget for 120 Hz. CPU command recording is still the main visible bottleneck.
- Finding: `OpenXR.RenderFrame.TryRenderVulkanEyesBatch` is still expensive in the CPU dump, and `OpenXR.Vulkan.SubmitFenceWait` can contribute significant eye-frame time even when per-window Vulkan fence wait is near zero.
- Finding: command-chain parallelism remains inactive in steady state for this desktop mirror path. The frame alternates between eye and desktop samples, but both still report a forced dirty command buffer each frame.

Next action: enable OpenXR primary reuse to see whether the eye path can stop re-recording primaries, then isolate why the desktop/editor view still forces dirty command buffers.

### 2026-06-28 Primary Reuse Experiment

- Launched the full feature profile with `XRE_OPENXR_VULKAN_PRIMARY_REUSE=1`, validation off, and dense timestamps off.
- Attempted to disable GPU pipeline profiling through MCP, but the first preference writes ran before there was an active world and failed with `No active world instance`. The run therefore still had GPU pipeline profiling enabled.
- MCP/engine evidence:
  - render stats: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_00-16-51_pid31120/profiler-render-stats.ndjson`
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/primary-reuse-no-gpuprof-ndjson-summary.txt`
  - desktop viewport: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/mcp-captures/Screenshot_20260628_001730.png`
  - final post-process texture: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/mcp-captures/RenderPipeline_FinalPostProcessOutputTexture_20260628_001733.png`
  - left/right eye previews: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/mcp-captures/OpenXRPreview_LeftEye_20260628_001731.png`, `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/mcp-captures/OpenXRPreview_RightEye_20260628_001732.png`
- Visual result: desktop, eye previews, and final post-process output are nonblank and finite. This clears the Baseline B black screenshot as a likely capture/readback artifact.
- Primary reuse steady-state after 60s:
  - active stereo mode: `vr-desktop-mirror`
  - median render dispatch: 49.1 ms
  - median Vulkan frame total: 22.6 ms
  - median Vulkan command-buffer recording: 22.3 ms
  - median Vulkan GPU command-buffer time: 10.0 ms
  - median GPU pipeline frame timing: 52.4 ms
  - OpenXR primary command buffers reused: median 2/frame on eye samples
  - forced dirty command buffers: median 1/frame
  - record allocation: median 4.2 MB/frame
- Finding: OpenXR eye primary reuse works when the environment flag is enabled. The trace shows cached primary reuse for both eyes.
- Finding: primary reuse improves the eye path but does not solve the editor/desktop view. Latest samples alternate between eye frames with two reused primaries and desktop/editor samples with zero primary reuse, while both still have a forced dirty command buffer.
- Finding: GPU pipeline profiling must be disabled after the active world exists, or by an explicit startup override, before concluding how much profiling overhead remains.
- Finding: the next root-cause target is the desktop/editor command-buffer invalidation path, not eye primary reuse.

Next action: inspect the dirty/reuse conditions around `EnsureCommandBufferRecorded`, run a focused no-capture timing pass with primary reuse enabled and GPU pipeline profiling actually disabled, then compare against `SinglePassStereoVR=true`.

### 2026-06-28 Descriptor Residency And Command-Chain Reuse Pass

- Fixed a texture-streaming churn source where Vulkan dense imported textures could shrink/demote resident size while there was no pressure. This removed repeated Sponza texture publication from the steady-state dirty path.
- Fixed a command-chain scheduler gate that prevented chain scheduling while frame-op resource planner switching was active.
- Fixed command-chain keying/ordinal churn by adding the dynamic-overlay bit to `CommandChainKey`, deriving chain ordinals from source start plus structural signature, and removing descriptor snapshots from structural frame-op signatures.
- Fixed an OpenXR primary-reuse regression that could reuse primaries whose secondary command buffers had been re-recorded or whose descriptor sets had been retired. The reuse signature now includes secondary command-buffer handle/generation and chain state/dirty/resource identity, and primary reuse is gated on reusable chain schedules.
- Fixed mutable frame-source descriptor churn for `SourceTexture` / `SourceTex`: the descriptor key is now descriptor-shape based instead of texture/object/allocator based, and reused frame-data descriptors are refreshed in the current frame slot before recording/reuse.
- Validation:
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests.VulkanCommandChains_DescriptorReuseTracksConcreteImageIdentityAndMutableFrameSources|FullyQualifiedName~VulkanCommandChainDataModelTests"`
  - Known Magick.NET advisory warnings only.
- Evidence:
  - parallel summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/frame-source-shape-key-warm-60-summary.json`
  - single-pass summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/singlepass-shape-key-warm-60-summary.json`
  - parallel desktop capture: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/mcp-captures/Screenshot_20260628_030636.png`
  - parallel final post-process capture: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/mcp-captures/RenderPipeline_FinalPostProcessOutputTexture_20260628_030652.png`
- Parallel-eye result after fixes:
  - median Vulkan frame total: 17.69 ms
  - median Vulkan command-buffer recording: 16.95 ms
  - median Vulkan GPU command-buffer time: 7.98 ms
  - command chains: median 25 recorded, 149 reused, 111 frame-data refreshed
  - descriptor pools: 15 total, no binding failures or skipped draws
  - validation errors: 0
- Single-pass stereo result after fixes:
  - median Vulkan frame total: 22.59 ms
  - median Vulkan command-buffer recording: 21.78 ms
  - median Vulkan GPU command-buffer time: 8.07 ms
  - command chains: median 25 recorded, 149 reused, 113 frame-data refreshed
  - descriptor pools: 15 total, no binding failures or skipped draws
  - validation errors: 0
- Finding: after descriptor key stabilization, parallel-eye recording is faster than single-pass stereo for this scene and current pipeline. Single-pass does not remove the dominant descriptor-generation/frame-data refresh work.
- Finding: the GPU command-buffer median is now roughly at the 120 Hz budget, but CPU recording is still roughly 2x too slow. The next bottleneck is the remaining descriptor-generation refresh hotspot around Sponza materials such as `Material__57` / `vase_round`.
- Visual note: the no-eye-copy timing run reports black OpenXR preview capture textures because the eye-preview copy diagnostic was disabled. This is expected for that timing configuration; a separate visual pass with `XRE_VULKAN_CAPTURE_EYE_OUTPUTS=1` is still needed.
- Visual note: the desktop/final post-process captures are nonblank but over-bright/color-wrong. This may predate the current optimization pass, but it is tracked as a possible visual regression until compared against a known-good capture.

Next action: diagnose why the remaining frame-data-only descriptor generation changes are causing ~25 chains to record and ~110 chains to refresh each frame, then run a dedicated eye-output visual pass.

### 2026-06-28 Regression Rollback And OpenXR Phase Timing

- Tried reclassifying compute dispatch frame ops as frame-data-only so more command chains could refresh instead of record. This was a regression:
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/compute-frame-data-classification-warm-60-summary.json`
  - median Vulkan frame total: 31.41 ms
  - median command-buffer recording: 30.53 ms
  - command chains: median 4 recorded, 166 reused, 148 refreshed
- Reverted compute dispatch classification to dynamic command. Performance remained regressed while descriptor-content refresh was still active:
  - summaries: `dispatch-dynamic-restored-warm-60-summary.json`, `dispatch-dynamic-restored-warm2-60-summary.json`
  - median Vulkan frame total: 31.19-34.69 ms
  - median command-buffer recording: 30.17-33.66 ms
- Rolled back descriptor-content refresh for reusable command chains. This cleared the latest regression and kept validation clean:
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/descriptor-refresh-rolled-back-warm-60-summary.json`
  - median Vulkan frame total: 22.56 ms
  - median command-buffer recording: 21.65 ms
  - median Vulkan GPU command-buffer time: 7.41 ms
  - median GPU pipeline timing: 42.49 ms
  - command chains: median 21 recorded, 149 reused, 131 refreshed
  - validation errors, descriptor binding failures, and skipped draws: 0
- Current samples are bimodal:
  - slower eye group: about 31.47 ms total / 30.46 ms record with 170 scheduled ops, 1 primary recorded, 21 chains recorded, 131 refreshed
  - faster group: about 16.10 ms total / 15.35 ms record with 166 scheduled ops, 3 primaries recorded, 25 chains recorded, 103 refreshed
- Added OpenXR/Vulkan phase profiling scopes and captured a slow eye sample:
  - log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_04-00-05_pid13628`
  - CPU dump: `profiler-cpu-frame-2026-06-28-04-00-23-623-fbe6133b.log`
  - sample signature: scheduled=170, primary=1, chains=21, refreshed=131, record=30.58 ms, total=31.39 ms
- Phase finding from the slow sample:
  - `OpenXR.RenderFrame.TryRenderVulkanEyesBatch`: 28.70 ms
  - `OpenXR.Vulkan.Batch.RenderDirectSwapchains`: 28.43 ms
  - left eye record path: 11.08 ms, with primary record/reuse path 5.94 ms and primary main op loop 5.72 ms
  - right eye record path: 9.71 ms, with primary record/reuse path 5.64 ms, frame-op emission 1.96 ms, and plan/schedule 1.51 ms
  - `OpenXR.Vulkan.Batch.SubmitAndWait`: 7.64 ms, almost entirely `OpenXR.Vulkan.SubmitFenceWait` at 7.50 ms
  - queue submit itself is cheap at about 0.11 ms
- Finding: the OpenXR Vulkan eye path is currently serialized as left record + right record + submit fence wait. Parallelizing eye command recording is a real target, but by itself it is not enough for 120 Hz if the fence wait remains at one frame interval.
- Finding: current GPU dump requests produced zero-byte GPU frame log files even though live GPU counters report useful timings. Track this as a profiler dump bug; do not trust empty GPU dump files as evidence of missing GPU work.

Next action: inspect the command-chain worker path and primary command-buffer reuse inputs without reintroducing descriptor-content refresh.

### 2026-06-28 Current Comparison And Recording Hotspots

- Re-ran single-pass stereo against the current rollback state, then restored `SinglePassStereoVR` to `false` for continued parallel-eye work:
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/singlepass-current-rollback-warm-60-summary.json`
  - median Vulkan frame total: 32.46 ms
  - median command-buffer recording: 31.51 ms
  - median Vulkan GPU command-buffer time: 7.98 ms
  - median GPU pipeline timing: 47.95 ms
  - command chains: median 21 recorded, 149 reused, 131 refreshed
  - validation errors, descriptor binding failures, and skipped draws: 0
- Finding: current single-pass stereo is still slower than parallel-eye recording. Parallel remains the path to optimize first.
- Captured command-chain dirty traces with `XRE_VULKAN_COMMAND_CHAIN_TRACE=1`. Trace mode disables the schedule cache, so these identify pass families rather than timing:
  - dirty rows: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/command-chain-dirty-trace-20260628-040801-dirty-lines.txt`
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/command-chain-dirty-trace-20260628-040801-summary.txt`
  - by-dump grouping: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/command-chain-dirty-trace-20260628-040801-by-dump.txt`
- Trace finding: the editor/main view still has many `OpaqueDeferred` mesh rows refreshed for frame data and a few structural rows around pre-render/full-screen passes. VREye dumps are smaller but still dominated by `OpaqueDeferred` frame-data refresh plus volatile dynamic compute rows for Forward+ light culling and exposure update.
- Tried a small command-chain data-model change so unchanged frame-data signatures would remain reused. This was a regression and was reverted before continuing:
  - regressed summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/frame-data-unchanged-reuse-warm-60-summary.json`
  - median Vulkan frame total: 32.07 ms
  - median command-buffer recording: 31.12 ms
  - command chains: median 21 recorded, 149 reused, 100 refreshed
  - reverted summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/frame-data-marker-reverted-warm-60-summary.json`
  - median Vulkan frame total: 19.62 ms
  - median command-buffer recording: 18.84 ms
  - median Vulkan GPU command-buffer time: 8.51 ms
  - command chains: median 25 recorded, 145 reused, 107 refreshed
  - validation errors, descriptor binding failures, and skipped draws: 0
- Captured a detailed editor/main-window CPU frame:
  - log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_04-17-17_pid5060`
  - CPU dump: `profiler-cpu-frame-2026-06-28-04-17-56-438-5da1f7c0.log`
  - `XRWindow.Renderer.RenderWindow`: 16.75 ms
  - `Vulkan.FrameLifecycle.RecordCommandBuffer`: 15.19 ms
  - `Vulkan.RecordPrimary`: 12.05 ms
  - `Vulkan.RecordPrimary.MainOpLoop`: 11.81 ms
  - `RecordPrimary.Op.MeshDraw`: 5.58 ms in the largest sampled mesh-draw scope
  - editor UI/profiler panel path: `XRWindow.RenderViewports` 7.96 ms, `UI.DrawProfilerPanel` 3.26 ms
- Captured a detailed OpenXR CPU frame:
  - CPU dump: `profiler-cpu-frame-2026-06-28-04-18-23-568-126dfbb2.log`
  - `OpenXR.RenderFrame.TryRenderVulkanEyesBatch`: 46.60 ms
  - `OpenXR.Vulkan.Batch.RenderDirectSwapchains`: 46.26 ms
  - left eye record path: 20.82 ms, with primary record/reuse path 12.21 ms and primary main op loop 11.92 ms
  - right eye record path: 16.70 ms, with primary record/reuse path 9.66 ms, frame-op emission 2.39 ms, and plan/schedule 1.62 ms
  - `OpenXR.Vulkan.Batch.SubmitAndWait`: 8.74 ms, almost entirely `OpenXR.Vulkan.SubmitFenceWait` at 8.56 ms
  - queue submit itself is cheap at about 0.13 ms
- Current focus: reduce serialized CPU command recording first, especially primary main-op-loop mesh-draw work and why OpenXR primaries are usually recorded rather than reused. The submit fence wait is the next major target once command recording is no longer the dominant cost.

### 2026-06-28 Regression Gate - Image View And Pipeline Layout Lifetime

- Fixed the image-view lifetime regression that appeared while continuing the OpenXR Vulkan timing work:
  - added live image-view tracking with owner labels
  - guarded stale `VkTextureView` refresh/destruction after device loss
  - added a per-view lifetime lock so aspect-only/depth-only descriptor views cannot be recreated while another path destroys them
- Fixed the follow-on pipeline-layout teardown regression:
  - added live pipeline-layout tracking with owner labels
  - destroyed tracked live pipeline layouts during renderer shutdown, including after OpenXR runtime loss/device loss
  - avoided new pipeline-layout creation after device loss while still allowing already-live layouts to be destroyed
- Intermediate regressions found and fixed before continuing:
  - `UNASSIGNED-ObjectTracker-Info` from duplicate/stale `VkImageView` handles
  - invalid `vkDestroyImageView` from stale texture-view handles
  - invalid/leaked `VkPipelineLayout` teardown after logical-device loss
  - a brief skip-on-device-lost attempt caused many pipeline-layout leaks at `vkDestroyDevice`; this was reverted/fixed by tracked live destruction
- Validation:
  - focused test pass: `OpenXrTimingPipelineContractTests`, `VulkanCommandChainDataModelTests`, and `ImportedTextureStreamingPhaseTests` all passed, 99/99
  - latest smoke summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/pipeline-layout-live-destroy-primary-reuse-off-90f-openxr-smoke-summary.json`
  - latest smoke log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_14-31-14_pid15260`
  - 88 submitted OpenXR frames, 2 no-layer frames, no EndFrame failures, zero per-frame allocation bytes, teardown completed
  - Vulkan log scan found no steady-state validation errors, no command-buffer invalidation regressions, no invalid image-view destroy, and no pipeline-layout destroy/leak errors. The logical-device-lost line is the expected Monado/OpenXR runtime-loss shutdown path.
- Perf evidence from the clean smoke:
  - GPU auto dump succeeded for the default render pipeline and shadow/UI pipelines.
  - Render-thread startup/shutdown-only jobs remain visible, for example `OpenXR.Vulkan.UpdateRuntimeState` and initial render-dispatch preference application.
  - Steady per-frame CPU cost is still dominated by OpenXR Vulkan eye recording and submit waiting, not by OpenXR next-frame wait/begin/locate prep.

### 2026-06-28 Collect-Visible Prep Boundary

- User guidance: non-render-thread per-frame init should run in the collect-visible path, which can prepare the next frame while the render thread works on the current frame. The render-thread CPU target is near zero.
- Current code reality:
  - `RuntimeRenderingHostServiceDefaults.OpenXrRenderPacingMode` is already `DedicatedThread`.
  - Vulkan OpenXR unit-testing launch re-forces that default after editor preferences load, unless `XRE_OPENXR_RENDER_PACING_MODE` overrides it.
  - In the intended default path, `xrWaitFrame`, `xrBeginFrame`, `LocateViews(Predicted)`, predicted action-pose cache update, and predicted VR rig recalc run on the OpenXR pacing thread, not the render thread.
  - `OpenXrCollectVisible` already performs the scene/rig/pipeline/camera setup and combined stereo visibility collection for the pending predicted frame.
- Important safety finding:
  - `VulkanRenderer.TryRecordOpenXrEyeSwapchainCommandBuffer` is not safe to parallelize as-is. It temporarily mutates renderer-wide swapchain image/view/framebuffer fields, depth target fields, external-swapchain state, resource-planner state, active renderer state, and recorded-upload publication lists. Recording left and right eyes on two tasks with this helper would race those globals.
  - Moving Vulkan frame-op emission or resource planning directly into collect-visible has the same hazard while the desktop/editor view can be rendering concurrently.
- Next safe optimization target:
  - factor an OpenXR eye recording context that stops mutating renderer-wide swapchain/resource-planner state, or introduce per-eye/per-thread command recording state and command pools.
  - only after that boundary is clean should left/right eye primary recording run in parallel or be moved fully into collect-visible prep.
  - keep Vulkan resource prewarm on the render/renderer-owned path until it has an explicit thread-safe handoff.

### 2026-06-28 Collect-Visible Pacing Mode And Mode Comparison

- Added an opt-in `OpenXrRenderPacingMode.CollectVisibleThread` mode.
  - `OpenXrCollectVisible` can now own the `xrWaitFrame` / `xrBeginFrame` / `LocateViews(Predicted)` prep block before it builds the eye visibility buffers.
  - The existing default remains `DedicatedThread` until deeper Vulkan command-recording state is no longer renderer-global.
  - The editor/env override already accepts enum names, so the mode is selectable with `XRE_OPENXR_RENDER_PACING_MODE=CollectVisibleThread`.
- Regressions found while validating the new mode and fixed before continuing:
  - First implementation assumed the CollectVisible callback used a stable managed thread. Smoke exposed `OpenXR CollectVisible prep thread moved ...` because the event dispatch can use different thread-pool workers. Fixed by making the collect-visible prep owner scoped to a prep call instead of a permanent thread identity.
  - Second implementation allowed a duplicate CollectVisible worker to clear the scoped owner while the real owner was blocked in `xrWaitFrame`. Fixed with a one-at-a-time collect-visible prep gate.
  - Shutdown then exposed a worker resuming from `xrWaitFrame` after session teardown and attempting `xrBeginFrame`. Fixed by rechecking `_sessionBegun` after `WaitFrame` and before `BeginFrame`.
- Validation:
  - focused tests after the fixes: `OpenXrTimingPipelineContractTests`, `VulkanCommandChainDataModelTests`, and `ImportedTextureStreamingPhaseTests` passed, 99/99
  - collect-visible pacing smoke: `collect-visible-pacing-shutdown-guard-primary-reuse-off-90f-openxr-smoke-summary.json`
    - 89 submitted frames, 1 no-layer frame, 0 EndFrame failures, 0 per-frame allocation bytes
  - dedicated pacing smoke: `dedicated-pacing-primary-reuse-off-90f-openxr-smoke-summary.json`
    - 88 submitted frames, 2 no-layer frames, 0 EndFrame failures, 0 per-frame allocation bytes
  - serial-eye-submit smoke: `serial-eye-submit-dedicated-primary-reuse-off-90f-openxr-smoke-summary.json`
    - 88 submitted frames, 2 no-layer frames, 0 EndFrame failures, 0 per-frame allocation bytes
  - single-pass stereo smoke: `singlepass-dedicated-primary-reuse-off-90f-openxr-smoke-summary.json`
    - 88 submitted frames, 2 no-layer frames, 0 EndFrame failures, 0 per-frame allocation bytes
- GPU/default-pipeline comparison from the 90-frame smokes:
  - collect-visible pacing, batched eye path: warm GPU p50 3.56 ms, warm GPU p95 21.68 ms, warm render-thread p95 110.40 ms. Log directory `xrengine_2026-06-28_14-47-14_pid8924` was later pruned by log retention, but these values were captured from its GPU timing dump before cleanup.
  - dedicated pacing, batched eye path: warm GPU p50 3.69 ms, warm GPU p95 12.51 ms, warm render-thread p95 321.82 ms. Log directory `xrengine_2026-06-28_14-48-26_pid31676`.
  - dedicated pacing, serial eye submit (`XRE_OPENXR_VULKAN_SERIAL_EYE_SUBMIT=1`): warm GPU p50 9.92 ms, warm GPU p95 22.43 ms, warm render-thread p95 547.23 ms. Log directory `xrengine_2026-06-28_14-51-17_pid15364`.
  - dedicated pacing, current single-pass stereo (`SinglePassStereoVR=true`): warm GPU p50 8.82 ms, warm GPU p95 23.53 ms, warm render-thread p95 188.79 ms. Log directory `xrengine_2026-06-28_14-52-43_pid11628`.
- Finding:
  - batched/parallel-capable eye rendering remains faster than serial eye submit and the current single-pass stereo path in this scene.
  - Collect-visible pacing is viable after the scoped-owner/gate/shutdown fixes, but it does not remove the real bottleneck by itself.
  - The dominant render-thread stall recovery path is still `XRWindow.Renderer.RenderWindow > Vulkan.FrameLifecycle.RecordCommandBuffer > Vulkan.RecordCommandBuffer.RecordPrimary > Vulkan.RecordPrimary.MainOpLoop`.
  - Repeated `DispatchCompute` invalid render-graph pass-index fallback warnings appear in all compared runs. Treat this as a nearby cleanup target because it adds noise and may be forcing conservative command-chain behavior, but it is not specific to collect-visible pacing.

Next action: split OpenXR eye command recording away from renderer-wide mutable swapchain/resource-planner state so left/right eye primary recording can be parallelized or prepared off the render thread without racing the desktop/editor render.

### 2026-06-28 Viewport/Scissor Recording Cache

- Implemented a safe Vulkan command-recording cache for viewport/scissor dynamic state:
  - touched `VulkanRenderer.CommandBufferState.cs`, `VulkanRenderer.CommandBufferRecording.cs`, and `VulkanRenderer.SecondaryCommandBuffers.cs`
  - tracks viewport/scissor signatures per command buffer beside the existing pipeline/descriptor/buffer bind-state tracking
  - skips duplicate `vkCmdSetViewport` / `vkCmdSetScissor` calls when the same state is already active in the same command buffer
  - does not disable any render feature or change render-pass/pipeline behavior
- Validation:
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj` passed
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanCommandChainDataModelTests"` passed 63/63
  - warnings were the existing Magick.NET advisory warnings
- First runtime sample, without `XRE_VULKAN_COMMAND_CHAINS=1`, was not comparable:
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/viewport-scissor-tracked-warm-60-summary.json`
  - 39/60 samples had `chains_scheduled=0`, meaning the editor view was not using command chains while OpenXR external swapchain targets still forced them on
  - kept as evidence, but not used as the optimization comparison
- Corrected runtime sample with `XRE_VULKAN_COMMAND_CHAINS=1` and `XRE_OPENXR_VULKAN_PRIMARY_REUSE=1`:
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/viewport-scissor-tracked-commandchains-warm-60-summary.json`
  - median Vulkan frame total: 32.21 ms
  - median command-buffer recording: 31.24 ms
  - median Vulkan GPU command-buffer time: 7.72 ms
  - median GPU pipeline timing: 44.28 ms
  - command chains: median 21 recorded, 149 reused, 131 frame-data refreshed
  - validation errors, descriptor binding failures, and skipped draws: 0
- Grouped comparison is the meaningful signal because samples are bimodal:
  - slow group `170,1,21,131`: 31 samples, median 33.34 ms total / 32.42 ms record
  - fast groups with 3 recorded primaries: mostly 16.25-16.55 ms total / 15.51-15.77 ms record
  - compared with the reverted baseline, the fast group improved by roughly 0.9-1.1 ms, while the slow group remains effectively unchanged
  - the overall p50 landed in the slow bucket because this run had 34 slow samples; treat that as distribution noise until a longer run proves otherwise
- CPU dump from the corrected run:
  - log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_04-31-04_pid19252`
  - CPU dump: `profiler-cpu-frame-2026-06-28-04-32-16-260-18905736.log`
  - editor-view frame: `Vulkan.FrameLifecycle.RecordCommandBuffer` 18.38 ms, `Vulkan.RecordCommandBuffer.RecordPrimary` 14.34 ms, `Vulkan.RecordPrimary.MainOpLoop` 14.07 ms
  - `UI.DrawProfilerPanel` was 6.41 ms in the same frame, so editor UI remains a real editor-view cost when the profiler panel is visible
- GPU dump tool returned named files but they are still zero bytes. Live GPU counters remain usable; GPU dump serialization remains a profiler dump bug.
- Current focus: primary main-op-loop recording is still the blocking CPU cost. Next diagnostic run should enable `XRE_VULKAN_RECORDING_PROFILE_DETAIL=1` to split the op loop into mesh draw, pass transition, and context-change costs.

### 2026-06-28 Detailed Recording Profile And Worker-Gate Revert

- Ran with `XRE_VULKAN_RECORDING_PROFILE_DETAIL=1`, `XRE_VULKAN_COMMAND_CHAINS=1`, and `XRE_OPENXR_VULKAN_PRIMARY_REUSE=1`:
  - log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_04-33-52_pid15324`
  - representative OpenXR CPU dump: `profiler-cpu-frame-2026-06-28-04-34-16-111-20137e74.log`
  - `OpenXR.RenderFrame.TryRenderVulkanEyesBatch`: 27.93 ms
  - `OpenXR.Vulkan.Batch.RenderDirectSwapchains`: 27.69 ms
  - left eye record path: 10.65 ms, with primary record/reuse path 5.33 ms, primary main-op loop 5.14 ms, and plan/schedule 2.03 ms
  - right eye record path: 9.85 ms, with primary record/reuse path 5.39 ms, primary main-op loop 5.21 ms, plan/schedule 1.93 ms, and frame-op emission 1.87 ms
  - `OpenXR.Vulkan.Batch.SubmitAndWait`: 7.18 ms, mostly `OpenXR.Vulkan.SubmitFenceWait` at 7.04 ms
  - queue submit itself was about 0.11 ms
- Representative editor/main-window detail dumps from the same run:
  - `profiler-cpu-frame-2026-06-28-04-34-12-066-69ee5d64.log`: `XRWindow.Renderer.RenderWindow` 32.06 ms, `RecordPrimary` 23.41 ms, `MainOpLoop` 23.03 ms
  - `profiler-cpu-frame-2026-06-28-04-34-14-108-b9047964.log`: aggregate `MeshDraw` 12.16 ms over 104 calls, `ContextChange` 1.80 ms over 106 calls, `PassTransition` 0.43 ms over 111 calls
  - `profiler-cpu-frame-2026-06-28-04-34-18-114-ecdec8b7.log`: `RenderWindow` 31.63 ms, `RecordPrimary` 23.16 ms, `MainOpLoop` 22.83 ms
- Finding: the dominant CPU work is still serial command recording. Mesh draws are the largest measured part of the editor primary main-op loop; OpenXR adds two eye records plus a submit fence wait near a 120 Hz frame interval.
- Finding: `chain_worker_record_ms` is currently misleading. It is populated from command-chain lowering/schedule construction (`TryBuildCommandChainSchedule`), while the actual render-thread wait for `DispatchCommandChainRecordingWorkers` is `render_thread_wait_for_workers_ms`.
- Tried gating `DispatchCommandChainRecordingWorkers` because the worker body currently only partitions keys into per-worker scratch lists and does not record executable secondary command buffers. This experiment was reverted because it did not improve frame time:
  - summary from the gated run: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/command-chain-placeholder-workers-gated-warm-60-summary.json`
  - median Vulkan frame total: 33.75 ms
  - median command-buffer recording: 32.76 ms
  - median Vulkan GPU command-buffer time: 7.55 ms
  - `render_thread_wait_for_workers_ms` dropped to 0, but `chain_worker_record_ms` remained about 2.26 ms because it is schedule/lowering time, not worker wait time
  - validation errors, descriptor binding failures, and skipped draws: 0
- Revert validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanCommandChainDataModelTests" /p:UseSharedCompilation=false /nodeReuse:false` passed 63/63
  - warnings were the existing Magick.NET advisory warnings
  - the earlier parallel build/test file-lock failure was an agent-induced validation collision; serial rerun passed
- Current focus: optimize the measured schedule/lowering and mesh-draw recording work, then revisit actual parallel eye recording/secondary command-buffer recording once there is a safe recorded-state boundary.

### 2026-06-28 Inline RenderPacket Draw/Dispatch Storage

- Implemented a command-chain lowering allocation cleanup:
  - touched `VulkanCommandChains.cs`, `VulkanRenderer.CommandChainLowering.cs`, and `VulkanCommandChainDataModelTests.cs`
  - replaced the hot production path's per-op one-element `DrawPacket[]` / `DispatchPacket[]` storage with inline first-packet fields and explicit counts
  - kept the compatibility constructor for tests that intentionally exercise multi-draw packet signatures
  - added guards so malformed multi-packet inline construction fails visibly instead of producing stale instance-count signatures
- Validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanCommandChainDataModelTests" /p:UseSharedCompilation=false /nodeReuse:false` passed 37/37
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanCommandChainDataModelTests" /p:UseSharedCompilation=false /nodeReuse:false` passed 63/63
  - warnings were the existing Magick.NET advisory warnings
- Runtime sample with `XRE_VULKAN_COMMAND_CHAINS=1` and `XRE_OPENXR_VULKAN_PRIMARY_REUSE=1`:
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/render-packet-inline-warm-60-summary.json`
  - log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_04-46-48_pid17916`
  - median Vulkan frame total: 31.94 ms
  - median command-buffer recording: 30.98 ms
  - median Vulkan GPU command-buffer time: 7.61 ms
  - median GPU pipeline timing: 43.15 ms
  - command chains: median 21 recorded, 149 reused, 131 frame-data refreshed
  - validation errors, descriptor binding failures, and skipped draws: 0
- Grouped comparison against the previous corrected command-chain sample:
  - slow group `170,1,21,131`: 31 samples, median 33.15 ms total / 32.25 ms record, a small improvement from 33.34 / 32.42
  - fast group `174,3,25,111`: 22 samples, median 15.90 ms total / 15.08 ms record, improved from roughly 16.25-16.55 / 15.51-15.77
  - overall p50 improved from 32.21 ms / 31.24 ms record to 31.94 ms / 30.98 ms record with a similar slow/fast distribution
  - actual worker wait remained small at about 0.06-0.18 ms by group; schedule/lowering time remains about 2.25-3.14 ms
- Log scan found no Vulkan VUIDs, device-loss messages, descriptor binding failures, or skipped draw/dispatch messages. The only hits were normal OpenXR loader validation lines and Vulkan validation-layer loading.
- CPU dump from the same run:
  - `profiler-cpu-frame-2026-06-28-04-48-33-962-0df4154f.log`
  - this sampled a UI-heavy editor frame rather than a full primary-record frame; `EditorImGuiUI.RenderEditor` / `UI.DrawProfilerPanel` dominated, with `NormalizeFrameOps` at 4.16 ms and `DrainFrameOps` at 1.45 ms
  - keep using JSONL timing buckets for command-chain comparison unless a CPU dump captures the same primary-record frame shape
- Current focus: schedule/lowering and frame-op normalization still cost multiple milliseconds in hot frames. Next step is to inspect `NormalizeFrameOps`/`DrainFrameOps` and the command-chain schedule cache inputs before attempting more parallel recording.

### 2026-06-28 Frame-Op Normalization Fast Paths

- Implemented two CPU-side recording fast paths:
  - `DrainFrameOps(out signature, bool computeSignature)` preserves the existing signature-producing API, but lets command recording skip the raw unsorted frame-op hash unless `FrameOpSignatureDiffDiagnosticsEnabled` is active
  - `VulkanRenderGraphCompiler.SortFrameOps` now detects already-sorted pooled sort keys and skips `Array.Sort` plus the array rewrite in that case
  - no render features were disabled; this only removes redundant CPU work in common recording paths
- Validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanCommandChainDataModelTests" /p:UseSharedCompilation=false /nodeReuse:false` passed 63/63
  - warnings were the existing Magick.NET advisory warnings
  - broader `VulkanP1ValidationTests` source-scan lane still fails 7 source-shape assertions that expect older token placement/text; these do not appear caused by the normalization patches, but they remain validation debt to address separately
- Runtime sample with `XRE_VULKAN_COMMAND_CHAINS=1` and `XRE_OPENXR_VULKAN_PRIMARY_REUSE=1`:
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/normalization-fastpaths-warm-60-summary.json`
  - log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_04-52-15_pid29872`
  - median Vulkan frame total: 30.80 ms
  - median command-buffer recording: 29.86 ms
  - median Vulkan GPU command-buffer time: 7.60 ms
  - median GPU pipeline timing: 41.08 ms
  - command chains: median 21 recorded, 149 reused, 131 frame-data refreshed
  - validation errors, descriptor binding failures, and skipped draws: 0
- Grouped comparison against the inline-packet run:
  - slow group `170,1,21,131`: 32 samples, median 31.42 ms total / 30.49 ms record, improved from 33.15 / 32.25
  - fast group `174,3,25,111`: 28 samples, median 15.28 ms total / 14.53 ms record, improved from 15.90 / 15.08
  - overall p50 improved from 31.94 ms / 30.98 ms record to 30.80 ms / 29.86 ms record
- Log scan again found no Vulkan VUIDs, device-loss messages, descriptor binding failures, or skipped draw/dispatch messages. The only hits were normal OpenXR loader validation lines and Vulkan validation-layer loading.
- CPU dump from the same run:
  - `profiler-cpu-frame-2026-06-28-04-53-31-200-db276d6a.log`
  - sampled a slow primary-record frame: `Vulkan.FrameLifecycle.RecordCommandBuffer` 33.60 ms, `RecordPrimary` 27.09 ms, `MainOpLoop` 26.72 ms
  - `CommandChainLowering` was 3.23 ms and `NormalizeFrameOps` was 2.34 ms
  - `DrainFrameOps` no longer appears as a top sampled Vulkan scope, consistent with skipping the raw hash in the common path
- Current focus: `RecordPrimary.MainOpLoop` is now the overwhelming CPU bottleneck in slow frames. Next target is lowering the per-op mesh draw/context work or making real secondary/parallel recording safe.

### 2026-06-28 Post-Fast-Path Detail Profile

- Relaunched with `XRE_VULKAN_RECORDING_PROFILE_DETAIL=1` for diagnostic CPU dumps only:
  - log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_04-55-03_pid21224`
  - dumps: `profiler-cpu-frame-2026-06-28-04-55-31-027-3833e5a9.log`, `profiler-cpu-frame-2026-06-28-04-55-33-071-d8b8b62e.log`, `profiler-cpu-frame-2026-06-28-04-55-35-076-623f3a49.log`
- Editor/main-view detail frame:
  - aggregate `Vulkan.RecordPrimary.Op.MeshDraw`: 12.96 ms over 106 calls, avg 0.122 ms, peak 1.30 ms
  - `Vulkan.RecordPrimary.ContextChange`: 1.87 ms over 106 calls
  - `Vulkan.RecordPrimary.PassTransition`: 0.49 ms over 114 calls
  - `Vulkan.RecordCommandBuffer.CommandChainLowering`: 3.02 ms
  - `Vulkan.RecordCommandBuffer.NormalizeFrameOps`: 2.06 ms
- OpenXR detail frame:
  - `OpenXR.RenderFrame.TryRenderVulkanEyesBatch`: 30.00 ms
  - `OpenXR.Vulkan.Batch.RenderDirectSwapchains`: 29.76 ms
  - `Vulkan.FrameLifecycle.RecordCommandBuffer`: 14.03 ms
  - `Vulkan.RecordCommandBuffer.RecordPrimary`: 10.92 ms
  - `Vulkan.RecordPrimary.MainOpLoop`: 5.31 ms
  - first sampled `MeshDraw` child: 1.96 ms
  - `OpenXR.Vulkan.Batch.SubmitAndWait`: 8.20 ms, mostly `OpenXR.Vulkan.SubmitFenceWait` at 8.07 ms
- Finding: mesh draw recording is still the main CPU cost in editor frames. OpenXR frames still combine eye command recording with a fence wait near the 120 Hz interval.
- Finding: the existing mesh secondary path records the entire mesh run serially inside `RecordPrimary.MainOpLoop`; it is secondary command buffer use, but not yet true parallel mesh recording.
- Finding: `VkMeshRenderer.RecordDraw` mutates renderer/program/descriptor/uniform state (`TryPrepareForRendering`, descriptor allocation/update, uniform updates, bone/blendshape pushes). True parallel mesh recording needs a prepared immutable mesh-draw recording state similar to the existing indirect draw path before worker threads can safely emit command buffers.
- Current focus: either build that prepared mesh draw recording state, or reduce per-draw state mutation and descriptor/uniform work enough that serial recording can fit under 8.33 ms.

### 2026-06-28 Mesh Prepared-State Parallel Recording Reverted

- Tried adding a prepared normal mesh draw state and using it to record mesh-run secondaries on worker threads, similar to the existing prepared indirect draw path.
- Per-draw secondary recording was validation-clean but clearly slower:
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/mesh-prepared-parallel-warm-60-summary.json`
  - median Vulkan frame total: 33.89 ms
  - median command-buffer recording: 32.81 ms
  - median Vulkan GPU command-buffer time: 8.25 ms
  - validation errors, descriptor binding failures, skipped draws, and dropped frame ops: 0
- Chunked mesh secondary recording reduced the number of secondaries but still regressed comparable buckets:
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/mesh-prepared-chunked-warm-60-summary.json`
  - median Vulkan frame total: 23.17 ms
  - median command-buffer recording: 22.11 ms
  - slow group `170,1,21,131`: 33.98 ms total / 33.05 ms record, worse than the pre-experiment 31.42 / 30.49
  - fast group `174,3,25,111`: 17.91 ms total / 17.11 ms record, worse than the pre-experiment 15.28 / 14.53
- Reverted the normal mesh prepared-state/parallel-secondary experiment before continuing. A post-revert sample is still slower than the previous best, but the experiment symbols are removed and the remaining slowdown appears to be run-to-run/environment variation or other pre-existing active changes rather than accepted mesh-parallel code:
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/mesh-parallel-reverted-warm-60-summary.json`
  - median Vulkan frame total: 32.92 ms
  - median command-buffer recording: 31.99 ms
  - validation errors, descriptor binding failures, skipped draws, and dropped frame ops: 0
- Validation after revert:
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj /p:UseSharedCompilation=false /nodeReuse:false` passed
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanCommandChainDataModelTests" /p:UseSharedCompilation=false /nodeReuse:false` passed 63/63
- Current focus: do not pursue normal-mesh parallel secondary recording until the per-draw prepared state can be batched more cheaply. Next target is a smaller serial hot-path reduction inside `VkMeshRenderer.RecordDraw` or frame-op lowering.

### 2026-06-28 Mutable Frame-Source Descriptor Identity Reverted

- Tried making `SourceTexture` / `SourceTex` descriptor allocation and command-chain descriptor signatures fully mutable/stable, with descriptor contents refreshed before reuse. The goal was to avoid primary re-recording when only post-process source descriptor contents changed.
- Runtime result did not improve the command-chain buckets:
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/frame-source-mutable-warm-60-summary.json`
  - median Vulkan frame total: 32.28 ms
  - median command-buffer recording: 31.36 ms
  - command chains: median 21 recorded, 149 reused, 131 frame-data refreshed
  - validation errors, descriptor binding failures, skipped draws, and dropped frame ops: 0
- Finding: the remaining descriptor/resource fingerprint churn is not explained by frame-source texture shape alone. The experiment was reverted because it added descriptor semantics risk without reducing recorded chains.
- Revert validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanCommandChainDataModelTests" /p:UseSharedCompilation=false /nodeReuse:false` passed 63/63
- Current focus: run descriptor fingerprint diagnostics to identify which resource-fingerprint component is changing for the first frame-data refresh failure before making another code change.

### 2026-06-28 Corrected Frame-Source Descriptor Mutability

- Descriptor diagnostics showed the previous reverted attempt was incomplete:
  - dirty reason: `programSampler[SourceTexture@2.0]`
  - cause: `SourceTexture` was captured in both named sampler bindings and sampler-unit bindings, and the descriptor allocation fingerprint still depended on frame-source texture binding/shape/presence.
- Implemented the narrower corrected fix:
  - `ComputeDispatchSnapshot` now carries `SamplerNamesByUnit`
  - `VkRenderProgram` records sampler names by unit when binding textures
  - command-chain sampler-unit and sampler-name hashes treat only `SourceTexture` / `SourceTex` as mutable frame-source descriptors
  - descriptor allocation resource fingerprints treat frame-source samplers as a constant mutable descriptor slot before testing whether the source texture is currently bound
  - normal material/program samplers still use concrete image descriptor identity
- Validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanCommandChainDataModelTests" /p:UseSharedCompilation=false /nodeReuse:false` passed 63/63 after the final accepted patch
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj /p:UseSharedCompilation=false /nodeReuse:false` passed during the run
  - warnings were the existing Magick.NET advisory warnings
- Runtime evidence:
  - incomplete unit-mutable run regressed: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/frame-source-unit-mutable-warm-60-summary.json`
    - p50 total 37.49 ms, p50 recording 36.54 ms
  - corrected constant frame-source run: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/frame-source-unit-mutable-constant-warm-60-summary.json`
    - p50 total 21.40 ms, p50 recording 20.63 ms
    - `SourceTexture@2.0` dirty summaries: 0
  - final accepted post-revert checkpoint: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/frame-source-presence-constant-warm-60-summary.json`
    - p50 total 32.46 ms, p50 recording 31.64 ms
    - fast buckets:
      - `166,3,16,112`: 16.52 ms total / 15.74 ms record
      - `174,3,16,120`: 17.47 ms total / 16.75 ms record
    - slow bucket `170,1,12,140`: 34.09 ms total / 33.13 ms record
    - validation errors, descriptor binding failures, skipped draws/dispatches, and dropped frame ops: 0
  - descriptor diagnostics after the accepted frame-source fix: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/frame-source-unit-mutable-presence-constant-diag-20-dirty.json`
    - `SourceTexture@2.0` dirty count: 0
    - next changed component was only `resourceAllocator`
- Regression fixed before continuing:
  - tried removing `ResourceAllocatorIdentity` from descriptor allocation resource fingerprints
  - result: descriptor allocations exploded into the hundreds (`allocs=900+`) and resource fingerprints changed every sample
  - reverted immediately and re-ran focused tests successfully
  - evidence: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/descriptor-no-allocator-warm-60-summary.json`
- Single-pass comparison after the accepted frame-source fixes:
  - `SinglePassStereoVR=true` summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/single-pass-stereo-compare-warm-60-summary.json`
  - p50 total 33.79 ms, p50 recording 32.98 ms
  - fast buckets were slower than parallel, and slow bucket remained about 34.87 ms
  - restored `Assets/UnitTestingWorldSettings.jsonc` to `SinglePassStereoVR=false`
- Visual evidence note:
  - MCP viewport screenshots in this sub-iteration were blank white from multiple camera positions despite an active 1920x1080 editor viewport and live OpenXR swapchains.
  - Treat this as a screenshot/readback issue to revisit; profiler counters and swapchain logs were used for timing evidence in this pass.
- Current focus: primary command recording is still far above the 8.33 ms target. The next useful target is why primary command-buffer reuse stays at 0 and why the editor-only bucket records about 150 mesh draws / 140 frame-data refreshed chains serially at about 33 ms.

### 2026-06-28 OpenXR Primary Signature + Vulkan Dense Texture Churn

- Narrowed the OpenXR primary command-buffer group signature to the actual primary dependency: schedule/group identity plus secondary command-buffer handles and generations.
  - Focused tests passed after the change: `OpenXrTimingPipelineContractTests` and `VulkanCommandChainDataModelTests`.
  - Live result: primary reuse still stayed at 0 because secondary command chains continued to re-record and refresh frame data/descriptors.
  - Evidence: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/openxr-primary-handle-signature-warm-60-summary.json`.
- Descriptor diagnostics then showed the next churn was not frame-source post-process descriptors:
  - top dirty reason involved `fabric_f` material descriptors and `resourceAllocator`/texture sampler fingerprints.
  - log evidence showed imported Vulkan dense textures oscillating between preview and full resident sizes without VRAM pressure, e.g. `sponza_column_b_bump` `64x64 <-> 1024x1024`.
  - settled diagnostic evidence: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/openxr-primary-handle-signature-descriptor-diag-settled-20-summary.json`.
- Fix accepted: non-pressure demotion preservation now also applies to a Vulkan dense texture's pending resident target, so an in-flight full-res promotion cannot be immediately superseded by a preview demotion unless the budget is actually tight.
  - Code: `ImportedTextureStreamingManager.ShouldPreserveDenseResidentTargetWithoutPressure`.
  - Tests added for under-budget pending target preservation and budget-pressure demotion.
- Validation after fix:
  - `ImportedTextureStreamingPhaseTests`: 34/34 passed.
  - Focused rendering contract set: 97/97 passed.
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj /p:UseSharedCompilation=false /nodeReuse:false` passed.
  - Only existing Magick.NET advisory warnings were present.
- Current focus: rerun the live OpenXR/Vulkan profile and verify whether the texture publish/descriptor dirty churn drops. If it does, continue into descriptor/resource planner churn; if it does not, inspect texture logs first and fix that regression before continuing.

### 2026-06-28 Compute Chain Refresh + OpenXR Primary Reuse Safety

- Accepted fix: compute dispatch packets are now treated as `FrameDataOnly` when the command structure is stable, with separate stable descriptor-set identity and mutable descriptor-generation refresh.
  - This removed the remaining steady-state volatile command-chain recordings for the OpenXR parallel path.
  - Focused rendering contract set: 97/97 passed.
  - Evidence: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/compute-refresh-primary-reuse-parallel-warm-60-summary.json`.
  - Result: p50 total 21.23 ms, p50 recording 20.38 ms, p50 GPU command 8.15 ms; chains recorded p50 0, volatile chains p50 0, frame-data refreshed p50 76; validation/binding/skipped/dropped all 0.
- Added trace-only OpenXR primary-reuse miss diagnostics to the command-buffer dirty summary.
  - Diagnostic run showed the current OpenXR primary-reuse blocker exactly: schedule chains were `Reused` but had no executable secondary command buffer handles.
  - Example miss: `openxr-primary-miss:chains-not-reusable ... no-secondary ... state=Reused dirty=None`.
- Regression tried and reverted immediately:
  - Tried allowing OpenXR primary reuse for schedule chains with reusable state even when no secondary command buffer existed.
  - Result: primary reuse did start (`primary_reused=2`, `primary_recorded=0`), but Vulkan validation reported reused primaries submitting command buffers invalidated by updated/destroyed descriptor sets.
  - Evidence: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/openxr-primary-reuse-relaxed-chain-state-warm-60-summary.json`.
  - Reverted the relaxed gate and restored the secondary-handle requirement. Post-rollback live sanity: validation/binding/skipped counts stayed 0, primary reuse blocked again, primary recorded max 2.
- Safe post-rollback CPU/GPU dumps:
  - copied dumps: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/logs/post-regression-rollback-safe-dumps/`.
  - CPU sampled frame: `Vulkan.FrameLifecycle.RecordCommandBuffer` 15.85 ms, `Vulkan.RecordCommandBuffer.RecordPrimary` 14.10 ms, `Vulkan.RecordPrimary.MainOpLoop` 13.78 ms.
  - DefaultRenderPipeline GPU dump after first 50 frames: p50 6.74 ms, p95 7.73 ms, max 8.10 ms.
  - Shadow pipeline p95s were about 0.21-0.29 ms each; UI p95 was about 0.01 ms.
- Current focus: build real executable secondary command buffers for the command-chain schedule, or move descriptor binding to a Vulkan update-after-bind/descriptor-buffer model. Reusing direct primaries while descriptor sets are updated is not valid Vulkan; the next optimization must make the primary execute stable secondaries or make bound descriptor data legally mutable.

### 2026-06-28 Descriptor-Refresh Primary-Reuse Guard

- Added explicit command-chain state for descriptor-touching frame-data refresh:
  - `CommandChain.FrameDataRefreshTouchedDescriptors`
  - `TryRefreshReusableCommandChainFrameData` marks descriptor-generation-only refreshes.
  - Reused/recorded chains clear the flag.
- Updated OpenXR primary reuse so a `FrameDataRefreshed` chain whose descriptor generation changed is not considered reusable for a previously recorded primary.
  - This preserves the safe secondary-handle gate from the previous rollback.
  - The guard is intentionally conservative: uniform-only frame-data refresh remains eligible, descriptor-refresh primary reuse is blocked until the descriptor update-after-bind/descriptor-buffer path is validated end to end.
- Validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanCommandChainDataModelTests|FullyQualifiedName~ImportedTextureStreamingPhaseTests" /p:UseSharedCompilation=false /nodeReuse:false`
  - Passed 98/98. Only existing Magick.NET advisory warnings were present.
- Live OpenXR/Monado/Vulkan validation:
  - editor PID/session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_10-52-07_pid6856`
  - summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/descriptor-refresh-primary-reuse-guard-warm-60-summary.json`
  - copied dumps/logs: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/logs/descriptor-refresh-primary-reuse-guard-safe-dumps/`
  - p50 total 19.99 ms, p90 32.43 ms, p95 40.81 ms
  - p50 command recording 19.23 ms, p90 31.51 ms, p95 39.88 ms
  - p50 GPU command-buffer time 7.77 ms, p90 9.27 ms, max 10.43 ms
  - command chains: p50 2 recorded, 170 reused, 134 frame-data refreshed, 2 volatile recorded
  - OpenXR primary reuse: max reused 0, max recorded 3
  - validation errors/messages, descriptor binding failures, skipped draws/dispatches, and dropped frame ops: all 0
- Log scan:
  - no Vulkan VUID/error/device-lost lines were found.
  - one steady performance warning remains: `UNASSIGNED-CoreValidation-Shader-OutputNotConsumed` for vertex attribute location 2 not consumed.
- CPU/GPU dump findings:
  - CPU dump: left eye `Vulkan.RecordPrimary.MainOpLoop` 6.04 ms, right eye `Vulkan.RecordPrimary.MainOpLoop` 6.02 ms, `OpenXR.Vulkan.SubmitFenceWait` 9.06 ms in the sampled frame.
  - DefaultRenderPipeline GPU dump after first 50 frames: p50 7.11 ms, p90 7.87 ms, p95 7.98 ms, max 9.36 ms.
  - Shadow pipeline sampled p50 about 0.25-0.26 ms; the GPU is not the main steady bottleneck.
- Finding: the guard did not improve performance directly, but it locks in the lesson from the descriptor-set validation regression. The next viable performance step is either:
  - make the command-chain schedule own executable secondary command buffers that a reusable primary can safely execute, or
  - parallelize left/right eye primary recording while preserving shared renderer state safety.
  Direct primary reuse without real secondary handles remains invalid.

### 2026-06-28 Schedule-Owned Mesh Secondaries + Isolated Eye Frame-Ops

- Current focus from user direction:
  - build schedule-owned executable secondary command buffers,
  - make parallel eye primary recording possible without cross-eye frame-op collisions,
  - move any safe non-render-thread per-frame init toward collect-visible for the next frame.
- Important collect-visible finding:
  - the existing Vulkan OpenXR prewarm helper mutates renderer-wide swapchain/frame-op/resource-planner state.
  - It cannot be moved directly into collect-visible because that thread can overlap the current frame render; doing so would race renderer globals.
  - A safe collect-visible prep path needs isolated frame-op/resource staging first.
- Implemented first isolation step:
  - added a thread-local Vulkan frame-op capture scope.
  - direct OpenXR eye recording, mirror-eye recording, and OpenXR eye prewarm now use `CaptureFrameOpsExcludingTextureUploads(...)` instead of the global enqueue/drain path.
  - texture upload frame ops are still forwarded to the global queue so upload publishing behavior is not silently lost.
- Implemented first schedule-owned executable secondary step:
  - command chains now track `SourceStartIndex`/`SourceCount`.
  - schedule utilities map static frame-op index to schedule key and resolve schedule frame slot.
  - primary recording now tries schedule-owned mesh secondaries before the older primary-owned mesh secondary path.
  - reused schedule-owned secondaries consume draw-uniform slots in frame-op order; newly recorded ones write the same slots while recording.
  - external swapchain targets bypass the command-chain fast schedule cache for now, because the cached path lacks packet data needed to refresh per-chain descriptor/resource state safely.
- Validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanCommandChainDataModelTests|FullyQualifiedName~ImportedTextureStreamingPhaseTests" /p:UseSharedCompilation=false /nodeReuse:false`
  - Passed 98/98.
  - Only existing Magick.NET advisory warnings were present.
- Regression fixed before continuing:
  - first build produced analyzer warning `CA2014` because a new dynamic-rendering inheritance `stackalloc` sat inside the scheduled-secondary loop.
  - moved the stack allocation outside the loop; focused validation then passed cleanly.
- Remaining risk to validate live:
  - one secondary per scheduled mesh op may be a CPU regression if too many chains re-record every frame.
  - descriptor-generation refresh can still force secondary re-recording; primary reuse will remain limited until descriptor updates are legally reusable or stop changing for stable chains.
  - parallel eye primary recording still needs renderer-state isolation beyond frame-op capture: swapchain target state, resource planner state, bind-state scratch, and recording scratch dictionaries remain renderer-global.

### 2026-06-28 Regression Gate - Destroyed Buffer Submit And Teardown Leaks

- Live smoke after schedule-owned secondaries initially exposed a real steady-state validation regression:
  - evidence: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/tracked-command-chain-pools-primary-reuse-off-180f-openxr-smoke-summary.json`
  - log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_13-37-12_pid28396/log_vulkan.log`
  - validation named destroyed indirect buffers from `MaterialTierDrawCounts` and `MaterialTierIndirectDraws` inside command-chain secondaries/primaries.
- Fixed the destroyed-buffer submit before continuing:
  - command-chain and frame-op signatures now include live `VkDataBuffer` backing handles/sizes/upload state for indirect/count/parameter buffers instead of only wrapper identity.
  - `VkDataBuffer` backing recreation now dirties swapchain command buffers and OpenXR primary variants.
  - focused contract set passed 98/98.
- Validation after the fix:
  - smoke summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/buffer-handle-signature-primary-reuse-off-180f-openxr-smoke-summary.json`
  - log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_13-45-47_pid11612/log_vulkan.log`
  - result: no `InvalidCommandBuffer`, destroyed `VkBuffer`, or pre-submit dirty guard hits.
- Fixed command-buffer/pool teardown regressions before continuing:
  - command-chain secondary pools are now destroyed during cleanup even if `_deviceLost` was already observed from Monado/OpenXR runtime teardown.
  - command-chain worker pools now follow the same rule.
  - focused contract set passed 98/98 after both patches.
  - latest smoke summary: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/worker-pool-device-lost-destroy-primary-reuse-off-90f-openxr-smoke-summary.json`
  - latest log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_13-54-05_pid26180/log_vulkan.log`
  - result: no command-chain secondary leaks, no generic command-pool leaks, no destroyed-buffer submits.
- Remaining known teardown issue:
  - Monado/OpenXR runtime teardown still reports unnamed `VkImageView` child objects at `vkDestroyDevice`.
  - This was exposed after command-buffer/pool cleanup and is tracked separately as a device-lost resource cleanup gap; it is shutdown-only in the current evidence, not a steady-state submit/render regression.
- Current focus after this gate:
  - continue performance work only from the validation-clean steady-state point.
  - do not move renderer-global prewarm/init into collect-visible until the data it prepares is staged per next-frame context instead of mutating live renderer state.

### 2026-06-28 Implementation Session - View Modes And View-Scoped State

- Run root: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes`
- Baseline state captured before code changes:
  - `git status --short` saved to `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/git-status-baseline.txt`
  - focused baseline result saved to `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-view-modes-baseline.trx`
- Baseline focused tests:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanCommandChainDataModelTests" --logger "trx;LogFileName=openxr-view-modes-baseline.trx"`
  - Passed 65/65.
  - Only existing Magick.NET advisory warnings were present.
- Current implementation focus:
  - expose explicit `VR.ViewRenderMode` and foveation settings with capability resolution before changing render scheduling.
  - keep legacy `SinglePassStereoVR` as a migration shim only.
  - introduce immutable per-view/per-view-group context data so serial desktop/eye correctness and later Vulkan parallel primary recording use the same state model.

### 2026-06-28 Phase 1 Progress - Settings And Capability Contracts

- Implemented:
  - `EVrViewRenderMode`: `SequentialViews`, `SinglePassStereo`, `ParallelCommandBufferRecording`
  - `EVrFoveationMode`, `EVrFoveationQualityPreset`, and foveation capability/effective-mode resolver contracts
  - `VR.ViewRenderMode` and `VR.Foveation` in unit-testing settings and generated schema/JSONC
  - `XRE_UNIT_TEST_VR_VIEW_RENDER_MODE` env override for smoke matrix runs
  - legacy `SinglePassStereoVR` migration into `VR.ViewRenderMode` when the new nested enum is not explicitly present
  - OpenXR backend mode validation so OpenGL rejects `ParallelCommandBufferRecording` visibly instead of silently falling back
- Validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VrViewRenderModeContractTests|FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanCommandChainDataModelTests" --logger "trx;LogFileName=openxr-view-modes-phase1-clean.trx"`
  - Passed 73/73.
  - Result copied to `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-view-modes-phase1-clean.trx`.
  - Only existing Magick.NET advisory warnings were present after fixing new nullable warnings in the test file.
- Important limitation:
  - `SequentialViews` now opts out of the Vulkan batch helper and uses the existing sequential eye loop as the reference path.
  - `SinglePassStereo` and true `ParallelCommandBufferRecording` still need the view-context and command-recording isolation phases before they are accepted as complete implementations.

### 2026-06-28 Phase 2.5/2.6 Progress - View Groups And Foveation Contexts

- Implemented:
  - immutable `ViewRenderGroupContext`, `ViewRenderContext`, `ViewVisibilityFrustumContext`, `ViewFoveationContext`, and `ViewRecordingWorkItem` contracts.
  - explicit view kinds for left eye, right eye, desktop editor, and smoothed cyclopean desktop.
  - explicit visibility policies for independent desktop editing versus one combined runtime left/right/cyclopean visible set.
  - combined runtime visibility-frustum construction from left-eye, right-eye, and cyclopean matrices.
  - per-view foveation metadata: requested/effective mode, capability path, fallback reason, gaze source, foveal centers, quality regions, and backend resource key.
- Validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VrViewRenderModeContractTests|FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanCommandChainDataModelTests" --logger "trx;LogFileName=openxr-view-modes-phase2-context.trx"`
  - Passed 78/78.
  - Result copied to `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-view-modes-phase2-context.trx`.
  - Only existing Magick.NET advisory warnings were present.
- Important limitation:
  - the renderer still needs to consume these contexts during collect-visible, frame-op generation, and Vulkan command recording. The current contract slice prevents architectural drift but does not yet remove the existing renderer-global target/resource-planner mutation.

### 2026-06-28 Phase 2 Progress - OpenXR Eye Target Context

- Implemented:
  - `VulkanRenderer.OpenXrEyeRenderTargetContext` with eye index, acquired OpenXR image index, Vulkan image/image-view, format, extent, depth image/memory/view/format/aspect, external target region, command-chain image key, frame-data slot, and resource-planner state index.
  - direct OpenXR eye recording now builds this context before frame-op capture and passes it through primary cache lookup/recording.
  - OpenXR primary cache keys now include image view, depth image/view, frame-data slot, and resource-planner state to prevent cross-eye/image aliasing.
  - trace/failure diagnostics now print the target context identity.
  - the old global swapchain/depth assignment path remains active inside `ApplyOpenXrEyeRenderTargetContext`; this is intentional until dynamic-rendering target setup and resource-planner state stop relying on renderer globals.
- Validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VrViewRenderModeContractTests|FullyQualifiedName~VulkanCommandChainDataModelTests" --logger "trx;LogFileName=openxr-target-context-contract.trx"`
  - Passed 52/52.
  - Result copied to `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-target-context-contract.trx`.
  - Only existing Magick.NET advisory warnings were present.
- Still open:
  - target-begin/dynamic-rendering helpers still resolve the active target through the legacy single-swapchain globals.
  - swapchain layout transitions still depend on `swapChainImages` through lower-level command recording.
  - resource planner scope is still swapped through renderer globals.

### 2026-06-28 Phase 1 Data Model Notes - Direct OpenXR Eye Recording

- Current direct eye call graph:
  - `OpenXRAPI.Vulkan` resolves `VR.ViewRenderMode`.
  - `SequentialViews` records/submits each eye through `TryRenderVulkanEyeToSwapchain`.
  - `SinglePassStereo` calls `TryRenderVulkanEyeSinglePassStereoToSwapchains`, then `VulkanRenderer.TryRenderOpenXrEyeSwapchainsSinglePassStereo`.
  - `ParallelCommandBufferRecording` calls `TryRenderVulkanEyeParallelCommandBufferRecordingToSwapchains`, then `VulkanRenderer.TryRenderOpenXrEyeSwapchainsParallelCommandBufferRecording`.
  - both batch modes currently call `VulkanRenderer.TryRenderOpenXrEyeSwapchains`.
  - the batch helper records left then right through `TryRecordOpenXrEyeSwapchainCommandBuffer`, submits both command buffers together, then publishes or cancels eye-scoped uploads.
  - `TryRecordOpenXrEyeSwapchainCommandBuffer` prepares frame slots, creates `OpenXrEyeRenderTargetContext`, applies the legacy target globals, enters the OpenXR planner scope, captures/sorts frame ops, prepares the planner, builds a command-chain schedule, reuses or records a primary, then moves texture uploads into the eye-local publication buffer.
  - primary recording now passes `OpenXrEyeRenderTargetContext` into `RecordCommandBuffer`; planner state is still global-scoped.
- Immutable frame input:
  - `OpenXrEyeSwapchainRenderRequest.Image`, `Format`, `Extent`, `OpenXrViewIndex`, `OpenXrImageIndex`, `ResourcePlannerStateIndex`, and `EmitFrameOps`.
  - captured/sorted `FrameOp[]` once `CaptureFrameOpsExcludingTextureUploads` returns.
  - computed `frameOpsSignature`, command-chain structural schedule, and `OpenXrEyeRenderTargetContext` after target preparation.
- Per-eye target state:
  - `OpenXrEyeRenderTargetContext` fields: image, image view, depth image/memory/view/format/aspect, extent, frame-data slot, command-chain image key, resource-planner state index, and foveation key.
  - `_openXrSingleSwapchainImages`, `_openXrSingleSwapchainImageViews`, and `_openXrSingleSwapchainImageEverPresented` are still the legacy compatibility slots used by `ApplyOpenXrEyeRenderTargetContext`.
  - `_openXrSwapchainImageViews`, `_openXrCachedDepthTarget`, and `_openXrCachedDepthExtent` are shared caches used to materialize per-eye target resources.
- Per-thread/recording state:
  - `commandBuffer`, `commandBufferAllocated`, `drainedFrameOps`, local target context, local frame ops, schedule, frame-op signature, planner revision, and reuse/record results.
  - `_isRecordingCommandBuffer`, dynamic uniform ring buffer slot state, frame-data slot waits, descriptor frame-slot floor, frame timing/profiler command-buffer state, and command-chain worker scratch are still renderer-owned and not parallel-safe.
- Renderer-global state that must remain single-owner or be split before real parallel recording:
  - `swapChainImages`, `swapChainImageViews`, `swapChainFramebuffers`, `_swapchainImageEverPresented`, `swapChainImageFormat`, and `swapChainExtent`.
  - `_swapchainDepthImage`, `_swapchainDepthMemory`, `_swapchainDepthView`, `_swapchainDepthFormat`, and `_swapchainDepthAspect`.
  - `_openXrExternalSwapchainRenderDepth`, `_openXrExternalSwapchainTargetRegion`, `_openXrExternalSwapchainPrewarmDepth`, and `_synchronousResourceUploadBlockDepth`.
  - `_resourcePlanner`, `_resourceAllocator`, `_barrierPlanner`, `_compiledRenderGraph`, `_lastActiveFrameOpContext`, planner/allocation signatures, failed signatures, fast-path keys, barrier fast-path keys, signature breakdowns, and `_resourcePlannerRevision`.
  - primary command-buffer variant cache, command-chain caches/schedules, descriptor frame data, bind state, current viewport/scissor state, and resource retirement queues.
- Publication state:
  - `_openXrEyeRecordedTextureUploadsForSubmit` is now eye-scoped and only merged after successful batched submit.
  - `_openXrRecordedTextureUploadsForSubmit` remains for mirror paths.
  - recorded upload cancellation/publication queues and completed-retirement flushes remain renderer-owned post-submit work.
- Minimal first boundary chosen:
  - context-owned target recording first, because it removes `swapChainImages`/depth reads from the dynamic-rendering target helpers while preserving the old apply/restore path.
  - planner split next, because `EnterOpenXrResourcePlannerScope` still swaps the global planner/allocation/barrier fields and is the next hard blocker to true left/right worker recording.

### 2026-06-28 Phase 5 Partial Progress - Direct Eye Upload Publication Isolation

- Implemented:
  - direct OpenXR swapchain eye recording now uses two eye-scoped upload publication buffers instead of the shared OpenXR upload list.
  - single-eye direct rendering publishes/cancels only that eye's buffer.
  - batched direct eye rendering clears both buffers up front, records left/right into their own buffers, and publishes the buffers only after the batched submit succeeds.
  - direct eye batch failure cancels the eye-scoped buffers instead of publishing them.
- Validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanCommandChainDataModelTests|FullyQualifiedName~VrViewRenderModeContractTests" --logger "trx;LogFileName=openxr-upload-isolation-contract.trx"`
  - Passed 53/53.
  - Result copied to `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-upload-isolation-contract.trx`.
  - Only existing Magick.NET advisory warnings were present.
- Still open:
  - OpenXR mirror render/publish paths still use the older shared OpenXR upload list.
  - failure-mode tests for left-record failure, right-record failure, submit failure, and device-lost are source/data-model coverage only so far; live fault injection is still needed before Phase 5 can be closed.

### 2026-06-28 Phase 3 Contract Progress - Planner Context Identity

- Implemented:
  - `OpenXrViewResourcePlannerContextKey` derived from `OpenXrEyeRenderTargetContext`.
  - `OpenXrEyeRenderTargetContext` now carries `FoveationResourceKey`.
  - OpenXR primary cache keys include the foveation key, frame slot, resource-planner state, image view, and depth target identity.
- Validation:
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VulkanCommandChainDataModelTests|FullyQualifiedName~VrViewRenderModeContractTests" --logger "trx;LogFileName=openxr-planner-context-contract.trx"`
  - Passed 53/53.
  - Result copied to `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-planner-context-contract.trx`.
  - Only existing Magick.NET advisory warnings were present.
- Still open:
  - `EnterOpenXrResourcePlannerScope` still swaps renderer-wide resource planner fields during command recording.
  - allocator-backed texture/FBO resolution still needs explicit view/planner context input before true parallel recording can be enabled.

### 2026-06-28 Build Gate

- Validation:
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
  - Passed.
  - Build log saved to `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-openxr-view-contexts.log`.
  - No new compiler warnings were emitted from touched files; the warnings are existing Magick.NET NuGet advisory warnings.

### 2026-06-28 Phase 0.5 Progress - Serial View-State Cache Boundary

- Problem found:
  - The 90-frame Vulkan/Monado sequential smoke passed at the OpenXR layer but showed steady-state allocator-backed render-resource churn: 264 `Physical group image handle changed` warnings and 340 `Rebuilding framebuffer` warnings.
  - This matched the suspected flicker/perf architecture issue: desktop/left/right serial context switches were making one logical `VkImageBackedTexture` wrapper and one logical `VkFrameBuffer` wrapper chase different per-context physical images/views.
- Implemented:
  - `VkImageBackedTexture` now caches primary and attachment image views per physical group/image handle when switching resource-planner contexts.
  - Same-group image reallocations still retire stale views; serial context switches restore cached views without treating the previous context as destroyed.
  - `VkFrameBuffer` now caches attachment variants keyed by the resolved attachment views/signatures/extents/layers and activates an existing variant on serial desktop/eye context switches instead of destroying/rebuilding the wrapper.
  - Added source-contract tests for allocator-backed texture view cache and framebuffer attachment-variant cache boundaries.
- Validation:
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore`
    - Passed; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-view-cache.log`.
  - `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~VrViewRenderModeContractTests|FullyQualifiedName~OpenXrTimingPipelineContractTests|FullyQualifiedName~VulkanCommandChainDataModelTests" --logger "trx;LogFileName=openxr-view-modes-view-cache-combined.trx"`
    - Passed 82/82.
    - Result copied to `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-view-modes-view-cache-combined.trx`.
  - `Tools\OpenXR\Run-OpenXrMonadoSmoke.ps1 -Renderer Vulkan -SmokeFrames 90 -NoBuild -StartService -SkipAllocationAudit`
    - Passed; summary: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/openxr-smoke-vulkan-sequential-view-cache-summary.json`.
    - Runtime log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_16-40-50_pid37904`.
    - Submitted frames: 89; no-layer frames: 1; EndFrame failures: 0; per-frame allocation bytes: 0.
    - Log scan report: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/sequential-view-cache-log-scan.json`.
    - Churn counts after fix: `Physical group image handle changed` = 0, `Rebuilding framebuffer` = 0, `DispatchCompute emitted with invalid render-graph pass index` = 0, `VUID-` = 0, descriptor-binding/skipped-draw matches = 0.
- Still open:
  - This is a serial context-switch cache boundary, not full explicit context injection. Frame-op generation and OpenXR target setup still need to pass view/planner context directly before true parallel recording can safely write command buffers concurrently.
  - This smoke did not emit `profiler-render-stats.ndjson`; CPU/GPU p95 evidence still needs a profiling-enabled run.
  - MCP/F5 visual stability still needs explicit viewport/eye captures before closing the flicker investigation.

### 2026-06-28 Mode Dispatch Progress - Explicit Vulkan Paths

- Implemented:
  - `OpenXRAPI.Vulkan` now dispatches `SinglePassStereo` and `ParallelCommandBufferRecording` through distinct named Vulkan paths instead of collapsing both modes into the same unlabelled batch helper.
  - `VulkanRenderer` exposes separate `TryRenderOpenXrEyeSwapchainsSinglePassStereo` and `TryRenderOpenXrEyeSwapchainsParallelCommandBufferRecording` methods with separate profile scopes.
  - The parallel-mode path now emits an explicit diagnostic that primary recording is still serialized until the remaining renderer-global recording state is removed. This prevents a silent false claim of true parallel primary recording while keeping the mode selectable for validation.
- Validation:
  - Focused tests passed 83/83; TRX copied to `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-view-modes-dispatch-combined.trx`.
  - `VR.ViewRenderMode=SinglePassStereo` 90-frame Vulkan/Monado smoke passed:
    - summary: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/openxr-smoke-vulkan-singlepass-summary.json`
    - session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_16-46-17_pid3228`
    - submitted frames: 88; no-layer frames: 2; EndFrame failures: 0; per-frame allocation bytes: 0.
  - `VR.ViewRenderMode=ParallelCommandBufferRecording` 90-frame Vulkan/Monado smoke passed:
    - summary: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/openxr-smoke-vulkan-parallel-mode-summary.json`
    - session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_16-46-53_pid34736`
    - submitted frames: 88; no-layer frames: 2; EndFrame failures: 0; per-frame allocation bytes: 0.
  - Mode log scan: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/mode-smoke-log-scan.json`.
    - Both mode runs had zero `Physical group image handle changed`, zero `Rebuilding framebuffer`, zero `VUID-`, zero descriptor/skipped-draw matches, and zero EndFrame matches.
    - `SinglePassStereo selected` appeared only in the single-pass run; `ParallelCommandBufferRecording path is selected` appeared only in the parallel-mode run.
- Still open:
  - `SinglePassStereo` is currently a compatibility path over per-eye OpenXR swapchains, not a full Vulkan multiview/subpass stereo implementation.
  - `ParallelCommandBufferRecording` is mode-separated and smoke-tested, but true left/right primary recording remains serialized until target begin/layout/resource-planner/descriptor recording state is fully context-owned.

### 2026-06-28 Foveation Capability And Smoke Matrix Progress

- Implemented:
  - unit-testing env overrides for `XRE_UNIT_TEST_VR_FOVEATION_MODE`, `XRE_UNIT_TEST_VR_FOVEATION_QUALITY_PRESET`, and `XRE_UNIT_TEST_VR_FOVEATION_REQUIRE_REQUESTED`.
  - bootstrap propagation from `VR.Foveation` into render settings so foveated view-set intent is visible at startup.
  - Vulkan capability detection and logging for `VK_KHR_fragment_shading_rate` and `VK_EXT_fragment_density_map`.
  - OpenXR optional extension reporting for foveation and quad-view runtime hints.
  - OpenXR foveation requested/effective-mode summaries for smoke output.
  - OpenGL explicit unsupported diagnostics when fixed/eye-tracked foveation is requested without a tested backend path.
- Harness fixes:
  - added `-SkipLoaderPreflight` to `Tools/OpenXR/Run-OpenXrMonadoSmoke.ps1` to avoid intermittent nested PowerShell/native OpenXR loader heap corruption before editor launch.
  - delete stale smoke summary JSON before each launch so failed editor launches cannot inherit an old passing summary.
- Validation:
  - `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore` passed after the foveation capability changes; logs are under `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-foveation-capabilities.log`.
  - focused rendering contract tests passed 84/84; TRX copied to `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-view-modes-foveation-capabilities.trx`.
  - Vulkan fixed-foveation smoke passed for all view render modes:
    - `reports/openxr-smoke-vulkan-sequential-fixed-foveation-summary.json`
    - `reports/openxr-smoke-vulkan-singlepass-fixed-foveation-summary.json`
    - `reports/openxr-smoke-vulkan-parallel-fixed-foveation-summary.json`
  - all three Vulkan fixed-foveation runs reported `FoveationEffectiveMode=Fixed`, `FoveationCapabilityPath=VulkanFragmentShadingRate`, no EndFrame failures, and zero per-frame allocation bytes.
  - OpenGL `SequentialViews` and `SinglePassStereo` smoke runs passed with foveation off:
    - `reports/openxr-smoke-opengl-sequential-summary.json`
    - `reports/openxr-smoke-opengl-singlepass-summary.json`
  - OpenGL `ParallelCommandBufferRecording` now rejects visibly with exit code 23 and summary `reports/openxr-smoke-opengl-parallel-unsupported-summary.json`.
  - OpenGL fixed foveation with `RequireRequested=true` rejects visibly with exit code 22 and summary `reports/openxr-smoke-opengl-fixed-foveation-require-summary.json`.
  - Vulkan collect-visible pacing smoke passed for all three view render modes:
    - `reports/openxr-smoke-vulkan-collectvisible-sequential-summary.json`
    - `reports/openxr-smoke-vulkan-collectvisible-singlepass-summary.json`
    - `reports/openxr-smoke-vulkan-collectvisible-parallel-summary.json`
  - log scans saved to `reports/foveation-smoke-log-scan.json` and `reports/collectvisible-smoke-log-scan.json`; both scans showed zero steady-state physical-image ping-pong, FBO rebuild loops, invalid dispatch warnings, descriptor/skipped-draw matches, EndFrame errors, VUIDs, or validation errors in the validated Vulkan runs.
- Still open:
  - foveation resources are not yet owned as explicit per-view shading-rate/density attachments in the Vulkan resource planner.
  - `ParallelCommandBufferRecording` still reports that primary recording is serialized; the next implementation target is removing the remaining global target/planner mutation from primary recording.
  - no MCP/F5 visual capture has closed the editor-origin or left-eye flicker investigation yet.
  - no fresh profiling-enabled run has produced GPU/render-thread p95 evidence for the 120 Hz acceptance target.

### 2026-06-28 Phase 2 Progress - Context-Owned Dynamic Rendering Target

- Implemented:
  - `RecordCommandBuffer` now accepts an optional `OpenXrEyeRenderTargetContext`.
  - the recorder resolves a `SwapchainRecordingTarget` once per primary recording and uses that immutable target for OpenXR color image, color view, depth image/view, formats, extent, depth aspect, and initial layout state.
  - dynamic-rendering swapchain begin, swapchain color transitions, unwritten-present transitions, dynamic UI overlay rendering, secondary inheritance format selection, and swapchain clears now consume the resolved target instead of reading `swapChainImages`/`swapChainImageViews`/depth fields directly.
  - the OpenXR direct eye primary path passes its prepared `targetContext` into `RecordCommandBuffer`.
  - legacy desktop/swapchain recording still resolves through the existing renderer fields, so this is a behavior-preserving boundary before removing the old global apply/restore path.
- Validation:
  - editor build passed after the refactor; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-context-target-recording.log`.
  - focused rendering tests passed 85/85; TRX: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-context-target-recording.trx`.
  - 90-frame Vulkan/Monado smoke with `VR.ViewRenderMode=ParallelCommandBufferRecording` passed after the refactor:
    - summary: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/openxr-smoke-vulkan-parallel-context-target-summary.json`
    - session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_17-26-09_pid47812`
    - submitted frames: 88; no-layer frames: 2; EndFrame failures: 0; per-frame allocation bytes: 0.
    - log scan: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/parallel-context-target-log-scan.json`
    - scan counts: zero physical-image handle churn, FBO rebuild loops, invalid dispatch warnings, descriptor/skipped-draw matches, EndFrame matches, VUIDs, validation errors, or OpenXR eye render failures.
- Still open:
  - `ApplyOpenXrEyeRenderTargetContext` still populates the legacy globals so lower-level and fallback paths can keep running while planner state is split.
  - `EnterOpenXrResourcePlannerScope` still swaps renderer-wide planner/allocation/barrier fields during recording.
  - true parallel primary recording remains gated until planner state, command-buffer ownership, descriptor frame data, and upload publication can all be recorded without shared mutable state.

### 2026-06-28 Phase 5 Progress - Direct Eye Upload Publication Tests

- Implemented/confirmed:
  - direct OpenXR eye rendering uses eye-scoped upload publication buffers.
  - batched eye rendering clears the eye buffers before recording, moves recorded uploads into the current eye buffer, publishes both only after a completed submit, and cancels/clears them on failed record/submit paths.
  - device-lost handling avoids invoking normal cancellation after the device is already lost.
- Validation:
  - focused rendering tests passed 86/86 after adding source-contract coverage for left record failure, right record failure, submit failure, device-lost guard, successful batched submit, and command-buffer cleanup.
  - TRX copied to `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-context-target-upload-tests.trx`.
- Still open:
  - OpenXR mirror render/publish helpers still use `_openXrRecordedTextureUploadsForSubmit`; this is not on the direct-eye parallel recording path, but it should be split before mirror paths are made parallel.

### 2026-06-28 Phase 3 Progress - OpenXR Planner State Keyed By View Target

- Implemented:
  - OpenXR direct-eye planner runtime state is now stored in a `Dictionary<OpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState>` instead of a two-slot eye array.
  - direct eye recording enters the planner scope with `OpenXrViewResourcePlannerContextKey.FromTarget(targetContext)`.
  - the planner key includes resource planner state index, OpenXR view index, acquired image index, command-chain image key, frame-data slot, and foveation resource key.
  - legacy integer planner scopes remain for prewarm/mirror compatibility.
  - planner context enter/leave diagnostics are available under existing OpenXR Vulkan tracing.
  - teardown destroys each keyed OpenXR planner state with a label that includes planner, eye, image index, command key, frame slot, and foveation key.
- Validation:
  - editor build passed; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-openxr-planner-key.log`.
  - focused rendering tests passed 87/87; TRX: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-planner-key-tests.trx`.
  - 90-frame Vulkan/Monado smoke with `VR.ViewRenderMode=ParallelCommandBufferRecording` passed:
    - summary: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/openxr-smoke-vulkan-parallel-planner-key-summary.json`
    - session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_17-32-16_pid44448`
    - submitted frames: 88; no-layer frames: 2; EndFrame failures: 0; per-frame allocation bytes: 0.
    - log scan: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/parallel-planner-key-log-scan.json`
    - scan counts: zero physical-image handle churn, FBO rebuild loops, invalid dispatch warnings, descriptor/skipped-draw matches, EndFrame matches, VUIDs, validation errors, or OpenXR eye render failures.
- Still open:
  - `EnterOpenXrResourcePlannerScope` still restores keyed planner state into renderer-global planner/allocation/barrier fields while recording.
  - allocator-backed texture/FBO resolution still relies on the active global allocator once the keyed state has been restored.
  - true worker parallelism still requires an explicit recording/planner object that can be used without mutating the renderer's active planner fields.

### 2026-06-28 Phase 4 Progress - Eye-Owned OpenXR Primary Command Pools

- Implemented:
  - `CommandBufferCacheVariant` now records the owning primary and dynamic-secondary command pools.
  - OpenXR direct-eye primary variants allocate from lazily created per-eye command pools (`OpenXR eye primary command pool[0/1]`) instead of the shared desktop command pool.
  - mirror variants keep the shared-pool overload until mirror paths are split separately.
  - OpenXR primary cache teardown frees command buffers through the recorded owner pool and then destroys the eye command pools, including the device-lost path where individual frees are skipped.
  - primary cache identity already includes eye index, OpenXR image index, Vulkan image/view, depth target identity, resource-planner state, frame-data slot, foveation key, frame-op signature, planner revision, command-chain schedule signature, and command-chain primary group handle/generation signature.
- Validation:
  - editor build passed; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-openxr-eye-command-pools.log`.
  - focused rendering tests passed 88/88; TRX: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-eye-command-pools-tests.trx`.
  - 90-frame Vulkan/Monado smoke with `VR.ViewRenderMode=ParallelCommandBufferRecording` passed:
    - summary: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/openxr-smoke-vulkan-parallel-eye-command-pools-summary.json`
    - session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_17-39-03_pid34432`
    - submitted frames: 88; no-layer frames: 2; EndFrame failures: 0; per-frame allocation bytes: 0.
    - log scan: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/parallel-eye-command-pools-log-scan.json`
    - scan counts: zero physical-image handle churn, FBO rebuild loops, invalid dispatch warnings, descriptor/skipped-draw matches, EndFrame matches, VUIDs, validation errors, OpenXR eye render failures, or command-pool lifetime validation matches.
- Still open:
  - recording still mutates renderer-global bind/profiler/descriptor/planner state, so eye-owned pools alone do not make true parallel primary recording safe.

### 2026-06-28 Phase 4 Progress - OpenXR Primary Cache Locking

- Implemented:
  - all `_openXrPrimaryCommandBufferVariants` dirty, lookup/reuse, create, mirror reuse, and teardown accesses are now guarded by `_openXrPrimaryCommandBufferVariantsLock`.
  - the lock is scoped to primary cache access and does not wrap frame-op capture, resource planning, or primary command recording.
- Validation:
  - editor build passed; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-openxr-primary-cache-lock.log`.
  - focused rendering tests passed 89/89; TRX: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-primary-cache-lock-tests.trx`.
  - 90-frame Vulkan/Monado smoke with `VR.ViewRenderMode=ParallelCommandBufferRecording` passed:
    - summary: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/openxr-smoke-vulkan-parallel-primary-cache-lock-summary.json`
    - session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_17-45-42_pid40660`
    - submitted frames: 88; no-layer frames: 2; EndFrame failures: 0; per-frame allocation bytes: 0.
    - log scan: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/parallel-primary-cache-lock-log-scan.json`
    - scan counts: zero physical-image handle churn, FBO rebuild loops, invalid dispatch warnings, descriptor/skipped-draw matches, EndFrame matches, VUIDs, validation errors, OpenXR eye render failures, or command-pool lifetime validation matches.
- Still open:
  - the cache is safe to access, but the command recording code behind a cache miss is still not parallel-safe because it mutates renderer-global state.

### 2026-06-28 Phase 7 Progress - Submit/Fence Diagnostics

- Implemented:
  - recorded OpenXR eye command buffers now carry the frame-data slot used for recording.
  - existing `OpenXrVulkanTrace` queue-submit and fence-wait timing diagnostics remain in `SubmitAndWaitOpenXrCommandBuffers`.
  - direct single-eye and batched-eye submit paths now trace completed frame slots, published upload counts, cancelled upload counts, and non-image retirement flush slot counts under `OpenXrVulkanTrace`.
  - upload publication and retirement flushing remain after successful fence completion; cancellation remains on failed/incomplete submit when the device is not already lost.
- Validation:
  - editor build passed; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-openxr-submit-diagnostics.log`.
  - focused rendering tests passed 90/90; TRX: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-submit-diagnostics-tests.trx`.
- Decision:
  - keep the current submit-and-wait path until true parallel recording is stable and measured. Timeline/fence recycling can be evaluated afterward with these diagnostics as the baseline.

### 2026-06-28 Phase 4 Progress - Thread-Local Primary Recording Scratch

- Implemented:
  - primary command-buffer recording scratch that was previously stored on the renderer is now owned by a per-thread `CommandBufferRecordingScratch`.
  - moved swapchain writer dictionaries, pipeline-name scratch, mesh draw slot scratch, FBO layout tracking, secondary bucket lookup scratch, writer sort scratch, and summary `StringBuilder` behind the thread-local scratch object.
  - primary-owned secondary command-buffer recording now receives the current record's executed-secondary handle set instead of reaching back to renderer-global scratch.
- Validation:
  - editor build passed; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-threadlocal-scratch.log`.
  - focused rendering tests passed 91/91; TRX: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-threadlocal-scratch-tests.trx`.
- Still open:
  - this removes recording scratch races, but it does not by itself make eye primary recording fully parallel-safe. Planner scope swapping, active renderer state, descriptor frame slots, profiler query slots, and some command-chain caches still need explicit ownership or locking before enabling worker dispatch.

### 2026-06-28 Phase 6 Progress - Prepared Eye Inputs Before Worker Recording

- Regression fixed before continuing:
  - the first worker-dispatch smoke regressed Forward+ compute recording with `Skipping unresolved StorageBuffer binding 'ForwardPlusLocalLightsBuffer'` and `Skipping compute dispatch` warnings.
  - root cause: worker-side frame-op emission called Vulkan resource/buffer readiness paths from non-render threads; `VkDataBuffer.EnsureStorageAllocatedForGpuUse` intentionally deferred SSBO allocation there.
- Implemented:
  - split direct OpenXR eye recording into `TryPrepareOpenXrEyeSwapchainCommandBuffer` and `TryRecordPreparedOpenXrEyeSwapchainCommandBuffer`.
  - prepare runs before worker dispatch and owns frame-slot wait, target-context setup, frame-op capture, resource-planner prep, descriptor/image wrapper refresh, and draw-resource prewarm.
  - worker inputs now carry immutable prepared eye data: target context, sorted frame ops, frame-op signature, planner revision, and command-chain schedule.
  - the bounded left/right worker scheduler records prepared inputs, captures worker timings/thread IDs, joins both workers before submit, and keeps deterministic failure behavior.
- Validation:
  - editor build passed; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-prepared-eye-workers.log`.
  - focused rendering tests passed 78/78; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-prepared-eye-workers-tests.log`.
  - 90-frame Vulkan/Monado smoke with `VR.ViewRenderMode=ParallelCommandBufferRecording` passed:
    - summary: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/openxr-smoke-vulkan-parallel-prepared-eye-workers-summary.json`
    - session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_18-15-05_pid44272`
    - submitted frames: 88; no-layer frames: 2; EndFrame failures: 0; per-frame allocation bytes: 0.
    - log scan: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/parallel-prepared-eye-workers-log-scan.json`
    - scan counts: `DescriptorFailure=0`, `PhysicalImageChurn=0`, `FboRebuild=0`, `EyeRenderFailed=0`, `EndFrame=0`; worker completion traces: 88; prepared-eye traces: 176.
- Still open:
  - workers still enter a serialized planner/global-state gate while recording. This is intentional until planner state, descriptor frame state, active target state, and profiler state are explicit per-view/per-recording objects.
  - this is not yet accepted as true parallel primary recording; it is the correctness boundary that prevents worker dispatch from doing renderer-owned per-frame initialization.

### 2026-06-28 Phase 2.6 Progress - Foveation Context In Eye Recording Inputs

- Implemented:
  - `OpenXrEyeSwapchainRenderRequest` now carries an immutable `ViewFoveationContext`.
  - OpenXR Vulkan direct and batched eye requests build per-eye foveation contexts from the effective backend/runtime foveation resolution.
  - `OpenXrEyeRenderTargetContext.FoveationResourceKey` now comes from `request.Foveation.BackendResourceKey` instead of being hardcoded to zero.
  - the existing planner key and primary command-buffer cache key already include `FoveationResourceKey`, so fixed/eye-tracked foveation resource identity is now part of eye planner/cache identity.
- Validation:
  - editor build passed; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-foveation-context-threading.log`.
  - focused rendering/view-mode tests passed 92/92; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/openxr-foveation-context-threading-tests.log`.
  - 90-frame Vulkan/Monado smoke with `VR.ViewRenderMode=ParallelCommandBufferRecording` and `VR.Foveation.Mode=Fixed` passed:
    - summary: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/openxr-smoke-vulkan-parallel-fixed-foveation-context-summary.json`
    - session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_18-21-57_pid3852`
    - effective foveation: `Fixed` via `VulkanFragmentShadingRate`; submitted frames: 88; EndFrame failures: 0; per-frame allocation bytes: 0.
    - log scan: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/parallel-fixed-foveation-context-log-scan.json`
    - scan counts: non-zero foveation-key traces: 1062; zero descriptor/skipped-dispatch matches, physical-image churn, FBO rebuild loops, eye render failures, or EndFrame failures.
- Still open:
  - Vulkan fragment-shading-rate or density-map attachment images are not created yet; this change gives them per-view/planner identity once the attachment resources are added.

### 2026-06-28 Phase 10 Progress - SinglePassStereoVR Runtime Shim Cleanup

- Implemented:
  - runtime render decisions that still read `RenderVRSinglePassStereo` now read `VrViewRenderMode == EVrViewRenderMode.SinglePassStereo` directly.
  - the old boolean remains only as a compatibility shim for legacy settings/bootstrap migration.
- Validation:
  - editor build passed; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-singlepass-shim-cleanup.log`.
  - view-mode contract tests passed 14/14; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/vr-view-render-mode-shim-cleanup-tests.log`.

### 2026-06-28 Phase 8 Progress - Collect-Visible Pacing Retest

- Validation:
  - 90-frame Vulkan/Monado smoke with `XRE_OPENXR_RENDER_PACING_MODE=CollectVisibleThread` and `VR.ViewRenderMode=ParallelCommandBufferRecording` passed after the prepared-eye-input split.
  - summary: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/openxr-smoke-vulkan-collectvisible-parallel-prepared-summary.json`
  - session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_18-29-17_pid47948`
  - submitted frames: 88; no-layer frames: 2; EndFrame failures: 0; per-frame allocation bytes: 0.
  - log scan: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/reports/collectvisible-parallel-prepared-log-scan.json`
  - scan counts: worker completions: 88; prepared-eye traces: 176; collect-visible traces: 532; zero descriptor/skipped-dispatch matches, physical-image churn, FBO rebuild loops, or eye render failures.
- Still open:
  - render-thread CPU cost before/after collect-visible integration still needs a profiler stats dump; `profiler-render-stats.ndjson` was not emitted by this smoke run.

### 2026-06-28 Preview-Copy Regression Fix And MCP Visual Gate

- Problem found:
  - after threading `VrCopyEyePreviewTextures` into the runtime host services, the MCP OpenXR eye preview captures were still black.
  - the copy helper was capable of copying, but bootstrap did not propagate `PreviewVRStereoViews` into `Engine.Rendering.Settings.VrCopyEyePreviewTextures` before the OpenXR/Vulkan path started.
- Implemented:
  - `BootstrapRenderSettings.Apply` now sets `VrCopyEyePreviewTextures = settings.PreviewVRStereoViews`.
  - bootstrap and Unit Testing World render-toggle logs now include `VrCopyEyePreviewTextures` so future MCP runs show whether preview-copy was requested.
  - `OpenXrTimingPipelineContractTests` now guards both bootstrap and editor toggle propagation/logging.
- Validation:
  - editor build passed; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-bootstrap-preview-copy.log`.
  - focused OpenXR/host-service tests passed 38/38; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/bootstrap-preview-copy-tests.log`.
  - MCP editor run: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_19-00-31_pid44256`.
  - bootstrap log confirms `VrCopyEyePreviewTextures=True`.
  - Vulkan log confirms direct swapchain copy for both eyes with `previewCopied=True`.
  - left preview: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/OpenXRPreview_LeftEye_20260628_190125.png`, stats `896x1007`, `maxRgb=1`, `averageRgb=0.074729726`.
  - right preview: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/OpenXRPreview_RightEye_20260628_190125.png`, stats `896x1007`, `maxRgb=1`, `averageRgb=0.074729726`.
  - final postprocess capture: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/RenderPipeline_PostProcessOutputTexture_20260628_190126.png`, stats `1920x1080`, `maxRgb=255.875`, `averageRgb=94.9118`.
- Still open:
  - the desktop viewport screenshot from MCP is pure white even after focusing the editor camera on `Sponza`.
  - the desktop `PostProcessOutputTexture` capture remains sky-only after focus despite render state showing the editor camera moved to `34.395214,21.634937,21.98929` and the pipeline recording 115 ops.
  - do not check the desktop visual acceptance box until the viewport/screenshot path or desktop visible/render path is isolated and fixed.

### 2026-06-28 Desktop Visual Regression Gate - Failed Render-Snapshot Publish Attempt

- Problem investigated:
  - desktop editor visible commands were present after focusing `Sponza`, but desktop `AlbedoOpacity` stayed empty while OpenXR left/right previews rendered valid scene imagery.
  - Vulkan logs repeatedly showed `DrawMetadataBuffer` queued with `ready=False` around `IndirectDrawSnapshot` upload blocking.
- Attempted:
  - added a render-snapshot buffer publish gate in `GPURenderPassCollection.Render`/`GPUScene` to force CPU-side GPU scene writes to publish before GPU cull/indirect consumption.
- Regression introduced:
  - desktop pipeline captures became all black:
    - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/publish-scene-snapshot-targets/RenderPipeline_FinalPostProcessOutputTexture_20260628_193616.png`
    - stats: `minRgb=0`, `maxRgb=0`, `averageRgb=0`.
  - OpenXR previews stayed healthy, so the regression was desktop-pipeline specific.
- Fixed before continuing:
  - removed the publish-gate change immediately and rebuilt.
  - validation after revert:
    - build passed with only existing Magick.NET advisory warnings.
    - session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_19-38-14_pid23584`
    - desktop post-process was nonblack again:
      - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/after-publish-gate-revert/RenderPipeline_FinalPostProcessOutputTexture_20260628_193853.png`
    - left/right preview captures remained nonblack:
      - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/after-publish-gate-revert/OpenXRPreview_LeftEye_20260628_193854.png`
      - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/after-publish-gate-revert/OpenXRPreview_RightEye_20260628_193855.png`
- Current conclusion:
  - publishing from inside GPU pass recording is the wrong boundary; the remaining issue must be handled at the command-chain/frame-op preparation boundary or in the specific desktop render-target/view state path.

### 2026-06-28 Desktop Mirror Composition Regression - Direct Swapchain Path

- Problem investigated:
  - fresh MCP captures from the live Vulkan/Monado editor showed:
    - desktop viewport screenshot was pure white:
      - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/fresh-desktop-regression-check/Screenshot_20260628_200229.png`
    - desktop `AlbedoOpacity` was all black and final post-process was sky-only:
      - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/fresh-desktop-regression-check/RenderPipeline_AlbedoOpacity_20260628_200230.png`
      - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/fresh-desktop-regression-check/RenderPipeline_FinalPostProcessOutputTexture_20260628_200229.png`
    - OpenXR left/right preview textures were healthy:
      - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/fresh-desktop-regression-check/OpenXRPreview_LeftEye_20260628_200230.png`
      - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/fresh-desktop-regression-check/OpenXRPreview_RightEye_20260628_200231.png`
- Root-cause direction:
  - `XRWindow` skips normal desktop viewport rendering while OpenXR is active when `VrMirrorComposeFromEyeTextures=true` and calls `TryRenderDesktopMirrorComposition`.
  - Vulkan direct-swapchain eye rendering publishes fresh eye images to `OpenXRPreviewLeftEyeColor` / `OpenXRPreviewRightEyeColor`.
  - Vulkan desktop mirror composition still blits `_viewportMirrorFbo`, which is populated by the older mirror-FBO path, not by direct swapchain rendering.
  - Result: the HMD/preview path is healthy, while the desktop window presents a stale/blank mirror target.
- Fix being attempted:
  - in direct Vulkan OpenXR swapchain publish, create the existing desktop mirror target when mirror composition is active and copy the left-eye swapchain image into `_viewportMirrorColor` in addition to the preview texture.
  - keep this as a regression fix only; the architectural cyclopean desktop view and shared visible-set contract still need their explicit view-context implementation before the final acceptance boxes are checked.

### 2026-06-28 Desktop Vulkan Screenshot Readback Context Fix

- Re-checked the current Unit Testing World settings and confirmed the active repro used `VR.AllowDesktopEditing=true` and `VR.PreviewStereoViews=true`, so `VrMirrorComposeFromEyeTextures=false`. The latest white desktop capture was therefore not the mirror-composition path.
- Evidence before the fix:
  - session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_20-22-15_pid24604`
  - desktop screenshot: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/desktop-state-snapshot/Screenshot_20260628_202254.png`, pure white
  - desktop final post-process texture: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/desktop-state-snapshot/RenderPipeline_FinalPostProcessOutputTexture_20260628_202254.png`, nonblank sky
  - desktop render state showed the editor viewport had 28 active commands, including 25 enabled `OpaqueDeferred` mesh commands, so high-level desktop collect-visible was not empty.
- Root cause:
  - `VPRC_RenderToWindow` tracked the texture/FBO that was presented to the desktop window.
  - Vulkan screenshot readback later resolved that tracked source without the `FrameOpContext`/resource-planner state that owned the desktop pipeline resources.
  - Pipeline texture capture already used a scoped planner readback; screenshot readback did not, so it could resolve stale or wrong physical resources after OpenXR eye contexts had also rendered.
- Implemented:
  - `VulkanRenderer.TrackWindowPresentSource` now stores the current `FrameOpContext` with the tracked desktop present source.
  - Vulkan screenshot/pixel readback now enters a frame-op resource-planner readback scope before resolving the tracked desktop present FBO/texture.
- Validation after the fix:
  - editor build passed; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-window-present-readback-context.log`
  - session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_20-27-56_pid41928`
  - desktop screenshot is no longer the stale white 1.4 KB image:
    - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/window-present-readback-context/Screenshot_20260628_202836.png`
  - desktop final post-process texture is nonblank:
    - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/window-present-readback-context/RenderPipeline_FinalPostProcessOutputTexture_20260628_202836.png`
  - left/right OpenXR previews remain nonblank in the same run:
    - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/window-present-readback-context/OpenXRPreview_LeftEye_20260628_202837.png`
    - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/mcp-captures/window-present-readback-context/OpenXRPreview_RightEye_20260628_202838.png`
  - `profiler-render-stats.ndjson` was emitted for this run at the session path above.
- Still open:
  - the manual desktop camera pose used for this check points mostly at sky; this validates desktop present/readback correctness, not the full editor-origin flicker fix.
  - OpenXR true parallel recording still has the serialized planner/global-state gate and is not yet accepted as true parallel.

### 2026-06-28 Thread-Scoped Prepared Eye Recording

- Implemented:
  - removed the serialized OpenXR eye worker gate from `TryRecordOpenXrEyeSwapchainCommandBufferFromWorker`.
  - prepared eye recording now enters a worker-local Vulkan render-state tracker and a worker-local OpenXR resource-planner runtime-state scope.
  - the prepared worker path no longer calls `ApplyOpenXrEyeRenderTargetContext`; it passes `OpenXrEyeRenderTargetContext` through to primary recording and swapchain target resolution.
  - `ActiveState`, `ResourceAllocator`, `BarrierPlanner`, `CompiledRenderGraph`, and `ResourcePlannerRevision` now resolve against thread-local state when a worker scope is active.
  - OpenXR planner states are protected by `_openXrResourcePlannerStatesLock`, and teardown snapshots/clears the dictionary before destroying keyed planner resources.
  - source-contract tests now assert the worker path contains no serialized planner/global-state lock and uses thread-scoped prepared primary recording.
- Validation:
  - `VulkanCommandChainDataModelTests` passed 52/52; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vulkan-command-chain-data-model-thread-scoped-eye-record.log`.
  - `VrViewRenderModeContractTests` passed 17/17; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vr-view-render-mode-contracts-thread-scoped-eye-record.log`.
  - editor build passed; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-thread-scoped-eye-record.log`.
  - warnings were existing Magick.NET package advisories only.
- Remaining risk:
  - target setup in `TryPrepareOpenXrEyeSwapchainCommandBuffer` still uses the transitional global swapchain/depth fields on the render thread before worker dispatch.
  - frame-op planner switching inside one recorded eye still has renderer-level active-key fields; current prepared-eye validation covers the one-planner-state-per-eye path, and a future mixed-pipeline eye frame should get an explicit worker-local switching state object before that path is accepted as fully general.

### 2026-06-28 OpenXR Eye Target Global Mutation Removal

- Implemented:
  - removed `ApplyOpenXrEyeRenderTargetContext` and the `_openXrSingleSwapchain*` fake one-slot swapchain scaffolding.
  - `TryPrepareOpenXrEyeSwapchainCommandBuffer` now builds `OpenXrEyeRenderTargetContext` without swapping `swapChainImages`, `swapChainImageViews`, `swapChainFramebuffers`, `_swapchainImageEverPresented`, `swapChainImageFormat`, `swapChainExtent`, or the swapchain depth fields.
  - OpenXR eye prewarm now uses the external target region and a local render-state tracker instead of writing dummy swapchain/depth fields.
  - resource-planner fallback dimensions now come from `TryGetExternalSwapchainTargetRegion` while an external target scope is active, so planning no longer depends on mutating `swapChainExtent` for eye targets.
- Validation:
  - `VulkanCommandChainDataModelTests` passed 52/52 after removing the prep-time target swap; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vulkan-command-chain-data-model-no-eye-prep-swap.log`.
  - `VulkanCommandChainDataModelTests` passed 52/52 after removing the fake one-slot swapchain fields; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vulkan-command-chain-data-model-remove-openxr-single-slot.log`.
- Remaining risk:
  - Superseded by the 2026-06-28 thread-local OpenXR eye planner-prep slice below: eye preparation and prewarm no longer use the old global-swapping planner scope.
  - mirror-framebuffer helpers still use their own legacy paths and need a separate pass before the mirror/cyclopean desktop architecture is final.

### 2026-06-28 Thread-Local OpenXR Eye Planner Preparation

- Implemented:
  - added active resource-planner runtime accessors so planner, allocator, barrier planner, compiled graph, signatures, fast-path keys, allocation-failure retry state, and last active frame-op context update the thread-local planner state when a planner scope is active.
  - changed OpenXR eye preparation, eye mirror recording, eye prewarm, and mirror prewarm to use `EnterOpenXrResourcePlannerThreadScope`.
  - removed the old `OpenXrResourcePlannerScope` that restored OpenXR planner state into renderer-global planner fields.
  - changed resource registration and lazy resource resolution to use the active allocator, so eye-local planning/recording no longer resolves physical images or buffers through the desktop/global allocator.
- Validation:
  - editor build passed after the thread-local prep change; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-thread-local-openxr-prep-planner.log`.
  - `VulkanCommandChainDataModelTests` passed 52/52; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vulkan-command-chain-data-model-thread-local-openxr-prep-planner.log`.
  - `VrViewRenderModeContractTests` passed 17/17; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vr-view-render-mode-contracts-thread-local-openxr-prep-planner.log`.
  - warnings remain the existing Magick.NET package advisories only.
- Remaining risk:
  - Superseded by the frame-op switching slice below: mixed frame-op planner switching now has an active/thread-local state object attached to the OpenXR planner context.
  - runtime validation still needs another explicit CLI/MCP launch and F5-equivalent launch after this refactor to confirm desktop, left-eye, and right-eye stability.

### 2026-06-28 Frame-Op Planner Switching State Isolation

- Implemented:
  - replaced renderer-global frame-op planner switching fields with `FrameOpResourcePlannerSwitchingState`.
  - added a thread-local switching-state scope and attached it to `ResourcePlannerRuntimeState`, so OpenXR eye contexts carry their own mixed-pipeline planner dictionaries, active keys, and recording-scope flags.
  - changed command-chain lowering, command-buffer reuse checks, readback planner scopes, and frame-op planner activation/save/destroy paths to resolve through the active switching state.
  - preserved the outer active switching state when nested frame-op planner substates are restored, avoiding accidental replacement by a substate's empty switching object.
- Validation:
  - editor build passed; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-frameop-switching-thread-local.log`.
  - `VulkanCommandChainDataModelTests` passed 53/53 with a new switching-state guard; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vulkan-command-chain-data-model-frameop-switching-guard.log`.
  - `VrViewRenderModeContractTests` passed 17/17; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vr-view-render-mode-contracts-frameop-switching-thread-local.log`.
- Remaining risk:
  - runtime validation still needs an OpenXR Vulkan smoke after the planner isolation refactor.
  - explicit API cleanup remains: some helpers still discover the active planner/allocator through renderer accessors rather than receiving a concrete immutable recording context parameter.

### 2026-06-28 Explicit View/Planner Cache Context Guard

- Implemented:
  - added a thread-local override for `AbstractRenderer.Current` and pushed it around OpenXR eye worker primary recording, so worker-side helpers that still query `Current` resolve to the worker renderer rather than a desktop/global value.
  - extended the prepared OpenXR eye input with the captured `FrameOpContext`, making the worker-facing recording boundary carry target, frame ops, planner context, planner revision, command-chain schedule, and foveation resource identity explicitly.
  - added source-contract coverage for descriptor image-info generation, descriptor allocation fingerprints, framebuffer attachment-state caches, command-chain view/frame-slot keys, OpenXR primary cache keys, and concurrent left/right prepared-eye worker inputs.
  - checked the TODO source-level hazard boxes for allocator-backed texture/FBO cache state, renderer-global active state, upload publication state, command-buffer/command-chain state, descriptor/frame-data state, and foveation/VRS state.
- Validation:
  - `VulkanCommandChainDataModelTests` passed 57/57 after the explicit planner-context and cache-key guards; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vulkan-command-chain-explicit-planner-context-fixed.log`.
  - earlier guard runs in this slice also passed:
    - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vulkan-command-chain-current-thread.log`
    - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vulkan-command-chain-cache-contexts-fixed.log`
  - warnings remain the existing Magick.NET package advisories only.
- Remaining risk:
  - runtime smoke/perf evidence is still required before checking the warm p95, flicker, F5-equivalent, and final default-selection boxes.

### 2026-06-28 Wrap-Up - Parallel Worker Descriptor Snapshot Gate

- Current pause state:
  - User asked to wrap up for now while implementing `docs/work/todo/rendering/vr/openxr-vulkan-true-parallel-eye-primary-recording-todo.md`.
  - No additional TODO checkboxes were closed during this pause because the latest parallel-worker runtime run exposed an active correctness blocker.
- Runtime evidence gathered this slice:
  - Current sequential Vulkan/Monado smoke passed:
    - summary: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/smoke-current-sequential/reports/openxr-smoke-summary.json`
    - engine log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_21-43-14_pid56048`
    - clean scan: zero VUIDs, validation errors, descriptor binding failures, skipped draws, EndFrame failures, physical-image handle churn, or framebuffer rebuild loops; one startup command-buffer recovery message and the known invalid render-graph pass warning remained.
  - Current sequential profiled Vulkan/Monado smoke passed:
    - summary: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/smoke-current-sequential-profile/reports/openxr-smoke-summary.json`
    - warm stats: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/smoke-current-sequential-profile/reports/warm-stats-summary.json`
    - warm p95 values were far over 8.33 ms: `RenderDispatch=170.286 ms`, `GpuPipelineFrame=90.083 ms`, `VulkanRecordCommandBuffer=58.366 ms`, `VulkanGpuCommandBuffer=23.047 ms`.
  - Exact parallel/dedicated profile smoke failed before writing a smoke summary:
    - run root: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/smoke-parallel-dedicated-profile`
    - engine log: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_21-47-12_pid50144`
    - process crashed with `0xC0000005` in `Vk.CmdEndRendering` while an OpenXR eye worker was recording a prepared primary command buffer.
    - log evidence before the native crash showed repeated `Frame op recording failed for MeshDrawOp` exceptions while resolving bloom/default-pipeline descriptors. The managed stack hit `VkImageBackedTexture.SaveCurrentPhysicalImageViewCache` while one worker was switching allocator-backed physical groups and another worker was resolving descriptor image/view state.
- Diagnosis:
  - OpenXR primary cache keying itself appears view-safe: the key already includes command-chain image key, image, image view, format, extent, depth target, eye index, OpenXR image index, frame-data slot, resource-planner state index, and foveation resource identity.
  - The crash is instead below primary ownership: allocator-backed logical textures still expose one mutable `_physicalGroup/_image/_view/_attachmentViews` snapshot while left/right workers resolve descriptors under different active planner states.
  - This is the same architecture class as the earlier desktop/eye-origin flicker: per-view planner state is scoped, but some logical backend wrappers still publish that active view state through shared mutable object fields.
- Code state at pause:
  - Added `VkImageDescriptorSnapshot` and a default `IVkImageDescriptorSource.TryGetDescriptorSnapshot(...)` contract.
  - Added `_imageStateLock` and coherent no-lock snapshot helpers to `VkImageBackedTexture<TTexture>`.
  - Locked image-backed texture descriptor readiness, descriptor properties, aspect-only descriptor views, expected-view descriptor views, and tracked layout resolution so a single texture wrapper cannot mutate its physical-image/view cache while another thread snapshots it.
  - `VkTextureView` now has an explicit `TryGetDescriptorSnapshot(...)` override that refreshes and snapshots viewed image, memory, view, sampler, format, aspect, usage, layout, allocator-backed identity, and readiness under the view lifetime lock.
  - `VkMeshRenderer.Descriptors` now consumes `TryGetDescriptorSnapshot(...)` for mesh image descriptors instead of reading `DescriptorUsage`, `DescriptorFormat`, `DescriptorAspect`, `DescriptorView`, `DescriptorSampler`, aspect-only views, and layout one property at a time.
  - Combined depth/stencil descriptor selection now requests a second aspect-specific snapshot, then uses that same snapshot for sampler/layout/view/logging.
  - Descriptor image-layout resolution has a snapshot overload so sampled/storage layout decisions do not re-query a source whose active planner/image state may have changed.
- Validation after the descriptor snapshot wiring:
  - Editor build passed with `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`.
  - `VulkanCommandChainDataModelTests` passed 58/58 with the new coherent descriptor snapshot source-contract guard; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vulkan-command-chain-descriptor-snapshot-wrapup.log`.
  - Warnings were the existing Magick.NET package advisories only.
  - The failed parallel smoke has not yet been rerun after the completed descriptor-snapshot wiring, so the regression remains open until runtime evidence proves the crash is gone.
- Next work, in order:
  - Rerun the exact failed parallel/dedicated OpenXR Vulkan smoke with `VR.ViewRenderMode=ParallelCommandBufferRecording`, `XRE_OPENXR_RENDER_PACING_MODE=DedicatedThread`, profiling enabled, command chains enabled, and eye-output capture enabled.
  - Scan the rerun for the original signatures: `Frame op recording failed for MeshDrawOp`, descriptor binding failures, `Vk.CmdEndRendering` crash/device loss, VUIDs, skipped draws, EndFrame failures, physical-image handle churn, and framebuffer rebuild loops.
  - If the crash persists, inspect remaining worker-shared mutable state in `VkMeshRenderer.RecordDraw`, descriptor allocation/update, `_isRecordingCommandBuffer`, descriptor frame slots, GPU profiler slots, material descriptor resolution outside the mesh image path, and command-chain caches before any more perf work.
  - If the crash is gone, rerun the focused `VrViewRenderModeContractTests`, then continue the sequential/single-pass/parallel mode matrix with warm p95 acceptance checks.
  - Only after the parallel worker smoke is clean should we continue the F5/MCP flicker gate, `AllowDesktopEditing` visibility validation, foveation capability smoke, OpenGL unsupported-mode diagnostics, and final default-mode selection.

### 2026-06-28 Follow-Up - Image-State Lock And Parallel Smoke Revalidation

- Regression rechecked:
  - reran the exact failed parallel/dedicated OpenXR Vulkan smoke after the descriptor-snapshot wiring.
  - that rerun reached the requested 90/90 frames but still failed to write the smoke summary.
  - the old native `Vk.CmdEndRendering` access violation did not reproduce, but the engine log showed repeated right-eye `Frame op recording failed for MeshDrawOp` exceptions and `BloomBlurTexture` attachment-view failures.
  - managed stack evidence moved the blocker from descriptor snapshot reads to `VkImageBackedTexture.SaveCurrentPhysicalImageViewCache`, where `_attachmentViews` was being copied while another eye worker could mutate the same logical texture wrapper.
- Implemented before continuing:
  - serialized image-backed texture physical-image cache save/restore and attachment-view cache access with `_imageStateLock`.
  - `GetAttachmentView(...)`, attachment extent lookup, and descriptor `CreateImageInfo()` now enter the image-state lock and use the no-lock refresh helper internally.
  - added a source-contract guard so allocator-backed texture attachment-view cache access, descriptor image-info creation, and the physical-image refresh wrapper stay image-state locked.
  - updated the stale OpenXR diagnostic text: `ParallelCommandBufferRecording` now describes the worker-backed left/right eye path instead of claiming primary recording is still serialized.
- Validation after the image-state lock:
  - editor build passed; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-image-state-lock-attachment-view.log`.
  - `VulkanCommandChainDataModelTests` passed 59/59; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vulkan-command-chain-image-state-lock-attachment-view.log`.
  - focused mode/data-model tests passed 76/76 after the diagnostic cleanup; log: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vulkan-command-chain-and-vr-view-mode-after-image-state-lock.log`.
  - parallel/dedicated profiled OpenXR Vulkan smoke passed when rerun with loader preflight skipped:
    - summary: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/smoke-parallel-dedicated-profile-image-state-lock-skip-preflight/reports/openxr-smoke-summary.json`
    - warm stats: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/smoke-parallel-dedicated-profile-image-state-lock-skip-preflight/reports/warm-stats-summary.json`
    - engine session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_22-18-44_pid47488`
    - submitted frames: 88, no-layer frames: 2, EndFrame failures: 0, per-eye acquire/wait/release: `[88, 88]`, per-frame allocations: 0.
    - log scan found zero `Frame op recording failed`, eye-render failures, descriptor binding failures, skipped draws, `BloomBlurTexture` attachment failures, `IndexOutOfRangeException`, VUIDs, validation errors, physical-image churn, framebuffer rebuild loops, or the old `Vk.CmdEndRendering`/`0xC0000005` crash signature.
  - a preflight-included rerun failed immediately with `0xC0000374` and produced no new engine log or editor stdout/stderr. Treat this as a harness/loader-preflight issue to isolate later, not as renderer evidence.
- Current performance evidence from the passing smoke:
  - warm filter: `render_frame_id >= 120 && active_stereo_mode == vr-desktop-mirror`, 165 samples.
  - `RenderDispatch`: p50 25.799 ms, p95 81.264 ms, max 197.392 ms.
  - `GpuPipelineFrame`: p50 22.950 ms, p95 97.552 ms, max 106.581 ms.
  - `VulkanRecordCommandBuffer`: p50 4.801 ms, p95 10.550 ms, max 12.751 ms.
  - `VulkanGpuCommandBuffer`: p50 19.295 ms, p95 24.720 ms, max 34.246 ms.
  - command-chain worker recording: p50 0.974 ms, p95 3.508 ms, max 8.234 ms.
  - render-thread wait for chain workers: p50 0.000 ms, p95 0.275 ms, max 0.382 ms.
- Current conclusion:
  - the active parallel-worker correctness regression is fixed by the image-state lock; do not reopen the descriptor-snapshot crash path unless it reproduces.
  - the 120 Hz target is still not met. The render-thread worker wait is near zero, so the next bottleneck is default-pipeline GPU/frame pacing and the remaining p95 command recording spikes, not the eye-worker join itself.
- Next work, in order:
  - run the same `ParallelCommandBufferRecording` smoke with `XRE_OPENXR_RENDER_PACING_MODE=CollectVisibleThread` and compare it to the passing dedicated-thread evidence.
  - then complete the sequential, single-pass stereo, and parallel command-buffer mode matrix using the same warm-stat filter.
  - investigate the default pipeline GPU p95 first: export/inspect GPU pass timing and, if logs are inconclusive, capture RenderDoc targets for shadow, G-buffer, AO, lighting, bloom, TSR, and final post-process.
  - validate the F5/unit-testing-editor path separately from the CLI smoke, because the earlier flicker may be launch-path or timing dependent.
  - finish the `AllowDesktopEditing=true/false` collect-visible contract checks, including the one-pass combined frustum for eyes plus smoothed cyclopean desktop view when desktop editing is disabled.
  - keep foveated rendering in the matrix: prefer Vulkan fragment shading rate/eye-tracked paths where available, and keep OpenGL support to fixed/diagnostic behavior where the backend can support it safely.

## Regression Tracker

- Active at pause: 120 Hz acceptance is still open. The latest passing `ParallelCommandBufferRecording` Vulkan/Monado smoke is correctness-clean, but warm p95 values are still far above the 8.33 ms budget (`GpuPipelineFrame` p95 97.552 ms, `VulkanGpuCommandBuffer` p95 24.720 ms, `RenderDispatch` p95 81.264 ms).
- Fixed before continuing: true parallel OpenXR eye worker recording could crash or fail right-eye recording in the default pipeline while two planner contexts touched the same allocator-backed logical texture. Descriptor snapshots first removed incoherent per-property reads, then `_imageStateLock` was extended over physical-image cache save/restore, attachment-view cache access, attachment extent lookup, and descriptor image-info creation. The follow-up parallel/dedicated smoke passed and the log scan showed zero old crash/failure signatures.
- Fixed before continuing: serial desktop/left/right Vulkan context switches caused allocator-backed `VkImageBackedTexture` wrappers and `VkFrameBuffer` wrappers to ping-pong physical image/view state, producing steady-state `Physical group image handle changed` and `Rebuilding framebuffer` logs. Per-physical-image view caching plus framebuffer attachment-variant caching reduced both counts to zero in the 90-frame Vulkan/Monado sequential smoke.
- Fixed before continuing: Vulkan desktop screenshot/readback could return a stale pure-white image while the desktop pipeline output texture was nonblank. Tracked desktop present sources now carry their frame-op/resource-planner context into Vulkan readback.
- Fixed before continuing: an attempted render-snapshot upload publish gate caused the desktop pipeline to capture all black; it was removed immediately, rebuilt, and the desktop post-process capture returned to the prior nonblack baseline while both eye previews stayed valid.
- Fixed before continuing: OpenXR preview-copy setting propagation was incomplete; bootstrap now sets and logs `VrCopyEyePreviewTextures`, and MCP eye previews are nonblack with direct swapchain `previewCopied=True`.
- Fixed before continuing: worker-side OpenXR eye frame-op capture made Forward+ SSBO setup run from non-render threads, causing unresolved `ForwardPlusLocalLightsBuffer` compute descriptors; eye inputs are now prepared before worker dispatch and the follow-up 90-frame Vulkan/Monado smoke has zero descriptor/skipped-dispatch matches.
- Remaining shutdown cleanup issue: unnamed `VkImageView` objects survive Monado/OpenXR runtime-loss teardown and trigger `VUID-vkDestroyDevice-device-00378`. Current evidence shows no steady-state submit/render validation errors; keep this on the regression list and fix before accepting a final optimized state.
- Fixed before continuing: command-chain worker command pools skipped Vulkan destruction once `_deviceLost` was set, leaving command pools alive at `vkDestroyDevice`; destroy worker pools during teardown even after device-lost observation.
- Fixed before continuing: command-chain secondary command pools skipped Vulkan destruction once `_deviceLost` was set, leaving many `CommandChain.Secondary` child command buffers alive at `vkDestroyDevice`; destroy owned secondary pools during teardown even after device-lost observation.
- Fixed before continuing: command-chain/primary signatures used indirect-buffer wrapper identity while recorded draw commands baked live Vulkan buffer handles, so a `VkDataBuffer` backing recreation could leave executable secondaries/primaries submitting destroyed `VkBuffer` handles; signatures now include live buffer handles and buffer recreation dirties dependent command buffers.
- Possible visual correctness issue: desktop/final post-process captures are nonblank but over-bright/color-wrong in the latest Vulkan timing run. Do not accept this as a final optimized state until a known-good comparison or fix is made.
- Guarded before continuing: earlier MCP viewport screenshots captured blank white from multiple camera positions even though render state and OpenXR swapchains were live; tracked desktop present sources now carry their planner/readback context and the follow-up screenshot was nonblank. Keep this on the F5/visual gate list in case it reproduces through the debugger launch path.
- Guarded before continuing: descriptor-generation-only frame-data refresh now marks command chains as descriptor-touched, and OpenXR primary reuse rejects those chains until the descriptor mutation path is legally reusable.
- Fixed before continuing: Vulkan dense imported texture streaming could queue preview demotions against a pending full-res promotion with no VRAM pressure, causing texture publish churn and descriptor invalidation; guarded pending resident targets and added tests.
- Fixed before continuing: relaxing OpenXR primary reuse to allow metadata-only command chains produced Vulkan descriptor-set invalid-command-buffer validation errors; restored the secondary-command-buffer-handle safety gate before continuing.
- Fixed before continuing: removing `ResourceAllocatorIdentity` from descriptor allocation fingerprints caused hundreds of descriptor allocations and unstable resource fingerprints; reverted and focused tests passed.
- Fixed before continuing: the incomplete frame-source mutable descriptor attempt regressed to 37.49 ms p50; completed the sampler-unit/presence-safe frame-source fingerprinting before continuing.
- Fixed before continuing: fully mutable frame-source descriptor identity did not reduce recorded command chains and was reverted after a clean live run and focused test pass.
- Fixed before continuing: normal mesh prepared-state parallel recording and chunked secondary recording both regressed comparable timing buckets, so the experiment was reverted and focused validation passed again.
- Fixed before continuing: placeholder command-chain worker gating removed the worker wait metric but did not improve frame time, so the experiment was reverted and focused tests passed again.
- Fixed before continuing: compute-dispatch frame-data classification and command-chain descriptor-content refresh both regressed CPU recording time, so both were rolled back before new optimization work continued.
- Fixed: stale OpenXR primary command-buffer reuse could bind descriptor sets whose underlying secondary recordings/descriptors had changed, producing Vulkan device-loss/descriptor validation risk. Primary reuse is now gated and generation-sensitive.
- Baseline A shutdown required force-stopping the launched editor PID after `CloseMainWindow` did not finish promptly. Treat later shutdown log noise as non-steady-state until proven otherwise.
- Baseline B desktop viewport screenshot was black, but later desktop and final-pipeline captures were nonblank with the same feature set. Treat as a transient screenshot/readback artifact unless it reproduces.

## Evidence

- Build logs:
  - baseline: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/logs/build-editor-baseline.log`
  - image-state lock: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/editor-build-image-state-lock-attachment-view.log`
- Focused test logs:
  - descriptor snapshot/source-contract guard: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vulkan-command-chain-descriptor-snapshot-wrapup.log`
  - image-state attachment-view guard: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vulkan-command-chain-image-state-lock-attachment-view.log`
  - image-state plus view-mode contracts: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/logs/test-vulkan-command-chain-and-vr-view-mode-after-image-state-lock.log`
- Editor/MCP captures: `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/mcp-captures/`
- Engine log sessions:
  - Baseline A: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_00-06-17_pid6072`
  - Baseline B: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_00-11-54_pid18828`
  - Primary reuse: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_00-16-51_pid31120`
  - image-state parallel/dedicated pass: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-28_22-18-44_pid47488`
- OpenXR smoke summaries:
  - image-state parallel/dedicated pass: `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/smoke-parallel-dedicated-profile-image-state-lock-skip-preflight/reports/openxr-smoke-summary.json`
- CPU/GPU frame timing summaries:
  - `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/baseline-full-parallel-ndjson-summary.txt`
  - `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/baseline-full-parallel-dirty-reasons.txt`
  - `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/baseline-warm-parallel-ndjson-summary.txt`
  - `Build/_AgentValidation/20260627-235935-openxr-vulkan-120hz/reports/primary-reuse-no-gpuprof-ndjson-summary.txt`
  - `Build/_AgentValidation/20260628-implement-openxr-vulkan-view-modes/smoke-parallel-dedicated-profile-image-state-lock-skip-preflight/reports/warm-stats-summary.json`
