using System.ComponentModel;
using System.Numerics;
using System.Threading.Tasks;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Scene;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>
    /// Reads the runtime VR view state that the legacy two-pass and shared-stereo
    /// render paths depend on: the published eye cameras, render world, headset
    /// node, eye viewports, culling frustum, per-eye pipelines and the shared
    /// command collection. Every value here is a precondition of the VR render
    /// callbacks, so a null or empty entry names the reason an eye view is not
    /// rendered.
    /// </summary>
    [XRMcp(Name = "get_vr_view_state", Permission = McpPermissionLevel.ReadOnly)]
    [Description("Read the runtime VR view state: published eye cameras, render world, headset node, eye/stereo viewports, culling frustum, per-eye pipelines, the shared eye command collection and the last VR render-pass counters. Use it to explain unrendered or black eye views in emulated, OpenVR and OpenXR modes.")]
    public static Task<McpToolResponse> GetVrViewStateAsync(McpToolContext context)
        => Task.FromResult(new McpToolResponse("Read runtime VR view state.", CreateVrViewState()));

    private static object CreateVrViewState()
    {
        RuntimeVrState state = RuntimeEngine.VRState;
        var view = state.ViewInformation;
        IRuntimeRenderWorld? world = view.World;
        VisualScene3D? visualScene = world?.VisualScene;
        RenderCommandCollection? shared = state.SharedMeshRenderCommands;
        Frustum? frustum = state.StereoCullingFrustum;
        return new
        {
            activeRuntime = state.ActiveRuntime.ToString(),
            isInVR = state.IsInVR,
            emulatedRenderActive = state.EmulatedRenderActive,
            openVrRuntimeActiveForRender = state.OpenVrRuntimeActiveForRender,
            openXrActive = state.IsOpenXRActive,
            renderWindowAttached = state.RenderWindow is not null,
            rendererAttached = state.Renderer is not null,
            lastRenderSize = new { width = state.LastRenderWidth, height = state.LastRenderHeight },
            viewInformation = new
            {
                leftEyeCamera = DescribeCamera(view.LeftEyeCamera),
                rightEyeCamera = DescribeCamera(view.RightEyeCamera),
                worldPresent = world is not null,
                worldType = world?.GetType().Name,
                worldTargetName = world?.TargetWorldName,
                visualScenePresent = visualScene is not null,
                visualSceneType = visualScene?.GetType().Name,
                hmdNodeName = view.HMDNode?.Name,
                hmdNodeId = view.HMDNode?.ID,
                hmdNodeActiveInHierarchy = view.HMDNode?.IsActiveInHierarchy,
            },
            stereoCullingFrustumPresent = frustum.HasValue,
            combinedProjectionIsIdentity = state.CombinedProjectionMatrix == Matrix4x4.Identity,
            leftEyeViewport = DescribeViewport(state.LeftEyeViewport),
            rightEyeViewport = DescribeViewport(state.RightEyeViewport),
            stereoViewport = DescribeViewport(state.StereoViewport),
            twoPassLeftPipeline = DescribePipelineInstance(state.TwoPassLeftPipeline),
            twoPassRightPipeline = DescribePipelineInstance(state.TwoPassRightPipeline),
            leftEyeRenderTarget = DescribeFrameBuffer(state.VRLeftEyeRenderTarget),
            rightEyeRenderTarget = DescribeFrameBuffer(state.VRRightEyeRenderTarget),
            stereoRenderTarget = DescribeFrameBuffer(state.VRStereoRenderTarget),
            sharedMeshRenderCommands = shared is null ? null : new
            {
                renderingCommandCount = shared.GetRenderingCommandCount(),
                updatingCommandCount = shared.GetUpdatingCommandCount(),
                commandsAddedCount = shared.GetCommandsAddedCount(),
                isRenderCommandSnapshotAuthority = shared.IsRenderCommandSnapshotAuthority,
                renderingPackage = DescribeFramePackage(shared.RenderingBackendReadyPackage),
                renderingCommandPasses = BuildRenderCommandPassSummary(shared),
            },
            lastFrameVrRenderPass = new
            {
                drawCalls = RuntimeEngine.Rendering.Stats.Vr.VrRenderPassDrawCalls,
                multiDrawCalls = RuntimeEngine.Rendering.Stats.Vr.VrRenderPassMultiDrawCalls,
                trianglesRendered = RuntimeEngine.Rendering.Stats.Vr.VrRenderPassTrianglesRendered,
                timeMs = JsonFinite(RuntimeEngine.Rendering.Stats.Vr.VrRenderPassTimeMs),
                trackingEnabled = RuntimeEngine.Rendering.Stats.EnableTracking,
            },
            realWorldIpd = state.RealWorldIPD,
        };
    }

    private static object? DescribeCamera(XRCamera? camera)
    {
        if (camera is null)
            return null;
        SceneNode? node = camera.Transform?.SceneNode;
        return new
        {
            parametersType = camera.Parameters?.GetType().Name,
            nodeName = node?.Name,
            nodeId = node?.ID,
            worldPosition = camera.Transform is null ? null : ToMcpVector3(camera.Transform.WorldTranslation),
            pipelinePresent = camera.RenderPipeline is not null,
            pipelineType = camera.RenderPipeline?.GetType().Name,
        };
    }

    private static object? DescribeViewport(XRViewport? viewport)
    {
        if (viewport is null)
            return null;
        XRCamera? camera = viewport.ActiveCamera;
        SceneNode? cameraNode = camera?.Transform?.SceneNode;
        return new
        {
            index = viewport.Index,
            width = viewport.Width,
            height = viewport.Height,
            internalWidth = viewport.InternalWidth,
            internalHeight = viewport.InternalHeight,
            cameraPresent = camera is not null,
            cameraNodeName = cameraNode?.Name,
            worldInstanceOverridePresent = viewport.WorldInstanceOverride is not null,
            worldPresent = viewport.World is not null,
            automaticallyCollectVisible = viewport.AutomaticallyCollectVisible,
            automaticallySwapBuffers = viewport.AutomaticallySwapBuffers,
            pipelineInstance = DescribePipelineInstance(viewport.RenderPipelineInstance),
            lastRenderedTargetPresent = viewport.LastRenderedTargetFBO is not null,
        };
    }

    private static object? DescribePipelineInstance(XRRenderPipelineInstance? instance)
    {
        if (instance is null)
            return null;
        return new
        {
            pipelineType = instance.Pipeline?.GetType().Name,
            activeGenerationPresent = instance.ActiveGeneration is not null,
            ownRenderingCommandCount = instance.MeshRenderCommands.GetRenderingCommandCount(),
            ownRenderingPackage = DescribeFramePackage(instance.MeshRenderCommands.RenderingBackendReadyPackage),
            lastRenderDeclineReason = instance.LastRenderDeclineReason,
            lastResourceGenerationFailure = instance.LastResourceGenerationFailure,
        };
    }

    private static object DescribeFramePackage(BackendReadyFramePackage package)
        => new
        {
            state = package.State.ToString(),
            packageGeneration = package.PackageGeneration,
            collectGeneration = package.Identity.CollectGeneration,
            frameId = package.Identity.FrameId,
            canonicalViewCount = package.CanonicalViews.Length,
            cpuVisibleDrawCount = package.CpuVisibleDraws.Length,
            commandCount = package.CommandCount,
            meshCommandCount = package.MeshCommandCount,
        };

    private static object? DescribeFrameBuffer(XRFrameBuffer? frameBuffer)
        => frameBuffer is null
            ? null
            : new { name = frameBuffer.Name, width = frameBuffer.Width, height = frameBuffer.Height, type = frameBuffer.GetType().Name };
}
