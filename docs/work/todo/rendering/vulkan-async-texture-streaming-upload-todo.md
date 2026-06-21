# Vulkan Async Texture Streaming Upload TODO

Last Updated: 2026-06-21
Owner: Rendering
Status: implementation complete; validation pending
Related:
- [Vulkan Imported Texture Streaming TODO](vulkan-imported-texture-streaming-todo.md)
- [Vulkan Frame Loop Performance TODO](vulkan-frame-loop-performance-todo.md)
- [Texture Runtime, Streaming, And Virtual Texturing TODO](../texturing/texture-runtime-streaming-virtual-texturing-todo.md)

## Purpose

Move Vulkan texture streaming promotions and demotions off the render thread as
much as possible. Imported textures should stream in and out without visible
frame drops, while preserving Vulkan synchronization correctness, descriptor
lifetime safety, and deterministic fallback diagnostics.

The current dense Vulkan texture streaming path is synchronized and safer than
the old one-shot texture mutation path, but it still prepares too much work on
the render thread. This TODO tracks the next architecture step: a Vulkan-native
asynchronous upload pipeline with budgeted preparation, transfer queue execution,
deferred descriptor publication, and lazy retirement.

## Current Behavior

CPU texture decode, resize, and mip construction run off-thread through the
streaming manager. After that, Vulkan texture residency changes still perform
important work on the render thread before the upload is merely recorded into
the frame:

- `VulkanDenseTextureResidencyBackend` schedules the resident data handoff.
- `VulkanTextureUploadService.TryScheduleImportedTextureUpload(...)` resolves
  the `VkTexture2D` wrapper and prepares upload resources immediately.
- `VkImageBackedTexture.TryCreateSynchronizedImportedUpload(...)` applies
  resident data metadata, refreshes texture layout, creates replacement
  `VkImage`/memory, creates image views and samplers, creates per-mip staging
  buffers, normalizes upload data, and fills staging buffers.
- `TextureUploadFrameOp` later records transfer barriers and copy commands into
  the frame command buffer.
- Descriptor publication and old-resource retirement are ordered by the frame
  op, which is correct, but publication still dirties command/descriptors at the
  moment the uploaded texture becomes visible.

This means "async" currently means "CPU image processing is async and GPU copy
is frame ordered." It does not yet mean "Vulkan upload preparation has a tight
frame budget" or "large texture promotions cannot monopolize a frame."

## Implementation Notes - 2026-06-20

Implemented the conservative Vulkan compatibility version of the async upload
pipeline:

- Added diagnostic/runtime toggles for:
  - `XRE_VULKAN_ASYNC_TEXTURE_UPLOAD`;
  - `XRE_VULKAN_TEXTURE_UPLOAD_TRANSFER_QUEUE`;
  - `XRE_VULKAN_TEXTURE_UPLOAD_PREP_WORKER`;
  - `XRE_VULKAN_TEXTURE_UPLOAD_PREP_BUDGET_MS`;
  - `XRE_VULKAN_TEXTURE_UPLOAD_TRACE`;
  - existing Vulkan texture-streaming switches
    `XRE_VULKAN_PROGRESSIVE_TEXTURE_UPLOAD` and
    `XRE_VULKAN_IMPORTED_TEXTURE_PREVIEW_FREEZE`.
- Mirrored those toggles into editor diagnostic settings so they can be changed
  like the existing Vulkan diagnostics.
- Split imported texture upload scheduling from Vulkan upload preparation.
  Scheduling now accepts an immutable upload job and queues it by priority.
- Added a budgeted render-thread upload-prep drain using
  `RenderWorkBudgetCoordinator`; this keeps the current synchronized
  descriptor-publication semantics while pacing image/staging preparation.
- Added explicit `[Vulkan Compat]` logs when the requested worker-prep or
  transfer-queue paths are not active and the engine is using budgeted
  render-thread preparation / graphics-frame copy submission.

Implementation update, 2026-06-21:

- Split imported texture preparation into resumable steps inside
  `VkImageBackedTexture`: unpublished metadata/layout capture, destination
  image/memory allocation, view creation, optional sampler creation, one-mip
  staging creation/fill, and copy-region append.
- Added opt-in worker-side Vulkan upload preparation through a renderer-owned
  upload-context lock. Worker prep never publishes descriptors or touches frame
  command buffers.
- Added transfer-queue texture uploads when a dedicated transfer family exists.
  Uploads are submitted to transfer command buffers, synchronized with a fence,
  queue-family-released to graphics, acquired before publication, then published
  on the render thread.
- Returned retired upload staging buffers to `VulkanStagingManager` instead of
  destroying them. The current implementation uses whole-buffer pool reuse plus
  allocator/VMA suballocation rather than a new custom staging arena.
- Added texture-upload profiler counters for queue depth, active prep, transfer
  submissions, bytes in flight, stale cancels, failures, and prep/transfer/
  publication timing.
- Added per-texture descriptor generation separate from resident-data
  generation, with exact dirty reasons when command buffers must be dirtied.

## Likely Hitch Sources

- Render-thread image allocation and memory binding for large resident textures.
- Render-thread staging buffer allocation and host copy for one or more mips.
- Per-upload image view and sampler creation.
- Mip normalization/conversion when source data does not match the target
  Vulkan format.
- Command-buffer structural dirtying when descriptor-visible image state
  changes.
- Immediate demotion image replacement when a texture streams out.
- Burst behavior: several visible textures can request promotion in nearby
  frames, and "one upload per frame" does not cap the cost of a single upload.

## Success Criteria

- [ ] Texture residency promotions and demotions do not cause visible frame
  hitches in the Vulkan Sponza unit-testing scene.
- [ ] Render-thread work for each texture residency transition is reduced to
  cheap queue polling, publication checks, descriptor swap, command-buffer dirty
  marking, and retirement enqueue.
- [ ] Large image allocation, staging allocation, staging memcpy, and mip
  normalization can occur away from the render thread or inside a strict
  render-thread budget while a worker-owned upload context is introduced.
- [ ] GPU copy work can execute on a transfer queue when available, synchronized
  to graphics sampling with timeline semaphores or equivalent frame-timeline
  barriers.
- [ ] Descriptor publication occurs only after the GPU upload is complete and is
  generation-gated against stale streaming requests.
- [ ] Demotions avoid immediate destructive image replacement when possible;
  old resources are retained briefly and retired lazily.
- [ ] Telemetry can show queue depth, oldest wait, prep time, transfer time,
  publication time, bytes uploaded, and render-thread upload cost.
- [ ] The system has kill switches and single-thread fallbacks for debugging,
  but no silent CPU/OpenGL fallback hides a Vulkan upload failure.
- [ ] OpenGL behavior remains unchanged.

## Non-Goals

- [x] Do not implement full virtual texturing in the first phase.
- [x] Do not require true Vulkan sparse residency for dense texture streaming to
  become smooth.
- [x] Do not publish descriptors before the copied image is valid for shader
  sampling.
- [x] Do not mutate material bindings or descriptor-visible texture state from
  worker threads.
- [x] Do not introduce unbounded worker-side allocations that can exceed VRAM or
  system memory under streaming pressure.

## Target Architecture

### Stage 1: CPU Resident Data Build

Keep the existing off-thread decode path:

- source file read;
- ImageMagick decode;
- resize to target resident dimension;
- mip-chain construction when required;
- resident data cache lookup/store.

Output should become an immutable `TextureStreamingResidentData` package plus
streaming generation, target resident size, priority, estimated bytes, and
format metadata.

### Stage 2: Vulkan Upload Preparation Queue

Introduce a Vulkan upload preparation queue owned by
`VulkanTextureUploadService`.

The queue should accept resident data packages and turn them into prepared
upload packages. A prepared package contains:

- destination image description;
- allocated image/memory or deferred allocation request;
- image view and optional sampler description;
- staging buffer handles or staging allocation requests;
- per-mip `BufferImageCopy` regions;
- estimated and actual byte counts;
- generation and cancellation predicates;
- failure diagnostics.

Preferred direction:

- Allow Vulkan object/resource creation from a dedicated upload worker only
  after the renderer has a thread-safe device/allocation context.
- Until that is in place, run preparation as budgeted render-thread slices with
  hard millisecond and byte budgets.
- Keep all descriptor-visible publication on the render thread/frame timeline.

### Stage 3: Transfer Queue Execution

Record and submit upload copies through a dedicated transfer queue when the
device exposes one.

Requirements:

- queue-family ownership transfer when transfer and graphics queues differ;
- timeline semaphore value or frame-fence dependency for upload completion;
- graphics-side wait before the image can be sampled;
- fallback to graphics queue upload when no transfer queue is available;
- explicit logs when using graphics-queue compatibility mode.

The upload copy should not force primary scene command buffer re-recording until
publication is ready.

### Stage 4: Descriptor Publication

Publish the uploaded image only after:

- request generation is still current;
- cancellation token is not canceled;
- transfer has completed;
- image layout is shader-read compatible;
- descriptor update is legal for the material binding mode currently active.

Publication should:

- atomically swap the descriptor-visible image/view/sampler on the texture
  wrapper;
- increment a texture descriptor generation;
- mark only dependent descriptor sets/material table entries dirty;
- mark command buffers dirty only when descriptor-visible state truly changed;
- enqueue old image, view, sampler, and staging resources for frame-slot
  retirement.

### Stage 5: Demotion And Stream-Out

Demotion should be less eager than promotion:

- Prefer keeping the higher-res image alive for a grace window after the policy
  selects a smaller resident size.
- When memory pressure requires demotion, schedule it through the same upload
  pipeline instead of doing immediate render-thread replacement.
- Consider publishing a lower-res mip-chain only when the texture is not
  currently visible or has been offscreen long enough.
- Keep a "pending demotion" state so a near-immediate promotion can cancel it
  before any GPU work is committed.

## Work Plan

### Phase 0: Measurement Baseline

- [ ] Add a reproducible Vulkan texture-streaming hitch scenario using the
  unit-testing Sponza scene.
- [ ] Capture profiler logs while camera motion triggers texture promotions and
  demotions.
- [ ] Record current p50/p95/p99 for:
  - frame time;
  - render-thread texture upload prep;
  - texture upload command recording;
  - descriptor publication;
  - command buffer dirty/re-record time.
- [x] Add log grouping for upload lifecycle states by texture name, generation,
  publication token, and resident size.
- [ ] Confirm whether observed drops correlate with promotion, demotion, or both.

### Phase 1: Diagnostics And Feature Flags

- [x] Add `XRE_VULKAN_ASYNC_TEXTURE_UPLOAD=0/1` to enable the new pipeline.
- [x] Add `XRE_VULKAN_TEXTURE_UPLOAD_TRANSFER_QUEUE=0/1` to force-disable the
  transfer queue path for bisection.
- [x] Add `XRE_VULKAN_TEXTURE_UPLOAD_PREP_WORKER=0/1` to gate worker-side Vulkan
  preparation once it exists.
- [x] Add `XRE_VULKAN_TEXTURE_UPLOAD_PREP_BUDGET_MS=<float>` for render-thread
  compatibility preparation budget.
- [x] Add `XRE_VULKAN_TEXTURE_UPLOAD_TRACE=1` for verbose lifecycle logs.
- [x] Add editor/profiler counters:
  - pending resident data packages;
  - pending Vulkan prep packages;
  - active prep packages;
  - pending transfer submissions;
  - transfer queue bytes in flight;
  - pending descriptor publications;
  - canceled stale uploads;
  - failed uploads;
  - render-thread upload prep milliseconds;
  - worker upload prep milliseconds;
  - transfer wait milliseconds.

### Phase 2: Separate Policy Request From Vulkan Preparation

- [x] Split `VulkanTextureUploadService.TryScheduleImportedTextureUpload(...)`
  into request acceptance and upload preparation.
- [x] Add an immutable `VulkanTextureUploadJob` that owns resident data,
  generation, priority, target residency, and cancellation state.
- [x] Stop calling `VkImageBackedTexture.TryCreateSynchronizedImportedUpload(...)`
  directly from the scheduling call.
- [x] Queue accepted jobs and let the upload service drain them by priority and
  budget.
- [x] Keep generation rejection early so stale work is discarded before Vulkan
  resources are allocated.

### Phase 3: Budgeted Render-Thread Preparation Compatibility

- [x] Implement a short-term budgeted prep drain on the render thread.
- [x] Use `RenderWorkBudgetCoordinator` for upload prep, not just upload
  scheduling.
- [x] Split preparation into resumable steps:
  - apply texture metadata into an unpublished layout description;
  - allocate destination image/memory;
  - create image view;
  - create sampler;
  - create one staging buffer;
  - fill one staging buffer;
  - append one mip copy region.
- [x] Stop after the configured time/byte budget and resume next frame.
- [x] Preserve current synchronized publication semantics.
- [x] Emit `[Vulkan Compat]` logs while prep still runs on the render thread.

### Phase 4: Thread-Safe Vulkan Upload Worker

- [x] Audit which Vulkan renderer members are safe to touch off the render
  thread.
- [x] Add a dedicated `VulkanUploadContext` with explicit locking around:
  - device calls;
  - VMA or engine allocator state;
  - debug object naming;
  - global resource tracking dictionaries;
  - staging buffer pools.
- [x] Move image allocation and staging allocation into the upload worker when
  the upload context is enabled.
- [x] Keep descriptor publication, command-buffer dirty marking, and material
  table updates on the render thread.
- [x] Add asserts that worker code never touches active frame command buffers,
  render pass state, current FBO state, material draw state, or scene objects.
- [x] Add teardown handling so in-flight worker uploads are canceled and joined
  before device destruction.

### Phase 5: Staging Buffer Pool

- [x] Add a Vulkan staging buffer pool for texture uploads.
- [x] Reuse host-visible staging allocations across uploads.
- [x] Support suballocation with alignment and lifetime tracking.
- [x] Avoid one allocation per mip where possible.
- [x] Track mapped persistent memory ranges when legal.
- [x] Add budget caps for total staging bytes and bytes in flight.
- [x] Add defragmentation or retirement policy for oversized staging buffers.

### Phase 6: Transfer Queue Uploads

- [x] Detect a dedicated transfer queue and expose it in Vulkan device
  capabilities.
- [x] Add upload command pool(s) for transfer work.
- [x] Record copy commands into transfer command buffers on the upload worker
  or upload service thread.
- [x] Submit with timeline semaphore or fence-backed completion signaling.
- [x] Handle queue-family ownership transfer when needed.
- [x] Add graphics-side wait/import before descriptor publication or first use.
- [x] Fallback to graphics queue upload with an explicit compatibility log.
- [ ] Measure whether transfer queue submission improves frame time on target
  hardware.

### Phase 7: Descriptor Publication And Dirty Scope

- [x] Add per-texture descriptor generation separate from resident-data
  generation.
- [x] Publish new image/view/sampler only after transfer completion.
- [x] Update only affected descriptor sets or bindless table entries.
- [x] Avoid marking all command buffers dirty for texture uploads when descriptor
  indirection allows stable command buffers.
- [x] If command buffers must be dirtied, record the exact dirty reason and
  affected material/texture.
- [x] Add validation that no descriptor points at a retired image.

### Phase 8: Lazy Retirement And Demotion

- [x] Introduce an old-resource grace window after descriptor publication.
- [x] Retire old images only after all frame slots that could sample them have
  completed.
- [x] Queue demotion uploads at lower priority than visible promotions unless
  memory pressure is critical.
- [x] Cancel pending demotions when the texture becomes visible again.
- [x] Add memory-pressure override that can force demotion while preserving
  synchronization correctness.

### Phase 9: Sparse Residency / Virtual Texture Upgrade Path

- [x] Keep dense uploads as the default path until the async pipeline is stable.
- [x] Design true Vulkan sparse image residency separately:
  - sparse image creation;
  - memory page allocation;
  - `vkQueueBindSparse`;
  - sparse mip tail handling;
  - partial page selection;
  - residency feedback.
- [x] Make the existing `SparseTextureStreamingTransitionRequested` Vulkan
  compatibility path route into the same async pipeline for dense uploads.
- [x] Preserve clear logs that dense sparse-compat is not true page residency.

### Phase 10: Validation

- [x] Build the editor.
- [ ] Run Vulkan unit-testing Sponza with texture streaming enabled.
- [ ] Capture before/after profiler logs for camera sweeps that force
  promotions and demotions.
- [ ] Verify no `ErrorDeviceLost`, upload VUID, stale descriptor sample, or black
  texture frame appears.
- [ ] Compare OpenGL behavior to confirm shared policy remains unchanged.
- [ ] Validate transfer-queue enabled and disabled modes.
- [ ] Validate worker-prep enabled and disabled modes.
- [ ] Validate device shutdown while uploads are queued.
- [ ] Validate texture reload/import while uploads are queued.
- [ ] Validate memory pressure demotion and cancellation under fast camera
  movement.

## Files Likely To Change

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanTextureUploadService.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/VulkanTextureStreamingHooks.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/LogicalDevice.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/Textures/VkImageBackedTexture.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/Textures/VkTexture2D.cs`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Objects/Types/Textures/VulkanDenseTextureResidencyBackend.cs`
- `XREngine.Runtime.Rendering/Objects/Textures/2D/ImportedTextureStreamingManager.cs`
- `XREngine.Runtime.Rendering/Runtime/RenderWorkBudgetCoordinator.cs`
- `XREngine/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs`
- profiler/runtime diagnostics files for new counters and NDJSON fields.

## Risks

- Vulkan object creation off-thread is legal only if engine-side allocator and
  tracking state are explicitly synchronized.
- Transfer queue ownership transfers can introduce subtle layout hazards.
- Descriptor publication bugs can sample destroyed images or uninitialized
  images.
- Excessive command-buffer dirtying can erase the gains from async upload.
- Keeping old images alive for lazy retirement can increase transient VRAM
  pressure.
- Worker cancellation during device loss or editor shutdown must be exact.

## Implementation Notes

- Treat failures as visible diagnostics, not silent fallback.
- Keep a conservative single-thread path until worker prep and transfer queue
  paths are proven.
- Prefer immutable upload job objects so stale generations can be canceled
  without touching live texture state.
- Keep render-thread publication small, deterministic, and easy to profile.
- Preserve the existing synchronized publication model while moving heavy prep
  and copy execution away from the render thread.
- Any step that still runs on the render thread must be budgeted by time and
  bytes, not merely "one upload per frame."

## Done Criteria

- [ ] A large visible texture promotion no longer causes a noticeable frame drop
  in the Vulkan editor.
- [ ] Texture stream-out/demotion no longer causes a noticeable frame drop.
- [ ] Render-thread upload prep p95 is below 0.5 ms during steady camera
  movement and below 1.0 ms during active streaming bursts on the target scene.
- [ ] Texture upload transfer work is visible in telemetry but does not block
  graphics recording except at descriptor publication.
- [ ] Descriptor publication is generation-gated, frame-safe, and validated.
- [x] All compatibility paths log clearly when they are used and explain the
  preferred Vulkan-native path.
