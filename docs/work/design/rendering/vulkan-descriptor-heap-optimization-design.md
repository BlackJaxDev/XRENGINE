# Vulkan Descriptor Heap Optimization Design

Status: design proposal
Last Updated: 2026-07-01
Owner: Rendering

## Related Docs

- [Vulkan Dynamic Rendering Migration TODO](../../todo/rendering/vulkan-dynamic-rendering-migration-todo.md)
- [Deferred+ Render Path Design](deferred-plus-render-path-design.md)
- [Deferred+ Render Path TODO](../../todo/rendering/optimization/deferred-plus-render-path-todo.md)
- [Retinal Visibility Cache Rendering Design](retinal-visibility-cache-rendering-design.md)
- [Retinal Visibility Cache Rendering TODO](../../todo/rendering/vr/retinal-visibility-cache-rendering-todo.md)
- [Dynamic Indirect Material Bindings](dynamic-indirect-material-bindings.md)
- [GPU Meshlet Zero-Readback Rendering Design](gpu-meshlet-zero-readback-rendering-design.md)
- [Material Binding Policy](../../../architecture/rendering/material-binding-policy.md)
- [Vulkan Renderer](../../../architecture/rendering/vulkan-renderer.md)
- [Vulkan ReSTIR Radiance Cache GI TODO](../../todo/rendering/vulkan-restir-radiance-cache-gi-todo.md)

## External References

- Vulkan descriptor heaps:
  <https://docs.vulkan.org/spec/latest/chapters/descriptorheaps.html>
- `VK_EXT_descriptor_heap` extension metadata:
  <https://docs.vulkan.org/spec/latest/appendices/extensions.html#VK_EXT_descriptor_heap>
- Vulkan device-generated commands:
  <https://docs.vulkan.org/spec/latest/chapters/device_generated_commands.html>
- Vulkan shader objects:
  <https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_shader_object.html>

## Summary

`VK_EXT_descriptor_heap` should become XRENGINE's preferred Vulkan binding
architecture when the device supports it. Descriptor indexing remains the
fallback for Vulkan 1.2/1.3-class hardware and driver stacks. Descriptor buffer
should not become the new long-term path because Khronos explicitly positions
descriptor heap as the replacement direction.

Descriptor heaps do not, by themselves, merge render calls. They provide the
global resource-addressing layer that makes large batches, indirect draws,
material-region dispatch, RVC shadelet caches, ray tracing scene tables, and
post-process graphs cheaper to bind. The render-call reduction comes from
putting material/resource identity in GPU-visible data and letting shaders read
heap indices from material rows, draw records, shadelet records, or pass tables.

The target shape is:

```text
Frame setup
  write descriptors into one resource heap and one sampler heap
  update material/pass/light/shadelet rows with heap indices

Command recording
  bind sampler heap and resource heap once per command buffer scope
  bind pipeline or shader object for one compatible kernel
  push small frame/pass/view data
  draw/dispatch large batches whose GPU data selects resources

Shader
  read MaterialId / DrawId / ShadeletId / PassResourceId
  load material or pass row
  use heap indices to access textures, buffers, images, TLAS, or probes
```

In one sentence: descriptor heaps turn resource binding into stable GPU data,
but the pipeline still needs material tables, indirect commands, and compatible
shader kernels to turn that into fewer submissions.

## Current Engine State

The Vulkan migration work has already added the first descriptor heap backend
slice:

- capability probing and startup diagnostics for `VK_EXT_descriptor_heap`
- native interop for descriptor sizes, heap binding, descriptor writes, push
  data, and legacy set/binding mapping
- one resource heap and one sampler heap when the backend is active
- descriptor writes for images, samplers, buffers, and texel buffers
- legacy shader mapping through
  `VkShaderDescriptorSetAndBindingMappingInfoEXT`
- heap pipeline creation via `VK_PIPELINE_CREATE_2_DESCRIPTOR_HEAP_BIT_EXT`
- heap payloads for material, mesh draw, compute, and ImGui descriptor binding
- descriptor indexing and descriptor sets as fallback

The current path is intentionally conservative. It maps existing set/binding
shaders onto heap indices mostly through push data. That is enough to stop
falling back to descriptor sets for ordinary draw binding, but it is not yet the
full optimization model. The production optimization model needs shaders and
material tables that can load heap indices from GPU-visible records without a
CPU push for every material or draw.

## Binding Model

### Heap Ownership

Use two global Vulkan heaps:

| Heap | Contents |
| --- | --- |
| Resource heap | sampled images, storage images, uniform/storage buffers, texel buffers, input attachments, acceleration structures, tensor resources where supported |
| Sampler heap | sampler descriptors |

The renderer owns allocation, generation, debug names, lifetime tracking, and
fallback descriptors. Higher-level systems should not allocate Vulkan
descriptor sets when the heap backend is active. They should ask the renderer
for stable heap references.

### Stable References

Every descriptor exposed above the Vulkan backend should have a stable,
backend-neutral reference shape:

```text
ResourceBindingRef
  uint ResourceHeapIndex
  uint SamplerHeapIndex
  uint Generation
  uint Flags
```

For OpenGL bindless, the same logical reference can map to a resident texture
handle table. For Vulkan descriptor indexing fallback, it maps to descriptor
array indices. For coarse fallback, it maps to per-material bucket routing. The
render pipeline should talk about material texture references and pass resource
references, not descriptor-set handles.

### Null And Fallback Descriptors

Descriptor heap paths need explicit null/default entries:

| Role | Default |
| --- | --- |
| Missing albedo | white texture |
| Missing normal | flat normal texture |
| Missing roughness/metallic | configured RMSE fallback texture or packed constants |
| Missing storage image | fail in required modes, otherwise route pass to fallback |
| Missing buffer | zero-sized/null descriptor only when feature and shader contract allow it |
| Missing TLAS | fail requested ray tracing path visibly |

Fallback entries should be real heap records so shader code does not branch
around missing resources in the common case.

### Push Data Versus GPU Data

Descriptor heap supports multiple mapping modes. XRENGINE should use them in
tiers:

| Tier | Use |
| --- | --- |
| Push-index mapping | Compatibility bridge for legacy set/binding shaders and CPU-direct draws. |
| Material/pass rows with heap indices | Main render-pipeline optimization path. |
| Indirect index/address mapping | GPU-driven and device-generated command path where CPU cannot push per draw. |
| Native `SPV_EXT_descriptor_heap` shaders | Long-term shader model once tooling and shader variants are ready. |

Push-index mapping is useful, but it should not become the only design. If an
indirect draw call contains many materials, the CPU cannot push a different
descriptor index for each draw inside that call. The shader must read a material
row, draw record, or indirect record that contains the heap indices.

## What Descriptor Heaps Do Not Solve

Descriptor heaps are powerful, but they do not erase render-state rules.
Separate batches or kernels are still needed for:

- different graphics pipelines or shader families
- incompatible blend/depth/stencil/raster state
- transparency and order-dependent rendering
- alpha-test coverage that must affect visibility
- material models that need different generated kernels
- attachment formats and render-target layouts
- synchronization and image layout transitions
- shader divergence limits in huge all-material kernels

The right mental model is "one draw or dispatch per compatible state/kernel
family", not "one draw for the whole scene."

## Opportunity Matrix

| Feature Area | Descriptor Heap Benefit | Required Companion Work |
| --- | --- | --- |
| CPU-direct material draws | Fewer descriptor set updates and binds; per-material descriptors become small heap payloads. | Keep draw sorting by state; move material resources into heap-backed rows. |
| GPU indirect and meshlet rendering | Large indirect batches can vary material textures through material rows instead of CPU rebinding. | Draw metadata must carry material row IDs; shaders must load heap indices from GPU data. |
| Deferred+ compatibility resolve | One standard PBR resolve dispatch can sample many materials from the material table. | Visibility payload, material classification, material rows with heap indices, explicit gradients. |
| Deferred+ native shading | One material-family kernel can shade many material instances and regions. | Region/pixel lists grouped by kernel/layout, not descriptor set; no CPU push per material. |
| RVC shadelet cache | Shadelets can cache material/resource identity independent of eye/view. | Shadelet keys and records need material row/generation; view-dependent resources stay per-view. |
| Quad-view Forward+ baseline | Per-view command buffers can share global scene/light/material heaps. | View-set pass data must be separate from resource binding. |
| Clustered froxel lighting | Light records, grids, shadows, cookies, and probe textures become stable heap refs. | Shared light resource table and froxel grid ownership. |
| Render graph/post process | Pass resources can be heap refs rather than regenerated descriptor sets. | Per-pass resource table, barriers, and lifetime integration. |
| ImGui/editor overlays | Texture IDs can resolve to heap payloads without per-image descriptor set churn. | Keep editor resources registered through renderer heap service. |
| Texture streaming and virtual texturing | Resident pages, indirection maps, feedback images, and fallback pages are heap-addressed. | Residency generation checks and streaming-safe fallback descriptors. |
| Ray tracing/ReSTIR/GI | TLAS, material table, vertex/index buffers, blue noise, probe atlases, and reservoir buffers share one scene resource model. | Acceleration-structure descriptors, SBT integration, closest-hit material lookup. |
| Shader objects | Descriptor heap removes pipeline layouts; shader objects remove monolithic pipelines. | Unified program/shader binding metadata and push-data contract. |
| Device-generated commands | DGC can reference heap indices/addresses in indirect data instead of CPU-bound descriptors. | Indirect token layouts and shader compatibility. |

## Deferred+ Application

Deferred+ should treat descriptor heap as the preferred Vulkan texture/resource
binding rung:

```text
DescriptorHeap
  -> DescriptorIndexing
  -> OpenGLBindless
  -> TextureArray for homogeneous groups
  -> Coarse per-material/per-bucket fallback
```

The visibility pass itself should stay texture-free, so descriptor heap mostly
benefits the later phases:

1. Compatibility material resolve:
   - one PBR resolve kernel reads `MaterialId`
   - material row stores heap indices for albedo, normal, RMSE, emissive, and
     optional texture slots
   - pass rows store G-buffer outputs and shared scene buffers
   - descriptor indexing remains a fallback row encoding
2. Native material-region shading:
   - classifier groups pixels by `shadingKernelId + materialLayoutHash`
   - each kernel shades many material instances by reading material rows
   - graphics-region and compute pixel-list backends must not bind descriptors
     per material
3. Meshlet and GPU-driven integration:
   - indirect draw records write visibility using material/draw IDs
   - material shading later resolves resources through heap-backed rows
   - no same-frame CPU readback or per-draw descriptor push is allowed in the
     production path

Deferred+ should not compile a pipeline per material instance. It should compile
one kernel per material family/layout and use heap-backed material rows for
instance variation.

## RVC Application

RVC is a stronger descriptor heap consumer than ordinary stereo rendering
because it intentionally shares material and stable lighting work across views.

Use descriptor heap to make resource identity view-independent:

- visibility samples store draw/material identity, not descriptor handles
- shadelet records store material row ID plus generation
- material rows store resource and sampler heap indices
- head-space light clusters reference heap-backed shadow maps, cookies, probe
  resources, and light buffers
- per-view resolve only supplies view-specific camera, projection, viewport,
  foveation, and correction data

The RVC shadelet cache should never key on descriptor set objects. Descriptor
sets are backend artifacts and would prevent reuse across views, command
buffers, and future shader object paths. The key should use stable material,
surface, deformation, LOD, and resource generations.

Recommended RVC flow:

```text
Quad-view visibility
  -> shadelet request generation
  -> material bins by kernel/layout
  -> heap-backed material shadelet evaluation
  -> shared head-space light/shadow/probe evaluation
  -> per-view resolve with eye-specific correction
```

The descriptor heap benefit is largest in phases that would otherwise rebuild
or bind equivalent material/light descriptors independently for left wide, right
wide, left inset, and right inset views.

## GPU-Driven And Device-Generated Commands

The current CPU path can push heap indices before each draw. GPU-driven paths
cannot rely on that. The data path must be:

```text
Indirect draw record
  -> DrawId
  -> DrawMetadata[DrawId].MaterialId
  -> MaterialTable[MaterialId].HeapIndices
  -> descriptor heap access in shader
```

For meshlet and device-generated command paths:

- command generation may group by pipeline/state class
- material row IDs and heap indices remain data, not command-buffer state
- indirect command buffers should not encode descriptor-set binds per material
- validation must catch accidental CPU fallback when material diversity rises

Once `VK_EXT_device_generated_commands` is evaluated, prefer token layouts that
push small draw constants or addresses and leave texture/material resources in
heap-backed tables.

## Ray Tracing And GI

Ray tracing should share the descriptor heap model from day one instead of
building a parallel descriptor-indexed scene layout.

Required heap-backed resources:

- TLAS descriptors
- vertex and index buffer address ranges
- instance, mesh, submesh, transform, and material tables
- material texture references
- blue-noise textures
- reservoir and radiance-cache buffers
- probe atlases and direction/depth images

Ray-query compute and RT closest-hit shaders should consume the same logical
scene resource table wherever possible. The fallback implementation can still
use descriptor indexing, but the renderer-facing contract should be heap-ready.

## Render Graph And Post-Process Passes

Frame-graph resources should be able to publish heap references as part of
resource state:

```text
RenderGraphResource
  image/buffer handle
  current layout/access state
  resource heap index
  sampler heap index if sampled
  generation/residency state
```

Passes then consume a compact pass-resource table instead of allocating
descriptor sets every time the graph is compiled or resized. This is especially
useful for:

- TSR/TAA history
- bloom mip chains
- AO/GI intermediates
- depth pyramids and HZB
- shadow atlases
- foveated resolve intermediates
- debug views

The frame graph still owns barriers and lifetimes. Descriptor heap does not make
layout transitions or write-after-read hazards disappear.

## Shader And Material Requirements

Material and shader metadata must grow from "what descriptors does this shader
bind?" to "what resource references can this kernel load from GPU data?"

Required metadata:

- material layout hash
- shading kernel ID
- texture slot semantics
- sampler policy
- required vertex attributes
- resource residency requirements
- whether a texture is sampled with implicit derivatives, explicit gradients,
  or compute-compatible manual LOD
- fallback behavior when the heap backend is absent

Shader variants should support three binding modes:

| Mode | Purpose |
| --- | --- |
| Legacy set/binding mapped to heap | Compatibility with current SPIR-V. |
| Descriptor indexing fallback | Vulkan fallback when descriptor heap is unavailable. |
| Native descriptor heap shader | Long-term path using heap-native shader interface. |

Runtime shader parsing, material layout synthesis, and descriptor payload
allocation must not occur in the per-frame hot path.

## Diagnostics

Every profile capture should be able to answer:

- active descriptor backend: descriptor heap, descriptor indexing, OpenGL
  bindless, texture array, coarse fallback, or unsupported
- why that backend was selected
- descriptor heap size, high-water marks, and allocation failures
- descriptor writes per frame by type
- heap bind count per command buffer
- push-data byte count and push count
- material rows updated and bytes uploaded
- resource generation mismatch count
- fallback descriptor usage count
- descriptor indexing fallback count
- CPU descriptor set binds still executed under heap-active mode
- runtime validation failures by pass/material/shader

For Deferred+ and RVC specifically, captures should include:

- material/shadelet bins by kernel/layout
- descriptor backend used by material resolve and native shading
- heap-backed material texture count
- fallback pixel count for missing heap support
- view count and per-view resource table generation for RVC
- shared shadelet cache hit/miss counts with resource generation reasons

## Validation Strategy

Source and unit tests:

- descriptor heap capability and fallback selection
- material row packing of resource/sampler heap indices
- shader prewarm keys include descriptor backend
- no descriptor-set bind path is used when descriptor heap is required
- descriptor indexing fallback produces the same material row semantics
- RVC shadelet keys do not contain backend descriptor-set handles
- Deferred+ material-region kernels group by layout/kernel, not material
  descriptor objects

Runtime validation:

- material-diverse opaque scene under descriptor heap and descriptor indexing
  fallback
- missing texture/default descriptor scene
- storage-image compute pass
- ImGui texture registration/unregistration
- clustered lighting with shadow/cookie/probe resources
- Deferred+ compatibility resolve once implemented
- RVC quad-view or simulator lane once implemented
- ray-query smoke path once TLAS descriptors are heap-backed

GPU captures:

- heap-backed material table rows
- resource and sampler heap high-water regions
- Deferred+ material resolve descriptors
- RVC shadelet records and per-view resolve resources
- ray tracing scene table and TLAS descriptor

## Rollout

1. Keep the descriptor heap backend active when supported, with descriptor
   indexing fallback.
2. Stabilize material/mesh/compute/ImGui runtime validation under heap-backed
   legacy shader mapping.
3. Add stable renderer-facing `ResourceBindingRef` values and make material
   rows store resource/sampler heap indices.
4. Promote descriptor heap to the top Vulkan rung in the material table and
   texture binding ladder.
5. Update Deferred+ compatibility resolve to require heap-ready material rows
   on Vulkan, with descriptor indexing fallback.
6. Update RVC shadelet/material cache contracts to use stable heap-backed
   resource generations.
7. Add acceleration-structure descriptor support before Vulkan ray tracing
   material-hit or ReSTIR paths.
8. Add native descriptor heap shader variants after legacy mapping is stable.
9. Evaluate shader objects and device-generated commands on top of the same
   heap/resource-table contract.

## Risks

- Driver availability may lag the rest of Vulkan 1.3/1.4 support. Keep
  descriptor indexing fallback well tested.
- Host-visible heap memory is simple but may not be ideal for all descriptor
  traffic. Staged/non-host-visible placement needs validation.
- Push-data compatibility can accidentally hide CPU per-draw binding costs.
  GPU-driven paths must load heap indices from GPU data.
- Resource generation bugs can cause stale texture or buffer reads. Use
  generation checks and visible fallback descriptors.
- Native descriptor heap shaders will need shader-toolchain work and should not
  block the compatibility path.
- RenderDoc/debug tooling support should be verified before removing descriptor
  indexing diagnostics.

## Recommendation

Adopt descriptor heap as the preferred Vulkan resource binding backend, but keep
the engine-facing architecture backend-neutral. Material rows, pass resource
tables, draw metadata, shadelet records, and ray tracing scene tables should all
store stable resource references that can map to descriptor heap, descriptor
indexing, OpenGL bindless handles, or a coarse fallback.

Use heap push-index mapping to finish the migration safely, then move the
performance-critical render paths toward GPU-loaded heap indices. That is the
step that lets Deferred+, RVC, meshlet rendering, ReSTIR, and post-process
graphs reduce CPU binding work without pretending one descriptor extension can
merge incompatible render states into a single draw.
