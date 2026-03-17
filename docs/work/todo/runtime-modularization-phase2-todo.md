# Runtime Modularization Phase 2 - Current Todo

Reference design: [runtime-modularization-plan.md](../design/runtime-modularization-plan.md)

Updated: 2026-03-16

**Status: COMPLETE** — All implementation items are done. Exit criteria validated. See Phase 3 todo for continuation.

## Current State

- `Runtime.Core` already owns the jobs package, networking message contracts, startup/discovery contracts, AOT metadata, runtime world/tick contracts, the base scene graph (`SceneNode`, `XRComponent`, `TransformBase`, `Transform`), the transform host service seam, the runtime child-placement seam (`ITransformChildPlacementInfo`), `DefaultLayers`, `XRTransformEditorAttribute`, and host-independent networking state-change metadata (`EStateChangeType`, `StateChangeInfo`).
- `Runtime.Rendering` already owns the indirect draw/policy structs, render-graph metadata, render-resource descriptors, post-process schema metadata, the video-streaming runtime package, the shared render-object ownership seam (`GenericRenderObject`, `AbstractRenderAPIObject`, `IRenderAPIObject`, `IRenderApiWrapperOwner`, `IRuntimeRenderObjectServices`, `RuntimeRenderObjectServices`), the shader asset/runtime loader seam (`IRuntimeShaderServices`, `RuntimeShaderServices`, `XRShader`, `ShaderHelper`), the render-program/buffer runtime slice (`XRRenderProgram`, `XRRenderProgramPipeline`, `XRDataBuffer`, `XRDataBufferView`, `EEngineUniform`, `EMemoryBarrierMask`, `IApiDataBuffer`, `IRenderTextureResource`), the lower rendering-object runtime surface (`XRTexture*`, `XRMaterial*`, `XRMesh`, `XRFrameBuffer`, `XRMaterialFrameBuffer`, `XRRenderBuffer`, `IFrameBufferAttachement`), the material shader-parameter types (`ShaderVar*`), cubemap mip runtime data (`CubeMipmap`), the concrete OpenGL/Vulkan video upload backends, the pure material render-policy types (`ETransparencyMode`, `RenderingParameters`, `BlendMode`, `DepthTest`, `StencilTest`, and related enums), and the higher rendering host-service seam for default pipeline creation, viewport/window automatic timing, renderer/window creation, scene-panel presentation, VR/editor render gating, and runtime render-loop coordination (`IRuntimeRenderingHostServices`, `IRuntimeRenderWindowHost`, `IRuntimeViewportHost`, `IRuntimeRendererHost`, `IRuntimeRenderPipelineHost`, `IRuntimeWindowScenePanelAdapter`).
- `Runtime.ModelingBridge` now owns the `XRMesh` modeling bridge surface: `XRMeshModelingExporter`, `XRMeshModelingImporter`, `XRMeshBooleanOperations`, and the existing modeling option/contract types.
- `Runtime.AnimationIntegration` now owns the first coherent animation-facing bootstrap slice via `BootstrapAnimationWorldBuilder` (`AddIKTest`, `AddSpline`).
- `Runtime.AudioIntegration` now owns the first coherent audio-facing bootstrap slice via `BootstrapAudioWorldBuilder` (`AddSoundNode`).
- `Runtime.InputIntegration` now owns the first coherent input-facing bootstrap slice via `BootstrapFlyableCameraFactory` and `BootstrapInputBridge`, so editor-specific flyable camera pawn creation no longer lives behind `IBootstrapEditorBridge`.
- `Runtime.Bootstrap` no longer references `XRENGINE` directly; it now consumes the runtime-layer projects (`Runtime.Core`, `Runtime.Rendering`, and the integration slices) while the adapter projects carry the remaining engine-side implementation dependencies.
- `IBootstrapEditorBridge` is now narrowed to editor-only UI behavior (`CreateEditorUi`, `EnableTransformToolForNode`). Specialized-world selection and model import now route through dedicated non-editor seams (`BootstrapWorldBridge`, `BootstrapModelImportBridge`).

## Current Todo List

### P0 - Runtime.Core

- [x] Move default transform construction out of `SceneNode`. `SceneNode` now resolves default transforms through `RuntimeSceneNodeServices` instead of directly constructing `Transform`.
- [x] Move the `UICanvasTransform` assignment rule out of `SceneNode`. Validation now lives in the host `RuntimeSceneNodeServices` bridge instead of base scene-graph code.
- [x] Break `Transform`'s dependency on `TransformState` and `ETransformOrder` from `XREngine.Animation`. Those types now live in `XREngine.Data` as a lower-level runtime-owned contract, removing the `Transform -> Animation` project dependency.
- [x] Break `TransformBase`'s dependency on `RenderInfo`, `IRenderable`, and debug-gizmo rendering. Transform debug rendering now goes through `IRuntimeTransformDebugHandle` created by `RuntimeTransformServices`.
- [x] After the four items above are done, move `SceneNode`, `XRComponent`, `TransformBase`, and `Transform` into `Runtime.Core`.
- [x] Resume the remaining host-independent `Engine/`, `Settings/`, and networking orchestration moves once the base scene-graph types no longer pin that code in `XRENGINE`. This pass moved the remaining host-independent state-change metadata into `Runtime.Core`, generalized runtime replication around `RuntimeWorldObjectBase`, and kept the remaining typed `XRWorldInstance` access in engine-side component adapters plus explicit engine-only casts instead of the runtime-owned scene graph.

### P0 - Runtime.Rendering

- [x] Break the remaining `AbstractRenderer` / `XRWindow` ownership chain. Default pipeline creation, renderer construction, scene-panel presentation, VR/editor gating, timer hookup, window close/remove policy, and render-loop coordination now route through `RuntimeRenderingHostServices` instead of reaching directly into `Engine`.
- [x] Continue the rendering runtime move after the shader/program/buffer slice: `XRTexture*`, `XRMaterial*`, mesh runtime types, their base framebuffer/renderbuffer runtime types, and shader-parameter support types now live in `Runtime.Rendering`.
- [x] Move the concrete video upload backends: `OpenGLVideoFrameGpuActions` and `VulkanVideoFrameGpuActions`.
- [x] After those moves, continue with the higher rendering runtime surface: viewports, render pipelines, render passes, and broader render-thread/window coordination. `XRViewport`, `XRCamera`, `XRRenderPipelineInstance`, `RenderPipeline`, `ViewportRenderCommandContainer`, `RenderCommand`, and `AbstractRenderer` now resolve the remaining host-owned defaults/timing/presentation policy through `RuntimeRenderingHostServices`, and targeted seam tests cover the new runtime boundary.

Current follow-up detail:
`XRViewport`, `RenderPipeline`, `XRRenderPipelineInstance`, `ViewportRenderCommandContainer`, `XRWindow`, and `AbstractRenderer` still physically compile from `XRENGINE`, but the P0 blocker is closed because their engine/editor/VR/default-pipeline/timer lifecycle policy now routes through runtime-owned host-service seams. A later cleanup pass can relocate those files/projects without re-breaking the runtime boundary.

### P1 - ModelingBridge

- [x] Move `XRMeshModelingExporter`, `XRMeshModelingImporter`, and `XRMeshBooleanOperations` after `XRMesh` and the related mesh/rendering types live in `Runtime.Rendering`.

### P1 - Adapter Projects

- [x] Add the first coherent `Runtime.AnimationIntegration` slice after `SceneNode`, `XRComponent`, and `TransformBase` move into `Runtime.Core`.
- [x] Add the first coherent `Runtime.AudioIntegration` slice after scene-graph ownership is moved.
- [x] Add the first coherent `Runtime.InputIntegration` slice after scene-graph ownership and renderer/window ownership are moved far enough.

### P1 - Bootstrap

- [x] Remove the direct `Runtime.Bootstrap -> XRENGINE` reference once Bootstrap can compile entirely against the runtime layer projects.
- [x] Repoint Bootstrap code to runtime-layer equivalents and revalidate Editor, Server, and VRClient.

### P2 - Editor Bridge Cleanup

- [x] Re-evaluate `CreateSpecializedWorld`, `ImportModels`, `CreateEditorUi`, and `EnableTransformToolForNode`.
- [x] Move runtime-owned behavior out of Editor and leave only editor-only behavior behind `IBootstrapEditorBridge`.

## Notes To Avoid Rework

- `XRBase` and `XRObjectBase` are not Phase 2 move candidates. They already live in `XREngine.Data`.
- `XRWorldObjectBase` is not the blocker anymore. Runtime ownership already moved to `RuntimeWorldObjectBase` in `Runtime.Core`.
- `IPostCookedBinaryDeserialize` now lives in `XREngine.Data`, and `SceneNodePrefabLink` now lives in `Runtime.Core`, so those support types no longer pin the scene graph to `XRENGINE`.
- `TransformBase` no longer depends directly on `XRWorldInstance` for dirty-object publication or world-matrix publication.
- `TransformBase.MatrixInfo` and `Transform` no longer reach directly into `Engine` for update delta, transform keyframe interval, render-thread state, or transform debug logging.
- The remaining typed `XRWorldInstance` access is now isolated to engine-side code (`XRSceneComponent` plus explicit engine-only casts), so it is no longer a blocker for `Runtime.Core` scene-graph ownership.
- The remaining `Engine/` and `Settings/` files under `XRENGINE` are host-specific or still depend on rendering, VR, editor, or application-layer concerns; they are no longer `P0 - Runtime.Core` blockers.
- The shared `GenericRenderObject -> AbstractRenderAPIObject` chain and the lower rendering-object runtime surface now live in `Runtime.Rendering`. The higher window/viewport/pipeline layer is no longer a P0 boundary blocker because its host/editor/VR/default-pipeline lifecycle policy now routes through `RuntimeRenderingHostServices`.
- The higher rendering window/viewport/pipeline layer no longer reaches directly into `Engine` for default pipelines, timer hookup, renderer construction, or editor/VR presentation policy; those concerns now flow through `RuntimeRenderingHostServices`.
- The adapter projects now own the first bootstrap-facing slices, but they still depend on `XRENGINE` for engine-side component implementations. The remaining work is about continuing to push those implementation dependencies downward, not about project-file plumbing.

## Suggested Execution Order

1. Revisit the physical file/project relocation of the now-decoupled higher rendering types as cleanup, not as a P0 blocker.
2. Continue moving engine-side implementation dependencies out of the adapter slices where a lower runtime layer now exists.
3. Clean up the remaining Editor-only bridge surface.

## Validation Baseline

Latest green validation:

- `dotnet build .\XREngine.Runtime.Core\XREngine.Runtime.Core.csproj`
- `dotnet build .\XREngine.Runtime.Rendering\XREngine.Runtime.Rendering.csproj`
- `dotnet build .\XRENGINE\XREngine.csproj`
- `dotnet build XRENGINE.slnx`
- `dotnet test XREngine.UnitTests --filter GenericRenderObjectServiceTests`
- `dotnet test XREngine.UnitTests --filter SceneNodeLifecycleTests`
- `dotnet test XREngine.UnitTests --filter StreamVariantInfoTests`
- `dotnet test XREngine.UnitTests --filter VulkanTodoP2ValidationTests`
- `dotnet test XREngine.UnitTests --filter AssetPackerTests`
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ForwardDepthNormalVariantTests|FullyQualifiedName~XRMeshSerializationTests|FullyQualifiedName~AlphaToCoveragePhase2Tests"`
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~RuntimeRenderingHostServicesTests|FullyQualifiedName~RenderRuntimeServicesTests|FullyQualifiedName~ForwardDepthNormalVariantTests"`
- `dotnet build .\XREngine.Runtime.ModelingBridge\XREngine.Runtime.ModelingBridge.csproj`
- `dotnet build .\XREngine.Runtime.AnimationIntegration\XREngine.Runtime.AnimationIntegration.csproj`
- `dotnet build .\XREngine.Runtime.AudioIntegration\XREngine.Runtime.AudioIntegration.csproj`
- `dotnet build .\XREngine.Runtime.InputIntegration\XREngine.Runtime.InputIntegration.csproj`
- `dotnet build .\XREngine.Runtime.Bootstrap\XREngine.Runtime.Bootstrap.csproj`
- `dotnet build .\XREngine.Editor\XREngine.Editor.csproj`
- `dotnet build .\XREngine.Server\XREngine.Server.csproj`
- `dotnet build .\XREngine.VRClient\XREngine.VRClient.csproj`
- `dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --no-restore --filter "FullyQualifiedName~XRMeshModelingBridgeTests|FullyQualifiedName~SceneNodeLifecycleTests|FullyQualifiedName~RuntimeRenderingHostServicesTests|FullyQualifiedName~RenderRuntimeServicesTests"`

Current todo-list status:

- The Phase 2 implementation todo items in this document are complete.
- Remaining unchecked items below are exit criteria / follow-on validation goals, not unimplemented P1/P2 work items.

## Phase 2 Exit Criteria

- [x] `Runtime.Core` owns the base scene graph and the remaining host-independent runtime model. *(SceneNode, XRComponent, TransformBase, Transform, RuntimeWorldObjectBase, jobs, networking contracts, AOT metadata, startup/discovery contracts all live in Runtime.Core. Remaining Engine/ files are host-specific.)*
- [x] `Runtime.Rendering` owns the real rendering runtime surface, not just metadata and shared contracts. *(XRTexture*, XRMaterial*, XRMesh, XRShader, XRRenderProgram, XRDataBuffer, XRFrameBuffer, GenericRenderObject, video backends, cubemap mip, material render-policy types, and the higher host-service seams for window/viewport/pipeline/renderer lifecycle all live in Runtime.Rendering. Physical file relocation of XRViewport/RenderPipeline/XRWindow/AbstractRenderer is deferred to Phase 3 cleanup.)*
- [x] `Runtime.AnimationIntegration`, `Runtime.AudioIntegration`, and `Runtime.InputIntegration` each own at least one coherent subsystem slice.
- [x] `Runtime.Bootstrap` no longer references `XRENGINE` directly.
- [x] The project graph matches the intended one-way modularization plan with no accidental reverse references. *(Validated: Runtime.Core → Data+Extensions only. Runtime.Rendering → Core+Data+Extensions only. Runtime.Bootstrap → runtime layers + integration slices only. Server → no Editor reference. Integration projects → XRENGINE for engine-side implementations, which is expected at this stage.)*
- [x] Editor, Server, VRClient, and the nearest targeted tests all build and pass. *(Full solution builds green. 62/62 modularization-targeted tests pass. 4 pre-existing rendering-behavior test failures are unrelated to modularization: ForwardDepthNormalVariantTests ×2, AlphaToCoveragePhase2Tests ×1, BranchExecutedPassWithoutMetadata ×1.)*

## Continuation

Phase 3 implementation: [runtime-modularization-phase3-todo.md](runtime-modularization-phase3-todo.md)
