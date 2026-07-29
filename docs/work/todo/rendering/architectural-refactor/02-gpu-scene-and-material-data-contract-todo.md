# 02 - GPU Scene And Material Data Contract TODO

Last Updated: 2026-07-29
Owner: Rendering
Status: Complete
Depends On: [01 - Pipeline Identity And Frame Contract](01-pipeline-identity-and-frame-contract-todo.md)
Next: [03 - GPU Visibility Preparation And Deformation](03-gpu-visibility-preparation-and-deformation-todo.md)
Progress: [GPU Scene/Material Contract Slice - 2026-07-29](../../../progress/rendering/advanced-render-pipeline-gpu-scene-material-contract-slice-2026-07-29.md)

## Goal

Make every later visibility and shading stage consume stable GPU-addressable
records rather than managed renderer objects, per-draw uniforms, or
per-material descriptor binding.

## Contract Principles

- Use stable integer handles and generation-checked table records.
- Keep the logical records backend-neutral.
- Use buffer device address or descriptor-heap addressing only as backend
  encodings of those records.
- Store current and previous temporal inputs explicitly.
- Keep immutable asset data separate from frame-slot instance data.
- Update dirty ranges; do not rewrite complete tables when a bounded subset
  changes.

## TODO

### 1. Canonical Draw And Instance Records

- [x] Define `AdvancedDrawRecord` in its own file with stable references to
  instance, geometry, material, deformation, render-state, editor identity,
  and current/previous transform data.
- [x] Define `AdvancedInstanceRecord` with current/previous world transforms,
  bounds, visibility flags, LOD, view mask, and optional animation/deformation
  handles.
- [x] Define stable-index behavior for add, remove, compaction, and generation
  replacement.
- [x] Ensure GPUScene compaction publishes a GPU-side remap for every dependent
  visibility/history table.
- [x] Reserve explicit invalid/sentinel values and test stale-generation access.

### 2. Geometry Database

- [x] Define mesh records for vertex/index bases, counts, primitive topology,
  meshlet ranges, vertex-layout ID, bounds, and material-section ranges.
- [x] Move visibility-readable vertex and index data into scene-owned arenas or
  stable immutable buffers.
- [x] Define backend-neutral buffer reference plus byte/element offset access.
- [x] Support static, pre-skinned current, pre-skinned previous, and
  meshlet-local geometry without changing visibility payload meaning.
- [x] Version cooked geometry payloads when an incompatible runtime layout is
  required.
- [x] Define residency and explicit missing-geometry behavior.

### 3. Material And Kernel Database

- [x] Extend the GPU material table with stable material-row ID,
  shading-kernel ID, material-layout hash, render-state class, coverage mode,
  required-attribute mask, texture references, constants, and feature flags.
- [x] Separate material instance data from kernel/pipeline identity so many
  material rows share one kernel.
- [x] Define opaque, masked, transparent, refractive, unlit, and unsupported
  eligibility flags without inferring architecture from shader filename.
- [x] Reject unknown per-material values that cannot be represented by the
  declared material layout.
- [x] Add material-table dirty-range uploads and generation tracking.
- [x] Add shader-cache keys for kernel, layout, vertex-format, coverage, view,
  and backend encoding without keying by material instance.

### 4. Global Resource Tables

- [x] Define stable view/camera records including jittered and unjittered
  matrices, current/previous matrices, depth convention, render size, output
  size, and per-view layer.
- [x] Define light, shadow, probe, environment, decal, and GI resource records
  consumed by native shading.
- [x] Define global texture and sampler reference encodings for Vulkan
  descriptor indexing/descriptor heap and OpenGL bindless/array rungs.
- [x] Bind the selected global resource tables once per compatible
  command-buffer scope.
- [x] Ensure resource non-residency produces a defined fallback value and
  delayed diagnostic counter.

### 5. Frame-Slot Upload And Lifetime

- [x] Add or reuse persistently mapped frame-slot arenas for changed instance,
  view, deformation-job, light, and material-table data.
- [x] Pre-size from high-water marks and grow only at an explicit frame
  boundary.
- [x] Define overflow allocations and fence-driven retirement; never wait for
  the whole device to recover ordinary capacity.
- [x] Coalesce dirty uploads into bounded transfer/copy submissions.
- [x] Add allocation telemetry for bytes written, dirty ranges, capacity
  growth, overflow, and retired generations.
- [x] Verify zero managed allocations in the warmed extraction/upload path.

### 6. Shader Access Library

- [x] Add shared shader includes for draw, instance, mesh, material, view,
  light, shadow, texture, and deformation record access.
- [x] Centralize row-major/column-major and row-vector conventions.
- [x] Add compile-time layout assertions and CPU/GPU byte-offset tests.
- [x] Keep Vulkan and OpenGL accessors logically identical even when the
  underlying buffer reference encoding differs.
- [x] Add bounds-checked diagnostic shader mode without imposing production
  branches.

### 7. Validation

- [x] Add pack/unpack and alignment tests for every record.
- [x] Add stable-handle and stale-generation tests.
- [x] Add GPUScene add/remove/compaction remap tests.
- [x] Add material-row dirty-update and texture-reference tests.
- [x] Add a capture/debug dump that resolves a draw ID to all dependent record
  IDs without using managed renderer object identity.
- [x] Validate identical logical records on OpenGL and Vulkan.

## Acceptance Criteria

- [x] A draw ID is sufficient to locate geometry, instance, material,
  transform, deformation, and editor identity entirely from GPU tables.
- [x] Material shading requires no per-material descriptor-set selection in the
  warmed production path.
- [x] Current and previous temporal data have explicit frame-slot ownership.
- [x] Scene mutation and compaction preserve or remap stable dependent state
  without same-frame readback.
- [x] The warmed table update path allocates zero managed heap memory.
