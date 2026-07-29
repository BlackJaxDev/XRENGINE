# Advanced Render Pipeline Architectural Refactor TODO

Last Updated: 2026-07-29
Owner: Rendering
Status: Active - documents 01-03 implementation complete; visibility-buffer resources/geometry next
Migration Source: `DefaultRenderPipeline2`
Target Type: `AdvancedRenderPipeline`

Progress:

- [Identity Rename Slice - 2026-07-28](../../../progress/rendering/advanced-render-pipeline-identity-slice-2026-07-28.md)
- [Capability Selection Slice - 2026-07-28](../../../progress/rendering/advanced-render-pipeline-capability-selection-slice-2026-07-28.md)
- [Output-Purpose And Feature-Contract Slice - 2026-07-28](../../../progress/rendering/advanced-render-pipeline-output-purpose-and-feature-contract-slice-2026-07-28.md)
- [Frame-Stage Skeleton Slice - 2026-07-28](../../../progress/rendering/advanced-render-pipeline-frame-stage-skeleton-slice-2026-07-28.md)
- [Resource/State Contract Slice - 2026-07-29](../../../progress/rendering/advanced-render-pipeline-resource-state-contract-slice-2026-07-29.md)
- [GPU Scene/Material Contract Slice - 2026-07-29](../../../progress/rendering/advanced-render-pipeline-gpu-scene-material-contract-slice-2026-07-29.md)
- [GPU Visibility Preparation/Deformation Slice - 2026-07-29](../../../progress/rendering/advanced-render-pipeline-gpu-visibility-preparation-deformation-slice-2026-07-29.md)

## Direction

Refactor `DefaultRenderPipeline2` into a new `AdvancedRenderPipeline` whose
opaque renderer is visibility-buffer based. The new pipeline must not preserve
the current deferred GBuffer, deferred light accumulation, ordinary opaque
Forward+, and full-frame light-combine sequence merely for behavioral parity.

The original `DefaultRenderPipeline` remains the visual reference and temporary
runtime fallback while this work is incomplete. It is not the implementation
base for the new architecture. `DefaultRenderPipeline2` is the migration
substrate and is renamed early so new work does not continue extending a second
copy of the old pipeline.

The target frame flow is:

```text
declared frame resources and immutable scene tables
  -> animation/deformation preparation
  -> early GPU visibility preparation
  -> depth + visibility raster
  -> current-frame depth pyramid and late visibility recovery
  -> light/decal/material work classification
  -> attribute reconstruction
  -> native material and lighting kernels writing opaque HDR
  -> explicit transparent and special late passes
  -> temporal, post-processing, output, and UI
```

## Architectural Invariants

- Compatible opaque and masked surfaces write one visibility payload and are
  shaded through the visibility path. They do not choose between deferred and
  ordinary opaque-forward color paths.
- The advanced path does not allocate or populate a classic full GBuffer in
  production. Diagnostic reconstruction targets may exist only behind an
  explicit capture/debug mode.
- Transparent, refractive, volumetric, particle, editor-overlay, gizmo, and UI
  work remain explicit late or special passes. They are not evidence that the
  opaque renderer is still a deferred/forward composite.
- Geometry identity is backend-neutral across CPU-direct bring-up,
  zero-readback indirect, meshlet, skinned, and future virtual-geometry
  producers.
- Material work groups by shading-kernel compatibility and visible coverage,
  not by material object or descriptor-set identity.
- Scene, geometry, material, light, shadow, animation, and texture data are
  GPU-addressable through stable table records. Per-object or per-material
  binding loops are not part of the warmed production path.
- Production GPU-driven modes perform no same-frame count, visibility,
  material-range, or overflow readback.
- Current and previous deformed vertex data are produced once and reused by
  visibility, shadow, velocity, and other geometry consumers.
- Frame execution never creates or resizes declared pipeline resources.
- GPU-written frame data does not invalidate otherwise reusable command
  topology.
- Required accelerated modes fail visibly with a machine-readable reason when
  their contract is unavailable. They do not silently route through CPU
  emulation.
- Per-frame hot paths allocate zero managed heap memory in steady state.
- OpenGL 4.6 and Vulkan use the same logical contracts. Backend-specific
  encodings may differ, but neither backend gets an architecturally different
  scene or material model.
- Desktop scene output is owned by the standard pipeline selector and becomes
  `AdvancedRenderPipeline` after desktop promotion. OpenXR eye output is owned
  by `RvcRenderPipeline`, including when RVC additions are disabled.
- Desktop, OpenXR eye, and offscreen-capture pipelines may consume the same
  scene/mesh/material data and compatible GI, temporal, froxel, and
  post-processing feature contracts without sharing output-local pipeline
  instances or histories.

## Ordered TODO Set

Execute these documents in order. A later document may prototype against an
earlier document's stable contract, but it may not redefine that contract
without updating the earlier document and this index.

| Order | TODO | Required outcome |
| --- | --- | --- |
| 01 | [Pipeline Identity And Frame Contract](01-pipeline-identity-and-frame-contract-todo.md) | Rename and isolate the migration substrate, define capabilities, and replace the copied frame graph with an advanced-stage skeleton. |
| 02 | [GPU Scene And Material Data Contract](02-gpu-scene-and-material-data-contract-todo.md) | Establish stable GPU-addressable draw, geometry, material, view, light, and texture records. |
| 03 | [GPU Visibility Preparation And Deformation](03-gpu-visibility-preparation-and-deformation-todo.md) | Aggregate animation outputs, skinning, blendshapes, culling, and indirect preparation without per-renderer submission. |
| 04 | [Visibility Buffer Resources And Geometry](04-visibility-buffer-resources-and-geometry-todo.md) | Rasterize backend-neutral surface identity and depth from all supported geometry producers. |
| 05 | [Attribute Reconstruction](05-attribute-reconstruction-todo.md) | Recover stable surface attributes and derivatives from visibility identity. |
| 06 | [Visible Material Work Classification](06-visible-material-work-classification-todo.md) | Build bounded GPU material work proportional to visible coverage. |
| 07 | [Native Material, Lighting, Decal, And GI Shading](07-native-material-lighting-decals-and-gi-todo.md) | Shade opaque HDR directly without the classic deferred-light-combine path. |
| 08 | [Transparency, Special Passes, And Post-Processing](08-transparency-special-passes-and-post-processing-todo.md) | Reconnect legitimate late passes and the temporal/post chain around the new opaque output. |
| 09 | [Stereo, XR, Capture, And Editor Integration](09-stereo-xr-capture-and-editor-integration-todo.md) | Make RVC-owned OpenXR views, advanced offscreen consumers, selection, diagnostics, and tooling first-class on shared scene/feature contracts. |
| 10 | [Validation, Performance, Cutover, And Retirement](10-validation-performance-cutover-and-retirement-todo.md) | Prove correctness and performance, make the advanced pipeline the default, and remove obsolete duplicated architecture. |

## Capability Policy

The first production capability floor must be written in document 01. At a
minimum it must cover:

- integer visibility attachments;
- storage-buffer geometry and scene-table access;
- compute dispatch and indirect dispatch/draw support;
- a production texture-indirection rung;
- explicit image/buffer synchronization;
- current and previous frame-slot storage;
- stereo-array resources when stereo is requested.

Optional acceleration such as buffer device address, descriptor heap,
subgroups, mesh shaders, and async compute may improve an implementation but
must not change the logical payload or material contracts.

## Rollout Policy

- Rename `DefaultRenderPipeline2` before adding new renderer behavior.
- Keep `DefaultRenderPipeline` as the selectable reference until document 10.
- Do not maintain feature parity by copying new work into both pipelines.
- Do not add a `DefaultRenderPipeline2` type alias or compatibility facade.
- Do not make `AdvancedRenderPipeline` the default until the document 10 gates
  pass.
- Do not describe a debug reconstruction path as a production fallback.
- Unsupported opaque materials in an explicitly required advanced mode render
  an observable error material or fail pipeline selection; they do not silently
  enter the old opaque renderer.

## Cross-Cutting Evidence

Each implementation document must leave:

- focused deterministic tests for its data and ordering contracts;
- named GPU annotations and stable capture resource names;
- delayed telemetry for work counts, overflow, and fallback reasons;
- OpenGL and Vulkan validation where the feature is intended to be supported;
- before/after timing with the exact scene, resolution, settings, build,
  hardware, and capture overhead recorded;
- durable implementation findings under `docs/work/progress/rendering/`;
- disposable captures and logs only under
  `Build/_AgentValidation/<run>/`.

## Canonical Related Work

- [Default Render Pipeline Notes](../../../../architecture/rendering/default-render-pipeline-notes.md)
- [Render Pipeline Resource Lifecycle](../../../../architecture/rendering/render-pipeline-resource-lifecycle.md)
- [Mesh Submission Strategies](../../../../architecture/rendering/mesh-submission-strategies.md)
- [Compact Zero-Readback Submission](../../../../architecture/rendering/vulkan-compact-zero-readback-submission.md)
- [GPU Scene BVH](../../../../architecture/rendering/gpu-scene-bvh.md)
- [Material Table And Texture Binding Ladder](../optimization/material-table-and-texture-binding-ladder-todo.md)
- [GPU-Driven Occlusion Culling](../gpu/gpu-driven-occlusion-culling-architecture-todo.md)
- [Skinning GPU Efficiency Follow-Ups](../gpu/skinning-gpu-efficiency-followups-todo.md)
- [GPU-Driven Animation](../gpu/gpu-driven-animation-todo.md)

## Program Completion

- [ ] Documents 01 through 10 satisfy every acceptance criterion.
- [ ] `AdvancedRenderPipeline` is the production default for desktop and
  validated offscreen profiles; `RvcRenderPipeline` owns validated OpenXR eye
  profiles.
- [ ] Compatible opaque and masked rendering contains no classic deferred
  GBuffer, deferred light accumulation, ordinary opaque Forward+, or light
  combine.
- [ ] Unsupported advanced content is visible and diagnosable.
- [ ] The warmed production path has no same-frame GPU readback and no managed
  per-frame allocations.
- [ ] Obsolete V2 names, selectors, tests, docs, shaders, and resources are
  removed or explicitly retained as historical documentation.
