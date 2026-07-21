# Physics Chain Thousands-Scale Implementation Progress

Date: 2026-07-20  
Branch: `rendering-vulkan-core-hardening`  
Tracker: [Physics Chain Thousands-Scale Optimization TODO](../../todo/physics/physics-chain-thousands-scale-optimization-todo.md)

## Current Status

Implementation is active. The current worktree advances the measurement
contract, world-owned runtime foundations, deterministic CPU oracle, explicit
quality/sleep diagnostics, GPU dependency tests, and backend failure behavior.
The component-owned production CPU/GPU data paths have not yet been fully
replaced by the target arenas, so end-to-end acceptance and hardware budgets
remain open.

## Implemented In This Pass

### Measurement contract and harness

- Added canonical scale-matrix configuration and deterministic seed policy.
- Added a settle gate that keeps spawn, upload, resource growth, and pipeline
  warmup outside the timed window.
- Timing requires both the configured duration and at least 1,000 samples.
- Added nearest-rank p50/p95/p99/minimum/maximum/mean statistics.
- Added optional raw JSON frame samples under an explicitly configured
  `Build/_AgentValidation/<run>/reports/` root.
- Benchmark cloning preserves quality-tier and automatic-sleep settings.

### World records, identity, and allocation

- Added handle-validated world slot resolution, capacity diagnostics, and
  latest-intent structural command versioning.
- Added immutable content-addressed templates with parent/depth/rest/coefficient
  data and per-world deduplication.
- Added a reusable geometrically growing generational slot arena with live,
  capacity, growth, and fragmentation diagnostics.
- Added a backend-neutral output record with current/previous palette slices,
  bounds slot, instance/output generations, mirror status, and explicit
  backend status.

### CPU backend foundations

- Added separate blittable CPU input, tree-input, particle-input, state, and
  output records.
- Added a deterministic template-driven scalar reference kernel with linear
  and branched depth ordering, fixed-step integration, reset semantics, and
  degenerate-input rejection.
- Added a world-owned CPU backend layer using generational arenas, stable
  runtime state, explicit update/reset/step APIs, and state preservation across
  arena growth.

### Quality and activity

- Added explicit named quality policies for simulation rate, solver work,
  collision enablement, catch-up limits, and palette/bounds cadence.
- Added CPU activity snapshots, wake reasons, and wake counters.
- CPU sleep now observes particle/root motion, external force, collider
  membership/pose/shape changes, and authored sleep/quality/relevance changes.

### GPU correctness and failure visibility

- Repaired compute integration tests to use the current shader path, SSBO
  bindings, host/std430 records, per-tree parameters, and current uniforms.
- Added dependency-order and exact host/shader layout contracts.
- Added explicit unavailable/unsupported/ready backend state. Unsupported GPU
  selection retains GPU work, reports no CPU fallback, and logs a rate-limited
  visible diagnostic instead of returning silently.
- Corrected isolated-mode debug/render-source lookup to consume dispatcher
  buffers; isolated requests already use unique dispatcher keys rather than
  the obsolete component-owned standalone execution method.

### Selective readback contract foundation

- Added typed fields, generational request handles, freshness metadata,
  earliest-completion/expiry frames, explicit status/rejection values,
  same-frame duplicate coalescing, byte/element caps, and cancellation.
- GPU gather, bounded staging-ring transfer, delivery counters, and worker-side
  transform mirroring remain future integration work.

## Validation

Focused isolated Release validation completed successfully:

- Benchmark contract and writer: 19 tests passed.
- Scalar CPU kernel and world-owned CPU backend: 14 tests passed, including
  exactly zero managed bytes across 1,000 warm steps.
- Physics-chain shader contracts: 7 tests passed before the backend-status
  extension; the extended focused contract fixture passed 8 tests.
- Live OpenGL compute integration: 5 tests passed, including shader
  compile/link, current uniforms, Verlet integration, and sphere collision.

The normal repository test graph is currently blocked before these fixtures by
unrelated Vulkan/rendering work already present on the branch. Current blockers
include inaccessible `VkRenderQuery.MarkResultEpochSubmitted`, missing
`RecordVulkanSwapchainRetirement`, missing `NvidiaDlssManager`, and stale
secondary-context APIs (`StreamlineFrameGenerationProvisioned` and the
`XRWindow` secondary-context constructor argument). Focused compilation of the
changed XRENGINE physics code reaches only those unrelated errors.

## Evidence Locations

Disposable focused validation projects and reports are under
`Build/_AgentValidation/`, including:

- `physics-chain-test-repair/`
- the dated physics-chain benchmark contract validation root

These paths are ignored evidence only; this progress note is the durable
record.

## Next Gates

1. Finish typed/shared collider-set records and scalar collision parity.
2. Bridge the world-owned CPU backend into production scheduling only after
   retained features match the deterministic oracle.
3. Replace component/resident-to-combined GPU ownership with stable world GPU
   arenas and a narrow backend-neutral compute adapter.
4. Add GPU active compaction/indirect arguments, specialized kernels, direct
   current/previous palette and conservative bounds output.
5. Complete selective GPU gather/staging/delivery and explicit-rate CPU mirror
   publication.
6. Run the Release 100-to-10,000-chain hardware matrix and record named
   hardware, p50/p95/p99/max, stage costs, transfers, barriers, allocations,
   and end-to-end rendering evidence before checking performance acceptance.
