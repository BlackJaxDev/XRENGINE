# Vulkan Pipeline — Current Issues

> Re-assessed with logs: `20260213_203324_55524` (Feb 13, 2026), plus latest runtime fixes applied.

## Critical (actively failing)

- [x] **Descriptor Indexing / Update-After-Bind Feature Mismatch** — Fixed by gating storage-image update-after-bind on explicit capability and emitting per-binding flags only when supported.

- [x] **Image Layout Tracking Breakage** — Fixed by preserving explicit sync `Undefined` layout semantics and preventing stale producer-layout overrides.

- [x] **Barrier Stage/Access Pairing Errors** — Fixed by stage/access sanitization before barrier emission.

- [x] **Descriptor Write Type/Usage Inconsistency** — Fixed by enforcing sampled/storage usage compatibility and correct storage image layout at descriptor write sites.

## High

- [x] **Dynamic Scissor Bounds Invalid** — Fixed by clamping scissor offset/extent to active target bounds in Vulkan state.

- [x] **Clear Rect Outside Render Area** — Fixed by clamping clear rects to target extent before `vkCmdClearAttachments`.

- [x] **Shutdown Resource Lifetime Violations** — Fixed by forcing GPU idle at Vulkan cleanup entry before resource destruction.

## Medium

- [x] **DepthView Sampling Usage Gap** — Fixed by ensuring depth-stencil usage profiles include sampled usage for descriptor-read depth views.

- [x] **Queue Utilization Policy (Compute/Transfer)** — Fixed by reducing auto-overlap promotion thresholds and relaxing candidate criteria so compute/transfer overlap engages earlier.

## Recently fixed (keep monitoring)

- [x] **Deferred Context Lost Active Pipeline** — Fixed by carrying pipeline instance in deferred frame-op context and applying thread-local pipeline override during recording.

- [x] **SSAO/Bloom Resilience Guards Silent** — Guards now log explicit throttled diagnostics instead of failing quietly.

- [x] **Zero-Size Buffer Creation Crash** — `VkDataBuffer.CreateBuffer` now tolerates zero-length logical buffers via minimum allocation size and conditional copy.

- [x] **Shader Compilation Regression (prior list)** — Not observed as a dominant failure in latest log; keep watch during next validation pass.

- [x] **Missing Descriptor/Vertex Bindings (prior list)** — Not observed as dominant failures in latest log; re-open only if they reappear after barrier/layout fixes.

## Investigate next pass

- [ ] **SPIR-V Interface/Attachment Mismatch Warnings** — Track whether interface warnings persist after core barrier/layout and descriptor fixes.

- [ ] **"Vulkan Fallback" Startup Path** — Still requires dedicated startup trace confirmation.
