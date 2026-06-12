# XRDataBuffer RHI Write Model TODO

Last Updated: 2026-06-12
Status: Core implementation landed. The branch/merge tasks were intentionally
skipped for this pass because the request explicitly said "don't branch".

Implementation notes:

- Public write contract landed in `XRBufferMemoryPolicy`,
  `XRBufferWriteMode`, `XRBufferCpuAccess`, `XRBufferWriteOptions`,
  `XRBufferWriter<T>`, and `XRDataBuffer<T>`.
- `XRDataBuffer` now tracks write revisions, uploaded revisions, dirty ranges,
  backend route, pending upload state, CPU mirror presence, descriptor readiness,
  device-address downgrade reasons, and backend-neutral state snapshots.
- Scoped writers support typed spans, raw-byte writes, append, discard,
  scattered dirty ranges, explicit commit/cancel, auto-commit disposal, and
  explicit-commit-required disposal diagnostics.
- Direct CPU-mirror writers can use `CommitDirtyElements` or
  `CommitDirtyBytes` without reinterpreting existing layout metadata.
- OpenGL and Vulkan wrappers now report allocated/uploaded bytes, pending state,
  readiness, resolved route, persistent mapping, and device-address support.
- Vulkan static/device-local uploads continue through the existing staging
  manager. OpenGL continues to expose queued/compatibility push routes through
  diagnostics rather than a fake staging abstraction.
- Readbacks are represented by `XRBufferReadbackTicket`; production readback is
  rejected unless the buffer policy allows it or the caller requests an explicit
  diagnostic path.
- Representative migrations landed in UI batching, dirty skinning uploads,
  blendshape weight/active-list uploads, and Surfel GI transform atlas uploads.
- Developer-facing usage docs: `docs/developer-guides/rendering/xrdatabuffer-write-model.md`.
- Remaining hardware rollout work is specifically the generic shared persistent
  mapped ring and full backend readback-ticket plumbing. Existing Vulkan dynamic
  UBO rings and staging pools remain backend-specific implementations.

## Goal

Make `XRDataBuffer` feel like a normal writable engine buffer from caller code:
allocate a typed writable region, fill it, commit it, and use it for draw,
dispatch, upload, or readback without each caller manually choosing
`PushData`, `PushSubData`, mapping flags, staging buffers, dirty ranges, or
backend-specific readiness checks.

The RHI must still preserve the real hardware split:

- A CPU pointer is writable only when the allocation is host-visible or staging
  memory.
- A GPU device address is shader/command-visible, not a CPU pointer.
- Device-local VRAM remains the preferred storage for static and heavy
  shader-read data.
- Persistent/coherent mapping does not remove overwrite hazards; ring slots and
  fences/timelines are still required.

## Source Inventory

Current shared buffer surface:

- `XREngine.Runtime.Rendering/Buffers/XRDataBuffer.cs`
- `XREngine.Runtime.Rendering/Buffers/IApiDataBuffer.cs`
- `XREngine.Runtime.Rendering/Buffers/XRDataBufferView.cs`
- `XREngine.Runtime.Rendering/Buffers/EBufferMapStorageFlags.cs`
- `XREngine.Runtime.Rendering/Buffers/EBufferMapRangeFlags.cs`
- `XREngine.Data/Core/Memory/DataSource.cs`

Backend implementations:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Buffers/GLDataBuffer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkDataBuffer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanDynamicUniformRingBuffer.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanStagingManager.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanSceneDatabaseAddresses.cs`

High-value consumers to migrate first:

- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.cs`
- `XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.*.cs`
- `XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs`
- `XREngine.Runtime.Rendering/Rendering/UI/UIBatchCollector.cs`
- `XREngine.Runtime.Rendering/Rendering/Compute/SkinningPrepassDispatcher/*.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Particles/ParticleEmitterComponent.cs`
- `XREngine.Runtime.Rendering/Scene/Components/Landscape/LandscapeComponent.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline*.cs`
  (light probe and PPLL transparency buffers)
- `XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs`
- `XRENGINE/Rendering/Compute/GPUSoftbodyDispatcher.cs`
- `XRENGINE/Scene/Components/UI/Text/UIText.cs`
- `XRENGINE/Scene/Components/UI/Text/UITextComponent.cs`
- `XRENGINE/Scene/Components/UI/Core/UIWebViewComponent.cs`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/GI/VPRC_SurfelGIPass.cs`

Related docs:

- `docs/work/todo/rendering/vulkan-wrapper-parity/README.md#xrdatabuffer`
- `docs/work/design/rendering/render-submission-perf-debug-plan.md`
- `docs/work/design/rendering/engine-optimization-and-avatar-optimizer-design.md`
- `docs/work/design/rendering/gpu-meshlet-zero-readback-rendering-design.md`
- `docs/architecture/rendering/mesh-submission-strategies.md`

## Current State

`XRDataBuffer` owns CPU-side data, layout metadata, mapping flags, and events.
Backends subscribe to `PushDataRequested`, `PushSubDataRequested`,
`MapBufferDataRequested`, `FlushRequested`, binding events, and descriptor/block
events.

OpenGL and Vulkan now have close parity for:

- Full upload and subdata upload.
- Mapping, unmapping, flush, and flush range.
- Resizable growth behavior.
- Immutable/dynamic storage behavior.
- Vulkan staging uploads for device-local buffers.
- Vulkan device-address support for selected scene database buffers.
- Upload/readback diagnostics and push-subdata tracing.

Existing related surface to build on rather than duplicate:

- `XRDataBuffer.Usage` (`EBufferUsage`, default `StaticCopy`) already expresses
  upload-frequency intent and must be reconciled with the new memory policy.
- `RenderDiagnosticsFlags.PushSubDataTrace` (`XRE_PUSHSUBDATA_TRACE`) traces
  compatibility-path pushes.
- `RenderWorkBudgetCoordinator` already tracks texture/shader/mesh upload queue
  depths for frame budgeting.
- `docs/work/audit/new-allocations.md` (generated by the `Report-NewAllocations`
  task) already lists every `new XRDataBuffer` call site.

The remaining problem is caller ergonomics and correctness. Callers still need
to know whether to allocate CPU storage, map the buffer, push full data, push a
subrange, avoid mapping during GPU use, manually flush, or avoid keeping a CPU
mirror. This spreads backend policy into renderer, physics, UI, and GI code.

## Non-Goals

- Do not expose Vulkan buffer device addresses as CPU-writable pointers.
- Do not hide unsupported accelerated paths behind silent CPU fallbacks.
- Do not require all buffers to keep a permanent CPU mirror.
- Do not force every buffer through persistent mapped host-visible memory; static
  shader data should still prefer device-local storage.
- Do not remove existing `PushData`, `PushSubData`, or map APIs in the first
  migration pass. Keep them as compatibility and diagnostic paths until callers
  are ported.

## Target Concepts

### Buffer Identity

`XRDataBuffer` remains the stable engine resource identity used by materials,
programs, descriptors, mesh attributes, debug tools, and serialization.

The new write model should layer on top of that identity rather than replacing
it outright.

When the element type is known at construction time, add a typed derived form:
`XRDataBuffer<T> : XRDataBuffer where T : unmanaged`. This should be a typed
facade over the same backend resource identity, not a second buffer system.
Because it derives from `XRDataBuffer`, existing bind/material/mesh APIs can
continue accepting the base type while typed call sites get `Alloc(count)`,
`Span<T>`, typed count/stride helpers, and automatic layout configuration.

### Memory Policy

Introduce an engine-facing policy enum that states intent instead of exposing
raw GL map flag combinations to every caller.

Proposed shape:

```csharp
public enum XRBufferMemoryPolicy
{
    GpuOnly,
    CpuToGpuUpload,
    CpuToGpuDynamic,
    CpuToGpuPersistentRing,
    GpuToCpuReadback,
    CpuGpuSharedDiagnostic,
}
```

Backend resolution examples:

- `GpuOnly`: device-local Vulkan buffer or ordinary GL buffer; writes use
  staging/upload.
- `CpuToGpuUpload`: transient upload allocation copied into final storage.
- `CpuToGpuDynamic`: host-visible buffer for small/frequent updates where
  staging would cost more than it saves.
- `CpuToGpuPersistentRing`: persistent mapped ring slots guarded by fences.
- `GpuToCpuReadback`: readback staging or host-cached memory with explicit
  invalidate/wait semantics.
- `CpuGpuSharedDiagnostic`: visibly slower shared memory path for tools and
  bring-up, not production steady state.

### Write Scope

Add a scoped writer API that returns typed spans and commits dirty ranges.

Example target call site:

```csharp
using (var writer = renderCommands.Alloc<GPUIndirectRenderCommand>(
        count,
        XRBufferWriteMode.DiscardOrRing))
{
    Span<GPUIndirectRenderCommand> commands = writer.Span;
    // Fill commands.
}
```

For typed buffers or typed views, the intended hot-path shape is even smaller:

```csharp
using (var writer = renderCommands.Alloc(count, XRBufferWriteMode.DiscardOrRing))
{
    Span<GPUIndirectRenderCommand> commands = writer.Span;
    // Fill commands.
}
```

The terse `Alloc` overloads use defaults stored on the buffer/view. An
`XRBufferWriteOptions` overload exposes the full policy surface (see API
Sketch) for unusual cases.

`XRBufferWriter<T>.Dispose()` should auto-commit by default when neither
`Commit()` nor `Cancel()` has already been called. That supports both
bracketed `using (...) { ... }` and `using var` scopes. Because the preferred
writer is stack-only, implement pattern-based disposal with a public
`Dispose()` method; only implement `IDisposable` if a non-`ref struct` writer is
introduced for a non-hot-path scenario. Callers that abandon a write must call
`Cancel()` before leaving the scope.

The writer owns:

- Capacity checks.
- Optional geometric growth.
- Backend allocation selection.
- Dirty range tracking.
- Auto-commit on scope exit unless cancelled or explicitly committed.
- Flush/invalidate on commit when needed.
- Upload queue or staging copy submission.
- Fence/timeline slot ownership.
- Optional CPU mirror update.

### Dirty Ranges

Writing APIs should record exact byte ranges by default. Callers can still
request full discard when cheaper than merging ranges.

Required range modes:

- Full discard/rewrite.
- Tail append.
- Single contiguous range.
- Multiple scattered ranges merged by threshold.
- No-op when revision/content is unchanged.

### Thread Contract

Buffer writes originate from the update thread (UI text, physics state), the
render thread (GPUScene swaps, pass collections), and async jobs (mesh
generation). The writer API must state an explicit contract:

- Which threads may call `BeginWrite`, `Commit`, and `Cancel` for each memory
  policy.
- Whether `Commit` performs GPU work inline (render thread only) or enqueues
  work that the render thread consumes.
- `ref struct` writers cannot cross `await` boundaries or be captured by
  closures; confirm every migrated call site tolerates that, or provide a
  pooled heap-safe variant for async producers.
- How commits interact with the separately clocked update, collect-visible,
  and render loops so one-shot writes cannot be dropped between loop swaps.

### Readback Ticket

Replace ad hoc map-and-read flows for GPU-written data with explicit readback
tickets.

Example target call site:

```csharp
XRBufferReadbackTicket ticket = particles.RequestReadback(byteOffset, byteCount);
if (ticket.TryGetSpan<GPUParticleData>(out ReadOnlySpan<GPUParticleData> data))
{
    // Consume completed readback.
}
```

The ticket owns:

- Copy to readback storage.
- Fence/timeline wait state.
- Non-coherent invalidate.
- Lifetime of temporary staging memory.
- Diagnostic readback counters.

## Phase Sequencing

Phases 0-3 are sequential prerequisites. Phases 4 and 5 depend on 1-3 but can
proceed in parallel with each other. Phase 6 depends on 2 and 4. Phase 7 must
land before the 9.4 ring migrations. Run a build plus the nearest targeted
tests at the end of each phase rather than deferring all validation to
Phase 10.

## Phase 0: Audit And Classification

- [x] Create a dedicated branch for this todo list. Skipped per explicit
      request: "don't branch".
- [x] Inventory all `new XRDataBuffer` call sites, seeded from the generated
      allocation audit (`Report-NewAllocations` →
      `docs/work/audit/new-allocations.md`) instead of a manual sweep.
- [ ] Classify each buffer by memory policy: static upload, dynamic upload,
      per-frame ring, GPU-written/readback, texture/PBO interop, diagnostic, or
      serialized CPU asset data.
- [ ] Inventory every direct `ClientSideSource`, `Address`, `VoidPtr`,
      `SetDataRaw`, `WriteDataRaw`, `MapBufferData`, `PushData`,
      `PushSubData`, `Flush`, and `GetDataRawAtIndex` use.
- [ ] Identify hot-path callers that currently allocate, copy, box, use LINQ,
      or build temporary arrays before upload.
- [ ] Identify buffers that should not keep CPU mirrors after upload.
- [ ] Identify GPU-written buffers that violate zero-readback policy in
      production paths.
- [x] Add the audit table to this doc or a linked generated report.

Acceptance:

- [ ] Every buffer call site has a proposed memory policy and migration owner.
- [ ] Production zero-readback strategy buffers are explicitly marked as
      readback-forbidden except diagnostic modes.

## Phase 1: Public Contract

- [x] Add `XRBufferMemoryPolicy`.
- [x] Define the relationship between `XRBufferMemoryPolicy` and the existing
      `XRDataBuffer.Usage` (`EBufferUsage`) hint: derive one from the other or
      replace `Usage`, but do not leave two competing intent knobs.
- [x] Add `XRBufferWriteMode` with at least `Preserve`, `Discard`,
      `DiscardOrRing`, `Append`, and `Scattered`.
- [x] Add `XRBufferCpuAccess` or equivalent read/write intent if policy alone is
      not precise enough.
- [x] Add `XRBufferWriteOptions` for alignment, growth, clear-on-allocate,
      keep-CPU-mirror, and allow-staging-copy.
- [x] Add default write policy/mode/alignment properties to buffers or typed
      views so most hot-path writes can use `Alloc(count)` or
      `Alloc(count, mode)`.
- [x] Add `XRBufferWriter<T>` as a `ref struct` or disposable scope that exposes
      `Span<T>`.
- [x] Add terse aliases:
      `Alloc<T>(count)`, `Alloc<T>(count, mode)`, `Alloc<T>(count, options)`,
      and typed-view `Alloc(count)` / `Alloc(count, mode)`.
- [x] Add raw-byte writer support for interop payloads.
- [x] Add `Commit()` and `Cancel()` semantics.
- [x] Add `Dispose()` semantics: default auto-commit on dispose, no-op after
      explicit `Commit()` or `Cancel()`, and optional explicit-commit-required
      mode for risky paths.
- [x] Use pattern-based disposal for `ref struct` writers. Do not require
      boxing or interface dispatch in hot paths.
- [x] Add debug checks for double commit/cancel, use-after-dispose, and dispose
      of partially initialized writers.
- [x] Add XML docs that distinguish CPU mapped pointers from GPU device
      addresses.
- [x] Keep existing `PushData` and `PushSubData` APIs, but mark new code paths
      to prefer scoped writers.

Acceptance:

- [ ] New code can allocate and write a typed buffer without touching
      `DataSource`, `VoidPtr`, `PushData`, `PushSubData`, or map flags.
- [ ] Misusing a GPU device address as CPU memory is impossible through the
      public API.

## Phase 2: Backend-Neutral State Model

- [x] Promote or standardize buffer state that callers can query without knowing
      the backend.
- [x] Track generated/API-object state separately from allocation state.
- [x] Track allocated byte size.
- [x] Track uploaded byte count or uploaded revision.
- [x] Track pending upload/copy state.
- [x] Track current memory policy and resolved backend route.
- [x] Track persistent mapping state.
- [x] Track CPU mirror presence.
- [x] Track device address availability and downgrade reason.
- [x] Track descriptor/binding readiness separately from upload readiness.
- [x] Add a backend-neutral `IsReadyForGpuUse` or equivalent readiness property.

Acceptance:

- [ ] OpenGL and Vulkan report the same state categories in diagnostics.
- [ ] Existing `IsGenerated` semantics remain API-object existence only.

## Phase 3: Dirty Range And Revision Tracking

- [x] Add a revision counter to `XRDataBuffer`.
- [x] Increment revision on committed writes.
- [x] Record dirty byte ranges for committed writes.
- [x] Merge adjacent dirty ranges.
- [x] Collapse to full upload when range count or byte coverage crosses a
      configured threshold.
- [x] Support append-only writes without uploading unchanged prefixes.
- [x] Support content/revision checks that skip `Memory.Move` and upload when
      source data is unchanged.
- [x] Add debug assertions for writes outside allocated capacity.
- [x] Add diagnostic traces for range merge decisions.

Acceptance:

- [ ] A caller that writes only a tail range emits only a tail upload.
- [ ] Static clean frames can skip CPU copy and GPU upload work.

## Phase 4: Upload Allocator And Staging Path

- [ ] Add a backend-neutral upload allocator interface.
- [ ] Route `GpuOnly` and large static writes through staging/upload memory.
- [ ] Reuse Vulkan `VulkanStagingManager` from writer commits.
- [ ] Add or reuse an OpenGL upload queue/staging path for large writes.
- [ ] Support frame-budgeted copy submission where the backend already supports
      it.
- [ ] Ensure `DisposeOnPush` behavior maps to writer completion, not merely
      enqueue time.
- [ ] Add visible diagnostics when an upload is deferred, skipped, cancelled, or
      downgraded.
- [ ] Add per-frame upload byte counters by route.

Acceptance:

- [ ] Static device-local Vulkan writes do not require caller-visible
      `ClientSideSource`.
- [ ] Large uploads do not accidentally become render-thread stalls without
      diagnostics.

## Phase 5: Persistent Mapped Ring Buffers

- [ ] Add a generic RHI persistent mapped ring allocator for CPU-to-GPU dynamic
      data.
- [ ] Support at least 3 slots or frames in flight.
- [ ] Guard slot reuse with GL sync objects and Vulkan fences/timelines.
- [ ] Expose slot index, byte offset, and backing buffer identity to descriptor
      and draw code.
- [ ] Support coherent and explicit-flush variants.
- [ ] Align allocations to backend requirements:
      uniform-buffer alignment, storage-buffer alignment, non-coherent atom
      size, indirect-command alignment, and vertex/index alignment.
- [ ] Add overrun diagnostics when a ring exhausts in a frame.
- [ ] Add fallback policy when persistent mapping is unavailable.
- [ ] Add debug validation that readers bind the same slot that the writer
      committed for the frame.

Acceptance:

- [ ] Per-frame command/material/count buffers can be updated without
      same-buffer overwrite hazards.
- [ ] Coherent mapping is never treated as a replacement for fence-protected
      slot ownership.

## Phase 6: Readback Tickets

- [x] Add `XRBufferReadbackTicket`.
- [x] Add `RequestReadback(offset, byteCount)` to `XRDataBuffer` or a readback
      service.
- [ ] Route Vulkan readbacks through host-cached readback buffers and explicit
      invalidate.
- [ ] Route OpenGL readbacks through mapped readback buffers or PBO-style paths.
- [x] Add nonblocking completion checks.
- [x] Add blocking wait only behind explicit diagnostic APIs.
- [ ] Record readback bytes, mapped readback buffers, and zero-readback
      violations.
- [x] Make production GPU submission strategies reject accidental readback by
      default.

Acceptance:

- [ ] GPU-written buffers are read only through explicit tickets.
- [ ] `GpuIndirectZeroReadback` and `GpuMeshletZeroReadback` steady-state frames
      report zero readback bytes.

## Phase 7: Descriptor, Device Address, And Binding Integration

- [x] Ensure writer commits update descriptor/binding readiness.
- [x] Ensure buffer recreation invalidates descriptor caches safely.
- [x] Preserve stable engine-facing buffer identity across backend resource
      recreation.
- [x] Add backend-neutral `TryGetGpuAddress` that reports whether a shader
      device address is available and why not.
- [ ] Route Vulkan scene-database consumers through device address when enabled.
- [x] Keep OpenGL consumers on SSBO/bindless/classic binding paths without
      exposing fake addresses.
- [x] Add visible capability downgrade logs for missing buffer-device-address
      support.
- [ ] Add tests for descriptor readiness after writer-driven growth/recreate.

Acceptance:

- [ ] A writer-triggered resize does not leave stale descriptors or stale device
      addresses.
- [ ] Device-address use remains Vulkan-native, optional, and diagnostic.

## Phase 8: Typed Buffers And Views

Note: the existing `XRDataBufferView` is a sized-internal-format subrange view
for texel-buffer-style binding, not a typed CPU accessor. Decide whether to
extend it or add a separate typed wrapper so the two roles stay distinct.

- [x] Add `XRDataBuffer<T> : XRDataBuffer where T : unmanaged` for buffers
      whose element type is known when the buffer is created.
- [x] Have `XRDataBuffer<T>` configure component type, component count, stride,
      element size, padding, and default typed allocation behavior from `T`.
- [x] Keep `XRDataBuffer<T>` assignable to existing `XRDataBuffer` parameters
      so render programs, descriptors, materials, and mesh APIs do not need a
      parallel generic surface.
- [ ] Decide serialization behavior for typed derived buffers. `XRDataBuffer`
      is MemoryPack/YAML serialized today; typed buffers may need explicit
      discriminators, factory registration, or a rule that serialized assets
      store only the base buffer payload plus layout metadata.
- [x] Add or extend `XRDataBufferView` for typed element count, stride, byte
      offset, byte length, and alignment.
- [x] Ensure typed views can represent structs, vector numeric buffers,
      interleaved vertex data, indirect commands, and std430-like records.
- [x] Add checked conversions from `Span<T>` length to byte length.
- [x] Add validation for non-blittable types.
- [x] Add debug display names that include buffer name, view name, stride,
      offset, and count.

Acceptance:

- [ ] Most new buffer code writes `Span<T>` rather than raw pointers.
- [ ] Raw pointer access remains available only for low-level interop and
      backend code.

## Phase 9: Migration Order

### 9.1 Low-Risk Static Uploads

- [ ] Migrate one static mesh/attribute upload path.
- [ ] Migrate one texture-buffer upload path.
- [x] Migrate one GI setup buffer or GI update buffer that currently uses
      manual push semantics.
- [ ] Validate OpenGL and Vulkan upload diagnostics.

### 9.2 UI And PBO-Like Streaming

- [x] Migrate text transform, UV, and index buffers.
- [ ] Migrate UI web view PBO writes.
- [ ] Preserve persistent mapping where it is beneficial.
- [ ] Confirm no extra CPU mirror is kept when not needed.

### 9.3 Physics And Compute

- [ ] Migrate GPU physics chain upload buffers.
- [ ] Migrate GPU physics readback buffers to readback tickets.
- [ ] Migrate softbody compute upload buffers.
- [ ] Validate compute dispatch writes and subsequent render reads with barriers.

### 9.4 GPUScene And Render Submission

- [ ] Migrate command buffer swaps to writer scopes.
- [ ] Add dirty-version checks before `Memory.Move`.
- [ ] Use tail append for atlas growth uploads.
- [ ] Move render command, material tier, draw count, culled command, and scatter
      table updates toward persistent ring or staging based on policy.
- [ ] Ensure each render pass binds the committed frame slot.
- [ ] Validate `GpuIndirectInstrumented` and `GpuIndirectZeroReadback`.

### 9.5 Diagnostic And Tool Paths

- [ ] Keep explicit slow/shared-memory paths for debugging.
- [ ] Ensure diagnostic paths log capability downgrades and readback costs.
- [ ] Update RenderDoc/temp inspection tooling only if buffer layout or naming
      changes affect captures.

Acceptance:

- [ ] No migrated caller manually calls `PushData` or `PushSubData` for normal
      writes.
- [ ] Hot-path migrated callers do not allocate in steady state.

## Phase 10: Telemetry And Validation

- [ ] Integrate with existing diagnostics rather than adding parallel systems:
      `RenderDiagnosticsFlags.PushSubDataTrace` for compatibility-path tracing
      and `RenderWorkBudgetCoordinator` for upload queue depth/budget counters.
- [ ] Add steady-state counters for upload bytes by route.
- [ ] Add staging allocation/reuse counters.
- [ ] Add persistent ring allocation, exhaustion, and fence-wait counters.
- [ ] Add host-visible write counters.
- [ ] Add host-cached readback counters.
- [ ] Add device-address consumer and descriptor-fallback counters.
- [ ] Add zero-readback violation counters.
- [ ] Add push-subdata compatibility-path counters so old callers remain visible.
- [ ] Add speed-profile summary fields for new counters.
- [ ] Add log rows that include buffer name, policy, route, bytes, dirty range
      count, allocated bytes, uploaded revision, frame slot, and readiness.

Validation:

- [x] Source/unit test: writer commit records dirty ranges and revisions.
- [x] Source/unit test: writer dispose auto-commits when dispose behavior is
      `Commit` and neither `Commit()` nor `Cancel()` was called.
- [x] Source/unit test: writer cancel leaves buffer revision unchanged.
- [x] Source/unit test: explicit `Commit()` makes later `Dispose()` a no-op.
- [x] Source/unit test: explicit `Cancel()` makes later `Dispose()` a no-op.
- [x] Source/unit test: `RequireExplicitCommit` reports an error when a writer
      is disposed without `Commit()` or `Cancel()`.
- [x] Source/unit test: dirty range merging collapses to full upload above the
      threshold.
- [x] Source/unit test: write policy resolves to expected OpenGL/Vulkan route.
- [x] Source/unit test: readback ticket cannot expose data before completion.
- [x] Source/unit test: device address query reports downgrade reason when
      unsupported.
- [ ] Hardware OpenGL: persistent ring path with fence-protected slot reuse.
- [ ] Hardware Vulkan: persistent ring path with fence/timeline-protected slot
      reuse.
- [ ] Hardware Vulkan: device-local static write through staging.
- [ ] Hardware Vulkan: readback ticket with non-coherent invalidate.
- [ ] Strategy validation: `GpuIndirectZeroReadback` steady state reports zero
      readback bytes and no compatibility `PushSubData` flood.

## API Sketch

Proposed caller-facing surface:

```csharp
public readonly struct XRBufferWriteOptions
{
    public XRBufferMemoryPolicy MemoryPolicy { get; init; }
    public XRBufferWriteMode WriteMode { get; init; }
    public XRBufferWriterDisposeBehavior DisposeBehavior { get; init; }
    public bool KeepCpuMirror { get; init; }
    public bool ClearOnAllocate { get; init; }
    public uint AlignmentBytes { get; init; }
}

public enum XRBufferWriterDisposeBehavior
{
    Commit = 0,
    Cancel,
    RequireExplicitCommit,
}

public partial class XRDataBuffer
{
    public XRBufferMemoryPolicy DefaultMemoryPolicy { get; set; }
    public XRBufferWriteMode DefaultWriteMode { get; set; }
    public uint DefaultAlignmentBytes { get; set; }

    public XRBufferWriter<T> Alloc<T>(uint count) where T : unmanaged;
    public XRBufferWriter<T> Alloc<T>(
        uint count,
        XRBufferWriteMode mode) where T : unmanaged;
    public XRBufferWriter<T> Alloc<T>(
        uint count,
        XRBufferWriteOptions options) where T : unmanaged;
}

public sealed class XRDataBuffer<T> : XRDataBuffer where T : unmanaged
{
    public uint TypedElementCount { get; }
    public uint TypedElementSize { get; }

    public XRBufferWriter<T> Alloc(uint count);
    public XRBufferWriter<T> Alloc(uint count, XRBufferWriteMode mode);
    public XRBufferWriter<T> Alloc(uint count, XRBufferWriteOptions options);

    public Span<T> GetCpuMirrorSpan();
    public void SetData(ReadOnlySpan<T> data);
    public void Write(uint elementOffset, ReadOnlySpan<T> data);
}

public ref struct XRBufferWriter<T> where T : unmanaged
{
    public Span<T> Span { get; }
    public uint ElementOffset { get; }
    public uint ElementCount { get; }
    public ulong ByteOffset { get; }
    public bool IsCommitted { get; }
    public bool IsCancelled { get; }
    public void MarkDirty(uint elementOffset, uint elementCount);
    public void Commit();
    public void Cancel();
    public void Dispose();
}
```

Possible usage:

```csharp
// Preferred hot-path form when the buffer/view already owns defaults.
using (var writer = renderCommands.Alloc(commandCount, XRBufferWriteMode.DiscardOrRing))
{
    commands.CopyTo(writer.Span);
}
```

Explicit override form:

```csharp
XRBufferWriteOptions options = new()
{
    MemoryPolicy = XRBufferMemoryPolicy.CpuToGpuPersistentRing,
    WriteMode = XRBufferWriteMode.DiscardOrRing,
    DisposeBehavior = XRBufferWriterDisposeBehavior.Commit,
    AlignmentBytes = 16,
};

using (var writer =
    renderCommands.Alloc<GPUIndirectRenderCommand>(commandCount, options))
{
    commands.CopyTo(writer.Span);
}
```

Explicit cancellation:

```csharp
using (var writer = renderCommands.Alloc(commandCount))
{
    if (!TryBuildCommands(writer.Span))
    {
        writer.Cancel();
        return;
    }
}
```

Backend commit choices:

- OpenGL persistent ring: write mapped slot, flush if explicit, bind slot/range.
- OpenGL fallback: enqueue/upload dirty ranges.
- Vulkan persistent ring: write mapped slot, flush if non-coherent, bind
  descriptor offset/range or address.
- Vulkan device-local: copy writer staging memory into final buffer and record
  ownership/barrier transitions.

## Risks

- A too-generic writer API could hide important performance choices. Keep memory
  policy explicit and logged.
- Persistent mapped rings can produce subtle one-frame or multi-frame stale data
  bugs if slot binding is wrong. Add debug asserts and frame-slot metadata.
- Dirty range merging can cost more CPU than it saves for highly scattered
  updates. Add thresholds and telemetry.
- Removing CPU mirrors too aggressively can break serialization, editor
  inspectors, or diagnostics. `XRDataBuffer` is MemoryPack- and YAML-serialized,
  so mirror ownership changes alter what persists; audit `[MemoryPackable]` and
  `[YamlIgnore]` surfaces when making CPU mirror ownership explicit per buffer.
- Readback tickets and writer scopes must obey the hot-path allocation policy:
  pool tickets and keep writers as `ref struct`s so steady-state frames do not
  allocate.
- Compatibility `PushData` and `PushSubData` paths may mask incomplete
  migrations. Count and report them.

## Done Criteria

- [ ] New production buffer writes use scoped writer APIs.
- [ ] Existing compatibility push/map APIs remain available but are no longer
      required in normal renderer, physics, UI, or GI update paths.
- [ ] OpenGL and Vulkan select equivalent memory policies from the same
      engine-facing contract.
- [ ] Static data can upload through staging into device-local storage without a
      permanent CPU mirror.
- [ ] Per-frame dynamic data can use fence-protected persistent mapped rings.
- [ ] GPU-written data uses explicit readback tickets.
- [ ] Zero-readback production strategies remain visibly readback-free.
- [ ] Buffer diagnostics explain policy, route, readiness, dirty ranges,
      device-address state, and fallback reasons.
- [ ] Merge the todo branch back into `main` after completion and validation.
