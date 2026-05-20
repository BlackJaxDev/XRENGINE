# GPU Modeling Preview TODO

Last Updated: 2026-05-19
Owner: Modeling/Rendering
Status: Planned child tracker for [GPU-Accelerated Modeling Roadmap](00-gpu-accelerated-modeling-roadmap.md) Phase 4
Target Branch: `modeling-gpu-accelerated-tools`

Source design:

- [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md)

Related docs:

- [GPU-Accelerated Modeling Roadmap TODO](00-gpu-accelerated-modeling-roadmap.md)
- [Core Modeling Tools TODO](03-core-modeling-tools-todo.md)
- [Mesh submission strategies](../../../architecture/rendering/mesh-submission-strategies.md)
- [GPU meshlet zero-readback rendering design](../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md)

## Parent Roadmap Contract

This tracker owns GPU acceleration for disposable modeling previews, selection, picking, and stable-topology interactions. It must not make GPU buffers authoritative for committed topology.

## Goal

Make modeling tools feel immediate by using compute shaders and GPU-resident preview buffers for picking, selection, live cut/bevel/bridge previews, smoothing/relax, proportional transforms, and derived visual overlays without synchronous readbacks.

## Non-Negotiable Rules

- [ ] GPU preview data is disposable and never required for commit correctness.
- [ ] No synchronous GPU readback in hover, picking, preview, or draw paths.
- [ ] Use compute shaders/storage buffers for persistent preview work, not geometry shaders.
- [ ] Overflow degrades preview quality visibly and safely; it must not corrupt commits.
- [ ] CPU commits recompute or validate final tool results.
- [ ] Preview/update hot paths avoid heap allocations after setup.
- [ ] Renderer integration stays behind bridge abstractions; `XREngine.Modeling` stays renderer-independent.

## Success Criteria

- [ ] Compact edit buffers can be uploaded from the modeling document.
- [ ] GPU picking and nearest element queries produce hover/selection candidates without render-thread stalls.
- [ ] GPU selection masks support grow/shrink and brush/falloff workflows.
- [ ] Live bevel, knife/cut, loop cut, and bridge previews render from preview arenas.
- [ ] Smooth/relax and proportional transform previews run on GPU for stable topology.
- [ ] Preview overflow and unsupported backend cases are visible in diagnostics.
- [ ] Source-contract tests protect no-readback paths.

## Primary Code Areas

- `XREngine.Runtime.Rendering/Rendering/API/Rendering/`
- `XREngine.Runtime.Rendering/Rendering/Commands/`
- `XREngine.Runtime.Rendering/Objects/Meshes/`
- `XREngine.Runtime.ModelingBridge/`
- `XREngine.Editor/`
- `Build/CommonAssets/Shaders/Compute/`
- `Build/CommonAssets/Shaders/Scene3D/`
- `XREngine.UnitTests/Modeling/`
- `XREngine.UnitTests/Rendering/`

## Phase 0: Capability And Ownership Boundaries

**Goal:** define renderer-facing preview capabilities before adding shaders.

### Tasks

- [ ] Define modeling GPU preview capability probes.
- [ ] Define preview buffer ownership and lifetime.
- [ ] Define buffer ring/fence policy to avoid overwriting in-flight preview data.
- [ ] Define fallback behavior for unsupported GPU preview features.
- [ ] Add source-contract tests that prevent `XREngine.Modeling` from depending directly on renderer backends.

### Exit Criteria

- [ ] Preview capability and ownership boundaries are explicit.

## Phase 1: Compact Edit Buffers

**Goal:** upload an editable topology snapshot suitable for compute queries and preview generation.

### Tasks

- [ ] Define `ModelingVertexBuffer` layout.
- [ ] Define `ModelingEdgeBuffer` layout.
- [ ] Define `ModelingLoopBuffer` layout.
- [ ] Define `ModelingFaceBuffer` layout.
- [ ] Define `ModelingSelectionBuffer` layout.
- [ ] Define `ModelingAttributeBuffer` layout for initial supported attributes.
- [ ] Add dirty-range uploads.
- [ ] Add buffer capacity planning and growth policy.
- [ ] Add debug names and diagnostics.

### Exit Criteria

- [ ] A modeling document can produce GPU edit buffers without baking a production mesh.

## Phase 2: Picking And Selection

**Goal:** accelerate editor hover, picking, and selection operations.

### Tasks

- [ ] Implement ray-to-vertex candidate query.
- [ ] Implement ray-to-edge candidate query.
- [ ] Implement ray-to-face candidate query.
- [ ] Implement brush/shape selection masks.
- [ ] Implement nearest element reduction without CPU stalls.
- [ ] Implement one-frame-late hover result consumption where needed.
- [ ] Add CPU fallback query path.
- [ ] Add tests/source contracts for no synchronous readback in hot paths.

### Exit Criteria

- [ ] Picking and selection acceleration work without blocking render or UI threads for GPU readback.

## Phase 3: Live Topology Previews

**Goal:** draw disposable previews for tools whose commit remains CPU-owned.

### Tasks

- [ ] Define `ModelingToolCommandBuffer`.
- [ ] Define `ModelingPreviewVertexBuffer`.
- [ ] Define `ModelingPreviewIndexBuffer`.
- [ ] Define `ModelingPreviewLineBuffer`.
- [ ] Define `ModelingPreviewCounterBuffer`.
- [ ] Implement knife/cut candidate preview.
- [ ] Implement loop cut/ring preview.
- [ ] Implement bevel strip preview.
- [ ] Implement bridge face strip preview.
- [ ] Implement preview arena overflow detection.
- [ ] Render degraded previews safely when overflow occurs.

### Exit Criteria

- [ ] Live topology previews are visibly useful and disposable.
- [ ] Preview overflow cannot affect CPU commits.

## Phase 4: Stable-Topology Compute Tools

**Goal:** accelerate position-only interactions.

### Tasks

- [ ] Implement smooth selected vertices compute preview.
- [ ] Implement relax selected vertices compute preview.
- [ ] Implement proportional transform preview.
- [ ] Implement masked falloff preview.
- [ ] Implement GPU normal refresh for preview if needed.
- [ ] Implement GPU bounds refresh for preview if needed.
- [ ] Add CPU commit parity tests for smooth/relax/proportional operations.

### Exit Criteria

- [ ] GPU preview and CPU commit agree within defined tolerances for stable-topology tools.

## Phase 5: Overlay Rendering And Diagnostics

**Goal:** make preview state visible and debuggable.

### Tasks

- [ ] Add editor overlay rendering for vertices, edges, faces, selected elements, hover candidates, cut paths, and preview faces.
- [ ] Add preview counters for bytes, arena usage, overflow count, dispatch count, and fallback count.
- [ ] Add optional debug visualization for edit buffers.
- [ ] Add profiler labels for preview dispatches.
- [ ] Ensure logging does not allocate in hot paths after warmup.
- [ ] Add source-contract tests for forbidden readback helpers in preview hot paths.

### Exit Criteria

- [ ] Preview diagnostics explain unsupported backends, capacity overflow, and fallbacks.

## Validation

```powershell
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj
dotnet build .\XREngine.Runtime.ModelingBridge\XREngine.Runtime.ModelingBridge.csproj
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Modeling
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Rendering
```
