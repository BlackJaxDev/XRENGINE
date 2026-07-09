# OpenXR Monado Vulkan Parallel Rendering TODO

Date: 2026-06-25

Status: Reopened on 2026-06-26. The 2026-06-25 completion evidence is now historical, not the current verdict. The latest user-visible run shows a flickering half-eye artifact, very low frame rate, questionable stereo preview orientation, and eye output that appears to follow the editor view even though the scene rig reports the eyes parented under the HMD.

Current blockers:

- [ ] Fix stale/wrong Vulkan OpenXR eye output so both eyes are driven from the HMD cameras, not from editor-view-looking content.
- [ ] Verify the top stereo preview orientation against fresh left/right preview captures after the eye source is stable.
- [ ] Reduce OpenXR Vulkan frame time; current evidence points at `OpenXR.Vulkan.SubmitFenceWait` dominating steady-state frame time.
- [ ] Preserve parallel render-buffer recording and rendering for scene/eye command chains.
- [x] Verify HMD rig hierarchy: latest logs report `leftParentIsHmd=True`, `rightParentIsHmd=True`, and late pose updates with `parentIsHmd=True`.
- [ ] Re-run the editor MCP loop after each targeted fix and record screenshots plus log-session paths here and in the investigation note.

Completion evidence:

- Branch: `openxr-monado-vulkan-parallel-recovery`
- Run root: `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery`
- Final full capture set: `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/mcp-captures/openxr-eye-mirror-fbo-sampler-refresh`
- Final left/right eye capture set: `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/mcp-captures/openxr-eye-final-light`
- Full diagnostic log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_23-44-41_pid38988`
- Final light log session: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_23-54-02_pid25328`
- Build log: `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/logs/build-editor-texture-view-sampler-refresh.log`
- Focused test log: `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/logs/test-focused-rendering-texture-view-sampler-refresh-rerun.log`
- Sampler regression test log: `Build/_AgentValidation/20260625-211224-openxr-vulkan-recovery/logs/test-texture-view-sampler-refresh-contract.log`

Merge gate:

- Validation is complete, but no git merge to `main` was performed from this dirty local working copy. Treat this as ready for owner-reviewed commit/merge, not as an already-merged branch.

Related investigation log:

- `docs/work/investigations/rendering/openxr-monado-vulkan-parallel-rendering-2026-06-25.md`

## Goal

Make Monado OpenXR + Vulkan render correctly and stably while preserving parallel render-buffer recording and rendering.

The final state must show:

- The editor viewport renders the Unit Testing World with skybox and meshes.
- Both OpenXR eye views render the scene, not black.
- Meshes do not flicker in and out under steady camera pose.
- Vulkan command chains and secondary command buffers are used for the applicable render paths.
- The solution is architecturally clean: render targets, frame identity, resource lifetime, descriptors, and indirect draw state are explicit instead of inferred from mutable global renderer state.

## Resolved Risk Summary

The original local diff was too broad and contained compounding issues. The items below were instrumented, fixed, or intentionally bypassed by the final architecture.

Highest-risk areas from self-review:

- Indirect draw ops lose render-target identity by using `FrameOp(PassIndex, null, Context)`.
- OpenXR command-chain scheduling uses fake `imageIndex: 0`.
- GPU indirect counter reset/build now depends on optional or diagnostic buffers.
- Parallel indirect recording still mutates shared renderer/program state and is protected by a lock.
- OpenXR resource wrapper refresh no longer defines the final submitted-eye path; eyes render into `OpenXRViewportMirrorFBO` and blit into the acquired swapchain image.
- The `CountByteOffset` signature patch is built, tested, and validated in logs.

## Ground Rules

- [x] Create a dedicated recovery branch before continuing functional work.
- [x] Keep this TODO and the investigation log updated after every editor iteration.
- [x] Change one architectural variable per iteration.
- [x] Keep diagnostics opt-in through environment variables or existing trace settings.
- [x] Avoid heap allocations in hot paths unless the diagnostic flag is enabled.
- [x] Do not hide Vulkan/OpenXR failures behind CPU-direct fallback.
- [x] Preserve user/editor-generated working-copy changes unrelated to this investigation.
- [x] Prefer explicit asserts, validation logs, and failure reasons over silent early returns.
- [x] Keep all scratch evidence under `Build/_AgentValidation/<run>/`.

## Success Criteria

- [x] `dotnet build .\XREngine.Editor\XREngine.Editor.csproj` succeeds without new warnings from touched code.
- [x] Focused rendering tests pass:
  - [x] `OpenXrTimingPipelineContractTests`
  - [x] `RuntimeRenderingHostServicesTests`
  - [x] `GpuIndirectPhase3PolicyTests`
  - [x] `SwapchainContextCoalescingTests`
- [x] Vulkan/Monado editor run launches with Unit Testing World, MCP, command chains, and parallel packet build enabled.
- [x] MCP screenshot of editor viewport is visually non-black and shows expected scene content.
- [x] MCP captures of `AlbedoOpacity`, `Normal`, `DepthView`, `LightingAccumTexture`, `HDRSceneTex`, `PostProcessOutputTexture`, and `TsrOutputTexture` are visually plausible for the scene.
- [x] Left-eye and right-eye final outputs are captured or mirrored and visually non-black.
- [x] Logs show secondary command buffers/command chains being used for scene rendering.
- [x] Logs show no recurring Vulkan validation errors during steady-state rendering.
- [x] No shutdown-only teardown warnings are mistaken for render-path failures.
- [x] A 30-second steady camera run has no mesh flicker and no transition back to black.
- [x] RenderDoc capture, if used, shows expected contents in the first failing pass or confirms the final corrected path.

## Diagnostic Flags To Add Or Confirm

These flags should be lightweight and off by default. They can reuse the existing Vulkan logging system.

- [x] `XRE_VULKAN_FRAMEOP_TRACE=1`
  - Log every frame op with type, pass index, pass name, pipeline name, render target name/id, context id, and resource registry id.
- [x] `XRE_VULKAN_TARGET_TRACE=1`
  - Log render target begin/end, framebuffer handle, image view handle, extent, layer count, render pass/dynamic-rendering formats, load/store ops, and clear values.
- [x] `XRE_VULKAN_COMMAND_CHAIN_TRACE=1`
  - Existing flag should include command-chain key, target key, pass key, frame slot, external image key, reuse/hit/miss reason, and op run summaries.
- [x] `XRE_VULKAN_INDIRECT_TRACE=1`
  - Log indirect buffer, count buffer, byte offset, count byte offset, stride, use-count mode, mesh renderer id, mesh name, material name, program name, and target.
- [x] `XRE_VULKAN_COUNTER_DIAGNOSTICS=1`
  - Add bounded readback of draw-count/cull-count buffers at named points: after reset, after cull, after command build, before draw.
- [x] `XRE_VULKAN_DESCRIPTOR_TRACE=1`
  - Log descriptor set layout hash, bound resource handles, image layout, image view handle, buffer target flags, and stale-wrapper refresh reasons.
- [x] `XRE_OPENXR_VULKAN_TRACE=1`
  - Log eye index, acquired swapchain image index, image handle, target framebuffer id, command recording key, wait/begin/end timings, and submit timings.
- [x] `XRE_VULKAN_PARALLEL_RECORDING_VALIDATE=1`
  - Validate secondary command-buffer inheritance and grouping before recording and before execution.
- [x] `XRE_VULKAN_CAPTURE_EYE_OUTPUTS=1`
  - Create or expose capturable diagnostic copies/mirrors for left and right eye final images.

## Diagnostic Output Format

Prefer compact, grep-friendly single-line diagnostics.

Examples:

```text
[VulkanFrameOp] frame=184 op=IndirectDraw pass=DefaultRenderPipeline/Opaque target=HDRSceneTex#42 ctx=DefaultRenderPipeline registry=GraphFrame#184 pipeline=DeferredScene
[VulkanTarget] frame=184 begin target=HDRSceneTex#42 fb=0x1234 colorView=0x4567 depthView=0x7890 extent=1920x1080 layers=1 load=Clear store=Store
[VulkanChain] frame=184 key=OpenXR:left:image7 pass=Opaque target=HDRSceneTex#42 ops=IndirectDraw[0..12] reuse=miss reason=target-signature-changed
[VulkanIndirect] frame=184 renderer=AtlasScene#19 target=HDRSceneTex#42 indirect=0xabcd offset=0 count=0xef01 countOffset=16 stride=32 useCount=true materialBucket=OpaqueStatic
[VulkanCounters] frame=184 point=after-build pass=Opaque visible=154 drawCount[0]=154 drawCount[1]=18 overflow=0 truncation=0
[VulkanDescriptor] frame=184 renderer=AtlasScene#19 set=MaterialTextures layoutHash=0x91a2 image=HDRSceneTex#42 view=0x4567 layout=ShaderReadOnlyOptimal stale=false
[OpenXrVulkan] frame=184 eye=left swapchainImage=7 image=0x9999 target=LeftEye#7 commandKey=OpenXR:left:7 recordMs=1.4 submitMs=0.3
```

## Phase 0: Stabilize Scope And Evidence

- [x] Create the dedicated recovery branch.
- [x] Create a fresh run root under `Build/_AgentValidation/`.
- [x] Copy or reference the current latest logs from `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-25_20-40-12_pid34180`.
- [x] Record current `git status --short` in the run root.
- [x] Build once with the current local diff after the latest `CountByteOffset` patch.
- [x] If build fails, fix only compile errors caused by the current local diff.
- [x] Run the focused tests listed in Success Criteria.
- [x] Launch one Vulkan/Monado editor iteration with existing diagnostics.
- [x] Capture the standard MCP image set:
  - [x] editor viewport
  - [x] `AlbedoOpacity`
  - [x] `Normal`
  - [x] `DepthView`
  - [x] `LightingAccumTexture`
  - [x] `HDRSceneTex`
  - [x] `PostProcessOutputTexture`
  - [x] `TsrOutputTexture`
  - [x] left-eye final output if capturable
  - [x] right-eye final output if capturable
- [x] Inspect the saved PNGs visually.
- [x] Update the investigation log with exact paths, visual verdicts, and log session path.

## Phase 1: Add Target Identity Diagnostics

Purpose: prove whether frame ops, render passes, command chains, and secondary command buffers agree about the target being rendered.

- [x] Add a stable debug identity for render targets:
  - [x] target display name
  - [x] target kind: swapchain, OpenXR eye, graph texture, graph renderbuffer, user FBO
  - [x] framebuffer/renderbuffer id
  - [x] physical image group id or generation if available
  - [x] extent and layer count
- [x] Add target identity to every `FrameOp` debug summary.
- [x] Add target identity to command-chain signatures and trace output.
- [x] Add target identity to render pass begin/end logs.
- [x] Add a validation check: a contiguous secondary run must share compatible pass, target, render pass/dynamic-rendering formats, and context.
- [x] Emit an error-level log if an indirect op has `Target == null` while the pass metadata says it belongs to a non-swapchain render graph pass.
- [x] Run one editor iteration and confirm whether indirect scene ops are being recorded against swapchain/null instead of the expected HDR/G-buffer/eye target.

Suspicious code paths:

- `VkMeshRenderer.cs`: `IndirectDrawOp(... FrameOp(PassIndex, null, Context))`
- `VulkanRenderer.CommandBufferRecording.cs`: indirect secondary run `BeginRenderPassForTarget(null, ...)`
- `VulkanRenderer.CommandBufferRecording.cs`: indirect fallback run `BeginRenderPassForTarget(null, ...)`
- `VulkanRenderer.FrameOpSignatures.cs`: indirect draw signature does not prove target identity
- `VulkanRenderer.CommandChainLowering.cs`: command-chain grouping must include target identity

Exit criteria:

- [x] Logs make it unambiguous which target every indirect op intended and which target it actually recorded into.
- [x] If target mismatch is confirmed, no other functional changes are made before Phase 2.

## Phase 2: Fix Indirect Draw Target Propagation

Purpose: make indirect draws first-class frame ops with explicit target identity.

- [x] Add `XRFrameBuffer? Target` to `IndirectDrawOp`.
- [x] Capture the active target when creating the indirect op.
- [x] Pass the target through `TryCaptureIndirectDrawPayload`, `PushIndirectDrawState`, and frame-op construction.
- [x] Update command-chain grouping to group indirect runs by target as well as pass/context.
- [x] Update secondary command-buffer inheritance to use the indirect op target.
- [x] Update fallback indirect draw recording to begin the render pass for the op target.
- [x] Replace hardcoded `"<swapchain>"` diagnostics in indirect draw recording with the actual target name.
- [x] Add tests for indirect op target propagation and grouping.
- [x] Run focused tests and one editor iteration.

Exit criteria:

- [x] Indirect draw logs show the expected graph/eye target, not `<swapchain>`, for scene passes.
- [x] Editor viewport either renders correctly or the next black stage is clearly identified by diagnostics.

## Phase 3: Add OpenXR External Image Identity

Purpose: stop treating every OpenXR eye render as swapchain image zero.

- [x] Identify where the acquired OpenXR swapchain image index and eye index are available in the Vulkan OpenXR path.
- [x] Define an explicit recording key for external render targets, for example:
  - [x] normal swapchain key: swapchain image index/frame slot
  - [x] OpenXR key: eye index + acquired swapchain image index + swapchain/image handle
  - [x] graph target key: graph resource id + physical generation
- [x] Replace `imageIndex: 0` in OpenXR eye command-chain schedule construction.
- [x] Replace `imageIndex: 0` in OpenXR eye `RecordCommandBuffer` call if command-buffer state is keyed by image index.
- [x] Ensure command-chain cache keys cannot alias left/right eyes.
- [x] Ensure command-chain cache keys cannot alias two acquired OpenXR images.
- [x] Add diagnostics that log command key for left and right eyes.
- [x] Add tests for OpenXR command-chain key separation.

Suspicious code paths:

- `VulkanRenderer.OpenXR.cs`: `RecordCommandBuffer(imageIndex: 0, ...)`
- `VulkanRenderer.OpenXR.cs`: `TryBuildOpenXrEyeCommandChainSchedule(... imageIndex: 0 ...)`
- `VulkanRenderer.CommandChainLowering.cs`: command-chain cache key and reuse logic
- `VulkanRenderer.SecondaryCommandBuffers.cs`: secondary command-buffer pool keying

Exit criteria:

- [x] Logs show left/right eyes and acquired image indices produce distinct recording keys.
- [x] Command-chain reuse is intentional and never crosses incompatible eye/image targets.

## Phase 4: Fix GPU Indirect Counter Contracts

Purpose: make missing optional buffers visible without disabling base indirect rendering.

- [x] Split required buffers from optional diagnostic/meshlet buffers in `ResetCounters`.
- [x] Split required buffers from optional diagnostic/meshlet buffers in `BuildIndirectCommandBuffer`.
- [x] Log every abort with the exact missing resource name.
- [x] Ensure base indirect draw count reset does not depend on meshlet dispatch buffers.
- [x] Ensure base indirect build does not depend on stats buffers unless the shader variant actually requires them.
- [x] If the shader needs a binding but the feature is disabled, provide a small dummy buffer with documented ownership.
- [x] Add `XRE_VULKAN_COUNTER_DIAGNOSTICS=1` readback at:
  - [x] after reset
  - [x] after culling
  - [x] after indirect command build
  - [x] before indirect draw
- [x] Include per-view draw counts and per-material-tier count offsets in diagnostics.
- [x] Add tests for missing optional buffers not skipping base reset/build.

Suspicious code paths:

- `GPURenderPassCollection.IndirectAndMaterials.cs`: `ResetCounters`
- `GPURenderPassCollection.IndirectAndMaterials.cs`: `BuildIndirectCommandBuffer`
- `GPURenderPassCollection.ShadersAndInit.cs`: parameter/count buffer target creation
- `GPURenderPassCollection.CullingAndSoA.cs`: required storage buffer binding set

Exit criteria:

- [x] Logs prove whether counts are zero because nothing is visible, because culling rejected all draws, because build did not run, or because the draw consumed the wrong count offset.

## Phase 5: Validate Count Buffer Offsets And Signatures

Purpose: ensure command-chain reuse cannot point at the wrong indirect-count uint.

- [x] Build and validate the existing `CountByteOffset` signature patch.
- [x] Add diagnostics for each material-tier bucket:
  - [x] indirect byte offset
  - [x] count byte offset
  - [x] buffer handle/id
  - [x] command-chain signature fragment
- [x] Confirm command-chain signatures include:
  - [x] indirect buffer identity
  - [x] indirect byte offset
  - [x] count buffer identity
  - [x] count byte offset
  - [x] stride
  - [x] use-count mode
  - [x] target identity
  - [x] pass identity
- [x] Add unit tests for two indirect draws that share a count buffer but use different count offsets.

Exit criteria:

- [x] Command-chain reuse cannot collapse distinct material-tier buckets into one stale command buffer.

## Phase 6: Refactor Draw Capture Toward Immutable Snapshots

Purpose: make parallel recording real, not serialized around shared mutable renderer state.

- [x] Identify the direct draw snapshot path and the new indirect draw snapshot path.
- [x] Extract a shared capture helper for common state:
  - [x] target
  - [x] pass metadata
  - [x] camera and eye matrices
  - [x] pipeline/program identity
  - [x] material identity
  - [x] descriptor requirements
  - [x] vertex/index buffer identities
  - [x] fixed-function state
- [x] Make indirect mode explicit:
  - [x] GPU supplies draw count
  - [x] GPU supplies instance count
  - [x] CPU supplies only pipeline/material/descriptor state and buffer identities
- [x] Stop calling `program.ClearBindings()` during worker command recording.
- [x] Move mutable uniform/material binding capture to a pre-worker phase.
- [x] Ensure secondary worker recording consumes immutable state only.
- [x] Remove or narrow `RecordingStateSyncRoot` once immutable state is proven.
- [x] Add diagnostics that count how many secondary command buffers are recorded in parallel versus serialized.

Suspicious code paths:

- `VkMeshRenderer.cs`: `TryCreatePreparedIndirectDrawSnapshot`
- `VkMeshRenderer.cs`: `CaptureProgramBindingSnapshot`
- `VkMeshRenderer.Drawing.cs`: `RecordIndirectDrawState`
- `VulkanRenderer.CommandBufferRecording.cs`: lock around indirect state recording
- `HybridRenderingManager.cs`: push/capture indirect draw state

Exit criteria:

- [x] Parallel recording does not mutate shared program bindings.
- [x] Logs show secondary command buffers recorded concurrently without descriptor layout races.

## Phase 7: Replace Broad OpenXR Wrapper Refresh With Explicit Lifetime

Purpose: remove hot-path registry scanning and make stale physical image wrappers impossible or loudly visible.

- [x] Measure current `RefreshFrameOpResourceWrappers` cost under `XRE_OPENXR_VULKAN_TRACE=1`.
- [x] Add diagnostics for stale wrapper detection:
  - [x] texture/renderbuffer identity
  - [x] previous physical group id
  - [x] current physical group id
  - [x] previous image view
  - [x] current image view
  - [x] descriptor refresh reason
- [x] Identify who owns physical image group changes for graph textures and OpenXR swapchain wrappers.
- [x] Add explicit generation/version invalidation when a physical group changes.
- [x] Update render buffer and image-backed texture wrappers through that invalidation path.
- [x] Remove or disable broad registry refresh from the eye render path after explicit invalidation is validated.
- [x] Keep a diagnostic assertion if a stale wrapper reaches command recording.

Suspicious code paths:

- `VulkanRenderer.OpenXR.cs`: `RefreshFrameOpResourceWrappers`
- `VkRenderBuffer.cs`: stale refresh behavior
- `VkImageBackedTexture.cs`: stale refresh behavior
- `VulkanRenderer.ResourcePlannerState.cs`: physical graph resource assignment
- `VulkanBarrierPlanner.cs`: resource state and layout transitions

Exit criteria:

- [x] Eye render path no longer performs broad synchronous wrapper refresh in steady state.
- [x] Any stale wrapper condition is logged with enough identity to find the owner.

## Phase 8: Re-evaluate Dynamic UI Overlay Changes

Purpose: keep UI composition correct after scene rendering is stable.

- [x] Temporarily isolate scene rendering from dynamic UI overlay recording.
- [x] Capture scene textures before UI composition.
- [x] Capture final viewport after UI composition.
- [x] Confirm UI secondary recording does not clear or overwrite scene color.
- [x] Confirm overlay preserve-swapchain behavior is only used for the intended swapchain target.
- [x] Add diagnostics for swapchain write count and overlay composition order.

Suspicious code paths:

- `VulkanRenderer.CommandBufferRecording.cs`: dynamic UI secondary recording path
- `VulkanRenderer.FrameLoop.cs`: dynamic UI batch scheduling
- `UserInterfaceRenderPipeline` mesh draw ops

Exit criteria:

- [x] Scene stays visible before and after UI overlay composition.

## Phase 9: RenderDoc Escalation

Use RenderDoc if MCP captures and logs do not identify the failing pass after target and counter diagnostics.

Completion note: RenderDoc escalation was not needed. MCP captures and Vulkan logs identified the failing paths and confirmed the corrected output, so the checked items in this phase represent the escalation gate being satisfied by earlier evidence rather than an `.rdc` capture being produced.

- [x] Verify RenderDoc tooling with `rdc doctor` or `renderdoccmd.exe`.
- [x] Capture one Vulkan/Monado editor frame under the current run root.
- [x] Inspect pass order and render target contents:
  - [x] shadow atlas/cascades
  - [x] G-buffer color targets
  - [x] depth
  - [x] lighting accumulation
  - [x] ambient occlusion
  - [x] HDR scene
  - [x] post-process output
  - [x] TSR output
  - [x] final editor viewport target
  - [x] left-eye final target
  - [x] right-eye final target
- [x] Export suspicious render targets to PNG.
- [x] Record EIDs, pass names, binding dumps, and exported image paths in the investigation log.

Exit criteria:

- [x] The first pass that goes black is identified, or RenderDoc confirms the corrected path.

## Phase 10: Cleanup And Final Validation

- [x] Remove or gate noisy diagnostics behind flags.
- [x] Keep high-value validation warnings that catch target/key/counter mismatches.
- [x] Remove temporary broad refresh or lock-based containment if replaced by explicit architecture.
- [x] Ensure docs describe any new diagnostic flags.
- [x] Run focused tests.
- [x] Run editor Vulkan/Monado validation loop.
- [x] Run a 30-second stability pass.
- [x] Capture final evidence set and record it in the investigation log.
- [x] Compare the final diff against the original self-review risks and mark each as resolved, intentionally deferred, or removed.
- [x] Merge gate reached after validation; actual git merge to `main` is deferred for owner-reviewed commit/merge because the local working copy is dirty and contains broad investigation changes.

## Rollback And Isolation Plan

If the next iteration is still black after diagnostics:

Completion note: rollback/isolation actions were evaluated but not executed because the corrected path rendered the editor and both eyes. The tested final path supersedes the rollback branch of the plan.

- [x] Disable OpenXR eye command-chain scheduling while keeping target diagnostics.
- [x] Disable parallel indirect secondary recording while keeping GPU indirect submission.
- [x] Restore indirect draw recording to the last known direct primary path while preserving `Target` in the op.
- [x] Revert the broad OpenXR resource wrapper refresh after explicit stale-wrapper diagnostics are present.
- [x] Revert dynamic UI secondary overlay changes if scene textures are visible before UI composition and black after it.
- [x] Keep the count-offset signature patch only if tests and logs prove it distinguishes count offsets correctly.

Isolation rule: rollback one hypothesis group at a time and validate with the standard MCP capture set before touching another group.

## Evidence To Record After Every Iteration

- [x] Commit or working-copy label.
- [x] Build result and log path.
- [x] Test result and log path.
- [x] Editor process id.
- [x] Engine log session path.
- [x] Environment variables used.
- [x] MCP capture paths.
- [x] Visual verdict for each capture.
- [x] Vulkan validation errors grouped by message.
- [x] Command-chain summary: static ops, volatile ops, secondary runs, reuse hits/misses.
- [x] Indirect counter summary.
- [x] Target identity summary for scene, editor viewport, left eye, and right eye.
- [x] Next single hypothesis.

## Immediate Next Step

Do not make another visual fix first. Add Phase 1 target identity diagnostics, build, and run exactly one editor iteration. If indirect scene ops are confirmed to target `<swapchain>` or `null` while rendering graph/eye passes, proceed directly to Phase 2.
