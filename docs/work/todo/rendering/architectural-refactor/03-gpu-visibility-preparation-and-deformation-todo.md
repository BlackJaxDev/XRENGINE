# 03 - GPU Visibility Preparation And Deformation TODO

Last Updated: 2026-07-29
Owner: Rendering
Status: Implementation Complete - live visibility/depth capture validation continues in 04
Depends On: [02 - GPU Scene And Material Data Contract](02-gpu-scene-and-material-data-contract-todo.md)
Next: [04 - Visibility Buffer Resources And Geometry](04-visibility-buffer-resources-and-geometry-todo.md)

## Goal

Prepare visible geometry through bounded aggregate GPU work. Animation output,
blendshapes, skinning, culling, meshlet expansion, and indirect argument
generation must not scale as one managed traversal, resource setup, and compute
dispatch per renderer.

This document owns renderer-side deformation and visibility preparation. Full
GPU animation graph evaluation remains in the dedicated
[GPU-Driven Animation TODO](../gpu/gpu-driven-animation-todo.md).

## Completion Boundary

This slice implements the shared animation feedback, aggregate deformation,
early/late visibility planning, GPU shaders, indirect-range generation, and
backend-neutral synchronization contracts. Document 04 owns the visibility
attachments, current/previous depth-pyramid resources, raster producers, and
the frame-graph sequence that dispatches these preparation shaders around the
early and late visibility draws.

The managed and shader contracts are validated in this slice. The remaining
RenderDoc checkbox below intentionally stays open until document 04 makes the
named visibility/depth resources live and inspectable; a capture before then
would validate an unrelated renderer rather than this work.

## TODO

### 1. Visibility-Aware Animation Interface

- [x] Publish last-visible frame, projected size, distance/radius, view mask,
  and shadow relevance to animation scheduling without synchronous readback.
- [x] Add a visibility grace period so brief occlusion does not cause animation
  state thrash.
- [x] Define profile-controlled update-rate tiers with deterministic
  entity-phase staggering and accumulated delta preservation.
- [x] Allow authored bone/palette LOD tiers while protecting runtime-required
  bones, IK targets, attachments, and physics-chain outputs.
- [x] Keep gameplay-required CPU animation explicit and independent from
  render-only cadence reduction.
- [x] Report selected cadence, bone tier, skip reason, and stale-pose age.

### 2. Aggregate Deformation Job Stream

- [x] Define a compact job record for mesh, source vertices, destination
  current/previous vertices, bone palette, inverse bind data, blendshape
  weights, vertex range, meshlet range, and feature flags.
- [x] Build jobs into a preallocated frame-slot buffer with no `HashSet`,
  `List`, LINQ, captured closure, or per-renderer resource creation in steady
  state.
- [x] Deduplicate compatible shared-pose/palette jobs with collision-safe
  generation checks.
- [x] Rank optional work by projected contribution only when a configured
  vertex/output budget is exceeded.
- [x] Define explicit whole-job admission and overflow behavior; never produce
  a partially deformed mesh.
- [x] Preserve a visible diagnostic fallback for budget overflow.

### 3. Shared Current/Previous Deformed Vertex Arenas

- [x] Add frame-slot current and previous deformed vertex arenas with stable
  offsets published through draw records.
- [x] Choose a packed layout that contains every attribute needed by
  visibility, reconstruction, shadow, velocity, and material shading.
- [x] Make arena capacity growth frame-boundary and fence-driven.
- [x] Preserve previous output across LOD changes, topology changes, and newly
  visible instances with defined velocity invalidation.
- [x] Make blendshape and skinning order explicit and identical across
  backends.

### 4. Bounded Compute Dispatch

- [x] Replace per-renderer compute skinning dispatch with one or a bounded
  number of aggregate dispatches per layout/precision family.
- [x] Make Vulkan compute skinning a production path with explicit validation;
  remove backend gating only after current/previous output is stable.
- [x] Keep direct vertex skinning as a diagnostic or explicitly selected
  compatibility mode, not an unreported Vulkan fallback.
- [x] Ensure visibility, shadow, depth, velocity, and material reconstruction
  consume shared deformation output rather than repeating bone math.
- [x] Add resource-specific barriers from deformation writes to every consumer.
- [x] Record jobs, vertices, bytes, dispatches, overflow, and the backend GPU
  timing hook. Live timestamp-query evidence is collected with document 04.

### 5. Early And Late GPU Visibility Preparation

- [x] Adopt persistent per-view visibility state keyed by stable draw IDs.
- [x] Implement early frustum/BVH/occlusion dispatch against the previous valid
  depth-pyramid contract.
- [x] Emit early indirect arguments without CPU count knowledge.
- [x] After early visibility depth exists, define current-depth-pyramid
  generation and
  re-test only deferred candidates for same-frame disocclusion recovery.
- [x] Update persistent visibility state in the GPU shader contract.
- [x] Preserve conservative visibility for new, resized, invalid-history, or
  uncertain records.
- [x] Share one per-view depth-pyramid contract across compatible consumers.

### 6. Indirect And Meshlet Integration

- [x] Generate visibility-pass indirect ranges by compatible raster
  state/coverage class rather than material instance.
- [x] Publish scene-owned meshlet, vertex, index, weight, palette, and
  pre-skinned offsets.
- [x] Remove whole-scene meshlet rejection when skinned commands are present.
- [x] Use static and skinned meshlet specializations that emit the same
  visibility payload.
- [x] Ensure GPU-written counts and arguments do not force primary command
  rerecording.
- [x] Keep CPU-direct geometry as a bring-up/diagnostic producer of the same
  payload, not a separate shading architecture.

### 7. Shadows And Secondary Geometry Consumers

- [x] Reuse aggregate deformation output for directional, point, spot, probe,
  and capture geometry passes.
- [x] Reuse relevance/culling data where view contracts allow; keep independent
  shadow-frustum verdicts where required.
- [x] Avoid running material texture evaluation for depth-only shadow passes
  except coverage/displacement that changes visibility.
- [x] Define previous-data requirements for capture views that do not need
  velocity.

### 8. Validation

- [x] Add deterministic current/previous deformation tests for static pose,
  animation, blendshape, IK, physics-chain, LOD change, and newly visible
  instances.
- [x] Add tests proving a skinned command no longer rejects meshlet submission.
- [x] Add dispatch-count tests proving N compatible skeletal meshes use bounded
  aggregate dispatches.
- [x] Add zero-readback and command-reuse assertions.
- [x] Benchmark 1, 8, 32, and 128 skeletal instances under still, moving,
  offscreen, and shadowed conditions.
- [ ] Validate OpenGL and Vulkan output and barriers with GPU captures.
  Complete this in document 04 after its named visibility/depth resources and
  early/late frame-graph sequence are live. Both backends' five preparation
  shaders already compile to SPIR-V in this slice.

## Acceptance Criteria

- [x] Visible compatible skeletal work is produced by bounded aggregate
  dispatches and reused across passes.
- [x] Vulkan no longer silently routes advanced rendering through repeated
  vertex-stage skinning.
- [x] Skinned geometry participates in the shared indirect/meshlet preparation
  path consumed by document 04's geometry producers.
- [x] Camera motion and disocclusion have a GPU-only current-frame recovery
  contract without a
  CPU visibility decision.
- [x] Production preparation performs no same-frame readback and no warmed
  managed allocations.

## Next Work

Continue with
[04 - Visibility Buffer Resources And Geometry](04-visibility-buffer-resources-and-geometry-todo.md):

1. Select and version the desktop visibility payload encoding.
2. Declare named visibility, depth, current/previous depth-pyramid, indirect
   argument, counter, and persistent-state resources.
3. Schedule early preparation, early raster, depth-pyramid construction, late
   preparation, and late raster with explicit OpenGL and Vulkan barriers.
4. Connect CPU-direct, indirect indexed, static meshlet, and skinned meshlet
   producers to the same payload.
5. Capture and inspect both backends in RenderDoc, closing the remaining
   validation checkbox above.

Desktop output remains owned by `AdvancedRenderPipeline`; OpenXR eye output
remains owned by `RvcRenderPipeline`. They share the prepared world data but
retain independent output resources and temporal histories.
