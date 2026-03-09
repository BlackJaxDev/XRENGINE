# XREngine Build Warnings Report

_Generated: 2026-03-09 15:03:40 | Configuration: Debug | Total warnings: 127_

---

## Summary by Project

| Project | Warnings |
|---------|----------|
| **XREngine** | 86 |
| **XREngine.Editor** | 23 |
| **XREngine.UnitTests** | 18 |

## Summary by Warning Code

| Code | Count | Description |
|------|-------|-------------|
| [`CS0414`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0414?view=net-10.0) | 22 | Compiler Warning (level 3) CS0414 |
| [`CS8604`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8604?view=net-10.0) | 17 | Possible null reference argument |
| [`CS0649`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0649?view=net-10.0) | 12 | Compiler Warning (level 4) CS0649 |
| [`CS8602`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8602?view=net-10.0) | 12 | Dereference of a possibly null reference |
| [`CS8600`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8600?view=net-10.0) | 11 | Converting null literal or possible null value to non-nullable type |
| [`CS0169`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0169?view=net-10.0) | 9 | Compiler Warning (level 3) CS0169 |
| [`CS0618`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-messages/cs0618?view=net-10.0) | 6 | Compiler Warning (level 2) CS0618 |
| [`CS0219`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0219?view=net-10.0) | 6 | Compiler Warning (level 3) CS0219 |
| [`CS9193`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs9193?view=net-10.0) | 5 | Argument should be a variable because it is passed to a ref readonly parameter |
| [`CS8603`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8603?view=net-10.0) | 5 | Possible null reference return |
| [`CS9192`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs9192?view=net-10.0) | 4 | Argument should be passed with ref or in keyword |
| [`CS8601`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8601?view=net-10.0) | 4 | Possible null reference assignment |
| [`CS4014`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-messages/async-await-errors?view=net-10.0) | 3 | These compiler errors and warnings indicate errors in the syntax for declaring and implementing `async` methods that use the `await` expression. |
| [`CS1717`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs1717?view=net-10.0) | 2 | Learn more about: Compiler Warning (level 3) CS1717 |
| [`CS0652`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0652?view=net-10.0) | 2 | Compiler Warning (level 2) CS0652 |
| [`CS9191`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs9191?view=net-10.0) | 2 | The ref modifier for argument corresponding to in parameter is equivalent to in |
| [`SYSLIB0050`](https://learn.microsoft.com/en-us/dotnet/fundamentals/syslib-diagnostics/syslib0050?view=net-10.0) | 2 | Learn about the obsoletion of formatter-based serialization APIs that generates compile-time warning SYSLIB0050. |
| [`CS0162`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0162?view=net-10.0) | 1 | Compiler Warning (level 2) CS0162 |
| [`CS0067`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0067?view=net-10.0) | 1 | Compiler Warning (level 3) CS0067 |
| [`CS0109`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0109?view=net-10.0) | 1 | Compiler Warning (level 4) CS0109 |

---

## Project: XREngine
> 86 warning(s)

### High Priority

#### [`CS8602`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8602?view=net-10.0) (10) - Dereference of a possibly null reference

- **XREngine/Core/Tools/Triangle Converter/TriStripper.cs**
  - L344: Dereference of a possibly null reference.
- **XREngine/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.cs**
  - L889: Dereference of a possibly null reference.
- **XREngine/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs**
  - L187: Dereference of a possibly null reference.
- **XREngine/Rendering/API/Rendering/OpenXR/OpenXRAPI.OpenGL.cs**
  - L378: Dereference of a possibly null reference.
- **XREngine/Rendering/API/Rendering/OpenXR/OpenXRAPI.RuntimeStateMachine.cs**
  - L189: Dereference of a possibly null reference.
- **XREngine/Rendering/API/Rendering/OpenXR/OpenXRAPI.XrCalls.cs**
  - L298: Dereference of a possibly null reference.
- **XREngine/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderBuffer.cs**
  - L140: Dereference of a possibly null reference.
- **XREngine/Rendering/XRMeshRenderer.cs**
  - L281: Dereference of a possibly null reference.
- **XREngine/Scene/Components/Audio/STT/Providers/DeepgramSTTProvider.cs**
  - L44: Dereference of a possibly null reference.
- **XREngine/Scene/Components/Audio/STT/Providers/GoogleSTTProvider.cs**
  - L54: Dereference of a possibly null reference.

#### [`CS8600`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8600?view=net-10.0) (9) - Converting null literal or possible null value to non-nullable type

- **XREngine/Core/Engine/XRMeshBufferCollectionYamlTypeConverter.cs**
  - L44: Converting null literal or possible null value to non-nullable type.
- **XREngine/Core/FontGlyphSet.cs**
  - L60: Converting null literal or possible null value to non-nullable type.
- **XREngine/Core/Tools/Triangle Converter/TriStripper.cs**
  - L308: Converting null literal or possible null value to non-nullable type.
- **XREngine/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs**
  - L683: Converting null literal or possible null value to non-nullable type.
- **XREngine/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs**
  - L1127: Converting null literal or possible null value to non-nullable type.
- **XREngine/Rendering/API/Rendering/Vulkan/Objects/Types/VkTextureView.cs**
  - L231: Converting null literal or possible null value to non-nullable type.
  - L317: Converting null literal or possible null value to non-nullable type.
- **XREngine/Rendering/API/Rendering/Vulkan/VulkanShaderTools.cs**
  - L335: Converting null literal or possible null value to non-nullable type.
- **XREngine/Rendering/Pipelines/Commands/ViewportRenderCommandContainer.cs**
  - L139: Converting null literal or possible null value to non-nullable type.

#### [`CS8603`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8603?view=net-10.0) (5) - Possible null reference return

- **XREngine/Core/Tools/DelegateBuilder.cs**
  - L11: Possible null reference return.
- **XREngine/Core/Tools/Triangle Converter/TriStripper.cs**
  - L304: Possible null reference return.
  - L336: Possible null reference return.
  - L395: Possible null reference return.
  - L431: Possible null reference return.

#### [`CS8604`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8604?view=net-10.0) (4) - Possible null reference argument

- **XREngine/Core/FontGlyphSet.cs**
  - L79: Possible null reference argument for parameter 'outputAtlasPath' in 'void FontGlyphSet.GenerateFontAtlas(SKTypeface typeface, List<string> characters, string outputAtlasPath, float textSize, SKPaintStyle style = SKPaintStyle.Fill, float strokeWidth = 0, bool embolden = false)'.
- **XREngine/Rendering/API/Rendering/Vulkan/VulkanComputeDescriptors.cs**
  - L158: Possible null reference argument for parameter 'item' in 'void List<ComputeDescriptorPoolBlock>.Add(ComputeDescriptorPoolBlock item)'.
- **XREngine/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs**
  - L140: Possible null reference argument for parameter 'camera' in 'void HybridRenderingManager.Render(GPURenderPassCollection renderPasses, XRCamera camera, GPUScene scene, XRDataBuffer indirectDrawBuffer, XRMeshRenderer? vaoRenderer, int currentRenderPass, XRDataBuffer? parameterBuffer, IReadOnlyList<DrawBatch>? batches = null)'.
- **XREngine/Scene/Components/Landscape/LandscapeComponent.cs**
  - L1129: Possible null reference argument for parameter 'list' in 'Remapper? XRDataBuffer.SetDataRaw<GPUTerrainChunk>(IList<GPUTerrainChunk> list, bool remap = false)'.

#### [`CS8601`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8601?view=net-10.0) (3) - Possible null reference assignment

- **XREngine/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs**
  - L462: Possible null reference assignment.
  - L473: Possible null reference assignment.
- **XREngine/Rendering/API/Rendering/Vulkan/VulkanComputeDescriptors.cs**
  - L95: Possible null reference assignment.

### Medium Priority

#### [`CS0414`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0414?view=net-10.0) (21) - Compiler Warning (level 3) CS0414

- **XREngine/Core/Time/EngineTimer.cs**
  - L130: The field 'EngineTimer._cancelRenderTokenSource' is assigned but its value is never used
  - L134: The field 'EngineTimer.RenderTask' is assigned but its value is never used
  - L135: The field 'EngineTimer.SingleTask' is assigned but its value is never used
  - L138: The field 'EngineTimer.JobManagerTask' is assigned but its value is never used
- **XREngine/Engine/Engine.VRState.cs**
  - L1311: The field 'Engine.VRState._writer' is assigned but its value is never used
  - L1312: The field 'Engine.VRState._waitingForInput' is assigned but its value is never used
- **XREngine/Engine/Subclasses/Rendering/Engine.Rendering.Settings.cs**
  - L294: The field 'Engine.Rendering.EngineSettings._lightProbeDepthResolution' is assigned but its value is never used
- **XREngine/Models/Materials/Textures/TextureData.cs**
  - L15: The field 'TextureData._pixelType' is assigned but its value is never used
- **XREngine/Rendering/API/Rendering/Objects/XRRenderProgram.cs**
  - L344: The field 'XRRenderProgram.UniformSetBoolArrayRequested' is assigned but its value is never used
  - L345: The field 'XRRenderProgram.UniformSetBoolVector2ArrayRequested' is assigned but its value is never used
  - L346: The field 'XRRenderProgram.UniformSetBoolVector3ArrayRequested' is assigned but its value is never used
  - L347: The field 'XRRenderProgram.UniformSetBoolVector4ArrayRequested' is assigned but its value is never used
- **XREngine/Rendering/API/Rendering/Vulkan/Objects/SyncObjects.cs**
  - L21: The field 'VulkanRenderer._presentTimelineValue' is assigned but its value is never used
  - L22: The field 'VulkanRenderer._transferTimelineValue' is assigned but its value is never used
- **XREngine/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.cs**
  - L336: The field 'VulkanRenderer.VkMeshRenderer._meshDirty' is assigned but its value is never used
- **XREngine/Rendering/API/Rendering/Vulkan/VulkanAutoExposure.cs**
  - L13: The field 'VulkanRenderer._autoExposureTextureInitialized' is assigned but its value is never used
- **XREngine/Rendering/Commands/GPUScene.cs**
  - L269: The field 'GPUScene._commandUpdateLogBudget' is assigned but its value is never used
- **XREngine/Scene/Components/Animation/IK/Solvers/VR/IKSolverVR.cs**
  - L675: The field 'IKSolverVR._lastLocomotionWeight' is assigned but its value is never used
- **XREngine/Scene/Components/Audio/Converters/MicrophoneComponent.RVCConverter.cs**
  - L27: The field 'MicrophoneComponent.RVCConverter._isInitialized' is assigned but its value is never used
- **XREngine/Scene/Components/Mesh/RenderableMesh.cs**
  - L37: The field 'RenderableMesh._skinnedBvhDirty' is assigned but its value is never used
- **XREngine/Scene/Transforms/TransformBase.cs**
  - L560: The field 'TransformBase._inverseRenderMatrixDirty' is assigned but its value is never used

#### [`CS0169`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0169?view=net-10.0) (8) - Compiler Warning (level 3) CS0169

- **XREngine/Rendering/API/Rendering/OpenXR/OpenXRAPI.State.cs**
  - L246: The field 'OpenXRAPI._timerHooksInstalled' is never used
- **XREngine/Scene/Components/Animation/IK/Solvers/VR/IKSolverVR.cs**
  - L260: The field 'IKSolverVR._rootV' is never used
  - L261: The field 'IKSolverVR._rootVelocity' is never used
  - L789: The field 'IKSolverVR._headPosition' is never used
  - L790: The field 'IKSolverVR._headDeltaPosition' is never used
  - L792: The field 'IKSolverVR._lastOffset' is never used
- **XREngine/Scene/Components/Animation/IK/Solvers/VR/IKSolverVR.Locomotion.cs**
  - L130: The field 'IKSolverVR.Locomotion._rootVelocityV' is never used
  - L204: The field 'IKSolverVR.Locomotion._lastVelLocalMag' is never used

#### [`CS9193`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs9193?view=net-10.0) (5) - Argument should be a variable because it is passed to a ref readonly parameter

- **XREngine/Rendering/UI/Ultralight/OpenGLGPUDriver.cs**
  - L215: Argument 3 should be a variable because it is passed to a 'ref readonly' parameter
  - L216: Argument 3 should be a variable because it is passed to a 'ref readonly' parameter
  - L217: Argument 3 should be a variable because it is passed to a 'ref readonly' parameter
  - L218: Argument 3 should be a variable because it is passed to a 'ref readonly' parameter
  - L219: Argument 3 should be a variable because it is passed to a 'ref readonly' parameter

#### [`CS9192`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs9192?view=net-10.0) (4) - Argument should be passed with ref or in keyword

- **XREngine/Core/Files/DirectStorageIO.cs**
  - L384: Argument 1 should be passed with 'ref' or 'in' keyword
  - L617: Argument 1 should be passed with 'ref' or 'in' keyword
  - L704: Argument 1 should be passed with 'ref' or 'in' keyword
  - L808: Argument 1 should be passed with 'ref' or 'in' keyword

#### [`CS0652`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0652?view=net-10.0) (2) - Compiler Warning (level 2) CS0652

- **XREngine/Core/Files/DirectStorageIO.cs**
  - L571: Comparison to integral constant is useless; the constant is outside the range of type 'int'
  - L667: Comparison to integral constant is useless; the constant is outside the range of type 'int'

#### [`SYSLIB0050`](https://learn.microsoft.com/en-us/dotnet/fundamentals/syslib-diagnostics/syslib0050?view=net-10.0) (1) - Learn about the obsoletion of formatter-based serialization APIs that generates compile-time warning SYSLIB0050.

- **XREngine/Rendering/API/Rendering/Objects/Meshes/XRMesh.BufferCollection.cs**
  - L204: 'ISerializable.GetObjectData(SerializationInfo, StreamingContext)' is obsolete: 'Formatter-based serialization is obsolete and should not be used.' (https://aka.ms/dotnet-warnings/SYSLIB0050)

#### [`CS9191`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs9191?view=net-10.0) (1) - The ref modifier for argument corresponding to in parameter is equivalent to in

- **XREngine/Scene/Components/UI/UISvgComponent.cs**
  - L241: The 'ref' modifier for argument 2 corresponding to 'in' parameter is equivalent to 'in'. Consider using 'in' instead.

#### [`CS0618`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-messages/cs0618?view=net-10.0) (1) - Compiler Warning (level 2) CS0618

- **XREngine/Core/FontGlyphSet.cs**
  - L156: 'SKPaint.TextAlign' is obsolete: 'Use SKTextAlign method overloads instead.'

### Low Priority

#### [`CS0649`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0649?view=net-10.0) (10) - Compiler Warning (level 4) CS0649

- **XREngine/Rendering/Commands/GPURenderPassCollection.Core.cs**
  - L546: Field 'GPURenderPassCollection._truncationFlagBuffer' is never assigned to, and will always have its default value null
- **XREngine/Rendering/Commands/GPUScene.cs**
  - L90: Field 'GPUScene.MeshInfo.IndexCount' is never assigned to, and will always have its default value 0
  - L91: Field 'GPUScene.MeshInfo.FirstIndex' is never assigned to, and will always have its default value 0
  - L92: Field 'GPUScene.MeshInfo.BaseVertex' is never assigned to, and will always have its default value 0
- **XREngine/Rendering/Lights3DCollection.cs**
  - L52: Field 'Lights3DCollection._instancedCellRenderer' is never assigned to, and will always have its default value null
- **XREngine/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs**
  - L34: Field 'VPRC_TemporalAccumulationPass.TemporalState.LastFrameCount' is never assigned to, and will always have its default value 0
- **XREngine/Scene/Components/Animation/IK/Solvers/VR/IKSolverVR.cs**
  - L262: Field 'IKSolverVR._bodyOffset' is never assigned to, and will always have its default value
- **XREngine/Scene/Components/Mesh/RenderableMesh.cs**
  - L45: Field 'RenderableMesh._skinnedBvhVersion' is never assigned to, and will always have its default value 0
- **XREngine/Scene/Components/Physics/Joints/D6JointComponent.cs**
  - L44: Field 'D6JointComponent._distanceLimitRestitution' is never assigned to, and will always have its default value 0
  - L45: Field 'D6JointComponent._distanceLimitBounceThreshold' is never assigned to, and will always have its default value 0

#### [`CS0067`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0067?view=net-10.0) (1) - Compiler Warning (level 3) CS0067

- **XREngine/Scene/Components/UI/Interactable/UIComboBoxComponent.cs**
  - L9: The event 'UIComboBoxComponent.Scrolled' is never used

#### [`CS0162`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0162?view=net-10.0) (1) - Compiler Warning (level 2) CS0162

- **XREngine/Rendering/API/Rendering/Vulkan/MemoryCopyIndirect.cs**
  - L95: Unreachable code detected

---

## Project: XREngine.Editor
> 23 warning(s)

### High Priority

#### [`CS8604`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8604?view=net-10.0) (10) - Possible null reference argument

- **XREngine.Editor/ComponentEditors/CameraComponentEditor.cs**
  - L988: Possible null reference argument for parameter 'stageState' in 'void CameraComponentEditor.DrawSchemaStage(PostProcessStageDescriptor stage, PostProcessStageState stageState, CameraComponent component)'.
  - L988: Possible null reference argument for parameter 'stage' in 'void CameraComponentEditor.DrawSchemaStage(PostProcessStageDescriptor stage, PostProcessStageState stageState, CameraComponent component)'.
- **XREngine.Editor/EditorProjectInitializer.cs**
  - L73: Possible null reference argument for parameter 'repositoryRoot' in 'bool EditorProjectInitializer.TryResolveSamplesRoot(string repositoryRoot, out string? samplesRoot)'.
  - L74: Possible null reference argument for parameter 'root' in 'bool EditorProjectInitializer.IsPathInside(string path, string root)'.
- **XREngine.Editor/IMGUI/EditorImGuiUI.PropertyEditor.cs**
  - L4434: Possible null reference argument for parameter 'source' in 'TransformBase[] Enumerable.ToArray<TransformBase>(IEnumerable<TransformBase> source)'.
- **XREngine.Editor/UI/Tools/McpAssistantWindow.cs**
  - L6352: Possible null reference argument for parameter 'argumentsJson' in 'Task<string> McpAssistantWindow.ExecuteLocalFileSearchToolAsync(string argumentsJson, CancellationToken ct)'.
  - L6367: Possible null reference argument for parameter 'argumentsJson' in 'Task<string> McpAssistantWindow.ExecuteLocalApplyPatchToolAsync(string argumentsJson, CancellationToken ct)'.
  - L6382: Possible null reference argument for parameter 'argumentsJson' in 'void McpAssistantWindow.TryAutoFocusCameraForToolCall(string toolName, string argumentsJson, string toolResult)'.
  - L6408: Possible null reference argument for parameter 'json' in 'JsonNode? JsonNode.Parse(string json, JsonNodeOptions? nodeOptions = null, JsonDocumentOptions documentOptions = default(JsonDocumentOptions))'.
- **XREngine.Editor/UI/Tools/ShaderLockingTool.cs**
  - L295: Possible null reference argument for parameter 'value' in 'bool? ShaderLockingTool.EvaluateCondition(object value, string op, string compareValue)'.

#### [`CS8600`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8600?view=net-10.0) (2) - Converting null literal or possible null value to non-nullable type

- **XREngine.Editor/IMGUI/EditorImGuiUI.HierarchyPanel.cs**
  - L826: Converting null literal or possible null value to non-nullable type.
- **XREngine.Editor/IMGUI/EditorImGuiUI.ModelDropSpawn.cs**
  - L239: Converting null literal or possible null value to non-nullable type.

#### [`CS8602`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8602?view=net-10.0) (2) - Dereference of a possibly null reference

- **XREngine.Editor/ComponentEditors/ModelComponentEditor.cs**
  - L238: Dereference of a possibly null reference.
- **XREngine.Editor/Mcp/Actions/EditorMcpActions.Introspection.cs**
  - L685: Dereference of a possibly null reference.

#### [`CS8601`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8601?view=net-10.0) (1) - Possible null reference assignment

- **XREngine.Editor/IMGUI/EditorImGuiUI.AssetExplorerPanel.cs**
  - L1354: Possible null reference assignment.

### Medium Priority

#### [`CS0169`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0169?view=net-10.0) (1) - Compiler Warning (level 3) CS0169

- **XREngine.Editor/UI/UIEditorComponent.cs**
  - L80: The field 'UIEditorComponent._inspector' is never used

#### [`CS0414`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0414?view=net-10.0) (1) - Compiler Warning (level 3) CS0414

- **XREngine.Editor/UI/Tools/ShaderAnalyzerWindow.cs**
  - L29: The field 'ShaderAnalyzerWindow._selectedCategoryIndex' is assigned but its value is never used

#### [`CS0618`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-messages/cs0618?view=net-10.0) (1) - Compiler Warning (level 2) CS0618

- **XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Models.cs**
  - L98: 'TransformBase.AddChild(TransformBase, bool, bool)' is obsolete: 'Use AddChild(child, preserveWorld, EParentAssignmentMode) instead'

#### [`CS9191`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs9191?view=net-10.0) (1) - The ref modifier for argument corresponding to in parameter is equivalent to in

- **XREngine.Editor/IMGUI/EditorImGuiUI.Icons.cs**
  - L119: The 'ref' modifier for argument 2 corresponding to 'in' parameter is equivalent to 'in'. Consider using 'in' instead.

### Low Priority

#### [`CS0649`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0649?view=net-10.0) (2) - Compiler Warning (level 4) CS0649

- **XREngine.Editor/ComponentEditors/CameraComponentEditor.cs**
  - L1510: Field 'CameraComponentEditor.DebugViewState.SelectedPipelineIndex' is never assigned to, and will always have its default value 0
  - L1511: Field 'CameraComponentEditor.DebugViewState.SelectedFboName' is never assigned to, and will always have its default value null

#### [`CS1717`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs1717?view=net-10.0) (2) - Learn more about: Compiler Warning (level 3) CS1717

- **XREngine.Editor/ComponentEditors/GPULandscapeComponentEditor.cs**
  - L578: Assignment made to same variable; did you mean to assign something else?
  - L581: Assignment made to same variable; did you mean to assign something else?

---

## Project: XREngine.UnitTests
> 18 warning(s)

### High Priority

#### [`CS8604`](https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs8604?view=net-10.0) (3) - Possible null reference argument

- **XREngine.UnitTests/Mcp/McpServerHostProtocolTests.cs**
  - L140: Possible null reference argument for parameter 'actual' in 'void ShouldBeStringTestExtensions.ShouldContain(string actual, string expected, Case caseSensitivity = Case.Insensitive, string? customMessage = null)'.
- **XREngine.UnitTests/Rendering/NativeInteropSmokeTests.cs**
  - L18: Possible null reference argument for parameter 'libraryPath' in 'bool NativeLibrary.TryLoad(string libraryPath, out nint handle)'.
  - L45: Possible null reference argument for parameter 'libraryPath' in 'bool NativeLibrary.TryLoad(string libraryPath, out nint handle)'.

### Medium Priority

#### [`CS0219`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0219?view=net-10.0) (6) - Compiler Warning (level 3) CS0219

- **XREngine.UnitTests/Rendering/GpuCullingPipelineTests.cs**
  - L617: The variable 'screenWidth' is assigned but its value is never used
  - L618: The variable 'screenHeight' is assigned but its value is never used
- **XREngine.UnitTests/Rendering/GpuIndirectRenderDispatchTests.cs**
  - L249: The variable 'COMMAND_FLOATS' is assigned but its value is never used
  - L265: The variable 'submeshIdOffset' is assigned but its value is never used
  - L269: The variable 'shaderProgramIdOffset' is assigned but its value is never used
  - L272: The variable 'lodLevelOffset' is assigned but its value is never used

#### [`CS0618`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-messages/cs0618?view=net-10.0) (4) - Compiler Warning (level 2) CS0618

- **XREngine.UnitTests/Rendering/IndirectRenderingAdditionalTests.cs**
  - L84: 'BufferPNameARB.BufferSize' is obsolete: 'Deprecated in favour of "Size"'
  - L85: 'BufferPNameARB.BufferSize' is obsolete: 'Deprecated in favour of "Size"'
  - L108: 'BufferPNameARB.BufferSize' is obsolete: 'Deprecated in favour of "Size"'
  - L109: 'BufferPNameARB.BufferSize' is obsolete: 'Deprecated in favour of "Size"'

#### [`SYSLIB0050`](https://learn.microsoft.com/en-us/dotnet/fundamentals/syslib-diagnostics/syslib0050?view=net-10.0) (1) - Learn about the obsoletion of formatter-based serialization APIs that generates compile-time warning SYSLIB0050.

- **XREngine.UnitTests/Core/XRAssetMemoryPackCoverageTests.cs**
  - L43: 'FormatterServices' is obsolete: 'Formatter-based serialization is obsolete and should not be used.' (https://aka.ms/dotnet-warnings/SYSLIB0050)

### Low Priority

#### [`CS4014`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-messages/async-await-errors?view=net-10.0) (3) - These compiler errors and warnings indicate errors in the syntax for declaring and implementing `async` methods that use the `await` expression.

- **XREngine.UnitTests/Mcp/McpServerHostProtocolTests.cs**
  - L110: Because this call is not awaited, execution of the current method continues before the call is completed. Consider applying the 'await' operator to the result of the call.
  - L126: Because this call is not awaited, execution of the current method continues before the call is completed. Consider applying the 'await' operator to the result of the call.
  - L150: Because this call is not awaited, execution of the current method continues before the call is completed. Consider applying the 'await' operator to the result of the call.

#### [`CS0109`](https://learn.microsoft.com/en-us/dotnet/csharp/misc/cs0109?view=net-10.0) (1) - Compiler Warning (level 4) CS0109

- **XREngine.UnitTests/Rendering/GpuBvhAndIndirectIntegrationTests.cs**
  - L1608: The member 'GpuBvhAndIndirectIntegrationTests.CreateGLContext()' does not hide an accessible member. The new keyword is not required.

---

