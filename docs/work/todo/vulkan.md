# Vulkan TODO Backlog (Canonical)

Last updated: 2026-03-23

This file is the canonical Vulkan work document for XRENGINE. It now consolidates the active backlog, prior status/report history, preserved diagnostic notes, and the architectural audit/guidance that previously lived in `docs/work/vulkan/vulkan-report.md` and `docs/work/vulkan/modern-vulkan-render-pipeline-summary.md`.

## Current implementation snapshot

### Implemented since earlier reports

The following items were previously tracked as planned or incomplete in older Vulkan docs but are now implemented in code:

- Per-frame Vulkan render loop is wired and active.
- Swapchain depth attachment path exists.
- Dynamic rendering is in active use for the swapchain main path.
- Secondary command buffer infrastructure and parallel bucket recording are present.
- Indirect draw paths are implemented, including indirect-count support when available.
- Readback APIs exist (`GetDepth*`, `GetScreenshotAsync`, `CalcDotLuminance*`).
- Shader compatibility fix for `UnlitTexturedForward.fs` output location is present.
- Bind-state reset logic exists for command buffer re-record paths.

### Strong and aligned areas

- Render-graph planning and barrier planning architecture are in place.
- Resource aliasing infrastructure exists through logical-to-physical planning.
- Descriptor tiering, layout caching, and feature-profile gating are in place.
- Pipeline cache persistence and runtime cache structure are in place.
- Transfer-queue upload path and timeline-based frame synchronization are in place.
- GPU-driven rendering enablers are present.
- Dedicated graphics, compute, and transfer queues are discovered and used.
- Multiview / VR paths are integrated.
- Persistent mapping is already used for host-visible buffers.
- Resolve attachment support and smarter store-op inference already exist in the render graph.

### Open correctness and robustness concerns still observed

- ~~Render-graph pass index fallback warnings are still emitted for some `MeshDrawOp` paths.~~ Fixed: `EnsureValidPassIndex` now accepts well-known `EDefaultRenderPass` values without warning.
- ~~Memory allocation remains predominantly per-resource `AllocateMemory` with no global suballocator backbone yet.~~ Fixed: `IVulkanMemoryAllocator` abstraction with `VulkanLegacyAllocator` (per-resource, default) and `VulkanBlockAllocator` (64 MB block suballocator with sorted free-list, separate image/buffer pools, dedicated allocation threshold). All ~14 allocation sites migrated. Feature toggle gates backend selection.
- ~~Readback memory paths still lack `HostCached` usage.~~ Fixed: readback staging prefers `HostCached` with `HostCoherent` fallback; `VulkanStagingManager` readback pool also updated.
- ~~Synchronization2 / Submit2 migration has not started.~~ Fixed: sync2/Submit2 backend is live with legacy fallback behind `VulkanRobustnessSettings.SyncBackend`.
- ~~Broad `AllCommandsBit` stage masks are still used in multiple Vulkan paths.~~ Fixed: barrier precision audit complete across all 8 audited files; remaining uses are documented as justified.
- ~~Descriptor pool lifecycle still relies heavily on destroy/recreate patterns and `FreeDescriptorSetBit` in hot paths.~~ Fixed: transient pools use reset/reuse; `FreeDescriptorSetBit` removed from hot pools; pool size classes implemented.

## Architectural guidance distilled into backlog priorities

The core Vulkan guidance that should continue to shape implementation work is:

- Performance comes from engine design choices, not API usage alone; profile on target vendors and avoid both missing sync and oversync.
- Memory should use device-local placement for static/high-bandwidth resources, host-visible upload arenas for dynamic data, suballocated large blocks instead of per-resource allocations, correct `bufferImageGranularity` handling, persistent mapping for host-visible arenas, selective dedicated allocations, lazy allocation for transient tiler-friendly attachments, and `HostCached` memory for GPU-to-CPU readback.
- Descriptor management should prefer reset-and-reuse pools over destroy/recreate, avoid `FREE_DESCRIPTOR_SET_BIT` for frame-style allocation patterns, use pool size classes, favor frequency-based descriptor sets, prefer immutable samplers where practical, and use update templates or equivalent bulk update paths for hot descriptor writes.
- Recording and submission should keep command pool reuse fence-safe, keep command buffers and submit batches coarse enough to amortize scheduling overhead, and avoid over-fragmented submit patterns.
- Synchronization should stay render-graph-driven, use precise stage/access masks, batch barriers, avoid unnecessary layout transitions, and treat `AllCommandsBit` as an exception path rather than a default.
- Render-pass bandwidth policy should aggressively prefer `DontCare` load/store operations when prior or final contents are irrelevant and prefer in-pass resolve behavior when possible.
- Pipeline strategy should minimize runtime JIT hitches through persistent caches, prewarming, and deliberate permutation control.

## Audit snapshot (2026-02-18, revised 2026-02-18)

### Executive result

The Vulkan renderer has a strong modern foundation in render-graph planning, barrier planning, descriptor-tier contracts, pipeline-cache persistence, GPU-driven rendering enablers, and multi-queue usage. It is not yet at the intended "best possible" architecture because several hot-path design gaps remain unresolved.

### Highest-priority gaps confirmed by audit

1. Memory allocation architecture is still per-resource `vkAllocateMemory` / `vkBind*Memory` with no allocator backbone, no `bufferImageGranularity` handling, no dedicated-allocation decision path, no lazy allocation, and no VRAM oversubscription fallback.
2. GPU-to-CPU readback still prefers `HostVisible | HostCoherent` instead of a `HostCached` readback path with explicit invalidate/flush handling.
3. Synchronization still uses legacy `vkQueueSubmit` / `vkCmdPipelineBarrier` APIs rather than synchronization2 / Submit2.
4. Broad `AllCommandsBit` stage masks remain in several fallback and utility paths even though the planner can already produce tighter masks.
5. Descriptor pools still use `FreeDescriptorSetBit` in hot subsystems and rely on destroy/recreate patterns instead of reset/reuse.

### Medium-priority gaps confirmed by audit

- Dynamic UBO offset usage is not yet a dominant binding strategy.
- Pipeline handling is still largely runtime/JIT driven even though persistent caches already exist.
- `RenderPassBuilder` defaults remain conservative and can bias call sites toward avoidable load/store bandwidth cost.
- Command-pool reset strategy still favors individual command buffer reset over pool-level reset for frame-style usage.
- Push constants are underused outside narrow paths such as ImGui.

### Low-priority or informational notes

- Split-barrier / event scheduling is not currently used; this is acceptable unless measurements show a win for specific workloads.
- Vulkan allocation callbacks are wired as placeholders rather than a real tracking allocator.
- ~~Descriptor pool size classes are not yet implemented.~~ Fixed: `EDescriptorPoolSizeClass` (Small/Medium/Large) with `InferPoolSizeClass` and `GetPoolSizeClassParameters` in `CommandBuffers.cs`.

## Preserved historical diagnostic notes

### Bind tracking hash-collision risk

The tracked-bind deduplication in `CommandBuffers.cs` uses `System.HashCode` truncated into a `ulong`, which only preserves 32 bits of entropy. If invisible-geometry or "no fragments" symptoms recur, bypass tracked binds first to rule out a collision-driven skipped bind.

### UI batch rendering investigation notes

During the 2026-02-20 UI batch rendering investigation, the render-graph pass metadata bug was the highest-value issue. Lower-priority hypotheses that remain useful if related issues recur are:

1. Non-batched transparent shader compatibility in Vulkan profiles.
2. Render pass / FBO target mismatch during the UI batch pass.
3. Camera / projection uniform state during `VPRC_RenderUIBatched`.
4. Descriptor fallback silently binding placeholder zeroed buffers for SSBOs.

### Prior cross-API finding

OpenGL batch rendering also had defects during the same investigation; visible OpenGL UI was coming from CPU fallback until the `gl_InstanceIndex` versus `gl_InstanceID` issue was fixed.

## Reference comparison: `diharaw/hybrid-rendering`

Comparison target: https://github.com/diharaw/hybrid-rendering

This reference is useful because it is a focused Vulkan hybrid-rendering sample with explicit AO / shadows / reflections / DDGI pipelines, heavy push-constant usage, VMA-backed resource allocation, and a sync2-style resource-state API. It is **not** a direct parity target for XRENGINE: XRENGINE already carries broader engine/editor scope, render-graph compilation, resource aliasing, multiview/VR paths, multi-queue usage, and feature-profile gating that the sample does not need to solve.

Use the comparison as a prioritization aid, not as a mandate to copy the sample's structure.

| Area | XRENGINE current position | `hybrid-rendering` reference | Backlog implication |
|---|---|---|---|
| Render architecture | Strong render-graph compiler + barrier planner + resource planner/allocation indirection. Broader engine/editor/VR scope. | Narrower feature-local pass chains are easier to inspect: ray trace -> temporal -> spatial filter -> upsample. | Keep XRENGINE's graph-centric architecture; improve observability and validation around pass chains instead of flattening into sample-style bespoke flows. |
| Memory allocation | Still dominated by per-resource `AllocateMemory` across core Vulkan objects. | Uses VMA-backed image/buffer allocation patterns (`vk_mem_alloc.h`, `VMA_MEMORY_USAGE_GPU_ONLY`). | Confirms allocator backbone remains the highest-priority architectural gap. |
| Synchronization | Good planner semantics, but submission/barrier APIs are still legacy and some broad masks remain. | Backend-level `use_resource(...)` / `flush_barriers(...)` flow is already written around synchronization2-era stage/access usage. | Confirms `Sync2` / `Submit2` migration and `AllCommandsBit` reduction should stay near the top of the backlog. |
| Descriptor strategy | Strong layout/cache/contracts, but hot paths still churn pools and lack templates / immutable samplers. | Feature-local descriptor sets, small push-constant payloads, and targeted dynamic-offset use keep hot paths lean. | Confirms reset/reuse pools, update templates, immutable samplers, and dynamic-offset evaluation are the right next steps. |
| Capability model | Broad feature-profile system with optional-path fallbacks. | Sample assumes high-end features such as ray tracing + descriptor indexing are present. | Keep XRENGINE's profile model, but improve startup capability logging and required/optional feature reporting. |
| Profiling and debuggability | Better engine-wide counters already exist, but feature-local slices are still thin. | Sample organizes work into highly inspectable feature stages with obvious profiling boundaries. | Add more per-feature / per-stage counters and validation recipes without sacrificing engine generality. |
| Push constants | Present, but underused outside narrow paths. | Used aggressively for tiny per-dispatch constants in compute / RT stages. | Keep push constants high on the optimization list for GPU-driven and compute-heavy paths. |

Comparison-driven priorities reinforced by this sample:

- Keep the existing render-graph and resource-planning direction; do **not** regress toward a narrower sample-style architecture just for similarity.
- Raise priority on allocator modernization, sync2 migration, descriptor-pool reset/reuse, and push-constant adoption because the reference sample is already lean in exactly those hot-path areas.
- Improve startup capability reporting so high-end paths are clearly classified as required, optional, or disabled-by-profile instead of being discoverable only through code or warnings.

---

## P0 — Baseline, observability, and safety rails

### Feature toggles

- [x] Define `VulkanRobustnessSettings` class with per-area toggles:
  - [x] Allocator backend toggle (`Legacy` vs `Suballocator`)
  - [x] Sync backend toggle (`Legacy` vs `Sync2`)
  - [x] Descriptor update backend toggle (`Legacy` vs `Template`)
- [x] Wire implemented backends into runtime so each backend can be switched independently
- [x] Expose toggles in editor settings UI or startup config

### Per-frame diagnostic counters

Already implemented: bind churn, pipeline cache hit/miss, barrier planner pass counts, GPU command buffer timing, queue overlap window.

Still needed:

- [x] Memory allocation count per frame (by memory class)
- [x] Memory bytes allocated per frame (by memory class)
- [x] Descriptor pool create count per frame
- [x] Descriptor pool destroy count per frame
- [x] Descriptor pool reset count per frame (counter instrumented; no reset call sites exist yet — see P2 descriptor pool reset/reuse)
- [x] Queue submit count per frame
- [x] Route new counters into profiler data source

### Startup capability snapshot

- [x] Log acceleration structure support at startup
- [x] Log descriptor indexing support at startup
- [x] Log draw indirect count support at startup
- [x] Log multiview support at startup
- [x] Log ray tracing pipeline support at startup
- [x] Log timeline semaphore support at startup
- [x] Log synchronization2 support at startup
- [x] Log max memory allocation count at startup
- [x] Log available memory heaps and types at startup
- [x] Classify startup capabilities as `Required`, `Optional`, or `DisabledByProfile` for each major Vulkan feature family

### CI integration

- [ ] Add CI pipeline step that runs Vulkan-focused unit tests
- [ ] Configure CI to fail on Vulkan test regressions
- [ ] Verify existing `VulkanTodoP2ValidationTests` run in CI

---

## P0 — Correctness and architectural blockers

### Render-graph pass-index mismatch

- [x] Investigate why `MeshDrawOp` emissions hit invalid pass indices
- [x] Identify which render pipeline paths emit pass index 4+ without metadata
- [x] Fix pass-index assignment or metadata generation to eliminate mismatch
- [x] Remove or downgrade the fallback-to-pass warning once fixed
- [x] Add regression test for pass-index validity

### Memory allocator backbone

Replace per-resource `AllocateMemory` (~14 call sites) with suballocation:

- [x] Design allocator abstraction interface (`IVulkanMemoryAllocator` or equivalent)
- [x] Implement `LegacyAllocator` wrapping current per-resource behavior
- [x] Implement `BlockSuballocator` with configurable block sizes (16–256 MB)
- [x] Add device-local memory pool
- [x] Add host-visible upload memory pool
- [x] Add host-visible + host-cached readback memory pool
- [x] Handle `bufferImageGranularity` safety:
  - [x] Option A: separate image and buffer pools, OR
  - [ ] Option B: correct alignment/padding within shared pools
- [x] Query `MemoryDedicatedAllocateInfo` / `prefersDedicatedAllocation` for large resources
- [x] Use dedicated allocations when driver indicates preference
- [x] Add `LAZILY_ALLOCATED_BIT` for transient depth attachments where supported
- [x] Defer `LAZILY_ALLOCATED_BIT` wiring for transient MSAA attachments until a transient MSAA attachment path exists
- [x] Migrate `VkDataBuffer.cs` allocation sites to allocator
- [x] Migrate `VkImageBackedTexture.cs` allocation sites to allocator
- [x] Migrate `VkRenderBuffer.cs` allocation sites to allocator
- [x] Migrate `VkMeshRenderer.Uniforms.cs` allocation sites to allocator
- [x] Migrate `SwapChain.cs` allocation sites to allocator
- [x] Migrate `VulkanRenderer.ImGui.cs` allocation sites to allocator
- [x] Migrate `VulkanRenderer.PlaceholderTexture.cs` allocation sites to allocator
- [x] Migrate `VulkanRenderer.State.cs` allocation sites to allocator
- [x] Gate all migrations behind `VulkanRobustnessSettings` allocator toggle
- [x] Verify allocation count stays well below hardware limits under stress

### Out-of-memory fallback

- [x] Detect `ErrorOutOfDeviceMemory` from allocation attempts
- [x] Attempt fallback to host-visible memory for eligible resources
- [x] Log fallback events with resource identity and size
- [x] Add test/simulation for OOM fallback path

### Stencil index readback

- [x] Implement `GetStencilIndex` in `Init.cs` (currently throws `NotImplementedException`)
- [x] Add stencil attachment readback via staging buffer
- [x] Test stencil-index picking path end-to-end

### Readback memory type fix

- [x] Change `Drawing.Readback.cs` staging buffer preference to `HostVisible | HostCached`
- [x] Add `vkInvalidateMappedMemoryRanges` for non-coherent reads
- [x] Add `vkFlushMappedMemoryRanges` for non-coherent writes
- [x] Add fallback to `HostCoherent` if `HostCached` is unavailable on device
- [x] Update `VulkanStagingManager` readback pool memory type selection

### P0 exit notes

- Runtime allocator backend switching is live. Sync and descriptor-update toggles are declared now and become actionable when their P1/P2 backends land.
- Lazy allocation is wired for transient depth attachments. Transient MSAA lazy allocation remains a future-path task because the current renderer does not yet create a distinct transient MSAA attachment allocation path to target.
- Readback latency benchmarking is intentionally carried forward to P2 because it is performance characterization work rather than a P0 correctness blocker.

---

## P1 — Synchronization modernization

### Sync2 / Submit2 migration

- [x] Design sync backend abstraction interface
- [x] Implement legacy sync backend (wrapping current `vkCmdPipelineBarrier` / `vkQueueSubmit`)
- [x] Implement sync2 backend (`vkCmdPipelineBarrier2` / `vkQueueSubmit2`)
- [x] Migrate `Drawing.Core.cs` submission path to Submit2
- [x] Migrate `CommandBuffers.cs` one-time-submit path to Submit2
- [x] Migrate `Drawing.Readback.cs` submission path to Submit2
- [x] Migrate `VulkanBarrierPlanner` barrier emission to sync2
- [x] Migrate utility/transition barrier helpers in `CommandBuffers.cs` to sync2
- [x] Migrate utility/transition barrier helpers in `VulkanRenderer.State.cs` to sync2
- [x] Preserve timeline semaphore semantics through migration
- [x] Keep legacy path as fallback behind `VulkanRobustnessSettings` sync toggle
- [ ] Run full validation-layer-clean pass with sync2 enabled (manual verification)
- [ ] Run visual regression comparison (sync2 vs legacy) (manual verification)

### Barrier precision audit

~26 `AllCommandsBit` usages across the codebase:

- [x] Audit `VulkanBarrierPlanner.cs` usages (3 sites) — Transfer stage narrowed to TransferBit
- [x] Audit `VulkanRenderer.State.cs` usages (1 site) — migrated to CmdPipelineBarrierTracked with layout-based precision
- [x] Audit `VkDataBuffer.cs` usages (3 sites) — cross-queue ownership: BottomOfPipeBit + VertexInput|VertexShader|Fragment|Compute
- [x] Audit `VkImageBackedTexture.cs` usages (5 sites) — AssembleTransitionImageLayout expanded to 13 known transitions; documented AllCommandsBit fallback for unknown transitions
- [x] Audit `VulkanRenderer.PlaceholderTexture.cs` usages (2 sites) — migrated to tracked; documented else-branch fallback
- [x] Audit `CommandBuffers.cs` usages (8 sites) — initial barriers narrowed by target layout; QueryBuffer AllCommandsBit documented as justified; NormalizePipelineStages fallback documented
- [x] Audit `Drawing.Readback.cs` usages (1 site) — narrowed to FragmentShaderBit|ComputeShaderBit
- [x] Audit `Drawing.Blit.cs` usages (2 sites) — swapchain barriers use layout-specific precise stages
- [x] Replace each with minimal producer/consumer stage+access pairs where possible
- [x] Document justified exceptions (fault-containment / unknown-prior-usage paths)
- [x] Add lint/assert to catch newly introduced broad masks in hot paths (`WarnBroadBarrierStages` debug-only assert in CmdPipelineBarrierTracked)
- [x] Prefer `DontCare` load/store ops when previous/final contents are irrelevant — render-graph builder already defaults DontCare; verified no missing DontCare opportunities in audited paths
- [x] Validate no decompression-heavy transitions for MSAA paths — no unnecessary AllCommandsBit on MSAA paths; resolve uses appropriate stages
- [x] Re-count `AllCommandsBit` usages after audit: reduced from ~26 to ~6 justified (QueryBuffer, NormalizePipelineStages fallback, unknown-transition else branches, fault-containment safety paths)

---

## P1 — Descriptor pool and update modernization

### Pool lifecycle overhaul

- [x] Replace transient compute descriptor pool destroy/recreate with `vkResetDescriptorPool`
- [x] Replace transient render program descriptor pool destroy/recreate with `vkResetDescriptorPool`
- [x] Add free-list reuse pattern for reset descriptor pools
- [x] Remove `FreeDescriptorSetBit` from `VulkanComputeDescriptors.cs` pools
- [x] Remove `FreeDescriptorSetBit` from `VkRenderProgram.cs` pools
- [x] Keep `FreeDescriptorSetBit` for ImGui pool (justified: long-lived, individually freed)
- [ ] Verify no descriptor-set lifetime validation errors after conversion (manual verification)

### Pool sizing

- [x] Introduce descriptor pool size classes (small / medium / large) — `EDescriptorPoolSizeClass` enum in `CommandBuffers.cs`
- [x] Assign size class based on pass schema (e.g., shadow vs gbuffer vs post-process) — `InferPoolSizeClass` classifies by descriptor count thresholds
- [x] Replace uniform 8× descriptor count scaling with measured sizing — `GetPoolSizeClassParameters` provides per-class multiplier and maxSets

### Dynamic UBO offsets

- [x] Evaluate per-draw constant update frequency across material types — all UBOs use static binding with per-frame host-coherent updates per material; no dynamic offsets in use
- [x] Prototype `UniformBufferDynamic` + dynamic offset binding for per-draw constants — `VulkanDynamicUniformRingBuffer` ring buffer class with persistent mapping, aligned allocation, per-frame reset; `BindDescriptorSetsTracked` dynamic offset overload added
- [ ] Measure descriptor update CPU reduction vs current approach (requires runtime profiling; infrastructure is in place)
- [x] Adopt if measurable improvement; document decision if deferred — infrastructure gated behind `VulkanRobustnessSettings.DynamicUniformBufferEnabled` (defaults off); adoption deferred until profiling data available

### P1 exit notes

- **Sync2/Submit2**: Backend selection is live with legacy fallback behind `VulkanRobustnessSettings.SyncBackend`. All barrier and submit paths (planner, frame, one-shot, readback, State.cs utility) route through the shared synchronization backend.
- **Barrier precision audit**: All 8 audited files complete. `AllCommandsBit` reduced from ~26 occurrences to ~6 justified uses (QueryBuffer, NormalizePipelineStages fallback, unknown-transition else branches, fault-containment safety paths). `WarnBroadBarrierStages` debug-only lint catches new regressions.
- **Descriptor pool lifecycle**: Transient compute and render program pools use reset/reuse. `FreeDescriptorSetBit` removed from hot pools (kept only for ImGui). Pool size classes (`Small`/`Medium`/`Large`) replace hardcoded 8× multiplier.
- **Dynamic UBO infrastructure**: `VulkanDynamicUniformRingBuffer` (4 MB per swapchain image, persistently mapped, aligned allocation, per-frame reset) is wired with lifecycle management. `BindDescriptorSetsTracked` supports dynamic offsets. Gated behind `VulkanRobustnessSettings.DynamicUniformBufferEnabled` (defaults off); actual adoption deferred until profiling shows measurable descriptor update reduction.
- **Tests**: 22 Vulkan validation tests pass (14 P0 + 8 P1: barrier precision coverage, pool size class inference, dynamic UBO toggle, descriptor lifetime flags).
- **Remaining unchecked items** are manual verification tasks (`validation-layer clean pass`, `visual regression comparison`, `descriptor lifetime validation`, `dynamic UBO profiling measurement`) and are intentionally carried forward rather than treated as phase blockers.

---

## P2 — Performance hardening

### Descriptor update templates

- [ ] Implement `vkUpdateDescriptorSetWithTemplate` wrapper
- [ ] Add update template for material descriptor hot path
- [ ] Add update template for compute descriptor hot path
- [ ] Benchmark descriptor update CPU time before/after

### Immutable samplers

- [ ] Define canonical immutable sampler set:
  - [ ] Linear clamp sampler
  - [ ] Nearest clamp sampler
  - [ ] Linear repeat sampler
  - [ ] Anisotropic sampler
  - [ ] Shadow comparison sampler
- [ ] Create immutable samplers at device init
- [ ] Wire `PImmutableSamplers` into descriptor layout creation for matching bindings
- [ ] Verify correct rendering with immutable samplers active

### Pipeline prewarm

- [ ] Add runtime pipeline miss counter (per pass, per material, per effect)
- [ ] Add pipeline permutation recording during QA sessions
- [ ] Serialize recorded permutations to prewarm database file
- [ ] Add startup prewarm step that compiles from database
- [ ] Add database versioning and invalidation rules
- [ ] Keep existing persistent Vulkan driver cache alongside engine-level cache
- [ ] Document prewarm workflow for QA and CI

### Push constants for GPU-driven rendering

- [ ] Identify per-draw data suitable for push constants (bindless material/instance index)
- [ ] Identify per-dispatch data suitable for push constants in compute-heavy Vulkan paths (small filter / resolve / compaction constants)
- [ ] Add push constant range to GPU-driven pipeline layouts
- [ ] Record push constant updates in command buffer recording path
- [ ] Measure per-draw descriptor update reduction

### Stress and regression testing

- [ ] Add descriptor pressure stress test (exhaust pool capacity, verify recovery)
- [ ] Add memory pressure stress test (simulate OOM, verify fallback)
- [ ] Add async compute + transfer overlap stress test (verify queue ownership)
- [ ] Add watchdog assertions for queue-family ownership transfer correctness
- [ ] Run stress tests in CI/nightly when infrastructure available

### Readback latency benchmarks

- [ ] Benchmark screenshot readback latency before/after HostCached readback path
- [ ] Benchmark depth readback latency before/after HostCached readback path
- [ ] Benchmark luminance readback latency before/after HostCached readback path

### Split-barrier experiments

- [ ] Identify candidate workloads with enough work between signal/wait
- [ ] Prototype `vkCmdSetEvent2` / `vkCmdWaitEvents2` for candidate workloads
- [ ] Measure latency hiding benefit vs regular barrier
- [ ] Adopt only where measured benefit exists; document results

### Profiling recipe documentation

- [ ] Add Nsight profiling guide with expected pass/fail indicators
- [ ] Add RGP profiling guide with expected pass/fail indicators
- [ ] Add ARM/perfdoc profiling guide (tiler-specific concerns)
- [ ] Document how to detect oversync, bandwidth waste, and decompression hits
- [ ] Add a feature-stage profiling checklist for hybrid pipelines (ray trace / temporal accumulation / spatial filter / upsample) where those paths exist

### Rendering correctness validation

- [ ] Validate line primitive rendering on Vulkan (visual + automated)
- [ ] Validate point primitive rendering on Vulkan (visual + automated)
- [ ] Validate line strip primitive rendering on Vulkan (visual + automated)

---

## Validation checklist (per phase)

### Unit tests

- [ ] Add/run targeted Vulkan unit tests for each touched subsystem
- [ ] Confirm no regressions in existing `VulkanTodoP2ValidationTests`
- [ ] Confirm no regressions in `GpuIndirectPhase3PolicyTests`
- [ ] Confirm no regressions in `GpuCullingPipelineTests`

### Validation layers

- [ ] Run full editor session with Vulkan validation layers enabled
- [ ] Confirm zero new validation errors for changed paths
- [ ] Confirm zero new validation warnings for changed paths

### Editor smoke checks

- [ ] Scene renders correctly (3D geometry, materials, lighting)
- [ ] Window resize produces correct swapchain recreation
- [ ] Screenshot/readback returns correct pixel data
- [ ] Indirect draw path submits correctly
- [ ] UI batch rendering produces visible output
- [ ] ImGui overlay renders correctly

### Feature path verification

- [ ] Indirect-count draw path works when hardware supports it
- [ ] Descriptor indexing / bindless path works when enabled
- [ ] Multiview / VR stereo path works when enabled
- [ ] Ray tracing pipeline / acceleration structure paths degrade cleanly when unsupported or disabled by feature profile
- [ ] Timeline semaphore frame sync is stable under load

### Negative / resilience tests

- [ ] Simulated OOM triggers fallback without crash
- [ ] Device-lost handling recovers or exits cleanly
- [ ] Invalid pass-index emission is caught and logged (not silent)

---

## Rollout strategy

- [ ] Ship each phase behind runtime toggles
- [ ] Enable new backend in CI first
- [ ] Promote to dev-default after CI is stable
- [ ] Promote to release-default after dev is stable
- [ ] Keep one fallback release window for allocator migration
- [ ] Keep one fallback release window for sync2 migration
- [ ] Remove legacy allocator path after two stable milestones with no regressions
- [ ] Remove legacy sync path after two stable milestones with no regressions
