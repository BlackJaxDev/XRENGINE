# Texture Management Runtime Design

Last Updated: 2026-05-01
Status: design
Scope: runtime texture residency, upload scheduling, diagnostics, and render-thread safety for imported and engine-owned textures.

Related docs:

- [Texture management runtime TODO](../todo/texture-management-runtime-todo.md)
- [Sparse texture streaming plan](sparse-texture-streaming-plan.md)
- [Startup FPS drop remediation](startup-fps-drop-remediation-plan.md)
- [Default render pipeline notes](../../architecture/rendering/default-render-pipeline-notes.md)
- [Neural texture compression plan](neural%20texture%20compression.md)

## 1. Summary

Texture management needs to become a first-class runtime service rather than a set of loosely related behaviors on `XRTexture2D`, `GLTexture2D`, and `ImportedTextureStreamingManager`.

The target system has four explicit responsibilities:

1. Keep texture identity stable for materials and render passes.
2. Keep GPU residency within a tracked VRAM budget.
3. Keep upload and residency transitions inside a render-thread frame budget.
4. Produce a dedicated texture log that explains decisions, waits, bytes, VRAM pressure, and slow paths without requiring the general, rendering, OpenGL, and profiler logs to be correlated by hand.

The immediate OpenGL safety requirement is that no queued upload may call `TexSubImage2D` unless its mip level and upload rectangle fit the storage currently allocated for that GL texture name. Immutable storage recreation and runtime streaming transitions must be generation-aware so stale work cannot upload into storage assumptions that are no longer true.

## 2. Current Problems Observed

The May 1, 2026 capture showed several separate issues landing in the same user-visible symptom: textures sharpened slowly, appeared to move between detail tiers, and frame time collapsed during heavy render-thread work.

### 2.1 Delayed Promotion

Imported textures spent many frames at preview quality while model import was active and visibility snapshots were not yet available. The streamer reported tracked textures but `visible=0` and `allowPromotions=False`, which kept content at the 64 px preview tier longer than expected.

### 2.2 Oversized Render-Thread Upload Jobs

The runtime-managed progressive upload path budgets bytes per frame, but individual mip upload calls can still take tens of milliseconds. A single `PushMipLevel` can upload an entire large mip through `TexSubImage2D`; the scheduler only regains control after that call returns.

### 2.3 Stale Storage Assumptions

One OpenGL error came from `TexSubImage2D` with `GL_INVALID_VALUE`, reported as "Size and/or offset out of range." The surrounding texture was changing resident dimensions and mip locks. This indicates an upload job attempted to write a mip rectangle that did not fit the GL immutable storage currently attached to the texture name.

### 2.4 Residency Thrash

The logs showed repeated resident data applications and sparse transitions for the same textures, including real demotions and later promotions. Promotions should happen quickly when a texture becomes important; demotions should be slower and coalesced to avoid visible quality bounce.

### 2.5 Shadow Atlas Contention

Slow shadow tile renders took large chunks of render-thread time during the same session. Texture streaming cannot feel smooth if shadow atlas work and upload work are allowed to saturate the render thread independently.

## 3. Goals

- Material texture handles stay stable from import through shutdown.
- Sampling never reaches missing or uncommitted mip data.
- Texture uploads are chunked by time, not only by bytes.
- Promotion is responsive for visible content even while import is active.
- Demotion is conservative and hysteresis-based.
- VRAM pressure is visible in telemetry and logs.
- Expensive texture work is attributable to texture name, source path, mip level, bytes, wait time, and GL operation.
- Shadow atlas and texture uploads share enough scheduling information that one cannot starve the other unknowingly.
- OpenGL is the primary implementation path, but runtime abstractions should not prevent Vulkan parity.

## 4. Non-Goals

- This document does not replace the sparse texture streaming plan.
- This document does not require full virtual texturing or bindless textures for the first milestone.
- This document does not require a new compressed texture asset format before fixing runtime stalls.
- This document does not make every render target participate in imported-texture streaming policy.

## 5. Core Invariants

### 5.1 Stable Texture Identity

Materials should bind stable `XRTexture` objects. Streaming may change residency, committed pages, or the internal GL name after an immutable recreate, but higher-level material slots should not swap texture objects unless the asset itself changes.

### 5.2 Generation-Gated GPU Work

Every queued GPU upload or sparse transition must capture the texture storage generation it was prepared against. If the texture is resized, recreated, deleted, or assigned a new sparse logical allocation before the job executes, the job must cancel or restart instead of uploading stale data.

Required generation changes:

- Immutable storage allocation.
- Immutable storage destruction.
- `DataResized`.
- Sparse logical storage allocation.
- External memory import replacement.
- GL object regeneration.

### 5.3 Upload Rectangle Validation

Before any `TexSubImage2D` call:

- The mip level must be within allocated storage.
- The upload width and height must be non-zero.
- The upload offset plus extent must fit the allocated dimensions for that mip.
- The validation diagnostic must include texture name, binding id, mip level, upload rectangle, allocated base dimensions, allocated mip dimensions, allocated level count, format, sparse state, streaming lock mip level, and storage generation.

When a full-push upload discovers that immutable storage is too small and the texture is not sparse or external-memory backed, OpenGL may recreate the immutable storage and retry. Incremental uploads must not recreate storage silently; they should cancel, invalidate the texture, and let the owning transition restart from a fresh generation.

### 5.4 Sampling Range Safety

`GL_TEXTURE_BASE_LEVEL` and `GL_TEXTURE_MAX_LEVEL` must expose only mips known to be uploaded and resident. Progressive uploads should reveal a mip only after that mip is complete. Sparse textures must never sample uncommitted pages.

### 5.5 Render-Thread Budget Ownership

Any texture operation that can block the driver must be scheduled through a budgeted path. A single job must have an internal stop condition so it cannot spend 30-100 ms on one mip while the outer scheduler believes the frame budget is 4 ms.

## 6. Target Runtime Architecture

### 6.1 Texture Streaming Manager

`ImportedTextureStreamingManager` remains the policy owner for imported assets. Its target responsibilities:

- Track all imported textures and their source assets.
- Record per-frame usage from main visible passes.
- Decide target resident mip or page coverage.
- Sort promotions and demotions by priority.
- Apply hysteresis, cooldowns, and memory pressure rules.
- Emit texture telemetry snapshots.

It should not directly encode OpenGL upload mechanics. It should produce transition intents.

### 6.2 Residency Backend

A backend owns how a transition becomes GPU state:

- `GLSparseTextureResidencyBackend`: stable logical storage, sparse page or mip commitments.
- `GLTieredTextureResidencyBackend`: fallback that swaps resident mip chains or resident dimensions.
- Future Vulkan backend: sparse image residency or staged image uploads.

Backends must expose:

- Current logical dimensions and mip count.
- Current resident range or page set.
- Estimated committed bytes.
- Pending transition bytes.
- Whether the transition is promotion, demotion, or replacement.
- Whether the transition can run on a shared context.

### 6.3 Upload Scheduler

Texture uploads need their own scheduler instead of piggybacking on generic render-thread jobs.

Scheduler inputs:

- Upload kind: preview, promotion, demotion, repair, render-target initialization.
- Source: cooked mip, decoded image mip, sparse page, PBO, CPU pointer.
- Estimated bytes and estimated driver cost.
- Deadline class: visible-now, near-visible, background, demotion.
- Captured storage generation.

Scheduler behavior:

- Process visible-now repair and preview uploads first.
- Keep a strict per-frame time budget.
- Stop inside large mip/page uploads when the budget expires.
- Prefer small row/page chunks over whole large mips.
- Coalesce duplicate requests for the same texture.
- Cancel stale work on generation mismatch.

### 6.4 Shared Frame Budget

Texture uploads, shader compilation, mesh uploads, and shadow atlas updates all compete for the render thread. A shared budget coordinator should expose:

- Frame budget remaining.
- Startup boost state.
- Upload queue depth.
- Shadow atlas queue depth.
- Last completed render age.
- Render-thread stall warning thresholds.

Texture streaming can then defer non-urgent promotions when shadow atlas work is already over budget, and shadow atlas can defer low-priority tiles when urgent visible texture repair is pending.

## 7. Policy Improvements

### 7.1 Promotion During Import

Do not block all promotion while imports are active. Instead:

- Keep preview-first behavior for newly discovered textures.
- Allow a small visible-promotion budget during active import.
- If no visibility snapshot exists, use recently bound material textures as fallback priority.
- Promote textures used by visible meshes before background imports.
- Promote normal/roughness/albedo together enough to avoid mismatched material detail.

### 7.2 Hysteresis

Promotion and demotion should use different thresholds:

- Promote quickly when projected pixel span exceeds resident quality.
- Demote only after a grace period below threshold.
- Keep newly promoted textures pinned for a short minimum lifetime.
- Do not demote during active import unless VRAM pressure demands it.
- Prefer demoting invisible large textures before reducing visible texture quality.

### 7.3 Coalescing

The manager should not queue a transition if the target resident size, page selection, and source generation are already pending or resident.

Coalescing keys:

- Texture instance.
- Source asset version.
- Target resident max dimension or mip base.
- Sparse page selection.
- Include-mip-chain flag.
- Backend generation.

### 7.4 Memory Pressure

VRAM budget handling should distinguish normal steady-state policy from pressure response.

Normal policy:

- Fit visible textures to projected need.
- Keep recently visible textures warm.
- Run background promotions only with spare budget.

Pressure policy:

- Cancel background promotions.
- Demote invisible textures first.
- Demote oldest recently visible textures second.
- Avoid demoting textures currently bound this frame.
- Log every pressure-driven demotion with bytes reclaimed.

## 8. Upload Implementation Plan

### Phase 1 - OpenGL Safety

Status: started.

- Validate every `TexSubImage2D` mip level and upload rectangle.
- Add storage generation tracking to `GLTexture2D`.
- Cancel stale progressive GL uploads when the generation changes.
- Recreate immutable non-sparse storage when a full-push upload proves storage is too small.
- Add diagnostics around skipped uploads and recreate attempts.

### Phase 2 - True Chunked Runtime Uploads

- Replace runtime-managed whole-mip `PushMipLevel` calls with chunked upload requests.
- Use row chunking for CPU pointer uploads where format/type support allows it.
- Fall back to small full-mip uploads only for tiny mips.
- Add per-chunk stopwatch checks, not just byte budget checks.
- Keep partially uploaded mips hidden until complete.

### Phase 3 - Transition Coalescing And Hysteresis

- Add pending-transition coalescing in `ImportedTextureStreamingManager`.
- Add promote/demote cooldowns.
- Add recently-bound fallback priority when visibility snapshots are missing.
- Prevent repeated `ApplyResidentData` calls for identical resident data.

### Phase 4 - Texture Log And Telemetry

- Add `log_textures.txt` beside `log_rendering.txt`, `log_opengl.txt`, and profiler logs.
- Move texture-specific streaming and upload diagnostics into that log.
- Keep high-severity OpenGL errors in `log_opengl.txt`, but mirror texture context in `log_textures.txt`.
- Add periodic summaries and slow-operation records.

### Phase 5 - Shared Render Work Budget

- Make texture upload and shadow atlas scheduling budget-aware.
- Report when shadow work delays texture work or texture work delays shadows.
- Add frame-level summaries for pending work and budget spent.

## 9. Dedicated Texture Log

Add a texture-only log file:

```text
Build/Logs/<configuration>_<tfm>/<platform>/<session>/log_textures.txt
```

The goal is to answer these questions without opening four logs:

- Which textures are resident?
- Which textures are pending promotion or demotion?
- Which textures are consuming the most VRAM?
- Which texture operations waited longest in the render queue?
- Which uploads took longer than the per-frame budget?
- Which transitions were skipped, canceled, coalesced, or retried?
- Which textures were demoted because of VRAM pressure?
- Which texture caused an OpenGL storage or upload validation failure?

### 9.1 Event Types

Recommended event names:

- `Texture.ImportPreviewQueued`
- `Texture.ImportPreviewReady`
- `Texture.VisibilityRecorded`
- `Texture.ResidencyDesired`
- `Texture.TransitionQueued`
- `Texture.TransitionCoalesced`
- `Texture.TransitionCanceled`
- `Texture.TransitionApplied`
- `Texture.UploadChunk`
- `Texture.UploadSlow`
- `Texture.StorageAllocated`
- `Texture.StorageRecreated`
- `Texture.UploadValidationFailed`
- `Texture.VramPressure`
- `Texture.VramSummary`

### 9.2 Required Fields

Every texture event should include:

- Frame id.
- Texture name.
- Source path or asset id when available.
- GL binding id when available.
- Logical dimensions.
- Resident dimensions or resident mip range.
- Mip level or page range for upload events.
- Bytes uploaded or committed.
- Estimated committed VRAM.
- Queue wait time.
- Execution time.
- Storage generation.
- Backend name.
- Reason.

### 9.3 Periodic Summary

Emit a summary every 60 frames or when a slow frame occurs:

```text
[TextureSummary] frame=6600 tracked=76 visible=37 pending=0 uploading=1
residentBytes=318MB budget=2048MB pressure=False
promotionsQueued=0 demotionsQueued=0 coalesced=12 canceledStale=3
slowUploads=2 maxUploadMs=18.4 maxQueueWaitMs=28159
topResident=sponza_floor_diff:32MB,sponza_column_b_diff:16MB
```

### 9.4 Slow Operation Records

Emit a slow record whenever an operation exceeds thresholds:

- CPU decode or resize: 5 ms.
- Mip build: 5 ms.
- Render-thread upload chunk: 2 ms.
- Full texture transition: 8 ms.
- Queue wait: 100 ms.
- Storage recreate: always log.
- Validation failure: always log.
- VRAM pressure demotion: always log.

## 10. Validation Plan

### 10.1 Repro Scenes

- Sponza import with many large textures.
- A Unity/Poiyomi avatar import with mixed albedo, normal, masks, and ramps.
- A scene with large shadow atlas updates and texture promotions at the same time.
- A low-VRAM-budget run that forces demotion.

### 10.2 Metrics

Track:

- Max render-thread texture upload time.
- Count of upload jobs over 2 ms, 4 ms, 8 ms, and 16 ms.
- Time from preview ready to first visible promotion.
- Time from visible promotion request to resident target applied.
- Number of duplicate transitions coalesced.
- Number of stale jobs canceled.
- Number of texture OpenGL validation failures.
- Resident VRAM bytes over time.
- Visible texture quality error, measured as desired resident size minus actual resident size.

### 10.3 Acceptance Criteria

- No `GL_INVALID_VALUE` from texture uploads during the Sponza repro.
- No single texture upload job exceeds the configured per-frame texture budget by more than one chunk.
- Visible textures begin promotion during import when they are bound or visible.
- Duplicate resident-data applications are coalesced.
- Demotion does not occur for a newly promoted visible texture during the cooldown window.
- `log_textures.txt` can explain every promotion, demotion, skipped upload, stale cancel, and VRAM pressure event.

## 11. Risks

- Too much logging can become its own frame-time issue. Texture logging should support summary mode, slow-only mode, and verbose mode.
- Shared-context OpenGL uploads can still block if synchronization is wrong. Fences must protect visibility without forcing the render thread to wait unnecessarily.
- Sparse texture behavior varies by driver. Sampling-range safety must remain conservative.
- Aggressive promotion during import can compete with mesh and shader warmup. Keep a separate import-era budget.
- Recreating immutable storage fixes correctness but can drop old contents. It should be a repair path, not the normal promotion path.

## 12. Open Questions

- Should `log_textures.txt` be always on in Debug builds or gated by a rendering setting?
- Should texture telemetry be exposed in the editor as a live table?
- Should shadow atlas and texture uploads share one global render-work coordinator or separate coordinators with a published budget contract?
- How should the Vulkan backend expose image residency and upload progress so the manager stays renderer-neutral?
- What should the default VRAM budget be for editor sessions on high-memory GPUs?
