# Vulkan render resource regression analysis

## Scope

This note analyzes the current Vulkan black-scene regression after the recent render
pipeline instance resource allocation/resizing and data-buffer writing work.

Evidence came from:

- `Assets/UnitTestingWorldSettings.jsonc`: `Rendering.RenderBackend` is `Vulkan`, ImGui editor is enabled, procedural sky and Sponza are active.
- Latest run logs:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-06-13_18-37-57_pid29020/`
- Source paths under `XREngine.Runtime.Rendering/Rendering/Resources`,
  `XREngine.Runtime.Rendering/Rendering/Pipelines`, and
  `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan`.

## Executive summary

The black 3D scene is not a simple shader/material failure. The scene render path is
driving Vulkan into invalid image layouts, invalid image view ranges, invalid image
usage transitions, and eventually device loss. ImGui can remain visible because it uses
a separate overlay/swapchain-oriented path and does not depend on the broken
DefaultRenderPipeline scene resources.

The latest log contains many Vulkan errors before device loss:

- `UNASSIGNED-CoreValidation-DrawState-InvalidImageLayout`: 4526
- `VUID-VkImageMemoryBarrier2-oldLayout-01197`: 2630
- `VUID-VkImageViewCreateInfo-subresourceRange-01478`: 656
- `VUID-VkImageViewCreateInfo-subresourceRange-01718`: 656
- `VUID-VkImageMemoryBarrier2-oldLayout-01211`: 481
- `VUID-VkDescriptorImageInfo-imageLayout-00344`: 18
- `VUID-vkCmdDraw-None-02699`: 18
- `VUID-VkImageMemoryBarrier2-oldLayout-01208`: 8

These validation errors do not themselves cause `VK_ERROR_DEVICE_LOST`. They report
invalid GPU work (sampling depth as color, layout/usage mismatches, out-of-range image
views) that the driver then actually executes, and that invalid work is what faults the
device. Treat the validation count as a symptom map, not the failure mechanism.

### Dominant root cause: the planner allocates wrong physical images

The single most direct cause of the black scene is that the Vulkan resource planner
creates physical images with the wrong format, one mip level, and one sample, regardless
of the resource spec:

1. `VulkanResourceAllocator.ResolveFormat(...)` understands only a handful of format
   labels and defaults everything else to `R8G8B8A8Unorm`. The resource spec carries the
   correct label all the way to the planner, so this is a pure translation gap, not
   descriptor drift. In the latest run this mis-allocates 863 images: depth buffers
   become color, `R32ui` ID targets become normalized RGBA, and HDR `R11fG11fB10f`
   lighting becomes 8-bit.
2. `AllocatePhysicalImage(...)` hardcodes `MipLevels = 1` and `Samples = Count1Bit`, so
   every view that references `baseMipLevel > 0`, a mip chain, or an MSAA source is
   invalid on creation.

These two bugs are independent of descriptor drift and would corrupt the scene on the
very first frame even if every descriptor were perfect.

### Two competing allocation paths produce contradictory images

The same logical resource is allocated by two different systems that disagree. The
"Dedicated texture image created" path resolves formats correctly (for example `Normal`
as `R16G16Sfloat`, `DepthStencil` as `D24UnormS8Uint`), while the planner's "Physical
image allocated" path resolves the same resources as `R8G8B8A8Unorm`. The latest run has
68 dedicated allocations versus 1304 planner allocations, so the wrong path dominates.
Two allocators emitting contradictory images for the same resource is the structural
heart of the regression.

### Contributing bugs

These do not cause the black first frame on their own, but they keep the scene path
unreliable and break resize:

1. Later bind/tracking paths overwrite generated descriptors from concrete texture/FBO
   instances, converting `InternalResolution`/`WindowResolution` policies to
   `AbsolutePixels`. At steady state these resolve to the same extent, so this is
   primarily a resize-stability problem, not the first-frame cause.
2. The descriptor type is too lossy: it drops resource usage, mip policy, MSAA sample
   count, typed image format, and texture-view source/subresource information, which is
   why the planner has to guess in the first place.
3. Texture views are planned as independent physical images instead of views over a
   source image.
4. The barrier planner tracks layouts by logical name/whole image, while descriptors may
   sample a source image through a view. It then emits transitions from stale old layouts
   and updates its tracked state even after invalid barriers.

There are multiple bugs here. Fixing only one will reduce the error count, but will not
make the scene path reliable.

## Log evidence

### Descriptor drift is visible immediately

`log_rendering.log` reports 63 descriptor parity mismatch warnings. The first batch
appears immediately after resource generation `DefaultRenderPipeline#28` commits at
1920x1080.

Examples:

- `DepthStencil`: expected `InternalResolution`, actual `AbsolutePixels 1920x1080`
- `AmbientOcclusionTexture`: expected `InternalResolution`, actual `AbsolutePixels 1920x1080`
- `HDRSceneTex`: expected `InternalResolution`, actual `AbsolutePixels 1920x1080`
- `TsrOutputTexture`: expected `WindowResolution`, actual `AbsolutePixels 1920x1080`
- `GBufferFBO`, `ForwardPassFBO`, `HistoryCaptureFBO`, `TsrUpscaleFBO`: expected policy
  descriptors but actual concrete framebuffer dimensions

The same mismatch repeats after the window resizes to `2560x1369`.

This is not harmless diagnostic noise. The Vulkan planner consumes the registry
descriptors, so once they drift, allocation and alias keys drift too.

### First validation errors are layout-state mismatches

The first hard Vulkan errors are layout transitions whose `oldLayout` is not the actual
current layout:

- `log_vulkan.log:412`: transition from `COLOR_ATTACHMENT_OPTIMAL`, but the previous
  layout is `SHADER_READ_ONLY_OPTIMAL`.
- `log_vulkan.log:425` and `log_vulkan.log:437`: transition depth/stencil aspects from
  `DEPTH_STENCIL_ATTACHMENT_OPTIMAL`, but the previous layout is
  `DEPTH_STENCIL_READ_ONLY_OPTIMAL`.

Soon after that, descriptor use is invalid:

- `log_vulkan.log:852`: a draw samples a depth/stencil image with descriptor layout
  `DEPTH_STENCIL_READ_ONLY_OPTIMAL`, but the actual layout is
  `DEPTH_STENCIL_ATTACHMENT_OPTIMAL`.
- `log_vulkan.log:864`: `vkCmdDraw` reports the bound descriptor set image layout no
  longer matches the actual layout.

The renderer then reaches device loss:

- `log_rendering.log:656`: `System.InvalidOperationException: Vulkan device is lost.
  Cannot render until the device is recreated.`

### Images are allocated with impossible/wrong definitions

These come from the planner's "Physical image allocated" path. The parallel "Dedicated
texture image created" path resolves the same resources correctly, so the two paths
disagree (68 dedicated vs 1304 planner allocations, 863 of them forced to
`R8G8B8A8Unorm`). Physical allocation logs show multiple classes of bad image creation:

- Format labels that should map to specialized Vulkan formats are defaulting to
  `R8G8B8A8Unorm`:
  - `DepthComponent32f` -> `R8G8B8A8Unorm` (planner) vs `D24UnormS8Uint` (dedicated `DepthStencil`)
  - `R11fG11fB10f` -> `R8G8B8A8Unorm`
  - `Rg16f` -> `R8G8B8A8Unorm` (planner) vs `R16G16Sfloat` (dedicated `Normal`)
  - `R16f` -> `R8G8B8A8Unorm`
  - `R32ui` -> `R8G8B8A8Unorm`
- Texture views are allocated as independent images:
  - `ForwardPassMsaaDepthView` is allocated as a physical image with format label
    `XRTexture2DView`.
  - Bloom mip source views are also allocated as physical images.
- Some images are created without usage bits later required by transitions:
  - transition to color attachment layout without `VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT`
  - transition to shader-read layout without sampled/input-attachment usage
- Mip image views are requested against one-mip images, producing repeated
  `VUID-VkImageViewCreateInfo-subresourceRange-01478` and `01718`.

## Finding 1: generated descriptors are overwritten by concrete instance descriptors

The new resource generation path starts correctly. In
`XRRenderPipelineInstance.MaterializeTexture` and `MaterializeFrameBuffer`, the resource
manager registers descriptors lowered from the layout spec, then binds the created
instance with that descriptor.

The problem is that later registry bind paths can silently replace the spec descriptor:

- `RenderResourceRegistry.BindTexture(...)` uses
  `RenderResourceDescriptorFactory.FromTexture(texture)` when no descriptor is provided.
- `RenderResourceRegistry.BindFrameBuffer(...)` does the same with
  `FromFrameBuffer(frameBuffer)`.
- `RenderResourceDescriptorFactory.FromTexture(...)` and `FromFrameBuffer(...)` describe
  the already-created object with absolute pixel sizes.
- `VulkanRenderer.State.EnsureFrameBufferRegistered(...)` calls
  `registry.BindFrameBuffer(frameBuffer)` without the spec descriptor.
- `VulkanRenderer.State.EnsureFrameBufferAttachmentsRegistered(...)` calls
  `registry.BindTexture(texture)` without the spec descriptor.
- `VPRC_CacheOrCreateTexture.RegisterDescriptor(...)` and
  `VPRC_CacheOrCreateFBO.RegisterDescriptor(...)` also rebuild descriptors from
  concrete objects.

That explains the parity warnings exactly: a generation-owned `InternalResolution`
resource is materialized at 1920x1080, then a later bind path registers the already-sized
object as `AbsolutePixels 1920x1080`.

### Why this breaks Vulkan

The Vulkan resource planner does not know which descriptors are authoritative. It sees
the drifted registry state and builds alias keys and physical groups from it. Resize
handling then becomes unstable because a resource that should be "internal resolution,
scale 1" is now "absolute 1920x1080" or "absolute 2560x1369". This causes resource plan
churn and can split resources that should share policy identity.

### Fix

Make spec descriptors authoritative for generation-owned resources.

Recommended changes:

1. Add descriptor provenance/ownership to registry records, for example
   `GeneratedSpec`, `ObservedExternal`, `LegacyCommand`.
2. Change no-descriptor `BindTexture`, `BindFrameBuffer`, `BindBuffer`, and
   `BindRenderBuffer` so they do not overwrite an existing generated descriptor. They
   should bind the instance only.
3. Add explicit APIs for the two separate operations:
   - `RegisterOrUpdateDescriptor(...)`
   - `BindInstancePreservingDescriptor(...)`
4. Update Vulkan helper paths to preserve descriptors:
   - `EnsureFrameBufferRegistered`
   - `EnsureFrameBufferAttachmentsRegistered`
   - blit setup paths
   - `TrackTextureBinding`
   - `TrackBufferBinding`
5. Update legacy cache/create render commands so managed resources resolve the expected
   descriptor from `ActiveGeneration.Layout` instead of rebuilding from concrete
   dimensions.
6. In Vulkan/debug builds, promote descriptor parity mismatch from warning to hard
   failure before the planner allocates physical resources.

## Finding 2: `TextureResourceDescriptor` is too lossy for Vulkan allocation

`TextureSpec` contains the information Vulkan needs:

- `Usage`
- `Samples`
- `MipPolicy`
- `InternalFormat`, `PixelFormat`, `PixelType`, `SizedInternalFormat`
- texture-view source and subresource fields in `TextureViewSpec`

But lowering to `TextureResourceDescriptor` preserves only:

- name/lifetime/size policy
- string `FormatLabel`
- stereo flag/layer count
- alias/storage booleans

It drops:

- usage flags
- sample count
- mip count / auto-mip policy
- typed format values
- texture-view source texture
- base mip/layer and view ranges
- view aspect information

This forces the Vulkan allocator to rediscover intent from render-pass metadata and FBO
descriptors. That inference is not complete enough for a render pipeline this large.

### Fix

Extend the resource descriptor model rather than patching around the losses downstream.

Recommended descriptor fields:

- `RenderPipelineResourceKind Kind`
- `RenderPipelineResourceUsage Usage`
- `ESizedInternalFormat? SizedInternalFormat`
- `EPixelInternalFormat? InternalFormat`
- `EPixelFormat? PixelFormat`
- `EPixelType? PixelType`
- `uint Samples`
- `RenderResourceMipPolicy MipPolicy`
- `uint ResolvedMipLevels` or enough information to compute it deterministically
- for views:
  - `SourceTextureName`
  - `BaseMipLevel`
  - `MipLevelCount`
  - `BaseLayer`
  - `LayerCount`
  - `DepthStencilAspect`
  - `ArrayTarget`
  - `Multisample`

For Vulkan, typed formats should be carried to allocation. String labels should be
diagnostic-only.

## Finding 3: image usage flags are inferred from incomplete metadata

`VulkanResourceAllocator.InferImageUsage(...)` starts with transfer usage, then adds
usage from pass metadata, FBO descriptors, depth format detection, and
`RequiresStorageUsage`.

That is fragile because:

- pass metadata can miss legacy/optional paths
- texture-view names do not map to source textures
- FBO descriptor drift changes the planner input
- spec `Usage` is ignored after lowering

The log confirms missing usage bits:

- `VUID-VkImageMemoryBarrier2-oldLayout-01208`: transition to
  `COLOR_ATTACHMENT_OPTIMAL` on an image missing color attachment usage.
- `VUID-VkImageMemoryBarrier2-oldLayout-01211`: transition to
  `SHADER_READ_ONLY_OPTIMAL` on an image missing sampled/input attachment usage.

### Fix

Use spec usage as the authoritative baseline:

1. Convert `RenderPipelineResourceUsage` directly into Vulkan `ImageUsageFlags`.
2. Add FBO/pass metadata usage only as extra usage.
3. For texture views, apply usage to the source physical image, not a view allocation.
4. Validate every planned layout transition against image creation usage before command
   buffer recording. If a pass wants a layout that the image usage does not permit,
   fail the plan with resource name, pass name, and missing bit.

## Finding 4: Vulkan format mapping is incomplete and silently falls back to RGBA8

This is the dominant root cause of the black scene, not a secondary issue.
`VulkanResourceAllocator.ResolveFormat(...)` first tries `Enum.TryParse` against the
Vulkan `Format` enum (which never matches the engine's label names), then a switch that
recognizes only five labels (`rgba16f`, `rgba8`, `rgb10a2`, `depth24stencil8`,
`depth32`/`depth32f`), and defaults everything else to `R8G8B8A8Unorm`.

However, `TextureSpec.ResolveFormatLabel()` emits engine enum names such as:

- `Rg16f`
- `R16f`
- `R32ui`
- `R11fG11fB10f`
- `DepthComponent32f`

These do not map through the current switch, so Vulkan allocates many resources as
RGBA8. Depth resources can become color images, integer ID buffers can become normalized
RGBA images, and lighting/normal buffers lose precision.

### Fix

1. Stop using string labels for allocation.
2. Route `ESizedInternalFormat` through the existing Vulkan format conversion helpers
   or add a complete conversion table.
3. Treat an unknown render-resource format as a planner error, not as RGBA8.
4. Add tests for every format label currently declared by `DefaultRenderPipeline`.

Genuinely missing mappings from the latest log (the switch does not handle these, so they
fall through to `R8G8B8A8Unorm`):

- `Rg16f` -> `R16G16Sfloat`
- `R16f` -> `R16Sfloat`
- `R32f` -> `R32Sfloat`
- `R32ui` -> `R32Uint`
- `R11fG11fB10f` -> `B10G11R11UfloatPack32`
- `DepthComponent32f` -> `D32Sfloat`

`Rgba16f` -> `R16G16B16A16Sfloat` and `Depth24Stencil8` -> `D24UnormS8Uint` are already
handled by the existing switch (the dedicated `DepthStencil` image confirms
`Depth24Stencil8` resolves correctly), so they are not part of the gap.

## Finding 5: planner-backed images are always one mip and one sample

`VulkanRenderer.State.AllocatePhysicalImage(...)` hardcodes:

- `MipLevels = 1`
- `Samples = SampleCountFlags.Count1Bit`

`VkImageBackedTexture` also treats planner-backed images as one mip:

- `_mipLevelsOverride = 1`
- `SampleCount => Count1Bit`

This conflicts with pipeline resources that declare:

- generated mip chains, for example HDR/bloom resources
- texture views over mip levels
- MSAA resources for deferred/forward paths

The log's repeated image-view subresource range VUIDs are exactly what happens when
views request `baseMipLevel > 0` or multiple mips from an image created with one mip.

### Fix

1. Compute required mip count per physical image group:
   - explicit `MipPolicy.MipLevelCount`
   - full chain when `AutoGenerateMipmaps` is true
   - maximum referenced view mip range
2. Carry sample count from `TextureSpec.Samples` and `RenderBufferSpec.Samples` into
   Vulkan physical image creation.
3. Include mip count and samples in the physical group template and planner signature.
4. Update `VkImageBackedTexture` overrides from the physical group instead of hardcoding
   one mip/one sample.
5. Validate every `VkTextureView` range against the source physical image before
   `vkCreateImageView`.

## Finding 6: texture views are planned as independent physical images

`TextureViewSpec.ToDescriptor()` returns a normal `TextureResourceDescriptor`.
`VulkanResourcePlanner.BuildPlan()` then creates a `VulkanAllocationRequest` for every
texture descriptor. It cannot distinguish real textures from views.

Evidence:

- `ForwardPassMsaaDepthView` is allocated as a physical image.
- Bloom mip source views are allocated as physical images.
- The allocation format label can be `XRTexture2DView`, which is a type/name artifact,
  not an image format.

But `VkTextureView` creates an image view against `Data.GetViewedTexture()`, meaning the
actual descriptor image is the source texture's image. The planner and barrier system may
therefore transition and track a separate physical image that descriptors never sample.

### Fix

Represent texture views as views, not textures.

Recommended design:

1. Add a `TextureViewResourceDescriptor`.
2. Store view descriptors separately in the registry and planner.
3. Do not allocate a physical image for a view.
4. Resolve a view to its source physical image plus subresource range when:
   - building usage profiles
   - planning barriers
   - validating image view creation
   - writing descriptors
5. Include source texture and subresource range in descriptor parity checks.

This is likely one of the largest contributors to descriptor layout mismatches, because
the barrier planner can currently update layout state for a view-name physical group
while the descriptor samples the source image.

## Finding 7: layout tracking is too coarse and can diverge

`VulkanBarrierPlanner` tracks image state by logical resource name. `CommandBuffers`
then emits barriers against the physical group and updates `group.LastKnownLayout`.

Problems:

- A physical image can be accessed through multiple logical names, especially once views
  are involved.
- A depth/stencil image has separate aspect usage, but `LastKnownLayout` is one layout
  for the whole group.
- Mip and layer ranges are ignored in planned barriers; emitted ranges are hardcoded to
  `BaseMipLevel = 0`, `LevelCount = 1`, all layers.
- `EmitPlannedImageBarriers(...)` uses the planner's previous layout unless it is
  `Undefined`, even when `group.LastKnownLayout` already disagrees.
- After emitting the barrier, it sets `planned.Group.LastKnownLayout =
  planned.Next.Layout` regardless of whether validation rejected the barrier.

The latest log shows multiple `BeginRendering` events whose attachments are already
tracked as shader-read/read-only before being used for rendering, then validation rejects
the transition from the wrong old layout.

### Fix

1. Track layouts per physical image subresource key:
   - aspect mask
   - mip range
   - layer range
2. Resolve logical resource names through view descriptors before state tracking.
3. Use the actual tracked current layout for the affected subresource as barrier
   `oldLayout`. If the graph's expected previous layout disagrees, log/fail with the
   pass/resource chain instead of emitting a known-invalid transition.
4. Update tracked state only after a command buffer path successfully records a legal
   transition.
5. Descriptor image layouts must be written from the same subresource layout tracker, or
   the renderer must insert the required transition before descriptor use.
6. Handle read-only depth/stencil vs depth/stencil attachment layout as explicit
   per-aspect states.

## Finding 8: render graph metadata is noisy/incomplete

The latest Vulkan log reports missing resource metadata before allocation:

- missing declared framebuffer `__OUTPUT_FBO__`
- many optional FBOs/textures absent under the active safe feature profile
- FBO slot references without matching color attachments for post-process FBOs

These warnings are not the first cause of device loss, but they matter because image
usage is currently inferred from pass metadata. Any metadata gap can become a missing
Vulkan image usage bit.

### Fix

1. Normalize `__OUTPUT_FBO__`/swapchain output as an external resource instead of a
   missing FBO.
2. Generate pass metadata from the same feature predicates used by the active resource
   layout.
3. Distinguish disabled optional resources from required missing resources.
4. Keep metadata validation, but stop relying on metadata as the sole source of Vulkan
   image usage.

## Finding 9: data-buffer writing is probably not the black-scene root cause

The recent data-buffer writing changes should still be tested, but the current log
signature is image-centric:

- No buffer upload VUID dominates the log.
- The first fatal errors are image layout transitions and descriptor image layouts.
- Device loss follows thousands of image/layout validation failures.

The buffer write model does have adjacent risks to harden:

- `XRBufferDirtyRange` is used to carry element offsets inside
  `XRBufferWriter.MarkDirty(...)`, then converted to byte ranges in
  `CommitWriterRanges<T>(...)`. The math is currently intentional, but the field names
  `OffsetBytes`/`LengthBytes` make misuse easy.
- `XRDataBuffer<T>.Write(...)` allocates a writer over `Math.Max(ElementCount,
  elementOffset + data.Length)` and marks only the changed subrange dirty. That is okay
  for preserve writes, but should be covered by tests for growth plus partial updates.
- `VkDataBuffer.PushData(...)` retires old buffers and `PushSubData(...)` falls back to
  full upload if the GPU allocation is too small. That looks structurally correct, but
  resizing while descriptors/commands reference old buffers should remain covered by
  fence/retirement tests.
- Shutdown/device-loss teardown still logs destruction-related VUIDs. Those appear
  secondary to the render failure, but retirement flushing should be audited separately
  after image errors are fixed.

### Fix

1. Add targeted tests for:
   - `XRDataBuffer<T>.Write` partial update without growth
   - partial update with growth
   - multiple dirty ranges collapsing
   - Vulkan `PushSubData` fallback to full upload when allocation is too small
2. Consider renaming the element-level dirty range type or adding a distinct
   `XRBufferElementRange` to avoid byte/element ambiguity.
3. Add diagnostics that distinguish image-resource device loss from buffer upload
   failure so future logs do not conflate them.

## Recommended fix order

Priority correction: the highest-impact, lowest-cost fix is Phase 3's format, mip, and
sample correctness, because that is the dominant root cause and it is independent of
descriptor drift. Do it first (after the Phase 0 diagnostics), then texture views
(Phase 6 content) and layout tracking (Phase 4). Descriptor preservation and
expressiveness (Phases 1-2) are required for resize stability and to feed the planner
declaratively, but they will not fix the black first frame on their own, so they should
follow rather than lead. The phases below keep their original numbering for reference;
the recommended execution order is 0 -> 3 -> 6 -> 4 -> 1 -> 2 -> 5.

### Phase 0: fail earlier and improve diagnostics

- Make descriptor parity mismatch fatal in Vulkan/debug builds.
- Add planner dump fields for resource kind, source spec kind, usage, mips, samples,
  source texture for views, resolved format, and actual image usage.
- Add a one-frame validation summary that separates setup errors from shutdown teardown
  noise.

### Phase 1: stop descriptor drift

- Preserve generated descriptors when no explicit descriptor is provided.
- Update Vulkan registration/tracking helpers to bind instances without replacing
  spec descriptors.
- Update legacy cache/create commands to use active layout descriptors for managed
  resources.

Expected result: descriptor parity warnings drop to zero. Physical allocation logs should
show `InternalResolution`/`WindowResolution` policies instead of concrete absolute sizes
for managed resources.

### Phase 2: make descriptors expressive enough

- Carry usage, mips, samples, typed format, and view source/range data through
  descriptor lowering.
- Split real texture descriptors from texture view descriptors.

Expected result: the planner can allocate from declarative resource intent without
guessing from pass metadata.

### Phase 3: fix Vulkan allocation

- Allocate correct Vulkan formats.
- Allocate correct image usage flags from spec usage plus metadata.
- Allocate required mip levels.
- Allocate correct sample counts.
- Do not allocate images for texture views.
- Include mips/samples/usage/view-source in planner signature and physical group keys.

Expected result: image-view subresource range VUIDs and image usage/layout compatibility
VUIDs disappear.

### Phase 4: fix barrier and descriptor layout tracking

- Resolve views to source physical images before planning barriers.
- Track layout per physical subresource instead of one layout per logical name/group.
- Use actual tracked current layout as `oldLayout`; fail on graph/tracker divergence.
- Write descriptor image layouts from the same tracker or transition immediately before
  descriptor use.

Expected result: old-layout mismatch VUIDs and descriptor image layout draw errors
disappear.

### Phase 5: buffer write and lifetime hardening

- Add the buffer write tests listed above.
- Audit retired resource flushing during shutdown/device loss.
- Keep this separate from the image-resource fix so the root-cause signal stays clear.

## Validation plan

After implementing phases 1 through 4:

1. Build the editor:
   `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
2. Launch the editor with Vulkan, Unit Testing World, and MCP enabled:
   `dotnet .\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.dll --unit-testing --mcp --mcp-allow-all --mcp-port 5467`
3. Capture at least two viewport screenshots from different camera positions.
4. Confirm the scene changes with the camera and is not a stale/black render target.
5. Inspect the latest `log_vulkan.log` and `log_rendering.log`:
   - zero descriptor parity mismatches
   - zero image-view subresource range VUIDs
   - zero old-layout mismatch VUIDs during steady-state rendering
   - zero descriptor image layout draw VUIDs
   - no device loss
   - physical allocation formats match resource specs
   - physical allocation usage includes every required usage
   - mip and sample counts match resource specs
6. Run targeted Vulkan/resource tests:
   - `Test-VulkanPhase3-Regression`
   - new descriptor-lowering/planner tests
   - new texture-view alias/source tests
   - new buffer dirty-range/write tests

## Bottom line

The new generation/resource system is directionally right, but the Vulkan planner is
building physical images with the wrong format, one mip, and one sample, and a second
"dedicated" allocation path disagrees with it. That format/mip/sample defaulting is the
primary cause of the black scene and is independent of descriptor drift; fix it first.
Beyond that, the fix should not be a one-off layout transition patch: texture views need
first-class representation, layout tracking needs to follow physical subresources rather
than logical names, and the resource registry needs to preserve authoritative generation
descriptors carrying the Vulkan-critical fields (so the planner stops guessing and resize
stays stable).
