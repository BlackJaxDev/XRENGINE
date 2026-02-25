# XRMesh Modeling and Editing Architecture

[<- Docs index](../README.md)

This document defines an architecture for advanced mesh authoring in `XREngine.Modeling` with a full round-trip:

1. Start from an existing `XRMesh`.
2. Edit topology and attributes in an authoring model.
3. Save back to `XRMesh` without losing supported data channels.

## Problem Statement

Current mesh editing exists but is narrow:

- `XREngine.Modeling.EditableMesh` supports triangle topology and position edits.
- Editor tooling (`MeshEditingPawnComponent`) can select primitives and run simple operations.
- Conversion back to renderable mesh is currently ad-hoc and attribute-light in test tooling.

`XRMesh` already supports richer runtime data:

- interleaved or separate vertex buffers,
- triangle/line/point indices,
- colors and UV sets,
- skinning and blendshapes,
- cooked-binary serialization.

The missing piece is a formal authoring architecture that preserves this richer data while enabling advanced operations.

## Design Goals

- Establish a stable authoring core in `XREngine.Modeling` for advanced topology edits.
- Support deterministic `XRMesh -> Editable -> XRMesh` round-trip.
- Preserve non-topology data where possible (normals, tangents, UVs, colors, skinning, blendshapes).
- Integrate with editor undo/redo and selection workflows.
- Keep runtime rendering and asset serialization paths unchanged where possible.

## Non-Goals

- Replacing `XRMesh` as the runtime render format.
- Rewriting renderer mesh upload logic.
- Shipping every advanced operation in phase 1.

## Architectural Constraints

- `XREngine` references `XREngine.Modeling`.
- `XREngine.Modeling` currently does not reference `XREngine` and should remain engine-agnostic.
- Therefore, `XRMesh` conversion logic must live in `XREngine` (adapter/bridge layer), not inside `XREngine.Modeling`.

## Proposed Layers

| Layer | Project | Responsibility |
|---|---|---|
| Authoring Domain | `XREngine.Modeling` | Topology kernel, attribute layers, editing operations, validation, acceleration caches |
| XRMesh Bridge | `XREngine` | Convert `XRMesh` to/from modeling DTOs; preserve channel semantics and buffer layout intent |
| Editor Session | `XREngine.Editor` | Selection, gizmos, tool commands, undo scopes, save/apply workflows |

## Authoring Domain Model

### Core Types (new/expanded in `XREngine.Modeling`)

- `ModelingMeshDocument`
- `ModelingTopology`
- `ModelingAttributeSet`
- `ModelingSelectionState`
- `ModelingOperationContext`
- `ModelingValidationReport`

### Topology

Evolve from current triangle-only record storage to explicit topological entities:

- vertex table
- directed edge/half-edge table
- face table (triangle-first, n-gon-ready)
- adjacency caches (vertex->edge, edge->face, face->face)

Current `EditableMesh` and `EdgeKey` remain valid as phase-1 scaffolding but should migrate toward a single canonical topology representation.

### Attributes

Store per-element channels independently from topology:

- per-vertex position (required)
- per-vertex normal/tangent (optional)
- N UV channels
- N color channels
- skin weights / indices
- blendshape deltas

This prevents topology rewrites from silently dropping non-position data.

## XRMesh Bridge Architecture

Add a bridge in `XREngine` (example namespace: `XREngine.Rendering.Modeling`):

- `XRMeshModelingImporter`
- `XRMeshModelingExporter`
- `XRMeshModelingOptions`

`XREngine.Modeling` receives/returns neutral DTOs, not `XRMesh` directly.

### Import Path (`XRMesh -> Modeling`)

1. Read geometry channels through `XRMesh` accessors and buffers (interleaved or separate).
2. Normalize into `ModelingMeshDocument` channel sets.
3. Import primitive indices (`Triangles`, `Lines`, `Points`) as applicable.
4. Capture metadata required for round-trip: source primitive type, channel counts, buffer layout preference, and skinning/blendshape presence.
5. Build topology caches and validation report.

### Export Path (`Modeling -> XRMesh`)

1. Validate topology and channel cardinality.
2. Build vertex/index streams according to export policy (keep indexed topology, optional remap/optimize pass).
3. Rebuild `XRMesh` buffers with explicit channel mapping.
4. Reapply skinning and blendshape streams when present.
5. Recompute bounds and invalidate transient caches.
6. Emit mesh change notification for renderers.

## Authoring Session and Undo

Each editor operation should run through a command pipeline:

- `IModelingOperation.Execute(document, context)`
- result contains changed element ranges + derived-cache invalidation flags

Operations are wrapped in editor undo scopes so one user action maps to one undo step, even if many vertices/faces change.

## Advanced Operation Set (Target)

Phase order for capabilities:

1. Topology-safe transforms, split/collapse, connect, detach.
2. Extrude, inset, bevel, bridge, loop-cut.
3. Surface operators (subdivide, relax, retopo helpers).
4. Boolean and remesh interoperability.

Each operation must declare:

- required topology preconditions,
- attribute interpolation rules,
- post-operation validation rules.

## Save Semantics and Invariants

### Required invariants before export

- no out-of-range indices
- no dangling directed edges
- consistent face winding per face
- channel length equals vertex count (for per-vertex channels)

### Renderer/runtime refresh contract

After export to `XRMesh`, the save path must:

1. update indices (`Triangles`, `Lines`, `Points`) and `VertexCount`,
2. rebuild or repopulate vertex buffers,
3. call `InvalidateIndexBufferCache`,
4. refresh bounds,
5. trigger mesh-changed notification used by GL/Vulkan mesh renderers.

## Persistence

Keep `XRMesh` as the persisted runtime asset.

- Authoring session data can remain transient editor state.
- Persisted output continues through existing `XRMesh` serialization (`CookedBinary`/asset pipeline).
- Optional future extension: sidecar modeling metadata for non-runtime authoring history.

## Migration Plan

### Phase 0: Baseline bridges

- Add importer/exporter that supports current `EditableMesh` capabilities.
- Guarantee round-trip for position + triangle indices.

### Phase 1: Channel-complete round-trip

- Add UV/color/normal/tangent preservation.
- Add validation and deterministic export ordering.

### Phase 2: Skinning and blendshape-aware edits

- Preserve or reproject weights/deltas when topology changes permit.
- Define explicit fallback policy when exact preservation is impossible.

### Phase 3: Advanced topology operators

- Introduce half-edge-backed operations and robust manifold checks.
- Integrate with full editor tools and production undo flows.

## Phase 0 Implementation Checklist (Concrete)

Use this checklist to implement the baseline bridge in a single pass.

### 1. Add Modeling DTOs in `XREngine.Modeling`

- [x] Create `XREngine.Modeling/ModelingMeshDocument.cs`.
- [x] Define `ModelingMeshDocument` with:
  - `List<Vector3> Positions`
  - `List<int> TriangleIndices`
  - optional `List<Vector3>? Normals`
  - optional `List<Vector3>? Tangents`
  - optional `List<List<Vector2>> TexCoordChannels`
  - optional `List<List<Vector4>> ColorChannels`
  - `ModelingMeshMetadata Metadata`
- [x] Create `XREngine.Modeling/ModelingMeshMetadata.cs`.
- [x] Define `ModelingMeshMetadata` with:
  - source primitive type
  - source interleaved flag
  - source color/texcoord channel counts
  - skinning/blendshape present flags
- [x] Create `XREngine.Modeling/ModelingMeshValidation.cs`.
- [x] Add `ModelingMeshValidation.Validate(ModelingMeshDocument document)` returning a report with:
  - out-of-range index detection
  - channel cardinality checks
  - empty/degenerate triangle checks

### 2. Add Editable Converters in `XREngine.Modeling`

- [x] Create `XREngine.Modeling/EditableMeshConverter.cs`.
- [x] Add `EditableMeshConverter.ToEditable(ModelingMeshDocument document)`.
- [x] Add `EditableMeshConverter.FromEditable(EditableMesh mesh, ModelingMeshMetadata metadata)`.
- [x] Ensure conversion keeps triangle winding order and position indices stable.

### 3. Add XRMesh Bridge in `XREngine`

- [x] Create folder `XREngine/Rendering/Modeling/`.
- [x] Create `XREngine/Rendering/Modeling/XRMeshModelingImportOptions.cs`.
- [x] Create `XREngine/Rendering/Modeling/XRMeshModelingExportOptions.cs`.
- [x] Create `XREngine/Rendering/Modeling/XRMeshModelingImporter.cs`.
- [x] Create `XREngine/Rendering/Modeling/XRMeshModelingExporter.cs`.
- [x] Implement importer API:
  - `public static ModelingMeshDocument Import(XRMesh mesh, XRMeshModelingImportOptions? options = null)`
- [x] Implement exporter API:
  - `public static XRMesh Export(ModelingMeshDocument document, XRMeshModelingExportOptions? options = null)`
- [x] In importer, read positions through `GetPosition`, indices through `GetIndices(EPrimitiveType.Triangles)`, and hydrate metadata.
- [x] In exporter, create `XRMesh` from position/triangle streams and enforce `VertexCount`/index consistency.

### 4. Add XRMesh Update Contract in Exporter

- [x] Export path must perform all of the following before returning:
  - rebuild bounds from positions
  - repopulate triangle index list
  - invalidate index buffer cache
  - clear or rebuild stale acceleration structures as needed
  - trigger renderer-facing mesh-changed signal/event
- [x] Add XML comments in exporter describing this contract for future operators.

### 5. Integrate with Editor Mesh Editing Flow

- [x] Update `XREngine.Editor/MeshEditingPawnComponent.cs` with helper entrypoints:
  - `LoadFromXRMesh(XRMesh mesh)`
  - `SaveToXRMesh()` (or `BuildXRMesh()`)
- [x] Route current `EditableMesh` assignment through importer + `EditableMeshConverter`.
- [x] Route bake/save through `EditableMeshConverter` + exporter.
- [x] Wrap editor-triggered save/apply actions in undo user interaction scopes.

### 6. Add Baseline Tests

- [x] Create `XREngine.UnitTests/Rendering/XRMeshModelingBridgeTests.cs`.
- [x] Add test: `XRMesh -> Modeling -> XRMesh` preserves vertex positions and triangle indices for a simple triangle list mesh.
- [x] Add test: exporter rejects invalid indices with a validation failure.
- [x] Add test: importer handles meshes with no normals/uv/colors.
- [x] Add test: save contract invalidates index cache and updates bounds.

### 7. Phase 0 Acceptance Criteria

- [x] A mesh loaded from `XRMesh` can be edited with current `EditableMesh` operations and saved back to valid `XRMesh`.
- [x] Round-trip preserves triangle topology and positions exactly for deterministic test meshes.
- [x] No project reference cycle is introduced (`XREngine.Modeling` remains independent of `XREngine`).
- [x] New bridge unit tests pass in `XREngine.UnitTests`.

## Phase 1 Implementation Checklist (Concrete)

Use this checklist to complete channel-preserving round-trip behavior.

### 1. Channel-complete bridge behavior

- [x] Set `XRMeshModelingImportOptions` defaults to import normals/tangents/texcoords/colors.
- [x] Ensure importer populates `Normals`, `Tangents`, `TexCoordChannels`, and `ColorChannels` when present in source `XRMesh`.
- [x] Ensure exporter writes imported normals/tangents/texcoords/colors back to `XRMesh` vertex streams.
- [x] Keep channel cardinality validation enforced before export via `ModelingMeshValidation`.

### 2. Editor save flow channel retention

- [x] Persist the imported `ModelingMeshDocument` on load so save can compare against source channels.
- [x] Preserve compatible normals/tangents/texcoord/color channels during `SaveToXRMesh` when vertex cardinality still matches.
- [x] Keep mesh save/apply actions wrapped in undo interaction/change scopes.

### 3. Phase 1 tests

- [x] Add bridge round-trip test for normals, tangents, UVs, and colors.
- [x] Keep no-channel import and validation-failure tests passing with channel-enabled import defaults.
- [x] Add integration test covering `MeshEditingPawnComponent.LoadFromXRMesh -> SaveToXRMesh` channel retention after non-topology edits.

### 4. Deterministic ordering policy

- [x] Add explicit deterministic export ordering policy in `XRMeshModelingExportOptions`.
- [x] Implement deterministic remap/ordering strategy with documented tie-break rules.
- [x] Add tests proving repeated exports are deterministic under each policy mode.

### 5. Phase 1 Acceptance Criteria

- [x] Representative round-trip tests preserve positions/indices plus normals/tangents/UV/color channels.
- [x] Editor bridge save path no longer drops compatible attribute channels by default.
- [x] Export ordering behavior is explicit and verified by deterministic tests.

## Phase 2 Implementation Checklist (Concrete)

Use this checklist to add skinning/blendshape-aware editing and save behavior.

### 1. Skinning and blendshape document model

- [x] Capture source skinning/blendshape presence flags in `ModelingMeshMetadata` during import.
- [x] Add DTO channels for per-vertex skin indices/weights in `ModelingMeshDocument`.
- [x] Add DTO channels for blendshape deltas (position/normal/tangent where available).
- [x] Extend validation to enforce skinning/blendshape channel cardinality and consistency rules.

### 2. Skinning/blendshape bridge import-export

- [x] Import skin indices/weights from `XRMesh` into modeling DTOs.
- [x] Export preserved or reprojected skin indices/weights back to `XRMesh`.
- [x] Import blendshape target/frame data into modeling DTOs.
- [x] Export blendshape target/frame data back to `XRMesh`.

### 3. Topology-change preservation policies

- [x] Define explicit interpolation/reprojection rules for skinning and blendshape deltas across split/collapse/extrude/connect operations.
- [x] Define fallback behavior when exact preservation is impossible (drop channel, clamp, or fail export).
- [x] Emit structured validation warnings/errors when fallback paths are used.

### 4. Editor workflow and UX

- [x] Surface skinning/blendshape preservation and fallback outcomes in editor-visible diagnostics.
- [x] Add export/save options for strict vs permissive fallback policy.
- [x] Ensure undo/redo captures skinning/blendshape-affecting edits and save outcomes.

### 5. Phase 2 tests and acceptance

- [x] Add unit tests for skinning/blendshape import-export preservation without topology changes.
- [x] Add unit tests for defined reprojection/fallback behavior under topology-changing edits.
- [x] Add integration round-trip tests for representative skinned meshes and blendshape meshes.
- [x] Confirm save contract remains valid (bounds/cache/notifications) with skinning/blendshape-enabled exports.

## Phase 3 Execution Todo List

- [x] Design half-edge topology core
- [x] Migrate `EditableMesh` to canonical topology
- [x] Add manifold and integrity checks
- [x] Implement extrude/inset/bevel/bridge
- [x] Implement loop-cut and split/collapse
- [x] Define per-operation attribute interpolation
- [x] Integrate operations into editor tools
- [x] Bind undo scopes per operation
- [x] Add topology operator unit tests
- [x] Add editor integration regression tests
- [x] Run rendering and bridge regressions
- [x] Update architecture docs for phase 3

## Test Strategy

- Unit tests (`XREngine.Modeling`): topology invariants, operation correctness, and channel interpolation correctness.
- Integration tests (`XREngine.Editor`/`XREngine`): `XRMesh -> Modeling -> XRMesh` round-trip equivalence, renderer refresh after save, and undo/redo stability.
- Performance tests: large mesh import/export timings plus operation cost and allocation tracking.

## Risks and Mitigations

- Risk: channel loss during topology edits.
- Mitigation: explicit attribute interpolation policies per operation.
- Risk: dependency cycles between projects.
- Mitigation: keep `XRMesh` bridge in `XREngine`, modeling core DTO-only.
- Risk: renderer stale data after in-place save.
- Mitigation: enforce post-save refresh contract in exporter.

## Recommended Initial Deliverables

1. `XRMeshModelingImporter` and `XRMeshModelingExporter` in `XREngine`.
2. `ModelingMeshDocument` and validation primitives in `XREngine.Modeling`.
3. Editor save/apply workflow using the bridge from `MeshEditingPawnComponent`.
4. Round-trip integration test for a representative mesh with UV/color channels.

