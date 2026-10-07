# Modeling Validation

## Scope
This document owns modeling checks for topology, geometry nodes, core tools, GPU preview, subdivision, and editor/runtime integration. It records commands, runtime checks, visual checks, dependency checks, and evidence. It does not hold implementation backlog.

Architecture: [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md); [Mesh submission strategies](../../../architecture/rendering/mesh-submission-strategies.md)  Code todos: [Modeling TODO](../../todo/modeling/modeling-todo.md)

## Setup
Use the ImGui editor unless a check says otherwise. The relevant VS Code tasks are `Build-Editor`, `Build-Editor-Fast`, `Start-Editor-NoDebug`, `Start-Editor-RendererDevelopment-NoDebug`, and `Generate-UnitTestingWorldSettings`. The relevant launch profiles are `Editor (Default World)`, `Editor (Renderer Development)`, `Editor (Unit Testing World)`, and `Editor (Unit Testing World, Validation Layers)`. Use `XRE_WORLD_MODE=UnitTesting` for unit-world editor checks. Use `XRE_GL_DEBUG=1` and `XRE_VULKAN_VALIDATION=1` for graphics validation checks. OpenSubdiv checks also use `Tools/Generate-Dependencies.ps1` after an approved dependency change.

## Checks

### Topology
Architecture: [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Build the modeling project. | `dotnet build .\XREngine.Modeling\XREngine.Modeling.csproj` | The project builds without new warnings. | Planned | none |
| Run modeling unit tests. | `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Modeling` | Topology, validation, undo, attribute, and tool tests pass. | Planned | none |
| Check topology export. | Convert authored topology to runtime buffers in a focused test or tool. | The output preserves required attributes and reports invalid data. | Planned | none |

### Geometry Nodes
Architecture: [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Validate graph assets. | Load valid and invalid graph assets through unit tests or a small editor workflow. | Valid graphs load. Invalid graphs report actionable diagnostics. | Planned | none |
| Run CPU graph evaluation. | Execute the first node set through unit tests. | Output is deterministic and unsupported geometry components pass through unchanged. | Planned | none |
| Check bake and apply. | Apply or bake graph output into authored topology in a focused workflow. | Stable IDs and named attributes are preserved. Anonymous attributes are dropped unless captured or stored. | Planned | none |

### Core Tools
Architecture: [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Run tool tests. | `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Modeling` | Tool lifecycle, invalid input, validation, undo, and attribute tests pass without a renderer. | Planned | none |
| Check direct edit sequences. | Use focused tests or the editor modeling workflow to run add, connect, split, bevel, bridge, extrude, inset, transform, smooth, and cleanup operations. | Each committed operation keeps topology valid or reports expected diagnostics. | Planned | none |

### GPU Preview
Architecture: [GPU meshlet zero-readback rendering design](../../design/rendering/gpu-meshlet-zero-readback-rendering-design.md); [Mesh submission strategies](../../../architecture/rendering/mesh-submission-strategies.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Build rendering and modeling integration. | `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`; `dotnet build .\XREngine.Runtime.ModelingIntegration\XREngine.Runtime.ModelingIntegration.csproj` | Both projects build without new warnings. | Planned | none |
| Run rendering source contracts. | `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Rendering` | Contracts block renderer dependencies in core modeling and block synchronous preview readback in hot paths. | Planned | none |
| Check picking and selection preview. | Run the editor with the modeling preview enabled and interact with vertices, edges, faces, brush selection, and hover. | Preview results appear without render-thread or UI-thread stalls. | Planned | none |
| Check overflow diagnostics. | Use a preview arena smaller than the test geometry requires. | The preview degrades visibly and diagnostics explain the overflow. CPU commits stay valid. | Planned | none |

### Subdivision And OpenSubdiv
Architecture: [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Run subdivision evaluator tests. | `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Modeling` | Interface, fake backend, CPU backend, preview, and bake tests pass without native dependencies. | Planned | none |
| Check subdivision preview and bake. | Preview and bake a supported control cage through the editor or focused test. | Preview is disposable. Bake writes authored topology, stable IDs, and attributes through explicit paths. | Planned | none |
| Check dependency output after OpenSubdiv approval. | `pwsh Tools/Generate-Dependencies.ps1`; `dotnet build XRENGINE.slnx` | Dependency tables, license files, and notices include the approved OpenSubdiv supply path. | Blocked by owner decision | none |

### Editor And Runtime Integration
Architecture: [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md); [MCP server guide](../../../developer-guides/ai/mcp-server.md)

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Build editor integration. | `dotnet build .\XREngine.Editor\XREngine.Editor.csproj` | The editor builds without new warnings. | Planned | none |
| Run XRMesh modeling bridge tests. | `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~XRMeshModelingIntegration` | Conversion, validation, cache invalidation, skinning, and blendshape contracts pass. | Planned | none |
| Check the ImGui modeling workflow. | Start `Editor (Default World)` or `Editor (Renderer Development)` and enter modeling mode. | Selection modes, active tools, options, overlays, commit, cancel, validation, and dirty state work. | Planned | none |
| Check runtime render refresh. | Commit a topology edit or bake graph output, then inspect the viewport and runtime diagnostics. | `XRMesh`, bounds, BVH, meshlets, and `GPUScene` refresh through explicit bridge paths. | Planned | none |
| Check MCP documentation after tool changes. | `pwsh Tools/Reports/generate_mcp_docs.ps1` after adding or renaming MCP tools. | MCP docs describe the new or renamed tools. | Planned | none |

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
