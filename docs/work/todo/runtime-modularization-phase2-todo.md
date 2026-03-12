# Runtime Modularization Phase 2 - Current Todo

Reference design: [runtime-modularization-plan.md](../design/runtime-modularization-plan.md)

Updated: 2026-03-12

## Current State

- `Runtime.Core` already owns the jobs package, networking message contracts, startup/discovery contracts, AOT metadata, runtime world/tick contracts, the transform host service seam, `DefaultLayers`, and `XRTransformEditorAttribute`.
- `Runtime.Rendering` already owns the indirect draw/policy structs, render-graph metadata, render-resource descriptors, post-process schema metadata, the video-streaming runtime package, the shared render-object ownership seam (`GenericRenderObject`, `AbstractRenderAPIObject`, `IRenderAPIObject`, `IRenderApiWrapperOwner`, `IRuntimeRenderObjectServices`, `RuntimeRenderObjectServices`), the shader asset/runtime loader seam (`IRuntimeShaderServices`, `RuntimeShaderServices`, `XRShader`, `ShaderHelper`), the render-program/buffer runtime slice (`XRRenderProgram`, `XRRenderProgramPipeline`, `XRDataBuffer`, `XRDataBufferView`, `EEngineUniform`, `EMemoryBarrierMask`, `IApiDataBuffer`, `IRenderTextureResource`), and the pure material render-policy types (`ETransparencyMode`, `RenderingParameters`, `BlendMode`, `DepthTest`, `StencilTest`, and related enums).
- `Runtime.ModelingBridge` currently owns only modeling option/contract types.
- `Runtime.AnimationIntegration`, `Runtime.AudioIntegration`, and `Runtime.InputIntegration` still do not own a real subsystem slice.
- `Runtime.Bootstrap` still references `XRENGINE` directly, so the project graph is not at the final Phase 2 target yet.

## Current Todo List

### P0 - Runtime.Core

- [x] Move default transform construction out of `SceneNode`. `SceneNode` now resolves default transforms through `RuntimeSceneNodeServices` instead of directly constructing `Transform`.
- [x] Move the `UICanvasTransform` assignment rule out of `SceneNode`. Validation now lives in the host `RuntimeSceneNodeServices` bridge instead of base scene-graph code.
- [x] Break `Transform`'s dependency on `TransformState` and `ETransformOrder` from `XREngine.Animation`. Those types now live in `XREngine.Data` as a lower-level runtime-owned contract, removing the `Transform -> Animation` project dependency.
- [x] Break `TransformBase`'s dependency on `RenderInfo`, `IRenderable`, and debug-gizmo rendering. Transform debug rendering now goes through `IRuntimeTransformDebugHandle` created by `RuntimeTransformServices`.
- [ ] After the four items above are done, move `SceneNode`, `XRComponent`, `TransformBase`, and `Transform` into `Runtime.Core`.
- [ ] Resume the remaining host-independent `Engine/`, `Settings/`, and networking orchestration moves once the base scene-graph types no longer pin that code in `XRENGINE`.

### P0 - Runtime.Rendering

- [ ] Break the remaining `AbstractRenderer` / `XRWindow` ownership chain. Concrete wrapper creation, renderer caches, and window lifecycle are still coupled together in `XRENGINE`.
- [ ] Continue the rendering runtime move after the shader/program/buffer slice: `XRTexture*`, `XRMaterial*`, and mesh runtime types still live in `XRENGINE`.
- [ ] Move the concrete video upload backends: `OpenGLVideoFrameGpuActions` and `VulkanVideoFrameGpuActions`.
- [ ] After those moves, continue with the higher rendering runtime surface: viewports, render pipelines, render passes, and broader render-thread/window coordination.

### P1 - ModelingBridge

- [ ] Move `XRMeshModelingExporter`, `XRMeshModelingImporter`, and `XRMeshBooleanOperations` after `XRMesh` and the related mesh/rendering types live in `Runtime.Rendering`.

### P1 - Adapter Projects

- [ ] Add the first coherent `Runtime.AnimationIntegration` slice after `SceneNode`, `XRComponent`, and `TransformBase` move into `Runtime.Core`.
- [ ] Add the first coherent `Runtime.AudioIntegration` slice after scene-graph ownership is moved.
- [ ] Add the first coherent `Runtime.InputIntegration` slice after scene-graph ownership and renderer/window ownership are moved far enough.

### P1 - Bootstrap

- [ ] Remove the direct `Runtime.Bootstrap -> XRENGINE` reference once Bootstrap can compile entirely against the runtime layer projects.
- [ ] Repoint Bootstrap code to runtime-layer equivalents and revalidate Editor, Server, and VRClient.

### P2 - Editor Bridge Cleanup

- [ ] Re-evaluate `CreateSpecializedWorld`, `ImportModels`, `CreateEditorUi`, `EnableTransformToolForNode`, and `CreateFlyableCameraPawn`.
- [ ] Move runtime-owned behavior out of Editor and leave only editor-only behavior behind `IBootstrapEditorBridge`.

## Notes To Avoid Rework

- `XRBase` and `XRObjectBase` are not Phase 2 move candidates. They already live in `XREngine.Data`.
- `XRWorldObjectBase` is not the blocker anymore. Runtime ownership already moved to `RuntimeWorldObjectBase` in `Runtime.Core`.
- `IPostCookedBinaryDeserialize` now lives in `XREngine.Data`, and `SceneNodePrefabLink` now lives in `Runtime.Core`, so those support types no longer pin the scene graph to `XRENGINE`.
- `TransformBase` no longer depends directly on `XRWorldInstance` for dirty-object publication or world-matrix publication.
- `TransformBase.MatrixInfo` and `Transform` no longer reach directly into `Engine` for update delta, transform keyframe interval, render-thread state, or transform debug logging.
- The remaining scene-graph move blocker is the typed `XRWorldInstance` world surface still relied on by many `XRComponent` / `TransformBase` subclasses across rendering, physics, audio, and capture code.
- The shared `GenericRenderObject -> AbstractRenderAPIObject` chain already lives in `Runtime.Rendering`. The current rendering blocker is the higher `AbstractRenderer` / `XRWindow` layer.
- The adapter projects are blocked by ownership boundaries, not by project-file setup.

## Suggested Execution Order

1. Finish the remaining `Runtime.Core` scene-graph blockers.
2. Finish the remaining `Runtime.Rendering` renderer/window ownership blockers.
3. Move rendering-heavy mesh/material/shader/runtime types.
4. Unblock `ModelingBridge` heavy implementations.
5. Start populating `AnimationIntegration`, `AudioIntegration`, and `InputIntegration`.
6. Remove the direct Bootstrap reference to `XRENGINE`.
7. Clean up the remaining Editor-only bridge surface.

## Validation Baseline

Latest green validation before this todo rewrite:

- `dotnet build .\XREngine.Runtime.Core\XREngine.Runtime.Core.csproj`
- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
- `dotnet build .\XRENGINE\XREngine.csproj`
- `dotnet build XRENGINE.slnx`
- `dotnet test XREngine.UnitTests --filter GenericRenderObjectServiceTests`
- `dotnet test XREngine.UnitTests --filter SceneNodeLifecycleTests`
- `dotnet test XREngine.UnitTests --filter StreamVariantInfoTests`
- `dotnet test XREngine.UnitTests --filter VulkanTodoP2ValidationTests`
- `dotnet test XREngine.UnitTests --filter AssetPackerTests`

## Phase 2 Exit Criteria

- [ ] `Runtime.Core` owns the base scene graph and the remaining host-independent runtime model.
- [ ] `Runtime.Rendering` owns the real rendering runtime surface, not just metadata and shared contracts.
- [ ] `Runtime.AnimationIntegration`, `Runtime.AudioIntegration`, and `Runtime.InputIntegration` each own at least one coherent subsystem slice.
- [ ] `Runtime.Bootstrap` no longer references `XRENGINE` directly.
- [ ] The project graph matches the intended one-way modularization plan with no accidental reverse references.
- [ ] Editor, Server, VRClient, and the nearest targeted tests all build and pass.
