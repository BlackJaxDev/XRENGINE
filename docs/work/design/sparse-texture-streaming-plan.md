# Sparse Texture Streaming Plan

## Implementation Status

| Phase | Description | Status |
|-------|-------------|--------|
| 0 | Stabilize current first pass | Complete |
| 1 | Separate policy from physical residency | Complete |
| 2 | Cooked mip-addressable texture assets | Complete |
| 3 | Sparse mip residency for OpenGL | Implemented (render-thread path) |
| 4 | Move uploads off the main render path | Implemented (shared-context sparse promotion path) |
| 5 | Improve residency heuristics | Implemented |
| 6 | Optional page-level sparse residency | Implemented (UV-bounds-driven page regions) |

---

## 1. Executive Summary

This document defines the target texture streaming architecture for XRENGINE so imported scenes can stay within a tracked VRAM budget without freezing the render thread or forcing full-resolution texture uploads during model import.

Phases 0–2 are substantially complete. The system now has:

- A dedicated `ImportedTextureStreamingManager` owning all streaming policy, separate from `XRTexture2D`.
- A pluggable `ITextureResidencyBackend` interface with a fully implemented `GLTieredTextureResidencyBackend` fallback.
- A cooked `XRTexture2D` streaming payload format (`XRTexture2D.StreamingPayload.cs`) with per-mip offset metadata and an explicit preview mip index, enabling mip reads without full PNG decode.
- Per-frame promotion and demotion throttling, per-texture telemetry, and unit tests covering budget fitting, projected-importance heuristics, and sparse page-byte accounting.

Phases 5 and 6 now extend that baseline with runtime view-aware inputs and partial sparse page commits:

- `RenderableMesh` supplies projected pixel span, approximate screen coverage, cached UV-density hints, and normalized UV bounds for imported texture usage.
- `ImportedTextureStreamingManager` ranks textures by projected on-screen importance, applies sampler-role multipliers, preserves short visibility grace windows, and gives each asset group a fair first promotion slot before filling the remaining frame budget.
- Streaming usage is recorded only from the main non-shadow view so reflections, scene captures, and probe passes do not prevent off-screen demotion or steal first-promotion slots from textures directly in view.
- Higher-detail promotions apply a short transient lod-bias fade so new mip residency sharpens in over a few frames instead of popping immediately.
- Sparse imported textures can keep only the page-aligned regions touched by the visible submesh UV bounds for high-detail mips, while the sparse mip tail remains fully committed.
- The first shipping page-level implementation slices page-sized upload regions directly out of the existing cooked mip blobs, so it does not require a separate page-table asset format.

The remaining Phase 0 cleanup items are complete: the streaming state queue recompacts dead `WeakReference` entries, and resident-byte estimation is format-aware.

The long-term target remains a stable full-size logical texture object whose mip residency changes over time under a streaming budget. In OpenGL terms, this means a sparse residency path when supported, with a robust fallback when it is not.

This plan also separates two concerns that are easy to conflate:

1. GPU residency control.
2. Source asset decoding and mip delivery.

Sparse residency solves the first problem. It does not by itself solve the second. PNG is still a poor on-demand source format because higher-resolution data usually requires a full CPU decode before mip generation. Real streaming therefore also needs a cooked texture asset format with individually addressable mips, and eventually page-addressable data for very large textures.

## 1.1 Runtime Texture Scheduler Contract

The texture-management runtime now exposes renderer-neutral residency and upload telemetry records in `TextureRuntimeTelemetry.cs`. OpenGL still owns the first implementation, but policy should move through backend-neutral fields: resident max dimension, base mip, level count, page selection, estimated committed bytes, source version/hash, queue wait, execution time, and storage generation.

Cooked texture metadata should keep enough addressing data for the upload scheduler to request work without reopening or decoding the whole source: per-mip offsets, optional sparse page offsets, source version/hash, and resident-byte estimates by format. Future BCn or neural-compressed assets should report bytes in compressed resident units and upload chunks in the scheduler's neutral `TextureUploadTelemetry`, leaving backend-specific block/page math behind the residency backend.

The Vulkan renderer has placeholder hooks for staged image upload progress, sparse image residency, and generation-gated cancellation. Those hooks should consume the same telemetry contract rather than adding Vulkan-only policy fields above the backend layer.

## 2. Problem Statement

## 2.1 Current problems

- Large FBX imports can overlap mesh construction, PNG decode, mip generation, and GPU upload in the same startup window. *(Mitigated by phases 0–2: preview-only uploads during import, promotions gated until import completes.)*
- The current OpenGL texture path allocates storage from the requested width, height, and mip count, so a normal full-size texture object still consumes the full chain even if sampling is temporarily clamped to lower-detail mips. *(Remains true — requires Phase 3 sparse residency.)*
- PNG decode is expensive and monolithic. Reaching a higher resident tier can still require decoding the entire source image on CPU. *(Resolved by Phase 2 cooked streaming payload format.)*
- Upload work is still constrained by OpenGL context ownership. Even when decode happens off-thread, residency transitions still need a GPU context and can stall presentation if not scheduled carefully. *(Phase 4 now routes sparse promotions through the shared GL context with fence-gated exposure; initial sparse allocations and demotions remain render-thread work.)*
- The current first-pass streaming model replaces resident texture content with different-size allocations. That saves VRAM, but it does not preserve a stable logical full-size texture representation. *(Remains true — requires Phase 3 sparse residency.)*

## 2.2 Why the first pass is not the final answer

The current tiered system is a good fallback and a useful stepping stone, but it has limitations:

- Logical texture size changes with resident size.
- Promotions can require reallocating and reuploading a new resident tier.
- It is coarser than true per-mip or per-page residency.
- Sampling state must be managed around the resident tier, rather than around a stable logical texture with changing residency.

What we actually want is:

- A stable texture handle bound by materials from import to shutdown.
- Preview-only residency during import.
- Budgeted promotion after import based on view relevance.
- Budgeted demotion when textures leave view or become unimportant.
- Minimal render-thread work.
- A path that eventually supports sparse per-mip or per-page residency instead of reallocating whole resident tiers.

## 3. Goals

- Keep model import responsive by uploading only preview-quality texture data during import.
- Enforce a tracked VRAM budget for imported content.
- Choose residency based on view frustum visibility, projected importance, and distance to the visible submesh.
- Preserve stable material bindings while residency changes underneath them.
- Move expensive CPU work off the render thread.
- Support asynchronous GPU upload through a secondary shared context when available.
- Allow fallback to the existing tiered-resident implementation on hardware that does not support sparse textures.
- Build toward cooked, mip-addressable texture assets instead of relying on raw PNG as the streaming source.

## 4. Non-Goals

- This document does not require a full megatexture or virtual-texturing atlas system in the first implementation.
- This document does not require sparse residency for every texture type on day one. Imported `XRTexture2D` content is the first target.
- This document does not remove the current tiered-resident implementation immediately. It remains the compatibility fallback.
- This document does not assume bindless textures are required.
- This document does not force Vulkan parity in the first phase, though the runtime abstractions should avoid painting Vulkan into a corner.

## 5. Design Principles

1. Stable logical texture identity, changing residency.
2. Preview-first import behavior.
3. No full-resolution eager uploads during model import.
4. Budget decisions are frame-driven and visibility-driven.
5. Decode, transcode, and upload are separate pipeline stages.
6. Sparse residency is the preferred GPU implementation when supported.
7. Tiered resident textures remain a fallback backend, not the target architecture.
8. PNG is a source input format, not the long-term streaming format.
9. Residency transitions must be rate-limited and hysteresis-based to prevent thrash.
10. Telemetry is part of the feature, not a later add-on.

## 6. Terminology

- Logical texture: the full source texture dimensions and mip chain as authored.
- Resident mip: a mip level currently backed by GPU memory.
- Resident range: the currently committed mip interval for a sparse texture.
- Preview mip set: the low-detail mip levels equivalent to a `64x64` maximum dimension.
- Tiered resident fallback: the current system that swaps actual resident texture sizes such as `64`, `128`, `256`, `512`, and so on.
- Streaming source asset: the cooked asset on disk that can deliver individual mips or pages without decoding the original PNG every time.

## 7. Target Architecture

## 7.1 Runtime split: logical texture vs residency backend

`XRTexture2D` should evolve from "owns raw mipmap array and uploads all of it" into two distinct concerns:

- Logical texture description
  - Source width and height
  - Pixel format / compression format
  - Full mip count
  - Streaming source handle
  - Material-visible identity

- Residency backend
  - Current resident mip range or resident pages
  - Pending promotion or demotion requests
  - Estimated committed VRAM bytes
  - GPU capability path
  - Upload queue state

Runtime objects — implemented names in parentheses:

- `TextureStreamingManager` → `ImportedTextureStreamingManager` *(implemented)*
- `TextureStreamingRecord` → inner `ImportedTextureStreamingRecord` on manager *(implemented)*
- `TextureStreamingSource` → `ITextureStreamingSource` with `AssetTextureStreamingSource` and `ThirdPartyTextureStreamingSource` *(implemented)*
- `ITextureResidencyBackend` *(implemented)*
- `GLSparseTextureResidencyBackend` *(implemented)*
- `GLTieredTextureResidencyBackend` *(implemented)*

The streaming manager decides what should be resident. The backend decides how that residency becomes real GPU storage.

## 7.2 OpenGL sparse-residency path

When sparse textures are supported by the active OpenGL device, imported textures should allocate one logical full-size texture object and initially commit only the low-detail mip range needed for preview.

Example for a `4096x4096` texture:

- Logical texture exists with mip levels `0..12`.
- During import, commit only mip levels `6..12`, where mip `6` is `64x64`.
- Clamp sampling to the resident range so missing higher-detail mips are never sampled. This is not a quality hint — it is a safety requirement. Sampling an uncommitted sparse page is undefined behavior in OpenGL and manifests differently across drivers (black, garbage data, or a crash). `GL_TEXTURE_BASE_LEVEL` must always be kept equal to the lowest committed mip index before any sampling occurs.
- After import, as budget and view priority allow, lower the base resident mip from `6` toward `0`.
- Demotion does the reverse: raise `GL_TEXTURE_BASE_LEVEL` to the new base, then uncommit the high-cost detail mips that are no longer accessible.

This gives us one stable texture handle and varying VRAM commitment.

### Sparse mip tail constraint

Not all mip levels can be committed individually. Hardware sparse page sizes (commonly `128x128` or `64x64` texels depending on format) mean that the smallest mip levels fall into a *mip tail* that must be committed as a single unit and cannot be individually managed.

The number of individually sparse-manageable levels is available via:

```
glGetTexParameteriv(target, GL_NUM_SPARSE_LEVELS_ARB, &numSparseLevels)
```

Only mip levels `0` through `numSparseLevels - 1` can be committed or uncommitted independently. Mip levels from `numSparseLevels` onward form the mip tail and are always resident together once committed. Promotion and demotion logic must query this value per texture and per format and treat the tail as an atomic unit. Never assume a fixed tail boundary based on dimensions alone.

### Sparse allocation ordering constraint

`GL_TEXTURE_SPARSE_ARB` must be set to `GL_TRUE` **before** calling `glTextureStorage2D`. OpenGL immutable storage, once allocated, cannot be made sparse retroactively. The correct sequence is:

1. Generate texture name.
2. Bind or reference the texture.
3. Set `GL_TEXTURE_SPARSE_ARB = GL_TRUE` via `glTextureParameteri`.
4. Call `glTextureStorage2D` with the full logical dimensions and full mip count.
5. Commit only the preview mip range via `glTexPageCommitmentARB`.

This is a structural departure from the current `GLTexture2D.EnsureStorageAllocated` path, which allocates immutable storage eagerly without sparse flags. The `GLSparseTextureResidencyBackend` must intercept storage allocation before any upload occurs, not wrap the existing upload flow.

Longer-term extension:

- Very large textures can move from per-mip sparse residency to per-page sparse residency.
- That allows partial residency inside a mip level instead of forcing the entire mip to be resident.
- Per-page residency is phase two or later, not the first sparse implementation.

## 7.3 Fallback path on unsupported hardware

If sparse residency is not supported:

- Keep the current tiered resident texture path.
- Preserve the same streaming manager, budget solver, and visibility heuristics.
- Only swap the residency backend.

This keeps the policy layer shared even when the physical allocation strategy differs.

### Non-power-of-two textures always use the fallback

OpenGL sparse textures require each dimension to be a multiple of the hardware's sparse page size for that format. Non-power-of-two (NPOT) textures frequently fail this alignment check. Attempting to set `GL_TEXTURE_SPARSE_ARB` on a texture whose dimensions are not page-aligned for the chosen format will either generate an error or silently ignore the flag on some drivers.

Rather than probing per-texture, the implementation should check eligibility before selecting the backend: if either dimension of the logical texture is not a multiple of `GL_VIRTUAL_PAGE_SIZE_X_ARB` and `GL_VIRTUAL_PAGE_SIZE_Y_ARB` for the chosen format, assign `GLTieredTextureResidencyBackend` regardless of extension support.

## 7.4 Streaming source asset format

Sparse residency is only half of the system. We also need a source asset format that can cheaply deliver the exact mip data requested.

Raw PNG is not sufficient for shipping-quality streaming because:

- It is CPU-expensive to decode.
- It is not mip-addressable on disk.
- Generating higher-detail mips usually requires the full image decode.

Target source format requirements:

- Stores logical dimensions and full mip count.
- Stores each mip as an individually addressable blob.
- Supports direct loading of preview mips without touching full-resolution data.
- Supports a future page table for very large textures.
- Preferably stores GPU-friendly compressed data such as BCn where platform support is known.

The cooked streaming payload format is implemented in `XRTexture2D.StreamingPayload.cs`. It uses magic `0x58525453` (XRTS), stores per-mip descriptors with absolute byte offsets, and stores a `previewBaseMipIndex` for fast import-time load. Mip reads seek directly to the requested offset and do not trigger full PNG decode. The format currently stores uncompressed RGBA8 mip blobs.

### Cooked format compression decision

The first cooked streaming format stores **uncompressed RGBA8 mip blobs**. BCn-compressed mip blobs are a later upgrade and must not be assumed in the initial implementation for two reasons:

1. The base `GL_ARB_sparse_texture` extension does not support compressed formats for per-page commitment. Committing individual mip pages of a BCn-compressed sparse texture requires `GL_ARB_sparse_texture2`, which has narrower hardware support. Using RGBA8 keeps the cooked format and the sparse residency path on the same universally-supported extension tier.
2. RGBA8 is simpler to validate: byte counts are predictable, mip sizes are unambiguous, and there is no block-alignment padding to reason about.

BCn support should be added as an explicit upgrade in a later phase, gated on `GL_ARB_sparse_texture2` presence for the sparse path and with a confirmed RGBA8 fallback for hardware that lacks it.

## 7.5 Import pipeline behavior

During model import:

1. Create logical texture records immediately.
2. Decode or load only the preview-equivalent mip set.
3. Upload only that preview residency.
4. Register each texture with the streaming manager.
5. Mark promotions as blocked while the model import scope is active.

After model import completes:

1. Release the import-scope promotion gate.
2. Re-evaluate visible imported textures.
3. Begin promotions in priority order under budget.

Important rule:

- Import completion should unblock promotions, not force them.
- Promotion still has to compete for VRAM budget and upload bandwidth.

## 7.6 Visibility and priority model

Residency decisions should be driven by submesh visibility, not just by whether a texture exists in memory.

Priority inputs:

- Visible this frame.
- Recently visible.
- Distance from camera to visible submesh bounds.
- Projected screen size of the submesh.
- Texture role importance.
- Current resident detail vs desired detail.
- Estimated upload cost.
- Hysteresis cooldown to avoid rapid oscillation.

Initial heuristic can stay simple:

- Visible and near: promote aggressively.
- Visible and far: keep mid or low detail.
- Recently visible: hold for a short grace period.
- Long-unseen: demote toward preview.

Longer-term desired heuristic:

- Replace raw distance buckets with projected texel density.
- Incorporate UV density hints where available.
- Bias normal maps and roughness maps differently if needed.

## 7.7 Budget model

We need three budgets, not one:

1. Resident texture VRAM budget.
2. Staging / upload budget per frame.
3. CPU decode memory budget.

The current tracked VRAM stats can remain the base signal for resident GPU memory, but the streaming manager should also track:

- Bytes currently committed by streamable textures.
- Bytes requested but not yet committed.
- Bytes currently staged in CPU memory.
- Upload bytes scheduled this frame.

Budget solver requirements:

- Highest-priority visible textures get first claim.
- Promotions can be denied if they would evict too much more important visible content.
- Demotions should happen before a promotion is rejected when lower-priority content is holding budget.
- The manager should cap promotions per frame and upload bytes per frame.

## 7.8 Threading model

The system should become a three-stage pipeline:

1. Policy stage
   - Runs on update / collect-visible side.
   - Computes desired residency.
   - No GPU calls.

2. CPU data stage
   - Runs on worker threads.
   - Reads mip blobs from cooked assets.
   - Decodes or transcodes data as needed.
   - Prepares upload payloads.

3. GPU commit stage
   - Runs on a valid GPU context.
   - Prefer the secondary shared context for uploads.
   - Falls back to throttled render-thread work when necessary.

The render thread should only be the mandatory last-resort submission point, not the place where decode, policy, or queue orchestration live.

### Thread safety for TextureStreamingRecord

Each `TextureStreamingRecord` is read by the policy stage and written by the CPU data stage and GPU commit stage. The locking strategy is:

- A per-record `object` lock (`ImportedTextureStreamingRecord.Sync`) guards all mutable fields.
- The policy stage takes a snapshot of each record under the lock and operates on the snapshot, never holding the lock across budget or visibility computations.
- The GPU commit stage acquires the lock only to write committed mip range and byte counts after a successful upload or demotion.
- No field on `TextureStreamingRecord` is written without holding the record lock. Volatile reads of frame IDs and import scope counters are acceptable for fields that are only ever widened (never partially updated).

This protocol is enforced in the current `ImportedTextureStreamingManager` implementation.

## 7.9 GPU upload path

Sparse-residency uploads should support:

- Committing mip ranges or pages before upload.
- Uploading only the mip data being promoted.
- Uncommitting detail mips during demotion.
- Rate-limited PBO-backed upload batches.

Preferred execution order:

1. Background worker prepares upload payload.
2. Secondary context sets `GL_TEXTURE_BASE_LEVEL` to the current resident base (so no uncommitted mips are accessible during transition), then commits the new mip range via `glTexPageCommitmentARB`, then uploads texture data.
3. Secondary context inserts a `glFenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0)` after the upload.
4. Residency backend stores the fence object and the new committed mip range on the record.
5. On the next render-thread frame, the renderer calls `glWaitSync` (GPU-side wait) or `glClientWaitSync` (CPU-side poll) against the fence before lowering `GL_TEXTURE_BASE_LEVEL` to expose the newly committed detail. The fence must signal before `GL_TEXTURE_BASE_LEVEL` is changed, or the new mip data may be sampled before the upload is visible.
6. Once the fence has signaled, update `GL_TEXTURE_BASE_LEVEL` and discard the fence object.

Cross-context synchronization is not optional. Omitting fence objects when the upload context and render context share a texture will cause the render context to sample partially-written or uncommitted pages.

## 7.10 Telemetry and tooling

The feature needs first-class introspection:

- Per-texture resident mip range.
- Per-texture committed bytes.
- Requested vs granted mip level.
- Promotion and demotion counts.
- Decode queue depth.
- Upload queue depth.
- Nonresident sample warnings in debug mode.
- VRAM budget usage split by streaming vs non-streaming allocations.

`ImportedTextureStreamingTelemetry` and `ImportedTextureStreamingTextureTelemetry` structs are implemented and exposed via `GetTelemetry()` and `GetTrackedTextureTelemetry()`.

Editor tooling should eventually expose:

- A texture streaming overlay.
- A "why is this texture at this mip?" inspector.
- A "pin full resident" debug override.
- A global "freeze streaming state" switch for diagnosis.

## 8. Proposed Data Model

## 8.1 `TextureStreamingRecord`

Stores runtime state for one logical texture. Implemented as inner class `ImportedTextureStreamingRecord` on `ImportedTextureStreamingManager`.

Fields present:

- Stable texture weak reference
- Source path / cooked asset handle
- Logical width and height
- Preview base mip (derived from 64px threshold)
- Current resident max dimension
- Pending max dimension
- Last visible frame ID
- Min visible distance this frame
- Preview ready flag
- Pending load cancellation token source

## 8.2 `TextureStreamingSource`

Abstracts disk access. Implemented as `ITextureStreamingSource` with:

- `AssetTextureStreamingSource` — loads from cooked `.asset` files via `XRTexture2D.StreamingPayload`
- `ThirdPartyTextureStreamingSource` — loads from raw image files via ImageMagick (explicit second-class fallback)

Factory: `TextureStreamingSourceFactory.Create()` selects implementation based on file path.

## 8.3 `ITextureResidencyBackend`

Backend operations implemented on `ITextureResidencyBackend`:

- `Name`
- `PreviewMaxDimension`
- `ActiveDecodeCount`
- `QueuedDecodeCount`
- `UploadBytesScheduledThisFrame`
- `EstimateCommittedBytes(...)`
- `GetNextLowerResidentSize(...)`
- `SchedulePreviewLoad(...)`
- `ScheduleResidentLoad(...)`

Implementations:

- `GLTieredTextureResidencyBackend` *(implemented)*
- `GLSparseTextureResidencyBackend` *(not yet implemented — Phase 3)*

## 9. Phased Implementation Plan

## Phase 0: Stabilize the current first pass ✓ Substantially complete

Purpose:

- Keep current import behavior useful while the long-term system is built.

Work:

- [x] Keep preview-only import uploads at `64x64`.
- [x] Keep promotions blocked while any imported model scope is active.
- [x] Tighten promotion and demotion throttling. (`MaxResidentTransitionsPerFrame = 4`, `MaxPromotionTransitionsPerFrame = 2`, `MinTransitionCooldownFrames = 2`, `MaxPromotionBytesPerFrame = 24MB`)
- [x] Add telemetry for current resident tier, request queue depth, and upload bytes per frame. (`ImportedTextureStreamingTelemetry`, `ImportedTextureStreamingTextureTelemetry`)
- [x] Add tests for budget fitting and visibility-based tier selection. (`ImportedTextureStreamingPhaseTests`)
- [x] Prune dead `WeakReference` entries from the streaming state queue. `CompactRecordRefs()` runs every 600 frames from `Evaluate()`, draining the queue and re-enqueuing only live entries.
- [x] Make `EstimateResidentBytes` format-aware. Added `ESizedInternalFormat format = ESizedInternalFormat.Rgba8` parameter; all callers default to RGBA8 today but can pass BCn formats when they are introduced.

Deliverable:

- Reliable tiered streaming fallback with better diagnostics.

## Phase 1: Separate policy from physical residency ✓ Complete

Purpose:

- Make the streaming manager own decisions while the backend owns implementation details.

Work:

- [x] Introduce `TextureStreamingManager` → `ImportedTextureStreamingManager`.
- [x] Introduce `TextureStreamingRecord` → inner `ImportedTextureStreamingRecord`.
- [x] Introduce `ITextureResidencyBackend`.
- [x] Move current imported-texture policy code out of `XRTexture2D` into manager-owned structures.
- [x] Keep `XRTexture2D` as the material-facing texture object.
- [x] Port the current tiered fallback to `GLTieredTextureResidencyBackend`.
- [x] Enforce per-record lock protocol: snapshot under lock, no lock held across budget computations, GPU commit writes under lock.

Deliverable:

- Shared streaming policy layer with pluggable residency backends.

## Phase 2: Introduce cooked mip-addressable texture assets ✓ Complete

Purpose:

- Stop using PNG as the streaming source of truth.

Work:

- [x] Add a streamable texture container with per-mip offset metadata. (`XRTexture2D.StreamingPayload.cs`, magic `0x58525453`, per-mip descriptor table with absolute offsets)
- [x] Store the preview mip set explicitly in cooked assets. (`previewBaseMipIndex` field)
- [x] Initial cooked format stores uncompressed RGBA8 mip blobs only. (No BCn; gated for later on `GL_ARB_sparse_texture2`)
- [x] Add an import pipeline step that produces streamable cooked textures from PNG source assets. (`CreateTextureStreamingPayload()`)
- [x] Validate that requested mip reads do not trigger full source PNG decode in the cooked path. (`ReadStreamableMipmaps()` seeks directly to requested mip offset; covered by `StreamableTexturePayload_ReadsOnlyRequestedResidentSlice` test)
- [x] Keep migration path so existing projects can still import from raw assets. (`ThirdPartyTextureStreamingSource` PNG fallback)

Deliverable:

- Requesting mip data no longer requires full PNG decode.

## Phase 3: Implement sparse mip residency for OpenGL

Purpose:

- Preserve one full logical texture handle while varying committed mip detail.

Status note:

- Implemented. Sparse textures now probe capability/page size at OpenGL startup, choose sparse vs tiered residency per imported texture, allocate sparse storage with `GL_TEXTURE_SPARSE_ARB` before immutable storage, query `GL_NUM_SPARSE_LEVELS_ARB`, commit preview/detail mip ranges with mip-tail handling, and account only committed bytes toward texture VRAM stats.
- Phase 4 adds the shared-context upload path and cross-context fence handoff for sparse promotions. Preview bootstrap allocations and demotions still run on the render thread by design, but newly committed detail mips are not exposed until the render thread observes a signaled fence.

Work:

- [x] Probe for `GL_ARB_sparse_texture` and `GL_ARB_sparse_texture2` separately at renderer initialization. Log which tier is available. Use `GL_ARB_sparse_texture` for RGBA8 uncompressed sparse paths and gate BCn sparse support explicitly on `GL_ARB_sparse_texture2`.
- [x] Query `GL_VIRTUAL_PAGE_SIZE_X_ARB` and `GL_VIRTUAL_PAGE_SIZE_Y_ARB` per format during initialization. Use these values to determine whether a given texture's dimensions are page-aligned before assigning the sparse backend. Textures that fail the alignment check get `GLTieredTextureResidencyBackend` regardless of extension support.
- [x] Implement `GLSparseTextureResidencyBackend`. Storage allocation must follow the strict ordering: set `GL_TEXTURE_SPARSE_ARB = GL_TRUE`, then call `glTextureStorage2D`. This is a structural change from the existing `EnsureStorageAllocated` path and cannot be bolted onto it.
- [x] After sparse storage allocation, query `GL_NUM_SPARSE_LEVELS_ARB` and store the result on the record. Treat all mip levels at and above this index as the mip tail; commit and uncommit the tail as an atomic unit.
- [x] Commit only preview-equivalent low-detail mips during import. For mip tail levels that fall within the preview range, commit the tail as a unit.
- [x] Support promoting by raising the committed range and then lowering `GL_TEXTURE_BASE_LEVEL` only after the cross-context fence signals (see Phase 4).
- [x] Support demoting by raising `GL_TEXTURE_BASE_LEVEL` first, then uncommitting the now-inaccessible high-detail mips.
- [x] Never lower `GL_TEXTURE_BASE_LEVEL` to expose a mip that has not yet been committed and uploaded. This is a correctness invariant, not a best-effort guard — sampling uncommitted sparse pages is undefined behavior.
- [x] Update VRAM accounting to use committed bytes (committed mip range × format bytes per texel), not logical full-chain bytes.

Deliverable:

- Sparse mip streaming on supported OpenGL hardware, with correct mip tail handling and page-alignment fallback.

## Phase 4: Move uploads off the main render path

Purpose:

- Prevent texture residency changes from stalling presentation.

Work:

- [x] Route sparse promotion upload jobs through the shared GPU context when available, with synchronous render-thread fallback when async offload is unavailable or unsafe.
- [x] After each shared-context sparse upload, insert `glFenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0)` and store the sync object on the `TextureStreamingRecord`.
- [x] On the render thread, poll pending fence objects each frame. Only after a fence signals: lower `GL_TEXTURE_BASE_LEVEL` on the texture to expose newly resident mips and discard the fence object. Never lower `GL_TEXTURE_BASE_LEVEL` before the corresponding fence signals.
- [x] Keep upload pressure bounded with the existing per-frame promotion-byte cap plus an explicit in-flight shared-upload limit.
- [x] Add cancellation and coalescing so stale promotion requests do not waste bandwidth.
- [x] Add explicit back-pressure when decode or upload queues run too deep.

Deliverable:

- Upload work is mostly decoupled from swap-chain rendering, with correct cross-context synchronization.

## Phase 5: Improve residency heuristics

Purpose:

- Make the budget solver spend detail where it matters on screen.

Work:

- [x] Replace pure distance buckets with projected screen-space importance.
- [x] Add visibility grace periods and promotion cooldowns.
- [x] Add per-texture-role importance multipliers.
- [x] Add optional UV-density hints where mesh import can provide them.
- [x] Add scene-level fairness so one asset pack cannot starve everything else.

Deliverable:

- More stable, view-aware texture quality under budget pressure.

## Phase 6: Optional page-level sparse residency

Purpose:

- Support very large textures more efficiently than whole-mip commits.

Work:

- [x] Add runtime page request tracking and page-level residency state on sparse imported textures.
- [x] Promote only the page-aligned regions touched by the visible submesh UV bounds inside partially visible high-detail mips.
- [x] Surface page-coverage telemetry in the texture streaming debug panel.
- [x] Reuse the existing cooked mip blobs by slicing page-sized upload regions on demand; dedicated asset-side page tables remain optional future work rather than a blocker for page-level sparse residency.

Deliverable:

- Fine-grained sparse residency for very large textures.

## 10. TODO Checklist

## 10.1 Immediate TODOs (Phase 0 remainders)

- [x] Move current imported streaming policy out of `XRTexture2D` into a dedicated manager type.
- [x] Define `TextureStreamingRecord`, `TextureStreamingSource`, and `ITextureResidencyBackend`.
- [x] Document and enforce the per-record lock protocol on `TextureStreamingRecord`: policy stage snapshots under lock, GPU commit stage writes under lock, no lock held across budget or visibility computations.
- [x] Port the current tiered implementation into `GLTieredTextureResidencyBackend`.
- [x] Add per-texture telemetry for current resident tier, desired tier, and committed bytes.
- [x] Add a debug overlay for imported texture streaming state in the editor. (`EditorImGuiUI.TextureStreamingPanel.cs`, accessible via View → Texture Streaming)
- [x] Compact the streaming state queue during `Evaluate()` to remove entries whose `WeakReference` targets have been collected. (`CompactRecordRefs()` called every 600 frames from `Evaluate()`)
- [x] Make `EstimateResidentBytes` format-aware so it computes actual bytes per pixel (or bytes per block for compressed formats) instead of hardcoding `Rgba8`. (Added `ESizedInternalFormat format = ESizedInternalFormat.Rgba8` parameter)

## 10.2 Asset TODOs

- [x] Decided on cooked format: extended `XRTexture2D` with a dedicated streamable texture payload (`XRTexture2D.StreamingPayload.cs`).
- [x] Add per-mip offset metadata to the cooked format.
- [x] Store the preview mip set explicitly in cooked assets.
- [x] Initial cooked format stores uncompressed RGBA8 mip blobs. BCn blobs deferred until `GL_ARB_sparse_texture2` support is confirmed.
- [x] Add a build or import pipeline step that produces streamable cooked textures from PNG source assets.
- [x] Add validation that requested mip reads do not trigger full source PNG decode in the cooked path.

## 10.3 OpenGL TODOs

- [x] Probe for `GL_ARB_sparse_texture` and `GL_ARB_sparse_texture2` separately at renderer initialization. Log which tier is available. Use `GL_ARB_sparse_texture` for RGBA8 uncompressed sparse paths and gate BCn sparse support explicitly on `GL_ARB_sparse_texture2`.
- [x] Query `GL_VIRTUAL_PAGE_SIZE_X_ARB` and `GL_VIRTUAL_PAGE_SIZE_Y_ARB` per format during initialization. Use these values to determine whether a given texture's dimensions are page-aligned before assigning the sparse backend.
- [x] Implement `GLSparseTextureResidencyBackend`. Storage allocation must set `GL_TEXTURE_SPARSE_ARB = GL_TRUE` before calling `glTextureStorage2D`. This cannot use the existing `EnsureStorageAllocated` code path.
- [x] After sparse storage allocation, query `GL_NUM_SPARSE_LEVELS_ARB` and store the result on the record. Treat all mip levels at and above this index as the mip tail; commit and uncommit the tail as an atomic unit.
- [x] Support committing preview mip ranges only during import, including the mip tail if preview mips overlap it.
- [x] Support promoting by raising the committed range and then lowering `GL_TEXTURE_BASE_LEVEL` only after the cross-context fence signals.
- [x] Support demoting by raising `GL_TEXTURE_BASE_LEVEL` first, then uncommitting the now-inaccessible high-detail mips.
- [x] Never lower `GL_TEXTURE_BASE_LEVEL` to expose a mip that has not yet been committed and uploaded. Treat this as a correctness invariant, not a best-effort guard.
- [x] Track committed bytes accurately in rendering stats. Use committed mip range × format bytes per texel, not logical full-chain size.

## 10.4 Upload TODOs

- [x] Route texture upload jobs through the secondary GPU context when possible.
- [x] After each sparse upload on the secondary context, insert `glFenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0)` and store the sync object on the `TextureStreamingRecord`.
- [x] On the render thread, poll pending fence objects each frame. Lower `GL_TEXTURE_BASE_LEVEL` only after the associated fence signals. Discard fence objects once they have signaled.
- [x] Keep the existing per-frame promotion-byte limits and add an explicit in-flight shared-upload cap.
- [x] Add queue coalescing so repeated requests for the same texture collapse into one latest request.
- [x] Add cancellation for stale background decode work.
- [ ] Add telemetry for decode queue depth and GPU upload queue depth.

## 10.5 Heuristics TODOs

- [x] Replace raw distance thresholds with projected screen importance.
- [x] Add visibility grace windows and promotion cooldowns.
- [x] Add texture-role priority multipliers.
- [x] Add optional mesh-import UV-density hints.
- [ ] Add configurable minimum resident detail for hero assets.

## 10.5.1 Page-Level Sparse Residency TODOs

- [x] Track normalized UV-bounds-derived page requests per sparse imported texture.
- [x] Commit and uncommit page-aligned sparse regions for partially visible high-detail mips.
- [x] Slice page-region uploads out of the existing cooked mip payloads.
- [ ] Add optional cooked page-table metadata or pre-sliced page blobs if profiling shows row-slicing overhead is material.

## 10.6 Validation TODOs

- [x] Add unit tests for budget fitting and request coalescing. (`ImportedTextureStreamingPhaseTests`)
- [x] Add unit tests for promotion and demotion hysteresis. (`ImportedTextureStreamingPhaseTests`)
- [x] Add integration coverage for preview-only import behavior.
- [x] Validate that requested mip reads do not trigger full PNG decode (`StreamableTexturePayload_ReadsOnlyRequestedResidentSlice`).
- [ ] Add an import regression test for large scenes such as Sponza.
- [ ] Add a diagnostic test that verifies sparse-unsupported hardware falls back to tiered residency cleanly.
- [ ] Add a test that verifies NPOT textures are assigned the tiered fallback backend rather than the sparse backend.
- [ ] Add a test that verifies `GL_TEXTURE_BASE_LEVEL` is never lowered to a mip level that has not been committed (i.e., that the fence-wait logic cannot be bypassed).
- [ ] Add a test that verifies the mip tail levels are always committed and uncommitted as a unit and never individually.

## 11. Risks and Mitigations

## 11.1 Sparse texture support is not universal

Mitigation:

- Keep the tiered-resident fallback.
- Keep the streaming manager backend-agnostic.

## 11.2 PNG remains a CPU bottleneck even after sparse residency exists

Mitigation:

- *(Resolved by Phase 2.)* Cooked mip-addressable assets are implemented. PNG is now a second-class fallback source.

## 11.3 Residency thrash can destroy frame pacing

Mitigation:

- Hysteresis, per-frame upload caps (`MaxResidentTransitionsPerFrame`, `MaxPromotionBytesPerFrame`), recency grace periods, and queue coalescing are implemented in Phase 0–1 work.

## 11.4 VRAM accounting can become inaccurate

Mitigation:

- Count committed bytes, not logical texture size.
- Surface committed-byte telemetry in debug builds.
- **Open gap**: `EstimateResidentBytes` still hardcodes RGBA8 — this will become inaccurate when BCn is added. Fix is tracked in 10.1.

## 11.5 Secondary-context behavior may differ across drivers

Mitigation:

- Keep a conservative render-thread fallback.
- Make upload path selection explicit and logged.

## 11.6 Sampling uncommitted sparse pages is undefined behavior

Mitigation:

- Treat `GL_TEXTURE_BASE_LEVEL` management as a correctness invariant, not a best-effort quality hint.
- Never lower `GL_TEXTURE_BASE_LEVEL` without a signaled fence confirming the upload is complete.
- In debug builds, add a validation pass that checks committed mip range against current base/max level on all sparse textures each frame.

## 11.7 NPOT and format-incompatible textures silently fail sparse allocation

Mitigation:

- Classify textures as sparse-eligible or fallback-only at backend assignment time, not at upload time.
- Query `GL_VIRTUAL_PAGE_SIZE_X_ARB` / `GL_VIRTUAL_PAGE_SIZE_Y_ARB` per format and check alignment before assigning the sparse backend.
- Log the reason a texture was assigned to the tiered fallback in debug mode.

## 11.8 `GL_ARB_sparse_texture2` required for BCn sparse residency but has narrower support

Mitigation:

- Do not assume BCn sparse residency is available just because `GL_ARB_sparse_texture` is.
- Gate BCn cooked format streaming and BCn sparse page commitment explicitly on `GL_ARB_sparse_texture2`.
- Fall back to RGBA8 cooked mips on hardware without `GL_ARB_sparse_texture2`.

## 12. Open Questions

- ~~Should the first cooked streaming format store uncompressed RGBA8 mip blobs for simplicity, or go directly to BCn-compressed mips?~~ **Resolved:** Start with RGBA8. BCn requires `GL_ARB_sparse_texture2` for sparse page commitment; RGBA8 works with the base `GL_ARB_sparse_texture` extension universally. Add BCn as a later upgrade gated on extension presence.
- Do we want page-level sparse residency only for textures above a dimension threshold?
- Should visibility be tracked per material texture slot or per logical texture asset?
- Do we want separate budgets for opaque-visible content, transparent-visible content, and offscreen cached content?
- How aggressively should imported textures demote when a world contains many active cameras or scene captures?

## 13. Recommended Order Of Attack

1. ~~Refactor the current imported streaming code into manager + backend abstractions.~~ *(Done — Phase 1)*
2. ~~Add cooked mip-addressable texture assets.~~ *(Done — Phase 2)*
3. ~~Fix the remaining Phase 0 items: queue compaction, format-aware byte estimation, debug overlay.~~ *(Done)*
4. Land sparse mip residency in the OpenGL backend. *(Phase 3)*
5. Move upload execution onto the secondary GPU context with fence synchronization. *(Phase 4)*
6. Improve heuristics and telemetry. *(Phase 5)*
