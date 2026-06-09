# Vulkan Dynamic Rendering Migration Todo

Last Updated: 2026-06-09
Owner: Rendering
Status: Todo extracted from the dynamic-rendering design. Dynamic rendering is already used for the swapchain main path when supported; the remaining work is to make it the default Vulkan graphics target path for every target while keeping the legacy `VkRenderPass` / `VkFramebuffer` path behind one runtime toggle for fallback and regression bisection.
Target Branch: `vulkan-dynamic-rendering-migration`

Design sources:

- [Vulkan Dynamic Rendering Migration Design](../../design/rendering/vulkan-dynamic-rendering-migration-design.md)
- [Vulkan Shader Object Pipeline Replacement](../../design/rendering/vulkan-shader-object-pipeline-replacement-design.md)
- Vulkan deprecation appendix: https://vulkan.lunarg.com/doc/view/1.4.328.1/linux/antora/spec/latest/appendices/deprecation.html
- Vulkan Guide deprecated functionality: https://docs.vulkan.org/guide/latest/deprecated.html
- Dynamic rendering sample: https://docs.vulkan.org/samples/latest/samples/extensions/dynamic_rendering/README.html
- Dynamic rendering local read proposal: https://docs.vulkan.org/features/latest/features/proposals/VK_KHR_dynamic_rendering_local_read.html

## Goal

Make explicit dynamic rendering the default Vulkan graphics target path for swapchain, offscreen FBO, post-process, shadow, capture, VR mirror, debug, and ImGui rendering.

The migration does **not** delete the legacy render-pass/framebuffer path. The legacy path remains selectable through a single runtime toggle so regressions can be bisected and devices without dynamic-rendering support can still render. Unsupported or incorrect dynamic-rendering states must fail visibly with diagnostics; the only sanctioned automatic fallback is selecting the legacy path when dynamic rendering is unsupported, and that selection must be logged.

High-level engine render-pass concepts remain unchanged. `RenderPassMetadata`, `RenderPassBuilder`, `EDefaultRenderPass`, and pass indices describe engine graph scheduling and resource usage, not Vulkan `VkRenderPass` objects.

## Current Baseline

- `Objects/LogicalDevice.cs` queries and enables `PhysicalDeviceDynamicRenderingFeatures` when Vulkan 1.3 core support or `VK_KHR_dynamic_rendering` is available.
- `Extensions.cs` exposes `SupportsDynamicRendering`.
- `Objects/CommandBuffers.cs` already uses `CmdBeginRendering` / `CmdEndRendering` for the swapchain main path when supported.
- Swapchain dynamic rendering explicitly transitions the swapchain image to `ColorAttachmentOptimal`, renders, then transitions to `PresentSrcKhr` on final close.
- Swapchain fallback still uses `_renderPass`, `_renderPassLoad`, `swapChainFramebuffers`, and `CmdBeginRenderPass`.
- Offscreen `XRFrameBuffer` targets still use `VkFrameBuffer.ResolveRenderPassForPass`, `VkFramebuffer`, and `CmdBeginRenderPass`.
- `FrameBufferRenderPasses.cs` owns the FBO-specific `VkRenderPass` cache and attachment-signature planning.
- `Objects/RenderPasses.cs` creates swapchain `VkRenderPass` objects.
- `Objects/FrameBuffers.cs` creates swapchain `VkFramebuffer` objects.
- `VkMeshRenderer.Pipeline.cs` already supports dynamic rendering pipeline creation with `PipelineRenderingCreateInfo`.
- ImGui already creates a dynamic-rendering-compatible graphics pipeline when `SupportsDynamicRendering` is true.

## Operating Rules

- [ ] Preserve the legacy render-pass/framebuffer path behind a runtime toggle.
- [ ] Keep the OpenGL backend unaffected.
- [ ] Do not replace `VkPipeline` in this work; shader-object replacement remains a separate design.
- [ ] Do not rewrite `DefaultRenderPipeline` or `DefaultRenderPipeline2` as part of this migration.
- [ ] Do not add CPU fallback rendering paths.
- [ ] Keep load/store/clear behavior identical between dynamic and legacy paths unless a deliberate bug fix is documented.
- [ ] Keep transient Vulkan handles out of dynamic-path graphics pipeline keys and prewarm manifests.
- [ ] Avoid heap allocations in per-frame command recording, target planning, and draw submission hot paths.
- [ ] Prefer explicit diagnostics over silent fallback.

## Phase 0 - Branch And Inventory

- [ ] **0.1** Create a dedicated branch for this work, for example `vulkan-dynamic-rendering-migration`.
- [ ] **0.2** Capture a fresh inventory of production `VkRenderPass` and `VkFramebuffer` usage:

  ```powershell
  rg -n "CreateRenderPass|DestroyRenderPass|CreateFramebuffer|DestroyFramebuffer|CmdBeginRenderPass|CmdEndRenderPass|RenderPassBeginInfo|FramebufferCreateInfo" XREngine.Runtime.Rendering\Rendering\API\Rendering\Vulkan
  ```

- [ ] **0.3** Classify every hit as one of:
  - legacy path to keep behind the runtime toggle
  - dynamic path code that must be migrated
  - high-level engine render-pass concept that should stay
  - documentation or diagnostic reference
- [ ] **0.4** Add source-verifiable tests that prevent accidental new dynamic-path dependencies on `VkRenderPass` / `VkFramebuffer`.
- [ ] **0.5** Add allowlist comments or test exemptions for the intentionally retained legacy path.
- [ ] **0.6** Audit current docs and logs for wording that conflates engine render passes with Vulkan `VkRenderPass`.
- [ ] **0.7** Decide the exact runtime toggle name and setting location.
- [ ] **0.8** Document the toggle in this todo and later in `docs/architecture/rendering/vulkan-renderer.md`.

## Phase 1 - Runtime Path Selection

- [ ] **1.1** Add a Vulkan rendering target mode enum:
  - `DynamicRendering`
  - `LegacyRenderPass`
  - `Auto`
- [ ] **1.2** Add a user/developer setting and environment override for the mode, for example `XRE_VK_RENDER_TARGET_MODE`.
- [ ] **1.3** Make `Auto` select dynamic rendering when `SupportsDynamicRendering` is true.
- [ ] **1.4** Make `Auto` select the legacy path only when dynamic rendering is unsupported.
- [ ] **1.5** Make explicit dynamic mode fail visibly when dynamic rendering is unsupported.
- [ ] **1.6** Make explicit legacy mode route all supported graphics targets through the retained render-pass/framebuffer path.
- [ ] **1.7** Add startup diagnostics reporting:
  - requested mode
  - resolved mode
  - dynamic-rendering feature support
  - whether the legacy path is active
  - fallback reason, if any
- [ ] **1.8** Update the current dynamic-rendering debug message so it describes the default dynamic path plus retained legacy toggle.
- [ ] **1.9** Add tests for explicit dynamic unsupported behavior and explicit legacy selection behavior where source-verifiable.

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

## Phase 4 - Dynamic Rendering For Simple FBOs

- [ ] **4.1** Add dynamic FBO planning behind the runtime path selection.
- [ ] **4.2** Support one color attachment with optional depth attachment.
- [ ] **4.3** Convert `VkFrameBuffer` attachment signatures into dynamic rendering attachment plans.
- [ ] **4.4** Preserve `VkFrameBuffer.WriteClearValues` behavior.
- [ ] **4.5** Preserve FBO extent resolution, including mip-level FBO dimensions.
- [ ] **4.6** Preserve current attachment identity recreation predicates.
- [ ] **4.7** Route FBO dynamic pipelines through `PipelineRenderingCreateInfo`.
- [ ] **4.8** Ensure simple dynamic FBO pipeline keys include color format, depth format, and sample count.
- [ ] **4.9** Keep legacy FBO render-pass creation and `CmdBeginRenderPass` behavior available through the runtime mode.
- [ ] **4.10** Add visible diagnostics for unsupported attachment layouts or target shapes.
- [ ] **4.11** Validate Unit Testing World with dynamic swapchain plus simple dynamic FBOs.
- [ ] **4.12** Validate `ForwardPassFBO` and deferred GBuffer writes survive compute/blit interruptions and render-scope re-entry.

## Phase 5 - Full FBO Coverage

- [ ] **5.1** Support multiple color attachments.
- [ ] **5.2** Support colorless depth-only targets.
- [ ] **5.3** Support depth/stencil targets.
- [ ] **5.4** Support stencil-only behavior where Vulkan and the engine target shape allow it.
- [ ] **5.5** Support mip-level targets.
- [ ] **5.6** Support texture array layer targets.
- [ ] **5.7** Support cubemap face targets.
- [ ] **5.8** Support resolve attachments with dynamic rendering resolve fields.
- [ ] **5.9** Support transient attachments.
- [ ] **5.10** Support read-only depth/stencil passes.
- [ ] **5.11** Support shadow map targets.
- [ ] **5.12** Support bloom/downsample/upsample targets.
- [ ] **5.13** Support cubemap and texture-array capture targets.
- [ ] **5.14** Support VR mirror targets that route through Vulkan FBOs.
- [ ] **5.15** Validate DefaultRenderPipeline in dynamic mode.
- [ ] **5.16** Validate DefaultRenderPipeline2 in dynamic mode when applicable.
- [ ] **5.17** Validate explicit legacy mode still renders the same FBO scenarios.

## Phase 6 - Synchronization And Layout Tracking

- [ ] **6.1** Move all dynamic-path implicit render-pass layout transitions into explicit barriers.
- [ ] **6.2** Reuse `VulkanBarrierPlanner` and `RenderGraphSynchronizationPlanner` where possible.
- [ ] **6.3** Ensure dynamic attachments enter:
  - `ColorAttachmentOptimal`
  - `DepthStencilAttachmentOptimal`
  - read-only depth/stencil layout when required
- [ ] **6.4** Ensure dynamic attachments leave rendering in the layout expected by the next resource usage.
- [ ] **6.5** Make the planner aware of swapchain target resources.
- [ ] **6.6** Make the planner aware of FBO attachments using stable semantic resource identities.
- [ ] **6.7** Replace dynamic-path dependence on `VkFrameBuffer.GetFinalLayouts()` with final states from the dynamic scope plan.
- [ ] **6.8** Preserve `QueryCurrentAttachmentLayouts` behavior for shared physical image groups.
- [ ] **6.9** Preserve `fboLayoutTracking` behavior or replace it with equivalent per-subresource tracking.
- [ ] **6.10** Ensure compute/blit interruptions end active dynamic rendering, transition resources, and resume with preserved contents when required.
- [ ] **6.11** Add diagnostics for old/render/final layout decisions.
- [ ] **6.12** Validate no layout errors under Vulkan validation layers.

## Phase 7 - Pipeline Signature And Prewarm Cleanup

- [ ] **7.1** Make dynamic-path graphics pipelines always use `PipelineRenderingCreateInfo`.
- [ ] **7.2** Keep `GraphicsPipelineCreateInfo.RenderPass = default` for dynamic-path graphics pipelines.
- [ ] **7.3** Keep legacy-path pipelines compatible with real `VkRenderPass` handles.
- [ ] **7.4** Split dynamic-path and legacy-path pipeline keys cleanly.
- [ ] **7.5** Remove `VkRenderPass` handles from dynamic-path pipeline keys.
- [ ] **7.6** Add ordered color attachment formats to dynamic-path keys.
- [ ] **7.7** Add color attachment count to dynamic-path keys.
- [ ] **7.8** Add depth format to dynamic-path keys.
- [ ] **7.9** Add stencil format to dynamic-path keys.
- [ ] **7.10** Add sample count to dynamic-path keys.
- [ ] **7.11** Add view mask to dynamic-path keys.
- [ ] **7.12** Include depth/stencil read-only state if it changes pipeline depth/stencil writes.
- [ ] **7.13** Convert dynamic-path prewarm signatures to semantic attachment signatures.
- [ ] **7.14** Ensure prewarm manifests do not include transient `VkRenderPass` or `VkFramebuffer` handles for dynamic entries.
- [ ] **7.15** Update pipeline diagnostics to report attachment signatures instead of render-pass handles in dynamic mode.
- [ ] **7.16** Verify pipeline cache miss summaries trend toward zero after warmup in dynamic mode.

## Phase 8 - Secondary Command Buffer Support

- [ ] **8.1** Audit all secondary command buffer recording paths.
- [ ] **8.2** Identify any path that still relies on render-pass inheritance.
- [ ] **8.3** Add `CommandBufferInheritanceRenderingInfo` for secondary graphics command buffers recorded inside dynamic rendering.
- [ ] **8.4** Ensure inherited formats match active color/depth/stencil formats.
- [ ] **8.5** Ensure inherited sample count matches active rendering scope.
- [ ] **8.6** Ensure inherited view mask matches active rendering scope.
- [ ] **8.7** Disable or keep legacy-only any secondary graphics recording path that cannot be made dynamic-safe yet.
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

## Phase 10 - Make Dynamic Rendering The Default

- [ ] **10.1** Flip `Auto` mode to dynamic rendering for all supported Vulkan graphics targets.
- [ ] **10.2** Keep explicit legacy mode functional and documented.
- [ ] **10.3** Ensure unsupported dynamic rendering in `Auto` mode logs a legacy fallback reason.
- [ ] **10.4** Ensure unsupported dynamic rendering in explicit dynamic mode fails visibly.
- [ ] **10.5** Update `docs/architecture/rendering/vulkan-renderer.md`.
- [ ] **10.6** Update `docs/work/todo/vulkan.md` manual validation guidance with the new mode/toggle.
- [ ] **10.7** Update diagnostics and profiler labels to distinguish:
  - engine render pass
  - dynamic rendering scope
  - legacy Vulkan `VkRenderPass`
- [ ] **10.8** Add source tests that dynamic path command recording uses `CmdBeginRendering`.
- [ ] **10.9** Add source tests that legacy `CmdBeginRenderPass` calls remain isolated behind the runtime mode.
- [ ] **10.10** Add source tests that dynamic-path pipeline keys do not include render-pass handles.

## Phase 11 - Optional Vulkan 1.4 Local Read

- [ ] **11.1** Inventory passes that would benefit from framebuffer-local dependencies.
- [ ] **11.2** Query and expose `VK_KHR_dynamic_rendering_local_read` / Vulkan 1.4 support.
- [ ] **11.3** Prototype local-read barriers only for a pass with a real need.
- [ ] **11.4** Validate tiled deferred or VR use cases before broad adoption.
- [ ] **11.5** Keep local read optional until the engine has a required Vulkan 1.4 tier.

## Phase 12 - Modern Follow-On Backlog

These are not required to flip the dynamic rendering default, but should stay visible because they share capability plumbing with this migration and the later shader-object work.

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

## Validation Checklist

### Static Validation

- [ ] Dynamic-path code has no unintended `CreateRenderPass` calls.
- [ ] Dynamic-path code has no unintended `CreateFramebuffer` calls.
- [ ] Dynamic-path command recording has no unintended `CmdBeginRenderPass` calls.
- [ ] Dynamic-path graphics pipelines always receive `PipelineRenderingCreateInfo`.
- [ ] Dynamic-path pipeline keys include attachment formats and sample count.
- [ ] Dynamic-path prewarm signatures do not include transient handles.
- [ ] Legacy render-pass/framebuffer usage is isolated behind the runtime mode.

### Build And Unit Tests

- [ ] Run the targeted Vulkan build:

  ```powershell
  dotnet build .\XREngine.Editor\XREngine.Editor.csproj
  ```

- [ ] Run targeted Vulkan tests:

  ```powershell
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter Vulkan
  ```

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

## Files Expected To Change

- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/LogicalDevice.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkFrameBuffer.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Pipeline.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Drawing.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanBarrierPlanner.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderGraphCompiler.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanPipelinePrewarmDatabase.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderer.ImGui.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Extensions.cs`
- [ ] `XREngine.UnitTests/Rendering/*Vulkan*`
- [ ] `docs/architecture/rendering/vulkan-renderer.md`
- [ ] `docs/work/todo/vulkan.md`

Legacy files expected to remain, but become runtime-mode-isolated:

- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/RenderPasses.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/FrameBuffers.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/FrameBufferRenderPasses.cs`

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

## Final Promotion

- [ ] **F.1** Confirm every acceptance criterion below is satisfied.
- [ ] **F.2** Update PR notes with what changed, why, validation performed, risks, and follow-ups.
- [ ] **F.3** Merge the dedicated branch back into `main` after implementation and validation.

Acceptance criteria:

- [ ] Dynamic rendering is the default Vulkan graphics target path in `Auto` mode when supported.
- [ ] Explicit legacy mode still renders through the retained `VkRenderPass` / `VkFramebuffer` path.
- [ ] Explicit dynamic mode fails visibly when dynamic rendering is unsupported.
- [ ] All dynamic-path graphics command recording uses `CmdBeginRendering` / `CmdEndRendering`.
- [ ] All dynamic-path graphics pipelines are created with `PipelineRenderingCreateInfo`.
- [ ] Dynamic-path pipeline and prewarm keys do not include `VkRenderPass` or `VkFramebuffer` handles.
- [ ] Legacy-path pipeline keys still retain all compatibility data needed by real render-pass objects.
- [ ] Swapchain present layout transition still occurs exactly once per submitted dynamic-rendering frame.
- [ ] Unit Testing World and default editor startup render correctly in dynamic mode under Vulkan validation.
- [ ] Unit Testing World and default editor startup render correctly in explicit legacy mode under Vulkan validation.
- [ ] Relevant Vulkan architecture docs and manual validation docs are updated.
