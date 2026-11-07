# Render Pipeline Workbench

The Render Pipeline Workbench is the ImGui editor window that shows and edits render pipelines. Open it from **View > Render Pipeline Workbench**, or with **Open in Workbench** in the render pipeline asset inspector and in property editors. This document describes the ownership, the data contracts, the cache keys, and the type map.

## Window Layout

- **Target bar.** Back and forward history, the pipeline selector, the instance selector, the generation badge, the backend and plan badges, the derived badge, the Live and Frozen toggle, and the validation counter.
- **Authoring row.** Edit, Apply, Discard, Fork, and Derive.
- **Outline.** The command tree with search, filters, timing badges, pass badges, and overlay markers.
- **Central views.** Flow, Passes, Resources, Execution, Settings, Validation, Script, and Patches (derived pipelines only).
- **Inspector.** The text and the properties of the selected command, pass, or resource.
- **Preview.** Live texture and framebuffer previews (`RenderTexturePreviewPanel`).

Every view shares one selection (`RenderPipelineWorkbenchSelection`). A selection in one view highlights the linked objects in the other views (`RenderPipelineWorkbenchLinks`).

## Ownership

| Owner | Responsibility |
|---|---|
| `RenderPipeline` | Generates the command chain, describes resources, describes the post-process schema, and applies overlays. |
| `XRRenderPipelineInstance` | Owns resource generations, the pass metadata of the active generation, and the execution trace (`RenderPipelineExecutionTrace`). |
| `AbstractRenderer.PlanDiagnostics` | Publishes the backend plan (`RenderGraphPlanSnapshot`) through `IRenderGraphPlanDiagnosticsSource`. Vulkan publishes its compiled plan. OpenGL publishes the recorded pass order (`RenderGraphRecordedPlanner`). |
| `RenderPipelineWorkbenchWindowState` | Holds the target, the cached model, the selection, the view settings, the authoring state, and the script editor. |
| `RenderPipelineWorkbenchModel` | Caches everything the views draw for one pipeline instance. |
| `RenderPipelineWorkbenchAuthoring` | Owns the draft, the draft preview, the background validation, and the command palette. |

The Workbench never changes a live pipeline while the user edits. Edits change a draft (`RenderPipelineDraft`). **Apply** copies the draft to the pipeline as one undo step. Code pipelines are read-only; use **Fork** to make an editable `CustomRenderPipeline`, or **Derive** to make a derived script.

## Data Contracts

| Snapshot | Source | Contents |
|---|---|---|
| `RenderPipelineStructureSnapshot` | `RenderPipelineStructureSnapshot.Build` | The flat command list with parents, depth, profiling paths, `RenderCommandInfo`, and the pass indices of each command. |
| `RenderPipelineResourceSnapshot` | `RenderPipelineResourceSnapshot.Build` | One entry for each logical resource: spec, flags, size, format, producer passes, consumer passes, first and last use, and estimated bytes. |
| `RenderGraphPlanSnapshot` | The renderer | Pass order, batches, submissions, synchronization edges, barriers, lifetimes, alias groups, reorders, and order violations. |
| `RenderPipelineTimingSnapshot` | The pipeline GPU profiler | GPU time for each profiling path. |
| `RenderPipelinePostProcessSchema` | `RenderPipeline.PostProcessSchema` | Stages, parameters, and categories. |
| `RenderPipelineValidationReport` | `RenderPipelineValidator` | Problems from the rules in `RenderPipelineValidator.DefaultRules` and from the pipeline sources. |
| `RenderPipelineOverlayReport` | `RenderPipeline.OverlayReport` | The result of each overlay patch and the marker of each changed command. |

Snapshots are immutable. A producer publishes a new snapshot object; the Workbench compares references to find changes.

## Cache Keys

Draw code must not allocate. Each model part rebuilds only when its key changes, and all display text is formatted when a part rebuilds.

| Part | Rebuilds when |
|---|---|
| Outline rows and Flow layout | The command generation changes. |
| Pass graph | The pass metadata collection or its revision changes. |
| Resource table | The resource snapshot changes. |
| Plan rows | The plan snapshot reference changes. |
| Timing badges | At most four times each second, when the profiler publishes a new snapshot. |
| Validation rows | The pipeline, the instance, the generation, or the draft revision changes. Draft validation runs on a background task with a private copy of the draft. |
| Script text | The pipeline or its command generation changes. |
| Patch rows | The overlay list or the overlay report changes. The base hash check runs only when the overlay list changes. |

## Authoring

`CustomRenderPipeline` holds data that a script can save: the command tree, the scene passes, the pass dependencies, the authored resources (`AuthoredRenderResource`), and the authored post-process stages (`AuthoredPostProcessStage`). Branches use data conditions (`RenderBranchCondition`) instead of code delegates, so they save to text.

`RenderPipelineDraftState` is the editable copy. The edit types (`RenderPipelineDraftEdits`, `RenderPipelineDraftPassEdits`, `RenderPipelineDraftResourceEdits`, `RenderPipelineDraftStageEdits`) keep push and pop pairs together, rename resource references, and prune resources that lose all producers. `RenderPipelineDraftPreview` builds pass metadata, the structure, the resource layout, and the schema from a draft without a live generation, so the views and the validator show the draft before **Apply**.

The Script view shows the `.xrs` text. For an authored pipeline, **Edit Script** opens `RenderPipelineScriptDraftEditor`. **Apply Script** loads the text into the draft as one undo step. A syntax error leaves the draft unchanged and lists the line and the column.

**File > New Pipeline...** opens `NewRenderPipelineWizard`. The wizard makes a new `.xrs` file from a template (`Build/CommonAssets/RenderPipelines/Templates/`), from a code base (`derive Default;`), from a parent script (`derive "parent.xrs";`), or as an expanded fork of the current pipeline. It loads and validates the text for OpenGL and Vulkan before it writes the file.

## Derived Pipelines

A derived pipeline is an instance of its base type plus `RenderPipeline.Overlays`. Each overlay holds command patches, resource patches, schema patches, and setting overrides. See [Derived Render Pipelines](../../developer-guides/rendering/render-pipelines/derived-render-pipelines.md).

The Workbench shows a derived pipeline as follows:

- The target bar shows "Derived from *Type* (*N* overlays)". A click opens the Patches view.
- The Outline and the Flow show a marker on each command that a patch added or changed: `+` insert, `R` replace, `W` wrap, `P` property, `C` condition.
- The context menu of a command has **Copy anchor id** and **Copy patch target**.
- The Patches view lists each patch with its status. **Revert** removes a patch. **Accept rebase** stores the current base hash and the renamed anchor ids (`RenderPipelineOverlayEdits`).
- The validator reports patches that did not apply (`derived.patch-not-applied`), patches that use structural paths (`derived.structural-path`), and overlays made for a different base type (`derived.base-type-mismatch`).

## MCP Tools

| Tool | Contents |
|---|---|
| `get_render_pipeline_structure` | The command tree with anchor ids, patch targets, and overlay markers, plus overlays and stages. |
| `get_render_graph_plan` | The backend plan snapshot. |
| `validate_render_pipeline` | Validator problems for the live pipeline or for an `.xrs` file. |
| `list_render_pipeline_resources` | Live textures and framebuffers, plus the logical resources of the layout. |

## Type Map

| Type | Location | Responsibility |
|---|---|---|
| `EditorImGuiUI.RenderPipelineWorkbench*.cs` | `XREngine.Editor/IMGUI/` | Window, target bar, authoring row, views, Inspector, and Preview drawing. |
| `RenderPipelineWorkbenchWindowState` | `XREngine.Editor/RenderPipelineWorkbench/` | Window state, target, and view settings. |
| `RenderPipelineWorkbenchModel`, `RenderPipelineWorkbenchInputs` | `XREngine.Editor/RenderPipelineWorkbench/` | Cached view data and its rebuild keys. |
| `RenderPipelineTimelineLayoutBuilder` | `XREngine.Editor/RenderPipelineWorkbench/` | Flow lanes and steps. |
| `RenderPipelinePassGraphLayout` | `XREngine.Editor/RenderPipelineWorkbench/` | Pass nodes and hazard edges from `RenderGraphSynchronizationPlanner.Build`. |
| `RenderPipelineWorkbenchResourceTable` | `XREngine.Editor/RenderPipelineWorkbench/` | Resources view rows, filters, and grouping. |
| `RenderPipelineWorkbenchPlanView` | `XREngine.Editor/RenderPipelineWorkbench/` | Execution view rows: plan, barriers, lifetimes, reorders, and compare. |
| `RenderPipelineWorkbenchValidation` | `XREngine.Editor/RenderPipelineWorkbench/` | Validator runs and cached rows. |
| `RenderPipelineWorkbenchTiming` | `XREngine.Editor/RenderPipelineWorkbench/` | GPU timing mapped to commands and passes. |
| `RenderPipelineDraft`, `RenderPipelineDraftState` | `XREngine.Editor/RenderPipelineWorkbench/` | The editable copy and Apply. |
| `RenderPipelineFork` | `XREngine.Editor/RenderPipelineWorkbench/` | An editable `CustomRenderPipeline` from any pipeline. |
| `NewRenderPipelineWizard`, `RenderPipelineTemplates` | `XREngine.Editor/RenderPipelineWorkbench/` | New `.xrs` files from templates, bases, parents, or forks. |
| `RenderPipelineScriptDraftEditor` | `XREngine.Editor/RenderPipelineWorkbench/` | The editable Script view. |
| `RenderPipelineOverlayEdits` | `XREngine.Editor/RenderPipelineWorkbench/` | Revert a patch and accept a rebase. |
| `RenderPipelineScriptLoader`, `RenderPipelineScriptWriter` | `XREngine.Runtime.Rendering/Rendering/Pipelines/Scripting/` | Load and save `.xrs` files. |
| `RenderPipelineOverlay`, patch types, `RenderCommandPaths` | `XREngine.Runtime.Rendering/Rendering/Pipelines/Overlays/` | Derived pipeline data and anchor resolution. |
| `RenderPipelineValidator`, rules | `XREngine.Runtime.Rendering/Rendering/Pipelines/Validation/` | Validation rules and reports. |
| `EditorMcpActions.RenderPipelineWorkbench.cs` | `XREngine.Editor/Mcp/Actions/` | Workbench MCP tools. |
