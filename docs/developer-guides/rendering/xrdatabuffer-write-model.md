# XRDataBuffer Write Model

`XRDataBuffer` now has a backend-neutral write contract for normal engine code.
Prefer this API for new runtime writes instead of manually touching
`ClientSideSource`, `VoidPtr`, `PushData`, `PushSubData`, or map flags.

## Scoped Writers

Use scoped writers when the destination element type is known:

```csharp
using (XRBufferWriter<GPUIndirectRenderCommand> writer =
    commands.Alloc<GPUIndirectRenderCommand>(commandCount, XRBufferWriteMode.DiscardOrRing))
{
    Span<GPUIndirectRenderCommand> span = writer.Span;
    // Fill span.
}
```

Writers are stack-only `ref struct`s. They cannot cross `await`, be captured by
closures, or be stored on heap objects. `Dispose()` commits by default when the
writer was not explicitly committed or cancelled. Use `Cancel()` when a producer
abandons a write.

`XRBufferWriteOptions` controls policy, write mode, CPU access, dispose behavior,
alignment, geometric growth, clear-on-allocate, and staging permission. The
short `Alloc<T>(count)` and `Alloc<T>(count, mode)` overloads read defaults from
the buffer.

## Direct Dirty Commits

Some low-level paths still write an existing CPU mirror directly because they
already own an interop pointer or must preserve the current layout metadata. In
those cases, commit the changed range through the write model:

```csharp
buffer.CommitDirtyElements(firstElement, elementCount);
buffer.CommitDirtyBytes(byteOffset, byteCount);
```

These methods preserve the current buffer layout and still update revisions,
dirty ranges, upload readiness, diagnostics, and backend upload state.

## Typed Buffers

Use `XRDataBuffer<T>` when the element type is known at construction time. It
derives from `XRDataBuffer`, so existing material, mesh, descriptor, and program
APIs can still accept it as the base type.

```csharp
XRDataBuffer<Vector4> glyphTransforms = new(
    "GlyphTransformsBuffer",
    EBufferTarget.ShaderStorageBuffer,
    glyphCapacity);

using (XRBufferWriter<Vector4> writer =
    glyphTransforms.Alloc(glyphCount, XRBufferWriteMode.Discard))
{
    transforms.CopyTo(writer.Span);
}
```

`XRDataBuffer<T>` configures component type, component count, stride, element
size, padding defaults, and typed `Alloc` aliases from `T`. Runtime-only buffers
with known records should prefer the typed declaration. Serialized mesh and
cooked asset buffers continue to store the base `XRDataBuffer` payload plus
layout metadata; typed buffers are a runtime facade and do not introduce generic
serialization discriminators.

## Policies And Routes

`XRBufferMemoryPolicy` is the engine-facing intent. It is reconciled with legacy
`EBufferUsage` so callers do not need two competing policy knobs.

- `GpuOnly`: final GPU storage, with upload/staging routes where supported.
- `CpuToGpuUpload`: CPU-produced upload data.
- `CpuToGpuDynamic`: small or frequent CPU-to-GPU updates.
- `CpuToGpuPersistentRing`: dynamic data that should use fence-protected ring
  storage where the backend has a real ring path.
- `GpuToCpuReadback`: explicit readback ticket paths.
- `CpuGpuSharedDiagnostic`: slow shared-memory diagnostics.

OpenGL reports persistent-mapped, queued upload, and compatibility push routes.
Vulkan reports host-visible, device-local/staging, persistent-ring-capable, and
device-address routes. OpenGL never exposes fake GPU device addresses; use
`TryGetGpuAddress(out address, out reason)` and respect the downgrade reason.

## State And Readiness

Use `GetStateSnapshot()` for backend-neutral diagnostics. It includes generated
state, allocated/uploaded bytes, revisions, pending upload state, memory policy,
resolved route, persistent mapping state, CPU mirror presence, device-address
availability, descriptor readiness, GPU readiness, and dirty range count.

`Revision` increments on committed writes. `UploadedRevision` advances when a
backend reports that the committed data is resident and ready. Dirty ranges are
merged, and they collapse to a full upload when range count or byte coverage
crosses the buffer thresholds.

## Upload Allocators And Rings

`IXRBufferUploadAllocator` is the backend-neutral upload allocation contract.
`XRBufferCpuUploadAllocator.Shared` provides pooled CPU upload memory for paths
that do not have a native backend allocator. Vulkan still prefers
`VulkanStagingManager` for device-local uploads; OpenGL reports compatibility or
queued upload routes rather than pretending it has a Vulkan-style staging path.

`XRBufferPersistentRingAllocator` is the generic ring-slot guard for dynamic
CPU-to-GPU data. It exposes slot index, byte offset, byte count, alignment, and
backing buffer identity, and it guards slot reuse with `XRGpuFence` instances.
Backend-specific code owns the actual GL/Vulkan mapped memory and descriptor
binding offsets.

## Readback

Use `RequestReadback(offset, byteCount)` for GPU-to-CPU reads. Production paths
should reject accidental readback unless the buffer policy allows it. Diagnostic
readback from an existing CPU mirror is available through the ticket API, but a
ticket will not expose data before it is complete.

## Telemetry

`XRBufferWriteTelemetry` integrates with `RenderWorkBudgetCoordinator` speed
profile summaries. Current fields include upload bytes by route, compatibility
push bytes, staging allocations/reuses, persistent ring allocations/exhaustion
and fence waits, host-visible writes, host-cached readbacks, device-address
consumers, descriptor fallbacks, readback bytes, diagnostic shared bytes, and
zero-readback violations.

When `RenderDiagnosticsFlags.UploadStageLogging` or
`RenderDiagnosticsFlags.PushSubDataTrace` is enabled, writer commits log rows
that include buffer name, policy, route, bytes, dirty range count, allocated
bytes, uploaded revision, current revision, readiness, and pending state.
Upload-stage logging is explicit opt-in through `XRE_UPLOAD_STAGE_LOGGING` or
the editor preference; attaching a debugger alone must not enable it.

## Migrated Callers

Current typed/writer migrations include UI batching, legacy UI text buffers,
web-view PBO byte buffers, particle compute buffers, global skin palette packing,
physics chain uploads/readback pools, softbody compute uploads, GPU-driven bone
palette mapping, and Surfel GI setup/update buffers. Public mesh, render-pass,
and GPUScene buffer accessors remain base-typed where serialization or tests
assert the stable `XRDataBuffer` contract.
