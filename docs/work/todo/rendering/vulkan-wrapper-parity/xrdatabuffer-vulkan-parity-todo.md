# XRDataBuffer Vulkan Parity TODO

Last Updated: 2026-06-05
Status: Active.

## Goal

Make `VkDataBuffer` provide the same engine-facing behavior as `GLDataBuffer`:
push, subdata, mapping, flush, storage flags, range flags, binding, SSBO/UBO
resolution, diagnostics, resize/growth handling, and a clear split between
API-object generation and data readiness.

## Source Inventory

Shared:

- `XREngine.Runtime.Rendering/Buffers/XRDataBuffer.cs`
- `XREngine.Runtime.Rendering/Buffers/IApiDataBuffer.cs`
- `XREngine.Data/Rendering/Enums/EBufferTarget.cs`
- `XREngine.Data/Rendering/Enums/EBufferUsage.cs`
- `XREngine.Runtime.Rendering/Buffers/EBufferMapStorageFlags.cs`
- `XREngine.Runtime.Rendering/Buffers/EBufferMapRangeFlags.cs`

OpenGL:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Buffers/GLDataBuffer.cs`

Vulkan:

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/VkDataBuffer.cs`

## Current Parity Already Present

- Vulkan subscribes to the same major `XRDataBuffer` events as OpenGL.
- Vulkan supports full-buffer upload, subdata upload, map/unmap, flush, and
  flush range.
- Vulkan resolves target/usage to Vulkan buffer usage flags.
- Vulkan supports device-local uploads through staging buffers.
- Vulkan supports buffer device address for selected scene database buffers.
- Vulkan has a GDeflate/NV decompression path that OpenGL does not have.

## Generation Contract

For `XRDataBuffer`, OpenGL `IsGenerated` means the GL buffer object has been
created and has a non-zero object ID. It does not mean storage has the latest
bytes, a queued upload has completed, or the buffer is ready for rendering.
OpenGL uses separate state such as `_lastPushedLength`, pending upload flags,
and `IsReadyForRendering` for content readiness.

Vulkan should mirror that meaning. `VkDataBuffer.IsGenerated` should indicate
that the Vulkan-side buffer object/handle exists for the wrapper. Upload
freshness, allocated byte size, mapped state, descriptor binding readiness, and
compute/draw readiness should be represented by separate state.

## Missing Parity TODO

1. Align generation-state semantics.
   - [ ] Replace or verify `public override bool IsGenerated { get; }` so it
         reflects Vulkan buffer handle/API-object existence.
   - [ ] Do not use `IsGenerated` to mean successful upload, current data,
         descriptor readiness, or draw readiness.
   - [ ] Add explicit state for data readiness, such as uploaded byte count,
         pending upload, or `IsReadyForRendering`-equivalent behavior.
   - [ ] Audit callers that use `IsGenerated`, `IsActive`, `BufferHandle`, or
         `AllocatedByteSize` before binding so they choose generation or
         readiness intentionally.
   - [ ] Add a source/unit test for `Generate()`, `PushData()`, `Destroy()`,
         `IsGenerated`, and data-readiness state transitions.

2. Match mapping contract.
   - [ ] Align Vulkan with OpenGL's generic mapping guard:
         `XRDataBuffer.ActivelyMapping.Count > 0` prevents duplicate maps by
         any API wrapper, not only this wrapper.
   - [ ] Decide whether resizable Vulkan buffers can map like OpenGL by
         allocating/clamping storage, or must emit a clear unsupported
         diagnostic.
   - [ ] Honor `ShouldMap` consistently during generation where engine code
         expects initial mapping.
   - [ ] Map readback buffers with visibility/invalidation behavior equivalent
         to OpenGL's client-mapped buffer barrier.
   - [ ] Preserve `GPUSideSource` lifetime and disposal semantics across
         recreate, unmap, and destroy.

3. Map `StorageFlags` and `RangeFlags` to Vulkan memory policy.
   - [ ] Translate Read/Write intent into host-visible, host-cached,
         host-coherent, device-local, or staging/readback allocation choices.
   - [ ] Respect Persistent and Coherent as a persistent mapping policy where
         Vulkan memory supports it.
   - [ ] Handle `FlushExplicit`, `InvalidateRange`, `InvalidateBuffer`, and
         `Unsynchronized` with Vulkan flush/invalidate/barrier equivalents or
         explicit diagnostics.
   - [ ] Treat `DynamicStorage` as "subdata update allowed" in the same places
         OpenGL avoids full immutable-storage recreation.
   - [ ] Document how `ClientStorage` maps to Vulkan, even if it is no-op.

4. Match upload and growth behavior.
   - [ ] Add a frame-budgeted upload path or staging-manager integration for
         large Vulkan buffer uploads comparable to OpenGL's upload queue.
   - [ ] Match `DisposeOnPush` behavior after successful upload and after
         queued/deferred upload completion.
   - [ ] Match OpenGL's resizable growth behavior in `PushSubData`: grow/full
         upload when needed, clamp out-of-range ranges, and warn when the
         requested offset exceeds allocated GPU length.
   - [ ] Ensure immutable/non-dynamic storage falls back to full reallocation
         for subdata updates.
   - [ ] Preserve GPU compressed upload behavior as a Vulkan extension, but
         make fallback CPU/staging upload diagnostics visible.

5. Match flush and mapped-memory behavior.
   - [ ] Fix early returns in `Flush` and `FlushRange` so explicit flush calls
         work while the buffer is mapped when required by the range flags.
   - [ ] Add invalidate paths for CPU reads from non-coherent Vulkan memory.
   - [ ] Clamp flush/invalidate ranges to the tracked allocation, matching
         existing readback safety rules.
   - [ ] Add validation-layer-safe behavior for zero-size and retired buffers.

6. Match SSBO/UBO binding behavior.
   - [ ] Implement `SetUniformBlockName` or a Vulkan descriptor-binding
         equivalent instead of a no-op.
   - [ ] Implement `SetBlockIndex` or a descriptor-binding equivalent instead
         of a no-op.
   - [ ] Resolve SSBO binding by shader block name when no explicit
         `BindingIndexOverride` is supplied, matching OpenGL's
         program-resource lookup.
   - [ ] Cache resolved descriptor binding points per program/layout.
   - [ ] Ensure buffers are allocated to current size before compute dispatches
         that write into SSBOs.

7. Match vertex-buffer binding semantics.
   - [ ] Verify Vulkan vertex input uses `AttributeName`, component type,
         component count, `Integral`, `Normalize`, `InstanceDivisor`, explicit
         binding override, and interleaved attribute metadata in the same
         effective way as OpenGL VAO binding.
   - [ ] Add diagnostics for missing attributes that include program, shader,
         buffer name, attribute name, and binding override.
   - [ ] Keep missing-attribute diagnostics rate-limited.

8. Match allocation accounting and diagnostics.
   - [ ] Add Vulkan VRAM budget checks before buffer allocation where OpenGL
         skips allocations over budget.
   - [ ] Track allocation removal/addition on recreate, retire, and destroy
         consistently.
   - [ ] Add upload route diagnostics comparable to OpenGL's first-use audit:
         target, usage, size, resizable, mapping flags, device-local vs
         host-visible, staging, compression, and device address.
   - [ ] Add PushSubData breakdown/trace equivalents for Vulkan buffer floods.

9. Clarify API differences that should remain non-parity.
   - [ ] Keep Vulkan device-address support as Vulkan-only.
   - [ ] Keep NV memory decompression as Vulkan-only.
   - [ ] Document both features so parity work does not accidentally remove
         them while aligning OpenGL behavior.

## Validation

- [ ] Source test: event subscription symmetry matches `IApiDataBuffer`.
- [ ] Unit/source test: `IsGenerated` changes with handle creation/recreation
      and destroy, while upload/readiness state changes with `PushData` and
      queued upload completion.
- [ ] Unit/source test: `PushSubData` clamps/grows/falls back like OpenGL.
- [ ] Unit/source test: mapping flags select expected Vulkan memory policy or
      emit expected diagnostics.
- [ ] Unit/source test: SSBO block-name binding resolves to the same declared
      binding as OpenGL for representative shaders.
- [ ] Hardware: run compute skinning, GPUScene, indirect draw, readback, UI
      PBO/webview, and texture-buffer paths with Vulkan validation layers.
