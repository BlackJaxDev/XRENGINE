# OpenXR Monado Vulkan Parallel Rendering Investigation

Date: 2026-06-25

Status: Fixed in the local working copy and validated with MCP captures plus Vulkan logs. The editor viewport and both OpenXR eye preview outputs render scene geometry through the Vulkan + Monado path with command chains and parallel packet build enabled.

Primary recovery run root: `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery`

Earlier failed run root: `Build/_AgentValidation/20260625-175629-openxr-vulkan-parallel`

Recovery TODO:

- `docs/work/todo/COMPLETED/openxr-monado-vulkan-parallel-rendering-todo-2026-06-25.md`

Latest failed validation session before the recovery plan:

- Editor PID: `34180`
- Logs: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_20-40-12_pid34180`
- Active log tab: `log_vulkan.log`
- Latest captures:
  - `Build/_AgentValidation/20260625-175629-openxr-vulkan-parallel/mcp-captures/Screenshot_20260625_204105.png`
  - `Build/_AgentValidation/20260625-175629-openxr-vulkan-parallel/mcp-captures/Screenshot_20260625_204109.png`
  - `Build/_AgentValidation/20260625-175629-openxr-vulkan-parallel/mcp-captures/Screenshot_20260625_204113.png`
  - `Build/_AgentValidation/20260625-175629-openxr-vulkan-parallel/mcp-captures/RenderPipeline_HDRSceneTex_20260625_204117.png`
  - `Build/_AgentValidation/20260625-175629-openxr-vulkan-parallel/mcp-captures/RenderPipeline_TsrOutputTexture_20260625_204121.png`
  - `Build/_AgentValidation/20260625-175629-openxr-vulkan-parallel/mcp-captures/Screenshot_20260625_204309.png`
  - `Build/_AgentValidation/20260625-175629-openxr-vulkan-parallel/mcp-captures/RenderPipeline_HDRSceneTex_20260625_204322.png`

## Problem Statement

Monado OpenXR with Vulkan is unstable. It was reported as not crashing, but meshes flickered in and out and most editor and eye renders stayed black instead of showing skybox or meshes. Monado OpenXR with OpenGL works, so generic OpenXR/editor scene setup is likely not the primary problem.

Requirement from the request:

- Vulkan editor view should render.
- Both OpenXR eye views should render.
- The path should use parallel render buffer recording and rendering.

## Historical Failed Evidence

Historical state before the recovery plan was bad:

- Viewport and HDR/Tsr captures from PID `34180` are black. Several latest PNGs are only 559 bytes.
- MCP texture stats for `RenderPipeline_HDRSceneTex_20260625_204322.png` reported `minRgb=0`, `maxRgb=0`, and `averageRgb=0`.
- Vulkan validation errors from the previous descriptor layout race no longer appear in the latest `log_vulkan.log`.
- Vulkan frame ops continue to submit:
  - `IndirectDrawOp` from `DefaultRenderPipeline`
  - `MeshDrawOp` from `UserInterfaceRenderPipeline`
  - successful presents
- Render diagnostics still show `GpuVisible(O/M/A/E)=0/0/0/0`. I later confirmed this metric is not proof that the GPU count buffer is zero in strict zero-readback mode, because per-view draw count readback is only enabled for the instrumented strategy.
- At that point, the visual result was still effectively black, so the absence of validation errors was not a successful fix.

Earlier captures show mixed progression:

- `Screenshot_20260625_190500.png` and several post-process captures had visible non-black content.
- `Screenshot_20260625_191103.png` had visible viewport content while later `HDRSceneTex` and post-process textures in the same iteration could be black.
- `Screenshot_20260625_191606.png`, `Screenshot_20260625_192234.png`, `Screenshot_20260625_194101.png`, and `Screenshot_20260625_202541.png` were non-trivial size and appeared to represent visible content checkpoints.
- Latest `record-state-lock` captures reverted to black.

## Screenshot And Capture Method

I am taking progress screenshots through the editor MCP server, not by manually using the OS desktop.

The editor is launched with UnitTesting, MCP, Monado OpenXR, Vulkan, and command-chain diagnostics:

```powershell
$env:XR_RUNTIME_JSON = (Resolve-Path 'Build\Deps\Monado\openxr_monado.json').Path
$env:XRE_WORLD_MODE = 'UnitTesting'
$env:XRE_UNIT_TEST_WORLD_KIND = 'Default'
$env:XRE_UNIT_TEST_RENDER_API = 'Vulkan'
$env:XRE_UNIT_TEST_VR_MODE = 'MonadoOpenXR'
$env:XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS = '1'
$env:XRE_VULKAN_COMMAND_CHAINS = '1'
$env:XRE_VULKAN_COMMAND_CHAIN_TRACE = '1'
$env:XRE_VULKAN_COMMAND_CHAIN_VALIDATE = '1'
$env:XRE_VULKAN_PARALLEL_PACKET_BUILD = '1'

dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467
```

MCP endpoint:

```text
http://localhost:5467/mcp/
```

Screenshot calls use JSON-RPC `tools/call` against MCP, with `output_dir` set to:

```text
Build/_AgentValidation/20260625-175629-openxr-vulkan-parallel/mcp-captures
```

The main MCP tools used:

- `capture_viewport_screenshot` for the editor viewport.
- `capture_render_pipeline_texture` for internal textures such as `HDRSceneTex`, `LightingAccumTexture`, `AlbedoOpacity`, `Normal`, `DepthView`, `PostProcessOutputTexture`, `FinalPostProcessOutputTexture`, `TsrOutputTexture`, and eye-related resources when available.
- `find_nodes_by_name` and `focus_node_in_view` to locate and focus `Sponza`.
- `get_render_pipeline_resources` / resource listing calls to identify capturable texture names.

I also used local image inspection on saved PNGs with `view_image`. The latest images were visually inspected and were black. File size was used as supporting evidence only, not as the sole verdict.

Historical limitation, now superseded: at this stage I had not captured and visually confirmed separate left-eye and right-eye final submitted swapchain images after the black regression. The final recovery evidence above includes both left and right eye captures.

## Final Validated State

Recovery branch:

- `openxr-monado-vulkan-parallel-recovery`

Final build and test evidence:

- Build passed: `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/logs/build-editor-texture-view-sampler-refresh.log`
- Focused rendering tests passed, 90/90: `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/logs/test-focused-rendering-texture-view-sampler-refresh-rerun.log`
- Texture-view sampler regression contract passed, 1/1: `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/logs/test-texture-view-sampler-refresh-contract.log`
- The test project still reports existing `Magick.NET-Q16-HDRI-AnyCPU` vulnerability warnings; these are pre-existing package warnings, not warnings introduced by the touched source.

Final image evidence:

- Full standard desktop/pipeline capture set from sampler-fixed build:
  - `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/mcp-captures/openxr-eye-mirror-fbo-sampler-refresh/Screenshot_20260625_234517.png`
  - `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/mcp-captures/openxr-eye-mirror-fbo-sampler-refresh/RenderPipeline_AlbedoOpacity_20260625_234523.png`
  - `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/mcp-captures/openxr-eye-mirror-fbo-sampler-refresh/RenderPipeline_Normal_20260625_234532.png`
  - `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/mcp-captures/openxr-eye-mirror-fbo-sampler-refresh/RenderPipeline_DepthView_20260625_234541.png`
  - `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/mcp-captures/openxr-eye-mirror-fbo-sampler-refresh/RenderPipeline_LightingAccumTexture_20260625_234554.png`
  - `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/mcp-captures/openxr-eye-mirror-fbo-sampler-refresh/RenderPipeline_HDRSceneTex_20260625_234611.png`
  - `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/mcp-captures/openxr-eye-mirror-fbo-sampler-refresh/RenderPipeline_PostProcessOutputTexture_20260625_234632.png`
  - `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/mcp-captures/openxr-eye-mirror-fbo-sampler-refresh/RenderPipeline_TsrOutputTexture_20260625_234657.png`
- Final lighter capture run from the same source with command chains and parallel packet build enabled:
  - `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/mcp-captures/openxr-eye-final-light/Screenshot_20260625_235442.png`
  - `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/mcp-captures/openxr-eye-final-light/OpenXRPreview_LeftEye_20260625_235500.png`
  - `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/mcp-captures/openxr-eye-final-light/OpenXRPreview_RightEye_20260625_235530.png`

Final visual verdict:

- Editor viewport: non-black; scene meshes render and remain visible.
- `AlbedoOpacity`, `Normal`, `DepthView`, `LightingAccumTexture`, `HDRSceneTex`, `PostProcessOutputTexture`, and `TsrOutputTexture`: all captured from the sampler-fixed build and are non-black/plausible for the current heavily colored unit test scene.
- Left eye: non-black; scene meshes render.
- Right eye: non-black; scene meshes render.
- The final validation ran longer than 30 seconds while captures were taken; it did not fall back to black.

Final log evidence:

- Full diagnostic sampler-fixed run: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_23-44-41_pid38988`
- Final light run: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_23-54-02_pid25328`
- Error sweep on both final runs found no `maxSamplerAllocationCount`, `vkCreateSampler`, `Validation Error`, `VUID`, managed exception, or device-lost matches.
- Final light `log_vulkan.log` includes command-chain scheduling:
  - `[Vulkan.CommandChains] schedule=... groups=28 packets=35 staticOps=35 volatileOps=0`
- Final light `log_vulkan.log` includes repeated left/right eye mirror copies with distinct eye indices and swapchain image handles:
  - `Vulkan eye mirror eye=0 ... preview='OpenXRPreviewLeftEyeColor' ...`
  - `Vulkan eye mirror eye=1 ... preview='OpenXRPreviewRightEyeColor' ...`

Important remaining quality note:

- The scene is visually over-saturated/hot in this Unit Testing World configuration. That is not the black/flicker failure from this investigation, but it should be tracked separately if the expected art calibration is different.

## Attempted Changes

### 1. Resource wrapper and physical image refresh

Files involved:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkRenderBuffer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.Vulkan.cs`

Intent:

- Make OpenXR Vulkan eye recording refresh render graph resource wrappers before recording.
- Allow OpenXR Vulkan eye rendering to build a command-chain schedule instead of bypassing the command-chain machinery.
- Refresh stale render-buffer/image-backed texture wrappers when the physical render-graph group changes.
- Route `EnableOpenXrVulkanParallelRendering` through runtime host services instead of reading game settings directly.

Validation:

- Builds passed after these changes.
- Captures improved during some middle iterations but did not converge to a stable render.

Risk:

- This is broad and touches render-graph resource lifetime. It needs audit against the last known good commit.

### 2. Auto exposure and post-process capture experiments

Files involved:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/VulkanRenderer.AutoExposure.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ExposureUpdate.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameTiming.cs`

Intent:

- Reduce auto-exposure sampling instability by switching to a bounded grid sample.
- Skip exposure update when render-graph pass metadata is missing instead of running without pass identity.
- Correct swapchain pass labeling in timing diagnostics.

Validation:

- Built successfully.
- Captures showed cases where G-buffer textures had data but post-process/HDR could still be black.

Risk:

- This may address symptoms downstream from scene rendering rather than the root issue. It should not be counted as the primary fix.

### 3. Dynamic UI secondary overlay recording changes

Files involved:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs`

Intent:

- Delay dynamic UI secondary recording when preserving swapchain content for overlay composition.
- Avoid recording overlay UI into a primary command buffer phase that might clear or overwrite scene content.

Validation:

- Built successfully.
- Later captures still regressed to black at this checkpoint, so this was not sufficient by itself.

Risk:

- Changes command buffer reuse and dynamic secondary lifetime. Needs focused validation once scene rendering is restored.

### 4. Force Vulkan OpenXR UnitTesting onto GPU indirect dispatch

Files involved:

- `Samples/MonkeyBallVR/Config/engine_defaults.asset`
- `XREngine.Runtime.Bootstrap/UnitTestingWorldSettingsStore.cs`
- `XRENGINE/Engine/Subclasses/Engine.EffectiveSettings.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/VulkanFeatureProfile.cs`
- Tests in `XREngine.UnitTests/Rendering/*`

Intent:

- Remove persisted `ForceMeshSubmissionStrategy: CpuDirect` from sample defaults.
- Ensure UnitTesting + OpenXR/MonadoOpenXR + Vulkan does not silently force CPU direct rendering.
- Ensure runtime Vulkan profiles permit GPU dispatch as required by the request.

Validation:

- Focused tests passed:
  - `OpenXrTimingPipelineContractTests`
  - `RuntimeRenderingHostServicesTests`
  - `GpuIndirectPhase3PolicyTests`
  - `SwapchainContextCoalescingTests`

Risk:

- This deliberately exposes the GPU path. If the GPU path is broken, it makes the regression more visible rather than hiding it behind CPU direct.

### 5. External index buffer preservation for atlas/indirect rendering

Files involved:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Buffers.cs`

Intent:

- Preserve externally provided triangle index buffers when the `VkMeshRenderer` mesh is null.
- Avoid losing atlas index buffer state used by indirect draw paths.

Validation:

- Focused tests passed after this change.

Risk:

- Localized, but should be verified with actual visible rendering.

### 6. Captured Vulkan indirect draw state

Files involved:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.IndirectDraw.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs`
- `XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs`

Intent:

- Vulkan indirect draws cannot rely on mutable renderer/material state later during command-buffer recording.
- Added `IndirectDrawOp` payload fields for `VkMeshRenderer` and `PendingMeshDraw`.
- Added `TryCreatePreparedIndirectDrawSnapshot` and `RecordIndirectDrawState`.
- Wrapped Vulkan indirect draw calls with a pushed material/program/model state in `HybridRenderingManager`.

Validation:

- Build and focused tests passed.
- This moved Vulkan indirect rendering from missing-state warnings toward recorded draw ops.

Risk:

- Large structural change. It snapshots a lot of current render state and therefore must be carefully compared against direct mesh draw state.

### 7. Count buffer target and descriptor binding fixes

Files involved:

- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.ShadersAndInit.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Core.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.CullingAndSoA.cs`

Intent:

- Vulkan `vkCmdDrawIndexedIndirectCount` needs the count buffer to be usable as an indirect buffer.
- Recreate parameter/count buffers with `DrawIndirectBuffer` target.
- Accept `DrawIndirectBuffer` as storage-buffer compatible where shaders write counts.
- Bind previously optional shader buffers consistently, including stats, truncation, overflow, and hot/fallback command buffers.

Validation:

- Build and focused tests passed.
- Descriptor layout validation errors were reduced after subsequent fixes.

Risk:

- At this checkpoint, this was a likely root area for the black regression. It changed buffer usage and binding behavior in the zero-readback path.

### 8. BVH buffer generation fixes

Files involved:

- `XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Buffers.cs`
- `XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Overflow.cs`

Intent:

- Ensure BVH buffers are generated before pushing or resizing data.

Validation:

- Build and focused tests passed.
- Latest black state still occurs, so this is not sufficient.

### 9. Parallel secondary indirect command recording

Files involved:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.SecondaryCommandBuffers.cs`

Intent:

- Use secondary command buffers for contiguous indirect draw command-chain runs.
- Emit a primary read barrier before secondary indirect draw execution.
- Use `Task.Run` to record indirect secondary command buffers in parallel when the run is large enough.
- Avoid inline barriers inside secondary command buffers when the primary run barrier covers the indirect/count reads.

Validation:

- Build and focused tests passed.
- Earlier warning `Indirect ad-hoc barrier suppressed` was removed after cleanup.

Risk:

- Parallel recording exposed mutable `VkMeshRenderer` recording state races. This was probably contributing to flicker and validation errors.

### 10. Descriptor layout race guard during parallel indirect recording

Files involved:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs`

Intent:

- Add a per-`VkMeshRenderer` recording-state lock.
- Serialize the mutable part of indirect draw state recording for a shared renderer while still allowing secondary command buffers to be allocated/recorded as a parallel run.
- Remove an allocation in task waiting by waiting each task directly.

Validation:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj --no-restore --tl:off` passed.
- Focused tests passed.
- Latest Vulkan validation errors for descriptor set layout mismatch disappeared.

Regression:

- Latest MCP viewport and HDR/Tsr captures are black after this patch.

Risk:

- This may reduce parallelism for a shared atlas renderer and may be masking rather than eliminating mutable state coupling.

### 11. Indirect count-offset signature patch

Files involved:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandChainLowering.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.FrameOpSignatures.cs`

Intent:

- Add `CountByteOffset` to indirect draw command-buffer signatures and debug signature output.
- Add `ByteOffset` and `CountByteOffset` to command-chain structural signatures for indirect draws.
- Rationale: material-tier buckets use one count buffer with different offsets. Reusable command buffers/chains must distinguish buckets that read different count uints.

Validation:

- Historical validation at this checkpoint: not yet built.
- Historical validation at this checkpoint: not yet visually validated.

Risk:

- At this checkpoint, this was a plausible correctness fix for command reuse, but it was made after the latest black validation and was not evidence of progress by itself.

## Current Working Copy Scope

Tracked files currently modified: 41.

This is too broad for an unresolved rendering regression. Before continuing with more functional changes, this diff should be split into smaller hypotheses or reverted selectively to the last known visible checkpoint.

Current modified areas:

- OpenXR Vulkan parallel rendering and runtime setting plumbing.
- Vulkan render-graph resource wrapper refresh.
- Vulkan command-buffer recording, command-chain lowering, command signatures, and secondary command buffers.
- Vulkan indirect draw state capture.
- Vulkan mesh renderer buffers, descriptor setup, preparation, and drawing.
- GPU render pass culling/material scatter/count buffers.
- BVH GPU buffer generation.
- Auto exposure and exposure pass metadata.
- Focused unit tests for OpenXR timing, runtime host services, GPU indirect policy, and swapchain context coalescing.

## Self-Review: Architecture Findings

These are historical findings from reviewing the local diff after the regression from intermittent mesh flicker to mostly black editor/eye output. At that point they were not all proven root causes, but they were the highest-risk architectural issues in my own changes.

### High: indirect draw ops lost render-target identity

`IndirectDrawOp` still derives from `FrameOp(PassIndex, null, Context)` in `VkMeshRenderer.cs`, so every indirect draw is modeled as swapchain-targeted even when it was captured while rendering an FBO-backed pass. The new secondary-command path reinforces that mistake by beginning indirect render-pass runs with `target: null` in `VulkanRenderer.CommandBufferRecording.cs`.

Representative current code:

- `VkMeshRenderer.cs`: `IndirectDrawOp(... FrameOp(PassIndex, null, Context))`.
- `VulkanRenderer.CommandBufferRecording.cs`: `BeginRenderPassForTarget(null, passIndex, firstDraw.Context, secondaryContents: true)`.
- `VulkanRenderer.CommandBufferRecording.cs`: fallback indirect run handling ends the active render pass, emits a barrier, then begins `BeginRenderPassForTarget(null, opPassIndex, activeContext)`.

This is architecturally wrong for the default render graph. The indirect op needs to carry the actual `XRFrameBuffer? Target`, just like mesh draw ops do, and grouping/inheritance must use that target. Otherwise a deferred/HDR/eye render pass can be accidentally recorded as a swapchain pass, which is consistent with black intermediate captures while command recording still appears active.

### High: OpenXR command-chain schedule uses a fake image index

`TryBuildOpenXrEyeCommandChainSchedule` calls `TryBuildCommandChainSchedule(imageIndex: 0, ...)`. The temporary OpenXR command buffer path also records with image index `0`.

This may have been tolerated in the old direct path, but command-chain caching and per-frame state need an explicit external swapchain frame/eye identity. A hardcoded slot risks reusing or keying frame resources as if both eyes and every acquired OpenXR image were the same render target. This should be replaced with a real external-frame key or an OpenXR-specific command-chain scheduling path that does not pretend to be swapchain image zero.

### High: optional/debug buffers now gate base indirect counter reset/build

`GPURenderPassCollection.IndirectAndMaterials.cs` now makes reset/build paths return early if diagnostic, meshlet, or overflow buffers are missing. That means base buffers such as `_culledCountBuffer`, `_drawCountBuffer`, and per-view draw counts may not be reset or built if an optional feature buffer is absent.

This is a fragile binding contract. Base GPU indirect rendering should have a minimal required buffer set; meshlet, stats, and overflow instrumentation should be capability-specific or backed by dummy buffers. Otherwise a missing optional resource can silently collapse all indirect draws to zero.

### Medium: indirect draw snapshot duplicates direct draw capture

`TryCreatePreparedIndirectDrawSnapshot` duplicates much of the direct mesh draw capture path and already missed target propagation. It also constructs a `PendingMeshDraw` with `Instances = 1u`, relying on the GPU indirect command buffer to supply real instance counts.

The better direction is a shared immutable mesh draw capture helper that produces the common pipeline/material/descriptor/target/camera state once, with an explicit indirect mode for GPU-sourced draw and instance counts. Duplicating this logic makes it too easy for direct and indirect paths to diverge.

### Medium: per-renderer recording lock is containment, not architecture

`RecordingStateSyncRoot` around `RecordIndirectDrawState` reduces descriptor/pipeline mutation races, but it also serializes recording for any shared renderer, especially atlas renderers used by many meshes. It is a containment patch, not a final architecture.

The preferred Vulkan design is to resolve mutable renderer/program/material state into immutable per-frame/per-pass snapshots before worker recording starts. Secondary command-buffer workers should only consume immutable state and write command buffers.

### Medium: OpenXR resource wrapper refresh is too broad for a hot path

`RefreshFrameOpResourceWrappers` scans texture/renderbuffer registries and can synchronously create API objects or descriptors during OpenXR eye rendering. That may paper over stale wrapper bugs, but it can also explain low Hz/stutter and obscure the real resource lifetime issue.

The cleaner fix is graph-owned physical image group versioning/invalidation: when an XR framebuffer adopts or wraps a new swapchain image, the resource wrapper/view/descriptors should be updated through explicit ownership/version transitions, not broad per-eye registry refresh.

### Medium/Low: count-offset signature patch was plausible but unvalidated at the time

Adding `CountByteOffset` and indirect byte offsets to the draw signatures was likely a real correctness fix for indirect-count recording. At the time this section was written, it did not address the black-output regression by itself and had not been built or validated after the doc update request. The final recovery evidence above supersedes that status.

## Historical Unproven Items Before Recovery

At the time this section was written, these items were still unproven. The final validation evidence above supersedes this list.

- The GPU count buffers were not proven to be zero. The visible counters shown in logs were not reliable proof in strict zero-readback mode.
- The editor viewport blackness was proven by MCP captures.
- The then-latest fixes had not proven both eye views rendered.
- The count-offset signature patch had not been validated.
- The broad diff could have contained more than one interacting issue.

## Next Diagnostic Steps

1. Stop making broad code changes until a smaller hypothesis is selected.
2. Build after the count-offset signature patch and run exactly one Vulkan/Monado editor iteration.
3. Capture:
   - editor viewport
   - `AlbedoOpacity`
   - `Normal`
   - `DepthView`
   - `LightingAccumTexture`
   - `HDRSceneTex`
   - `PostProcessOutputTexture`
   - left eye final texture/swapchain image if capturable
   - right eye final texture/swapchain image if capturable
4. Inspect images visually and record verdicts in this file.
5. If still black, bisect within this working copy by disabling/reverting one hypothesis group at a time:
   - dynamic UI secondary overlay delay
   - OpenXR eye command-chain scheduling
   - count-buffer target conversion
   - parallel indirect secondary recording
   - indirect draw state snapshot
6. Use RenderDoc if MCP textures and logs do not identify the failing pass/resource.

## Accountability Note

This progress note was created late. The investigation should have had this tracker before multiple visual iterations and before the diff grew this large. At the time this note was first written, the result was an unstable intermediate state with some validation errors removed but visual output regressed to black. The recovery sections above and below supersede that earlier state.

## Resolved Findings After Recovery

The self-review risks above are preserved as the state of the investigation when the recovery TODO was written. The final local working copy resolves the black editor/eye path with these root-cause changes:

1. Indirect draws now carry explicit target identity.
   - `IndirectDrawOp` has `XRFrameBuffer? Target`.
   - Indirect secondary and fallback recording use the op target instead of assuming `<swapchain>`.
   - Command-chain grouping/signatures include target identity so graph/FBO and eye paths do not alias.
   - Final logs show indirect scene draws targeting `DeferredGBufferFBO`, not `<swapchain>`.

2. The OpenXR Vulkan eye path now mirrors the working OpenGL architecture.
   - Each eye renders into `OpenXRViewportMirrorFBO`.
   - Vulkan explicitly blits `OpenXRViewportMirrorColor` into the acquired OpenXR swapchain image.
   - The final eye previews are populated from the submitted image path and captured through `capture_openxr_eye_preview_texture`.

3. OpenXR external image identity is explicit where command scheduling still needs it.
   - The older direct external-swapchain command path has a key based on eye index, acquired swapchain image index, and image handle.
   - The final path avoids direct graph recording into the runtime-owned image and uses a separate image blit after rendering into the mirror FBO.

4. GPU indirect counters and count offsets are now diagnostically visible.
   - Count/readback diagnostics are opt-in through `XRE_VULKAN_COUNTER_DIAGNOSTICS=1`.
   - Indirect traces include indirect buffer, parameter/count buffer, byte offset, count byte offset, stride, material, program, and target.
   - Command signatures include count-buffer offsets so material-tier buckets cannot collapse to the wrong indirect count.

5. CPU BVH mutation/traversal races are guarded.
   - `CpuBvhRenderTree` now synchronizes remake/swap/traversal paths.
   - Invalid CPU BVH node ranges fail loudly with range context instead of surfacing as opaque flicker/collection failures.

6. At the time of this investigation, Vulkan GPU BVH culling was opt-in.
   - This historical gate was removed on 2026-07-16. GPU submission strategies now request the GPU BVH automatically, with a flat GPU frustum fallback only while BVH resources are unavailable.

7. Texture-view sampler lifetime no longer follows backing-image churn.
   - `VkTextureView.RefreshFromViewedTextureIfStale` now retires image views when the backing image changes, but keeps the sampler unless sampler state changes.
   - This fixed the `VUID-vkCreateSampler-maxSamplerAllocationCount-04110` validation flood seen during the first successful visual run.

The older "What Is Not Yet Proven" list is now superseded:

- The editor viewport, render-pipeline textures, left eye, and right eye have all been captured and visually inspected.
- The count-offset signature patch has been built and covered by focused/source-contract tests.
- The final validation logs have no recurring Vulkan validation errors.
- RenderDoc was not used because MCP captures plus Vulkan diagnostics identified the failing paths and confirmed the corrected output.

## Reopened 2026-06-26: Half-Eye Flicker And Low Hz

The user provided a fresh screenshot showing the editor viewport rendering scene geometry, while the OpenXR/preview eye content shows a flickering almost-half artifact and the overlay reports low frame rate. A follow-up MCP/log pass against session `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-26_07-15-51_pid48208` found:

- The scene rig still reports the eyes parented under `VRHeadsetNode`; this makes the "parented to editor view" symptom more likely to be stale or wrong eye image contents than an actual transform hierarchy fallback.
- The top preview captures match the bad OpenXR eye source; the preview widgets are not yet proven to be the independent source of the upside-down image.
- The main editor view is visually healthy in the OS screenshot, while the Monado/eye source is not.
- Steady-state frame time is dominated by `OpenXR.Vulkan.SubmitFenceWait`; queue submit itself is much shorter.
- The OpenXR Vulkan mirror primary command-buffer reuse path was missing the `OpenXrVulkanPrimaryReuseEnabled` guard that exists on the direct swapchain primary reuse path. That allowed mirror primary reuse even when the environment flag was not enabled.

Targeted change for the next iteration:

- Gate `TryReuseOpenXrMirrorPrimaryCommandBuffer` behind `OpenXrVulkanPrimaryReuseEnabled`.
- Add a source-contract assertion that the mirror reuse helper itself contains the opt-in guard.

Validation needed next:

- Build and run `OpenXrTimingPipelineContractTests`.
- Launch Unit Testing World with Monado OpenXR, Vulkan, command chains, parallel packet build, `XRE_OPENXR_VULKAN_TRACE=1`, and `XRE_VULKAN_CAPTURE_EYE_OUTPUTS=1`.
- Capture editor viewport plus left/right OpenXR preview textures and visually inspect the PNGs.
- Confirm logs no longer show unconditional `mirror reused primary` messages when `XRE_OPENXR_VULKAN_PRIMARY_REUSE` is unset.
- If visuals stabilize but frame rate remains low, refactor the OpenXR mirror render/publish sequence to avoid redundant CPU waits while keeping parallel command recording.

## Reopened 2026-06-26: Slow Left-Eye Deferred Flicker

After the editor-view deferred flicker was resolved by refreshing material/auto uniform buffers during primary command-buffer reuse, the user reported that the left eye deferred meshes now flicker slowly while the editor view remains stable.

Latest user repro logs from `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-26_13-50-01_pid41156` show direct Vulkan OpenXR swapchain rendering with left/right eye pipelines `DefaultRenderPipeline#37` and `DefaultRenderPipeline#38`. There are no Vulkan validation errors, no frame-op recording failures, and no dropped command-chain ops. The recurring clue is:

- `[OpenXR] Vulkan skipped retired-resource drain before eye rendering because frame slot 0 is still pending ...`

Current hypothesis:

- OpenXR eye recording maps left/right eye frame data onto desktop frame-data slots 0/1.
- The eye submit path waits on its own fence after submission, but it did not wait before borrowing a desktop slot that may still be referenced by an in-flight desktop frame.
- Rewriting per-draw UBOs in a slot still owned by desktop GPU work creates a host/GPU race. It is most visible as one-eye deferred flicker because deferred mesh descriptors depend on camera/material auto uniforms; forward meshes are less sensitive.

Targeted change:

- First attempt: before direct eye or mirror-FBO eye recording writes frame-data slot 0/1, wait for the corresponding desktop frame-slot timeline value to complete.
- User result: the main editor view began flickering again. That means waiting on completed GPU work is insufficient when OpenXR can record between editor command-buffer recording and editor submit; the eye recorder can still overwrite a desktop UBO slot before the desktop command buffer reaches the GPU.
- Corrected fix: OpenXR now writes per-eye frame data into dedicated slots after the desktop descriptor-frame range, and grows the per-slot transient compute, deferred secondary command-buffer, and compute descriptor-cache arrays to cover those slots.
- Keep this as a correctness fix. A later performance cleanup can make these dedicated OpenXR frame-data slots explicit in a small slot allocator instead of deriving them from the desktop swapchain image count.
- Crash follow-up: the first dedicated-slot fix was incomplete. The dynamic UBO ring-buffer array still only covered desktop slots, and the mirror-FBO primary recorder used the high frame-data slot as the render target image index. The follow-up fix expands dynamic UBO ring buffers with the other frame-data stores and records mirror primaries with swapchain target slot 0 plus `frameDataImageIndexOverride`.
- Direct-swapchain crash follow-up: making the descriptor frame-slot count a scoped override is unsafe because shared mesh/material descriptor allocations can flip between desktop-sized and OpenXR-sized shapes while cached eye command buffers still reference the larger shape. The corrected fix keeps a monotonic descriptor frame-slot floor once OpenXR expands the frame-data slots.
- Editor flicker follow-up: even with a monotonic floor, the first floor growth could still happen after desktop primaries had already recorded against the desktop-only descriptor shape. Vulkan initialization and swapchain recreation now reserve OpenXR frame-data slots up front when OpenXR is selected, and any later floor growth dirties both desktop and OpenXR primary command-buffer caches.

Validation needed next:

- Build `XREngine.Editor`. Done: passed on 2026-06-26 with only existing Magick.NET advisory warnings.
- Run `OpenXrTimingPipelineContractTests`. Done: 24/24 passed on 2026-06-26 with only existing Magick.NET advisory warnings.
- Re-run Monado OpenXR Vulkan with the existing direct swapchain settings.
- Confirm the left eye no longer flickers.
- Confirm the editor view no longer flickers.
- Check `log_vulkan.log` for absence of Vulkan validation errors and for any repeated OpenXR frame-data slot diagnostics.

## Reopened 2026-06-26: Editor Deferred Flicker Still Occurs

After the dedicated OpenXR frame-data slot fixes, the user reported that the main editor view deferred meshes still flicker. A deeper source/log review of the latest Vulkan session found a stronger recurring clue than the frame-data-slot path:

- `[VulkanResourcePlanner] Keeping pre-recorded physical plan during command-buffer recording despite context change.`
- `DispatchCompute emitted with invalid render-graph pass index ... CurrentPipeline=XRRenderPipelineInstance`

Root cause hypothesis:

- The desktop primary recorder drains a global frame-op queue that can contain editor, shadow, helper/UI, and OpenXR-adjacent pipeline contexts in one command buffer.
- The existing recorder selected one physical render-graph plan before recording and kept it active across context changes.
- Multiple `DefaultRenderPipeline` instances use identical logical graph resource names such as `DeferredGBufferFBO`, `LightingAccumTexture`, `HDRSceneTex`, and related G-buffer attachments.
- `VulkanResourceAllocator` resolves physical groups by logical resource name inside the active allocator. If the active allocator belongs to another pipeline/viewport context, deferred FBO attachments and descriptors can resolve to the wrong physical image.
- Forward meshes are less sensitive because the forward/swapchain path does not depend on the same deferred G-buffer/lighting resource graph.

Targeted fix implemented:

- Added persistent per-frame-op-context resource planner states keyed by pipeline identity, viewport identity, resource registry identity, and pass metadata identity.
- Preparing a mixed frame-op batch now builds/updates one resource planner, physical allocator, compiled graph, and barrier planner per active context.
- Primary command-buffer recording enters a scoped planner switcher. On context change, it saves the outgoing context planner state, restores the incoming state, and saves the final state before returning to the outer renderer state.
- The first sorted op context is activated before frame-start barriers so initial barrier emission matches the first planned resource context.
- Primary command-chain scheduling/reuse is disabled for batches requiring multiple planner states, because primary command chains currently snapshot a single resource-plan revision.
- Shutdown now destroys the cached frame-op-context planner states.

Validation:

- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj` passed on 2026-06-26 with only existing Magick.NET advisory warnings.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~ResourcePlanner_SwitchesPerFrameOpContextDuringPrimaryRecording"` passed on 2026-06-26 with only existing Magick.NET advisory warnings.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-build --filter OpenXrTimingPipelineContractTests` passed 24/24 on 2026-06-26. The first build-backed attempt hit a transient SourceLink file lock on `XREngine.Editor.sourcelink.json`, so the already-built test assembly was rerun with `--no-build`.
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter VulkanP1ValidationTests` was also run, but the bucket is not green because several pre-existing source-contract assertions still look for planner/push-constant code in files where it no longer lives. The new per-context planner guard itself passes.

Validation still needed:

- Re-run the Monado OpenXR Vulkan repro with the same editor launch path the user is using.
- Confirm that `log_vulkan.log` no longer repeats the "Keeping pre-recorded physical plan during command-buffer recording despite context change" warning during steady-state rendering.
- Visually confirm deferred meshes stay stable in the editor viewport and both eye paths.
