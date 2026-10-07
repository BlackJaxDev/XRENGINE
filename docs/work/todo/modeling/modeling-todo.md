# Modeling TODO

Last Updated: 2026-10-06
Status: Planned
Architecture: Not available. Write `docs/architecture/modeling/` pages for geometry nodes, modeling tools, GPU preview, and subdivision when that code lands. Design: [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md)
Validation: [Modeling validation](../../testing/modeling/modeling-validation.md)

## Current State
`XREngine.Modeling` contains `EditableMesh`, `HalfEdgeTopology`, `ModelingMeshDocument`, and triangle-list validation. `XREngine.Runtime.ModelingIntegration` converts between `ModelingMeshDocument` and `XRMesh`, clears acceleration caches, and has `XRMeshModelingIntegrationTests`. The code does not yet contain stable authoring IDs, `GeometrySet`, geometry node graphs, modeling tool sessions, GPU preview buffers, subdivision evaluators, or editor modeling workflows.

## Open Code Items

### Topology
- [ ] Define stable element ID structs for vertices, edges, loops, faces, and attribute layers. `XREngine.Modeling`. Done when stale IDs cannot mutate reused elements and tests cover reuse and compaction.
- [ ] Add dense storage with free-list allocation for all editable element types. `ModelingMeshDocument`. Done when deletion, reuse, and compaction preserve selection and ID validity.
- [ ] Replace the triangle-list authoring model with records for vertices, edges, loops, and faces. `ModelingMeshDocument`. Done when the document can store triangles, quads, n-gons, boundaries, loose vertices, loose edges, and non-manifold edges.
- [ ] Add deterministic topology traversal helpers. `HalfEdgeTopology` and `ModelingMeshDocument`. Done when tests cover vertex fans, radial edge loops, face loops, edge rings, and boundary loops.
- [ ] Add attribute domains and value types for object, point, edge, corner, face, and instance data. `ModelingMeshDocument` and `ModelingOperationOptions`. Done when direct tools and geometry nodes can use the same domain model.
- [ ] Store loop and corner attributes for UVs, normals, tangents, colors, material slots, and custom face-varying data. `ModelingMeshDocument`. Done when split and bake tests preserve required attributes by policy.
- [ ] Add dirty tracking for topology, positions, attributes, selection, triangulation, normals, tangents, bounds, BVH, meshlets, subdivision patches, and physics collision output. `ModelingMeshDocument`. Done when position-only edits preserve safe caches and topology edits invalidate dependent caches.
- [ ] Add undo and redo delta records for element, attribute, and compound tool changes. `XREngine.Modeling`. Done when undo and redo restore topology and attributes deterministically.
- [ ] Expand topology validation. `ModelingMeshValidation`. Done when diagnostics cover invalid IDs, links, radial loops, degenerate faces, duplicate vertices, zero-area faces, non-manifold edges, boundaries, and loose elements.
- [ ] Add triangulation and bake support for renderable buffers. `ModelingMeshDocument` and `XREngine.Runtime.ModelingIntegration`. Done when n-gons bake to deterministic triangle buffers with required attributes.
- [ ] Add source-level tests for topology migration. `XREngine.UnitTests/Modeling`. Done when current `EditableMeshTopologyOperatorTests` are preserved or replaced by equivalent stable-ID tests.

### Geometry Nodes
- [ ] Define `GeometryNodeGraphAsset` with node IDs, socket IDs, link IDs, group interface IDs, graph version, compatibility flags, parameter metadata, and kernel cache keys. `XREngine.Modeling/GeometryNodes`. Done when graph assets round-trip and invalid assets report diagnostics.
- [ ] Define typed socket values. `XREngine.Modeling/GeometryNodes`. Done when geometry, scalar, vector, matrix, name, material, asset reference, and field sockets validate links before execution.
- [ ] Implement `GeometrySet` with mesh and instance components and extension points for curves, point clouds, volumes, SDFs, attributes, and material bindings. `XREngine.Modeling/GeometryNodes`. Done when unsupported components pass through unchanged unless a node consumes or realizes them.
- [ ] Implement named and anonymous attribute storage for geometry nodes. `XREngine.Modeling/Attributes`. Done when anonymous attributes do not reach authored topology unless captured or stored.
- [ ] Implement typed field IR and evaluation contexts. `XREngine.Modeling/GeometryNodes`. Done when fields evaluate deterministically on object, instance, point, edge, corner, and face domains.
- [ ] Compile graphs to a deterministic execution plan. `XREngine.Modeling/GeometryNodes`. Done when the compiler detects cycles, validates inputs and group interfaces, emits IR, hashes inputs, and reports missing or unsupported data.
- [ ] Implement the CPU reference evaluator and first node set. `XREngine.Modeling/GeometryNodes`. Done when graph input/output, group input/output, transform, join, store named attribute, capture attribute, realize instances, primitive mesh, and instance-on-points nodes have tests.
- [ ] Add graph cache invalidation. `XREngine.Modeling/GeometryNodes`. Done when graph edits, parameter edits, and input geometry changes invalidate the correct outputs.
- [ ] Implement preview, render-cache, and apply or bake outputs for graphs. `XREngine.Modeling` and `XREngine.Runtime.ModelingIntegration`. Done when graph output can stay disposable, render through a cache, or apply to authored topology with one undo record.
- [ ] Define tool-context graph inputs. `XREngine.Modeling/GeometryNodes`. Done when active object, active element, selection masks, ray input, viewport transform, modifier state, and numeric parameters bind to node tools.

### Core Tools
- [ ] Define a renderer-independent modeling tool session API. `XREngine.Modeling/Tools`. Done when tools expose begin, update, commit, cancel, diagnostics, selection requirements, dirty regions, and undo records.
- [ ] Implement basic element operations. `XREngine.Modeling/Operations`. Done when add, remove, move, dissolve, fill, merge, and collapse operations handle loose, boundary, manifold, and non-manifold cases.
- [ ] Implement connect and split operations. `XREngine.Modeling/Operations`. Done when vertex connect, face split, boundary connect, edge split, multi-edge split, and face-path split preserve attributes or report clear diagnostics.
- [ ] Implement knife and loop-cut operations. `XREngine.Modeling/Operations`. Done when cut paths, edge and face intersections, loop traversal, multiple cuts, and slide factors produce deterministic topology.
- [ ] Implement bevel, bridge, extrude, and inset operations. `XREngine.Modeling/Operations`. Done when edge bevel, optional vertex bevel, bridge, extrude, and inset keep target v1 topology valid and preserve material and attribute policy.
- [ ] Implement transform, proportional edit, smooth, relax, merge-by-distance, remove-loose, triangulate, and optional quadrangulate operations. `XREngine.Modeling/Operations`. Done when stable-topology operations update only positions, selection, and attributes as intended.
- [ ] Add tool regression tests. `XREngine.UnitTests/Modeling`. Done when lifecycle, cancellation, validation-after-tool, undo/redo, attribute interpolation, invalid input, and bake/export sequences are deterministic without a renderer.

### GPU Preview
- [ ] Define modeling GPU preview capability probes, ownership, lifetime, ring, and fence policy. `XREngine.Runtime.ModelingIntegration` and `XREngine.Runtime.Rendering`. Done when unsupported features report diagnostics and do not corrupt CPU commits.
- [ ] Add source contracts that keep `XREngine.Modeling` independent of renderer backends. `XREngine.UnitTests`. Done when tests fail if core modeling directly references renderer backend assemblies.
- [ ] Define compact edit buffer layouts for vertices, edges, loops, faces, selection, and attributes. `XREngine.Runtime.ModelingIntegration`. Done when a modeling document can upload editable topology without baking a production mesh.
- [ ] Add dirty-range upload and capacity policy for preview buffers. `XREngine.Runtime.Rendering`. Done when buffer growth, debug names, and diagnostics work without steady-state hot-path allocation.
- [ ] Implement GPU picking and selection queries. `XREngine.Runtime.Rendering` and shaders under `Build/CommonAssets/Shaders`. Done when vertex, edge, face, brush, and nearest-element results avoid synchronous readback in hover, picking, preview, and draw paths.
- [ ] Implement one-frame-late hover result consumption and CPU fallback query paths. `XREngine.Runtime.ModelingIntegration`. Done when fallback is explicit and GPU results never block the UI or render thread.
- [ ] Implement disposable live topology previews. `XREngine.Runtime.Rendering` and shaders. Done when knife, loop-cut, bevel, and bridge previews use preview arenas with overflow detection and safe degraded rendering.
- [ ] Implement stable-topology compute previews. `XREngine.Runtime.Rendering` and shaders. Done when smooth, relax, proportional transform, masked falloff, normals, and bounds previews match CPU commit tolerances.
- [ ] Add editor overlay rendering and preview diagnostics. `XREngine.Editor` and `XREngine.Runtime.Rendering`. Done when vertices, edges, faces, selected elements, hover candidates, cut paths, preview faces, counters, and profiler labels explain preview state.

### Subdivision And OpenSubdiv
- [ ] Add subdivision settings to the authored topology. `XREngine.Modeling`. Done when scheme, preview level, render level, bake level, boundary interpolation, crease policy, face-varying policy, edge creases, and optional vertex creases serialize without an evaluator backend.
- [ ] Define `ISubdivisionEvaluator`. `XREngine.Modeling`. Done when callers can prepare topology, update control points, evaluate preview, bake output, query capabilities, and read diagnostics through the interface.
- [ ] Implement a dependency-free CPU subdivision evaluator. `XREngine.Modeling`. Done when supported Catmull-Clark or a smaller documented subset previews and bakes simple control cages with attribute interpolation.
- [ ] Add evaluator tests with fake and CPU backends. `XREngine.UnitTests/Modeling`. Done when tests do not require native dependencies.
- [ ] Add OpenSubdiv only after owner approval. `Build/Submodules`, `docs/DEPENDENCIES.md`, and `docs/licenses`. Done when the approved supply path, optional build flags, license files, notices, and dependency report are updated.
- [ ] Implement the optional OpenSubdiv backend after approval. `XREngine.Modeling` or an integration assembly. Done when the backend can be enabled or disabled and preserves the evaluator boundary.
- [ ] Connect subdivision output to preview, render, and bake paths. `XREngine.Runtime.ModelingIntegration`. Done when evaluated output can preview, render through transient or cached `XRMesh` data, and bake back with stable IDs and cache invalidation.

### Editor And Runtime Integration
- [ ] Define the ImGui modeling workflow. `XREngine.Editor`. Done when object mode, edit mode, geometry-node mode, selection modes, active tool state, tool options, undo, committed edit updates, and the first supported workflow are documented for implementation.
- [ ] Add an ImGui modeling mode shell. `XREngine.Editor`. Done when selection modes, active tools, options, commit, cancel, validation display, dirty state, cache state, and undo/redo are available in the default editor UI.
- [ ] Render modeling overlays in editor viewports. `XREngine.Editor` and `XREngine.Runtime.Rendering`. Done when selection, hover, active tool previews, and preview fallback diagnostics are visible without mutating production mesh state.
- [ ] Add the first geometry node editor workflow. `XREngine.Editor`. Done when users can create graph assets, inspect node types, edit parameters, see validation errors, preview output, and apply or bake output.
- [ ] Extend runtime conversion for authored topology and evaluated geometry. `XREngine.Runtime.ModelingIntegration`. Done when `ModelingMeshDocument` and `GeometrySet` mesh output convert to `XRMesh`, preserve material slots and required attributes, report unsupported skin data clearly, and clear acceleration caches.
- [ ] Refresh render data after commits and bake operations. `XREngine.Runtime.Rendering`. Done when bounds, BVH, normals, tangents, meshlets, command data, and `GPUScene` updates flow through explicit bridge paths.
- [ ] Add source contracts for preview and production ownership. `XREngine.UnitTests/Rendering`. Done when modeling edit buffers cannot become production `GPUScene` meshlet ownership.
- [ ] Add safe automation only after scope approval. `XREngine.Editor` and MCP docs. Done when read-only tools land first, mutation tools require undo and validation, allowed and denied tool configuration exists, and MCP docs regenerate after tool changes.
- [ ] Update user-facing modeling documentation when workflows, launch flags, tasks, or MCP tools change. `docs/`. Done when users can follow the supported workflow without reading this todo.

## Decisions Needed
- [ ] Decide whether geometry node assets start as runtime assets or editor-only assets. Owner: modeling and editor owner.
- [ ] Decide the first geometry node UI shape. Owner: editor owner.
- [ ] Decide whether vertex bevel and quadrangulate are in v1 scope. Owner: modeling owner.
- [ ] Decide the OpenSubdiv supply path or defer it. Owner: repository owner.
- [ ] Decide whether MCP scope includes raw topology operations, high-level tool commands, geometry graph operations, or all three. Owner: editor and tooling owner.

## Out Of Scope
- Native UI modeling workflows until the ImGui workflow exists.
- OpenSubdiv, submodules, native binaries, or dependency upgrades without owner approval.
- GPU preview data as the authority for committed topology.
- Synchronous GPU readback in hover, picking, preview, or draw paths.
