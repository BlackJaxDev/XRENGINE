# Vulkan Descriptor Lifetime Freeze - 2026-07-10

## Problem

The editor could take an extremely long time to start and appear frozen with all
desktop, preview, and OpenXR views black. Pausing the debugger repeatedly landed
in descriptor-pool ownership checks around
`descriptorSet.Pool.Handle != pool.Handle`.

This was CPU-side lifetime bookkeeping pressure, not a GPU device loss. The old
path traversed the global descriptor-set table for individual pool operations,
while mesh descriptors created large eager per-frame/per-draw allocations keyed
by mutable program/resource identities. Repeated pool retirement multiplied the
global scan cost and delayed every view's submission.

## Root Causes And Fixes

1. Pool retirement, reset, mutation validation, and destruction performed global
   descriptor-set ownership scans. A pool-to-owned-set reverse index now makes
   each operation proportional only to that pool's sets.
2. Duplicate retirement was detected after ticket capture. Pool handles are now
   reserved before capture so duplicates perform no traversal.
3. Mesh allocation identity included mutable/transient identity. It now uses the
   cached descriptor-layout handles, schema, slot shape, and set count. Resource
   changes rewrite a slot; they do not create a pool.
4. Every frame/draw slot was allocated eagerly. Slots are now allocated only on
   first use, and structural variants are LRU-bounded.
5. Physical-plan replacement globally released descriptors. Resource-to-set and
   resource-to-command-buffer reverse indexes now advance/dirtify only exact
   dependents. Pool-owned dependents are aggregated into one invalidation pass.
6. Late command-local publication threw and triggered debugger first-chance
   breaks. Submission validation now rejects and dirties the exact command buffer
   without throwing. Retirement logs are also rate-limited by object type.

The profiler initially revealed that `VkRenderProgram.BindingId` remained in the
supposedly structural key: a retained 250-frame tail created and retired 4,050
descriptor pools despite stable live-object counts. Replacing it with the actual
descriptor-layout-handle fingerprint reduced both values to zero in the final
equivalent tail.

## Validation

- Focused lifetime/Phase 4/Phase 5.2.4 contracts: 27 passed, 0 failed.
- Runtime rendering and editor builds: 0 compiler errors.
- Final live session:
  `Build/Logs/Debug_net10.0-windows7.0/windows_x64/xrengine_2026-07-10_02-39-41_pid5884`.
- Mode: Vulkan + Monado OpenXR + requested/effective `SinglePassStereo` using
  `OpenXrSinglePassCompatibility`, with desktop/mirror output and dynamic
  rendering active.
- Retained 250-frame tail:
  - descriptor pool creates/retirements: 0/0,
  - submission rejections/global fallback invalidations: 0/0,
  - live Vulkan resources: 4,669-4,727,
  - tracked descriptor sets: 2,356-2,367.
- Full run: no render exception, first-chance lifetime exception, Vulkan VUID,
  or device loss. Three startup submissions were safely rejected while streamed
  resources were replaced; no rejection recurred in the retained tail.

The strict harness could not automatically choose its five-second window because
two valid output workload identities alternated. This is a harness selection
limitation, not resource growth; the same emitted gate fields were evaluated
directly across the retained profiler tail. Machine-readable results are in
[`vulkan-core-hardening-phase524a-validation-2026-07-10.json`](../../testing/rendering/vulkan-core-hardening-phase524a-validation-2026-07-10.json).

## Result

The descriptor ownership scan/freeze path is eliminated. Descriptor resources
remain bounded in desktop and multi-output OpenXR rendering, and steady-state
program/pass rotation no longer churns descriptor pools. Phase 5.2.5 can proceed
without carrying this CPU lifetime-pressure failure forward.
