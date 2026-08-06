# Vulkan Hot-Data Layout Inventory

Date: 2026-08-06  
Status: Phase 4 baseline  
Owner: Rendering

This inventory records the identifiable per-frame and per-draw streams feeding
the Vulkan render loop before Phase 4 layout migration. Sizes are exact where a
contract declares them and estimates where the managed runtime controls layout.
Estimated sizes must be replaced by ABI assertions or a representative layout
benchmark before changing the stream.

## GPUScene And Indirect Streams

| Stream | Current layout and size | Producer, consumers, fields touched | Copies, mutation, owner |
|---|---|---|---|
| `GPUIndirectRenderCommand` source/publication buffers | Sequential AoS, 80 bytes (20 32-bit lanes), no managed references | `GPUScene.ConvertToGPUCommand` and scene updates publish it; culling/conversion reads sphere, mesh/material/pass, instance, layer/flags, transform/skin/identity, and distance lanes | Dirty on renderable add/remove/update; double-buffered by `GPUScene`. The hot-command compatibility pass copies/converts another 80 bytes per element before culling. |
| `DrawMetadata` | Sequential AoS, 64 bytes (16 `uint` lanes), no managed references | `WriteDrawMetadata` publishes it; Hi-Z/BVH/material/meshlet stages read draw ID, instance/layer/flags, bounds ID, mesh/material/state, transform, skin, and identity subsets | Dirty-range publication owned by the GPUScene generation. |
| `BoundsGpu` | AoS, 64 bytes: three `Vector4` records plus version/padding | `WriteBounds` publishes it; culling reads sphere data and BVH conversion reads AABB min/max | Dirty on bounds changes; GPUScene generation owner. AABB conversion copies the selected bounds into its output. |
| `TransformGpu` | AoS matrix, 64 bytes, no managed references | Transform publication feeds meshlet, skinning, and draw consumers | Dirty on transform changes; GPUScene ID allocator/publication owner. Current and previous transforms are not yet separate stage-native streams. |
| `MaterialStateGpu` | AoS, 32 bytes (eight `uint` lanes) | Material-state classification publishes it; material/pass/bindless stages consume selected state lanes | Dirty on material or pass changes; GPUScene owner. |
| `GPUTransparencyMetadata` | AoS, 16 bytes | `FromMaterial` publishes transparency domain/classification; transparent sorting/classification consumes it | Dirty with material/command changes; GPUScene owner. |
| `GPULodTransitionState` | AoS, 16 bytes | LOD-transition publication feeds LOD selection and transition stages | Conditional mutation; GPUScene owner. |
| Tight AABB/BVH, mesh/material ID, atlas/meshlet, and skin-palette buffers | Native GPU records; exact ABI depends on the declared record | GPUScene residency/update stages feed BVH, culling, meshlet expansion, and rendering | Scene/revision and dirty-range driven. Each changed record requires an ABI assertion and measured bytes-touched entry before permanent relayout. |
| `GPUIndirectRenderCommandHot` compatibility stream | AoS, 80 bytes, no managed references | `BuildSourceHotCommandBuffer` converts the broad source before active culling/batch/meshlet consumers | Unconditional per relevant cull invocation. This is a temporary compatibility envelope, not the target canonical layout; meter conversion elements/bytes/time until deleted. |
| Legacy BVH culled broad-command output | Shader AoS, 80 bytes | `bvh_frustum_cull.comp` emits sphere and metadata in parallel with hot output | Per cull. The broad and hot outputs are duplicate representations until consumer/binding audit permits deletion. |
| Final Vulkan indirect arguments | Driver ABI AoS; `VkDrawIndirectCommand` is 16 bytes and `VkDrawIndexedIndirectCommand` is 20 bytes, subject to Silk.NET ABI assertion | Culling/classification/indirect-build output is consumed directly by Vulkan indirect draw commands | GPU-written per active range. This stays contiguous AoS at submission. |

The removed `GPURenderExtractSoA.comp` and `SoACull` path were not part of the
canonical inventory: the extraction had no real consumer, and `SoACull` bound
the ordinary metadata/bounds buffers rather than the extracted streams. Phase 4
deletes that conversion and its scratch sphere/control/index resources before
selecting replacement stage-native streams.

## CPU Planning And Packet Streams

| Stream | Current layout and managed state | Producer and consumer | Copies, mutation, owner |
|---|---|---|---|
| `FrameOp` ingress | Polymorphic managed objects. Base state contains an optional framebuffer reference, context, and resource-use list; concrete operations add managed payloads | Frame-operation enqueue feeds `FramePlanBuilder`, graph planning, scheduling, then primary/secondary recording | Per-frame thread-local `List<T>` pools. The plan lease protects reuse, but planning still chases objects. Must lower exactly once to opcode/index plus dense per-kind arenas before sorting. |
| `FramePlan` streams | Reference arrays for static/overlay `FrameOp`, plus output requests, DAG nodes, and operation keys | Builder slot publishes to command planning/recording | Plan construction and logical-view helpers clone/slice arrays. Owner is a builder slot and generation lease. Target is immutable numeric headers/ranges. |
| `RenderPacket` | Managed class with hot `string TargetName`, optional owned draw/dispatch arrays, snapshots, and keys | Packet lowering feeds scheduling/reuse and recorders | Pooled/leased, but legacy reset paths materialize per-packet arrays. Replace with numeric IDs and start/count ranges into frame arenas; names move to a cold sidecar. |
| `DrawPacket` / `DispatchPacket` | Compact managed structs without references; estimated near 48 / 40 bytes pending runtime layout assertion | Frame-op packetization feeds packet recorders | Per-frame arrays owned by packet/planning publication. These become dense per-kind payload streams. |

## Prepared Draw And Descriptor Streams

| Stream | Current layout and managed state | Producer and consumer | Copies, mutation, owner |
|---|---|---|---|
| `VkPreparedMeshDraw` | Large AoS containing managed `Viewport[]`, `Rect2D[]`, diagnostic string, and `VulkanPreparedMeshDrawState` references | `PendingMeshDraw` snapshot feeds worker/mesh encoding | Per prepared draw. Creation rents and copies indexed viewport/scissor arrays; release returns them. Replace with a compact hot header and typed frame-slot ranges. |
| `VulkanPreparedMeshDrawState` | Large AoS with owner/renderer/program references and seven variable arrays | Mesh renderer preparation feeds the encoder's descriptor, offset, vertex, primitive, payload, and push-constant reads | Per draw/frame-data generation. Flatten arrays into frame-slot side streams; keep generation audit, managed owners, and names in cold indexed sidecars. |
| Descriptor binding snapshot | Compact AoS, estimated 24 bytes pending assertion | Descriptor mutation/planning feeds prepared draw and command recording | Generation/dirty driven. Current prepared bindings still carry arrays; target publication stores dirty/generation/resource/layout lanes in scan-friendly streams and materializes native writes only for dirty ranges. |
| Uniform fallback bytes | Rented `byte[]` sized from reflected uniform block | Reflected uniform writer feeds mapped/staged uniform upload | Rented and returned per relevant write. Move to typed frame-slot mapped allocation; retain reflection only outside the warm encoding path. |

Prepared-draw AoSoA is not selected by this baseline. Encoding consumes most hot
header fields together, so compact AoS plus flattened side streams is the
default candidate. AoSoA requires a representative end-to-end win including
transpose, publication, tail, and worker merge costs.

## Render Graph, Barrier, And Worker Streams

| Stream | Current layout and managed state | Producer and consumer | Copies, mutation, owner |
|---|---|---|---|
| Compiled render graph | Immutable managed object graph and read-only collections | Graph compiler publishes to command planning/recording | Structural-generation driven. Replace execution representation with typed numeric resource IDs and flat offset/count adjacency. |
| Planned barriers | `List<Planned*>`, resource/pass string dictionaries, and lists per pass; captured into three arrays and read-only wrappers | Barrier planner feeds barrier emission and final native `Vk*Barrier2` construction | Rebuilt with graph generation. Strings and reference-rich grouping are cold-authoring data; execution becomes numeric flat ranges, with contiguous ABI AoS only at the native call. |
| Command-chain worker batch | `CommandChain[]`, `CommandBuffer[]`, three `int[]`; worker state includes event/thread/arena/batch references | Scheduler publishes jobs; workers record secondary buffers; coordinator merges | Reused and geometrically resized. Queue entries should remain compact AoS; mutable counters/trace rings move to independently aligned worker-owned blocks and merge after completion. |

## Upload, Mapped, And Native Scratch Streams

| Stream | Current layout and managed state | Producer and consumer | Copies, mutation, owner |
|---|---|---|---|
| `AdvancedFrameSlotUploadArena` | Byte-backed frame-slot storage; allocation is `Memory<byte>` plus stream/generation/slot/offset/count | Advanced producers publish upload ranges; backend copy plans feed mappings | Explicit frame begin/end and completion-owned storage generations. This is a useful typed-slice precedent, but not the focused Vulkan mapped/native authority. |
| `VulkanStagingManager` | Managed list of object entries containing native buffer/memory handles, size, and state | Upload/readback callers acquire a whole staging entry and record transfer work | Per upload with frame trimming. Replace whole-entry hot use with typed offset/length/alignment/generation slices where suballocation is valid. |
| Native barrier/descriptor/submit scratch | Scattered arrays, pooled buffers, and `stackalloc` at call sites | Planning/publication prepares ABI arrays for Vulkan commands | Consolidate into `VulkanNativeScratchArena`; validate capacity/alignment/generation and expose raw pointers only inside final native-call scope. |
| Mapped uniform/upload/staging/readback bytes | Multiple owner-specific paths | CPU writers feed mapped memory and GPU consumers; readback reverses ownership | Consolidate into frame-indexed `VulkanMappedFrameArena` slices. Each slice records host/device ownership, offset, length, alignment, generation, and noncoherent flush/invalidate expansion. |

## Required Measurements And Layout Decisions

Every changed stream receives a decision record comparing current AoS against
SoA, compact AoS/hot-cold, and AoSoA only when relevant. Record representative
element counts, producer/consumer fields, bytes read/written, conversions,
allocation, publication time, worker/encoder time, whole-frame p50/p95/p99/worst,
and the owner/generation transition. ABI assertions remain mandatory for
`DrawMetadata`, `BoundsGpu`, `GPUIndirectRenderCommandHot`, shader records, and
native Vulkan structs.

Allocation-free telemetry must report at least stream elements/bytes,
compatibility conversion bytes, native scratch reservations/high-water marks,
mapped reservations and flush/invalidate expansion, dirty descriptor ranges,
graph edges, prepared-draw side-stream bytes, worker queue depth, and worker
merge cost. Diagnostic names are resolved only by cold exporters.

## Primary Source Map

- `XREngine.Runtime.Rendering/Commands/GPUIndirectRenderCommand.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/Workers/`
- `XREngine.Runtime.Rendering/Buffers/Advanced/`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanStagingManager.cs`
- `Build/CommonAssets/Shaders/Compute/Culling/`
- `Build/CommonAssets/Shaders/Scene3D/RenderPipeline/`
