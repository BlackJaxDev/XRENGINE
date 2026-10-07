# Post-Process Editor Extension

This guide tells you how a render pipeline describes its post-process stages and how it adds custom editor UI to the camera editor. The camera editor contains no code for a specific pipeline type. Each pipeline supplies its schema, its editor controls, and its preview targets.

## Type Map

| Type | Location | Responsibility |
|---|---|---|
| `RenderPipeline.DescribePostProcessSchema(...)` | `XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipeline.cs` | A pipeline overrides this method to add stages and categories to the schema builder. |
| `RenderPipelinePostProcessSchemaBuilder` | `XREngine.Runtime.Rendering/Rendering/PostProcessing/` | Builds stages, categories, parameters, and uniform customizations. |
| `CommonPostProcessStages` | `XREngine.Runtime.Rendering/Rendering/PostProcessing/CommonPostProcessStages.cs` | Shared stage keys, category keys, and `Describe*Stage` methods. |
| `PostProcessStageDescriptor` | `XREngine.Runtime.Rendering/PostProcessing/PostProcessStageDescriptor.cs` | Built stage: key, parameters, backing type, editor section, custom drawer, state evaluator. |
| `IRenderPipelineEditorUIProvider` | `XREngine.Runtime.Rendering/Rendering/PostProcessing/` | Pipeline-owned controls for the camera Settings, Post Processing, and Debug tabs. |
| `PipelineEditorContext` | `XREngine.Runtime.Rendering/Rendering/PostProcessing/PipelineEditorContext.cs` | Camera, component, active pipeline, selected pipeline, state, and schema passed to provider methods. |
| `PipelineEditorUiServices` | `XREngine.Runtime.Rendering/Rendering/PostProcessing/PipelineEditorUiServices.cs` | Registry that maps a pipeline type to an editor UI provider factory. |
| `IPostProcessStageCustomDrawer` | `XREngine.Runtime.Rendering/Rendering/PostProcessing/IPostProcessStageCustomDrawer.cs` | Stage header, stage footer, and parameter override hooks. |
| `PostProcessCustomDrawerRegistry` | `XREngine.Editor/ComponentEditors/PostProcessDrawers/` | Editor-side lookup of custom drawers. |
| `CameraComponentEditor` | `XREngine.Editor/ComponentEditors/CameraComponentEditor*.cs` | Draws the target pipeline selector, tabs, schema stages, and preview. |

## Describe The Schema

Override `DescribePostProcessSchema(...)` in the pipeline. Use the shared stage library for standard stages, and add only the stages that are unique to the pipeline:

```csharp
protected override void DescribePostProcessSchema(RenderPipelinePostProcessSchemaBuilder builder)
{
    CommonPostProcessStages.AddStandardPipelineSchema(builder);
    // Add pipeline-unique stages with builder.Stage(key, displayName).
}
```

`RenderPipeline.BuildPostProcessSchema()` calls this method and builds the schema. `DefaultRenderPipeline` and `AdvancedRenderPipeline` both call `AddStandardPipelineSchema`.

A stage builder supports these calls:

- `BackedBy<TSettings>()` binds the stage to a settings class.
- `AddParameter(...)`, `SetUniformDefault`, `SetUniformRange`, `TreatUniformAsColor`, `DefineUniformEnum`, `HideUniform`, and `RenameUniform` shape the parameter list.
- `InEditorSection(...)` and `SetUniformEditorSection(...)` select the camera editor tab (`PipelineEditorSection`).
- `WithStateEvaluator(...)` returns `(Disabled, Reason)` for a camera. The editor disables the stage controls and shows the reason. The tonemapping stage uses this hook to show the HDR-output bypass.
- `WithCustomDrawer(...)` attaches an `IPostProcessStageCustomDrawer` directly to the stage.

Use the `CommonPostProcessStages.StageKeys` constants for shared stage keys. Do not compare stage keys in editor code.

## Add Pipeline Editor UI

1. Implement `IRenderPipelineEditorUIProvider`. All four methods have empty default bodies, so implement only the ones you need:
   - `DrawCameraSettings(context)` for the Settings tab.
   - `DrawPostProcessingHeader(context)` and `DrawPostProcessingFooter(context)` around the stage selector.
   - `DrawDebug(context)` for the Debug tab.
2. Register a factory for the pipeline type in the UI host. The ImGui host does this in `XREngine.Runtime.Rendering.ImGui/ImGuiBackend.cs`:

   ```csharp
   PipelineEditorUiServices.Register<AdvancedRenderPipeline>(static pipeline => new AdvancedRenderPipelineEditorUI(pipeline));
   ```

3. Override `EditorUIProvider` in the pipeline and create the provider lazily:

   ```csharp
   public override IRenderPipelineEditorUIProvider? EditorUIProvider
       => _editorUIProvider ??= PipelineEditorUiServices.Create(this);
   ```

The registry matches the exact runtime type of the pipeline. A derived pipeline type needs its own registration. Keep ImGui code in the UI host project, not in `XREngine.Runtime.Rendering`.

Rules for provider code:

- Camera-wide edits are allowed only when `context.IsActivePipeline` is true. Selecting another pipeline for inspection never rebinds the camera.
- Pipeline settings affect every instance that uses the selected pipeline asset. Camera settings stay local to the camera.
- `AdvancedRenderPipelineEditorUI` draws the `ShadingDebugView` selector in the Debug tab. `DefaultPipelineEditorUI` draws the shared camera settings and the Forward+ debug controls.

## Add A Custom Stage Drawer

Implement `IPostProcessStageCustomDrawer`:

- `DrawStageHeader(PostProcessStageCustomDrawerContext)` runs before the standard parameters.
- `DrawStageFooter(PostProcessStageCustomDrawerContext)` runs after them.
- `TryDrawParameter(PostProcessParameterCustomDrawerContext)` returns `true` when it drew the parameter. Return `false` to use the default control.

Both context records carry the camera, the optional `CameraComponent`, the stage descriptor, the stage state, and an undo target. Record edits against the undo target so undo and redo work.

`PostProcessCustomDrawerRegistry.GetDrawer(descriptor)` selects the drawer in this order:

1. `PostProcessStageDescriptor.CustomDrawer`, set by `WithCustomDrawer(...)`.
2. A drawer registered with `PostProcessCustomDrawerRegistry.Register(stageKey, drawer)`.
3. A built-in drawer selected by the stage backing type. `DepthOfFieldStageDrawer` serves `DepthOfFieldSettings` (focus-target drop target). `ColorGradingStageDrawer` serves `ColorGradingSettings` (luminance-weight presets).

Use `Register(stageKey, drawer)` for editor-only drawers, because the runtime schema cannot reference editor types.

## Preview Targets

The camera preview asks the selected pipeline for its preferred targets. Override these properties to list targets in priority order:

```csharp
public override IReadOnlyList<string> PreferredPreviewTextureNames => ...;
public override IReadOnlyList<string> PreferredPreviewFrameBufferNames => ...;
```

`RenderPipeline` supplies defaults. `DefaultRenderPipeline` and `AdvancedRenderPipeline` override both properties in their `PostProcessing` partial files.

## Target Pipeline Selection

The camera editor lists the active viewport pipeline first, then the other candidate pipelines. It stores the selected pipeline ID in per-camera editor state. Post-process values persist in `CameraPostProcessStateCollection` for each pipeline ID, so edits for one pipeline do not change another.

## Validation

Runtime checks for this feature are in the [Default and Advanced pipeline validation doc](../../work/testing/rendering/default-and-advanced-pipeline-validation.md#from-pipeline-driven-post-processing-and-camera-editor-todomd).
