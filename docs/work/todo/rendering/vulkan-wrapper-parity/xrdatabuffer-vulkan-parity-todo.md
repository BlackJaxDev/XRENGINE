# XRDataBuffer Vulkan Parity TODO

Last Updated: 2026-06-08
Status: Implemented in source; hardware validation and expanded steady-state
telemetry follow-ups remain open.

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
   - [x] Replace or verify `public override bool IsGenerated { get; }` so it
         reflects Vulkan buffer handle/API-object existence.
   - [x] Do not use `IsGenerated` to mean successful upload, current data,
         descriptor readiness, or draw readiness.
   - [x] Add explicit state for data readiness, such as uploaded byte count,
         pending upload, or `IsReadyForRendering`-equivalent behavior.
   - [x] Audit callers that use `IsGenerated`, `IsActive`, `BufferHandle`, or
         `AllocatedByteSize` before binding so they choose generation or
         readiness intentionally.
   - [x] Add a source/unit test for `Generate()`, `PushData()`, `Destroy()`,
         `IsGenerated`, and data-readiness state transitions.

2. Match mapping contract.
   - [x] Align Vulkan with OpenGL's generic mapping guard:
         `XRDataBuffer.ActivelyMapping.Count > 0` prevents duplicate maps by
         any API wrapper, not only this wrapper.
   - [x] Decide whether resizable Vulkan buffers can map like OpenGL by
         allocating/clamping storage, or must emit a clear unsupported
         diagnostic.
   - [x] Honor `ShouldMap` consistently during generation where engine code
         expects initial mapping.
   - [x] Map readback buffers with visibility/invalidation behavior equivalent
         to OpenGL's client-mapped buffer barrier.
   - [x] Preserve `GPUSideSource` lifetime and disposal semantics across
         recreate, unmap, and destroy.

3. Map `StorageFlags` and `RangeFlags` to Vulkan memory policy.
   - [x] Translate Read/Write intent into host-visible, host-cached,
         host-coherent, device-local, or staging/readback allocation choices.
   - [x] Respect Persistent and Coherent as a persistent mapping policy where
         Vulkan memory supports it.
   - [x] Handle `FlushExplicit`, `InvalidateRange`, `InvalidateBuffer`, and
         `Unsynchronized` with Vulkan flush/invalidate/barrier equivalents or
         explicit diagnostics.
   - [x] Treat `DynamicStorage` as "subdata update allowed" in the same places
         OpenGL avoids full immutable-storage recreation.
   - [x] Document how `ClientStorage` maps to Vulkan, even if it is no-op.

4. Match upload and growth behavior.
   - [x] Add a frame-budgeted upload path or staging-manager integration for
         large Vulkan buffer uploads comparable to OpenGL's upload queue.
   - [x] Match `DisposeOnPush` behavior after successful upload and after
         queued/deferred upload completion.
   - [x] Match OpenGL's resizable growth behavior in `PushSubData`: grow/full
         upload when needed, clamp out-of-range ranges, and warn when the
         requested offset exceeds allocated GPU length.
   - [x] Ensure immutable/non-dynamic storage falls back to full reallocation
         for subdata updates.
   - [x] Preserve GPU compressed upload behavior as a Vulkan extension, but
         make fallback CPU/staging upload diagnostics visible.

5. Match flush and mapped-memory behavior.
   - [x] Fix early returns in `Flush` and `FlushRange` so explicit flush calls
         work while the buffer is mapped when required by the range flags.
   - [x] Add invalidate paths for CPU reads from non-coherent Vulkan memory.
   - [x] Clamp flush/invalidate ranges to the tracked allocation, matching
         existing readback safety rules.
   - [x] Add validation-layer-safe behavior for zero-size and retired buffers.

6. Match SSBO/UBO binding behavior.
   - [x] Implement `SetUniformBlockName` or a Vulkan descriptor-binding
         equivalent instead of a no-op.
   - [x] Implement `SetBlockIndex` or a descriptor-binding equivalent instead
         of a no-op.
   - [x] Resolve SSBO binding by shader block name when no explicit
         `BindingIndexOverride` is supplied, matching OpenGL's
         program-resource lookup.
   - [x] Cache resolved descriptor binding points per program/layout.
   - [x] Ensure buffers are allocated to current size before compute dispatches
         that write into SSBOs.

7. Match vertex-buffer binding semantics.
   - [x] Verify Vulkan vertex input uses `AttributeName`, component type,
         component count, `Integral`, `Normalize`, `InstanceDivisor`, explicit
         binding override, and interleaved attribute metadata in the same
         effective way as OpenGL VAO binding.
   - [x] Add diagnostics for missing attributes that include program, shader,
         buffer name, attribute name, and binding override.
   - [x] Keep missing-attribute diagnostics rate-limited.

8. Match allocation accounting and diagnostics.
   - [x] Add Vulkan VRAM budget checks before buffer allocation where OpenGL
         skips allocations over budget.
   - [x] Track allocation removal/addition on recreate, retire, and destroy
         consistently.
   - [x] Add upload route diagnostics comparable to OpenGL's first-use audit:
         target, usage, size, resizable, mapping flags, device-local vs
         host-visible, staging, compression, and device address.
   - [x] Add PushSubData breakdown/trace equivalents for Vulkan buffer floods.

9. Clarify API differences that should remain non-parity.
   - [x] Keep Vulkan device-address support as Vulkan-only.
   - [x] Keep NV memory decompression as Vulkan-only.
   - [x] Document both features so parity work does not accidentally remove
         them while aligning OpenGL behavior.

10. Consume Vulkan-native buffer capabilities where they improve the planned
    full implementation.
    - [x] Route at least one production scene-database path through
          `VkDataBuffer.DeviceAddress` instead of a classic descriptor binding.
    - [x] Report whether each scene-database buffer has a resolved device
          address, whether a draw path consumed it, and why fallback descriptor
          binding was used when it was not consumed.
    - [x] Keep the engine-facing buffer identity stable so OpenGL can still use
          SSBO/bindless-buffer equivalents without exposing Vulkan addresses.
    - [x] Treat missing buffer-device-address support as a visible capability
          downgrade for Vulkan GPU-driven paths, not as a silent CPU fallback.

## Vulkan-Native Acceptance Additions

- [x] Move normal Vulkan buffer churn toward allocator suballocation or pooled
      blocks. Per-buffer `vkAllocateMemory` should be treated as a bring-up or
      dedicated-allocation path, not the default for high-churn runtime buffers.
- [x] Report memory heap, memory type, allocation backend, dedicated vs
      suballocated placement, block ID, offset, size, alignment, budget, and
      allocation-count pressure for every significant buffer allocation.
- [x] Align non-coherent flush and invalidate ranges to
      `nonCoherentAtomSize`, then clamp to the tracked allocation before
      issuing Vulkan memory operations.
- [x] Track queue-family ownership and buffer access state for transfer,
      compute, graphics, indirect draw, index/vertex input, descriptor read,
      shader write, and readback uses.
- [x] Prefer transfer-queue and async-compute buffer work only when ownership
      transfers and synchronization are cheaper than same-queue execution;
      otherwise record the reason for staying on the graphics queue.
- [x] Retire replaced Vulkan buffers through timeline/fence ownership. Physical
      destruction should wait for the owning frame slot instead of forcing
      routine `DeviceWaitIdle`.
- [x] Keep buffer device address, memory decompression, indirect copy, and
      indirect-count draw consumers feature-gated and diagnostic, with visible
      capability downgrade reasons.
- [ ] Add steady-state counters for upload bytes, readback bytes, staging reuse,
      host-visible writes, host-cached reads, device-address consumers,
      descriptor-binding fallbacks, and zero-readback violations.

## OpenGL Backfill Additions

- [x] Report OpenGL buffer policy decisions in the same shape as Vulkan:
      generated, uploaded, resident/allocated, mapped, binding ready, readback
      ready, and retired.
- [x] Add diagnostics for immutable vs dynamic storage, persistent/coherent
      mapping, explicit flush, client storage, resize/growth, and upload route
      so OpenGL buffer behavior can be compared to Vulkan memory policy.
- [x] Track OpenGL upload and readback bytes by strategy and enforce the same
      no-readback rules for production GPU indirect and meshlet paths.
- [x] Keep scene-database buffer identity backend-neutral so OpenGL SSBO,
      bindless-buffer, or classic binding paths match Vulkan device-address
      consumers at the engine contract level.
- [x] Report OpenGL buffer cache/binding misses, SSBO/UBO block-name resolution,
      and vertex attribute binding diagnostics with the same names used by the
      Vulkan descriptor and geometry layout paths.

## Validation

- [x] Source test: event subscription symmetry matches `IApiDataBuffer`.
- [x] Unit/source test: `IsGenerated` changes with handle creation/recreation
      and destroy, while upload/readiness state changes with `PushData` and
      queued upload completion.
- [x] Unit/source test: `PushSubData` clamps/grows/falls back like OpenGL.
- [x] Unit/source test: mapping flags select expected Vulkan memory policy or
      emit expected diagnostics.
- [x] Unit/source test: SSBO block-name binding resolves to the same declared
      binding as OpenGL for representative shaders.
- [ ] Hardware: run compute skinning, GPUScene, indirect draw, readback, UI
      PBO/webview, and texture-buffer paths with Vulkan validation layers.
- [ ] Hardware: validate the Vulkan device-address consumer path and confirm the
      equivalent OpenGL path still renders through the shared buffer identity
      contract.
