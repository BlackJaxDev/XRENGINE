# Geometry Nodes Foundation TODO

Last Updated: 2026-05-19
Owner: Modeling
Status: Planned child tracker for [GPU-Accelerated Modeling Roadmap](00-gpu-accelerated-modeling-roadmap.md) Phase 2
Target Branch: `modeling-gpu-accelerated-tools`

Source design:

- [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md)

Related docs:

- [GPU-Accelerated Modeling Roadmap TODO](00-gpu-accelerated-modeling-roadmap.md)
- [Modeling Topology Foundation TODO](01-modeling-topology-foundation-todo.md)
- [GPU Modeling Preview TODO](04-gpu-modeling-preview-todo.md)

## Parent Roadmap Contract

This tracker owns the procedural geometry node foundation: graph assets, sockets, `GeometrySet`, fields, attributes, graph validation, and CPU reference evaluation. GPU kernels and editor UI build on this foundation but do not define graph semantics.

## Goal

Create a Blender-inspired geometry node layer that can procedurally generate, transform, instance, and annotate geometry while preserving deterministic CPU evaluation and clear bake/apply boundaries.

## Non-Negotiable Rules

- [ ] A single geometry socket carries `GeometrySet` values.
- [ ] Unsupported geometry components pass through unchanged unless a node explicitly consumes or realizes them.
- [ ] CPU reference evaluation comes before GPU acceleration.
- [ ] Node evaluation is side-effect-free until explicit apply/bake.
- [ ] Named attributes are persistent; anonymous attributes are temporary unless captured/stored.
- [ ] Fields evaluate per domain and must be deterministic.
- [ ] Graph assets are versioned and serializable.
- [ ] Node groups are reusable across objects.

## Success Criteria

- [ ] `GeometrySet` supports mesh and instances in the first milestone, with extension points for curves, point clouds, SDF/volume, and material bindings.
- [ ] Typed sockets and link validation reject invalid graphs before execution.
- [ ] Fields and attributes can evaluate on point, edge, corner, face, object, and instance domains as applicable.
- [ ] A CPU evaluator can execute the first node set deterministically.
- [ ] Node groups can expose typed inputs/outputs and be reused.
- [ ] Graph outputs can preview, render through a cache, or bake/apply into authored topology.

## Primary Code Areas

- `XREngine.Modeling/`
- `XREngine.Modeling/ModelingMeshDocument.cs`
- `XREngine.UnitTests/Modeling/`
- Future namespace candidates:
  - `XREngine.Modeling/GeometryNodes/`
  - `XREngine.Modeling/Geometry/`
  - `XREngine.Modeling/Attributes/`

## Phase 0: Graph Asset Shape

**Goal:** define the persistent asset model before adding evaluation complexity.

### Tasks

- [ ] Define `GeometryNodeGraphAsset`.
- [ ] Define node IDs, socket IDs, link IDs, and group interface IDs.
- [ ] Define graph version and compatibility flags.
- [ ] Define exposed parameter metadata.
- [ ] Define generated kernel cache key placeholders.
- [ ] Add serialization tests.
- [ ] Add invalid graph loading tests.

### Exit Criteria

- [ ] Graph assets can round-trip through serialization.
- [ ] Missing/unknown nodes and sockets produce clear diagnostics.

## Phase 1: Sockets And GeometrySet

**Goal:** establish the core value model.

### Tasks

- [ ] Define socket value types:
  - [ ] geometry
  - [ ] bool
  - [ ] int/uint
  - [ ] float
  - [ ] vector2/vector3/vector4
  - [ ] matrix
  - [ ] string/name
  - [ ] material reference
  - [ ] object/asset reference
  - [ ] field values
- [ ] Implement link validation.
- [ ] Implement `GeometrySet`.
- [ ] Implement `MeshComponent`.
- [ ] Implement `InstancesComponent`.
- [ ] Add placeholders for curve, point cloud, and volume/SDF components.
- [ ] Implement pass-through semantics for unsupported components.
- [ ] Add tests for geometry socket pass-through and component ownership.

### Exit Criteria

- [ ] Nodes can receive and return `GeometrySet` without discarding unknown components.
- [ ] Invalid links are rejected before evaluation.

## Phase 2: Attributes And Fields

**Goal:** make procedural data flow agree with the topology attribute model.

### Tasks

- [ ] Share attribute domains with topology foundation.
- [ ] Implement named attribute lookup and storage.
- [ ] Implement anonymous attribute handles and lifetime rules.
- [ ] Define `Field<T>` and evaluation context.
- [ ] Support field evaluation on:
  - [ ] object domain
  - [ ] instance domain
  - [ ] point/vertex domain
  - [ ] edge domain
  - [ ] corner/loop domain
  - [ ] face domain
- [ ] Implement domain adaptation or explicit diagnostics for unsupported domain conversions.
- [ ] Add tests for field determinism and attribute lifetime.

### Exit Criteria

- [ ] Fields produce deterministic per-element values.
- [ ] Anonymous attributes do not leak into authored topology unless captured/stored.

## Phase 3: Graph Validation And Execution IR

**Goal:** compile node graphs into a deterministic execution plan.

### Tasks

- [ ] Build dependency DAG from links.
- [ ] Detect cycles unless a future node explicitly supports stateful feedback.
- [ ] Validate required inputs and defaults.
- [ ] Validate node group interfaces.
- [ ] Produce execution IR.
- [ ] Add structural hashes for graph, node parameters, and relevant input geometry dependencies.
- [ ] Add output cache invalidation policy.
- [ ] Add diagnostics for type, domain, missing asset, and unsupported component errors.

### Exit Criteria

- [ ] Valid graphs compile to IR.
- [ ] Invalid graphs fail before evaluation with actionable errors.

## Phase 4: CPU Reference Evaluator

**Goal:** provide deterministic node semantics before optimizing.

### Tasks

- [ ] Implement evaluation context.
- [ ] Implement graph input/output nodes.
- [ ] Implement group input/output.
- [ ] Implement transform geometry.
- [ ] Implement join geometry.
- [ ] Implement store named attribute.
- [ ] Implement capture attribute.
- [ ] Implement realize instances.
- [ ] Implement basic mesh primitive nodes.
- [ ] Implement instance on points.
- [ ] Add cache reuse for unchanged inputs.
- [ ] Add tests for every initial node.

### Exit Criteria

- [ ] Initial node set evaluates deterministically on CPU.
- [ ] Cache invalidation responds to graph edits, parameter edits, and input geometry changes.

## Phase 5: Bake, Apply, And Node Tools Boundary

**Goal:** define how graph output becomes authored topology or editor tool output.

### Tasks

- [ ] Implement preview output path.
- [ ] Implement render/evaluated cache output path.
- [ ] Implement apply/bake to `ModelingMeshDocument`.
- [ ] Allocate stable IDs when baking.
- [ ] Preserve requested named attributes when baking.
- [ ] Drop anonymous attributes unless captured/stored.
- [ ] Create one undo record for apply/bake.
- [ ] Define `ToolContext` graph inputs:
  - [ ] active object
  - [ ] active vertex/edge/face
  - [ ] selection masks
  - [ ] mouse/controller ray
  - [ ] viewport transform
  - [ ] modifier state
  - [ ] numeric parameters
- [ ] Add tests for bake/apply and tool-context binding.

### Exit Criteria

- [ ] Graph output can remain disposable, renderable, or be applied into authored topology.
- [ ] Node tool context is defined even if full editor UI lands later.

## Validation

```powershell
dotnet build .\XREngine.Modeling\XREngine.Modeling.csproj
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Modeling
```
