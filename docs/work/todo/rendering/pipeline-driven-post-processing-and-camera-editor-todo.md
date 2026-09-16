# Pipeline-Driven Post-Processing And Camera Editor Decoupling TODO

Last Updated: 2026-09-15  
Owner: Rendering / Editor UI  
Status: Active / Ready for Implementation  
Design Reference:
- [Pipeline-Driven Post-Processing And Camera Editor Design](../../design/rendering/pipeline-driven-post-processing-and-camera-editor-design.md)

Related Code Surfaces:
- [`XREngine.Editor/ComponentEditors/CameraComponentEditor.cs`](../../../../XREngine.Editor/ComponentEditors/CameraComponentEditor.cs)
- [`XREngine.Runtime.Rendering/Rendering/PostProcessing/RenderPipelinePostProcessSchema.cs`](../../../../XREngine.Runtime.Rendering/Rendering/PostProcessing/RenderPipelinePostProcessSchema.cs)
- [`XREngine.Runtime.Rendering/Rendering/PostProcessing/RenderPipelinePostProcessSchemaBuilder.cs`](../../../../XREngine.Runtime.Rendering/Rendering/PostProcessing/RenderPipelinePostProcessSchemaBuilder.cs)
- [`XREngine.Runtime.Rendering/Rendering/PostProcessing/CameraPostProcessStateCollection.cs`](../../../../XREngine.Runtime.Rendering/Rendering/PostProcessing/CameraPostProcessStateCollection.cs)
- [`XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipeline.cs`](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipeline.cs)
- [`XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.PostProcessing.cs`](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.PostProcessing.cs)
- [`XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.PostProcessing.cs`](../../../../XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.PostProcessing.cs)

---

## 1. Motivation & Scope

While post-processing parameters (sliders, floats, colors) are dynamically described by `RenderPipelinePostProcessSchema`, the host UI in `CameraComponentEditor` is currently coupled to implicit runtime defaults, contains hardcoded type-checks (`if (pipeline is AdvancedRenderPipeline)`), and injects ad-hoc stage checks (such as DoF scene node dragging, tonemapping HDR bypasses, and luminance weight buttons) directly into generic ImGui rendering loops.

This TODO tracks the phased implementation of a fully dynamic, decoupled, pipeline-driven post-processing editor architecture.

---

## 2. Work Breakdown & Implementation Tasks

### Phase 1: Pipeline Selection & UI Extensibility Hook

**Objective**: Give users an explicit pipeline selector in the Camera Component Post Processing tab, and provide a generic extension hook so pipelines can inject header/footer UI (e.g. `ShadingDebugView`) without modifying `CameraComponentEditor.cs`.

- [x] **Task 1.1: Define `IRenderPipelinePostProcessUIProvider` and `PipelineEditorContext`**
  - **Files**: `XREngine.Runtime.Rendering/Rendering/PostProcessing/IRenderPipelinePostProcessUIProvider.cs`
  - Create `PipelineEditorContext` record holding `XRCamera`, `CameraComponent?`, `RenderPipeline ActiveViewportPipeline`, `RenderPipeline SelectedPipeline`, `PipelinePostProcessState State`, and `RenderPipelinePostProcessSchema Schema`.
  - Define `IRenderPipelinePostProcessUIProvider` with `DrawPipelineHeader(PipelineEditorContext)` and `DrawPipelineFooter(PipelineEditorContext)`.
- [x] **Task 1.2: Add `PostProcessUIProvider` to `RenderPipeline`**
  - **Files**: `XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipeline.cs`
  - Add virtual property `public virtual IRenderPipelinePostProcessUIProvider? PostProcessUIProvider => null;`.
- [x] **Task 1.3: Implement `AdvancedRenderPipelinePostProcessUI`**
  - **Files**: `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.PostProcessing.cs`
  - Move the `ShadingDebugView` dropdown selector into `DrawPipelineHeader`.
  - Wire `AdvancedRenderPipeline.PostProcessUIProvider` to return an instance of this provider.
- [x] **Task 1.4: Add Target Pipeline Selector to `CameraComponentEditor`**
  - **Files**: `XREngine.Editor/ComponentEditors/CameraComponentEditor.cs`
  - Add a pipeline selection combo at the top of `DrawRuntimeCameraPostProcessing`:
    - Options: `Active Viewport (Auto)` followed by all registered pipeline instances or asset types.
    - Track the selected pipeline key in a per-component or per-camera editor state.
  - Call `pipeline.PostProcessUIProvider?.DrawPipelineHeader(...)` before stages.
  - Call `pipeline.PostProcessUIProvider?.DrawPipelineFooter(...)` after stages.
  - Remove concrete references to `AdvancedRenderPipeline` and `DrawAdvancedShadingDebugViewSelector` from `CameraComponentEditor.cs`.

---

### Phase 2: Custom Stage & Parameter Drawers

**Objective**: Encapsulate specialized UI widgets (scene node pickers, preset buttons, HDR disable notices) into reusable stage drawer callbacks instead of hardcoded type branches in `CameraComponentEditor.cs`.

- [x] **Task 2.1: Define `IPostProcessStageCustomDrawer`**
  - **Files**: `XREngine.Runtime.Rendering/Rendering/PostProcessing/IPostProcessStageCustomDrawer.cs`
  - Define interface with:
    - `void DrawStageHeader(PostProcessStageCustomDrawerContext context)`
    - `void DrawStageFooter(PostProcessStageCustomDrawerContext context)`
    - `bool TryDrawParameter(PostProcessParameterCustomDrawerContext context)`
  - Add `WithCustomDrawer(IPostProcessStageCustomDrawer)` to `RenderPipelinePostProcessSchemaBuilder.PostProcessStageBuilder`.
  - Expose `IPostProcessStageCustomDrawer? CustomDrawer` on `PostProcessStageDescriptor`.
- [x] **Task 2.2: Extract Depth of Field Focus Target Drawer**
  - **Files**: `XREngine.Editor/ComponentEditors/PostProcessDrawers/DepthOfFieldStageDrawer.cs`
  - Move `DrawDepthOfFieldFocusTarget` from `CameraComponentEditor` into a dedicated `IPostProcessStageCustomDrawer` for DoF.
  - Register with `.WithCustomDrawer(new DepthOfFieldStageDrawer())` during schema construction.
- [x] **Task 2.3: Extract Luminance Weights Preset Drawer**
  - **Files**: `XREngine.Editor/ComponentEditors/PostProcessDrawers/ColorGradingStageDrawer.cs`
  - Move "Default", "Rec.709", and "Rec.601" buttons for `AutoExposureLuminanceWeights` into a custom parameter drawer.
- [x] **Task 2.4: Generalize HDR Tonemapping Bypass**
  - **Files**: `XREngine.Runtime.Rendering/Rendering/PostProcessing/`
  - Replace hardcoded `stage.Descriptor.Key.Equals(TonemappingStageKey, ...)` with an optional stage predicate or drawer hook:
    `public Func<XRCamera, (bool Disabled, string? Reason)>? StateEvaluator { get; init; }`.
- [x] **Task 2.5: Clean `CameraComponentEditor.cs`**
  - **Files**: `XREngine.Editor/ComponentEditors/CameraComponentEditor.cs`
  - Update `DrawSchemaStageSelection` and `DrawSchemaParameter` to invoke custom drawer hooks.
  - Delete `DrawDepthOfFieldFocusTarget`, hardcoded DoF type checks, and luminance button branching from `CameraComponentEditor.cs`.

---

### Phase 3: Dynamic Pipeline Preview Targets

**Objective**: Allow render pipelines to declare their preferred intermediate and final preview texture/framebuffer targets, removing hardcoded references to `DefaultRenderPipeline` in the camera preview inspector.

- [x] **Task 3.1: Add Preview Target Discovery to `RenderPipeline`**
  - **Files**: `XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipeline.cs`
  - Add virtual properties:
    - `public virtual IReadOnlyList<string> PreferredPreviewTextureNames => ...`
    - `public virtual IReadOnlyList<string> PreferredPreviewFrameBufferNames => ...`
- [x] **Task 3.2: Implement Preview Target Overrides in `AdvancedRenderPipeline`**
  - **Files**: `XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Advanced/AdvancedRenderPipeline.PostProcessing.cs`
  - Provide prioritized pass targets (`FinalPostProcessOutputTexture`, `PostProcessOutputTexture`, `HDRSceneTex`, `LightingTexture`, `AlbedoOpacity`, etc.).
- [x] **Task 3.3: Update `CameraComponentEditor.DrawPreviewSection`**
  - **Files**: `XREngine.Editor/ComponentEditors/CameraComponentEditor.cs`
  - Query `selectedPipeline.PreferredPreviewTextureNames` and `selectedPipeline.PreferredPreviewFrameBufferNames` dynamically.
  - Remove `PreferredPreviewTextureNames` and `PreferredPreviewFrameBufferNames` static arrays that reference `DefaultRenderPipeline.*`.

---

### Phase 4: Stage Schema Deduplication & Composable Pass Library

**Objective**: De-duplicate the ~2,000 lines of copy-pasted post-processing schema code shared between `DefaultRenderPipeline.PostProcessing.cs` and `AdvancedRenderPipeline.PostProcessing.cs`.

- [x] **Task 4.1: Create Standardized Post-Process Stage Library**
  - **Files**: `XREngine.Runtime.Rendering/Rendering/PostProcessing/CommonPostProcessStages.cs`
  - Extract reusable builder methods for universal passes:
    - `DescribeTonemappingStage(...)`
    - `DescribeColorGradingStage(...)`
    - `DescribeBloomStage(...)`
    - `DescribeVignetteStage(...)`
    - `DescribeChromaticAberrationStage(...)`
    - `DescribeLensDistortionStage(...)`
    - `DescribeDepthOfFieldStage(...)`
- [x] **Task 4.2: Refactor `DefaultRenderPipeline.PostProcessing.cs`**
  - Delegate shared stages to `CommonPostProcessStages`.
  - Keep only pipeline-unique passes (e.g. legacy OpenGL fog/scattering passes).
- [x] **Task 4.3: Refactor `AdvancedRenderPipeline.PostProcessing.cs`**
  - Delegate shared stages to `CommonPostProcessStages`.
  - Keep only native compute passes (e.g. GTAO, compute AutoExposure, compute TAA).

---

## 3. Verification & Acceptance Criteria

### Automated Verification
```powershell
# Build verification
dotnet build .\XREngine.Editor\XREngine.Editor.csproj
dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj

# Unit tests
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter "FullyQualifiedName~Rendering"
```

### Manual Validation Scenarios
1. **Pipeline Selector Inspection**:
   - Open Editor -> Select a Scene Node with `CameraComponent`.
   - Verify the "Post Processing" tab shows `Target Pipeline: [ Active Viewport (Auto) v ]`.
   - Switch target to `AdvancedRenderPipeline`: verify GTAO and native Vulkan stages are visible.
   - Switch target to `DefaultRenderPipeline`: verify OpenGL-specific stages are visible.
   - Verify changing values persists in `CameraPostProcessStateCollection` for that specific pipeline ID without cross-polluting other pipelines.
2. **Extensibility & Decoupling**:
   - Verify `git grep "AdvancedRenderPipeline" XREngine.Editor/ComponentEditors/CameraComponentEditor.cs` yields 0 matches.
   - Verify `ShadingDebugView` functions on the live viewport under `AdvancedRenderPipeline`.
3. **Parity Check on Custom Widgets**:
   - Drag and drop a `TransformBase` / `SceneNode` into Depth of Field Focus Target; confirm tracking and undo/redo work.
   - Click "Rec.709" on Auto Exposure Luminance Weights; confirm normalization and undo/redo work.
   - Enable HDR Output; confirm Tonemapping displays the bypass notice and disables interactive controls.
