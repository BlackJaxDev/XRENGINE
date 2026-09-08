using System;
using System.ComponentModel;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>
    /// Reads one Advanced visibility pixel through the backend's asynchronous staging path.
    /// The response includes the generation-checked canonical identities resolved from the
    /// exact accepted publication paired with those attachments.
    /// </summary>
    [XRMcp(Name = "query_advanced_pick", Permission = McpPermissionLevel.ReadOnly)]
    [Description("Asynchronously resolve canonical Advanced visibility identities at one normalized viewport coordinate without a synchronous GPU readback.")]
    public static async Task<McpToolResponse> QueryAdvancedPickAsync(
        McpToolContext context,
        [McpName("x"), Description("Normalized viewport X coordinate in [0,1].")] float x,
        [McpName("y"), Description("Normalized viewport Y coordinate in [0,1].")] float y,
        [McpName("view_index"), Description("Logical visibility view/layer index.")] uint viewIndex = 0u,
        [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
        [McpName("vr_eye"), Description("Optional runtime VR eye viewport to target: left or right.")] string? vrEye = null,
        [McpName("window_index"), Description("Optional window index to target.")] int windowIndex = 0,
        [McpName("viewport_index"), Description("Optional viewport index to target.")] int viewportIndex = 0,
        CancellationToken token = default)
    {
        XRViewport? viewport = ResolveViewport(
            context.World,
            cameraNodeId,
            vrEye,
            windowIndex,
            viewportIndex,
            out string? viewportError);
        if (viewport is null)
            return new McpToolResponse(
                viewportError ?? "No viewport found for Advanced picking.",
                isError: true);
        if (viewport.RenderPipelineInstance.Pipeline is not AdvancedRenderPipeline)
            return new McpToolResponse(
                "The selected viewport is not using the Advanced render pipeline.",
                isError: true);

        var completion = new TaskCompletionSource<AdvancedPickingResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!viewport.TryPickAdvancedAsync(
                new Vector2(x, y),
                viewIndex,
                result => completion.TrySetResult(result),
                out AdvancedPickingRequest? request,
                out string? failure))
        {
            return new McpToolResponse(
                failure ?? "The backend rejected the Advanced picking readback.",
                isError: true);
        }

        AdvancedPickingResult pick;
        try
        {
            pick = await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            request?.Dispose();
            return new McpToolResponse(
                "Timed out waiting for the asynchronous Advanced picking readback.",
                isError: true);
        }
        catch (OperationCanceledException)
        {
            request?.Dispose();
            throw;
        }

        return new McpToolResponse(
            pick.IsHit
                ? "Resolved an Advanced visibility hit."
                : "The Advanced visibility pixel contains no canonical hit.",
            new
            {
                hit = pick.IsHit,
                pick.RequestGeneration,
                pick.DatabaseEpoch,
                pick.PublicationSequence,
                draw = DescribeHandle(pick.Draw),
                instance = DescribeHandle(pick.Instance),
                geometry = DescribeHandle(pick.Geometry),
                material = DescribeHandle(pick.Material),
                currentTransform = DescribeHandle(pick.CurrentTransform),
                previousTransform = DescribeHandle(pick.PreviousTransform),
                editorIdentity = DescribeHandle(pick.EditorIdentity),
                pick.StableComponentId,
                pick.LogicalMeshId,
                pick.SelectionId,
                pick.PrimitiveSection,
                producer = pick.Producer.ToString(),
                pick.PrimitiveId,
                pick.MeshletOrClusterId,
                pick.LocalPrimitiveId,
                pick.ViewIndex,
                sceneNodeId = pick.AuthoringRenderInfo?.Owner switch
                {
                    XREngine.Components.XRComponent component =>
                        component.SceneNode?.ID.ToString(),
                    XREngine.Scene.Transforms.TransformBase transform =>
                        transform.SceneNode?.ID.ToString(),
                    _ => null,
                },
                sceneNodeName = pick.AuthoringRenderInfo?.Owner switch
                {
                    XREngine.Components.XRComponent component =>
                        component.SceneNode?.Name,
                    XREngine.Scene.Transforms.TransformBase transform =>
                        transform.SceneNode?.Name,
                    _ => null,
                },
            });
    }

    private static object DescribeHandle(
        XREngine.Rendering.Commands.AdvancedGpuHandle handle)
        => new
        {
            handle.Index,
            handle.Generation,
            handle.IsValid,
        };
}
