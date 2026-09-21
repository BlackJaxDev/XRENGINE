using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XREngine;
using XREngine.Components;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>
    /// Clears the resource cache for exactly the pipeline instance selected by a viewport.
    /// </summary>
    [XRMcp(
        Name = "clear_render_pipeline_cache",
        Permission = McpPermissionLevel.Mutate,
        PermissionReason = "Destroys and recreates GPU resources for only the selected render-pipeline instance.")]
    [Description("Clear the selected viewport pipeline's GPU-resource cache on its render thread. The pipeline selection is revalidated immediately before the cache is cleared.")]
    public static async Task<McpToolResponse> ClearRenderPipelineCacheAsync(
        McpToolContext context,
        [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
        [McpName("vr_eye"), Description("Optional runtime VR viewport: left, right, or stereo.")] string? vrEye = null,
        [McpName("window_index"), Description("Target window index.")] int windowIndex = 0,
        [McpName("viewport_index"), Description("Target viewport index within the selected window.")] int viewportIndex = 0,
        CancellationToken token = default)
    {
        if (!TryResolveStrictViewport(
                context,
                cameraNodeId,
                vrEye,
                windowIndex,
                viewportIndex,
                out XRViewport? viewport,
                out string? viewportError))
        {
            return new McpToolResponse(viewportError!, isError: true);
        }

        XRViewport targetViewport = viewport!;
        XRWindow? window = targetViewport.Window
            ?? RuntimeEngine.Windows.FirstOrDefault(candidate => candidate.Viewports.Contains(targetViewport));
        if (window is null)
            return new McpToolResponse("The selected viewport has no owning window.", isError: true);
        XRWindow targetWindow = window;

        XRRenderPipelineInstance instance = ResolveSelectedPipelineInstance(targetViewport, vrEye);
        try
        {
            var result = await RunOnViewportRenderThreadAsync(
                targetViewport,
                targetWindow,
                "MCP: Clear selected render pipeline cache",
                renderer =>
                {
                    // A changed stereo eye or camera assignment must not turn this
                    // diagnostic into a cache clear for an unrelated pipeline.
                    if (!ReferenceEquals(instance, ResolveSelectedPipelineInstance(targetViewport, vrEye)))
                    {
                        throw new InvalidOperationException(
                            "The selected viewport pipeline changed before its cache could be cleared.");
                    }
                    if (!RuntimeEngine.IsRenderThread ||
                        !ReferenceEquals(renderer, targetWindow.Renderer) ||
                        !renderer.AcceptsBackendWork ||
                        renderer.IsDeviceLost)
                    {
                        throw new InvalidOperationException(
                            "The selected window renderer is unavailable or no longer owns the active render-thread callback.");
                    }

                    int oldGeneration = instance.ResourceGeneration;
                    int oldDescriptorRevision = instance.Resources.DescriptorRevision;
                    int oldTextureCount = instance.Resources.TextureRecords.Count;
                    int oldFrameBufferCount = instance.Resources.FrameBufferRecords.Count;

                    // Cache-clearing subscribers, including DDGI, require the
                    // selected pipeline context rather than a coincidental window pass.
                    using IDisposable? pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipeline(instance);
                    instance.DestroyCache();

                    return new
                    {
                        viewport_index = targetViewport.Index,
                        pipeline_instance_id = instance.InstanceId,
                        old_pipeline_instance_id = instance.InstanceId,
                        new_pipeline_instance_id = instance.InstanceId,
                        pipeline_asset_id = instance.Pipeline?.ID,
                        pipeline_debug_name = instance.DebugName,
                        old_resource_generation = oldGeneration,
                        new_resource_generation = instance.ResourceGeneration,
                        old_descriptor_revision = oldDescriptorRevision,
                        new_descriptor_revision = instance.Resources.DescriptorRevision,
                        old_texture_count = oldTextureCount,
                        new_texture_count = instance.Resources.TextureRecords.Count,
                        old_framebuffer_count = oldFrameBufferCount,
                        new_framebuffer_count = instance.Resources.FrameBufferRecords.Count,
                    };
                },
                token).ConfigureAwait(false);

            return new McpToolResponse(
                $"Cleared GPU resources for render-pipeline instance {result.pipeline_instance_id}.",
                result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new McpToolResponse($"Failed to clear the selected render-pipeline cache: {ex.Message}", isError: true);
        }
    }

    private static bool TryResolveStrictViewport(
        McpToolContext context,
        string? cameraNodeId,
        string? vrEye,
        int windowIndex,
        int viewportIndex,
        out XRViewport? viewport,
        out string? error)
    {
        viewport = null;
        error = null;
        if (!string.IsNullOrWhiteSpace(cameraNodeId) && !string.IsNullOrWhiteSpace(vrEye))
        {
            error = "Choose either camera_node_id or vr_eye, not both.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(vrEye))
        {
            if (windowIndex != 0 || viewportIndex != 0)
            {
                error = "vr_eye cannot be combined with a non-default window or viewport index.";
                return false;
            }

            viewport = ResolveViewport(context.World, null, vrEye, 0, 0, out error);
            return viewport is not null;
        }

        if (!string.IsNullOrWhiteSpace(cameraNodeId))
        {
            if (windowIndex != 0 || viewportIndex != 0)
            {
                error = "camera_node_id cannot be combined with a non-default window or viewport index.";
                return false;
            }
            if (!TryGetNodeById(context.World, cameraNodeId, out var node, out error) ||
                node?.GetComponent<CameraComponent>() is not { } camera)
            {
                error ??= "camera_node_id does not identify a node with a camera component.";
                return false;
            }

            viewport = ResolveViewport(context.World, cameraNodeId, null, 0, 0, out error);
            if (viewport is null || !ReferenceEquals(viewport.CameraComponent, camera))
            {
                error = "The requested camera has no active viewport.";
                return false;
            }
            return true;
        }

        if (windowIndex < 0 || windowIndex >= RuntimeEngine.Windows.Count)
        {
            error = $"window_index {windowIndex} does not identify an active window.";
            return false;
        }

        XRWindow window = RuntimeEngine.Windows.ElementAt(windowIndex);
        if (viewportIndex < 0 || viewportIndex >= window.Viewports.Count)
        {
            error = $"viewport_index {viewportIndex} does not identify a viewport in window {windowIndex}.";
            return false;
        }

        viewport = window.Viewports[viewportIndex];
        return true;
    }
}
