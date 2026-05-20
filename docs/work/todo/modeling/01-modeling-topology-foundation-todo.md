# Modeling Topology Foundation TODO

Last Updated: 2026-05-19
Owner: Modeling
Status: Planned child tracker for [GPU-Accelerated Modeling Roadmap](00-gpu-accelerated-modeling-roadmap.md) Phase 1
Target Branch: `modeling-gpu-accelerated-tools`

Source design:

- [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md)

Related docs:

- [GPU-Accelerated Modeling Roadmap TODO](00-gpu-accelerated-modeling-roadmap.md)
- [Geometry Nodes Foundation TODO](02-geometry-nodes-foundation-todo.md)
- [Core Modeling Tools TODO](03-core-modeling-tools-todo.md)

## Parent Roadmap Contract

This tracker owns the CPU-authoritative editable topology foundation. The parent roadmap owns branch lifecycle and cross-feature ordering. Geometry nodes, GPU preview, and editor integration depend on this document for stable IDs, attributes, validation, and bake/apply semantics.

## Goal

Evolve the current triangle-list-oriented `EditableMesh` and `HalfEdgeTopology` scaffolding into a production authoring topology suitable for direct editing, geometry node bake/apply, undo, validation, and export.

## Non-Negotiable Rules

- [ ] CPU topology is authoritative for committed mesh edits.
- [ ] Stable element IDs are separate from dense storage indices.
- [ ] Loops/corners are first-class because UVs, normals, tangents, colors, and other face-varying data live there.
- [ ] Loose vertices, loose edges, boundaries, and non-manifold topology are representable and validated, not silently deleted.
- [ ] N-gons are supported at the authoring layer, even when render/export paths triangulate.
- [ ] Dirty tracking distinguishes topology, positions, attributes, and derived caches.
- [ ] Unit tests do not require a renderer.
- [ ] Hot edit/update paths avoid unnecessary heap allocations after setup.

## Success Criteria

- [ ] `ModelingMeshDocument` owns vertices, edges, loops/corners, faces, attributes, selection, dirty regions, undo journal, and derived cache handles.
- [ ] Stable IDs survive storage compaction and undo/redo.
- [ ] Vertex/edge/face/loop adjacency can be traversed deterministically.
- [ ] Attribute layers support point, edge, corner, face, object, and instance domains where applicable.
- [ ] Topology validation reports degenerate faces, invalid links, non-manifold edges, and expected boundary/loose element warnings.
- [ ] Bake/export can produce renderable triangle buffers and preserve required attributes.
- [ ] Existing `EditableMeshTopologyOperatorTests` are preserved or migrated.

## Primary Code Areas

- `XREngine.Modeling/EditableMesh.cs`
- `XREngine.Modeling/HalfEdgeTopology.cs`
- `XREngine.Modeling/ModelingMeshDocument.cs`
- `XREngine.Modeling/ModelingMeshMetadata.cs`
- `XREngine.Modeling/ModelingMeshValidation.cs`
- `XREngine.Modeling/ModelingOperationOptions.cs`
- `XREngine.Runtime.ModelingBridge/`
- `XREngine.UnitTests/Modeling/`

## Phase 0: Baseline And Migration Plan

**Goal:** preserve current behavior while defining the migration path to stable-ID topology.

### Tasks

- [ ] Audit current `EditableMesh`, `HalfEdgeTopology`, `EditableVertex`, `EditableEdge`, and `EditableFace` APIs.
- [ ] Capture current triangle-list assumptions and tests.
- [ ] Decide whether `EditableMesh` becomes the production document or wraps a new `ModelingMeshDocument`.
- [ ] Define public ID types:
  - [ ] vertex ID
  - [ ] edge ID
  - [ ] loop/corner ID
  - [ ] face ID
  - [ ] attribute layer ID
- [ ] Define storage-index and generation-counter strategy.
- [ ] Add migration notes for existing tests and bridge code.

### Exit Criteria

- [ ] Existing modeling tests still pass before structural changes.
- [ ] Stable-ID migration shape is documented in code comments or implementation notes.

## Phase 1: Stable-ID Pools

**Goal:** add dense storage plus stable handles for all editable element types.

### Tasks

- [ ] Implement element ID structs with index and generation.
- [ ] Implement free-list allocation and deletion for vertices.
- [ ] Implement free-list allocation and deletion for edges.
- [ ] Implement free-list allocation and deletion for loops/corners.
- [ ] Implement free-list allocation and deletion for faces.
- [ ] Add stable-ID to dense-index lookup with generation validation.
- [ ] Add compaction-safe selection storage.
- [ ] Add tests for ID reuse, stale ID rejection, and compaction.

### Exit Criteria

- [ ] Deleted/stale IDs cannot mutate new elements accidentally.
- [ ] Selection survives compaction.
- [ ] Stable ID tests pass.

## Phase 2: BMesh-Like Topology

**Goal:** represent editable mesh topology without relying on triangle rebuilds after every edit.

### Tasks

- [ ] Add vertex records with position, first edge/loop, flags, and attribute references.
- [ ] Add edge records with two vertices, radial loop reference, crease/sharp flags, and selection flags.
- [ ] Add loop/corner records with vertex, edge, face, next, previous, radial next, and face-varying attributes.
- [ ] Add face records with first loop, material slot, flags, normal cache, and triangulation cache handle.
- [ ] Support loose vertices.
- [ ] Support loose edges.
- [ ] Support boundary edges and faces.
- [ ] Support n-gon faces.
- [ ] Add traversal helpers for vertex fans, edge radial loops, face loops, edge rings, and boundary loops.
- [ ] Add tests for adjacency traversal and non-manifold representation.

### Exit Criteria

- [ ] Topology can represent triangles, quads, n-gons, loose elements, boundaries, and non-manifold cases.
- [ ] Traversal helpers are deterministic and covered by tests.

## Phase 3: Attribute Layers And Domains

**Goal:** make attributes explicit and shared with geometry node evaluation.

### Tasks

- [ ] Define attribute domains: object, point/vertex, edge, corner/loop, face, instance.
- [ ] Define supported value types: bool, int, uint, float, vector2, vector3, vector4, matrix, string/name where needed.
- [ ] Implement named attribute layers.
- [ ] Implement anonymous/temporary attribute storage hooks for geometry nodes.
- [ ] Add loop/corner storage for UVs, normals, tangents, colors, and custom face-varying data.
- [ ] Add interpolation policies to `ModelingOperationOptions`.
- [ ] Add tests for split edge, face split, and bake/export attribute interpolation.

### Exit Criteria

- [ ] Direct tools and future geometry nodes can share attribute domains and interpolation policy.
- [ ] Corner attributes survive topology mutations where policy says they should.

## Phase 4: Dirty Tracking And Derived Caches

**Goal:** separate authored data from disposable derived data.

### Tasks

- [ ] Track dirty topology regions.
- [ ] Track dirty position ranges.
- [ ] Track dirty attribute layers.
- [ ] Track dirty selection state.
- [ ] Track dirty triangulation cache.
- [ ] Track dirty normals/tangents/bounds.
- [ ] Track dirty render buffers, BVH, meshlets, subdivision patches, and physics collision outputs.
- [ ] Add cache invalidation hooks for `XRMesh.ClearAccelerationCaches()` and bridge code.

### Exit Criteria

- [ ] Position-only edits can preserve topology-derived caches where safe.
- [ ] Topology edits invalidate all affected derived caches.

## Phase 5: Undo, Redo, And Validation

**Goal:** make topology edits reversible and diagnosable.

### Tasks

- [ ] Define undo delta records for create/delete/update element operations.
- [ ] Define undo delta records for attribute edits.
- [ ] Define compound operation records for tools.
- [ ] Add undo/redo journal integration.
- [ ] Expand topology validation:
  - [ ] invalid IDs
  - [ ] invalid next/previous links
  - [ ] invalid radial links
  - [ ] degenerate faces
  - [ ] duplicate vertices in a face
  - [ ] zero-area faces
  - [ ] non-manifold edges
  - [ ] boundary/loose warnings
- [ ] Add tests for undo/redo of topology and attribute edits.

### Exit Criteria

- [ ] Undo/redo can restore topology and attributes deterministically.
- [ ] Validation reports actionable issue codes.

## Phase 6: Bake, Export, And Bridge Hooks

**Goal:** produce renderable/runtime data from authored topology without leaking implementation details.

### Tasks

- [ ] Add triangulation cache for faces and n-gons.
- [ ] Add bake to vertex/index buffers.
- [ ] Add bake to `XRMesh` or bridge data model.
- [ ] Preserve material slots.
- [ ] Preserve point/corner attributes needed by rendering.
- [ ] Clear acceleration caches after committed topology edits.
- [ ] Schedule meshlet/bounds/normal/tangent refresh through the runtime bridge.
- [ ] Add source-contract tests for cache invalidation hooks.

### Exit Criteria

- [ ] Authored topology can bake to runtime mesh data with correct attributes.
- [ ] Runtime caches are invalidated explicitly after topology commits.

## Validation

```powershell
dotnet build .\XREngine.Modeling\XREngine.Modeling.csproj
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Modeling
```
