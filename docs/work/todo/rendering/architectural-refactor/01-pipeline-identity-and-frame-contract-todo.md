# 01 - Pipeline Identity And Frame Contract TODO

Last Updated: 2026-07-29
Owner: Rendering
Status: Complete
Depends On: [Architectural Refactor Index](00-advanced-render-pipeline-refactor-todo.md)
Next: [02 - GPU Scene And Material Data Contract](02-gpu-scene-and-material-data-contract-todo.md)

Implementation Evidence:

- [Identity Rename Slice - 2026-07-28](../../../progress/rendering/advanced-render-pipeline-identity-slice-2026-07-28.md)
- [Capability Selection Slice - 2026-07-28](../../../progress/rendering/advanced-render-pipeline-capability-selection-slice-2026-07-28.md)
- [Output-Purpose And Feature-Contract Slice - 2026-07-28](../../../progress/rendering/advanced-render-pipeline-output-purpose-and-feature-contract-slice-2026-07-28.md)
- [Frame-Stage Skeleton Slice - 2026-07-28](../../../progress/rendering/advanced-render-pipeline-frame-stage-skeleton-slice-2026-07-28.md)
- [Resource/State Contract Slice - 2026-07-29](../../../progress/rendering/advanced-render-pipeline-resource-state-contract-slice-2026-07-29.md)
- [Default Reference Baseline Investigation - 2026-07-29](../../../investigations/rendering/default-reference-baseline-capture-2026-07-29.md)

## Goal

Rename `DefaultRenderPipeline2` to `AdvancedRenderPipeline`, remove its identity
as a second copy of the old default renderer, and establish the stable
capability and frame-stage contracts that every later phase will implement.

## Starting Surface

The migration covers more than the partial class files. Before the identity
slice, source and tests contained direct `DefaultRenderPipeline2` type checks
in pipeline creation, XR pipeline cloning, light/GI commands, editor
inspectors, upscale routing, resource-lifecycle tests, source-contract tests,
and diagnostic capture names.

## Target Source Shape

Use focused partial files under
`XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/`:

```text
AdvancedRenderPipeline.cs
AdvancedRenderPipeline.Resources.cs
AdvancedRenderPipeline.CommandChain.cs
AdvancedRenderPipeline.Visibility.cs
AdvancedRenderPipeline.Shading.cs
AdvancedRenderPipeline.Transparency.cs
AdvancedRenderPipeline.PostProcessing.cs
AdvancedRenderPipeline.Diagnostics.cs
```

Keep separately reusable capability, payload, settings, enum, record, and
command types in their own files. Do not nest unrelated enums or records inside
the pipeline merely to shorten the file list.

## TODO

### 1. Baseline And Inventory

- [x] Record the starting commit and unrelated dirty-worktree exclusions.
- [x] Inventory every `DefaultRenderPipeline2` source, test, documentation,
  serialization, editor, XR, capture, and environment-variable reference.
- [x] Record which V2 settings and features are advanced-pipeline requirements,
  explicit late-pass requirements, diagnostics only, or obsolete deferred/
  forward implementation details.
- [x] Capture reference images and timings from `DefaultRenderPipeline` for the
  desktop static, moving, skeletal, material-diverse, transparency, stereo, and
  post-processing scenes used in document 10.
  The document-01 composite seed covers each category; document 10 still owns
  the split deterministic named cohorts, matched advanced captures, production
  GPU timing, and OpenXR runtime evidence.
- [x] Record all current hard-coded file-path tests so the rename replaces
  brittle source-string assertions with behavior or contract tests where
  practical.

### 2. Rename The Migration Substrate

- [x] Move the `Default2` source folder to `Advanced`.
- [x] Rename all `DefaultRenderPipeline2` partial declarations, constructors,
  capture labels, file names, and tests to `AdvancedRenderPipeline`.
- [x] Remove the `DefaultRenderPipeline2` type completely; do not leave a
  subclass, alias, forwarding type, or obsolete compatibility facade.
- [x] Replace `XRE_USE_PIPELINE_V2`-style binary selection with an explicit
  pipeline-kind setting such as `LegacyDefault` and `Advanced`.
- [x] Update `NewRenderPipeline`, editor pipeline selection, two-pass VR,
  single-pass stereo, OpenXR pipeline creation, foveated views, light probes,
  mirrors, impostor capture, and unit-testing-world creation.
- [x] Update editor inspector type checks to depend on focused capability
  interfaces where the feature is not inherently pipeline-specific.
- [x] Update GI/light commands that switch on concrete default-pipeline types
  to consume explicit provider interfaces.
- [x] Regenerate settings/schema assets if a serialized pipeline selector
  changes. Treat any broader storage migration as an owner-approval gate.
  The generator produced no tracked schema delta because this selector belongs
  to runtime rendering settings rather than unit-testing-world startup data.

### 3. Define Capabilities And Required Failure

- [x] Add an `AdvancedRenderPipelineCapabilities` type that reports required
  integer targets, compute, storage-buffer, indirect, texture-indirection,
  synchronization, current/previous frame-slot storage, stereo, and optional
  acceleration features.
- [x] Add a structured capability result with selected backend encodings and
  one machine-readable rejection reason.
- [x] Define `Disabled`, `Available`, `Required`, and diagnostic-selection
  behavior without silently changing renderer architecture.
- [x] Make explicitly required selection fail before frame execution if a
  required capability or shader family is unavailable.
- [x] Surface selected capability encodings in the editor and profiler.

### 4. Replace The Copied Frame Graph

- [x] Remove advanced-pipeline calls to `AppendDeferredGBufferPass`,
  `AppendForwardDepthPrePass`, `AppendLightingPass`, ordinary opaque
  `AppendForwardPass`, and deferred light-combine.
- [x] Remove advanced-pipeline resource declarations whose only purpose is the
  old GBuffer/lighting/composite graph.
- [x] Establish named command-chain stages for frame begin, deformation,
  visibility preparation, visibility raster, depth pyramid/late recovery,
  work classification, native opaque shading, late passes, temporal/post,
  output, and UI.
- [x] Each stage must be a stable command or focused command group; do not
  rebuild a monolithic pipeline method.
- [x] Keep incomplete stages unavailable behind the capability selector rather
  than executing a half-old, half-new renderer.
- [x] Add GPU annotations for every stage before shader implementation.

### 5. Resource And State Contracts

- [x] Define which resources are pipeline-owned persistent, frame-slot
  transient, temporal history, imported, or externally owned.
- [x] Add every layout-affecting setting to the immutable resource profile and
  generation key.
- [x] Define current/previous frame-slot ownership and fence/timeline reuse.
- [x] Define synchronization boundaries between compute preparation, graphics
  visibility writes, compute classification/shading, late graphics, and
  presentation for OpenGL and Vulkan.
- [x] Specify the exact topology, capacity, binding, shader, and resource
  generations that may invalidate recorded command packets.
- [x] Assert that ordinary GPU-written counts, visibility, transforms, and
  material data do not invalidate command topology.

### 6. Focused Validation

- [x] Build the editor after the identity rename.
- [x] Build the editor again after the old frame graph is disconnected.
- [x] Add tests proving no live source or runtime type named
  `DefaultRenderPipeline2` remains.
- [x] Add pipeline-selection tests for supported, unsupported, required, mono,
  stereo, capture, and XR requests.
  Purpose-routing tests cover desktop, capture, per-eye OpenXR, layered
  OpenXR, RVC-off eye ownership, and Advanced-to-RVC feature synchronization.
- [x] Add command-tree tests proving the advanced chain contains no deferred
  GBuffer, ordinary opaque-forward, or light-combine stage.
- [x] Add resource-layout tests proving inactive advanced stages cannot request
  undeclared resources.

## Acceptance Criteria

- [x] `AdvancedRenderPipeline` is selectable but not yet the production default.
- [x] The original `DefaultRenderPipeline` still supplies the temporary
  reference/fallback renderer.
- [x] There is no `DefaultRenderPipeline2` runtime type or selector.
- [x] The advanced command chain exposes only the target architecture's named
  stages.
- [x] Unsupported required selection fails visibly before rendering.
- [x] OpenGL and Vulkan agree on the logical frame and resource contract.
