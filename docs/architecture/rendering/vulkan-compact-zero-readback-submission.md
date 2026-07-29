# Vulkan Compact Zero-Readback Submission

## Production Contract

`GpuIndirectZeroReadback` defaults to `BindlessMaterialTable`. Current-frame
submission must not map or inspect a GPU count, active-material list, active
bucket list, or overflow flag. Missing capabilities skip the affected pass with
a throttled warning; they never switch to CPU direct or a full-capacity bucket
scan.

The old paths remain available only as explicitly named diagnostics:

- `FullBucketScanDiagnostic`
- `ActiveBucketListReadbackDiagnostic`

Their obsolete enum aliases exist only for configuration compatibility.

## Compact Representation

Each `GPURenderPassCollection` owns one compact output range for each geometry
atlas tier:

| Group | Count element | Indirect range |
| --- | ---: | --- |
| Static | 0 | `0 * MaxDrawsPerTier` |
| Dynamic | 1 | `1 * MaxDrawsPerTier` |
| Streaming | 2 | `2 * MaxDrawsPerTier` |

The current render pass and material ID remain in `DrawMetadata`. Material
properties and texture descriptor indices live in the stable GPU material
table. The effective table row is rebuilt from the pass override or
depth/normal variant before the pass, so the compact command does not need a
CPU material bucket. The CPU submits at most three
`DrawIndexedIndirectCount` operations per supported pass. A zero GPU count is
a harmless no-op.

The Vulkan binding rung is capability-probed through
`IMaterialTableBackendCapability`. Production uses the descriptor-indexed
`Bindless` rung when available. The selected rung and reason are reported as
`gpu_material_binding_rung` and `gpu_material_binding_rung_reason`.

## Workgroup Compaction And Capacity

`GPURenderMaterialScatter.comp` uses a portable 64-lane workgroup prefix scan.
Each workgroup makes one clamped atomic reservation per atlas tier, then derives
per-lane output positions from the shared scan. The production branch has no
per-survivor global atomic. This is the declared lower-capability rung and is
reported as `WorkgroupPrefixScan64`; a future subgroup-optimized shader may be
added without changing the output contract.

Capacity is bounded to twice the source command capacity per tier: one selected
LOD command and, during a transition, one previous-LOD command. Reservations
clamp the published GPU count to that capacity. Overflow sets the GPU overflow
flag and cannot index outside the indirect buffer. Capacity changes occur in
the pass-buffer preparation phase, never during submission.

## Synchronization And Diagnostics

The scatter dispatch publishes shader-storage and indirect-command writes. The
draw stage emits one coalesced `ShaderStorage | Command` barrier before the
three tier groups, then Vulkan consumes the count buffer with
`vkCmdDrawIndexedIndirectCount`.

Normal production capture reports:

- configured material slots and material pass groups;
- selected material-binding and compaction rungs;
- full scans, active buckets, overflow, readback bytes, and mappings;
- Vulkan indirect API calls, requested/consumed commands, frame operations,
  and primary reuse.

Optional Vulkan counter tracing uses a fence-polled staging ring. It calls
`vkGetFenceStatus`; it never waits for the current frame and never controls
submission. The trace is diagnostic and therefore not valid promotion
evidence.

## Supported And Unsupported Variants

The compact generated fragment variants currently cover:

- deferred opaque material-table shading;
- forward opaque and masked depth/normal prepass, including descriptor-indexed
  alpha cutoff and normal-map evaluation;
- pass override and per-material depth/normal table rows;
- static, dynamic, and streaming atlas tiers.

Arbitrary forward shaders and exact transparency methods such as per-pixel
linked lists and depth peeling do not yet have a semantically equivalent
generated material-table fragment program. Scheduled unsupported passes are
counted in `gpu_driven_unsupported_compact_passes` and emit a throttled warning.
They are skipped without CPU or `FullBucketScanDiagnostic` fallback. This
visible limitation must be removed before a scene using those variants can be
promoted.

## Optional Visibility Input

`IGpuCompactVisibilityInput` is the stable handoff for a future GPU visibility
producer. It supplies a GPU command-ID buffer, GPU count buffer, capacity,
resource generation, and an explicit conservative-bypass state. Consumers do
not inspect its count on the CPU. Workstream 07 can therefore add or bypass
Hi-Z without changing the compact submission topology.
