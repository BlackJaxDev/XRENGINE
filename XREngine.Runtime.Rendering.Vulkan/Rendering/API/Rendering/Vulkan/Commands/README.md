# Vulkan Commands

Owns command-buffer allocation, scheduling, recording, command-chain lowering,
frame-operation queues/signatures, synchronization emission, transfers, blits,
readback, render-state application, and one-time submit helpers.

- `FrameOps/` owns operation contracts, captures, and the per-renderer queue.
- `Scheduling/` owns command-chain data and ordering contracts.
- `Recording/` owns per-domain native command emission and render-scope policy.
- `Synchronization/` lowers immutable barrier plans to Vulkan barriers.
- `Transfers/` records upload, copy, and blit operations and publishes upload
  completion state.
- `Readback/` owns observational layout restoration, pixel decoding, and CPU
  readback operations.
- `CommandBuffers/` owns cache/allocation state plus the short scheduling and
  recording lifecycle entry points.

Command recording consumes explicit contexts and immutable render-graph plans.
It may reuse owner-provided workspaces, but it must not allocate persistent
resources or introduce thread-static recording context.
