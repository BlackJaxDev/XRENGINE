# GPU-Accelerated Modeling Tools Design

Last Updated: 2026-05-19
Status: design proposal (post-audit revision 2)
Scope: interactive polygon modeling tools in `XREngine.Modeling`, with GPU acceleration for previews, selection, and stable-topology evaluation.

Note on normativity: every rule in this doc is normative for Phase 1+ work. Words like "should" and "may" describe choices already made; rationale is included but the rules are not optional unless the section header says "Optional" or "Future."

Related docs:

- [GPU meshlet zero-readback rendering design](../rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [GPU meshlet zero-readback rendering TODO](../../todo/rendering/gpu/gpu-meshlet-zero-readback-rendering-todo.md)
- [Mesh submission strategies](../../../architecture/rendering/mesh-submission-strategies.md)
- [Model import binary cache design](../assets/model-import-binary-cache-design.md)
- [MCP server](../../../features/mcp-server.md)

External references:

- Blender Geometry Nodes overview: https://developer.blender.org/docs/features/nodes/
- Blender geometry socket design: https://developer.blender.org/docs/features/nodes/geometry_socket/
- Blender Geometry Nodes modifier manual: https://docs.blender.org/manual/en/latest/modeling/modifiers/generate/geometry_nodes.html
- Blender 4.2 node tools notes: https://developer.blender.org/docs/release_notes/4.2/geometry_nodes/
- Blender BMesh design: https://developer.blender.org/docs/features/objects/mesh/bmesh/
- Blender mesh data model: https://developer.blender.org/docs/features/objects/mesh/mesh/
- OpenSubdiv overview: https://www.pixar.com/technology-libraries
- OpenSubdiv repository: https://github.com/PixarAnimationStudios/OpenSubdiv
- OpenSubdiv license: https://raw.githubusercontent.com/PixarAnimationStudios/OpenSubdiv/release/LICENSE.txt
- OpenSubdiv NOTICE: https://raw.githubusercontent.com/PixarAnimationStudios/OpenSubdiv/release/NOTICE.txt

## 1. Summary

XRENGINE should support Blender-like edit tools such as add/remove vertices, connect vertices with new edges, knife cuts, loop cuts, bevels, smooth/relax, bridge, extrude, inset, and subdivision preview. It should also support a Blender-style procedural geometry node layer for non-destructive modeling, instancing, scattering, attribute processing, and reusable node tools.

The right architecture is node-first but not GPU-topology-owned. Geometry node graphs should be a first-class way to generate and transform geometry, while topology editing remains deterministic dynamic graph mutation. The CPU must remain the authoritative owner of mesh identity, adjacency, undo, validation, serialization, and export.

The GPU should make interaction feel instant by accelerating the parts that are embarrassingly parallel or disposable:

- picking and nearest element queries
- hover/cut/bevel preview geometry
- brush falloff and smooth/relax previews
- large selection masks
- background normal/tangent/bounds/meshlet refresh
- subdivision surface evaluation when topology is stable

The commit path applies the tool operation to the authoritative `XREngine.Modeling` document, then updates rendering buffers and derived caches through dirty ranges or background rebuilds. GPU work is allowed to be ahead of the CPU visually, but the CPU model is the source of truth.

## 2. Decision

Adopt a hybrid modeling architecture:

```text
Editor input
    -> Modeling tool session or geometry node graph
    -> CPU authoritative editable topology
    -> procedural GeometrySet evaluation
    -> GPU preview and query buffers
    -> visible preview geometry
    -> commit operation
    -> CPU topology mutation + undo record
    -> render/physics/import-cache invalidation
    -> GPU buffer refresh
```

This mirrors the mature DCC pattern used by Blender: edit mode keeps a CPU-side topology structure suitable for small graph edits, while GPU paths draw, preview, and evaluate derived geometry.

## 3. OpenSubdiv Position

OpenSubdiv is allowed in principle, but it should be an optional dependency for subdivision surface evaluation, not the core topology editing engine.

OpenSubdiv is a set of open source libraries for high-performance subdivision surface evaluation on massively parallel CPU and GPU architectures. Its own description says the optimized path targets deforming subdivs with static topology at interactive framerates. That is a strong fit for smooth/subdivision preview and final evaluated meshes, but it does not replace our editable half-edge/BMesh-style topology.

License posture:

- The current OpenSubdiv repository uses a Pixar-published license commonly described as a modified Apache 2.0 with a trademark carve-out (recent versions reference "Tomorrow Open Source Technology License 1.0" or similar wording; verify the exact name and text against the repo at integration time).
- The license grants copyright and patent permissions and supports redistribution in source or object form when license/notice obligations are preserved.
- The repository includes a `NOTICE.txt`; if distributed, the notice must be preserved in our third-party notices.
- This appears compatible with the repository rule that dependencies must permit open-source and commercial use, but the exact license text and name must be reconfirmed before integration.
- Before actually adding OpenSubdiv as a submodule or native binary, run the normal dependency process:

```powershell
pwsh Tools/Generate-Dependencies.ps1
```

Then review and include updated `docs/DEPENDENCIES.md` and `docs/licenses/`.

Recommended integration:

- Add OpenSubdiv only after the CPU modeling document and subdivision API boundary exist.
- Prefer a separate native wrapper or integration layer instead of referencing OpenSubdiv directly from general modeling tools.
- Build only required libraries for Windows first.
- Disable examples, tutorials, docs, Ptex, OpenCL, CUDA, and optional viewer dependencies unless a later feature requires them.
- Use OpenSubdiv for Catmull-Clark/Loop subdivision evaluation, patch tables, limit surface preview, and final evaluated surface generation.
- Keep authored topology, edge tags, crease weights, face-varying attributes, and undo records in `XREngine.Modeling`.

## 4. Geometry Nodes Architecture

Geometry nodes should be designed as the procedural authoring layer that sits beside direct edit tools:

```text
Authored object/control cage
    -> modifier stack
    -> GeometryNodeGraph evaluation
    -> GeometrySet
    -> preview/render/bake output
```

Direct edit tools mutate the authored mesh document. Geometry nodes evaluate from that authored state into derived geometry. Applying a node graph bakes the evaluated result back into an authored `ModelingMeshDocument` or `XRMesh`, depending on the user's target.

### 4.1 GeometrySet

Follow Blender's strongest architectural idea here: use a single geometry socket that carries a geometry container instead of separate socket types for mesh, curve, point cloud, volume, and instances.

Initial shape:

```text
GeometrySet
    MeshComponent
    CurveComponent
    PointCloudComponent
    VolumeOrSdfComponent
    InstancesComponent
    AttributeSet
    MaterialBindings
```

Benefits:

- Transform, instance, material, and attribute nodes can operate on multiple geometry kinds.
- Node groups stay reusable.
- Instances can reference nested `GeometrySet` values without losing source identity.
- Future curves, SDFs, volumes, particles, and splines do not require redesigning the socket system.

Nodes declare which components they read and write. Unsupported components pass through unchanged unless the node explicitly consumes or realizes them.

### 4.2 Fields And Attribute Domains

Use fields for deferred per-element values:

```text
Field<T> + EvaluationContext -> T per element
```

`Field<T>` is a **typed IR node tree**, not a `Func<>` delegate, not an expression tree, and not a captured closure. The IR shape is roughly:

```text
FieldNode<T>
    input fields (FieldNode<U>)
    constant payload (struct, no boxing)
    Evaluate(in EvaluationContext, Span<T> output, Span<int> indices)
```

Rules:

- A `FieldNode` MUST NOT allocate per evaluation call. All scratch buffers come from a pooled `EvaluationContext`.
- Captured closures, LINQ, boxed structs, and per-element delegate invocation are banned in evaluation. The evaluator walks the IR with a stack of `Span<T>` views.
- `FieldNode` instances are immutable and shareable across threads. Graph compilation produces one IR per graph, reused for every evaluation.
- AOT-friendly: no `Expression.Compile()`, no `Reflection.Emit`. Custom field operations register through the AOT factory registration flow already used by the engine.

Attribute domains:

- object
- instance
- point/vertex
- edge
- corner/loop
- face
- curve point
- spline
- volume grid

The modeling topology must keep loop/corner attributes because UVs, normals, tangents, and colors often live per face corner rather than per vertex.

Support two attribute modes:

- Named attributes for persistent authoring/export data.
- Anonymous attributes for temporary graph values that should not leak into the authored mesh unless captured/stored.

### 4.3 Graph Evaluation

Node graphs should compile into a small internal IR before evaluation:

```text
NodeGraph asset
    -> validated typed graph
    -> dependency DAG
    -> execution IR
    -> CPU reference evaluator
    -> optional GPU compute kernels
```

The CPU reference evaluator is required first. It gives us deterministic tests, editor reliability, and a fallback for unsupported GPUs. GPU compute should accelerate specific kernels once the semantics are stable.

Evaluation rules:

- Use structural hashes to cache node outputs.
- Track dirty inputs, node parameters, attribute dependencies, and scene resource dependencies.
- Keep evaluation side-effect-free until a graph is explicitly applied or baked.
- Do not let node evaluation mutate the base modeling document implicitly.
- Allow graph assets to be reused across objects.

### 4.4 Node Tools

Geometry node groups can also become editor tools. A node tool is a graph with an explicit tool context:

```text
ToolContext
    active object
    active vertex/edge/face
    selection masks
    mouse/controller ray
    viewport transform
    keyboard/modifier state
    numeric parameters
```

This lets artists build procedural tools such as scatter-on-selection, bevel presets, surface decals, curve-to-mesh tools, cleanup tools, or kitbash placement tools without writing C#.

For v1, native C# tools should still exist for foundational topology edits. Later, those native operations can also be exposed as geometry nodes so the same operation works in a tool session, modifier stack, or scripted/MCP workflow.

### 4.5 Bake And Apply Semantics

Node output has three destinations:

- Preview: evaluated but disposable.
- Render: evaluated into transient render buffers or an `XRMesh` cache.
- Apply/Bake: converted into authored editable topology with stable IDs and undo records.

Applying a node graph must:

- allocate stable element IDs
- preserve/capture requested attributes
- triangulate only where required by the output format
- clear or regenerate derived caches
- create one undo step

### 4.6 Asset Model

Geometry node graphs should be serializable assets:

```text
GeometryNodeGraphAsset
    nodes
    sockets
    links
    exposed parameters
    version
    compatibility flags
    optional generated kernel cache key
```

Node groups should be nestable and publish typed inputs/outputs. The editor can expose graph parameters as modifier properties, tool properties, or prefab/asset parameters.

## 5. Goals

- Provide a coherent modeling tool framework in `XREngine.Modeling`.
- Make geometry nodes a first-class procedural modeling and tool-authoring layer.
- Keep topology correctness deterministic and testable on CPU.
- Make interactive previews feel immediate without GPU readback stalls.
- Support common edit tools: vertex add/remove, edge connect/delete, split edge, knife cut, loop cut, bevel, bridge, smooth/relax, extrude, inset, and subdivision preview.
- Support procedural graph outputs including meshes, curves, point clouds, instances, SDF/volume placeholders, attributes, and material bindings.
- Preserve stable IDs for vertices, edges, faces, and loops/corners across tool sessions.
- Support undo/redo through operation records or topology deltas.
- Maintain per-element attributes: position, normal, tangent, UV, color, material slot, skin weights, crease flags, sharp flags, selection, and custom data.
- Allow rendering to use dirty range uploads and background cache rebuilds rather than full rebuilds for every small edit.
- Keep `XREngine.Modeling` independent from renderer backends where possible.

## 6. Non-Goals

- Do not make GPU buffers the only authoritative copy of editable topology.
- Do not require GPU readbacks for normal per-frame interaction.
- Do not use geometry shaders for persistent topology edits.
- Do not require OpenSubdiv for the first modeling tools milestone.
- Do not clone Blender's entire geometry node library in the first version.
- Do not require every direct modeling tool to be authored as a node graph in the first milestone.
- Do not mix modeling edit topology with production `GPUScene` meshlet ownership.
- Do not require complete Blender parity in the first version.

## 7. Current Local Baseline

The repository already has a starter modeling project:

- `XREngine.Modeling/EditableMesh.cs` exposes add vertex, connect vertices, split/collapse edge, extrude, inset, bevel, bridge, loop cut, transform, validation, and bake.
- `XREngine.Modeling/HalfEdgeTopology.cs` rebuilds adjacency, half-edge links, edge-to-face maps, loop traversal, and topology validation.
- `XREngine.UnitTests/Modeling/EditableMeshTopologyOperatorTests.cs` covers split/collapse, extrude/inset/bevel, loop cut, and bridge.
- `XREngine.Runtime.ModelingBridge` provides import/export and runtime bridge scaffolding.

This baseline is suitable for unit-test fixtures and import/export wiring, but it is **incompatible with the architecture in this doc** and Phase 1 is a rewrite, not an evolution:

- The baseline is triangle-list oriented; it cannot represent n-gons, loops/corners, loose edges, or non-manifold topology.
- Every mutating call (e.g. `AddVertex`, `SetVertex`) snapshots the full vertex list and calls `Reset`/`Rebuild`. This violates the hot-path allocation rules in Section 18.
- The 2-sided half-edge structure cannot represent non-manifold edges (see Section 8.2).
- There are no stable IDs, no loop/corner attribute layers, no undo journal, and no dirty-region tracking.

Phase 1 replaces `HalfEdgeTopology` and rewrites `EditableMesh` against the new document. The existing tests should be ported to the new operators, not preserved verbatim.

## 8. Core Data Model

### 8.1 Modeling Document

Create or expand `ModelingMeshDocument` as the root authoring object:

```text
ModelingMeshDocument
    Vertices: stable-id pool
    Edges: stable-id pool
    Loops/Corners: stable-id pool
    Faces: stable-id pool
    Attribute layers
    Selection state
    Tool session state
    Undo/redo journal
    Dirty regions
    Derived cache handles
```

Stable IDs are required so editor selection, gizmos, previews, undo records, and external tool APIs can survive array compaction.

Implementation details:

- Use dense arrays plus free lists and generation counters.
- Separate stable element IDs from storage indices.
- Keep adjacency explicit, not inferred from triangle lists after every edit.
- Track dirty spans by element type and attribute layer.
- Allow n-gons at the authoring level, even if bake/export triangulates them.
- Preserve boundary, non-manifold, and loose elements intentionally; validation reports problems instead of silently deleting data.

### 8.2 Element Types

Use a **BMesh-style radial-loop topology**, not a classical 2-sided half-edge. This is a deliberate choice:

- A 2-sided half-edge cannot represent non-manifold edges (3+ faces sharing an edge). Section 6 commits to preserving non-manifold geometry, so a 2-sided half-edge is disqualified.
- BMesh-style topology stores a **radial cycle of corners** per edge (one per incident face), which represents any number of incident faces and degrades cleanly to manifold and boundary cases.
- Loose vertices and loose edges are first-class (see Section 8.5).

Element shapes:

- `Vertex`: position, first incident corner, attribute references, flags (selected, hidden, loose).
- `Edge`: two vertex IDs, first radial corner, flags (crease weight, sharp, seam, boundary marker, selected, hidden).
- `Corner` (Blender's "Loop"): face corner with vertex ID, edge ID, next/previous corner around the face, **radial next/previous corner around the edge**, UV/color/normal/tangent/face-varying data.
- `Face`: first corner, material slot, cached normal, flags (smooth, selected, hidden, n-gon triangulation dirty), optional triangulation cache.

The radial pointers on `Corner` are what make non-manifold and boundary cases work. Walk the radial cycle to enumerate all faces touching an edge.

Flag enumerations must be defined in Phase 1 and include subdivision-relevant data: vertex crease weight, edge crease weight, edge sharp flag, edge seam flag, face smooth flag. Adding these later is a topology-format break.

`Corner` is the canonical home for face-varying attributes. UVs, vertex colors, and split normals belong on the corner, not the vertex.

### 8.2.1 Stable-ID Compaction

Stable IDs use dense storage + free list + generation counter. Without compaction, ID ranges grow unbounded in long sessions and serialized files inherit the bloat.

Requirements:

- A `Compact()` operation runs on explicit user/tool request (save, export, large bake), not implicitly per-edit.
- `Compact()` produces a `RemapTable` (`Dictionary<OldId, NewId>` or parallel arrays per element type) returned to callers.
- Selection state, undo journal entries, MCP client snapshots, and any external observer must consume the remap table or invalidate.
- Generation counters bump on free; stale IDs detected post-compaction return a typed `StaleElementId` error rather than aliasing a recycled slot.

### 8.3 Derived Caches

Keep these derived and disposable:

- triangulation cache
- render vertex/index buffers
- normals/tangents
- bounds
- BVH or spatial acceleration
- meshlets
- subdivision patch/evaluation buffers
- physics collision meshes

Dirtying topology invalidates these caches. Dirtying only positions can preserve most topology-derived caches.

### 8.4 Loose Elements And Render Story

Loose vertices and loose edges are part of the authored document. They have no faces, so the production mesh path does not render them. Render policy:

- Loose vertices and edges render **only through the dedicated editor overlay pipeline** (Section 11) when the object is in edit mode.
- Bake/export to runtime `XRMesh` drops loose elements unless an exporter explicitly opts in (e.g. for wireframe-only debug meshes).
- Validation reports loose elements as informational, not as errors.

### 8.5 Procedural Graph Companion

Objects should be able to own both an authored topology document and procedural graph stack:

```text
ModelingObject
    BaseDocument
    ModifierStack
        GeometryNodeGraph
        SubdivisionPreview
        Other procedural modifiers
    EvaluatedGeometryCache
```

The base document is editable. The evaluated cache is disposable. Applying a graph converts the evaluated cache into a new base document or replaces the selected part of the existing document through an explicit operation.

The full specification of the modifier stack, including reorder rules, instance/reference sharing, and per-modifier evaluation contracts, is in Section 23.

## 9. Tool Framework

Modeling tools should be explicit command/session objects:

```text
IModelingTool
    Begin(session)
    Update(input, previewContext)
    Commit(document)
    Cancel()
```

Each tool defines:

- required selection mode
- editable element types
- preview GPU jobs
- CPU commit operation
- undo delta
- affected dirty regions
- validation requirements

Tool commits must be deterministic and unit-testable without a renderer.

Geometry node tools use the same lifecycle, but their `Update` and `Commit` steps evaluate a graph with `ToolContext` inputs.

## 10. Tool Ownership

### 10.1 CPU-Authoritative Topology Tools

These should commit on CPU:

- Add vertex
- Remove vertex
- Connect vertices
- Delete edge/face
- Split edge
- Knife/cut through edges
- Loop cut
- Bridge edges/loops
- Extrude
- Inset
- Bevel commit
- Merge/collapse
- Dissolve
- Fill face
- Triangulate/quadrangulate

The GPU may preview them, but commit belongs to the CPU document.

### 10.2 GPU-Friendly Interactive Tools

These can use compute shaders heavily:

- brush smooth/relax preview
- proportional editing falloff
- selection grow/shrink masks
- nearest vertex/edge/face picking
- live bevel strip preview while dragging amount/segments
- knife cut intersection preview
- transform preview for large selections
- normal/tangent/bounds regeneration
- subdivision surface evaluation for stable topology

The GPU preview is disposable. It must be legal to discard and recompute it if the user changes selection, view, tool options, or source topology.

### 10.3 Procedural Node Tools

These should be possible as geometry node groups:

- scatter/instance on selected faces
- align objects to surface normals
- generate curves from selected edges
- create cables, pipes, fences, stairs, panels, and trims from curves or edge selections
- procedural bevel/inset presets
- cleanup tools that remove loose geometry, merge by distance, or store attributes
- SDF/blockout generators

Node tools should call into the same validated topology operations where they commit authored mesh changes.

## 11. GPU Preview Architecture

### 11.1 Backend Posture

The renderer is OpenGL 4.6 primary with Vulkan/DX12 WIP. Modeling preview compute work must go through the engine's existing cross-backend program abstraction, not raw GL calls:

- Preview compute kernels are authored once and dispatched through the same abstraction the renderer uses for compute (whichever `XRComputeProgram`/equivalent lands in the rendering layer).
- The first shipping milestone targets the GL backend. Vulkan/DX12 parity tracks the renderer's own backend progress; modeling does not gate on it.
- Where a backend lacks a needed compute feature, the tool falls back to the CPU reference path rather than disabling.
- Preview overlay draws use a **dedicated editor overlay pipeline**, not the production material table, so modeling release cycles do not couple to material/renderer churn.

### 11.2 Buffer Layout

Use compute shaders and SSBOs/storage buffers for preview work.

```text
CPU document snapshot
    -> compact edit buffers
    -> tool input buffer
    -> compute preview dispatch
    -> preview vertex/index/line buffers
    -> editor overlay draw
```

Preview buffer examples:

- `ModelingVertexBuffer`
- `ModelingEdgeBuffer`
- `ModelingLoopBuffer`
- `ModelingFaceBuffer`
- `ModelingSelectionBuffer`
- `ModelingToolCommandBuffer`
- `ModelingPreviewVertexBuffer`
- `ModelingPreviewIndexBuffer`
- `ModelingPreviewCounterBuffer`

Rules:

- Never block the UI on preview readback.
- Use GPU-written counters only for drawing with indirect count when supported.
- Prefer fixed-capacity preview arenas with overflow diagnostics.
- If preview capacity overflows, show a degraded preview and keep the CPU commit valid.
- Use fences only to avoid overwriting in-flight buffers, not to stall for results.

## 12. Why Not Geometry Shaders

Geometry shaders are not a good foundation for modeling edits:

- They generate transient primitives during draw, not persistent topology.
- They are usually slower and less predictable than compute or mesh shaders.
- They are awkward for random writes, allocation, compaction, and undo.
- They do not solve adjacency mutation or attribute ownership.

They are acceptable for simple debug overlays, but not for production edit tools.

## 13. Tool Details

### 13.1 Add/Remove Vertices

CPU:

- allocate/free stable vertex IDs
- update adjacency
- mark topology dirty
- record undo delta

GPU:

- preview placement point
- draw snap hints
- update hover/cursor overlays

### 13.2 Connect Vertices

CPU:

- create an edge if one does not exist
- split faces if connecting vertices within the same face
- preserve loop/corner attributes
- validate non-manifold cases

GPU:

- preview candidate edge
- detect crossing/intersection warnings
- display affected face split

### 13.3 Knife/Cut

CPU:

- compute exact intersections against selected edges/faces
- insert vertices at intersection parameters
- split affected edges and faces
- preserve interpolated attributes

GPU:

- ray/plane/line intersection preview
- draw cut path
- show inserted vertex markers
- optionally compute candidate intersections in parallel

Commit must not depend on GPU readback. The CPU recomputes the cut from the final input path snapshot. See Section 13.10 for the preview/commit parity rule that all continuous tools must satisfy.

### 13.4 Loop Cut

CPU:

- walk edge rings through half-edge/loop topology
- insert one or more vertices per crossed edge
- reconnect faces consistently
- support slide factor

GPU:

- preview the candidate loop/ring
- draw inserted rings for multiple cuts
- preview slide amount

### 13.5 Bevel

CPU:

- create bevel topology on commit
- support amount, segments, profile, miter mode, harden normals, affected selection mode
- preserve and interpolate attributes

GPU:

- generate temporary bevel strip preview
- update live as amount/segments/profile change
- use fixed-capacity preview arenas

### 13.6 Bridge

CPU:

- validate compatible loops or edges
- create faces between loops
- support twist offset, segment count, interpolation, smoothing

GPU:

- preview face strips
- highlight incompatible loops

### 13.7 Smooth/Relax

CPU:

- commit final positions if the user accepts
- preserve topology
- update dirty position ranges

GPU:

- run iterative smoothing or relax passes on selected vertices
- support masked/proportional falloff
- show result immediately while dragging

This is one of the best early GPU acceleration candidates because topology is stable.

### 13.8 Subdivision Preview

CPU:

- stores authored control cage, creases, boundaries, and attributes
- owns the preview/subdivision settings
- can bake the evaluated mesh when requested

GPU/OpenSubdiv:

- evaluates subdivision surfaces for preview and render
- handles static topology with animated/deforming positions efficiently
- can produce limit-surface positions/normals for display

OpenSubdiv should be introduced behind an interface:

```text
ISubdivisionEvaluator
    PrepareTopology(document, settings)
    UpdateControlPoints(positionBuffer)
    EvaluatePreview(...)
    Bake(...)
```

Initial implementation can be internal or CPU-only. OpenSubdiv can become one backend when the dependency is accepted.

### 13.10 Preview/Commit Parity Contract

Tools whose preview is parameterized by a continuous input (bevel amount/segments/profile, loop cut slide factor, inset thickness, knife cut parameter, smooth iterations, proportional falloff radius) **must** satisfy:

- Preview GPU kernel and CPU commit operator consume the same parameter struct.
- The math is shared via a single formula module (e.g. a static class consumed by C# and translated to shader source, or a generated shader header).
- Each such tool has a unit test that runs both paths against a fixture and asserts vertex positions agree within a stated ε (typically 1e-5 in object space).
- When the GPU and CPU disagree by more than ε, the GPU path is treated as a regression and the test fails. The CPU path is the reference.

This rule exists to prevent the "visible snap on release" failure mode where preview and commit drift apart silently.

### 13.9 Geometry Node Modifiers

Geometry node modifiers evaluate after direct edit data and before final render/bake output.

Examples:

- scatter bolts across selected faces
- convert selected curves to tubes
- procedurally generate panel lines, rivets, trims, or cables
- store masks and material IDs as attributes
- instance prefabs while keeping instance data compact
- convert point clouds or SDFs into renderable meshes when needed

The renderer should consume evaluated output, not the node graph itself, unless a later backend can execute selected graph kernels directly.

## 14. Rendering Integration

Modeling edit buffers should not become production `GPUScene` mesh ownership. Instead:

```text
Modeling document + geometry node stack
    -> editable preview buffers for editor overlays
    -> baked XRMesh or render mesh for normal rendering
    -> GPUScene upload and meshlet/cache refresh
```

During edit mode:

- render the object through normal mesh rendering using last committed/baked mesh
- overlay the live preview using modeling preview buffers
- optionally replace the object draw with a live evaluated preview when topology/positions are stable

On commit:

- apply CPU topology delta
- refresh render mesh dirty ranges
- clear affected `XRMesh` acceleration caches
- schedule normals/tangents/bounds/meshlets rebuild
- update `GPUScene` only after the baked/renderable representation is ready

This keeps zero-readback rendering and modeling edit state decoupled.

### 14.1 Render Bridge Ownership

To avoid re-debating dirty-range scheduling later, the boundary with the zero-readback GPUScene design is:

- The modeling bridge (`XREngine.Runtime.ModelingBridge`) **writes intent**: it produces a typed `MeshDirtyRegion` describing which vertex/index ranges, attribute layers, and derived caches changed on commit.
- `GPUScene` and the renderer **own scheduling**: when uploads run, how they coalesce with other scene updates, when meshlets/bounds rebuild, and which frame the change becomes visible.
- The modeling layer never calls renderer upload APIs directly. The bridge publishes `MeshDirtyRegion` and the renderer pulls.
- See [GPU meshlet zero-readback rendering design](../rendering/gpu-meshlet-zero-readback-rendering-design.md) for the consumer contract.

### 14.2 Threading And Concurrency

The modeling stack has a single explicit threading contract:

- **Document mutation is single-threaded.** Only one thread mutates a given `ModelingMeshDocument` at a time. Tool commits acquire a per-document write lock.
- **Document reads are concurrent.** Multiple reader threads (preview, picking, node evaluation, render bridge) may read a document while no writer holds the lock. Mutation publishes a new immutable snapshot reference for in-flight readers to drain.
- **Node graph evaluation runs on a worker pool**, never on the render thread. The CPU reference evaluator is required to be re-entrant and stateless except for pooled scratch from `EvaluationContext`.
- **GPU preview dispatch runs on the render thread** (or whichever thread owns the backend's command queue). Preview compute kernels read GPU buffers that were uploaded from a prior committed snapshot; they do not read the live document.
- **Picking** runs on a dedicated picking thread that holds a read snapshot. Sync picks (Section 16.1) return on the picking thread; the caller marshals to the UI thread.
- **Undo journal writes** are part of the commit critical section.
- **Background cache rebuilds** (normals, bounds, BVH, meshlets) run on the worker pool against an immutable snapshot taken at commit time. Stale results are discarded if a newer commit superseded the snapshot.

## 15. Undo And Determinism

Undo uses a **command + inverse-delta hybrid**. Each undoable operation produces both:

- A `ToolCommand` describing intent: typed operation name, parameter struct, input element IDs. Used for MCP, scripting, repro logs, and history UI labels. Not replayed on undo.
- An `InverseDelta` describing the minimum mutation required to reverse the operation: created/deleted element IDs, attribute layer slice deltas, adjacency link before/after where applicable, dirty cache list.

Undo applies the inverse delta directly. Redo applies the forward delta. The command is metadata; it is never replayed to reverse an operation, because pure command replay is fragile across merges and floating-point variation.

Rules:

- Undo records are CPU-side. GPU preview buffers are never undo state.
- Inverse-delta payloads are pooled where possible (selection deltas in particular).
- Every topology operator has a deterministic unit test in `XREngine.UnitTests/Modeling`.
- Geometry node graphs are undoable as asset/property edits through the same `ToolCommand`/`InverseDelta` machinery. Applying a graph is a single topology operation in the journal.
- Stable-ID compaction (Section 8.2.1) clears the undo journal or applies the remap table to all journal entries; partial remap is not allowed.

## 16. Selection And Picking

Selection has two layers:

- CPU authoritative selected element IDs.
- GPU masks and hover IDs for interactive previews.

Picking is split by latency requirement. The two paths are **not** symmetric:

### 16.1 Synchronous Pick (CPU BVH)

Click-to-select, marquee/box select, lasso select, and any operation where the user expects the result on the same frame use a **CPU spatial index**:

- A BVH (or grid for small meshes) is built per modeling document and refreshed on commit.
- Ray and shape queries run on the picking thread and return on the same frame.
- This path never depends on the GPU.

Reasoning: users perceive selection lag as a bug; preview lag is acceptable.

### 16.2 Asynchronous Pick (GPU Mask)

Hover highlight, brush coverage masks, proportional-editing falloff visualization, and bulk-coverage queries on huge meshes use the GPU:

```text
screen ray / brush shape
    -> compute query against compact vertex/edge/face buffers
    -> hover/coverage buffer
    -> overlay draw
```

Rules:

- Never block the render thread for a pick readback.
- One-frame-late hover state is acceptable.
- GPU pick results are never authoritative for commits. If a tool needs the result to commit, it routes through the CPU path (Section 16.1).

## 17. Attribute Handling

All topology-changing tools resolve attribute behavior through a single shared policy enum and default table, owned by `ModelingOperationOptions`. The policy is defined in Phase 1 so every later tool consumes the same vocabulary.

### 17.1 Policy Enum

```text
enum AttributePolicy
    Interpolate   // linearly/barycentrically interpolate from source elements using the operation's parameter
    Copy          // copy from a designated source element (first source, dominant source, etc.)
    Default       // reset to the layer's declared default value
    Reject        // operation fails if this attribute is present and no explicit override is provided
    Custom        // operation provides a per-attribute callback (must satisfy Section 18 allocation rules)
```

### 17.2 Default Table

Built-in attribute layers have these defaults; tools may override per operation through `ModelingOperationOptions`:

| Layer                | Domain        | Default Policy |
|----------------------|---------------|----------------|
| Position             | vertex        | Interpolate    |
| Normal               | vertex/corner | Default (recompute on commit) |
| Tangent              | corner        | Default (recompute on commit) |
| UV0..UVn             | corner        | Interpolate    |
| Color                | corner        | Interpolate    |
| Skin weights         | vertex        | Interpolate (renormalized) |
| Skin indices         | vertex        | Copy (dominant) |
| Material slot        | face          | Copy (source face) |
| Crease weight        | vertex/edge   | Copy           |
| Sharp flag           | edge          | Copy           |
| Seam flag            | edge          | Copy           |
| Smooth flag          | face          | Copy           |
| Selection flag       | any           | Default        |
| Custom named layer   | any           | Reject (must opt in) |
| Custom anonymous     | any           | Default        |

Geometry node attribute nodes consume the same enum and table so direct tools and procedural tools agree on behavior.

## 18. Performance Rules

- No heap allocations in per-frame modeling preview hot paths after setup.
- No LINQ in tool update loops.
- No synchronous GPU readback in hover, preview, or draw paths.
- Use pooled arrays/lists for CPU temporary topology work.
- Use fixed-capacity GPU arenas and visible overflow diagnostics.
- Rebuild broad caches on background jobs when possible.
- Upload only dirty ranges for position-only edits.
- Treat full topology rebuilds as commit-time work, not per-frame preview work.
- **Node and field evaluation must not allocate per element.** Captured closures, `Func<>` per element, boxed structs, and per-element delegate invocation are banned in evaluation. Use the IR-node form described in Section 4.2.
- `foreach` over non-struct enumerators is banned in per-frame modeling paths.

## 19. Validation

### 19.1 Determinism Contract

Every topology operator has a **golden-hash snapshot test**. The canonical hash is:

```text
TopologyHash =
    H(
        sorted vertex IDs,
        sorted edge keys with crease/sharp flags,
        per-face loop hash (face id, ordered corner ids, material slot),
        per-layer attribute hash for each named attribute
    )
```

The hash is implemented in `XREngine.UnitTests/Modeling` as a deterministic 128-bit value. Operator tests load a fixture, apply the operation, and assert the hash matches a stored expected value. Hash changes require explicit fixture updates and are visible in diffs.

### 19.2 Required Tests

- add/remove vertex preserves stable IDs and validation reports expected loose vertices
- connect vertices splits an n-gon/face correctly
- split edge interpolates attributes
- knife cut inserts vertices and splits affected faces
- loop cut follows ring traversal and handles boundaries
- bevel commit creates valid topology for boundary and interior edges
- bridge validates incompatible loops and creates expected faces
- smooth/relax preserves topology and only changes selected positions
- subdivision evaluator interface can run with no OpenSubdiv dependency
- OpenSubdiv backend, if enabled, preserves license metadata and can be disabled
- render bridge clears `XRMesh` caches and schedules GPUScene refresh after commit
- geometry node graph type validation rejects invalid links
- geometry socket passes unsupported components through unchanged
- field evaluation produces deterministic values per domain
- named and anonymous attributes have correct lifetime semantics
- node tool context exposes active element, selection, and viewport/controller ray
- applying a node graph creates stable IDs and one undo record

Continuous-input tools additionally satisfy the preview/commit parity test required by Section 13.10.

Useful validation commands:

```powershell
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Modeling
dotnet build .\XREngine.Modeling\XREngine.Modeling.csproj
dotnet build .\XREngine.Runtime.ModelingBridge\XREngine.Runtime.ModelingBridge.csproj
```

## 20. Phased Implementation

Phase ordering puts artist value first. Node infrastructure lands **after** core CPU tools and GPU preview are stable, so artists are not blocked on graph plumbing to get reliable bevels and loop cuts.

### Phase 0: Document And Boundaries

- Land this design.
- Keep `XREngine.Modeling` renderer-independent.
- Define tool/session interfaces.
- Define subdivision evaluator interface without adding OpenSubdiv yet.
- Define `GeometrySet`, geometry components, attribute domains, and field concepts on paper (no code).
- Define the serializable geometry node graph asset shape on paper.

### Phase 1: CPU Topology Foundation (rewrite)

- Replace `HalfEdgeTopology` with a BMesh-style radial-loop topology (Section 8.2).
- Implement stable IDs with free-list + generation, plus the `Compact()` operation (Section 8.2.1).
- Add Vertex/Edge/Corner/Face element types with the flag set from Section 8.2, including crease/sharp/seam/smooth flags.
- Add loop/corner attribute layers.
- Support loose vertices/edges and n-gon faces.
- Implement the `AttributePolicy` enum + default table (Section 17).
- Implement the `ToolCommand` + `InverseDelta` undo journal (Section 15).
- Implement the topology hash and golden-hash test harness (Section 19.1).
- Port the existing topology tests to the new operators.

### Phase 2: Core CPU Tools + GPU Preview Layer

This phase delivers the first artist-usable tool set with previews. It combines what was previously Phases 3 and 4.

- Implement add/remove vertex.
- Implement connect vertices with face split.
- Implement edge/face delete and dissolve.
- Implement split edge and loop cut.
- Implement bridge edges/loops.
- Implement extrude, inset, and harden bevel commit.
- Add compact GPU edit buffers and modeling preview command buffers.
- Implement the CPU BVH picking path (Section 16.1).
- Implement GPU hover/coverage masks (Section 16.2).
- Implement live bevel, knife/cut, loop-cut slide, and bridge preview buffers.
- Add the dedicated editor overlay pipeline (Section 11.1).
- Add overflow diagnostics for preview arenas.
- Implement preview/commit parity tests for every continuous-input tool (Section 13.10).

### Phase 3: Stable-Topology GPU Tools

- Implement smooth/relax compute preview.
- Implement proportional transform preview.
- Implement GPU normal/bounds refresh for position-only edits.
- Add no-readback source-contract tests for preview paths.

### Phase 4: Geometry Nodes Foundation

- Implement graph asset serialization.
- Implement typed sockets and link validation.
- Implement `GeometrySet` with mesh and instances first (curves, point clouds, volumes are stubs).
- Implement named and anonymous attributes.
- Implement the CPU reference evaluator using the IR-node `Field<T>` shape from Section 4.2.
- Add basic nodes: group input/output, transform geometry, join geometry, store named attribute, capture attribute, realize instances, mesh primitive, instance on points.

### Phase 5: Node Tools And Modifier Stack

- Add `ToolContext` graph inputs for active element, selection, viewport transform, and mouse/controller ray.
- Allow geometry node groups to run as editor tools.
- Add the first procedural node tools that reuse Phase 2 topology operators internally for any mutation of authored data.
- Implement the modifier stack on `EditableMeshAsset` (Section 23): `ModifierEntry`, `ModifierContract`, `EvaluatedGeometryCache`, bottom-up evaluation through the node-graph evaluator.
- Implement the stack-validation pass and reorder-warning UI policy (Section 23.4).
- Implement `Copy` and `Instance` stack sharing modes (Section 23.5). `Reference` (split-point with private tail) is targeted for Phase 5.1 or Phase 6 depending on stability.
- Implement collapse and per-modifier edit-at-position workflow (Section 23.8).
- Wire stack operations into the `ToolCommand`/`InverseDelta` undo and networking machinery (Section 23.11).

### Phase 6: Subdivision

- Add `ISubdivisionEvaluator`.
- Implement an internal baseline evaluator or simple CPU fallback.
- Wire crease/boundary settings (already in the document since Phase 1) into the evaluator.
- Evaluate OpenSubdiv as an optional backend (Section 3).
- If accepted, add it through the dependency process and preserve license/notice files.
- OpenSubdiv is **never** on the dependency-graph critical path; modeling shipping milestones do not gate on it.

### Phase 7: Runtime/Editor Integration

- Connect ImGui editor tools to modeling sessions.
- Add a minimal node graph inspector (full node editor is a later milestone).
- Add overlay rendering for preview buffers through the dedicated editor pipeline.
- Bake committed topology into `XRMesh`.
- Evaluate geometry node modifiers into renderable caches.
- Invalidate render caches and schedule meshlet/bounds refresh per Section 14.1.
- Add editor workflow docs and launch/task updates if needed.

## 21. Runtime Integration And In-Game Edit Mode

Modeling tools must work against meshes used by a running game session, not only against editor-only assets. The goal: any user can select a mesh in a running world (single-player or multi-user) and edit it in place, with results visible to gameplay systems (rendering, physics, networking) when they commit.

### 21.1 Two Mesh Tiers, One Asset

There are two mesh representations in the engine:

- **`XRMesh`** (runtime): triangulated, attribute-packed, GPU-friendly, possibly meshlet-baked. Owned by `GPUScene` while resident. No adjacency, no stable IDs, no loop/corner attributes, no undo.
- **`ModelingMeshDocument`** (authoring): BMesh-style radial-loop topology with n-gons, stable IDs, loop/corner attributes, undo journal, attribute layers.

These are not merged. Instead, an **`EditableMeshAsset`** wraps both:

```text
EditableMeshAsset
    ModelingMeshDocument   (authoritative when editing; cold otherwise)
    XRMesh                 (always present; the gameplay/render representation)
    BakeMetadata           (corner->vertex split table, triangulation map, ID remap)
    EditState              (None | Editing | Baking)
    NetworkIdentity        (see Section 22)
```

Non-editable meshes in a scene continue to be pure `XRMesh` and pay zero authoring overhead. Editable meshes carry the document but it sits cold (no derived caches, no GPU edit buffers) until edit mode is entered.

### 21.2 Promotion And Demotion

Three lifecycle paths exist:

**Path A: scene already has an `EditableMeshAsset`.** Entering edit mode is free — the document is already there. Activate edit session, build derived caches (BVH, GPU edit buffers, overlay buffers), lock the asset for the editor. The `XRMesh` keeps rendering normally for gameplay while previews overlay on top.

**Path B: scene has a pure `XRMesh` and a user requests edit.** This is the "promote" path. Run the **importer** that builds a `ModelingMeshDocument` from the `XRMesh`:

- Reconstruct adjacency from triangle indices.
- **Detect split vertices** (same position but different normal/UV/color) and merge them into a single document vertex with per-corner attribute splits. This is the most failure-prone step and has explicit tests.
- Reconstruct n-gons opportunistically only when the source asset carried n-gon metadata; otherwise keep triangles.
- Allocate stable IDs and a `BakeMetadata` table that records which document corner each original `XRMesh` vertex came from.

Promotion replaces the scene reference from `XRMesh` to `EditableMeshAsset`. Other systems holding raw `XRMesh` references receive a typed invalidation via the asset table. Promotion is undoable as a single asset-level operation.

**Path C: pure runtime mesh, no edit ever requested.** Stays as `XRMesh`. No cost.

Demotion (exit edit mode) does **not** discard the document by default. The document stays cold on the asset for future edit sessions and for network replication (Section 22). Explicit "Flatten to runtime" operation discards the document and reverts to a pure `XRMesh`.

### 21.3 Commit Pipeline During Play

When a tool commits inside a running game session:

```text
ToolCommit
    -> ModelingMeshDocument mutation + InverseDelta record
    -> MeshDirtyRegion published to render bridge (Section 14.1)
    -> Bake job scheduled on worker pool against committed snapshot
        -> Triangulate dirty n-gons
        -> Bake corner attributes into split vertices
        -> Recompute normals/tangents for dirty range
        -> Update XRMesh vertex/index slices (dirty ranges only)
        -> Update BakeMetadata corner->vertex mapping
    -> XRMesh publishes update generation
    -> GPUScene uploads dirty range (Section 14.1)
    -> Physics collider rebuild scheduled (Section 21.4)
    -> Network broadcast scheduled (Section 22)
```

Rules:

- Bakes run on the worker pool against an immutable document snapshot. Gameplay never blocks on bake.
- During the bake window, the previous `XRMesh` keeps rendering. The next frame after bake completes shows the new mesh.
- Position-only edits skip topology rebake and use the position-only fast path.
- Full retopology (loop cut, knife, bevel commit) does a topology rebake.
- Meshlet/BVH/LOD regeneration is async and may complete frames after the visual update; this is acceptable.

### 21.4 Gameplay Systems Coupling

Systems that consume `XRMesh` must declare their refresh policy when the mesh changes:

- **Rendering**: dirty-range upload, meshlet rebuild on topology change. Already covered by Section 14.1.
- **Static physics colliders** (mesh shapes): rebuild on commit. Triggered by `MeshDirtyRegion`. The new collider replaces the old at the next physics step boundary, not mid-step.
- **Dynamic/animated colliders** (cloth, soft body): generally not editable while simulating. Editing on a soft-body mesh requires pausing simulation, editing, then resuming with the new rest state. Documented as a tool precondition.
- **Skinned meshes**: the document stores skin weights as attribute layers (Section 17). Bind pose and bone IDs are preserved across edits. The skeleton is not editable through modeling tools.
- **Navmesh**: rebuilt by the navigation system on `MeshDirtyRegion`, async.
- **Lightmaps / GI**: marked dirty; rebake is user-triggered, not automatic.
- **References from gameplay scripts** (e.g. "spawn at vertex 42"): scripts that hold raw element references must consume the `RemapTable` published on stable-ID compaction (Section 8.2.1). Scripts that hold transform-space anchors are unaffected.

### 21.5 Edit Mode Semantics In A Running Session

Entering edit mode on a mesh in a live world does not pause the world. Edit mode is per-asset, not per-scene. Multiple meshes can be in edit mode concurrently (by different users or the same user).

While an asset is in `Editing` state:

- The asset accepts `ToolCommand` from the owning session.
- Gameplay reads of the `XRMesh` continue normally.
- Triggers and physics queries hit the last committed bake.
- Other clients (Section 22) see committed changes streamed in; they cannot enter `Editing` on the same asset unless the lock policy allows it.

Exiting edit mode flushes pending bakes, releases the GPU edit buffers and BVH, demotes the document to cold storage, and broadcasts an end-of-session message.

## 22. Networked Modeling (Multi-User)

Multi-user modeling in a shared world is in scope. The existing `ToolCommand` + `InverseDelta` design (Section 15) is the network unit. Previews are local-only and never replicate.

### 22.1 Authority Model

**Server-authoritative with command relay.** Matches the existing client/server architecture (`XREngine.Server`, dedicated server). Peer-to-peer concurrent editing (CRDT/OT) is out of scope for v1.

```text
Client (originator)
    Local preview (no network traffic)
    User releases tool -> ToolCommand sent to server
Server
    Validate against current document state and edit-session locks
    Allocate authoritative stable IDs
    Apply ToolCommand to server's document
    Compute ForwardDelta + ID assignments
    Broadcast (ForwardDelta, originatingClientId, sequenceNumber) to all clients
Client (originator)
    Reconcile local prediction with server delta (rebase or accept)
Other clients
    Apply ForwardDelta to local document
    Republish MeshDirtyRegion locally so their render/physics refresh
```

The originating client may apply the command locally before server confirmation (**local prediction**) to hide latency. If the server rejects (lock conflict, validation failure, version skew), the client rolls back via the local `InverseDelta` and surfaces the rejection to the user.

### 22.2 Stable IDs Across The Network

Two ID strategies are compatible; v1 uses the second:

- **Server-allocated IDs**: client sends a `ToolCommand` referencing existing IDs; server allocates any new IDs and returns them in the broadcast. Simple but adds a round-trip before the originator can reference new elements.
- **Composite IDs `(ClientId, LocalCounter)`**: each client allocates within its own ID namespace; the server validates uniqueness. The originator can chain tool commands referencing its own freshly-allocated IDs without waiting. This is the v1 choice.

`(ClientId, LocalCounter)` IDs survive replication unchanged. The generation counter still exists per-asset for stale-reference detection. Stable-ID compaction (Section 8.2.1) is a **server-only** operation in a networked session; clients receive the remap table.

### 22.3 Edit Session Locks

To prevent two users from bevelling the same edge at the same time, the server issues **soft locks per tool session**:

- When a client begins a tool that targets a selection, it requests a lock on the affected element IDs from the server.
- The server grants the lock with a TTL (e.g. 30 seconds, renewed by activity).
- Concurrent tool commands from other clients that touch locked elements are rejected.
- Selection-only operations (hover, marquee, navigation) do not require locks.
- Locks are released on tool commit, tool cancel, session timeout, or client disconnect.
- The lock state is part of the document network state and is replicated to all clients so the editor can show "Bob is editing this loop" indicators.

Lock granularity is per-element-ID. Large-selection tools take many locks; the server batches lock requests to avoid chatter.

### 22.4 Presence And Awareness

Each connected editor publishes a lightweight **`EditorPresence`** message at a low rate (e.g. 10 Hz):

- client ID and display name
- current tool (or none)
- current selection IDs (capped; large selections summarize to bounds)
- hover element ID
- viewport ray (for ghost cursor display)

Presence is separate from `ToolCommand` replication and never affects document state. Other clients render ghost cursors, colored selections, and tool-in-progress indicators from presence data. Presence is best-effort; dropped messages do not affect correctness.

**Previews are never networked.** Bob's live bevel preview stays on Bob's machine. Other clients see the bevel only after Bob commits and the server broadcasts the delta. This is a deliberate bandwidth choice; live shared previews would require streaming preview buffers per frame.

### 22.5 Bandwidth And Large Operations

Most tool commits are small: a few vertices, a few edges, a parameter struct. These delta messages are tens to hundreds of bytes and replicate fine.

Large operations have a fallback path:

- If `ForwardDelta` exceeds a threshold (e.g. 64 KiB or N elements), the server skips delta broadcast and instead pushes the operation as an **asset snapshot**: a new baked `XRMesh` slice plus the updated document region.
- Applying a node graph that regenerates the whole mesh always uses the snapshot path.
- Snapshot messages are versioned with the same `sequenceNumber` so clients reconcile correctly.

### 22.6 Late Joiners And Reconnects

A client joining a session mid-edit receives:

- The full `EditableMeshAsset` (document + last-committed `XRMesh` + `BakeMetadata`) for any asset currently in `Editing` state.
- The current lock table.
- The next `sequenceNumber` to expect.

Reconnects with a known last `sequenceNumber` receive only the missed deltas if the server retains them; otherwise they fall back to the full asset snapshot.

### 22.7 Dedicated Server Without Editors

A dedicated game server (no editors connected) still owns the authoritative document for any `EditableMeshAsset` that was promoted by a player or pre-authored as editable. If no client requests edit mode, the document sits cold on the server and only the `XRMesh` is used for gameplay queries. Network bandwidth for non-edited assets is zero.

### 22.8 Persistence

Committed changes are persisted by the server on its normal save cadence. The persisted asset includes the document, the baked `XRMesh`, and the `BakeMetadata`. On world load, editable assets are restored to `EditableMeshAsset` instances; non-editable assets load as pure `XRMesh`.

Per-session undo journals are not persisted across server restart by default. A "merged history" persistence option may be added later for collaborative authoring sessions where history matters; v1 does not commit to it.

### 22.9 Security

- Only clients granted the **modeling capability** for a given asset can send `ToolCommand` messages targeting it. Capability checks happen on the server before lock acquisition.
- Asset snapshots pushed from a client to the server (large-operation path) are size-capped and validated; an oversized or malformed snapshot is rejected, the client is notified, and the lock is released.
- Tool command rate-limiting per client is enforced server-side to prevent denial-of-service.

## 23. Modifier Stack

The modifier stack is a 3ds Max-style ordered list of non-destructive operations applied to a base mesh document. Each entry produces a `GeometrySet` from the previous entry's output. The stack lives on the `EditableMeshAsset` (Section 21.1).

### 23.1 Data Model

```text
ModifierStack
    BaseObjectRef        (-> ModelingMeshDocument)
    Entries: ModifierEntry[]
    EvaluatedCache: Dictionary<EntryIndex, GeometrySet>
    StackIdentity        (asset id for sharing; see 23.5)

ModifierEntry
    Type                  (subdivide, mirror, edit-poly, skin, lattice, node-graph, ...)
    Parameters            (typed struct, no boxed values)
    Enabled               (bool)
    GeometryNodeGraphRef? (modifiers are node graphs internally; see 23.2)
    Contract              (see 23.3)
    LocalSelectionFilter  (optional sub-object selection that scopes the modifier)
```

Evaluation order is bottom-up: `BaseObject -> Entry[0] -> Entry[1] -> ... -> Entry[N-1] -> EvaluatedGeometryCache -> Bake -> XRMesh`.

The `EvaluatedGeometryCache` stores per-entry output keyed by `(inputHash, parametersHash, contractHash)`. Re-evaluation skips entries whose key is unchanged.

### 23.2 Modifiers Are Node Graphs

A modifier is a thin wrapper around a `GeometryNodeGraph` (Section 4). This avoids two parallel procedural systems:

- Built-in modifiers (subdivide, mirror, decimate, weld, skin, lattice, FFD, etc.) ship as authored node graphs with curated parameter UIs.
- User node-tool modifiers are arbitrary node graphs exposed as stack entries.
- An "Edit Poly / Edit Mesh" modifier is special: it carries a recorded `ToolCommand` journal that re-applies authored edits onto the upstream output. Its parameters are the command list and the IDs they target.

This means the entire modifier stack is evaluated by the same CPU reference evaluator and the same caching machinery as the node graph system. There is no separate modifier evaluator.

### 23.3 Modifier Contracts (Reorder Safety)

Every modifier declares a `ModifierContract` describing its data dependencies. Contracts power the reorder-warning system.

```text
ModifierContract
    Reads: AttributeRequirement[]
        // e.g. RequiresUV(channel: 1), RequiresFaceIds, RequiresVertexCountStable,
        //      RequiresSubObjectSelection, RequiresMaterialSlots, RequiresSkinWeights
    Writes: AttributeEffect[]
        // e.g. ChangesTopology, ChangesVertexCount, GeneratesUV(channel: 2),
        //      ConsumesSelection, ReplacesNormals
    Preserves: AttributeLayer[]
        // layers guaranteed unchanged by this modifier
    Invalidates: DerivedCache[]
        // e.g. Lightmaps, Navmesh, PhysicsCollider
```

Contracts are static metadata on the modifier type; built-in modifiers declare them, and node-graph modifiers compute them automatically from the nodes they contain.

### 23.4 Reorder And Insert Validation

Reordering or inserting a modifier runs a **stack validation pass**:

```text
ValidateStack(newOrder):
    available = BaseObject.AvailableAttributes
    for entry in newOrder:
        missing = entry.Contract.Reads - available
        if missing:
            issues.Add(MissingDependency(entry, missing, suggested earlier producer))
        if entry.Contract.Writes contains ChangesTopology
           and any downstream entry requires VertexCountStable or stable element IDs:
            issues.Add(TopologyBreaksDownstream(entry, downstream))
        available = apply(available, entry.Contract)
```

Reorder UI behavior:

- **No issues**: silent, instant reorder.
- **Warnings only** (e.g. lightmap invalidation, recompute cost): inline warning badge, no modal prompt.
- **Data-loss issues** (e.g. sub-object selection no longer matches because upstream topology now changes after a selection-consuming modifier; UV channel removed by upstream): modal warning with specifics, options *Proceed*, *Cancel*, *Insert compatibility shim* where shim is a generated modifier that restores the missing attribute (e.g. re-project UVs, re-derive selection by tag).

The modal only appears when the validator confirms real data loss, not on every reorder. This matches the requested 3ds Max behavior.

Reorder is a single `ToolCommand` with an `InverseDelta` so it participates in undo and networking like any other operation.

### 23.5 Sharing: Instance And Reference

Three sharing modes, modeled on 3ds Max:

**Copy**: independent base document and independent stack. Default for duplication.

**Instance**: shared base document **and** shared stack. Multiple `EditableMeshAsset`s reference the same `ModelingMeshDocument` and the same `ModifierStackAsset`. Editing either propagates to all instances. Implemented by reference-counted asset handles.

**Reference**: shared base document and shared stack **up to a split point**, with each reference owning a private tail of additional modifiers. Editing below the split propagates; editing the private tail does not.

```text
ReferenceModifierStack
    SharedStackRef        (-> ModifierStackAsset)
    SplitIndex            (entries [0..SplitIndex) are shared; [SplitIndex..end) are private)
    PrivateTail: ModifierEntry[]
```

Operations:

- **Make Unique**: clone the shared stack (and optionally the base document) so this asset becomes a Copy. Breaks the sharing link.
- **Make Instance**: re-point this asset at another asset's stack and base. Discards any private content; warns if private content exists.
- **Make Reference**: re-point at a shared stack with a split point at the current top; new private modifiers added beyond the split.
- **Promote Private To Shared**: move private tail entries into the shared stack (affects all references). Requires that no other reference has a conflicting private tail at the same index, or warns.

The shared base document, if also shared, follows the same rules as Section 22 for editing authority — only one editor at a time per shared asset.

### 23.6 Stack Operations

Standard operations, each a typed `ToolCommand`:

- `AddModifier(index, type, parameters)`
- `RemoveModifier(index)` (with confirmation if downstream depends on it)
- `ReorderModifier(fromIndex, toIndex)` (see 23.4)
- `EnableModifier(index, bool)` (skipped during evaluation; cached output discarded)
- `EditModifierParameters(index, paramDelta)`
- `CollapseRange(fromIndex, toIndex)` (bake the range into the upstream `GeometrySet`, which for the bottom of the stack means baking into a new `ModelingMeshDocument` that becomes the base; entries in range are removed)
- `CollapseAll` (collapse the entire stack to a new editable base document; equivalent to 3ds Max "Collapse To")
- `MakeUnique`, `MakeInstance`, `MakeReference`, `PromotePrivateToShared` (see 23.5)

Collapse is a destructive operation. It produces a single `InverseDelta` that captures the pre-collapse stack + base for undo. After collapse, downstream consumers (selection sets, animation references) consume the post-collapse `RemapTable` (Section 8.2.1).

### 23.7 Sub-Object Selection And The Selection Pipeline

3ds Max's sub-object selection passing between modifiers is the most error-prone area in any modifier stack. The design:

- Sub-object selections flow as a named attribute (Section 17) on the appropriate domain (vertex/edge/face/corner).
- A modifier that consumes selection (e.g. "Bevel selected edges") reads the selection attribute on its input `GeometrySet`.
- A modifier whose contract includes `ChangesTopology` without `PreservesSelection` causes downstream selection-consuming modifiers to flag a reorder warning (Section 23.4).
- Selection identity uses stable IDs. Topology operations that create new elements assign new IDs; the selection attribute carries forward only IDs that still exist.
- For modifiers that need "select again after topology change," a `Tagged Selection` mode lets the user mark elements by a string tag; downstream modifiers re-resolve by tag instead of by ID.

### 23.8 Edit-At-Stack-Position Workflow

Clicking a modifier in the stack enters edit mode showing the **cached `GeometrySet` up to and including that modifier**. The workflow mirrors 3ds Max:

- The user sees and can pick the geometry as it exists at that stack position.
- Edits made at this position go into the **input of the next modifier** (typically via inserting or extending an "Edit Poly / Edit Mesh" modifier at the current position).
- Topology operators (Section 13) at this position record their `ToolCommand` into the modifier's command journal.
- Direct base-document editing requires selecting the bottom of the stack (or the editable base node explicitly).

The edit-mode session described in Section 21 takes a `stackPosition` parameter that selects which `GeometrySet` to expose.

### 23.9 Evaluation, Caching, And Performance

- Each entry's output `GeometrySet` is cached by content hash. Reordering or parameter changes invalidate only the affected entry and its downstream.
- Cache memory is capped per asset; LRU eviction is allowed because re-evaluation is deterministic.
- Shared `ModifierStackAsset`s share their cache across all instances.
- Background evaluation: when an upstream change invalidates downstream entries, evaluation runs on the worker pool (Section 14.2) against an immutable snapshot. The previous baked `XRMesh` keeps rendering until the new bake is ready.
- Per-modifier evaluation time is reported through the existing profiler categories so artists can identify expensive modifiers.

### 23.10 World-Space vs Object-Space

3ds Max separates Object-Space Modifiers (OSM) and World-Space Modifiers (WSM, always evaluated last). V1 ships **OSM only**. WSMs (gravity, screen-space deformers, path follow) are deferred; the design allows adding a `WorldSpace: true` flag on `ModifierEntry` later without a format break.

### 23.11 Networking

Modifier stack operations replicate via the same authority model as Section 22:

- Stack operations (`AddModifier`, `ReorderModifier`, parameter edits) are `ToolCommand`s and replicate as deltas.
- Shared `ModifierStackAsset`s have one authoritative server-side instance; all references receive deltas.
- Locks (Section 22.3) apply per `ModifierStackAsset`, not per asset, when the stack is shared. Two clients cannot reorder the same shared stack simultaneously; private tails on references can be edited independently.
- Collapse is a large operation and uses the asset-snapshot fallback path (Section 22.5).

### 23.12 Runtime Behavior

- A non-edited mesh in a running game session uses only its baked `XRMesh`. The stack is dormant; the `EvaluatedGeometryCache` is empty.
- Entering edit mode warms the cache by evaluating the stack.
- Gameplay code can hold references to a specific stack output (e.g. "give me the geometry at the output of modifier 3") only via an explicit subscription that triggers cache retention; otherwise caches are free to evict.
- A runtime-only mode (no editor) does not allocate stack evaluation buffers.

## 24. Risks

- Dynamic topology on GPU can become a correctness trap. Keep GPU topology previews disposable.
- Stable IDs and loops/corners are more work up front, but avoiding them causes tool and UV bugs later.
- Geometry nodes can sprawl quickly. Keep the first node set small and build the evaluator/attribute model before chasing node count.
- OpenSubdiv is a native C++ dependency; build, packaging, and optional GPU backend choices must be kept contained.
- GPU preview overflow must degrade gracefully instead of corrupting visuals or commits.
- Directly sharing editable modeling buffers with production render buffers could blur ownership; keep a clear bake/bridge boundary.
- **Promotion (`XRMesh` -> document) is lossy if split-vertex detection misclassifies attribute splits.** This is the highest-risk failure mode for in-game edit mode; it requires dedicated round-trip tests.
- **Networked stable-ID composite IDs increase ID width.** Document storage and serialization must accept this from Phase 1 or networking will require a format break.
- **Server-authoritative lock contention** can frustrate users on slow links. Mitigate with optimistic local prediction and clear lock-conflict UI.

- **Modifier stack reorder validation has many edge cases.** Sub-object selection invalidation under topology changes is the classic 3ds Max footgun; the contract system (Section 23.3) and the tagged-selection mode (Section 23.7) must be tested with golden-hash fixtures before stack reorder ships.
- **Shared stacks (Instance / Reference) create cross-asset coupling** that complicates undo, networking, and asset loading order. Phase ordering must land Copy semantics first and Instance/Reference after the basic stack is stable.

## 25. Resolved Open Questions

The following decisions are made and are normative for Phase 1+:

- **N-gons in v1**: yes. The authoring document stores n-gons natively; triangulation happens only at bake/export. Internally triangulating would defeat the loop/corner attribute story and produce UV/normal bugs.
- **First-shipping geometry components**: mesh + instances only. Curve, point cloud, and volume/SDF components exist as `GeometrySet` slots but ship as stubs; nodes that need them pass them through unchanged until a later milestone implements them.
- **MCP exposure**: high-level tool commands first (idempotent, named, versioned, defined by the `ToolCommand` shape in Section 15). Raw topology operators are exposed only behind an explicit `unsafe` opt-in flag.
- **Preview rendering**: dedicated editor overlay pipeline (Section 11.1). The production material table is not used for transient tool geometry.
- **Geometry node assets**: ship as editor-only documents until the evaluator stabilizes in Phase 4; promote to engine assets under `Assets/` in Phase 7.
- **First geometry node editor**: ImGui inspector-driven graph editor in Phase 7. A full native node editor is a later milestone.
- **Node tools writing to authored mesh in v1**: yes, but only by invoking the same Phase 2 topology operators internally. Direct attribute scribbling on the authored document from a node is rejected.

- **Modifier stack v1 scope**: ship in Phase 5 (after node graphs land in Phase 4). Stack entries are node-graph wrappers, not a parallel modifier system. World-Space Modifiers are deferred.
- **Stack sharing v1**: Copy and Instance ship together; Reference (split-point with private tail) ships one milestone later. The data model accommodates Reference from Phase 5 so it is not a format break.
- **Reorder warning policy**: silent on safe reorders, inline badge on warnings, modal only on confirmed data loss. See Section 23.4.

## 26. Remaining Open Questions

- Should OpenSubdiv be vendored as a submodule under `Build/Submodules`, or consumed through a prebuilt native dependency flow? (Defer to Phase 6.)
- Should the first OpenSubdiv backend use CPU evaluation only, or should OpenGL evaluation be part of the first dependency milestone? (Defer to Phase 6.)
- Should in-game edit mode require an explicit per-asset "editable" flag set at authoring time, or should any `XRMesh` be promotable on demand? Default proposal: promotable on demand, with an optional flag to forbid promotion for assets that must never be edited at runtime (e.g. networked competitive game geometry).
- Should the networked authority model support a peer-to-peer (non-server) mode for small co-op sessions, or remain strictly server-authoritative? (Defer; server-authoritative is sufficient for v1.)
- Should presence ghost cursors render through the editor overlay pipeline or a dedicated multi-user overlay layer? (Defer to Phase 7.)
- Should the modifier stack expose a per-modifier "render this stack position" mode (3ds Max "Show End Result" off), and if so, should it be a per-viewport or per-asset setting?
- Should built-in modifiers' authored node graphs be user-editable (i.e. can an artist open the "Subdivide" modifier as a node graph and tweak it), or are they sealed implementations?
