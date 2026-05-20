# Core Modeling Tools TODO

Last Updated: 2026-05-19
Owner: Modeling
Status: Planned child tracker for [GPU-Accelerated Modeling Roadmap](00-gpu-accelerated-modeling-roadmap.md) Phase 3
Target Branch: `modeling-gpu-accelerated-tools`

Source design:

- [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md)

Related docs:

- [GPU-Accelerated Modeling Roadmap TODO](00-gpu-accelerated-modeling-roadmap.md)
- [Modeling Topology Foundation TODO](01-modeling-topology-foundation-todo.md)
- [GPU Modeling Preview TODO](04-gpu-modeling-preview-todo.md)

## Parent Roadmap Contract

This tracker owns deterministic CPU commits for foundational modeling tools. GPU previews may make these tools feel live, but commit correctness must be testable without rendering.

## Goal

Implement direct modeling operations that mutate the authoritative topology document: add/remove, connect, delete/dissolve, split, knife, loop cut, bevel, bridge, extrude, inset, transform, and smooth/relax commit.

## Non-Negotiable Rules

- [ ] Tool commits operate on the CPU modeling document.
- [ ] Tool previews are optional and disposable.
- [ ] Every topology-changing tool records undo data.
- [ ] Every topology-changing tool defines attribute interpolation/copy policy.
- [ ] Every tool updates dirty regions and derived cache invalidation.
- [ ] Unit tests cover deterministic results, invalid input, and validation reports.
- [ ] Tool code does not depend on editor UI.

## Success Criteria

- [ ] Direct tools work through an `IModelingTool` or equivalent session/operation model.
- [ ] Add/remove/connect/delete/split/knife/loop cut/bevel/bridge/extrude/inset are implemented with tests.
- [ ] Smooth/relax has a CPU commit path that matches the future GPU preview semantics.
- [ ] Attribute interpolation is deterministic for positions, corner data, material slots, and skin weights where applicable.
- [ ] Invalid operations fail with clear diagnostics instead of corrupting topology.

## Primary Code Areas

- `XREngine.Modeling/EditableMesh.cs`
- `XREngine.Modeling/HalfEdgeTopology.cs`
- `XREngine.Modeling/ModelingOperationOptions.cs`
- `XREngine.Modeling/ModelingMeshValidation.cs`
- Future namespace candidates:
  - `XREngine.Modeling/Tools/`
  - `XREngine.Modeling/Operations/`
- `XREngine.UnitTests/Modeling/`

## Phase 0: Tool Framework

**Goal:** create a renderer-independent tool/session layer.

### Tasks

- [ ] Define `IModelingTool` or equivalent operation/session interface.
- [ ] Define tool begin/update/commit/cancel lifecycle.
- [ ] Define tool input context independent from editor UI.
- [ ] Define tool diagnostics and failure results.
- [ ] Define selection mode requirements.
- [ ] Define dirty-region reporting.
- [ ] Define undo record creation.
- [ ] Add tests for tool lifecycle and cancellation.

### Exit Criteria

- [ ] Tools can be invoked in unit tests without editor/runtime dependencies.

## Phase 1: Basic Element Operations

**Goal:** cover low-level operations needed by all higher tools.

### Tasks

- [ ] Add vertex.
- [ ] Remove vertex.
- [ ] Move vertex.
- [ ] Add loose edge.
- [ ] Remove edge.
- [ ] Delete face.
- [ ] Dissolve vertex.
- [ ] Dissolve edge.
- [ ] Fill face from boundary loop.
- [ ] Merge vertices.
- [ ] Collapse edge.
- [ ] Add tests for loose, boundary, manifold, and non-manifold cases.

### Exit Criteria

- [ ] Basic element edits preserve topology validity or report expected warnings.

## Phase 2: Connect And Split

**Goal:** implement edge creation and subdivision across existing topology.

### Tasks

- [ ] Connect two loose vertices.
- [ ] Connect vertices within one face and split the face.
- [ ] Connect vertices across compatible boundary loops.
- [ ] Reject or diagnose ambiguous non-manifold connect operations.
- [ ] Split edge at parameter `t`.
- [ ] Split multiple selected edges.
- [ ] Split face by path.
- [ ] Interpolate corner/point attributes on split.
- [ ] Add tests for triangles, quads, n-gons, boundaries, and attribute interpolation.

### Exit Criteria

- [ ] Connect/split operations work across target face types and preserve attributes by policy.

## Phase 3: Knife And Loop Cut

**Goal:** add cutting tools that create vertices, edges, and faces in one committed operation.

### Tasks

- [ ] Define knife cut input path representation.
- [ ] Compute CPU intersections against selected faces and edges.
- [ ] Insert vertices at edge/face intersection parameters.
- [ ] Split affected edges.
- [ ] Split affected faces.
- [ ] Preserve path order and attribute interpolation.
- [ ] Implement loop/ring traversal using half-edge/loop topology.
- [ ] Implement loop cut with one cut.
- [ ] Implement loop cut with multiple cuts.
- [ ] Implement loop cut slide factor.
- [ ] Add tests for boundaries, poles, triangles, quads, n-gons, and interrupted loops.

### Exit Criteria

- [ ] Knife and loop cut produce deterministic topology and clear diagnostics for unsupported paths.

## Phase 4: Bevel, Bridge, Extrude, And Inset

**Goal:** implement common face/edge modeling tools.

### Tasks

- [ ] Implement bevel selected edges.
- [ ] Implement bevel selected vertices if included in v1 scope.
- [ ] Support bevel amount.
- [ ] Support bevel segment count.
- [ ] Support bevel profile.
- [ ] Support bevel miter policy.
- [ ] Implement bridge edges.
- [ ] Implement bridge loops.
- [ ] Support bridge twist offset and segment count.
- [ ] Implement extrude faces.
- [ ] Implement extrude edges/boundaries.
- [ ] Implement inset faces.
- [ ] Preserve material slots and attributes by policy.
- [ ] Add tests for boundary and interior cases.

### Exit Criteria

- [ ] Bevel, bridge, extrude, and inset keep topology valid for target v1 cases.

## Phase 5: Transform, Smooth, Relax, And Cleanup

**Goal:** add stable-topology operations that can later use GPU preview.

### Tasks

- [ ] Transform selected vertices.
- [ ] Implement proportional editing falloff on CPU.
- [ ] Implement smooth selected vertices.
- [ ] Implement relax selected vertices.
- [ ] Implement merge by distance.
- [ ] Implement remove loose geometry.
- [ ] Implement triangulate.
- [ ] Implement quadrangulate if included in v1 scope.
- [ ] Add tests for topology-preserving operations.

### Exit Criteria

- [ ] Stable-topology operations change positions/selection/attributes without unintended topology mutation.

## Phase 6: Tool Validation Matrix

**Goal:** protect the tool set from regressions.

### Tasks

- [ ] Add source-level tests for tool ownership boundaries.
- [ ] Add topology validation after every tool test.
- [ ] Add undo/redo tests for every topology tool.
- [ ] Add attribute interpolation tests.
- [ ] Add invalid input diagnostics tests.
- [ ] Add simple bake/export tests after tool sequences.

### Exit Criteria

- [ ] Tool tests are deterministic and can run without a renderer.

## Validation

```powershell
dotnet build .\XREngine.Modeling\XREngine.Modeling.csproj
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Modeling
```
