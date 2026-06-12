# GPU-Accelerated Modeling Roadmap TODO

Last Updated: 2026-05-19
Owner: Modeling
Status: Planned parent roadmap for GPU-accelerated modeling, geometry nodes, subdivision, and editor integration.
Target Branch: `modeling-gpu-accelerated-tools`

Source design:

- [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md)

Child trackers:

- [Modeling Topology Foundation TODO](01-modeling-topology-foundation-todo.md)
- [Geometry Nodes Foundation TODO](02-geometry-nodes-foundation-todo.md)
- [Core Modeling Tools TODO](03-core-modeling-tools-todo.md)
- [GPU Modeling Preview TODO](04-gpu-modeling-preview-todo.md)
- [Subdivision And OpenSubdiv TODO](05-subdivision-opensubdiv-todo.md)
- [Modeling Editor And Runtime Integration TODO](06-modeling-editor-runtime-integration-todo.md)

Related docs:

- [Mesh submission strategies](../../../architecture/rendering/mesh-submission-strategies.md)
- [MCP server](../../../developer-guides/ai/mcp-server.md)
- [GPU meshlet zero-readback rendering design](../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)
- [Model import binary cache design](../../design/assets/model-import-binary-cache-design.md)

## Parent Roadmap Contract

This parent roadmap owns canonical ordering, branch scope, acceptance criteria, and cross-child status. Child trackers own implementation checklists and validation detail for their feature areas.

Keep this table in sync when changing scope:

| Roadmap phase | Child tracker | Notes |
| --- | --- | --- |
| Phase 0: branch and baselines | This file | Dedicated branch, source audit, local baseline, split confirmation. |
| Phase 1: topology foundation | [Modeling Topology Foundation TODO](01-modeling-topology-foundation-todo.md) | Stable IDs, loops/corners, attributes, undo, validation. |
| Phase 2: geometry nodes foundation | [Geometry Nodes Foundation TODO](02-geometry-nodes-foundation-todo.md) | `GeometrySet`, sockets, fields, attributes, graph assets, CPU evaluator. |
| Phase 3: core modeling tools | [Core Modeling Tools TODO](03-core-modeling-tools-todo.md) | Add/remove/connect, split, knife, loop cut, bevel, bridge, extrude, inset. |
| Phase 4: GPU preview layer | [GPU Modeling Preview TODO](04-gpu-modeling-preview-todo.md) | Compute picking, selection masks, live previews, smoothing, no-readback contracts. |
| Phase 5: subdivision | [Subdivision And OpenSubdiv TODO](05-subdivision-opensubdiv-todo.md) | `ISubdivisionEvaluator`, CPU fallback, optional OpenSubdiv integration. |
| Phase 6: editor/runtime integration | [Modeling Editor And Runtime Integration TODO](06-modeling-editor-runtime-integration-todo.md) | ImGui workflows, overlays, bake to `XRMesh`, cache invalidation, MCP. |
| Phase 7: validation and merge | This file plus all child trackers | Cross-feature validation, docs, branch merge. |

## Goal

Build a node-first but CPU-authoritative modeling system for XRENGINE:

- Direct edit tools commit deterministic topology mutations to `XREngine.Modeling`.
- Geometry node graphs generate non-destructive derived geometry and reusable node tools.
- GPU compute accelerates preview, picking, selection, smoothing, and stable-topology evaluation without becoming the source of truth.
- Subdivision is exposed behind an evaluator interface, with OpenSubdiv considered as an optional backend after dependency approval.
- Editor/runtime integration bakes committed/evaluated output into `XRMesh`, refreshes render caches, and preserves zero-readback render-path ownership boundaries.

## Non-Negotiable Rules

- [ ] Create a dedicated branch for this TODO set, for example `modeling-gpu-accelerated-tools`.
- [ ] Keep the CPU modeling document authoritative for committed topology, stable IDs, undo, validation, serialization, and export.
- [ ] Keep GPU preview output disposable and never required for deterministic commit correctness.
- [ ] Do not require synchronous GPU readback in per-frame hover, picking, preview, or draw paths.
- [ ] Keep `XREngine.Modeling` renderer-independent except for explicit bridge/integration assemblies.
- [ ] Preserve loop/corner attributes for UVs, normals, tangents, colors, and face-varying data.
- [ ] Geometry node evaluation is side-effect-free until an explicit apply/bake operation.
- [ ] New code must compile without new warnings.
- [ ] Use `SetField(...)` when mutating any touched `XRBase`-derived type.
- [ ] Hot preview/update/render paths must avoid heap allocations after warmup.
- [ ] Stop for approval before adding OpenSubdiv, submodules, native binaries, or dependency upgrades.
- [ ] Update docs when user-facing workflows, launch flags, editor tasks, or MCP tools change.

## Success Criteria

- [ ] `XREngine.Modeling` has stable-ID editable topology with vertices, edges, loops/corners, faces, attributes, selection, dirty tracking, undo, and validation.
- [ ] Core direct modeling tools are deterministic and covered by unit tests.
- [ ] Geometry node graphs can serialize, validate, evaluate on CPU, pass unsupported geometry components through, and produce deterministic attributes.
- [ ] GPU preview paths provide picking/selection and live tool previews without steady-state readback stalls.
- [ ] Subdivision has an evaluator interface and CPU fallback before any optional OpenSubdiv backend is introduced.
- [ ] Editor integration supports usable ImGui workflows, preview overlays, bake/apply, and render cache invalidation.
- [ ] `XRMesh`, acceleration caches, meshlet refresh, and `GPUScene` updates happen only through explicit bake/bridge paths.
- [ ] Targeted modeling tests and narrow builds pass.

## Primary Code Areas

- `XREngine.Modeling/`
- `XREngine.Runtime.ModelingBridge/`
- `XREngine.Editor/`
- `XREngine.Runtime.Rendering/Objects/Meshes/`
- `XREngine.Runtime.Rendering/Rendering/API/Rendering/`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/`
- `Build/CommonAssets/Shaders/`
- `XREngine.UnitTests/Modeling/`
- `XREngine.UnitTests/Rendering/`
- `docs/work/design/modeling/gpu-accelerated-modeling-tools-design.md`

## Phase 0: Branch, Baseline, And Scope Split

**Goal:** isolate the modeling work, capture current local scaffolding, and confirm child tracker ownership before implementation.

### Tasks

- [ ] Create the dedicated branch `modeling-gpu-accelerated-tools`.
- [ ] Confirm current `XREngine.Modeling` public APIs and tests:
  - [ ] `EditableMesh`
  - [ ] `HalfEdgeTopology`
  - [ ] `ModelingMeshDocument`
  - [ ] `EditableMeshTopologyOperatorTests`
- [ ] Record current limitations: triangle-list orientation, no stable IDs, limited loose element support, no loop/corner attribute model, no node graph evaluator.
- [ ] Confirm child tracker boundaries and avoid duplicate task ownership.
- [ ] Identify narrow validation commands for each child tracker.
- [ ] Decide whether geometry node assets begin as runtime assets or editor-only assets.
- [ ] Decide whether the first node editor is ImGui, native UI, or inspector-only.

### Exit Criteria

- [ ] Branch exists.
- [ ] Baseline scaffolding and limitations are documented.
- [ ] Every child tracker has an owner scope, exit criteria, and validation section.

## Phase 1: Topology Foundation

**Goal:** make the modeling document suitable for direct edit tools and geometry node bake/apply.

- [ ] Complete [Modeling Topology Foundation TODO](01-modeling-topology-foundation-todo.md).

### Exit Criteria

- [ ] Stable IDs, loops/corners, attributes, undo, validation, and bake/export hooks are implemented and tested.

## Phase 2: Geometry Nodes Foundation

**Goal:** introduce the procedural graph layer with deterministic CPU evaluation before GPU acceleration.

- [ ] Complete [Geometry Nodes Foundation TODO](02-geometry-nodes-foundation-todo.md).

### Exit Criteria

- [ ] `GeometrySet`, fields, attributes, graph assets, graph validation, and the first CPU evaluator are implemented and tested.

## Phase 3: Core Modeling Tools

**Goal:** implement deterministic CPU commits for the foundational tool set.

- [ ] Complete [Core Modeling Tools TODO](03-core-modeling-tools-todo.md).

### Exit Criteria

- [ ] Add/remove/connect/delete/split/knife/loop cut/bevel/bridge/extrude/inset work on the topology foundation with tests.

## Phase 4: GPU Modeling Preview

**Goal:** make interaction feel immediate without giving GPU preview buffers topology authority.

- [ ] Complete [GPU Modeling Preview TODO](04-gpu-modeling-preview-todo.md).

### Exit Criteria

- [ ] Picking, selection, live previews, smoothing/relax, overflow diagnostics, and no-readback source contracts are in place.

## Phase 5: Subdivision And OpenSubdiv Evaluation

**Goal:** add subdivision through an interface, then evaluate optional OpenSubdiv integration with dependency approval.

- [ ] Complete [Subdivision And OpenSubdiv TODO](05-subdivision-opensubdiv-todo.md).

### Exit Criteria

- [ ] Subdivision can run through an evaluator interface with a CPU fallback. OpenSubdiv is either explicitly integrated through the dependency process or deferred with a written reason.

## Phase 6: Editor And Runtime Integration

**Goal:** expose the system in the editor and bridge committed output into render/runtime systems.

- [ ] Complete [Modeling Editor And Runtime Integration TODO](06-modeling-editor-runtime-integration-todo.md).

### Exit Criteria

- [ ] ImGui workflows, preview overlays, bake/apply, render cache invalidation, and runtime bridge behavior are validated.

## Phase 7: Consolidated Validation And Merge

**Goal:** prove the feature set works together and close the branch cleanly.

### Tasks

- [ ] Run all child tracker validation commands.
- [ ] Run the consolidated modeling test suite.
- [ ] Run narrow rendering/modeling bridge builds.
- [ ] Confirm no new compiler warnings.
- [ ] Confirm docs describe user-facing workflows and dependency posture.
- [ ] Review dependency/license outputs if OpenSubdiv or any other dependency was added.
- [ ] Update this roadmap and all child trackers with final validation results.
- [ ] Merge `modeling-gpu-accelerated-tools` back into `main` after implementation and validation.

### Exit Criteria

- [ ] All child trackers are complete or explicitly deferred.
- [ ] Validation results are recorded.
- [ ] Branch is merged back into `main`.

## Validation

Baseline commands:

```powershell
dotnet build .\XREngine.Modeling\XREngine.Modeling.csproj
dotnet build .\XREngine.Runtime.ModelingBridge\XREngine.Runtime.ModelingBridge.csproj
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Modeling
```

Broader validation will be owned by each child tracker.
