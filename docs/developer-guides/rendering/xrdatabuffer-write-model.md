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

## Readback

Use `RequestReadback(offset, byteCount)` for GPU-to-CPU reads. Production paths
should reject accidental readback unless the buffer policy allows it. Diagnostic
readback from an existing CPU mirror is available through the ticket API, but a
ticket will not expose data before it is complete.
