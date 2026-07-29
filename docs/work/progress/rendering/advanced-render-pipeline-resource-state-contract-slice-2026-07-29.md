# Advanced Render Pipeline Resource/State Contract Slice - 2026-07-29

Status: Complete
TODO: [01 - Pipeline Identity And Frame Contract](../../todo/rendering/architectural-refactor/01-pipeline-identity-and-frame-contract-todo.md)

## Outcome

Document 01 is complete. The inactive advanced frame skeleton now has immutable
resource ownership, capacity, frame-slot, synchronization, and command-packet
reuse contracts without allocating production visibility resources early.
Original-pipeline desktop and emulated-stereo seed references were also
captured so later work has a concrete correctness/performance target.

## Resource And State Contracts

- `AdvancedRenderResourceOwnershipContract` defines pipeline-persistent,
  frame-slot transient, temporal-history, imported, and external ownership,
  including allocation, disposal, binding, and synchronization responsibility.
- `AdvancedRenderResourceProfile` combines the full output target profile,
  capability-selected encodings, frame-slot count, shader family, and every
  declared advanced capacity into one immutable structural value.
- `AdvancedRenderResourceGenerationKey` compares that complete value directly;
  it does not collapse layout state into a lossy hash.
- The current inactive pipeline captures the selected backend encodings but
  reserves zero draw, geometry, material, light, deformation, visibility,
  froxel, or transparency capacity. Later documents must add capacity/profile
  fields with the first corresponding resource declarations.

## Frame Slots And Synchronization

- Current/previous indices rotate over a validated minimum of two slots, with
  three slots as the default.
- A slot can be reused only after its last fence/timeline completion value has
  completed. OpenGL uses a fence; Vulkan selects a timeline semaphore when
  available and a fence otherwise.
- Four stable boundaries cover compute preparation to visibility raster,
  visibility raster to compute classification/shading, compute shading to late
  graphics, and final graphics to presentation.
- Each boundary carries logical Vulkan stage/access/layout state and the
  matching OpenGL memory-barrier mask.

## Command Reuse

Exactly five structural generations can invalidate a recorded packet:
topology, capacity, binding, shader, and resource. GPU-written counts,
visibility, transforms, and material contents refresh behind stable bindings
and do not invalidate topology.

## Desktop And XR Separation

These contracts apply to the advanced desktop pipeline. OpenXR eyes remain
owned by independent `RvcRenderPipeline` instances. Both paths are expected to
consume the same eventual scene/mesh/material records and compatible temporal,
froxel, GI, and post-processing feature configuration, while output-local
resources and histories remain independent.

## Reference Baseline

The Release/OpenGL seed cohort used `DefaultRenderPipeline` at 1920x1080,
VSync off, TAA, with static Sponza geometry, animated skeletal/morph content,
mixed deferred/forward materials, moving lights, water/transparency,
atmosphere, fog, decal, and post-processing.

- Desktop 146-command pinned view: p50 `10.6553-10.9496 ms`, p95
  `13.6565-14.0813 ms`, and p99 `16.5630-16.6779 ms`.
- Emulated single-pass stereo plus desktop preview: p50
  `12.0770-12.2870 ms`, p95 `15.9288-16.3129 ms`, and p99
  `19.9898-20.5149 ms`.
- The current original output is visibly washed out. The artifact and
  isolation attempts are recorded in
  [the baseline investigation](../../investigations/rendering/default-reference-baseline-capture-2026-07-29.md).
- Exact host data, captures, and CPU profiler dumps are under
  `Build/_AgentValidation/renderer-root-trace/baseline-default-20260729/`.

This is the document-01 seed reference. Document 10 still owns the exact named
cohort matrix, matched advanced images, longer sampling, production GPU timing,
moving-camera tails, and OpenXR runtime evidence.

## Validation

- Runtime rendering project build: passed with 0 errors.
- Unit-test project build: passed with 0 errors.
- Affected advanced pipeline, resource lifecycle, purpose routing, RVC,
  stereo-post, and OpenXR timing suite: 225 passed, 0 failed.
- Resource/state tests cover every target-profile and capacity field,
  frame-slot rotation and completion, both backend synchronization encodings,
  all five structural invalidation channels, and mutable GPU frame-data reuse.

Both builds retained the repository's pre-existing Magick `NU1902` advisory.

## Next Slice

Start document 02 with the canonical GPU scene identities and records: stable
draw/instance/geometry handles, immutable record layouts, and the shared
scene-data interface consumed by desktop Advanced and OpenXR RVC paths.
