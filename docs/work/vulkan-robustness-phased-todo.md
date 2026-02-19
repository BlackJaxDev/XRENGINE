# Vulkan Robustness Phased TODO (2026)

This roadmap turns the findings in:
- `docs/work/modern-vulkan-render-pipeline-summary.md` (sections 1-11 guidance)
- `docs/work/modern-vulkan-render-pipeline-summary.md` section 12 (XREngine audit)

into an execution plan to make the Vulkan renderer robust across desktop and tiler/mobile-like constraints.

---

## Goals

1. Eliminate architectural blockers (memory allocation model, sync API generation gaps).
2. Reduce CPU overhead in descriptor + submission hot paths.
3. Tighten synchronization precision and bandwidth behavior.
4. Improve failure resilience (OOM fallback, validation, regressions).
5. Add durable observability and CI checks so quality does not regress.

## Non-goals (for this plan)

- Rewriting the full render graph architecture (already strong).
- Replacing existing feature-profile gating model.
- Large-scale content/pipeline authoring redesign in a single phase.

---

## Phase overview

| Phase | Name | Priority | Outcome |
|---|---|---|---|
| 0 | Baseline + Safety Rails | P0 | Measurable baseline, regression checks, rollout toggles |
| 1 | Memory Allocator Backbone | P0 | Suballocation architecture in place, allocation pressure removed |
| 2 | Readback + Host Memory Correctness | P0 | Fast and correct GPU→CPU readback path |
| 3 | Descriptor Pool + Update Modernization | P1 | Lower descriptor CPU cost, cleaner pool lifecycle |
| 4 | Sync2/Submit2 Migration | P1 | Modern synchronization API with planner parity |
| 5 | Barrier Precision + Bandwidth Pass | P1 | Reduced oversync and load/store waste |
| 6 | Pipeline Robustness + Prewarm | P2 | Fewer runtime hitches, stronger startup state |
| 7 | Advanced Robustness Hardening | P2 | Long-tail reliability + perf guardrails |

---

## Phase 0 — Baseline + Safety Rails (P0)

### Tasks
- [ ] Add `VulkanRobustnessSettings` feature toggles for each major migration area:
  - allocator backend (`Legacy` vs `Suballocator`)
  - sync backend (`Legacy` vs `Sync2`)
  - descriptor update backend (`Legacy` vs `Template`)
- [ ] Add per-frame counters + logs for:
  - memory allocation count and bytes by memory class
  - descriptor pool creates/destroys/resets
  - queue submit count and command buffer count
  - `AllCommandsBit` barrier count
- [ ] Add startup capability snapshot log (descriptor indexing, draw indirect count, multiview, timeline, sync2 support).
- [ ] Add CI test gate for Vulkan-focused unit tests already in solution.

### Candidate files
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanFeatureProfile.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.Core.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Init.cs`

### Definition of done
- [ ] Runtime can print baseline metrics each frame or on interval.
- [ ] Each new backend path can be enabled/disabled independently.
- [ ] CI fails when Vulkan regression suite fails.

---

## Phase 1 — Memory Allocator Backbone (P0)

### Tasks
- [ ] Introduce allocator abstraction (`IVulkanMemoryAllocator` or equivalent) with implementations:
  - `LegacyAllocator` (current behavior)
  - `BlockSuballocator` (new default target)
- [ ] Implement block-based suballocation by memory class:
  - device-local
  - host-visible upload
  - host-visible + host-cached readback
- [ ] Ensure `bufferImageGranularity` safety:
  - either separate image/buffer pools or correct alignment/padding rules
- [ ] Implement dedicated-allocation decision path (large resources and driver hints).
- [ ] Implement `OutOfDeviceMemory` fallback path for eligible resources.
- [ ] Add lazy allocation option for transient depth/MSAA attachments where supported and safe.
- [ ] Migrate allocation call sites incrementally behind feature toggle.

### Candidate files
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanRenderer.State.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Init.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkDataBuffer.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkImageBackedTexture.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderBuffer.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/SwapChain.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanResourceAllocator.cs`

### Definition of done
- [ ] Per-resource `vkAllocateMemory` is removed from core hot paths.
- [ ] Allocation count remains well below hardware limits under stress scenes.
- [ ] No validation errors from aliasing/alignment.
- [ ] Fallback path behaves deterministically under simulated OOM.

---

## Phase 2 — Readback + Host Memory Correctness (P0)

### Tasks
- [ ] Change readback staging preference to `HostVisible | HostCached`.
- [ ] Add coherent/non-coherent handling policy:
  - invalidate mapped ranges for non-coherent reads
  - flush mapped ranges for non-coherent writes
- [ ] Keep fallback to `HostCoherent` if host-cached type is unavailable.
- [ ] Benchmark screenshot/readback and async luminance readback before/after.

### Candidate files
- `XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.Readback.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkDataBuffer.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanStagingManager.cs`

### Definition of done
- [ ] Readback correctness preserved across all tested GPUs.
- [ ] CPU-side readback latency improved measurably on at least one desktop GPU.
- [ ] Memory property fallback path covered by tests/log assertions.

---

## Phase 3 — Descriptor Pool + Update Modernization (P1)

### Tasks
- [ ] Convert transient descriptor pool lifecycle from destroy/recreate to reset/reuse (`vkResetDescriptorPool`).
- [ ] Remove `FREE_DESCRIPTOR_SET_BIT` from non-essential high-frequency pools.
- [ ] Introduce descriptor pool size classes (e.g., small/medium/large pass schemas).
- [ ] Implement descriptor update templates for hot material/compute update paths.
- [ ] Introduce immutable samplers for canonical sampler states.
- [ ] Evaluate migration to dynamic UBO offsets for per-draw constants where profitable.

### Candidate files
- `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanComputeDescriptors.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanRenderer.ImGui.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanDescriptorLayoutCache.cs`

### Definition of done
- [ ] Descriptor pool reset reuse is default in frame loop.
- [ ] Descriptor alloc/update CPU time reduced in representative scenes.
- [ ] No descriptor-set lifetime validation errors.

---

## Phase 4 — Sync2/Submit2 Migration (P1)

### Tasks
- [ ] Add sync backend abstraction so planner can emit either legacy barriers or sync2 barriers.
- [ ] Migrate queue submission from `vkQueueSubmit` to `vkQueueSubmit2`.
- [ ] Migrate barrier emission from `vkCmdPipelineBarrier` to `vkCmdPipelineBarrier2`.
- [ ] Keep timeline semaphore semantics equivalent during migration.
- [ ] Preserve fallback path until parity is proven.

### Candidate files
- `XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.Core.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanBarrierPlanner.cs`

### Definition of done
- [ ] Sync2 backend passes full frame with validation clean.
- [ ] No functional regressions vs legacy path.
- [ ] Legacy backend remains available as temporary fallback toggle.

---

## Phase 5 — Barrier Precision + Bandwidth Pass (P1)

### Tasks
- [ ] Enumerate all `AllCommandsBit` barrier call sites and classify:
  - render-graph-reachable (must tighten)
  - fault-containment/unknown legacy path (documented exceptions)
- [ ] Replace broad masks with minimal producer/consumer stage+access pairs.
- [ ] Add lint/assert diagnostics for newly introduced broad masks in hot paths.
- [ ] Update pass defaults and call-site conventions to minimize unnecessary load/store:
  - prefer `DontCare` when previous/final contents are irrelevant
- [ ] Validate no decompression-heavy transitions are introduced for MSAA paths.

### Candidate files
- `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanRenderer.State.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.Blit.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.Readback.cs`
- `XRENGINE/Rendering/RenderGraph/RenderPassBuilder.cs`
- `XRENGINE/Rendering/RenderGraph/RenderGraphDescribeContext.cs`

### Definition of done
- [ ] `AllCommandsBit` hot-path usage is near-zero and justified where retained.
- [ ] Bandwidth-sensitive passes show reduced unnecessary load/store usage.
- [ ] Validation and visual output remain correct.

---

## Phase 6 — Pipeline Robustness + Prewarm (P2)

### Tasks
- [ ] Add runtime pipeline miss telemetry (per pass/material/effect).
- [ ] Record and serialize commonly used pipeline permutations from QA sessions.
- [ ] Add startup prewarm step from recorded database.
- [ ] Keep persistent Vulkan driver cache, but add engine-level permutation cache layer.

### Candidate files
- `XRENGINE/Rendering/API/Rendering/Vulkan/VulkanPipelineCache.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs`
- `XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.Pipeline.cs`

### Definition of done
- [ ] Pipeline compile hitches reduced in gameplay-critical scenes.
- [ ] Prewarm database versioning + invalidation rules are documented.

---

## Phase 7 — Advanced Robustness Hardening (P2)

### Tasks
- [ ] Introduce per-phase stress scenarios:
  - descriptor pressure
  - memory pressure / OOM simulation
  - heavy async compute + transfer overlap
- [ ] Add watchdog assertions for queue ownership transfer correctness.
- [ ] Add optional event-based split-barrier experiments in targeted workloads only.
- [ ] Expand profiling recipe docs (Nsight/RGP/ARM/perfdoc) with expected pass/fail indicators.

### Candidate files
- `XREngine.UnitTests/Rendering/*`
- `docs/work/*` (profiling playbooks)
- Vulkan render backend files touched in phases 1-6

### Definition of done
- [ ] Stress tests run in CI/nightly and are stable.
- [ ] Known perf anti-patterns are caught by tooling before merge.

---

## Cross-phase testing matrix

- [ ] Unit tests: render-graph planner, barrier planner, pipeline policy tests.
- [ ] Smoke tests: editor launch, scene load, swapchain resize, screenshot/readback.
- [ ] Feature tests: indirect-count draw path, descriptor indexing path, multiview path.
- [ ] Negative tests: simulated OOM, device-lost handling where feasible.
- [ ] Validation layers: clean run for both legacy and new backends during migration windows.

---

## Rollout strategy

1. Ship each phase behind runtime toggles.
2. Enable in CI first, then dev-default, then release-default.
3. Keep one fallback release window for each high-risk migration (allocator and sync2).
4. Remove legacy path only after two stable milestones with no regressions.

---

## Immediate next sprint recommendation

1. Start **Phase 0** and **Phase 2** together (highest ROI, lowest risk).
2. Begin **Phase 1 allocator abstraction skeleton** in parallel, without immediate full cutover.
3. Defer full **Phase 4 sync2 cutover** until allocator and descriptor pool lifecycle changes are stable.

This ordering gives early wins (readback speed + observability) while reducing migration risk for core architecture changes.