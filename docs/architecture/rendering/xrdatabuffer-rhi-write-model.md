# XRDataBuffer RHI Write Model

Last updated: 2026-06-16

`XRDataBuffer` is the engine-facing identity for GPU buffer data. The RHI write
model gives callers a backend-neutral way to allocate, write, commit, upload,
bind, diagnose, and read back buffers without asking every feature to know
OpenGL map flags, Vulkan staging rules, dirty-range upload details, or descriptor
readiness edge cases.

The caller contract is documented in
[XRDataBuffer Write Model](../../developer-guides/rendering/xrdatabuffer-write-model.md).
This architecture note describes the runtime invariants that OpenGL, Vulkan,
render submission, UI, physics, compute, and GI code must preserve.

## Goals

Normal engine code should treat a writable buffer as:

1. Choose an engine-level memory intent.
2. Allocate a typed or raw scoped write region.
3. Fill the returned span or byte span.
4. Commit or cancel.
5. Let the backend route the data through the correct upload, ring, mapped, or
   readback path.

The RHI still exposes real hardware distinctions:

- CPU pointers are valid only for host-visible, mapped, upload, readback, or
  diagnostic memory.
- GPU device addresses are shader/command-visible addresses, not CPU pointers.
- Device-local memory remains the preferred destination for static and heavy
  shader-read data.
- Persistent/coherent mapping does not remove overwrite hazards; frame slots,
  fences, or timelines still own reuse.
- Unsupported accelerated routes must produce visible diagnostics instead of
  silently becoming unrelated CPU fallback behavior.

## Source Map

The shared contract lives under:

- `XREngine.Runtime.Rendering/Buffers/XRDataBuffer.cs`
- `XREngine.Runtime.Rendering/Buffers/XRDataBufferView.cs`
- `XREngine.Runtime.Rendering/Buffers/XRBufferWriter.cs`
- `XREngine.Runtime.Rendering/Buffers/XRBufferWriteOptions.cs`
- `XREngine.Runtime.Rendering/Buffers/XRBufferReadbackTicket.cs`
- `XREngine.Runtime.Rendering/Buffers/XRBufferCpuUploadAllocator.cs`
- `XREngine.Runtime.Rendering/Buffers/XRBufferPersistentRingAllocator.cs`
- `XREngine.Runtime.Rendering/Buffers/XRBufferWriteTelemetry.cs`
- `XREngine.Runtime.Rendering/Buffers/IApiDataBuffer.cs`

Backend implementations report the same state categories through:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Buffers/GLDataBuffer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanStagingManager.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanDynamicUniformRingBuffer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanSceneDatabaseAddresses.cs`

The migration audit remains useful for call-site ownership and policy
classification:
[XRDataBuffer RHI Write Model Audit](../../work/audit/xrdatabuffer-rhi-write-model-audit.md).

## Buffer Identity

`XRDataBuffer` remains the stable resource identity accepted by materials,
programs, mesh attributes, descriptors, debug tools, serialization, and backend
wrappers. Backend resource recreation must not force callers to replace that
engine object.

`XRDataBuffer<T>` is a typed facade over the same identity when the element type
is known at construction time. It derives from `XRDataBuffer`, so existing APIs
can continue taking the base type while typed callers get element counts,
stride, padding, layout defaults, and `Span<T>` writers.

Typed buffers are preferred for runtime-only records such as indirect commands,
UI streams, palette rows, particle records, physics structs, and GI setup data.
Serialized mesh and cooked asset buffers may intentionally remain base
`XRDataBuffer` instances when the serialized payload and layout metadata are the
source of truth.

## Memory Policy

`XRBufferMemoryPolicy` is the engine-level statement of intent. It is reconciled
with legacy `EBufferUsage` so callers do not have two unrelated knobs for the
same allocation decision.

| Policy | Intended use | Backend route examples |
| --- | --- | --- |
| `GpuOnly` | Static or heavy shader-read storage. | Vulkan device-local final buffer with staging upload; OpenGL ordinary buffer with upload diagnostics. |
| `CpuToGpuUpload` | CPU-produced data uploaded into GPU storage. | Shared CPU upload allocator, Vulkan staging manager, or OpenGL queued/compatibility upload. |
| `CpuToGpuDynamic` | Small or frequent CPU-to-GPU updates. | Host-visible or mapped dynamic route when cheaper than staging. |
| `CpuToGpuPersistentRing` | Per-frame dynamic data with overwrite hazards. | Fence-protected persistent ring slots with backend-owned mapped storage and binding offsets. |
| `GpuToCpuReadback` | GPU-written data intentionally consumed by CPU. | Readback ticket backed by host-cached/readback storage and explicit completion. |
| `CpuGpuSharedDiagnostic` | Tooling, bring-up, and visible slow paths. | Shared-memory diagnostic route with counters and warnings. |

The resolved backend route must be observable in diagnostics. OpenGL may report
persistent-mapped, queued upload, or compatibility push routes. Vulkan may
report host-visible, device-local/staging, persistent-ring-capable, and
device-address routes.

## Write Scopes

`XRBufferWriter<T>` is the normal write surface for typed data. Writers are
stack-only `ref struct`s so hot paths avoid heap allocation and interface
dispatch. A writer exposes a span, tracks dirty ranges, and owns commit or
cancel semantics for that write.

`Dispose()` auto-commits by default when the writer was not explicitly committed
or cancelled. Risky paths can request explicit commit behavior through
`XRBufferWriteOptions`.

Writers own:

- Capacity checks and optional geometric growth.
- Backend route selection.
- Dirty range tracking.
- Commit, cancel, double-use, and dispose diagnostics.
- Flush or invalidate behavior when the chosen route needs it.
- Upload queue or staging-copy submission.
- Fence/timeline slot ownership for ring routes.
- Optional CPU mirror updates.

Raw-byte writers exist for interop payloads. Direct CPU-mirror writers that
already own a pointer should call `CommitDirtyElements` or `CommitDirtyBytes`
rather than manually pushing data, so revisions, readiness, dirty ranges, and
telemetry stay consistent.

## Dirty Ranges And Revisions

Committed writes increment the buffer revision and record dirty byte ranges.
Backends advance the uploaded revision when committed data is resident and ready
for GPU use.

Dirty tracking supports:

- Full discard or rewrite.
- Tail append.
- Single contiguous range.
- Multiple scattered ranges merged by threshold.
- Collapse to full upload when range count or byte coverage makes merging more
  expensive than re-upload.
- No-op work when content or revision checks prove the source data did not
  change.

Static clean frames should perform no CPU copy and no GPU upload work. Tail-only
writes should upload only the tail unless the range thresholds intentionally
collapse the operation.

## Backend-Neutral State

Callers and diagnostics query buffer state without knowing the renderer backend.
The state snapshot includes:

- API object generation state.
- Allocation state and allocated byte size.
- Current revision and uploaded revision.
- Pending upload or copy state.
- Requested memory policy and resolved backend route.
- Persistent mapping state.
- CPU mirror presence.
- Descriptor or binding readiness.
- GPU-use readiness.
- Device-address availability and downgrade reason.
- Dirty range count.

`IsGenerated` means only that the backend API object exists. It must not be
reinterpreted as uploaded, descriptor-ready, or safe for GPU use.

## Upload Allocation

`IXRBufferUploadAllocator` is the backend-neutral upload allocation contract.
`XRBufferCpuUploadAllocator.Shared` provides pooled CPU upload memory for paths
without a native backend allocator.

Vulkan device-local writes should continue through `VulkanStagingManager` where
that route is available. OpenGL should report queued or compatibility upload
routes honestly rather than pretending it has Vulkan-style staging.

`DisposeOnPush` behavior maps to writer completion and staging lifetime, not
merely to enqueue time. Deferral, cancellation, downgrade, and skipped uploads
must be visible in diagnostics.

## Persistent Rings

`XRBufferPersistentRingAllocator` is the shared ring-slot guard for dynamic
CPU-to-GPU data. Backend code owns the actual GL or Vulkan mapped storage, but
the generic allocator owns stable slot accounting:

- At least three frames or slots in flight.
- Slot index, byte offset, byte count, alignment, and backing-buffer identity.
- Explicit slot reuse guards through `XRGpuFence` or backend fences/timelines.
- Coherent and explicit-flush variants.
- Alignment for uniform buffers, storage buffers, non-coherent atom size,
  indirect commands, vertex buffers, and index buffers.
- Overrun and fallback diagnostics.

Consumers that bind descriptor offsets, device addresses, or draw ranges must
bind the same slot that the writer committed for that frame. Debug validation
should catch mismatched slots because those failures usually present as stale
one-frame or multi-frame data.

## Readback

GPU-to-CPU reads use `XRBufferReadbackTicket`. A ticket owns the copy into
readback storage, completion state, non-coherent invalidation, temporary staging
lifetime, and readback counters.

Production GPU submission strategies reject accidental readback by default.
Readback is allowed only when the buffer policy allows it or the caller chooses
an explicit diagnostic path. `GpuIndirectZeroReadback` and meshlet zero-readback
steady-state frames should report zero readback bytes.

## Descriptor And Device Address Integration

Writer commits and writer-driven growth must update descriptor and binding
readiness. Backend resource recreation invalidates descriptor caches safely while
preserving the stable engine-facing `XRDataBuffer` identity.

Vulkan consumers may use `TryGetGpuAddress` for buffer-device-address paths.
The query reports both success and downgrade reason. OpenGL consumers remain on
SSBO, bindless, or classic binding paths and must not receive fake device
addresses.

## Threading Contract

Writes can originate from update-thread producers, render-thread producers, and
async jobs. Each policy must keep a clear boundary:

- `BeginWrite`, `Commit`, and `Cancel` must state whether they run inline on the
  caller thread or enqueue work for the render thread.
- GPU work that needs command recording or descriptor mutation belongs on the
  render thread or in an explicit render-thread queue.
- `ref struct` writers cannot cross `await`, be captured by closures, or live on
  heap objects.
- One-shot writes must survive the update, collect-visible, and render loop
  handoff instead of being dropped between buffer swaps.

If an async producer cannot use a stack-only writer, it needs an explicit
pooled, heap-safe producer path rather than capturing the writer.

## CPU Mirrors And Serialization

CPU mirrors are owned intentionally:

- Keep them for serialized asset buffers, cooked binary restore, editor
  inspection, diagnostics, and CPU-side dedupe or rebuild helpers.
- Avoid permanent mirrors for static device-local render data once the upload
  path owns staging and diagnostics.
- Do not create a CPU mirror only to inspect GPU-written production data.

Typed buffers are runtime facades. Serialized assets continue to store the base
buffer payload and layout metadata unless a specific serializer discriminator is
introduced deliberately.

## Telemetry

`XRBufferWriteTelemetry` integrates with `RenderWorkBudgetCoordinator` speed
profile summaries. Counters cover:

- Upload bytes by route.
- Compatibility push bytes.
- Staging allocations and reuse.
- Persistent ring allocation, exhaustion, and fence waits.
- Host-visible writes.
- Host-cached readbacks.
- Device-address consumers and descriptor fallbacks.
- Readback bytes and diagnostic shared bytes.
- Zero-readback violations.

When `RenderDiagnosticsFlags.UploadStageLogging` or
`RenderDiagnosticsFlags.PushSubDataTrace` is enabled, writer commits should log
buffer name, policy, route, bytes, dirty range count, allocated bytes, uploaded
revision, current revision, frame slot when available, readiness, and pending
state. Upload-stage logging is explicit opt-in through `XRE_UPLOAD_STAGE_LOGGING`
or the editor preference; attaching a debugger alone must not enable it.

## Current Migration State

The core implementation and representative migrations have landed. Migrated
areas include UI batching, legacy UI text buffers, web-view PBO byte buffers,
particle compute buffers, global skin palette packing, dirty skinning uploads,
blendshape weights and active lists, physics chain upload/readback pools,
softbody compute uploads, GPUScene swap dirty commits, view-set buffers, and
Surfel GI transform atlas uploads.

Compatibility `PushData`, `PushSubData`, and map APIs remain available for
backend internals, serialized asset restore, diagnostics, and unmigrated legacy
paths. New runtime code should prefer scoped writers or direct dirty commits.

Remaining hardware, barrier, and strategy validation is tracked in
[XRDataBuffer RHI Write Model Validation](../../work/testing/xrdatabuffer-rhi-write-model-validation.md).
