# Vulkan Hot-Data Layout Inventory And Decision Record

Date: 2026-08-12
Status: Phase 4.5 and Vulkan core-hardening Phase 4 complete
Owner: Rendering

This is the reproducible Phase 4.5 inventory for every changed per-frame and
per-draw stream feeding the Vulkan render loop. It replaces the pre-migration
baseline. Exact managed sizes were measured with `Unsafe.SizeOf<T>()`, managed
reference content with `RuntimeHelpers.IsReferenceOrContainsReferences<T>()`,
and Vulkan ABI sizes through the Silk.NET types used by the backend. The local
measurement program lives in the ignored validation tree at
`Build/_AgentValidation/p45-capabilities/scratch/LayoutSizeAudit/`.

Representative layout counts are 64, 1,024, and 16,384 elements. Those counts
cover a small editor view, an ordinary scene, and a deliberately high draw or
operation count without presenting benchmark timings that were not measured.
The layout decision is structural: candidate byte traffic, consumer access,
copy count, ownership, and allocation behavior are known. Runtime timing and
tail-latency comparisons remain telemetry rather than invented constants.

## Decision Summary

| Domain | AoS candidate | SoA / hot-cold candidate | AoSoA candidate | Canonical selection |
|---|---|---|---|---|
| GPUScene | One historical 80-byte broad command plus separate metadata was easy to address but forced unrelated fields through every stage | Eight stage-native streams let culling, classification, material, transform, and visibility stages bind only their domains | Tiles would add transpose and tail handling without a CPU SIMD consumer | Stage/domain streams, with vector AoS records inside each stream |
| Final indirect arguments | The driver ABI is a complete 20-byte indexed command | Splitting fields would require submission-time reconstruction | No benefit because Vulkan consumes a native-stride array | Contiguous `DrawElementsIndirectCommand` / `VkDrawIndexedIndirectCommand` AoS |
| Frame operations | Polymorphic `FrameOp` objects made sorting and planning chase references | A 32-byte numeric header plus dense typed payload arrays lowers authoring objects once | Operation kinds have different payloads and branch behavior | Numeric header stream plus per-kind compact AoS payload streams |
| Prepared mesh draws | One reference-rich object with per-draw arrays pooled independently | A reference-free 304-byte encoder record plus typed `start/count` side streams and a cold indexed owner/audit sidecar | Encoder consumption is whole-record; tiling would add transpose, publication, and tail cost | Compact value AoS header/state plus flattened frame-owned side streams |
| Render graph and barriers | Strings, dictionaries, and lists-of-lists were useful authoring data but unsuitable execution data | Numeric IDs, flat records, and offset/count adjacency remove execution-time reference traversal | Graph traversal is irregular and does not present a stable vector lane | Flat numeric compact AoS records; native ABI AoS only at the call boundary |
| Descriptor publication | One 80-byte lifecycle slot record is efficient for coherent slot mutation/retirement | Dirty slot IDs and contiguous range columns isolate the publication scan; prepared bindings, generations, layouts, samplers, and frequency state are flattened | Publication is sparse and range-shaped, so tiles add no useful locality | Hybrid compact slot AoS plus column-oriented dirty/range streams and preallocated native scratch |
| Worker queues | A worker consumes one full job | Mutable counters and traces benefit from separate write ownership | Job tiling adds no benefit to dequeue/encode | 24-byte queue-entry AoS plus independently aligned worker-local blocks |
| Frame lifecycle | The transaction, typed outcomes, receipts, and dependency state are consumed coherently | Splitting would complicate settlement and ownership proof | No repeated field-wise bulk stage exists | Safe `VulkanFrameAttempt` AoS; no naked ownership pointers |
| Native and mapped memory | Scattered arrays, pools, pointers, and call-site mappings obscured lifetime | Focused arenas and pointer-free identity slices make generation/alignment visible | Not applicable | Typed arenas/leases; raw pointers exist only in the final bounded native scope |

AoSoA was rejected for all Phase 4.5 streams. None has a demonstrated
end-to-end advantage after transpose, publication, tail, and worker-merge cost.
It remains an optimization candidate only if the allocation and frame-time
telemetry first identifies a relevant bottleneck and a representative benchmark
then proves a whole-frame win.

## GPUScene And Indirect Streams

All eight logical streams are declared by `GPUSceneLayoutSchema` and published
by the single `GPUScene` storage/generation transaction. A logical stream is an
aligned typed range; it does not imply one wrapper, source file, Vulkan
allocation, or descriptor binding per scalar field.

| Stream | Element ABI | Managed refs / per-element arrays | Producer and consumers; fields touched | Copy, mutation, owner |
|---|---:|---|---|---|
| Cull control | `DrawMetadata`, 64 bytes, 16-byte aligned | None | Scene publication writes draw/instance/layer/flags, bounds, mesh/material/state, transform, skin, and identity lanes; cull and expansion stages read only their declared control subset | Dirty-range write; no broad-command conversion; GPUScene generation |
| Cull bounds | `BoundsGpu`, 64 bytes, 16-byte aligned | None | Bounds publication writes sphere and AABB vector groups; sphere/AABB consumers read the complete selected vector group | Dirty-range write; GPUScene generation |
| Classification/sort key | `GPUViewBatchClassification`, 32 bytes, 16-byte aligned | None | Classification stages write view/batch/material/pass/sort state; indirect batching consumes it | GPU-written/read per active range; GPUScene generation |
| Material/state | `MaterialStateGpu`, 32 bytes, 16-byte aligned | None | Material publication writes state lanes; material scatter, transparency, and pass consumers read selected state | Dirty-range write; GPUScene generation |
| Transform | `TransformGpu`, 64 bytes, 16-byte aligned | None | Transform publication writes the current matrix; meshlet, skinning, and draw consumers read it | Dirty-range write; GPUScene generation |
| Previous transform | `TransformGpu`, 64 bytes, 16-byte aligned | None | Transform publication writes the previous matrix; velocity/history consumers read it | Dirty-range write; GPUScene generation |
| Visibility | `uint`, 4 bytes, 4-byte aligned | None | Visibility/culling stages publish and consume the draw visibility lane | GPU-written/read per active range; GPUScene generation |
| Optional AABB | eight `float` lanes, 32 bytes, 16-byte aligned | None | Only tight-AABB/BVH consumers bind it | Optional dirty/GPU write; GPUScene generation |
| Final indexed indirect | `DrawElementsIndirectCommand`, 20 bytes; Silk `DrawIndexedIndirectCommand` also measured at 20 bytes | None | The final indirect-build stage writes index count, instance count, first index, vertex offset, and first instance; Vulkan consumes it directly | One final GPU write; no submission conversion; indirect generation |

The required GPUScene streams total 324 bytes per draw; including optional AABB
they total 356 bytes. This is not a claim that every stage reads 324 bytes: the
point of the layout is that each stage binds only its stream subset.

| Elements | Required streams | With optional AABB | Final 20-byte indirect |
|---:|---:|---:|---:|
| 64 | 20,736 bytes | 22,784 bytes | 1,280 bytes |
| 1,024 | 331,776 bytes | 364,544 bytes | 20,480 bytes |
| 16,384 | 5,308,416 bytes | 5,832,704 bytes | 327,680 bytes |

The deleted broad compatibility pass read and rewrote an unconditional 80-byte
record, or 160 bytes of conversion traffic per element: 10,240, 163,840, and
2,621,440 bytes at the representative counts. `CompatibilityConversionBytes`
now remains zero on the canonical path. The superseded 80-byte
`GPUIndirectRenderCommand`, its all-loaded buffer, and
`GPURenderBuildHotCommands.comp` were deleted. Draw IDs are unsigned throughout,
and final commands are generated only after culling/classification.

## CPU Operation And Packet Streams

| Stream | Element layout | Managed refs / per-element arrays | Producer and consumers | Copy, mutation, owner |
|---|---:|---|---|---|
| `FrameOperationHeader` | 32-byte value record | None | Ingress lowering writes opcode, payload/context/resource indices, target ID, order flags; sorting, DAG planning, scheduling, and recording read numeric state | Written once during seal; frame-plan generation |
| Per-kind operation payloads | Dense compact AoS arrays for draw, dispatch, copy, clear, barrier, query, output, upload, and related variants | No `FrameOp` references; variable data is range-indexed | `FrameOperationStream` appends the concrete payload selected at ingress; the matching encoder reads it by payload index | One authoring-to-stream copy; frame-plan generation |
| `VulkanPrimaryPlanNode` | 16-byte value record | None | Planner publishes opcode/operation indices and scheduler/recorder consumes them | One planning write; frame-plan generation |
| Packet/range records | Numeric IDs plus `start/count` ranges | No owned draw/dispatch arrays or hot diagnostic strings | Frame-plan packetization writes; primary/secondary scheduling consumes | Frame-owned arenas; names resolve from cold diagnostics only |

At the representative counts, the 32-byte header stream occupies 2,048,
32,768, and 524,288 bytes. `FramePlanBuilder` is the only sorting, dependency,
planning, and scheduling path. Rich `FrameOp` subclasses remain solely as the
authoring ingress and are cleared after the single lowering pass; compute
program readiness before sealing is not an alternate planner. The former
per-kind object sidecars, direct compute-dispatch sequence, legacy snapshots,
and packet-owned arrays were deleted.

## Prepared Draw And Descriptor Streams

`VkPreparedMeshDraw` measures 304 bytes and contains no managed references. Its
inlined `VulkanPreparedMeshDrawState` is 232 bytes and also reference-free. The
record is larger than a driver command because the mesh encoder consumes its
pipeline, primitive, viewport/scissor, range, frame-slot, push-constant, and
instance state together. Splitting that state would add another indexed fetch
without reducing publication bytes. It is therefore the compact encoder record
relative to the former object graph, not a claim that 304 bytes is intrinsically
small.

| Stream | Exact element size | Managed refs / per-element arrays | Producer and consumers | Copy, mutation, owner |
|---|---:|---|---|---|
| Prepared draw header/state | 304 / 232 bytes | None | Render-thread preparation writes once; worker encoder reads as a unit | One append; prepared-frame generation |
| Typed range | 8 bytes | None | Headers index all variable side streams by offset/count | Value copy; prepared-frame generation |
| Descriptor-set binding | 24 bytes | None | Preparation resolves set/slot and dynamic-offset range; encoder binds | One append per binding |
| Dynamic offsets | 4 bytes | None | Preparation writes; encoder consumes contiguous range | One append per offset |
| Descriptor image payload | 16 bytes | None | Captures descriptor-set handle and payload generation | One append per payload |
| Descriptor image requirement | 32 bytes | None | Captures resource generation, mip/layer/aspect/layout; synchronization validates | One append per requirement |
| Frame-data payload handle | 216 bytes | None | Preparation captures pointer-free arena identity/range/generation; encoder resolves through owner sidecar | One append per payload |
| Vertex buffer / offset | 8 / 8 bytes | None | Preparation writes native buffer and offset streams; encoder binds | One append per binding |
| Viewport / scissor | 24 / 16 bytes | None | Indexed variants append only when count exceeds one; encoder reads the range | One append per indexed element |
| Cold draw sidecar | Indexed managed owner, generation audit, diagnostic name | Managed references intentionally cold; no pooled array per draw | Read for lifetime validation or diagnostics, not the ordinary encoding loop | One owner entry per prepared draw; prepared-frame generation |

The 304-byte header stream occupies 19,456, 311,296, and 4,980,736 bytes at the
representative counts. Every variable stream is frame-recording-owned and
geometrically preallocated; no array or pooled buffer is owned per draw. Appends
perform one exact value copy into the destination stream. The removed primary
reuse wrapper and `VulkanPersistentArrayPool` have no remaining consumers.

Descriptor publication uses two access-shaped representations:

- the 80-byte `MaterialTextureDescriptorSlot` compact AoS record retains the
  resource reference, native image/sampler handles, expected layout,
  image-view/sampler/slot generations, last-used and retirement cadence, and
  dirty/retirement flags because allocation, refresh, and retirement mutate the
  slot coherently;
- `VulkanBindlessDescriptorPublicationStream` stores preallocated dirty slot
  IDs and contiguous range starts/counts as scan columns, sorts without
  allocation, gathers only dirty 24-byte `DescriptorImageInfo` records, and
  emits only the required 64-byte `WriteDescriptorSet` records;
- prepared descriptor and uniform publication streams separately retain
  resource/layout requirements and `EVulkanBindingFrequency` generations, so
  frame/view/pass/material/object/instance/runtime-callback cadence is numeric
  and does not require reflection in the encoder.

This hybrid was selected over full scalar SoA because coherent slot mutation
touches most of the 80-byte record, while the bulk publication scan needs only
the isolated dirty/range columns. It removes per-flush `ArrayPool` rentals and
one-write-per-slot expansion while preserving stable slot IDs and generations.

## Render Graph, Barriers, Workers, And Lifecycle

| Stream | Exact element size | Managed refs | Producer and consumers | Ownership / materialization |
|---|---:|---|---|---|
| Compiled pass | 20 bytes | None | Cold graph compiler writes; plan/record traversal reads | Structural graph generation |
| Resource use | 48 bytes | None | Compiler assigns numeric `VulkanResourceId`; dependency/barrier planning reads | Flat pass offset/count range |
| Edge | 56 bytes | None | Compiler writes producer/consumer numeric state; scheduler reads | One array per graph generation |
| Submission | 28 bytes | None | Compiler writes pass/wait offsets and queue/signal IDs; submit planning reads | Flat pass/wait adjacency |
| Barrier pass range | 12 bytes | None | Barrier compiler writes offsets/counts; emitter reads | Barrier-plan generation |
| Frozen image / buffer / swapchain barrier | 80 / 56 / 52 bytes | None | Barrier compiler writes normalized records; emitter materializes ABI records | Flat numeric arrays |
| Native memory / buffer / image barrier | 48 / 80 / 96 bytes | None | Final emitter writes Silk ABI AoS; `vkCmdPipelineBarrier2` consumes immediately | `VulkanNativeScratchArena<T>` reservation |
| Worker queue entry | 24 bytes | None | Scheduler writes prepared/cold indices, command buffer, worker, flags; worker consumes complete entry | Frame-owned queue |
| Worker-local blocks | Independently base/stride aligned | No per-item managed owner | Each worker writes only its counters/traces; coordinator merges after completion | Worker generation |
| `VulkanFrameAttempt` | 1,184-byte safe AoS | Yes, by design | Frame loop owns transaction state, typed outcomes, receipts, and settlement proof | One lifecycle owner; no native ownership pointers |

The 24-byte worker queue occupies 1,536, 24,576, and 393,216 bytes at the
representative counts. Mutable worker data no longer uses per-item global
atomics. Graph names, dictionaries, and rich authoring collections end at the
cold compiler; the published execution object retains numeric arrays only.
Barrier ABI arrays exist only for the native-call reservation lifetime.

## Native, Mapped, Decoder, Pool, And Pin Boundaries

`VulkanNativeScratchArena<T>` owns one aligned unmanaged allocation, validates
capacity, power-of-two alignment, reservation generation, single active writer,
and owning thread, and exposes a `Span<T>` whose raw address is consumed only by
the immediate native call. Submit, graph barrier, format query, and other
variable hot ABI arrays use focused reusable scratch. Fixed small bootstrap
`pNext` chains remain local purpose-specific native scopes because they do not
cross a call, loop, frame, worker, or owner boundary.

Mapped paths use pointer-free `VulkanMappedMemorySlice` or frame-arena slices
containing buffer/memory identity, allocation offset/size, requested
offset/length, required alignment, device/arena identity, allocation generation,
coherency, and host visibility. Short-lived read/write leases acquire the native
address, validate host/device ownership and `minMemoryMapAlignment`, and expand
noncoherent flush/invalidate ranges to `nonCoherentAtomSize`. Coherent owners do
not report fictitious expansion. Upload, uniform, staging, readback, and output
callback mappings contribute to the same reservation/failure/visibility
telemetry.

Validated cooked-binary readers/writers are span-based `ref struct` owners with
bounds checks. Screenshot pixels crossing into asynchronous CPU work use the
single-owner `VulkanPooledReadbackBytes`; disposal atomically returns the exact
rental on copy failure, scheduling failure, callback failure, or ordinary
completion. Other `ArrayPool<T>` sites retain a visible rent/return owner and no
raw rental crosses a task. The sole Vulkan `GCHandle.Alloc` is the non-pinned,
device-lifetime ImGui platform-window callback owner; no per-frame pin survives
a native call.

The strict local Roslyn boundary audit reports:

```text
SUMMARY stackalloc_in_loop=0 pointer_loops=0 unsafe_ordinary_owners=0
```

Type-wide `unsafe` was removed from the renderer facade and ordinary planning,
graph, scheduling, frame-loop, resource, and output authorities. The remaining
type-wide unsafe owners are focused native/mapped mechanisms. Unmanaged generic
copies require `unmanaged` constraints and validated lengths; no unchecked
bitwise copy of a padded or reference-containing record is an execution path.

## Allocation-Free Telemetry

| Domain | Published counters |
|---|---|
| GPUScene | Per-stream elements and bytes read/written, stream generation, compatibility conversion bytes |
| Prepared frame | Elements, bytes, and high-water bytes across flattened streams |
| Descriptors | Slots scanned/dirty, contiguous ranges, native bytes, compatibility/publication time, high-water slots |
| Graph/native scratch | Graph edge count, native reservations, requested bytes, high-water bytes |
| Mapped memory | Reservations, reserved bytes, flush/invalidate expansion bytes, failures, frame-arena reservation/mapped-byte high water |
| Workers | Queue depth/bytes and high water, worker-local and execution merge bytes/time |
| CPU stages | Per-stage allocation bytes, high water, boundary allocation bytes, and boundary high water |

Counters are preallocated scalar fields updated with bounded arithmetic or
atomics and snapshotted at the existing frame telemetry boundary. Diagnostic
names are resolved by cold exporters only.

## Superseded Data And Helpers Deleted

- broad `GPUIndirectRenderCommand`, `AllLoadedCommandsBuffer`, and
  `GPURenderBuildHotCommands.comp` conversion path;
- dead extraction/`SoACull` compatibility resources;
- per-kind `FrameOp` object sidecars, object planner/scheduler compatibility,
  packet-owned draw/dispatch arrays, and hot target strings;
- prepared primary reuse wrapper, per-draw pooled arrays, and
  `VulkanPersistentArrayPool`;
- pooled native barrier arrays and per-flush descriptor rentals;
- facade raw map/flush/invalidate/readback pointer entry points and unused raw
  mapping helpers.

## Phase 4.5 Closeout Evidence

The ignored validation root is
`Build/_AgentValidation/p45-capabilities/`; the isolated editor-session evidence
is under
`Build/_AgentValidation/mcp-sessions/vulkan-phase45-final-20260812/`.

- The exact layout program reproduced every size in this inventory, including
  the 64-byte GPUScene records, 20-byte final indexed indirect command, 32-byte
  operation header, 304-byte reference-free prepared draw, 24-byte worker job,
  and native Vulkan ABI records.
- The strict Roslyn boundary audit reported
  `stackalloc_in_loop=0`, `pointer_loops=0`, and
  `unsafe_ordinary_owners=0`. Searches also found no remaining broad GPUScene
  command/conversion path, prepared-primary reuse owner, persistent prepared
  array pool, raw facade mapping entry point, or type-wide unsafe renderer.
- Parser validation passed for all 17 changed shaders in their engine
  helper/include context. Warning-as-error builds of Runtime.Rendering,
  Runtime.Rendering.Vulkan, and the full editor each completed with zero
  warnings and zero errors.
- Named Vulkan session `vulkan-phase45-final-20260812` produced two visually
  inspected, camera-dependent Sponza views. During camera interpolation, 16
  profiler reads observed at most 48 numeric operations: 40 mesh operations,
  one compute operation, and two swapchain writes. Every instrumented CPU stage
  and boundary reported zero allocated bytes; structured validation and
  descriptor failure counts remained zero.
- With `XRE_VULKAN_FINAL_PRESENT_LEDGER=1`, the final-presentation ledger retained
  128 observations across swapchain descriptor slots 0, 1, and 2. Source
  view/sampler identities matched the written descriptor payloads, the ledger
  did not freeze, and it reported zero invariant failures.
- The diagnostic replay recorded the expected G-buffer, AO, lighting, bloom,
  final-postprocess, and FXAA render sequence, then sustained successful submits
  and presents. Its Vulkan/rendering logs contained no VUID, validation error,
  device-loss result, plan-precondition exception, descriptor-binding failure,
  mapped-memory failure, short-destination exception, or unhandled render
  exception.
- `rdc doctor` passed RenderDoc replay, command-line, and Vulkan-layer checks.
  A capture was not needed because the two camera views, internal target
  captures, descriptor ledger, structured telemetry, and category logs were
  conclusive.

Automated tests were neither added nor run during this integration, following
the repository rule that live feature validation completes before test work is
explicitly cleared.

## Primary Source Map

- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUSceneLayoutSchema.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/Workers/`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/VulkanNativeScratchArena.cs`
- `XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/`
- `XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan*.cs`
- `Build/CommonAssets/Shaders/Compute/Culling/`
- `Build/CommonAssets/Shaders/Compute/Indirect/`
