# Vulkan Dynamic Rendering Migration Todo (Remaining Work)

Last Updated: 2026-06-18
Owner: Rendering
Status: Not fully implemented. Core migration is implemented and the audited completed items were verified against source on 2026-06-09 and 2026-06-18. `Auto` resolves to dynamic rendering when supported, explicit legacy remains selectable, and explicit dynamic fails visibly when unsupported. The source-verifiable migration test suite has grown beyond the original 3 checks, and stale source/assertion checks were refreshed on 2026-06-18, but rebuilt test execution is still blocked by a live editor DLL lock. This document tracks the remaining architecture, runtime-validation, and modern-extension work.
Target Branch: intentionally skipped; user requested not to branch.

Design sources:

- [Vulkan Dynamic Rendering Migration Design](../../design/rendering/vulkan-dynamic-rendering-migration-design.md)
- [Vulkan Shader Object Pipeline Replacement](../../design/rendering/vulkan-shader-object-pipeline-replacement-design.md)
- Vulkan deprecation appendix: https://vulkan.lunarg.com/doc/view/1.4.328.1/linux/antora/spec/latest/appendices/deprecation.html
- Vulkan Guide deprecated functionality: https://docs.vulkan.org/guide/latest/deprecated.html
- Dynamic rendering sample: https://docs.vulkan.org/samples/latest/samples/extensions/dynamic_rendering/README.html
- Dynamic rendering local read proposal: https://docs.vulkan.org/features/latest/features/proposals/VK_KHR_dynamic_rendering_local_read.html
- `VK_EXT_descriptor_heap`: https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_descriptor_heap.html
- `VK_EXT_descriptor_buffer`: https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_descriptor_buffer.html
- `VK_EXT_shader_object`: https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_shader_object.html
- `VK_KHR_fragment_shading_rate`: https://docs.vulkan.org/refpages/latest/refpages/source/VK_KHR_fragment_shading_rate.html
- `VK_EXT_fragment_density_map`: https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_fragment_density_map.html

## Goal

Finish hardening explicit dynamic rendering across every Vulkan graphics target (swapchain, offscreen FBO, post-process, shadow, capture, VR mirror, debug, ImGui) and validate it under the Vulkan validation layers. The legacy render-pass/framebuffer path stays selectable through the runtime toggle until validation says it can be retired or isolated as a diagnostics-only backend.

High-level engine render-pass concepts remain unchanged. `RenderPassMetadata`, `RenderPassBuilder`, `EDefaultRenderPass`, and pass indices describe engine graph scheduling and resource usage, not Vulkan `VkRenderPass` objects.

This todo also tracks the adjacent modern Vulkan extension plan because dynamic rendering is the compatibility pivot for several follow-on migrations:

- `VK_KHR_dynamic_rendering_local_read` / Vulkan 1.4 local read.
- `VK_EXT_shader_object` pipeline replacement.
- `VK_EXT_descriptor_heap` descriptor binding replacement.
- `VK_KHR_fragment_shading_rate` and `VK_EXT_fragment_density_map` for XR foveation.
- Sync2, timeline semaphore, memory-budget, ray-tracing, and device-generated/indirect GPU-driven plumbing.

The intent is not to enable every modern extension by default. The intent is to make every capability explicit: queried, reported, gated by profile/runtime policy, validated where used, and never hidden behind silent CPU or legacy fallbacks when a modern path was explicitly requested.

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

## Source Audit Update (2026-06-18)

Additional items verified directly against source:

- Swapchain dynamic rendering preserves first-use/re-entry layouts, first-entry clear/re-entry load behavior, depth clear behavior, and present-transition diagnostics.
- FBO dynamic rendering now flows through `VulkanBarrierPlanner` / `RenderGraphSynchronizationPlanner` planning where available and resolves FBO attachment resource names through stable `fbo::<name>::<slot>` semantic bindings.
- A dynamic UI text secondary command-buffer path now uses `CommandBufferInheritanceRenderingInfo` with inherited color/depth/stencil formats, sample count, and view mask, then executes inside a dynamic rendering scope.
- Resolve attachment metadata still exists in the render-graph model, but dynamic rendering does not yet map resolve attachments to `RenderingAttachmentInfo.ResolveMode` / `ResolveImageView`.
- `DynamicRenderingFormatSignature` still allocates arrays (`ToArray()` / `new Format[...]`) while constructing dynamic-path signatures, so allocation hardening remains open.

## Modern Vulkan Capability Matrix (2026-06-18)

Status meanings:

- **Active**: queried/enabled and used on a production or normal runtime path.
- **Plumbed**: queried/enabled or partially loaded, but not broadly exercised.
- **Designed**: design docs or todos exist, but runtime capability plumbing is missing.
- **Absent**: no source-level runtime plumbing found in the Vulkan renderer.

| Area | Extension / API | Current status | Required follow-up |
| --- | --- | --- | --- |
| Dynamic rendering | `VK_KHR_dynamic_rendering` / Vulkan 1.3 | **Active**. `Auto` resolves to dynamic when supported; legacy render-pass mode remains selectable. | Finish shared scope planner, runtime validation, resolve attachments, multiview view masks, and promotion criteria. |
| Render-pass local reads | `VK_KHR_dynamic_rendering_local_read` / Vulkan 1.4 | **Absent**. | Query feature/properties, expose diagnostics, and prototype only for passes that previously needed framebuffer-local dependencies. |
| Synchronization | `VK_KHR_synchronization2` / Vulkan 1.3 | **Plumbed**. Device feature is queried; `QueueSubmit2` and `CmdPipelineBarrier2` backend exists behind `EVulkanSynchronizationBackend.Sync2`. | Make Sync2 the default once validation confirms no regressions; eliminate broad legacy stage/access masks from hot paths. |
| Frame pacing | Timeline semaphores / Vulkan 1.2 | **Active**. Renderer uses graphics/present/transfer timeline semaphores and treats them as required. | Audit frame pacing and async transfer/compute overlap. |
| Descriptor indexing | `VK_EXT_descriptor_indexing` / Vulkan 1.2 | **Active**. Used for bindless material texture table and update-after-bind policy. | Keep as the Vulkan 1.2/1.3 fallback binding backend after descriptor heap arrives. |
| Descriptor heap | `VK_EXT_descriptor_heap` | **Absent**. Khronos marks it ratified and positions it as the descriptor model that replaces descriptor sets/pools/layouts with one resource heap and one sampler heap. | Add capability query, heap allocator, descriptor write/copy path, push-data mapping, shader mapping policy, secondary inheritance, diagnostics, and a profile-gated backend. Prefer this over starting new `VK_EXT_descriptor_buffer` work. |
| Descriptor buffer | `VK_EXT_descriptor_buffer` | **Absent**. Khronos marks it deprecated by `VK_EXT_descriptor_heap`. | Do not build a new long-term backend on this extension. Consider only as a compatibility research path if descriptor heap availability is too narrow. |
| Buffer device address | `VK_KHR_buffer_device_address` / Vulkan 1.2 | **Plumbed**. Required by NV copy/decompression and scene-database address work; `VkDataBuffer` can expose device addresses. | Wire production draw/compute consumers so scene DB buffers can avoid descriptor rebinding where appropriate. |
| Shader objects | `VK_EXT_shader_object` | **Designed**. Detailed design exists; no runtime Vulkan shader-object backend found. | Add capability query and explicit program-binding backend toggle. Use dynamic rendering and dynamic state contracts; decide how it composes with descriptor heap. |
| Graphics pipeline library | `VK_KHR_pipeline_library` + `VK_EXT_graphics_pipeline_library` | **Active/Plumbed**. Dependency is enabled with GPL; dynamic rendering keys preserve format identity. | Validate cache miss behavior after warmup and keep GPL as the pipeline-object fallback while shader objects mature. |
| Fragment shading rate | `VK_KHR_fragment_shading_rate` | **Absent**. Khronos exposes pipeline/primitive/attachment shading-rate paths and dynamic-rendering attachment structs. | Add capability query, render-target planner support for shading-rate images, material/per-view policy, and XR visual validation. |
| Fragment density map | `VK_EXT_fragment_density_map` | **Absent**. Relevant to lens/peripheral foveation and dynamic rendering through `VkRenderingFragmentDensityMapAttachmentInfoEXT`. | Evaluate against OpenXR/OpenVR foveation needs; add only if it beats or complements fragment shading rate on target devices. |
| Multiview/stereo | `VK_KHR_multiview` / Vulkan 1.1 | **Plumbed**. Feature is queried and engine state is set. | Add dynamic rendering `ViewMask` target planning, pipeline keying, and OpenVR/OpenXR validation. |
| Mesh/task shaders | `VK_EXT_mesh_shader` | **Plumbed**. EXT mesh shader handle and indirect-count dispatch path exist. | Keep gated on EXT feature bits; validate production meshlet dispatch under dynamic rendering and descriptor backend changes. |
| Draw indirect count | `VK_KHR_draw_indirect_count` / Vulkan 1.2 | **Active/Plumbed**. Used for multi-draw indirect-count paths with fallback diagnostics. | Ensure fallback is visible when GPU-driven rendering explicitly requires indirect count. |
| External memory/semaphore | `VK_KHR_external_memory`, `VK_KHR_external_semaphore`, Win32 variants | **Active/Plumbed**. Used by Windows sidecar/upscale/interop paths. | Keep Windows-first diagnostics and validate against OpenVR/OBS/upscaler handoff flows. |
| Memory budget/residency | `VK_EXT_memory_budget`, `VK_EXT_memory_priority` | **Designed/Absent** in this dynamic-rendering scope. | Add to memory allocator diagnostics and residency policy; connect with transient/lazily allocated attachment policy. |
| Transient/lazy attachments | lazily allocated memory where supported | **Absent** for dynamic rendering targets. | Add attachment policy for depth and temporary color targets; never silently degrade explicitly requested GPU memory policies. |
| Ray tracing | `VK_KHR_acceleration_structure`, `VK_KHR_ray_tracing_pipeline`, `VK_KHR_ray_query`, `VK_KHR_deferred_host_operations` | **Designed/Probe-only** in renderer context; ReSTIR GI todo owns most work. | Keep capability reporting shared; do not mix ray-tracing enablement into this migration except for descriptor heap/resource binding compatibility. |
| Device generated commands | `VK_EXT_device_generated_commands` | **Designed/Absent**. Mentioned in GPU roadmap, not runtime-plumbed here. | Defer until descriptor heap/shader object/resource-table architecture is stable. |
| Maintenance / flags | `VK_KHR_maintenance4` active; `VK_KHR_maintenance5` / `VK_KHR_extended_flags` not found | **Partial**. Maintenance4 is queried/enabled. Descriptor heap depends on extended flags or maintenance5 plus buffer device address, or Vulkan 1.4. | Add maintenance5/extended-flags query as part of descriptor heap bring-up. |
| Depth clip / viewport-layer | `VK_EXT_depth_clip_control`, `VK_EXT_shader_viewport_index_layer` | **Plumbed**. Used for clip-space/layered shadow planning. | Keep diagnostics in startup capability snapshot; validate with layered shadow and cubemap/cascade paths. |
| Index type uint8 | `VK_EXT_index_type_uint8` / `VK_KHR_index_type_uint8` | **Plumbed**. Feature queried; byte-sized index buffers gated. | Add targeted test coverage if byte-indexed runtime meshes are in production content. |
| Transform feedback | `VK_EXT_transform_feedback` | **Plumbed**. Vulkan transform-feedback object path uses it when enabled. | Treat as legacy/parity support, not a modernization target. Ensure it does not block shader-object adoption. |
| NVIDIA data movement | `VK_NV_memory_decompression`, `VK_NV_copy_memory_indirect` | **Plumbed**. Optional accelerated copy/decompression helpers exist. | Keep NVIDIA-only paths explicit and diagnostic; no CPU fallback when an accelerated path is explicitly required. |

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
- [x] **3.3** Preserve first-use/re-entry layout rules:
  - `PresentSrcKhr`
  - `ColorAttachmentOptimal`
  - `Undefined`
- [x] **3.4** Preserve first-entry clear and re-entry load behavior.
- [x] **3.5** Preserve depth clear behavior.
- [x] **3.6** Keep final swapchain transition diagnostics.
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

- [x] **6.2** Reuse `VulkanBarrierPlanner` and `RenderGraphSynchronizationPlanner` where possible.
- [ ] **6.5** Make the planner aware of swapchain target resources.
- [x] **6.6** Make the planner aware of FBO attachments using stable semantic resource identities.
- [ ] **6.12** Validate no layout errors under Vulkan validation layers.

## Phase 7 - Pipeline Signature And Prewarm Cleanup (Open Items)

Dynamic-path keys, format/sample-count fields, semantic prewarm signatures, and diagnostics are implemented. Remaining:

- [ ] **7.11** Add view mask to dynamic-path keys.
- [ ] **7.16** Verify pipeline cache miss summaries trend toward zero after warmup in dynamic mode.

## Phase 8 - Secondary Command Buffer Support (Open Items)

2026-06-18 audit note: the dynamic UI text secondary command-buffer path now records and executes a secondary graphics command buffer inside dynamic rendering. It uses `CommandBufferInheritanceRenderingInfo` with swapchain color/depth/stencil formats, `SampleCountFlags.Count1Bit`, and view mask `0`. General parallel/secondary validation remains open.

- [x] **8.3** Add `CommandBufferInheritanceRenderingInfo` for secondary graphics command buffers recorded inside dynamic rendering.
- [x] **8.4** Ensure inherited formats match active color/depth/stencil formats.
- [x] **8.5** Ensure inherited sample count matches active rendering scope.
- [x] **8.6** Ensure inherited view mask matches active rendering scope.
- [ ] **8.8** Validate parallel/secondary recording in dynamic mode if enabled.

## Phase 9 - Resolve, Multiview, And Stereo

- [x] **9.1** Preserve `ERenderPassResourceType.ResolveAttachment` metadata.
- [ ] **9.2** Map resolve attachments to `RenderingAttachmentInfo.ResolveMode`.
- [ ] **9.3** Map resolve attachments to `RenderingAttachmentInfo.ResolveImageView`.
- [ ] **9.4** Add explicit layout transitions for resolve targets.
- [ ] **9.5** Validate resolve source and target sample-count requirements.
- [ ] **9.6** Validate resolve formats match Vulkan requirements.
- [ ] **9.7** Add dynamic rendering `ViewMask` support for multiview.
- [ ] **9.8** Add view mask to pipeline compatibility and diagnostics.
- [ ] **9.9** Ensure stereo target planning does not infer view count from texture array length alone.
- [ ] **9.10** Validate stereo, OpenVR mirror, and OpenXR-related paths that use Vulkan targets.

## Phase 11 - Vulkan 1.4 Dynamic Rendering Local Read

- [ ] **11.1** Inventory passes that would benefit from framebuffer-local dependencies.
- [ ] **11.2** Query and expose `VK_KHR_dynamic_rendering_local_read` / Vulkan 1.4 support.
- [ ] **11.3** Query and report Vulkan 1.4 local-read properties:
  - storage-resource local read support
  - single-sampled color attachment local read support
  - depth/stencil local read support
  - multisampled attachment local read support
- [ ] **11.4** Add `VK_IMAGE_LAYOUT_RENDERING_LOCAL_READ_KHR` / core alias mapping to the render graph layout model.
- [ ] **11.5** Add `RenderingAttachmentLocationInfo` and `RenderingInputAttachmentIndexInfo` plumbing for dynamic rendering scopes and secondary inheritance.
- [ ] **11.6** Prototype local-read barriers only for a pass with a real need.
- [ ] **11.7** Validate tiled deferred or VR use cases before broad adoption.
- [ ] **11.8** Keep local read optional until the engine has a required Vulkan 1.4 tier.

## Phase 12 - Capability Tiers And Startup Reporting

These are not required to keep the dynamic rendering default, but they define how the Vulkan backend presents modern capability use.

- [ ] **12.1** Define a Vulkan 1.4 opt-in baseline tier.
- [ ] **12.2** Define a Vulkan 1.3 production baseline tier:
  - dynamic rendering
  - synchronization2 capable, even if the legacy backend remains selectable
  - timeline semaphores
  - descriptor indexing
  - buffer device address
  - draw indirect count
  - maintenance4
- [ ] **12.3** Define a Vulkan 1.4 experimental tier:
  - local read
  - maintenance5 / extended flags where needed by descriptor heap
  - descriptor heap capability reporting
  - shader-object capability reporting
- [ ] **12.4** Keep runtime selection explicit through settings/env vars:
  - render target mode
  - synchronization backend
  - descriptor backend
  - program binding backend
  - foveation/VRS backend
  - ray-tracing backend
- [ ] **12.5** Add startup reporting for every capability in the Modern Vulkan Capability Matrix.
- [ ] **12.6** Report each capability as: unavailable, available-disabled, enabled-unused, enabled-active, or explicitly-required-missing.
- [ ] **12.7** Include Vulkan API version, extension name, feature bit, relevant properties/limits, runtime mode, and fallback reason in capability diagnostics.
- [ ] **12.8** Add tests/source checks that every optional extension in `Extensions.cs` is represented in the startup capability snapshot.
- [ ] **12.9** Make explicitly requested modern backends fail visibly when unsupported; do not silently use legacy paths for required modern modes.

## Phase 13 - Descriptor Binding Modernization

The current production path is descriptor sets plus descriptor indexing. Keep that path as the fallback while adding a profile-gated modern backend. `VK_EXT_descriptor_buffer` is not the long-term target because Khronos marks it deprecated by `VK_EXT_descriptor_heap`.

- [ ] **13.1** Add a renderer-level descriptor backend enum:
  - `DescriptorSets`
  - `DescriptorIndexing`
  - `DescriptorHeap`
- [ ] **13.2** Add capability query and startup reporting for `VK_EXT_descriptor_heap`.
- [ ] **13.3** Add dependency checks for descriptor heap:
  - `VK_KHR_extended_flags` or `VK_KHR_maintenance5`
  - `VK_KHR_buffer_device_address` or Vulkan 1.2
  - Vulkan 1.4 shortcut when available
  - `SPV_EXT_descriptor_heap` shader capability/toolchain support
- [ ] **13.4** Add descriptor heap properties reporting:
  - resource heap size/alignment limits
  - sampler heap size/alignment limits
  - descriptor sizes per resource type
  - capture/replay support
- [ ] **13.5** Add one resource heap and one sampler heap allocator.
- [ ] **13.6** Allocate heap storage with `VK_BUFFER_USAGE_DESCRIPTOR_HEAP_BIT_EXT` and `VK_BUFFER_USAGE_SHADER_DEVICE_ADDRESS_BIT`.
- [ ] **13.7** Add descriptor write path using:
  - `vkWriteResourceDescriptorsEXT`
  - `vkWriteSamplerDescriptorsEXT`
  - direct mapped writes where host-visible
  - staged GPU copy where not host-visible
- [ ] **13.8** Bind heaps through `vkCmdBindResourceHeapEXT` and `vkCmdBindSamplerHeapEXT`.
- [ ] **13.9** Replace push-constant assumptions in heap-backed shaders with `vkCmdPushDataEXT` / `VkPushDataInfoEXT` where needed.
- [ ] **13.10** Add legacy shader mapping through `VkShaderDescriptorSetAndBindingMappingInfoEXT` so existing `set`/`binding` SPIR-V can run during migration.
- [ ] **13.11** Add secondary command-buffer inheritance support with `CommandBufferInheritanceDescriptorHeapInfoEXT`.
- [ ] **13.12** Add descriptor heap diagnostics:
  - heap capacity and residency
  - descriptor allocation high-water marks
  - per-frame descriptor write/copy counts
  - missing mapping failures
  - fallback reason
- [ ] **13.13** Validate material textures, storage images, SSBOs, UBOs, texel buffers, immutable samplers, and acceleration structures under descriptor heap.
- [ ] **13.14** Add a migration note that `VK_EXT_descriptor_buffer` should be evaluated only as a temporary compatibility/backend-comparison path, not the primary v1 architecture.

## Phase 14 - Shader Object Program Binding

Shader objects are a separate backend from descriptor heap, but the two should be designed together because descriptor heap removes pipeline layouts and shader objects remove monolithic pipeline objects.

- [ ] **14.1** Add runtime capability query for `VK_EXT_shader_object`.
- [ ] **14.2** Add a program binding backend enum:
  - `PipelineObjects`
  - `ShaderObjectsNative`
  - `ShaderObjectsLayer`
- [ ] **14.3** Gate `ShaderObjectsNative` on native driver support, not only the emulation layer.
- [ ] **14.4** Keep `ShaderObjectsLayer` as coverage/iteration only; do not count it as a shipping hitch/permutation win.
- [ ] **14.5** Add `VkShaderEXT` artifact cache keyed by shader source, entry point, specialization constants, stage, and required dynamic-state feature set.
- [ ] **14.6** Emit dynamic fixed-function state required by shader objects:
  - vertex input
  - primitive topology and restart
  - viewport/scissor count
  - rasterization state
  - depth/stencil state
  - color blend/write state
  - multisample state
  - fragment shading rate / density map state when active
- [ ] **14.7** Validate shader objects with dynamic rendering, graphics pipeline library fallback, descriptor indexing fallback, and descriptor heap.
- [ ] **14.8** Add hot-reload validation that shader-object rebuilds do not invalidate unrelated material/resource bindings.

## Phase 15 - XR Foveation And Variable Rate Shading

- [ ] **15.1** Add capability query/reporting for `VK_KHR_fragment_shading_rate`.
- [ ] **15.2** Query and report fragment shading rate properties and attachment texel-size limits.
- [ ] **15.3** Add render-target planner support for fragment shading rate attachments in dynamic rendering.
- [ ] **15.4** Add dynamic state / pipeline state wiring for pipeline, primitive, and attachment shading-rate modes.
- [ ] **15.5** Add OpenVR/OpenXR foveation policy hooks so VRS is driven by headset/runtime data where available.
- [ ] **15.6** Add capability query/reporting for `VK_EXT_fragment_density_map`.
- [ ] **15.7** Add dynamic-rendering fragment density map attachment support if the XR runtime/device mix justifies it.
- [ ] **15.8** Validate foveation visually:
  - center clarity
  - periphery stability
  - stereo mismatch
  - UI/text readability
  - motion artifacts
- [ ] **15.9** Measure frame pacing and fragment workload savings versus quality loss.

## Phase 16 - Memory, Residency, And Transient Attachments

- [ ] **16.1** Add `VK_EXT_memory_budget` query/reporting.
- [ ] **16.2** Add `VK_EXT_memory_priority` query/reporting if driver coverage makes it useful.
- [ ] **16.3** Add memory heap/type budget reporting to startup and per-run logs.
- [ ] **16.4** Connect memory-budget data to VMA/allocator residency diagnostics.
- [ ] **16.5** Add explicit transient/lazily allocated attachment policy for temporary color/depth targets.
- [ ] **16.6** Validate transient attachments with dynamic rendering, resize, bloom/downsample, shadow maps, and capture targets.
- [ ] **16.7** Keep GPU-required allocation paths visible: if a transient/lazy policy is explicitly requested and unsupported, fail or warn loudly according to profile policy.

## Phase 17 - Ray Tracing And GI Extension Plumbing

Most ray-tracing feature work belongs to the ReSTIR/radiance-cache todos, but the dynamic-rendering/descriptor modernization work must not paint the binding model into a corner.

- [ ] **17.1** Share capability reporting for:
  - `VK_KHR_acceleration_structure`
  - `VK_KHR_ray_tracing_pipeline`
  - `VK_KHR_ray_query`
  - `VK_KHR_deferred_host_operations`
  - `VK_KHR_buffer_device_address`
  - descriptor indexing / descriptor heap
- [ ] **17.2** Make descriptor heap support acceleration-structure descriptors before enabling heap-backed ray tracing.
- [ ] **17.3** Validate ray query and ray tracing pipeline descriptor compatibility with material/resource table plans.
- [ ] **17.4** Keep ray-tracing backend selection independent from dynamic rendering target mode.

## Phase 18 - GPU-Driven Follow-Ups

- [ ] **18.1** Add capability reporting for `VK_EXT_device_generated_commands` without enabling a runtime path yet.
- [ ] **18.2** Defer device-generated command implementation until descriptor heap, shader objects, and GPU scene/material table contracts are stable.
- [ ] **18.3** Continue validating `VK_KHR_draw_indirect_count` and `VK_EXT_mesh_shader` as the current GPU-driven production path.
- [ ] **18.4** Ensure every GPU-driven fallback path reports whether the missing capability was optional, profile-required, or explicitly requested.

## Validation Checklist (Remaining)

Static validation (no unintended `CreateRenderPass` / `CreateFramebuffer` / `CmdBeginRenderPass` in the dynamic path, `PipelineRenderingCreateInfo` always present, attachment-format keys, legacy isolation) is complete and verified by `VulkanDynamicRenderingMigrationTests`.

### Build And Unit Tests

- [ ] Run targeted Vulkan tests:

  ```powershell
  dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter Vulkan
  ```

  2026-06-09 note: `--filter VulkanDynamicRenderingMigrationTests` passed 3/3. A broader Vulkan-focused filter passed 59 tests and failed 10 stale source-path assertions that still look under `XRENGINE\Rendering\API\Rendering\Vulkan\...` instead of `XREngine.Runtime.Rendering\Rendering\API\Rendering\Vulkan\...`. Fix the stale source-path assertions before relying on the broad filter.

  2026-06-18 note: `--filter VulkanDynamicRenderingMigrationTests --no-restore` did not reach test execution because `Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.dll` was locked first by `.NET Host (11020)` and then by `.NET Host (29760)`. A pre-fix `--no-build` run executed the existing test assembly and reported 4/9 passing with 5 stale source/assertion failures; the stale checks were updated in `VulkanDynamicRenderingMigrationTests.cs`, but rebuilt execution remains pending until the editor output DLL is unlocked.

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

### Modern Extension Validation

- [ ] Startup capability snapshot includes every extension/API in the Modern Vulkan Capability Matrix.
- [ ] Explicitly requested dynamic rendering, Sync2, descriptor heap, shader object, VRS, and ray-tracing modes fail visibly when unsupported.
- [ ] Descriptor indexing path still works after descriptor backend abstraction lands.
- [ ] Descriptor heap path renders the same material/texture/storage-buffer scenarios as descriptor indexing.
- [ ] Shader object path renders the same dynamic rendering targets as the pipeline-object path.
- [ ] Shader object path does not regress hot reload or material shader invalidation.
- [ ] Fragment shading rate path improves or preserves frame pacing within documented quality thresholds.
- [ ] Fragment density map path is enabled only when it has a validated XR/runtime use case.
- [ ] Memory budget reporting agrees with driver-reported heap/type data and allocator residency state.
- [ ] Ray-tracing capability reporting agrees with the ReSTIR/radiance-cache todo requirements.

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
- [ ] `Vulkan.Capability.<Name>`
- [ ] `Vulkan.Sync.Backend`
- [ ] `Vulkan.DescriptorBackend`
- [ ] `Vulkan.DescriptorHeap.Capability`
- [ ] `Vulkan.DescriptorHeap.Allocation`
- [ ] `Vulkan.DescriptorHeap.Write`
- [ ] `Vulkan.DescriptorHeap.Bind`
- [ ] `Vulkan.DescriptorHeap.FallbackDenied`
- [ ] `Vulkan.ShaderObject.Capability`
- [ ] `Vulkan.ShaderObject.Create`
- [ ] `Vulkan.ShaderObject.Bind`
- [ ] `Vulkan.ShaderObject.MissingDynamicState`
- [ ] `Vulkan.ShaderObject.FallbackDenied`
- [ ] `Vulkan.Foveation.FragmentShadingRate`
- [ ] `Vulkan.Foveation.FragmentDensityMap`
- [ ] `Vulkan.MemoryBudget`
- [ ] `Vulkan.RayTracing.Capability`
- [ ] `Vulkan.GpuDriven.DeviceGeneratedCommands`

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
- Vulkan API version
- extension name
- feature/property name and value
- selected backend
- requested backend
- fallback reason

Keep high-frequency logs behind existing throttles or explicit trace flags.

## Files Expected To Change (Remaining)

- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanBarrierPlanner.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanRenderGraphCompiler.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Extensions.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/LogicalDevice.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanSynchronization.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanDescriptorLayoutCache.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanDescriptorUpdateTemplates.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/DescriptorPool.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/DescriptorSetLayout.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/DescriptorSets.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Descriptors.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Pipeline.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/MeshRenderer/VkMeshRenderer.Drawing.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/Textures/VkImageBackedTexture.cs`
- [ ] `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Memory/VulkanVmaAllocator.cs`
- [ ] new descriptor heap backend files under `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/`
- [ ] new shader object backend files under `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/`
- [ ] new VRS/foveation capability files under `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/`
- [ ] relevant unit tests under `XREngine.UnitTests/Rendering/`
- [ ] relevant docs under `docs/work/design/rendering/` and `docs/work/todo/rendering/`

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
- [ ] Descriptor heap backend diverges from descriptor indexing material binding semantics.
- [ ] Descriptor heap capacity fragmentation causes intermittent missing descriptors.
- [ ] Descriptor heap shader mapping masks broken legacy set/binding declarations.
- [ ] Descriptor buffer work accidentally becomes the new long-term backend despite descriptor heap superseding it.
- [ ] Shader object path misses fixed-function dynamic state that pipeline objects previously baked.
- [ ] Shader object emulation layer is mistaken for native-driver performance coverage.
- [ ] Fragment shading rate or density map reduces UI/text legibility in VR.
- [ ] Memory budget data is logged but not connected to allocator decisions.
- [ ] Ray-tracing descriptor requirements conflict with material table or descriptor heap layout.
- [ ] Device-generated commands are implemented before the binding/program backends are stable.

## Open Questions

- [ ] Should explicit dynamic mode fail at initialization or only when the first graphics target is used?
- [ ] Should dynamic rendering local read become part of a future required Vulkan 1.4 tier?
- [ ] How much of `VkFrameBuffer` should survive as a Vulkan-specific attachment-view cache versus moving into a renderer-level target cache?
- [ ] Should render graph metadata become the only source of load/store behavior, or should `XRFrameBuffer` keep explicit overrides for hand-authored target flows?
- [ ] Should source files be renamed away from "RenderPass" after dynamic mode is default, even though the legacy path is retained?
- [ ] Should descriptor heap become the only v1 Vulkan binding architecture when supported, with descriptor indexing as fallback?
- [ ] Should descriptor buffer be skipped entirely unless driver coverage makes descriptor heap impractical?
- [ ] Should shader objects and descriptor heap ship together, or should descriptor heap land first under pipeline objects?
- [ ] Which Vulkan 1.4 features are worth requiring for the editor versus optional for clients/VR?
- [ ] Should foveation choose fragment shading rate first and fragment density map only for runtimes/devices where it is clearly better?
- [ ] Should ray tracing share the same descriptor heap global resource model from day one?

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
