# Vulkan Deferred Rendering And Light Probe GI Fixes Todo

Last Updated: 2026-06-09
Owner: Rendering
Status: Code fixes implemented on 2026-06-09. Focused Vulkan contract tests pass and compile the touched projects. Runtime Vulkan visual/log validation is still pending after the latest device-loss/crash fixes. Symptoms were Vulkan-only: deferred meshes did not appear and light-probe capture/GI did not produce usable results. OpenGL was unaffected.
Target Branch: none. Implementation stayed in the current working tree per user request; no branch was created.

Reference run: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-09_12-54-51_pid36628`

- `log_vulkan.log` (descriptor/render-graph/layout diagnostics)
- `log_lighting.log` (probe capture/GI readiness)
- `log_meshes.log` (deferred material assignment)

Related design/todo:

- [Vulkan Dynamic Rendering Migration Todo](vulkan-dynamic-rendering-migration-todo.md)
- [DefaultRenderPipeline notes](../../../architecture/rendering/default-render-pipeline-notes.md)

## Goal

Make Vulkan deferred rendering visibly produce shaded geometry and make light-probe capture/IBL/GI produce usable probe data, matching the OpenGL path. The work splits into three layers: (1) render-graph pass identity and resource metadata so the deferred GBuffer -> light-combine -> forward chain is ordered and barriered correctly, (2) Vulkan descriptor resolution for program-bound graphics SSBOs (probe + forward-plus light buffers), and (3) explicit Vulkan resource transitions for probe capture/IBL generation.

## Implementation Summary (2026-06-09)

- Deferred pass ordering now assigns `ForwardPassFBO` clears to a real render-graph metadata pass instead of leaving them at `pass=-1`.
- The deferred light-combine quad now declares the GBuffer, AO/diffuse/BRDF, depth-view, probe array textures, and probe SSBO inputs it samples.
- Vulkan descriptor resolution now consults buffers bound through `XRRenderProgram.BindBuffer(...)`, tracks those buffers in descriptor fingerprints, and tracks program-bound textures in the active resource registry.
- Probe resource names and registry entries now match shader bindings, including the corrected `LightProbeTetrahedra` SSBO name.
- `DefaultRenderPipeline` now registers/removes probe array textures and probe SSBOs in the render resource registry like `DefaultRenderPipeline2`.
- Vulkan dynamic-rendering FBO paths now transition FBO attachments on begin/end and fall back from partial attachment layout misses to the known whole-image layout, avoiding the first-frame `Undefined` GBuffer layout.
- The first runtime validation attempt exposed invalid dynamic-rendering barrier stage/access pairs. The FBO transition helpers now choose stage/access masks from the actual old/new layouts, so shader-read layouts use shader stages and attachment layouts use attachment stages.
- Retired Vulkan image resources are now deduplicated before destruction, preventing recreated textures from destroying the same sampler/image view/image/memory handle more than once.
- Vulkan compute dispatch now reuses the last active frame context when an immediate compute call loses the active graph scope, and compute auto-uniform lookup now accepts block-name and set/binding matches.
- The graphics pipeline library extension dependency fix is covered by the Vulkan dynamic-rendering migration tests: `VK_EXT_graphics_pipeline_library` now enables/checks `VK_KHR_pipeline_library`.
- Follow-up runtime fixes from the `14:13` crash run:
  - Vulkan pipeline override stacks are thread-local so parallel secondary command-buffer recording cannot cross-contaminate Default/UI pipeline contexts.
  - Frame ops now capture pipeline context before pass validation, so pass metadata and pass index come from the same pipeline snapshot.
  - Compute dispatch no longer records in parallel secondary buckets and skips dispatch when required descriptors are unresolved, instead of submitting partially bound SSBO/storage-image descriptors.
  - Compute warning de-duplication is concurrent-safe.
  - VMA map/unmap/free/destruction accounting is serialized around native `vmaMapMemory`/`vmaUnmapMemory`, preventing the close-time `Unmapping allocation not previously mapped` assertion.
- Follow-up runtime fixes from the `14:28` crash run:
  - Vulkan compute descriptors now validate buffer usage before descriptor updates; buffers created only as vertex/transfer buffers are rejected for `StorageBuffer` bindings and the dispatch is skipped instead of submitting invalid descriptor writes.
  - GPU mesh BVH static triangle packing now uses storage-capable cloned compute views for source position/interleaved vertex data, avoiding the invalid `ArrayBuffer`-as-SSBO path hit by editor GPU picking.
  - GPU BVH pick readback buffers are mapped explicitly by the dispatcher instead of auto-mapping again during Vulkan buffer generation.
  - One-shot submit fences are destroyed even after failed/device-lost submits, preventing shutdown fence leaks.
  - Retired image cleanup removes tracked image allocations by image handle before final cleanup, preventing stale `vkDestroyImage` attempts on already-retired images.
- Follow-up runtime fixes from the `14:42` crash run:
  - Vulkan no longer enters the OpenGL-only GPU mesh BVH raycast/readback path; backend-gated picking now falls back to CPU mesh picking on Vulkan with an explicit throttled warning.
  - The BVH raycast dispatcher now requires an OpenGL renderer for fence/readback dispatch and clears queued/in-flight work if the backend is unsupported.
  - Vulkan device-loss handling now marks the device lost centrally, clears dead timeline waits, and stops one-shot command buffers, buffer uploads, texture pushes, mip generation, video uploads, and image-layout transitions from creating more Vulkan work after `VK_ERROR_DEVICE_LOST`.
  - Command-buffer teardown skips freeing primary/deferred secondary command buffers that may still be in use after device loss, avoiding validation-layer use-after-free fallout.
  - VMA allocator teardown drains tracked maps and logs live allocations instead of calling native allocator destruction while VMA still has live blocks, avoiding the close-time assertion dialog after a device-loss leak path.

## Runtime Validation Attempt (2026-06-09 14:05)

Reference run: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-09_14-05-07_pid28136`

Result: failed. The renderer lost the Vulkan device before deferred/probe rendering could be visually validated.

Primary findings:

- `log_rendering.log` reported `Failed to submit draw command buffer (ErrorDeviceLost)` at 14:05:48.987, followed by repeated `Vulkan device is lost` messages.
- `log_vulkan.log` reported invalid dynamic-rendering barriers before device loss:
  - `srcAccessMask (VK_ACCESS_SHADER_READ_BIT) is not supported by stage mask ...`
  - `dstAccessMask (VK_ACCESS_SHADER_READ_BIT) is not supported by stage mask ...`
- `log_vulkan.log` also reported duplicate/invalid retired resource destroys (`vkDestroySampler`, `vkDestroyImageView`, `vkDestroyImage`) from `DrainRetiredImages`.
- Later `vkBindImageMemory`, `vkBindBufferMemory`, and `no memory bound` errors occurred after the device had already been lost and are treated as fallout unless they recur in a clean rerun.

Follow-up fix: patched dynamic-rendering barrier stage/access resolution and retired image-resource deduplication. A fresh Vulkan editor run is still needed.

## Runtime Validation Attempt (2026-06-09 14:13)

Reference run: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-09_14-13-34_pid14556`

Result: failed. The 3D view flickered black, stopped re-rendering after Vulkan device loss, and closing the editor raised a native VMA assertion:

```text
Expression: 0 && "Unmapping allocation not previously mapped."
```

Primary findings:

- `log_general.log` reported a first-chance `InvalidOperationException` at 14:14:06 from `VkRenderProgram.WarnComputeOnce`: a non-concurrent `HashSet` was mutated while compute descriptors were recorded in parallel secondary command buffers.
- `log_vulkan.log` showed `GTAOBlur` from the Default pipeline being recorded with `UserInterfaceRenderPipeline` context and invalid pass fallback, then failing against mismatched `AmbientOcclusionTexture`/`GBufferFBO` dimensions. This points to shared runtime pipeline override state being used by parallel secondary recording.
- `log_vulkan.log` showed compute SSBO bindings such as `Hits` and `TriangleIndexBuffer` unresolved while the dispatch proceeded with incomplete descriptors. In Vulkan this is unsafe and can lead to GPU faults/device loss.
- `log_rendering.log` reported `Vulkan device lost while waiting for timeline value 326`, followed by queued render-thread `VkDataBuffer.PushData` / `MapBufferData` work waiting about 17.8 seconds.
- On close, the VMA bridge asserted because managed map-count bookkeeping could race: two teardown/readback paths could consume the same tracked map count and call `vmaUnmapMemory` twice.

Follow-up fix: patched thread-local pipeline override stacks, frame-op context capture order, compute descriptor skip behavior, compute secondary-bucket eligibility, concurrent compute warnings, and VMA map-count serialization. A fresh Vulkan editor run is still needed.

## Runtime Validation Attempt (2026-06-09 14:28)

Reference run: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-09_14-28-55_pid34680`

Result: failed. The close-time VMA assertion was gone, but the editor still lost the Vulkan device, went black/stopped re-rendering, and then shutdown produced validation errors.

Primary findings:

- `log_rendering.log` reported `Vulkan device lost while waiting for timeline value 286` at 14:30:07, followed by repeated `Vulkan device is lost` render exceptions.
- Just before the device loss, `log_vulkan.log` reported invalid compute descriptor writes from `VkRenderProgram.TryBuildAndBindComputeDescriptorSets`: a buffer created with `TRANSFER_SRC | TRANSFER_DST | VERTEX_BUFFER` was written into `VK_DESCRIPTOR_TYPE_STORAGE_BUFFER` bindings.
- The bad descriptor writes line up with editor GPU BVH picking/packing. Static mesh position/interleaved vertex buffers were being bound as SSBOs; OpenGL tolerates rebinding the same buffer name through an SSBO point, but Vulkan requires the backing buffer to have `VK_BUFFER_USAGE_STORAGE_BUFFER_BIT`.
- The same run still logged readback buffers as already mapped during `BvhRaycastDispatcher.EnsureReadbackMapping`.
- Shutdown logged stale `vkDestroyImage` calls from `DestroyRemainingTrackedImageAllocations` and leaked one-shot `VkFence` objects after device-lost submits.

Follow-up fix: patched Vulkan descriptor usage validation, GPU mesh BVH storage views for source vertex data, BVH pick readback mapping ownership, one-shot fence destruction on failed submit, and retired-image allocation removal. A fresh Vulkan editor run is still needed.

## Runtime Validation Attempt (2026-06-09 14:42)

Reference run: `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-09_14-42-15_pid30304`

Result: failed. Moving the mouse triggered editor GPU picking, the renderer lost the Vulkan device, and closing the editor raised a VMA live-allocation assertion.

Primary findings:

- `log_rendering.log` showed repeated `Invoke:XRWorldInstance.GpuMeshBvhPick` jobs, culminating in a 17.65s render-thread stall at 14:43:32.
- Immediately after that stall, rendering reported `Vulkan device lost while waiting for timeline value 246`.
- `log_vulkan.log` showed one-shot transfer submission failure at the same time: `WaitForFences for one-shot submit failed (result=ErrorDeviceLost)`, followed by many `One-shot QueueSubmit failed (result=ErrorDeviceLost)` messages.
- Post-loss resource churn continued until shutdown, reaching `activeVkAllocations=5054` and `allocatorBytes=1021047984`.
- Shutdown then reported `DeviceWaitIdle returned ErrorDeviceLost` and cleanup stack traces from `DestroyRemainingTrackedBufferAllocations`, matching the close-time VMA assertion that some allocations were still live when the allocator was destroyed.

Follow-up fix: gated GPU BVH picking to OpenGL only, restored Vulkan CPU mesh picking fallback, added device-lost guards for one-shot command buffers and resource upload/layout paths, and made VMA teardown log live allocations instead of asserting during device-lost shutdown. A fresh Vulkan editor run is still needed.

## Evidence Summary (verified 2026-06-09)

Confirmed directly against the reference run and source:

- Deferred materials are imported as deferred, not forward-only. `log_meshes.log` shows `[MakeMaterialDeferred] ... shader=ColoredDeferred/TexturedNormalDeferred/TexturedNormalSpecDeferred ... pass=1`.
- The deferred GBuffer pass runs with real geometry. `log_vulkan.log` shows `BeginRendering FBO='DeferredGBufferFBO' ... attachments=5` and frames with `draws=103`, `draws=133`. So this is not a "no deferred submission" problem.
- Render-graph pass identity is intermittently unresolved. Early frames record `BeginRendering FBO='DeferredGBufferFBO' pass=-1` and `[FwdClear] ForwardPassFBO clear pass=-1`; later frames show `pass=1`. The `pass=-1` periods correlate with probe batch capture being in flight.
- First GBuffer begin reports an uninitialized attachment layout: `trackedLayouts=ShaderReadOnlyOptimal,ShaderReadOnlyOptimal,ShaderReadOnlyOptimal,Undefined,DepthStencilReadOnlyOptimal` (attachment index 4 = `Undefined`).
- Probe SSBOs fall back to zero buffers every frame: `Using fallback descriptor buffer for unresolved StorageBuffer binding 'LightProbePositions' / 'LightProbeTetrahedra' / 'LightProbeParameters' / 'LightProbeGridCells' / 'LightProbeGridIndices'`, preceded by `Descriptor binding 'LightProbePositions' could not be matched to an engine uniform.`
- Forward/Forward+ light SSBOs also fall back: `ForwardPlusLocalLightsBuffer`, `ForwardPlusVisibleIndicesBuffer`, `ForwardDirectionalLightsBuffer`, `ForwardPointLightsBuffer`, `ForwardSpotLightsBuffer`, `ForwardPointShadowMetadataBuffer`, `ForwardSpotShadowMetadataBuffer`.
- Probe capture is slow and unusable for ~1 minute, then reaches ready. `log_lighting.log` shows repeated `[ProbeGI] No usable probes. Total=36, Ready=0 (need valid irradiance+prefilter textures and CaptureVersion>0)` for the first ~65s, a sequential batch (`Completed 36/36 probes. totalMs=34599.85 avgProbeMs=961.11`), then `[ProbeGI] structural refresh ready=36 ... deferredByBatchCapture=True`. Even at ready=36 the SSBOs above still resolve to zero buffers.
- Compute dispatches escape the render graph: `DispatchCompute skipped for 'UnnamedProgram' because no active render-graph pass could be resolved` and `[VkCompute:UnnamedProgram] Using zero-filled cached fallback uniform buffer for unresolved binding 'XREngine_AutoUniforms_Compute'` (auto exposure).

Source confirmation:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs` `TryResolveBuffer` resolves buffers only from the mesh-renderer `_bufferCache`, auto-uniforms, and engine-uniforms. There is no path that consults program-bound graphics SSBOs (`XRRenderProgram.BindBuffer(...)`), so probe/forward-plus SSBOs always hit the fallback path.
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderQuadToFBO.cs` `DescribeRenderPass` declares only `builder.SampleTexture(MakeFboColorResource(SourceQuadFBOName))` for the light-combine quad. It does not declare GBuffer color/normal/depth, AO/lighting/BRDF, probe arrays, or probe SSBOs as sampled inputs, so the render graph has incomplete resource usage for the `LightCombineFBO -> ForwardPassFBO` blit.

### Interpretation / prioritization note

The most likely cause of *missing* deferred meshes is the render-graph metadata and clear/ordering issues (the GBuffer writes albedo regardless of SSBO state). The SSBO fallbacks primarily break *lighting/GI correctness*, which would make deferred surfaces look black/unlit rather than absent. Prioritize the render-graph and layout work first to restore visible geometry, then the descriptor work to restore correct lighting/GI.

## Phase 1 - Render-Graph Pass Identity And Resource Metadata (visible-geometry blockers)

- [x] **1.1** Determine why `DeferredGBufferFBO` and `ForwardPassFBO` record `pass=-1` on some frames and `pass=1` on others; the `pass=-1` periods correlate with in-flight probe batch capture. Document the cause before changing behavior.
  Cause: immediate clear/compute/probe work could execute after losing the active graph scope, so Vulkan had no pass metadata for some FBO operations. Probe capture made this visible by interleaving offscreen work with the main graph.
- [x] **1.2** Give the `ForwardPassFBO` clear a real render-graph pass identity/order instead of `pass=-1`, or fold the clear into the first Vulkan dynamic-rendering load op for `ForwardPassFBO`, so it cannot wipe the light-combine result.
- [x] **1.3** Verify clear vs light-combine ordering: confirm the `[FwdClear] ForwardPassFBO clear` cannot be sorted/emitted after the `LightCombineFBO -> ForwardPassFBO` quad.
- [x] **1.4** Extend `VPRC_RenderQuadToFBO.DescribeRenderPass` so the deferred light-combine quad declares all resources `DeferredLightCombine.fs` actually samples: GBuffer color/normal/depth, AO/lighting/BRDF textures, probe array textures, and probe SSBOs. Implemented as an explicit resource list for the shader; automatic program-resource enumeration remains a future hardening improvement.
- [x] **1.5** Investigate the `Undefined` GBuffer attachment layout in the first `BeginRendering` (`trackedLayouts=...,Undefined,...` at attachment index 4). Ensure every GBuffer attachment has a defined initial layout / load op before it is sampled by the light-combine pass.
- [x] **1.6** Confirm the deferred GBuffer attachments transition to a sampled layout before the light-combine quad reads them, and that the transition is described in the render graph (not relying on implicit global state the way OpenGL tolerates).

## Phase 2 - Vulkan Descriptor Resolution For Program-Bound Graphics SSBOs (lighting/GI correctness)

- [x] **2.1** Add a resolution path in `VkMeshRenderer.Descriptors.cs` `TryResolveBuffer` (or an earlier step) that consults graphics SSBOs bound through `XRRenderProgram.BindBuffer(...)`, not just the mesh-renderer `_bufferCache`, auto-uniforms, and engine-uniforms.
- [x] **2.2** Confirm `DefaultRenderPipeline` binds the probe buffers onto the program (the probe-buffer bind path around the GI structural refresh) and that those binds are visible to the Vulkan descriptor resolver.
- [x] **2.3** Include program-bound graphics SSBOs in the descriptor resource fingerprint so descriptor sets rebuild when probe/light buffers change (e.g. after `[ProbeGI] structural refresh ready=36`).
- [x] **2.4** Resolve the probe SSBOs to real buffers: `LightProbePositions`, `LightProbeTetrahedra`, `LightProbeParameters`, `LightProbeGridCells`, `LightProbeGridIndices`.
- [x] **2.5** Resolve the forward/forward-plus light SSBOs to real buffers: `ForwardPlusLocalLightsBuffer`, `ForwardPlusVisibleIndicesBuffer`, `ForwardDirectionalLightsBuffer`, `ForwardPointLightsBuffer`, `ForwardSpotLightsBuffer`, `ForwardPointShadowMetadataBuffer`, `ForwardSpotShadowMetadataBuffer`.
- [ ] **2.6** After binding works, confirm the `Descriptor binding '...' could not be matched to an engine uniform` and `Using fallback descriptor buffer for unresolved StorageBuffer binding '...'` warnings stop appearing in `log_vulkan.log`.

## Phase 3 - Light Probe Capture And IBL On Explicit Vulkan Rails

- [x] **3.1** Audit `XREngine.Runtime.Rendering/Scene/Components/Capture/SceneCaptureComponent.cs` (probe face capture) for direct FBO bind/render/mipmap work that assumes OpenGL implicit state.
- [x] **3.2** Audit `XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.IBL.cs` (irradiance/prefilter/mip generation) for the same.
- [x] **3.3** Convert probe face capture, octahedral encode, irradiance convolution, prefilter, mip generation, and probe-array uploads into Vulkan-described render commands/passes with explicit image layout transitions, so the batch cannot "complete" with textures left in the wrong layout/contents for later sampling.
  Implementation note: the immediate capture API remains in place, but Vulkan dynamic-rendering FBO command recording now performs explicit begin/end attachment transitions and updates physical image layouts. A larger pipeline-script refactor is no longer required for this bug, but may still be useful cleanup.
- [x] **3.4** Resolve the cubemap/image layout transition stack traces logged during probe texture work; ensure capture targets end in a sampled layout.
- [ ] **3.5** Verify `[ProbeGI] structural refresh ready=36` corresponds to GPU textures whose contents and layouts are valid for the deferred light-combine and forward-plus lookups.

## Phase 4 - Compute Dispatch Render-Graph Membership

- [x] **4.1** Fix compute dispatches that run outside a render-graph pass (`DispatchCompute skipped ... no active render-graph pass could be resolved`), starting with auto exposure.
- [x] **4.2** Resolve the `XREngine_AutoUniforms_Compute` zero-filled fallback uniform buffer for compute programs.
- [x] **4.3** Ensure compute passes participate in render-graph ordering so resources they produce/consume are synchronized, not left in indeterminate state between graphics passes.

## Phase 5 - Diagnostics And Isolation Tooling

- [ ] **5.1** Add a Vulkan debug mode/dump for GBuffer albedo and depth so missing deferred output can be isolated visually in a single run.
- [ ] **5.2** Add a Vulkan debug dump for probe irradiance/prefilter textures to confirm capture contents independently of the descriptor path.
- [ ] **5.3** Capture a before/after comparison against the OpenGL path for the same Unit Testing World scene.

Diagnostic tooling was not added as part of the code fix. Keep this phase open until a runtime validation run confirms whether extra dump tooling is still needed.

## Validation Checklist

### Build And Targeted Tests

- [ ] Build the editor:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

  Latest focused test run compiled `XREngine.Runtime.Rendering`, `XREngine`, `XREngine.Editor`, and `XREngine.UnitTests` successfully. A standalone explicit editor build command was not rerun after the 14:42 crash fixes.

- [x] Run Vulkan descriptor / render-graph unit tests touched by the fixes:

  ```powershell
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanDeferredProbeGiFixesTests"
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~VulkanDynamicRenderingMigrationTests"
  ```

  Note: the broad `--filter Vulkan` run timed out locally because it includes heavier Vulkan shader/compiler tests. The focused contract suites above passed.

### Runtime Scenarios (Vulkan)

- [ ] Run the editor Unit Testing World on Vulkan and confirm deferred Sponza geometry is visible and shaded. Latest attempt (`xrengine_2026-06-09_14-42-15_pid30304`) failed from Vulkan device loss during editor GPU BVH picking; follow-up backend-gating and device-lost cleanup fixes are in and need rerun.
- [ ] Confirm light-probe capture completes and GI visibly affects deferred surfaces.
- [ ] Confirm `log_vulkan.log` no longer reports probe/forward-plus SSBO fallbacks.
- [ ] Confirm `log_vulkan.log` no longer reports `pass=-1` for `DeferredGBufferFBO` / `ForwardPassFBO` clears.
- [ ] Confirm no `Undefined` GBuffer attachment layout at first `BeginRendering`.
- [ ] Confirm no `DispatchCompute skipped ... no active render-graph pass` warnings.
- [ ] Run under Vulkan validation layers and confirm no layout/synchronization errors for the deferred + probe paths. Last attempt found invalid dynamic-rendering barrier stage/access pairs; code fix is in and needs rerun.

### Visual Criteria

- [ ] Deferred meshes appear on Vulkan (parity with OpenGL).
- [ ] Probe GI/ambient is present and not black.
- [ ] No forward content wiping the deferred base color.
- [ ] Dynamic and legacy/OpenGL paths produce visually comparable output for the same scene.
