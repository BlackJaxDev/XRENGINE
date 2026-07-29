# Advanced Render Pipeline GPU Scene/Material Contract Slice - 2026-07-29

Status: Complete
TODO: [02 - GPU Scene And Material Data Contract](../../todo/rendering/architectural-refactor/02-gpu-scene-and-material-data-contract-todo.md)

## Outcome

Document 02 is complete. The renderer now has one backend-neutral,
GPU-addressable scene/material contract that desktop `AdvancedRenderPipeline`
and OpenXR eye `RvcRenderPipeline` consumers can read independently. The two
pipelines retain separate output topology, command recording, frame slots, and
temporal histories.

This slice establishes the data, upload, lookup, and shader-access contracts.
Live scene extraction, deformation scheduling, culling, and indirect command
generation begin in document 03.

## Scene And Geometry

- Eight-byte generational handles reserve slot zero as invalid and reject stale
  rows after removal or slot reuse.
- Fixed-capacity record tables separate stable logical handles from compact
  physical rows. Add/remove/replace, explicit-boundary growth, dirty rows,
  allocation-free compaction, and uploadable dependent-table remaps are all
  explicit.
- A packed logical-to-dense lookup image covers draw, instance, transform,
  deformation, render-state, editor-identity, geometry, material, kernel, and
  layout tables. Ordinary publication copies only changed logical ranges;
  capacity changes rebuild and publish the full image at a frame boundary.
- A draw row resolves instance, geometry, material, deformation, render state,
  editor identity, and current/previous transform rows without managed renderer
  identity. The diagnostic snapshot exposes all corresponding stable and dense
  IDs for capture tooling.
- Geometry records preserve the same logical primitive identity across static,
  pre-skinned current/previous, and meshlet-local sources. Scene-owned immutable
  arenas, cooked-layout version checks, residency state, skip behavior, and
  explicit resident fallback behavior are defined.

## Materials And Global Resources

- Material rows carry stable row and generation IDs, a shared shading-kernel
  identity, layout hash, render-state class, coverage, required attributes,
  packed constant/texture ranges, feature flags, and explicit eligibility.
- Layout validation rejects undeclared, duplicate, or type-mismatched authored
  values. Kernel identity is separate from material-instance identity, and
  shader-cache keys contain only the kernel/layout/vertex/coverage/view/backend
  axes.
- Material header, constant-word, and texture-reference arenas publish bounded
  dirty ranges and independent generations. Replacement appends immutable
  payload data without allocating when capacity is already available.
- View records include current/previous jittered and unjittered matrices,
  render/output dimensions, layer, history, and depth state. Light, shadow,
  probe, environment, decal, GI, texture, and sampler records share the same
  packed layout policy.
- Texture and sampler references lower to OpenGL bindless/array or Vulkan
  descriptor-indexing/heap encodings without changing logical identity.
  Missing or stale resources select a defined fallback and publish delayed
  diagnostic counters.
- Compatible global resource tables bind once per command-buffer scope.

## Upload And Shader Contracts

- The frame upload arena provides pinned, persistently addressable host regions
  for instance, view, deformation-job, light, and material streams.
- Current/previous slots rotate behind fence/timeline completion. Capacity
  grows only at a frame boundary; bounded overflow generations retire by
  completion value without a whole-device wait.
- Dirty writes coalesce into fixed-capacity backend-neutral copy plans, with
  telemetry for bytes, ranges, growth, overflow, deferral, and retirement.
- The shared GLSL access library defines identical logical accessors for
  OpenGL and Vulkan, explicit row-major/row-vector matrix conventions, CPU/GPU
  size and offset checks, branchless production generation rejection, and an
  optional diagnostic bounds mode.

## Validation

- Runtime rendering project build: passed with 0 errors.
- Unit-test project build: passed with 0 errors.
- Focused document-02 contract suite: 43 passed, 0 failed.
- Combined advanced pipeline, resource lifecycle, desktop/XR routing, stereo
  post-process, OpenXR timing, and upload contract suite: 269 passed, 0 failed.
- Allocation tests cover warmed record mutation/compaction, logical lookup
  publication, material replacement, and frame extraction/copy planning.
- Packed-byte round trips, CPU/GPU offsets, stale generations, every scene-table
  remap, material dirty updates, backend-neutral copy plans, and matching
  OpenGL/Vulkan logical shader access are covered.

Builds retained only the repository's pre-existing Magick.NET `NU1902`
advisory.

## Next Slice

Start document 03 by extracting the live render scene into these records,
coalescing skeleton/blendshape work into deformation jobs, and producing
zero-readback visibility and indirect-preparation data shared by desktop and
eye consumers.
