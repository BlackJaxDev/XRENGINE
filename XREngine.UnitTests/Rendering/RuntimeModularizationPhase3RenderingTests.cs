using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeModularizationPhase3RenderingTests
{
    [Test]
    public void P4_2_RuntimeUiAndInputRouting_HaveFinalAssemblyOwners()
    {
        typeof(UIComponent).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Rendering");
        typeof(UICanvasComponent).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Rendering");
        typeof(UICanvasInputComponent).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.InputIntegration");
        typeof(FlyingCameraPawnComponent).Assembly.GetName().Name.ShouldBe("XREngine.Runtime.InputIntegration");
        typeof(UICanvasInputComponent).GetInterfaces().ShouldContain(typeof(IUICanvasInputSource));
        Type.GetType("XREngine.Components.UICanvasComponent, XREngine").ShouldBe(typeof(UICanvasComponent));
        Type.GetType("XREngine.Components.UICanvasInputComponent, XREngine").ShouldBe(typeof(UICanvasInputComponent));
        Type.GetType("XREngine.Components.FlyingCameraPawnComponent, XREngine").ShouldBe(typeof(FlyingCameraPawnComponent));
        Type.GetType("XREngine.Rendering.UI.UIComponent, XREngine").ShouldBe(typeof(UIComponent));

        string root = ResolveWorkspaceRoot();
        Directory.Exists(Path.Combine(root, "XRENGINE", "Scene", "Components", "UI")).ShouldBeFalse();
        File.Exists(Path.Combine(root, "XRENGINE", "Scene", "Components", "Pawns", "UICanvasComponent.cs")).ShouldBeFalse();
        File.Exists(Path.Combine(root, "XRENGINE", "Core", "FontGlyphSet.cs")).ShouldBeFalse();

        string renderingProject = ReadWorkspaceFile("XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj");
        renderingProject.ShouldNotContain("XREngine.Runtime.InputIntegration.csproj");

        string canvasSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/UI/UICanvasComponent.cs");
        canvasSource.ShouldContain("IUICanvasInputSource? GetInputComponent()");
        canvasSource.ShouldNotContain("UICanvasInputComponent? GetInputComponent()");
    }

    [Test]
    public void P2a_RenderCommandOwnerDebugContext_SourceContracts_ArePresent()
    {
        string runtimeHostSource = ReadRuntimeRenderingContracts();
        string collectionSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs");
        string gpuPassSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Core.cs");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");

        runtimeHostSource.ShouldContain("public interface IRuntimeRenderPipelineDebugContext");
        runtimeHostSource.ShouldContain("string DebugName { get; }");
        runtimeHostSource.ShouldContain("string DebugDescriptor { get; }");

        collectionSource.ShouldContain("private IRuntimeRenderPipelineDebugContext? _ownerPipeline;");
        collectionSource.ShouldContain("internal void SetOwnerPipeline(IRuntimeRenderPipelineDebugContext pipeline)");
        collectionSource.ShouldNotContain("private XRRenderPipelineInstance? _ownerPipeline;");

        gpuPassSource.ShouldContain("private IRuntimeRenderPipelineDebugContext? _ownerPipeline;");
        gpuPassSource.ShouldContain("internal void SetDebugContext(IRuntimeRenderPipelineDebugContext? pipeline, int passIndex)");
        gpuPassSource.ShouldNotContain("internal void SetDebugContext(XRRenderPipelineInstance? pipeline, int passIndex)");

        pipelineSource.ShouldContain("public sealed partial class XRRenderPipelineInstance : XRBase, IRuntimeRenderPipelineDebugContext");
        pipelineSource.ShouldContain("public string DebugName");
    }

    [Test]
    public void P2a_RenderCommandGpuStateSceneContracts_SourceContracts_ArePresent()
    {
        string runtimeHostSource = ReadRuntimeRenderingContracts();
        string gpuViewSetSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Commands/GPUViewSet.cs");
        string collectionSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs");
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs");
        string sceneSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/VisualScene.cs");
        string scene3DSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/VisualScene3D.cs");
        string gpuPassCoreSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Core.cs");
        string gpuPassViewSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.ViewSet.cs");

        runtimeHostSource.ShouldContain("public interface IRuntimeGpuRenderPassHost");
        runtimeHostSource.ShouldContain("public interface IRuntimeRenderCommandSceneContext");
        runtimeHostSource.ShouldContain("public interface IRuntimeRenderCamera");
        runtimeHostSource.ShouldContain("public interface IRuntimeRenderCommandExecutionState");
        runtimeHostSource.ShouldContain("IRuntimeRenderCommandExecutionState? ActiveRenderCommandExecutionState { get; }");

        gpuViewSetSource.ShouldContain("public struct GPUViewDescriptor");
        gpuViewSetSource.ShouldContain("public struct GPUViewConstants");
        gpuViewSetSource.ShouldContain("public static class GPUViewSetLayout");

        collectionSource.ShouldContain("IRuntimeRenderCommandExecutionState? renderState = RuntimeRenderingHostServices.FrameTiming.ActiveRenderCommandExecutionState;");
        collectionSource.ShouldContain("scene.RenderGpuPass(gpuPass);");
        collectionSource.ShouldContain("scene.RecordGpuVisibility(draws, instances);");
        collectionSource.ShouldContain("private static RenderFrameViewSet ConfigureGpuViewSet(GPURenderPassCollection gpuPass, IRuntimeRenderCommandExecutionState renderState, IRuntimeRenderCamera leftCamera)");

        stateSource.ShouldContain("public class RenderingState : IRuntimeRenderCommandExecutionState");
        stateSource.ShouldContain("IRuntimeRenderCommandSceneContext? IRuntimeRenderCommandExecutionState.RenderingScene");
        stateSource.ShouldContain("IRuntimeRenderCamera? IRuntimeRenderCommandExecutionState.RenderingCamera");

        sceneSource.ShouldContain("public abstract class VisualScene : XRBase, IEnumerable<RenderInfo>, IRuntimeRenderCommandSceneContext");
        sceneSource.ShouldContain("public virtual void RenderGpuPass(IRuntimeGpuRenderPassHost gpuPass)");

        scene3DSource.ShouldContain("public override void RecordGpuVisibility(uint draws, uint instances)");

        gpuPassCoreSource.ShouldContain("public sealed partial class GPURenderPassCollection : IRuntimeGpuRenderPassHost");
        gpuPassViewSource.ShouldContain("public void SetIndirectSourceViewId(uint viewId)");

        string cameraSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Camera/XRCamera.cs");
        cameraSource.ShouldContain("public class XRCamera : XRBase, IRuntimeRenderCamera");
        cameraSource.ShouldContain("public bool? StereoEyeLeft");
    }

    [Test]
    public void RuntimeTimingFacade_SeparatesUpdateAndRenderDeltaContracts()
    {
        string runtimeHostSource = ReadRuntimeRenderingContracts();
        string runtimeEngineTimerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngineTimer.cs");
        string runtimeTimerFrameSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeTimerFrame.cs");
        string engineHostSource = ReadWorkspaceFile("XRENGINE/Engine/Engine.RuntimeRenderingHostServices.cs");
        string skyboxSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Misc/SkyboxComponent.cs");

        runtimeHostSource.ShouldContain("double UpdateDeltaSeconds { get; }");
        runtimeHostSource.ShouldContain("long LastUpdateTimestampTicks { get; }");
        runtimeHostSource.ShouldContain("double RenderDeltaSeconds { get; }");
        runtimeHostSource.ShouldContain("long LastRenderTimestampTicks { get; }");

        engineHostSource.ShouldContain("public double UpdateDeltaSeconds => Engine.Time.Timer.Update.Delta;");
        engineHostSource.ShouldContain("public long LastUpdateTimestampTicks => Engine.Time.Timer.Update.LastTimestampTicks;");
        engineHostSource.ShouldContain("public double RenderDeltaSeconds => Engine.Time.Timer.Render.Delta;");

        runtimeEngineTimerSource.ShouldContain("public RuntimeTimerFrame Update { get; } = new(ERuntimeTimerFrameKind.Update);");
        runtimeEngineTimerSource.ShouldContain("public RuntimeTimerFrame Render { get; } = new(ERuntimeTimerFrameKind.Render);");
        runtimeTimerFrameSource.ShouldContain("RuntimeRenderingHostServices.FrameTiming.UpdateDeltaSeconds");
        runtimeTimerFrameSource.ShouldContain("RuntimeRenderingHostServices.FrameTiming.RenderDeltaSeconds");

        skyboxSource.ShouldContain("Engine.Time.Timer.Update.Delta");
        skyboxSource.ShouldNotContain("float dt = Math.Max(0.0f, Engine.Delta);");
    }

    [Test]
    public void P2a_PipelineFrameAndScreenSpaceUiContracts_SourceContracts_ArePresent()
    {
        string runtimeHostSource = ReadRuntimeRenderingContracts();
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");
        string stateSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs");
        string cameraSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Camera/XRCamera.cs");
        string uiCanvasSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/UI/UICanvasComponent.cs");

        runtimeHostSource.ShouldContain("public interface IRuntimeRenderPipelineFrameContext");
        runtimeHostSource.ShouldContain("public interface IRuntimeScreenSpaceUserInterface");
        runtimeHostSource.ShouldContain("IRuntimeRenderPipelineFrameContext? CurrentRenderPipelineContext { get; }");
        runtimeHostSource.ShouldContain("IDisposable? PushRenderingPipeline(IRuntimeRenderPipelineFrameContext pipeline);");
        runtimeHostSource.ShouldContain("void PrepareUpscaleBridgeForFrame(IRuntimeViewportHost viewport, IRuntimeRenderPipelineFrameContext pipeline);");

        pipelineSource.ShouldContain("IRuntimeRenderCommandExecutionState IRuntimeRenderPipelineFrameContext.RenderState => RenderState;");
        pipelineSource.ShouldContain("IRuntimeScreenSpaceUserInterface? userInterface = null");
        pipelineSource.ShouldContain("using (RuntimeRenderingHostServices.Diagnostics.PushRenderingPipeline(this))");

        stateSource.ShouldContain("public IRuntimeScreenSpaceUserInterface? ScreenSpaceUserInterface");
        stateSource.ShouldContain("ScreenSpaceUserInterface = screenSpaceUI?.IsScreenSpace == true ? screenSpaceUI : null;");

        cameraSource.ShouldContain("IRuntimeRenderPipelineFrameContext? currentPipeline = RuntimeRenderingHostServices.FrameTiming.CurrentRenderPipelineContext;");

        uiCanvasSource.ShouldContain("IRuntimeScreenSpaceUserInterface");
        uiCanvasSource.ShouldContain("bool IRuntimeScreenSpaceUserInterface.IsScreenSpace =>");
    }

    [Test]
    public void P2a_RenderInfoRenderCommandCameraCallbacks_SourceContracts_ArePresent()
    {
        string runtimeHostSource = ReadRuntimeRenderingContracts();
        string renderInfoSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Info/RenderInfo.cs");
        string renderInfo2DSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Info/RenderInfo2D.cs");
        string renderInfo3DSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Info/RenderInfo3D.cs");
        string renderCommandSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommand.cs");
        string renderCommand3DSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommand3D.cs");
        string renderCommandMesh3DSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandMesh3D.cs");
        string cameraSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Camera/XRCamera.cs");

        runtimeHostSource.ShouldContain("bool RendersLayer(int layer);");
        runtimeHostSource.ShouldContain("float DistanceFromRenderNearPlane(Vector3 point);");

        renderInfoSource.ShouldContain("public delegate void DelPreRenderCallback(RenderInfo info, RenderCommand command, IRuntimeRenderCamera? camera);");
        renderInfoSource.ShouldContain("public delegate bool DelAddRenderCommandsCallback(RenderInfo info, RenderCommandCollection passes, IRuntimeRenderCamera? camera);");
        renderInfoSource.ShouldContain("public void CollectCommands(RenderCommandCollection passes, IRuntimeRenderCamera? camera)");

        renderInfo2DSource.ShouldContain("public virtual bool AllowRender(BoundingRectangleF? cullingVolume, RenderCommandCollection passes, IRuntimeCullingCamera? camera)");
        renderInfo3DSource.ShouldContain("IRuntimeCullingCamera? camera,");
        renderInfo3DSource.ShouldContain("(camera is null || camera.RendersLayer(_layer))");

        renderCommandSource.ShouldContain("public delegate void DelPreRender(RenderCommand command, IRuntimeRenderCamera? camera);");
        renderCommandSource.ShouldContain("public virtual void CollectedForRender(IRuntimeRenderCamera? camera)");
        renderCommand3DSource.ShouldContain("public void UpdateRenderDistance(Vector3 thisWorldPosition, IRuntimeRenderCamera camera)");
        renderCommandMesh3DSource.ShouldContain("public override void CollectedForRender(IRuntimeRenderCamera? camera)");

        cameraSource.ShouldContain("public bool RendersLayer(int layer)");
        cameraSource.ShouldContain("public float DistanceFromRenderNearPlane(Vector3 point)");
    }

    [Test]
    public void P2a_VisualSceneCullingCameraContracts_SourceContracts_ArePresent()
    {
        string runtimeHostSource = ReadRuntimeRenderingContracts();
        string sceneSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/VisualScene.cs");
        string scene2DSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/VisualScene2D.cs");
        string scene3DSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/VisualScene3D.cs");
        string renderInfo2DSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Info/RenderInfo2D.cs");
        string renderInfo3DSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Info/RenderInfo3D.cs");
        string cameraComponentSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Camera/CameraComponent.cs");
        string cameraSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Camera/XRCamera.cs");

        runtimeHostSource.ShouldContain("public interface IRuntimeCullingCamera : IRuntimeRenderCamera");
        runtimeHostSource.ShouldContain("Frustum WorldFrustum();");
        runtimeHostSource.ShouldContain("BoundingRectangleF? GetOrthoCameraBounds();");

        sceneSource.ShouldContain("IRuntimeCullingCamera? activeCamera,");
        sceneSource.ShouldContain("Func<IRuntimeCullingCamera>? cullingCameraOverride,");
        sceneSource.ShouldContain("public virtual void DebugRender(IRuntimeCullingCamera? camera, bool onlyContainingItems = false)");

        scene2DSource.ShouldContain("IRuntimeCullingCamera? activeCamera,");
        scene2DSource.ShouldContain("Func<IRuntimeCullingCamera>? cullingCameraOverride,");
        scene2DSource.ShouldContain("public void CollectRenderedItems(RenderCommandCollection commands, BoundingRectangleF? collectionVolume, IRuntimeCullingCamera? camera)");

        scene3DSource.ShouldContain("IRuntimeCullingCamera? camera,");
        scene3DSource.ShouldContain("Func<IRuntimeCullingCamera>? cullingCameraOverride,");
        scene3DSource.ShouldContain("public void CollectRenderedItems(RenderCommandCollection commands, IVolume? collectionVolume, IRuntimeCullingCamera? camera, bool collectMirrors)");

        renderInfo2DSource.ShouldContain("public virtual bool AllowRender(BoundingRectangleF? cullingVolume, RenderCommandCollection passes, IRuntimeCullingCamera? camera)");
        renderInfo3DSource.ShouldContain("IRuntimeCullingCamera? camera,");

        cameraComponentSource.ShouldContain("private Func<IRuntimeCullingCamera>? _cullingCameraOverride = null;");
        cameraComponentSource.ShouldContain("public Func<IRuntimeCullingCamera>? CullingCameraOverride");

        cameraSource.ShouldContain("public class XRCamera : XRBase, IRuntimeRenderCamera, IRuntimeCullingCamera");
        cameraSource.ShouldContain("public Frustum WorldFrustum()");
        cameraSource.ShouldContain("public BoundingRectangleF? GetOrthoCameraBounds()");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string root = ResolveWorkspaceRoot();
        string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(fullPath).ShouldBeTrue($"Expected workspace file to exist: {relativePath}");
        return File.ReadAllText(fullPath);
    }

    private static string ReadRuntimeRenderingContracts()
    {
        string root = ResolveWorkspaceRoot();
        string interfacesRoot = Path.Combine(root, "XREngine.Runtime.Rendering", "Runtime", "Interfaces");
        return string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(interfacesRoot, "*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
    }

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XRENGINE.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find workspace root from base directory '{AppContext.BaseDirectory}'.");
    }
}
