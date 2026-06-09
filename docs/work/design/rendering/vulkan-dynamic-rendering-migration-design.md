# Vulkan Dynamic Rendering Migration

Status: reference design

Primary goal: make explicit dynamic rendering the default Vulkan graphics target path for every target, while retaining the legacy `VkRenderPass` / `VkFramebuffer` path behind a runtime toggle as a fallback and regression-bisection tool.

Implementation todo: [Vulkan Dynamic Rendering Migration Todo](../../todo/rendering/vulkan-dynamic-rendering-migration-todo.md).

Follow-up design: [Vulkan Shader Object Pipeline Replacement](vulkan-shader-object-pipeline-replacement-design.md).

## Summary

Vulkan dynamic rendering lets command buffers begin graphics rendering directly against image views by calling `vkCmdBeginRendering` / `vkCmdEndRendering`. This replaces the older model where the application creates a `VkRenderPass`, creates a compatible `VkFramebuffer`, and begins rendering with `vkCmdBeginRenderPass`.

XRENGINE already uses dynamic rendering for the main swapchain path when the device supports it. The remaining deprecated Vulkan-object usage is concentrated in swapchain fallback objects, offscreen `XRFrameBuffer` targets, the FBO-specific render pass cache, and graphics pipeline compatibility for FBO render pass handles.

The migration should make dynamic rendering the default Vulkan graphics target path while keeping the legacy render-pass/framebuffer path available behind a runtime toggle. This is intentionally different from deleting the legacy code outright: the retained path gives us a field fallback, a regression-bisection tool, and a route for devices that do not expose dynamic rendering.

High-level engine names such as `RenderPassMetadata`, `EDefaultRenderPass`, and "render pipeline pass" should remain. Those terms describe engine scheduling and resource usage, not Vulkan `VkRenderPass` objects.

## External Context

Current Vulkan documentation says render pass objects and related commands are deprecated by dynamic rendering in Vulkan 1.4. Vulkan 1.3 promoted `VK_KHR_dynamic_rendering` to core, and Vulkan 1.4 promoted `VK_KHR_dynamic_rendering_local_read`, closing most of the historical subpass gap.

References:

- Vulkan deprecation appendix: https://vulkan.lunarg.com/doc/view/1.4.328.1/linux/antora/spec/latest/appendices/deprecation.html
- Vulkan Guide deprecated functionality: https://docs.vulkan.org/guide/latest/deprecated.html
- Dynamic rendering sample: https://docs.vulkan.org/samples/latest/samples/extensions/dynamic_rendering/README.html
- Dynamic rendering local read proposal: https://docs.vulkan.org/features/latest/features/proposals/VK_KHR_dynamic_rendering_local_read.html

## Goals

- Make dynamic rendering the default Vulkan graphics target path for swapchain, offscreen FBO, post-process, shadow, capture, VR mirror, debug, and ImGui targets.
- Retain a fully working legacy `VkRenderPass` / `VkFramebuffer` path selectable through a single runtime toggle.
- Preserve existing `XRFrameBuffer` and render-pipeline authoring semantics where possible.
- Preserve load/store/clear behavior currently encoded by `AttachmentDescription`, `RenderPassBeginInfo`, and `ClearValue` in both paths.
- Move implicit render-pass layout transitions into explicit barrier planning and render-target scope tracking for the dynamic path.
- Keep failures visible. Do not silently choose a weaker path for incorrect reasons; the only sanctioned automatic fallback is selecting the legacy path when dynamic rendering is unsupported, and that selection must be logged.
- Keep graphics pipeline creation valid by using `VkPipelineRenderingCreateInfo` for all non-compute graphics pipelines on the dynamic path.
- Keep transient Vulkan handles out of dynamic-path graphics pipeline keys and prewarm manifests.
- Keep the OpenGL backend unaffected.

## Non-Goals

- Do not delete the legacy `VkRenderPass` / `VkFramebuffer` path during this migration.
- Do not rename engine-level render passes. `RenderPassMetadata`, `RenderPassBuilder`, `EDefaultRenderPass`, and pass indices remain useful engine concepts.
- Do not replace `VkPipeline`; shader-object replacement is covered by the separate shader-object design.
- Do not rewrite `DefaultRenderPipeline` or `DefaultRenderPipeline2` as a new frame graph.
- Do not add CPU fallback paths for render targets.
- Do not require Vulkan 1.4 immediately. The first production target is Vulkan 1.3 plus `VK_KHR_dynamic_rendering`; Vulkan 1.4/local-read features can be added when needed.

## Current State

`Objects/LogicalDevice.cs` already queries and enables `PhysicalDeviceDynamicRenderingFeatures` when Vulkan 1.3 core support or `VK_KHR_dynamic_rendering` is available. `SupportsDynamicRendering` is surfaced through `Extensions.cs`.

`Objects/CommandBuffers.cs` contains `BeginRenderPassForTarget`. When `target is null`, it uses dynamic rendering if supported: it transitions the swapchain image to `ColorAttachmentOptimal`, transitions the swapchain depth image to `DepthStencilAttachmentOptimal`, creates `RenderingAttachmentInfo` for color and depth, begins with `CmdBeginRendering`, ends with `CmdEndRendering`, and transitions the swapchain image to `PresentSrcKhr` on the final close.

The same method still contains a traditional swapchain fallback using `RenderPassBeginInfo`, `_renderPass`, `_renderPassLoad`, `swapChainFramebuffers`, and `CmdBeginRenderPass`.

Offscreen FBO rendering still uses the old Vulkan object model. When `BeginRenderPassForTarget` receives an `XRFrameBuffer`, it resolves a `VkFrameBuffer`, asks it for a `RenderPass` via `ResolveRenderPassForPass`, begins `CmdBeginRenderPass`, stores the active `RenderPass` and `Framebuffer`, and uses `VkFrameBuffer.GetFinalLayouts()` after the pass to update layout tracking.

`Objects/Types/VkFrameBuffer.cs` currently owns the live `Framebuffer`, live `RenderPass`, attachment signatures, attachment views, dimensions, clear value writing, and final-layout helpers. `FrameBufferRenderPasses.cs` owns the FBO render-pass cache and builds `AttachmentDescription`, `AttachmentReference`, `SubpassDescription`, and `RenderPassCreateInfo`.

`Objects/RenderPasses.cs` creates `_renderPass` and `_renderPassLoad`, and `Objects/FrameBuffers.cs` creates one `VkFramebuffer` per swapchain image. These remain necessary for the legacy path, but they should stop being part of the default path once dynamic rendering is validated for every target.

`Objects/Types/VkMeshRenderer.Pipeline.cs` already supports dynamic rendering pipelines through `PipelineRenderingCreateInfo`. ImGui also creates a dynamic-rendering-compatible pipeline when `SupportsDynamicRendering` is true. The missing piece is making all FBO targets and target signatures flow through the same dynamic pipeline path.

## Runtime Path Selection

The renderer should expose one runtime target mode:

```csharp
internal enum VulkanRenderTargetMode
{
    Auto,
    DynamicRendering,
    LegacyRenderPass,
}
```

`Auto` should select dynamic rendering when `SupportsDynamicRendering` is true, and select the legacy path only when dynamic rendering is unsupported. Explicit `DynamicRendering` mode should fail visibly when the feature is unavailable. Explicit `LegacyRenderPass` mode should route all supported graphics targets through the retained render-pass/framebuffer path.

Startup diagnostics should report the requested mode, resolved mode, dynamic-rendering feature support, whether the legacy path is active, and any fallback reason.

## Target Architecture

After migration, `VulkanRenderer` owns dynamic rendering scopes and explicit layout transitions. `VkFrameBuffer` owns Vulkan image views and target metadata, not the default rendering path's active `VkFramebuffer` / `VkRenderPass` handles. `VulkanBarrierPlanner` owns pass-to-pass resource transitions. `VulkanStateTracker` owns viewport, scissor, and clear state. `VkMeshRenderer` owns graphics pipeline selection using dynamic-rendering attachment signatures.

The legacy object path remains, but it should be isolated behind runtime target-mode checks. No default dynamic-path code should need a live `VkRenderPass` or `VkFramebuffer` handle.

## Dynamic Rendering Scope Plan

Add a small internal target-planning layer that can describe either the swapchain or an `XRFrameBuffer` as dynamic rendering attachments.

The rendering scope plan should include:

- render area
- layer count
- view mask
- color attachment plans
- depth attachment plan
- stencil attachment plan
- read-only depth/stencil state
- color, depth, and stencil formats
- sample count
- semantic signature

Each attachment plan should include:

- image and image view
- format and aspect mask
- initial, rendering, and final layouts
- load and store ops
- clear value
- resolve view and resolve mode when applicable

The plan must be cheap to produce and should use stack-backed spans or other allocation-free shapes inside command recording. Avoid LINQ, captured closures, and heap allocations in per-frame recording.

## Swapchain Planning

Swapchain dynamic rendering should be moved from a hand-coded branch into the shared target planner.

The swapchain plan must preserve:

- active swapchain image and image view
- swapchain depth image and view
- `swapChainImageFormat`
- `_swapchainDepthFormat`
- `SampleCountFlags.Count1Bit`
- `swapChainExtent`
- first-use/re-entry layout rules
- clear on first entry and load on re-entry
- final transition to `PresentSrcKhr`

The existing invariant remains load-bearing: a dynamic-rendering swapchain frame transitions the image to `PresentSrcKhr` exactly once before submit/present.

## XRFrameBuffer Planning

`VkFrameBuffer` should be split conceptually into attachment inventory, image view cache, clear value source, target extent resolver, and render target signature builder.

It should continue to support:

- mip-level FBO extents
- array and cubemap layer views
- attachment identity change detection
- color/depth/stencil role resolution
- clear values from the owning `XRFrameBuffer`
- read-only depth/stencil passes
- final layout tracking per physical image/subresource group

For the dynamic path, `ResolveRenderPassForPass` should be replaced by a method that builds a dynamic rendering scope plan from the current target, pass metadata, and tracked initial layouts. The current attachment-signature logic is valuable and should be retained, but it should produce dynamic rendering attachment plans instead of `AttachmentDescription` arrays.

## Begin And End Semantics

`BeginRenderPassForTarget` should become, or delegate to, `BeginRenderingForTarget`.

The dynamic begin path should:

1. Resolve the target plan.
2. Emit pre-rendering image barriers for attachments not already in the requested layout.
3. Build `RenderingAttachmentInfo` values.
4. Build `RenderingInfo`.
5. Call `CmdBeginRendering`.
6. Store active target identity, active pass index, render area, formats, samples, and read-only depth/stencil state.

The dynamic end path should:

1. Call `CmdEndRendering`.
2. Emit post-rendering layout transitions required by the target plan and planner.
3. Update physical image layout tracking for touched attachments.
4. For swapchain final close, transition exactly once to `PresentSrcKhr`.
5. Clear active rendering state.

The legacy begin/end path should remain selectable and should continue using `CmdBeginRenderPass` / `CmdEndRenderPass`.

## Pipeline Signature Rules

Dynamic graphics pipelines should always use `PipelineRenderingCreateInfo` and keep `GraphicsPipelineCreateInfo.RenderPass = default`. Legacy graphics pipelines still need real render-pass compatibility.

Dynamic-path pipeline keys must include:

- color attachment count
- ordered color attachment formats
- depth format
- stencil format
- sample count
- view mask
- depth/stencil read-only state if it changes writes
- color write mask and blend state

Dynamic-path keys and prewarm manifests must not include transient `VkRenderPass` or `VkFramebuffer` handles. Legacy-path keys may continue to include the render-pass compatibility data needed by the old API path.

## Load, Store, Clear, And Layout Mapping

Traditional render passes encoded load/store/layout behavior in `AttachmentDescription`, `AttachmentReference`, and `RenderPassBeginInfo`. Dynamic rendering pushes this into `RenderingAttachmentInfo` plus explicit barriers.

Mapping:

| Old location | Dynamic rendering location |
|--------------|----------------------------|
| `AttachmentDescription.LoadOp` | `RenderingAttachmentInfo.LoadOp` |
| `AttachmentDescription.StoreOp` | `RenderingAttachmentInfo.StoreOp` |
| `AttachmentDescription.InitialLayout` | pre-rendering barrier old layout |
| `AttachmentDescription.FinalLayout` | post-rendering barrier new layout |
| `AttachmentReference.Layout` | `RenderingAttachmentInfo.ImageLayout` |
| `RenderPassBeginInfo.RenderArea` | `RenderingInfo.RenderArea` |
| `RenderPassBeginInfo.ClearValueCount` | per-attachment `ClearValue` |
| `SubpassDescription.ColorAttachmentCount` | `RenderingInfo.ColorAttachmentCount` |
| subpass depth/stencil attachment | `RenderingInfo.PDepthAttachment` / `PStencilAttachment` |

`Clear` is valid only when the pass intends to clear. `Load` requires preserved content and a valid old layout. `DontCare` is allowed only when later passes do not observe prior contents. Re-entry after compute or blit must use `Load` for preserved color contents.

`Store` is required when later passes sample, blit, present, or otherwise read the attachment. `DontCare` is acceptable for transient depth or temporary color only when no later read exists.

## Synchronization And Layout Tracking

The most important technical risk is losing implicit render-pass transitions. Dynamic rendering does not replace them. The renderer must make all transitions explicit.

Existing pieces to reuse:

- `VulkanBarrierPlanner`
- `RenderGraphSynchronizationPlanner`
- `RenderPassResourceUsage`
- `QueryCurrentAttachmentLayouts`
- `fboLayoutTracking`
- physical group layout tracking helpers
- swapchain present transition diagnostics

Attachments used by dynamic rendering must enter `ColorAttachmentOptimal`, `DepthStencilAttachmentOptimal`, or a read-only depth/stencil layout before `CmdBeginRendering`. They must leave rendering in the layout expected by the next usage. The planner should know about swapchain and FBO target resources using stable semantic resource identities.

The command recorder should stop relying on `GetFinalLayouts()` for dynamic-path state updates and instead update final states from the dynamic scope plan.

## Secondary Command Buffers

If secondary command buffers execute inside active dynamic rendering, their begin info must use dynamic rendering inheritance rather than render-pass inheritance.

Requirements:

- `CommandBufferInheritanceInfo.RenderPass` is legacy-path only.
- Dynamic secondary buffers use `CommandBufferInheritanceRenderingInfo`.
- Inherited formats, depth/stencil formats, samples, and view mask must match the active rendering scope.
- Any secondary graphics path that cannot be made dynamic-safe should remain disabled or legacy-only until validated.

## Resolve, Multiview, And Stereo

Dynamic rendering supports resolves through `RenderingAttachmentInfo.ResolveMode` and `ResolveImageView`. Resolve attachments must have explicit layout transitions, valid format/sample compatibility, and store ops based on later usage.

Dynamic rendering uses `RenderingInfo.ViewMask` for multiview rendering. Graphics pipelines must use the same view mask through `PipelineRenderingCreateInfo`. View mask is part of graphics pipeline compatibility. Stereo planning must be based on active view-set semantics, not inferred from texture array length alone.

## Diagnostics

Add or update diagnostics for:

- dynamic rendering scope begin/end
- attachment plan decisions
- layout mismatches
- unsupported attachments
- present transitions
- runtime target mode
- target-mode fallback reason
- dynamic pipeline attachment signatures

Diagnostics should include pass index/name, target name, runtime mode, attachment role/index, image/view handles, mip/layer, old/render/final layouts, load/store ops, color/depth/stencil formats, sample count, view mask, and fallback reason. High-frequency logs must be throttled or behind explicit trace flags.

## Validation Strategy

Validation must cover source shape, builds, unit tests, runtime behavior, visual output, and performance.

Static checks should prove that dynamic-path code does not accidentally depend on `CreateRenderPass`, `CreateFramebuffer`, or `CmdBeginRenderPass`, while legacy usage remains isolated behind runtime mode checks.

Runtime validation should cover default editor startup, Unit Testing World, explicit dynamic mode, explicit legacy mode, resize/recreate, deferred GBuffer writes, forward depth reuse, transparency/OIT, bloom/downsample, shadow maps, cubemap and texture-array capture, ImGui, compute/blit interruptions, forced magenta diagnostics, shader invalidation, and VR mirror targets where Vulkan is active.

Performance validation should ensure no new per-draw allocations, no LINQ or closure allocation in command recording and target planning hot paths, no pipeline explosion from missing attachment-key fields, no redundant barrier spam, and no unexplained frame pacing regression versus the legacy path.

## Acceptance Criteria

The migration is complete when:

- Dynamic rendering is the default Vulkan graphics target path in `Auto` mode when supported.
- Explicit legacy mode still renders through the retained `VkRenderPass` / `VkFramebuffer` path.
- Explicit dynamic mode fails visibly when dynamic rendering is unsupported.
- All dynamic-path graphics command recording uses `CmdBeginRendering` / `CmdEndRendering`.
- All dynamic-path graphics pipelines are created with `PipelineRenderingCreateInfo`.
- Dynamic-path pipeline and prewarm keys do not include `VkRenderPass` or `VkFramebuffer` handles.
- Legacy-path pipeline keys still retain all compatibility data needed by real render-pass objects.
- Swapchain present layout transition occurs exactly once per submitted dynamic-rendering frame.
- Unit Testing World and default editor startup render correctly in dynamic mode under Vulkan validation.
- Unit Testing World and default editor startup render correctly in explicit legacy mode under Vulkan validation.
- Vulkan architecture docs and manual validation docs are updated.

## Open Questions

- Should explicit dynamic mode fail at initialization or only when the first graphics target is used?
- Should dynamic rendering local read become part of a future required Vulkan 1.4 tier?
- How much of `VkFrameBuffer` should survive as a Vulkan-specific attachment-view cache versus moving into a renderer-level target cache?
- Should render graph metadata become the only source of load/store behavior, or should `XRFrameBuffer` keep explicit overrides for hand-authored target flows?
- Should source files be renamed away from "RenderPass" after dynamic mode is default, even though the legacy path is retained?

