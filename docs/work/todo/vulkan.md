# Vulkan TODO Backlog (Canonical)

Last updated: 2026-03-17

This file is the canonical **active TODO** list for Vulkan work. Historical context and completed items are documented in `vulkan-report.md`. Deep architectural guidance and audit narrative are in `modern-vulkan-render-pipeline-summary.md`.

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

- [ ] Define `VulkanRobustnessSettings` class with per-area toggles:
  - [ ] Allocator backend toggle (`Legacy` vs `Suballocator`)
  - [ ] Sync backend toggle (`Legacy` vs `Sync2`)
  - [ ] Descriptor update backend toggle (`Legacy` vs `Template`)
- [ ] Wire toggles into runtime so each backend can be switched independently
- [ ] Expose toggles in editor settings UI or startup config

### Per-frame diagnostic counters

Already implemented: bind churn, pipeline cache hit/miss, barrier planner pass counts, GPU command buffer timing, queue overlap window.

Still needed:

- [ ] Memory allocation count per frame (by memory class)
- [ ] Memory bytes allocated per frame (by memory class)
- [ ] Descriptor pool create count per frame
- [ ] Descriptor pool destroy count per frame
- [ ] Descriptor pool reset count per frame
- [ ] Queue submit count per frame
- [ ] Route new counters into profiler data source

### Startup capability snapshot

- [ ] Log acceleration structure support at startup
- [ ] Log descriptor indexing support at startup
- [ ] Log draw indirect count support at startup
- [ ] Log multiview support at startup
- [ ] Log ray tracing pipeline support at startup
- [ ] Log timeline semaphore support at startup
- [ ] Log synchronization2 support at startup
- [ ] Log max memory allocation count at startup
- [ ] Log available memory heaps and types at startup
- [ ] Classify startup capabilities as `Required`, `Optional`, or `DisabledByProfile` for each major Vulkan feature family

### CI integration

- [ ] Add CI pipeline step that runs Vulkan-focused unit tests
- [ ] Configure CI to fail on Vulkan test regressions
- [ ] Verify existing `VulkanTodoP2ValidationTests` run in CI

---

## P0 — Correctness and architectural blockers

### Render-graph pass-index mismatch

- [ ] Investigate why `MeshDrawOp` emissions hit invalid pass indices
- [ ] Identify which render pipeline paths emit pass index 4+ without metadata
- [ ] Fix pass-index assignment or metadata generation to eliminate mismatch
- [ ] Remove or downgrade the fallback-to-pass warning once fixed
- [ ] Add regression test for pass-index validity

### Memory allocator backbone

Replace per-resource `AllocateMemory` (~14 call sites) with suballocation:

- [ ] Design allocator abstraction interface (`IVulkanMemoryAllocator` or equivalent)
- [ ] Implement `LegacyAllocator` wrapping current per-resource behavior
- [ ] Implement `BlockSuballocator` with configurable block sizes (16–256 MB)
- [ ] Add device-local memory pool
- [ ] Add host-visible upload memory pool
- [ ] Add host-visible + host-cached readback memory pool
- [ ] Handle `bufferImageGranularity` safety:
  - [ ] Option A: separate image and buffer pools, OR
  - [ ] Option B: correct alignment/padding within shared pools
- [ ] Query `MemoryDedicatedAllocateInfo` / `prefersDedicatedAllocation` for large resources
- [ ] Use dedicated allocations when driver indicates preference
- [ ] Add `LAZILY_ALLOCATED_BIT` for transient depth attachments where supported
- [ ] Add `LAZILY_ALLOCATED_BIT` for transient MSAA attachments where supported
- [ ] Migrate `VkDataBuffer.cs` allocation sites to allocator
- [ ] Migrate `VkImageBackedTexture.cs` allocation sites to allocator
- [ ] Migrate `VkRenderBuffer.cs` allocation sites to allocator
- [ ] Migrate `VkMeshRenderer.Uniforms.cs` allocation sites to allocator
- [ ] Migrate `SwapChain.cs` allocation sites to allocator
- [ ] Migrate `VulkanRenderer.ImGui.cs` allocation sites to allocator
- [ ] Migrate `VulkanRenderer.PlaceholderTexture.cs` allocation sites to allocator
- [ ] Migrate `VulkanRenderer.State.cs` allocation sites to allocator
- [ ] Gate all migrations behind `VulkanRobustnessSettings` allocator toggle
- [ ] Verify allocation count stays well below hardware limits under stress

### Out-of-memory fallback

- [ ] Detect `ErrorOutOfDeviceMemory` from allocation attempts
- [ ] Attempt fallback to host-visible memory for eligible resources
- [ ] Log fallback events with resource identity and size
- [ ] Add test/simulation for OOM fallback path

### Stencil index readback

- [ ] Implement `GetStencilIndex` in `Init.cs` (currently throws `NotImplementedException`)
- [ ] Add stencil attachment readback via staging buffer
- [ ] Test stencil-index picking path end-to-end

### Readback memory type fix

- [ ] Change `Drawing.Readback.cs` staging buffer preference to `HostVisible | HostCached`
- [ ] Add `vkInvalidateMappedMemoryRanges` for non-coherent reads
- [ ] Add `vkFlushMappedMemoryRanges` for non-coherent writes
- [ ] Add fallback to `HostCoherent` if `HostCached` is unavailable on device
- [ ] Update `VulkanStagingManager` readback pool memory type selection
- [ ] Benchmark screenshot readback latency before/after
- [ ] Benchmark depth readback latency before/after
- [ ] Benchmark luminance readback latency before/after

---

## P1 — Synchronization modernization

### Sync2 / Submit2 migration

- [ ] Design sync backend abstraction interface
- [ ] Implement legacy sync backend (wrapping current `vkCmdPipelineBarrier` / `vkQueueSubmit`)
- [ ] Implement sync2 backend (`vkCmdPipelineBarrier2` / `vkQueueSubmit2`)
- [ ] Migrate `Drawing.Core.cs` submission path to Submit2
- [ ] Migrate `CommandBuffers.cs` one-time-submit path to Submit2
- [ ] Migrate `Drawing.Readback.cs` submission path to Submit2
- [ ] Migrate `VulkanBarrierPlanner` barrier emission to sync2
- [ ] Migrate utility/transition barrier helpers in `CommandBuffers.cs` to sync2
- [ ] Migrate utility/transition barrier helpers in `VulkanRenderer.State.cs` to sync2
- [ ] Preserve timeline semaphore semantics through migration
- [ ] Keep legacy path as fallback behind `VulkanRobustnessSettings` sync toggle
- [ ] Run full validation-layer-clean pass with sync2 enabled
- [ ] Run visual regression comparison (sync2 vs legacy)

### Barrier precision audit

~26 `AllCommandsBit` usages across the codebase:

- [ ] Audit `VulkanBarrierPlanner.cs` usages (3 sites)
- [ ] Audit `VulkanRenderer.State.cs` usages (1 site)
- [ ] Audit `VkDataBuffer.cs` usages (3 sites)
- [ ] Audit `VkImageBackedTexture.cs` usages (5 sites)
- [ ] Audit `VulkanRenderer.PlaceholderTexture.cs` usages (2 sites)
- [ ] Audit `CommandBuffers.cs` usages (8 sites)
- [ ] Audit `Drawing.Readback.cs` usages (1 site)
- [ ] Audit `Drawing.Blit.cs` usages (2 sites)
- [ ] Replace each with minimal producer/consumer stage+access pairs where possible
- [ ] Document justified exceptions (fault-containment / unknown-prior-usage paths)
- [ ] Add lint/assert to catch newly introduced broad masks in hot paths
- [ ] Prefer `DontCare` load/store ops when previous/final contents are irrelevant
- [ ] Validate no decompression-heavy transitions for MSAA paths
- [ ] Re-count `AllCommandsBit` usages after audit to confirm reduction

---

## P1 — Descriptor pool and update modernization

### Pool lifecycle overhaul

- [ ] Replace transient compute descriptor pool destroy/recreate with `vkResetDescriptorPool`
- [ ] Replace transient render program descriptor pool destroy/recreate with `vkResetDescriptorPool`
- [ ] Add free-list reuse pattern for reset descriptor pools
- [ ] Remove `FreeDescriptorSetBit` from `VulkanComputeDescriptors.cs` pools
- [ ] Remove `FreeDescriptorSetBit` from `VkRenderProgram.cs` pools
- [ ] Keep `FreeDescriptorSetBit` for ImGui pool (justified: long-lived, individually freed)
- [ ] Verify no descriptor-set lifetime validation errors after conversion

### Pool sizing

- [ ] Introduce descriptor pool size classes (small / medium / large)
- [ ] Assign size class based on pass schema (e.g., shadow vs gbuffer vs post-process)
- [ ] Replace uniform 8× descriptor count scaling with measured sizing

### Dynamic UBO offsets

- [ ] Evaluate per-draw constant update frequency across material types
- [ ] Prototype `UniformBufferDynamic` + dynamic offset binding for per-draw constants
- [ ] Measure descriptor update CPU reduction vs current approach
- [ ] Adopt if measurable improvement; document decision if deferred

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
