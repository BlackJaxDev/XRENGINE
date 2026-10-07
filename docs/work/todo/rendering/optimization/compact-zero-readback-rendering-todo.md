# Compact Zero-Readback Rendering TODO

Last Updated: 2026-10-06
Status: Active
Architecture: [Mesh Submission Strategies](../../../../architecture/rendering/mesh-submission-strategies.md), [GPU Record Layouts](../../../../architecture/rendering/gpu-record-layouts.md)
Validation: [GPU-Driven Submission Validation](../../../testing/rendering/gpu-driven-submission-validation.md)

## Current State
The bounded Vulkan implementation has a three-tier compact data model, portable workgroup prefix scan, one clamped reservation per workgroup and tier, bounded indirect output, coalesced compute-to-indirect barriers, Vulkan indirect-count consumption, diagnostic-only scan and readback modes, delayed fence-polled counters, and source contracts. The optimized subgroup rung, exhaustive runtime matrix, GPU trace acceptance, scaling crossover, and workstream-07 Hi-Z work remain open.

## Open Code Items

### Active-list data model
- [ ] Define active draw, material, state-class, and bucket buffers with explicit capacities and ownership. `GPUScene`, `GPURenderPassCollection`. Done when: each buffer has a named owner, capacity rule, and writer.
- [ ] Make GPU culling write compact active draw IDs. Culling shaders and `GPUScene` buffers. Done when: production culling does not preserve upper-bound scene-size lists as real work.
- [ ] Make material scatter consume active draw IDs. `GPURenderMaterialScatter.comp`. Done when: scatter work is proportional to active draws.
- [ ] Make bucket submission consume active buckets. `HybridRenderingManager`, scatter shaders. Done when: production submission does not loop full material-table capacity for strict paths.
- [ ] Add static-scene reuse rules. `GPURenderPassCollection`, `GPUScene`. Done when: unchanged camera, topology, BVH, material table, and visibility inputs short-circuit safely.
- [ ] Keep diagnostic full scans behind explicit flags and profiler labels. Rendering diagnostics. Done when: full scans cannot run unnoticed in production zero-readback.

### Compaction and overflow
- [ ] Implement subgroup or wave partitioned prefix sums for stream compaction. Compaction shaders. Done when: production compaction can use a subgroup rung where supported.
- [ ] Use one workgroup-level atomic to reserve each group's output span. Compaction shaders. Done when: per-survivor `atomicAdd` is absent from production compaction.
- [ ] Provide a fallback compaction shader for hardware without subgroup arithmetic. Shader set and profiler labels. Done when: fallback is reported as a lower rung.
- [ ] Add counters for input count, survivor count, group count, atomics, and compaction time. Stats and profiler capture JSON. Done when: each counter appears in profile output.
- [ ] Size active-list and indirect-output buffers from historical visible counts with a safety multiplier. GPU-driven buffer allocation code. Done when: capacities adjust across frames and never mid-frame.
- [ ] Add capacity checks to every active-list and indirect-output writer. Shaders. Done when: overflow cannot corrupt adjacent buffers or indirect-count arguments.
- [ ] Add conservative overflow handling. Shaders and resolver policy. Done when: overflow either emits conservative GPU-visible work or reports an explicit unsupported result without hidden CPU-direct switching.
- [ ] Surface overflow in editor diagnostics and profile capture JSON. Editor diagnostics and stats. Done when: overflow has a visible counter and message.
- [ ] Add deterministic tests for empty input, exact capacity, overflow, and resize-on-next-frame behavior. `XREngine.UnitTests/Rendering/`. Done when: each case asserts counts, diagnostics, and guard safety.

### Hi-Z integration
- [ ] Define one render-graph contract for last-frame and current-frame Hi-Z consumers. Render graph and `GPURenderPassCollection`. Done when: producers and consumers have explicit resource declarations.
- [ ] Complete one-phase Hi-Z mode. `GPURenderOcclusionHiZ.comp`, pass collection. Done when: accepted work renders and skipped reasons are visible.
- [ ] Complete current-frame two-pass Hi-Z validation hooks. `GPURenderPassCollection.TwoPassOcclusion.cs`. Done when: phase-one and phase-two draw counts and diagnostics are published.
- [ ] Keep one-phase mode available for diagnostics and cheaper cases. Resolver and settings. Done when: the active mode appears in profiler output.
- [ ] Add stereo-safe Hi-Z handling. Hi-Z shaders and view resources. Done when: stereo depth sources use a per-eye chain, array variant, or explicit conservative fallback.
- [ ] Add tests for missing depth, stale depth, stereo depth, dirty bypass, and no-depth fallback. `XREngine.UnitTests/Rendering/`. Done when: each case reports the expected skipped reason or result.

### Barriers, submission, and diagnostics
- [ ] Group compute dispatches by resource dependency before barriers. Render graph and pass collection. Done when: redundant broad barriers are not emitted between independent stages.
- [ ] Replace per-dispatch broad barriers with resource and stage-specific grouped barriers. Backend barrier code. Done when: GPU traces show fewer redundant bubbles.
- [ ] Emit command-buffer and shader-storage barriers only when needed. Backend barrier code. Done when: barriers match following usage.
- [ ] Make GPU timestamp query density opt-in and pass-level by default. Profiler and query code. Done when: timestamp queries do not add per-dispatch default overhead.
- [ ] Ensure OpenGL uses indirect-count support where available. OpenGL renderer. Done when: `ARB_indirect_parameters` or equivalent path submits GPU-written counts.
- [ ] Ensure Vulkan uses indirect-count draw. Vulkan renderer. Done when: `vkCmdDrawIndexedIndirectCount` or equivalent path consumes the count buffer.
- [ ] Add source-contract checks that prevent CPU draw-count readback in `GpuIndirectZeroReadback`. `XREngine.UnitTests/Rendering/`. Done when: source tests fail on current-frame count readback.
- [ ] Keep diagnostic count readback in instrumented or delayed profiler paths only. Diagnostics code. Done when: strict zero-readback profiles assert zero current-frame readback bytes.
- [ ] Publish active draw count, bucket count, empty-bucket skips, full bucket scans, indirect command count, Hi-Z phase counts, and overflow counts. Stats and profile capture JSON. Done when: each field is present.

## Decisions Needed
- [ ] Choose the production Hi-Z mode default after workstream-07 evidence passes. Owner: rendering lead.

## Out Of Scope
- CPU direct state-cache optimization.
- Material row layout generation beyond the active zero-readback consumption contract.
