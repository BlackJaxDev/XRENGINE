# Vulkan Dynamic Rendering Migration Todo (Remaining Work)

Last Updated: 2026-06-09
Owner: Rendering
Status: Core migration is implemented and the audited completed items verified against source on 2026-06-09. `Auto` resolves to dynamic rendering when supported, explicit legacy remains selectable, and explicit dynamic fails visibly when unsupported. The three source-verifiable migration tests pass (3/3). This document now tracks only the work that remains.
Target Branch: intentionally skipped; user requested not to branch.

Design sources:

- [Vulkan Dynamic Rendering Migration Design](../../design/rendering/vulkan-dynamic-rendering-migration-design.md)
- [Vulkan Shader Object Pipeline Replacement](../../design/rendering/vulkan-shader-object-pipeline-replacement-design.md)
- Vulkan deprecation appendix: https://vulkan.lunarg.com/doc/view/1.4.328.1/linux/antora/spec/latest/appendices/deprecation.html
- Vulkan Guide deprecated functionality: https://docs.vulkan.org/guide/latest/deprecated.html
- Dynamic rendering sample: https://docs.vulkan.org/samples/latest/samples/extensions/dynamic_rendering/README.html
- Dynamic rendering local read proposal: https://docs.vulkan.org/features/latest/features/proposals/VK_KHR_dynamic_rendering_local_read.html

## Goal

Finish hardening explicit dynamic rendering across every Vulkan graphics target (swapchain, offscreen FBO, post-process, shadow, capture, VR mirror, debug, ImGui) and validate it under the Vulkan validation layers. The legacy render-pass/framebuffer path stays selectable through the runtime toggle and is not removed.

High-level engine render-pass concepts remain unchanged. `RenderPassMetadata`, `RenderPassBuilder`, `EDefaultRenderPass`, and pass indices describe engine graph scheduling and resource usage, not Vulkan `VkRenderPass` objects.

## Audit Summary (2026-06-09)

Verified directly against source and confirmed correctly implemented:

- Runtime target mode enum, `XRE_VK_RENDER_TARGET_MODE` override, and visible unsupported-dynamic failure in `VulkanRenderTargetMode.cs`.
- `Auto` selects dynamic when supported and legacy only when unsupported; startup diagnostics report requested/resolved mode (`[Vulkan] Render target mode:`).
- Swapchain and FBO dynamic paths record `CmdBeginRendering` / `CmdEndRendering` with explicit layout barriers; legacy `CmdBeginRenderPass` is mode-gated.
- Swapchain present transition to `PresentSrcKhr` occurs exactly once per dynamic frame and is runtime-asserted.
- Dynamic-path pipeline and prewarm keys use `DynamicRenderingFormatSignature` (ordered color formats, depth, stencil, sample count) with no `VkRenderPass` handle.
- `RenderPasses.cs` / `FrameBuffers.cs` create swapchain `VkRenderPass` / `VkFramebuffer` only in legacy mode.
- Source-verifiable migration tests pass: `VulkanDynamicRenderingMigrationTests` 3/3.

Phases 0, 1, and 10 are fully complete and are not reproduced below.

## Operating Rules (Open)

- [ ] Avoid heap allocations in per-frame command recording, target planning, and draw submission hot paths.

All other operating rules were satisfied during the core migration.

## Phase 2 - Shared Dynamic Rendering Scope

- [ ] **2.1** Extract the current swapchain dynamic-rendering branch from `BeginRenderPassForTarget` into a reusable dynamic rendering scope helper.
- [ ] **2.2** Rename or wrap `BeginRenderPassForTarget` with a dynamic-aware name such as `BeginRenderingForTarget` without disturbing call sites prematurely.
- [ ] **2.3** Add a lightweight target plan type for dynamic rendering scopes.
- [ ] **2.4** Include these fields in the plan:
  - render area
  - layer count
  - view mask
  - color attachment plans
  - depth attachment plan
  - stencil attachment plan
  - read-only depth/stencil state
  - color/depth/stencil formats
  - sample count
  - semantic signature
- [ ] **2.5** Add a lightweight attachment plan type with:
  - image
  - image view
  - format
  - aspect mask
  - initial layout
  - rendering layout
  - final layout
  - load op
  - store op
  - clear value
  - resolve image view and resolve mode
- [ ] **2.6** Keep the scope plan stack-backed or otherwise allocation-free in command recording.
- [ ] **2.7** Preserve current swapchain dynamic-rendering behavior exactly through the new shared helper.
- [ ] **2.8** Preserve the existing invariant that a dynamic-rendering swapchain frame transitions to `PresentSrcKhr` exactly once before submit/present.
- [ ] **2.9** Keep the legacy swapchain render-pass branch selectable through the runtime mode.
- [ ] **2.10** Add tests/source checks that the shared dynamic path still uses `CmdBeginRendering` and `CmdEndRendering`.

## Phase 3 - Swapchain Dynamic Path Hardening

- [ ] **3.1** Move swapchain target planning into the shared target planner.
- [ ] **3.2** Ensure the swapchain plan includes:
  - active swapchain image and image view
  - swapchain depth image and view
  - `swapChainImageFormat`
  - `_swapchainDepthFormat`
  - `SampleCountFlags.Count1Bit`
  - `swapChainExtent`
- [ ] **3.3** Preserve first-use/re-entry layout rules:
  - `PresentSrcKhr`
  - `ColorAttachmentOptimal`
  - `Undefined`
- [ ] **3.4** Preserve first-entry clear and re-entry load behavior.
- [ ] **3.5** Preserve depth clear behavior.
- [ ] **3.6** Keep final swapchain transition diagnostics.
- [ ] **3.7** Validate resize/minimize/recreate does not reuse stale command buffers or image views.
- [ ] **3.8** Validate ImGui overlay still renders in dynamic mode.
- [ ] **3.9** Validate explicit legacy mode still renders the swapchain through `_renderPass` / `_renderPassLoad`.

## Phase 4 - Dynamic Rendering For Simple FBOs (Open Items)

Planning, conversion, pipeline keying, and diagnostics for simple FBOs are implemented. Remaining items are runtime validation:

- [ ] **4.11** Validate Unit Testing World with dynamic swapchain plus simple dynamic FBOs.
- [ ] **4.12** Validate `ForwardPassFBO` and deferred GBuffer writes survive compute/blit interruptions and render-scope re-entry.

## Phase 5 - Full FBO Coverage (Open Items)

Multiple color attachments, depth-only, depth/stencil, stencil-only, mip-level, array-layer, cubemap-face, and read-only depth/stencil targets are implemented. Remaining:

- [ ] **5.8** Support resolve attachments with dynamic rendering resolve fields.
- [ ] **5.9** Support transient attachments.
- [ ] **5.11** Support shadow map targets.
- [ ] **5.12** Support bloom/downsample/upsample targets.
- [ ] **5.13** Support cubemap and texture-array capture targets.
- [ ] **5.14** Support VR mirror targets that route through Vulkan FBOs.
- [ ] **5.15** Validate DefaultRenderPipeline in dynamic mode.
- [ ] **5.16** Validate DefaultRenderPipeline2 in dynamic mode when applicable.
- [ ] **5.17** Validate explicit legacy mode still renders the same FBO scenarios.

## Phase 6 - Synchronization And Layout Tracking (Open Items)

Explicit barriers, attachment entry/exit layouts, dynamic-scope final states, and compute/blit interruption handling are implemented. Remaining:

- [ ] **6.2** Reuse `VulkanBarrierPlanner` and `RenderGraphSynchronizationPlanner` where possible.
- [ ] **6.5** Make the planner aware of swapchain target resources.
- [ ] **6.6** Make the planner aware of FBO attachments using stable semantic resource identities.
- [ ] **6.12** Validate no layout errors under Vulkan validation layers.

## Phase 7 - Pipeline Signature And Prewarm Cleanup (Open Items)

Dynamic-path keys, format/sample-count fields, semantic prewarm signatures, and diagnostics are implemented. Remaining:

- [ ] **7.11** Add view mask to dynamic-path keys.
- [ ] **7.16** Verify pipeline cache miss summaries trend toward zero after warmup in dynamic mode.

## Phase 8 - Secondary Command Buffer Support (Open Items)

2026-06-09 audit note: current secondary buckets are limited to `BlitOp` and `IndirectDrawOp`, and the primary command buffer ends active rendering before those paths execute. `ComputeDispatchOp` records on the primary path so per-program descriptor state is not touched by parallel secondary workers. No secondary graphics recording currently runs inside dynamic rendering; the inheritance items below remain open for any future secondary graphics path.

- [ ] **8.3** Add `CommandBufferInheritanceRenderingInfo` for secondary graphics command buffers recorded inside dynamic rendering.
- [ ] **8.4** Ensure inherited formats match active color/depth/stencil formats.
- [ ] **8.5** Ensure inherited sample count matches active rendering scope.
- [ ] **8.6** Ensure inherited view mask matches active rendering scope.
- [ ] **8.8** Validate parallel/secondary recording in dynamic mode if enabled.

## Phase 9 - Resolve, Multiview, And Stereo

- [ ] **9.1** Preserve `ERenderPassResourceType.ResolveAttachment` metadata.
- [ ] **9.2** Map resolve attachments to `RenderingAttachmentInfo.ResolveMode`.
- [ ] **9.3** Map resolve attachments to `RenderingAttachmentInfo.ResolveImageView`.
- [ ] **9.4** Add explicit layout transitions for resolve targets.
- [ ] **9.5** Validate resolve source and target sample-count requirements.
- [ ] **9.6** Validate resolve formats match Vulkan requirements.
- [ ] **9.7** Add dynamic rendering `ViewMask` support for multiview.
- [ ] **9.8** Add view mask to pipeline compatibility and diagnostics.
- [ ] **9.9** Ensure stereo target planning does not infer view count from texture array length alone.
- [ ] **9.10** Validate stereo, OpenVR mirror, and OpenXR-related paths that use Vulkan targets.

## Phase 11 - Optional Vulkan 1.4 Local Read

- [ ] **11.1** Inventory passes that would benefit from framebuffer-local dependencies.
- [ ] **11.2** Query and expose `VK_KHR_dynamic_rendering_local_read` / Vulkan 1.4 support.
- [ ] **11.3** Prototype local-read barriers only for a pass with a real need.
- [ ] **11.4** Validate tiled deferred or VR use cases before broad adoption.
- [ ] **11.5** Keep local read optional until the engine has a required Vulkan 1.4 tier.

## Phase 12 - Modern Follow-On Backlog

These are not required to keep the dynamic rendering default, but should stay visible because they share capability plumbing with this migration and the later shader-object work.

- [ ] **12.1** Define a Vulkan 1.4 opt-in baseline tier.
- [ ] **12.2** Add startup reporting for Vulkan 1.3, Vulkan 1.4, dynamic rendering, local read, synchronization2, timeline semaphore, descriptor indexing, buffer device address, mesh shader, fragment shading rate, and graphics pipeline library support.
- [ ] **12.3** Evaluate foveated rendering / variable rate shading for VR.
- [ ] **12.4** Evaluate `VK_KHR_fragment_shading_rate`.
- [ ] **12.5** Evaluate `VK_EXT_fragment_density_map` if relevant to XR runtime integration.
- [ ] **12.6** Evaluate Sync2 as the default Vulkan synchronization backend.
- [ ] **12.7** Audit timeline semaphore usage for frame pacing and async transfer/compute overlap.
- [ ] **12.8** Expand memory budget reporting and residency diagnostics.
- [ ] **12.9** Add attachment transient/lazily allocated policy for depth and temporary color targets where supported.
- [ ] **12.10** Share capability plumbing with the shader-object design.

## Validation Checklist (Remaining)

Static validation (no unintended `CreateRenderPass` / `CreateFramebuffer` / `CmdBeginRenderPass` in the dynamic path, `PipelineRenderingCreateInfo` always present, attachment-format keys, legacy isolation) is complete and verified by `VulkanDynamicRenderingMigrationTests`.

### Build And Unit Tests

- [ ] Run targeted Vulkan tests:

  ```powershell
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter Vulkan
  ```

  2026-06-09 note: `--filter VulkanDynamicRenderingMigrationTests` passed 3/3. A broader Vulkan-focused filter passed 59 tests and failed 10 stale source-path assertions that still look under `XRENGINE\Rendering\API\Rendering\Vulkan\...` instead of `XREngine.Runtime.Rendering\Rendering\API\Rendering\Vulkan\...`. Fix the stale source-path assertions before relying on the broad filter.

- [ ] Before final promotion, run:

  ```powershell
  dotnet restore
  dotnet build XRENGINE.slnx
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj
  ```

### Runtime Scenarios

- [ ] Run editor default startup in dynamic mode with validation layers.
- [ ] Run editor `--unit-testing` in dynamic mode with validation layers.
- [ ] Run editor default startup in explicit legacy mode with validation layers.
- [ ] Run editor `--unit-testing` in explicit legacy mode with validation layers.
- [ ] Resize and recreate the swapchain repeatedly.
- [ ] Validate deferred scene with GBuffer writes.
- [ ] Validate forward scene with depth read from deferred prepass.
- [ ] Validate transparent/weighted blended OIT pass.
- [ ] Validate bloom/downsample/upsample.
- [ ] Validate shadow map rendering.
- [ ] Validate cubemap capture.
- [ ] Validate texture-array capture.
- [ ] Validate ImGui overlay.
- [ ] Validate compute pass between graphics passes.
- [ ] Validate blit pass between graphics passes.
- [ ] Validate forced magenta diagnostic path.
- [ ] Validate pipeline rebuild / material shader invalidation.
- [ ] Validate VR mirror path if Vulkan mirror rendering is active.

### Visual Criteria

- [ ] No black frame.
- [ ] No stale-frame flash after resize.
- [ ] No lost GBuffer color content.
- [ ] No lost GBuffer depth content.
- [ ] No forward pass depth rejection caused by discarded depth.
- [ ] No bloom mip size mismatch.
- [ ] No missing ImGui.
- [ ] No shadow atlas/layer corruption.
- [ ] No presentation layout validation error.
- [ ] Dynamic and legacy modes produce visually comparable output for the same scene.

### Performance Criteria

- [ ] No new per-draw heap allocations.
- [ ] No LINQ in command recording or target planning hot paths.
- [ ] No per-frame render target dictionary rebuild when target identity is unchanged.
- [ ] No pipeline explosion from missing attachment-key fields.
- [ ] Pipeline cache miss summary trends toward zero after warmup.
- [ ] No extra `CmdPipelineBarrier` spam when layout is already correct.
- [ ] Dynamic mode does not regress frame pacing versus legacy mode beyond an explained tolerance.

## Diagnostics To Add Or Update

- [ ] `Vulkan.DynamicRendering.Scope.Begin`
- [ ] `Vulkan.DynamicRendering.Scope.End`
- [ ] `Vulkan.DynamicRendering.AttachmentPlan`
- [ ] `Vulkan.DynamicRendering.LayoutMismatch`
- [ ] `Vulkan.DynamicRendering.UnsupportedAttachment`
- [ ] `Vulkan.DynamicRendering.PresentTransitions`
- [ ] `Vulkan.RenderTargetMode`
- [ ] `Vulkan.RenderTargetMode.Fallback`
- [ ] `Vulkan.Pipeline.AttachmentSignature`

Each diagnostic should include the relevant subset of:

- pass index and pass name
- target name
- runtime rendering mode
- attachment role/index
- image handle
- image view handle
- mip level and layer
- old/render/final layouts
- load/store ops
- color/depth/stencil formats
- sample count
- view mask
- fallback reason

Keep high-frequency logs behind existing throttles or explicit trace flags.

## Files Expected To Change (Remaining)

- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanBarrierPlanner.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderGraphCompiler.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Extensions.cs`

Already-changed files (`CommandBuffers.cs`, `LogicalDevice.cs`, `VkFrameBuffer.cs`, `VkMeshRenderer.Pipeline.cs`, `VkMeshRenderer.Drawing.cs`, `VulkanRenderTargetMode.cs`, `VulkanPipelinePrewarmDatabase.cs`, `VulkanRenderer.ImGui.cs`, the Vulkan unit tests, and the architecture/todo docs) plus the runtime-mode-isolated legacy files (`RenderPasses.cs`, `FrameBuffers.cs`, `FrameBufferRenderPasses.cs`) are complete.

## Risks To Track

- [ ] Lost implicit layout transitions.
- [ ] FBO re-entry clears preserved contents.
- [ ] Dynamic pipeline key omits attachment compatibility.
- [ ] Legacy pipeline key accidentally stops including render-pass compatibility.
- [ ] Mip/layer FBO extents regress.
- [ ] Depth read-only passes write depth accidentally.
- [ ] Secondary command buffers inherit stale render-pass info.
- [ ] Resolve attachment behavior diverges.
- [ ] Unsupported dynamic rendering falls back without a visible reason.
- [ ] Terminology confusion between engine render passes and Vulkan `VkRenderPass`.

## Open Questions

- [ ] Should explicit dynamic mode fail at initialization or only when the first graphics target is used?
- [ ] Should dynamic rendering local read become part of a future required Vulkan 1.4 tier?
- [ ] How much of `VkFrameBuffer` should survive as a Vulkan-specific attachment-view cache versus moving into a renderer-level target cache?
- [ ] Should render graph metadata become the only source of load/store behavior, or should `XRFrameBuffer` keep explicit overrides for hand-authored target flows?
- [ ] Should source files be renamed away from "RenderPass" after dynamic mode is default, even though the legacy path is retained?

## Final Promotion (Remaining)

- [ ] **F.1** Confirm every acceptance criterion below is satisfied.
- [ ] **F.2** Update PR notes with what changed, why, validation performed, risks, and follow-ups.

Acceptance criteria still open:

- [ ] Unit Testing World and default editor startup render correctly in dynamic mode under Vulkan validation.
- [ ] Unit Testing World and default editor startup render correctly in explicit legacy mode under Vulkan validation.

Acceptance criteria already satisfied (kept for reference):

- Dynamic rendering is the default in `Auto` mode when supported; explicit legacy still renders through the retained `VkRenderPass` / `VkFramebuffer` path; explicit dynamic fails visibly when unsupported.
- All dynamic-path graphics recording uses `CmdBeginRendering` / `CmdEndRendering` and all dynamic-path pipelines use `PipelineRenderingCreateInfo`.
- Dynamic-path pipeline and prewarm keys exclude `VkRenderPass` / `VkFramebuffer` handles; legacy keys retain render-pass compatibility.
- Swapchain present transition occurs exactly once per submitted dynamic frame.
- Relevant Vulkan architecture and manual-validation docs are updated.
