# Modeling Editor And Runtime Integration TODO

Last Updated: 2026-05-19
Owner: Modeling/Editor/Rendering
Status: Planned child tracker for [GPU-Accelerated Modeling Roadmap](00-gpu-accelerated-modeling-roadmap.md) Phase 6
Target Branch: `modeling-gpu-accelerated-tools`

Source design:

- [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md)

Related docs:

- [GPU-Accelerated Modeling Roadmap TODO](00-gpu-accelerated-modeling-roadmap.md)
- [Core Modeling Tools TODO](03-core-modeling-tools-todo.md)
- [Geometry Nodes Foundation TODO](02-geometry-nodes-foundation-todo.md)
- [GPU Modeling Preview TODO](04-gpu-modeling-preview-todo.md)
- [MCP server](../../../features/mcp-server.md)

## Parent Roadmap Contract

This tracker owns editor workflow, runtime bridge, overlay rendering, bake/apply, `XRMesh` conversion, cache invalidation, and MCP/tool exposure. It depends on the topology, tools, geometry nodes, GPU preview, and subdivision trackers for core semantics.

## Goal

Expose the modeling system through the ImGui editor path first, bridge committed/evaluated output into runtime render data, and keep editable modeling state separate from production `GPUScene` ownership.

## Non-Negotiable Rules

- [ ] Default editor integration targets ImGui, not native UI, unless a task explicitly targets native UI.
- [ ] Editor UI invokes renderer-independent tool/session APIs.
- [ ] Modeling preview buffers are editor overlays or transient evaluated output, not production `GPUScene` ownership.
- [ ] Bake/apply explicitly converts authored/evaluated data into `XRMesh`.
- [ ] `XRMesh` acceleration caches are cleared after committed topology edits.
- [ ] Meshlet/bounds/normal/tangent refresh is scheduled explicitly after bake/apply.
- [ ] MCP tools, if added or renamed, require docs regeneration.
- [ ] User-facing workflows and launch/task changes require docs updates.

## Success Criteria

- [ ] Users can enter an ImGui modeling mode and use core tools on selected mesh objects.
- [ ] Selection, hover, transform, and preview overlays are visible and responsive.
- [ ] Geometry node graphs can be inspected/edited at least through a minimal workflow.
- [ ] Apply/bake creates or updates `XRMesh` output and invalidates caches.
- [ ] Runtime bridge preserves material slots and attributes required by rendering.
- [ ] `GPUScene` is refreshed through the normal render data path after bake/apply.
- [ ] Optional MCP operations expose safe modeling workflows if included in scope.

## Primary Code Areas

- `XREngine.Editor/`
- `XREngine.Editor/MeshEditingPawnComponent.cs`
- `XREngine.Editor/AssetEditors/`
- `XREngine.Runtime.ModelingBridge/`
- `XREngine.Runtime.Rendering/Objects/Meshes/`
- `XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/`
- `XREngine.Runtime.Rendering/Rendering/Pipelines/`
- `docs/features/mcp-server.md`
- `XREngine.UnitTests/Modeling/`
- `XREngine.UnitTests/Rendering/`

## Phase 0: Editor Workflow Shape

**Goal:** define the user-facing modeling workflow before wiring tools.

### Tasks

- [ ] Define how users enter/exit modeling mode.
- [ ] Define object/edit/geometry-node modes.
- [ ] Define selection modes: vertex, edge, face, object, instance.
- [ ] Define active tool state and tool option storage.
- [ ] Define undo/redo interaction with existing editor undo if present.
- [ ] Define how committed edits update assets/scenes.
- [ ] Document the first supported workflow.

### Exit Criteria

- [ ] Workflow shape is clear enough to implement without UI churn.

## Phase 1: ImGui Tool Shell

**Goal:** expose direct modeling tools through the current day-to-day editor UI.

### Tasks

- [ ] Add ImGui modeling mode panel.
- [ ] Add selection mode controls.
- [ ] Add active tool controls.
- [ ] Add tool option controls.
- [ ] Add commit/cancel behavior.
- [ ] Add topology validation display.
- [ ] Add dirty/cache status display.
- [ ] Add undo/redo commands.
- [ ] Keep text compact and tool-focused.

### Exit Criteria

- [ ] Core tools can be invoked from the ImGui editor path.

## Phase 2: Overlays And GPU Preview Display

**Goal:** make edit state and GPU previews visible in viewports.

### Tasks

- [ ] Render vertex markers.
- [ ] Render edge overlays.
- [ ] Render face selection overlays.
- [ ] Render hover highlights.
- [ ] Render cut/loop/bevel/bridge previews.
- [ ] Render proportional editing falloff if included.
- [ ] Display preview overflow/fallback diagnostics.
- [ ] Ensure overlays do not mutate production mesh state.

### Exit Criteria

- [ ] Users can visually understand selection, hover, and active tool preview state.

## Phase 3: Geometry Node UI

**Goal:** provide a minimal usable workflow for geometry node graph assets.

### Tasks

- [ ] Decide first UI shape: graph editor, inspector-driven editor, or asset inspector.
- [ ] List available node types.
- [ ] Create graph assets.
- [ ] Edit graph parameters.
- [ ] Connect node links if graph editor exists in this phase.
- [ ] Display graph validation errors.
- [ ] Preview evaluated graph output.
- [ ] Apply/bake graph output.

### Exit Criteria

- [ ] Geometry node graphs can be authored or at least inspected and parameterized in-editor.

## Phase 4: Runtime Modeling Bridge

**Goal:** convert authored/evaluated modeling data into runtime render data.

### Tasks

- [ ] Convert `ModelingMeshDocument` to `XRMesh`.
- [ ] Convert evaluated `GeometrySet` mesh output to `XRMesh`.
- [ ] Preserve material slots.
- [ ] Preserve required point/corner attributes.
- [ ] Preserve skin weights where supported or report unsupported data clearly.
- [ ] Clear `XRMesh` acceleration caches after committed edits.
- [ ] Rebuild bounds.
- [ ] Notify mesh data changed.
- [ ] Schedule meshlet/cache refresh.
- [ ] Add bridge tests.

### Exit Criteria

- [ ] Runtime render data updates through explicit bake/apply and cache invalidation paths.

## Phase 5: GPUScene And Rendering Refresh

**Goal:** ensure committed/evaluated output reaches rendering without blurring ownership.

### Tasks

- [ ] Verify bake/apply updates render command data.
- [ ] Verify `GPUScene` receives updated mesh data through existing scene upload paths.
- [ ] Verify meshlet caches refresh after topology changes.
- [ ] Verify bounds/BVH refresh after position-only edits.
- [ ] Verify traditional and meshlet paths route editable/uncached meshes safely.
- [ ] Add source-contract tests that modeling edit buffers do not become production `GPUScene` meshlet ownership.

### Exit Criteria

- [ ] Rendering reflects modeling commits without relying on editor preview buffers.

## Phase 6: MCP And Automation

**Goal:** expose safe automation entry points if included in v1 scope.

### Tasks

- [ ] Decide MCP scope: raw topology ops, high-level tool commands, geometry node graph ops, or all three.
- [ ] Add read-only scene/modeling inspection tools first if useful.
- [ ] Add mutation tools only with undo and validation.
- [ ] Add allowed/denied tool configuration where appropriate.
- [ ] Regenerate MCP docs after adding or renaming tools:

```powershell
pwsh Tools/Reports/generate_mcp_docs.ps1
```

- [ ] Update `docs/features/mcp-server.md`.

### Exit Criteria

- [ ] MCP exposure is documented, validated, and safe by default.

## Phase 7: Workflow Docs And Validation

**Goal:** keep user-facing behavior documented and tested.

### Tasks

- [ ] Update editor workflow docs.
- [ ] Update README or docs index if modeling workflows become user-facing.
- [ ] Add launch/task updates only if workflows require them.
- [ ] Add targeted editor/runtime bridge tests.
- [ ] Run narrow builds.
- [ ] Record validation results in this tracker.

### Exit Criteria

- [ ] Users have a documented workflow and validation results are recorded.

## Validation

```powershell
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
dotnet build .\XREngine.Runtime.ModelingBridge\XREngine.Runtime.ModelingBridge.csproj
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Modeling
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~XRMeshModelingBridge
```
