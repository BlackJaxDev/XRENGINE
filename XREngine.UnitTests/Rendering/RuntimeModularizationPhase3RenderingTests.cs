using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeModularizationPhase3RenderingTests
{
    [Test]
    public void P2a_RenderCommandOwnerDebugContext_SourceContracts_ArePresent()
    {
        string runtimeHostSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServices.cs");
        string collectionSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/RenderCommandCollection.cs");
        string gpuPassSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.Core.cs");
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/XRRenderPipelineInstance.cs");

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
        string runtimeHostSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServices.cs");
        string gpuViewSetSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Commands/GPUViewSet.cs");
        string collectionSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/RenderCommandCollection.cs");
        string stateSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/RenderingState.cs");
        string sceneSource = ReadWorkspaceFile("XRENGINE/Rendering/VisualScene.cs");
        string scene3DSource = ReadWorkspaceFile("XRENGINE/Rendering/VisualScene3D.cs");
        string gpuPassCoreSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.Core.cs");
        string gpuPassViewSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.ViewSet.cs");

        runtimeHostSource.ShouldContain("public interface IRuntimeGpuRenderPassHost");
        runtimeHostSource.ShouldContain("public interface IRuntimeRenderCommandSceneContext");
        runtimeHostSource.ShouldContain("public interface IRuntimeRenderCamera");
        runtimeHostSource.ShouldContain("public interface IRuntimeRenderCommandExecutionState");
        runtimeHostSource.ShouldContain("IRuntimeRenderCommandExecutionState? ActiveRenderCommandExecutionState { get; }");

        gpuViewSetSource.ShouldContain("public struct GPUViewDescriptor");
        gpuViewSetSource.ShouldContain("public struct GPUViewConstants");
        gpuViewSetSource.ShouldContain("public static class GPUViewSetLayout");

        collectionSource.ShouldContain("IRuntimeRenderCommandExecutionState? renderState = RuntimeRenderingHostServices.Current.ActiveRenderCommandExecutionState;");
        collectionSource.ShouldContain("scene.RenderGpuPass(gpuPass);");
        collectionSource.ShouldContain("scene.RecordGpuVisibility(draws, instances);");
        collectionSource.ShouldContain("private static void ConfigureGpuViewSet(GPURenderPassCollection gpuPass, IRuntimeRenderCommandExecutionState renderState, IRuntimeRenderCamera leftCamera)");

        stateSource.ShouldContain("public class RenderingState : IRuntimeRenderCommandExecutionState");
        stateSource.ShouldContain("IRuntimeRenderCommandSceneContext? IRuntimeRenderCommandExecutionState.RenderingScene");
        stateSource.ShouldContain("IRuntimeRenderCamera? IRuntimeRenderCommandExecutionState.RenderingCamera");

        sceneSource.ShouldContain("public abstract class VisualScene : XRBase, IEnumerable<RenderInfo>, IRuntimeRenderCommandSceneContext");
        sceneSource.ShouldContain("public virtual void RenderGpuPass(IRuntimeGpuRenderPassHost gpuPass)");

        scene3DSource.ShouldContain("public override void RecordGpuVisibility(uint draws, uint instances)");

        gpuPassCoreSource.ShouldContain("public sealed partial class GPURenderPassCollection : IRuntimeGpuRenderPassHost");
        gpuPassViewSource.ShouldContain("public void SetIndirectSourceViewId(uint viewId)");

        string cameraSource = ReadWorkspaceFile("XRENGINE/Rendering/Camera/XRCamera.cs");
        cameraSource.ShouldContain("public class XRCamera : XRBase, IRuntimeRenderCamera");
        cameraSource.ShouldContain("public bool? StereoEyeLeft");
    }

    [Test]
    public void P2a_PipelineFrameAndScreenSpaceUiContracts_SourceContracts_ArePresent()
    {
        string runtimeHostSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServices.cs");
        string pipelineSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/XRRenderPipelineInstance.cs");
        string stateSource = ReadWorkspaceFile("XRENGINE/Rendering/Pipelines/RenderingState.cs");
        string cameraSource = ReadWorkspaceFile("XRENGINE/Rendering/Camera/XRCamera.cs");
        string uiCanvasSource = ReadWorkspaceFile("XRENGINE/Scene/Components/Pawns/UICanvasComponent.cs");

        runtimeHostSource.ShouldContain("public interface IRuntimeRenderPipelineFrameContext");
        runtimeHostSource.ShouldContain("public interface IRuntimeScreenSpaceUserInterface");
        runtimeHostSource.ShouldContain("IRuntimeRenderPipelineFrameContext? CurrentRenderPipelineContext { get; }");
        runtimeHostSource.ShouldContain("IDisposable? PushRenderingPipeline(IRuntimeRenderPipelineFrameContext pipeline);");
        runtimeHostSource.ShouldContain("void PrepareUpscaleBridgeForFrame(IRuntimeViewportHost viewport, IRuntimeRenderPipelineFrameContext pipeline);");

        pipelineSource.ShouldContain("IRuntimeRenderCommandExecutionState IRuntimeRenderPipelineFrameContext.RenderState => RenderState;");
        pipelineSource.ShouldContain("IRuntimeScreenSpaceUserInterface? userInterface = null");
        pipelineSource.ShouldContain("using (hostServices.PushRenderingPipeline(this))");

        stateSource.ShouldContain("public IRuntimeScreenSpaceUserInterface? ScreenSpaceUserInterface");
        stateSource.ShouldContain("ScreenSpaceUserInterface = screenSpaceUI?.IsScreenSpace == true ? screenSpaceUI : null;");

        cameraSource.ShouldContain("IRuntimeRenderPipelineFrameContext? currentPipeline = RuntimeRenderingHostServices.Current.CurrentRenderPipelineContext;");

        uiCanvasSource.ShouldContain("IRuntimeScreenSpaceUserInterface");
        uiCanvasSource.ShouldContain("bool IRuntimeScreenSpaceUserInterface.IsScreenSpace =>");
    }

    [Test]
    public void P2a_RenderInfoRenderCommandCameraCallbacks_SourceContracts_ArePresent()
    {
        string runtimeHostSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServices.cs");
        string renderInfoSource = ReadWorkspaceFile("XRENGINE/Rendering/Info/RenderInfo.cs");
        string renderInfo2DSource = ReadWorkspaceFile("XRENGINE/Rendering/Info/RenderInfo2D.cs");
        string renderInfo3DSource = ReadWorkspaceFile("XRENGINE/Rendering/Info/RenderInfo3D.cs");
        string renderCommandSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/RenderCommand.cs");
        string renderCommand3DSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/RenderCommand3D.cs");
        string renderCommandMesh3DSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/RenderCommandMesh3D.cs");
        string cameraSource = ReadWorkspaceFile("XRENGINE/Rendering/Camera/XRCamera.cs");

        runtimeHostSource.ShouldContain("bool RendersLayer(int layer);");
        runtimeHostSource.ShouldContain("float DistanceFromRenderNearPlane(Vector3 point);");

        renderInfoSource.ShouldContain("public delegate void DelPreRenderCallback(RenderInfo info, RenderCommand command, IRuntimeRenderCamera? camera);");
        renderInfoSource.ShouldContain("public delegate bool DelAddRenderCommandsCallback(RenderInfo info, RenderCommandCollection passes, IRuntimeRenderCamera? camera);");
        renderInfoSource.ShouldContain("public void CollectCommands(RenderCommandCollection passes, IRuntimeRenderCamera? camera)");

        renderInfo2DSource.ShouldContain("public virtual bool AllowRender(BoundingRectangleF? cullingVolume, RenderCommandCollection passes, IRuntimeRenderCamera? camera)");
        renderInfo3DSource.ShouldContain("IRuntimeRenderCamera? camera,");
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
        string runtimeHostSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeRenderingHostServices.cs");
        string sceneSource = ReadWorkspaceFile("XRENGINE/Rendering/VisualScene.cs");
        string scene2DSource = ReadWorkspaceFile("XRENGINE/Rendering/VisualScene2D.cs");
        string scene3DSource = ReadWorkspaceFile("XRENGINE/Rendering/VisualScene3D.cs");
        string renderInfo2DSource = ReadWorkspaceFile("XRENGINE/Rendering/Info/RenderInfo2D.cs");
        string renderInfo3DSource = ReadWorkspaceFile("XRENGINE/Rendering/Info/RenderInfo3D.cs");
        string cameraComponentSource = ReadWorkspaceFile("XRENGINE/Scene/Components/Camera/CameraComponent.cs");
        string cameraSource = ReadWorkspaceFile("XRENGINE/Rendering/Camera/XRCamera.cs");

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