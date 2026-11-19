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
    /// Reads one editor identity pixel and reports the retained mesh owner from
    /// the frame that wrote it. The render thread does not wait for a GPU fence.
    /// </summary>
    [XRMcp(Name = "query_editor_pick", Permission = McpPermissionLevel.ReadOnly)]
    [Description("Resolve the exact editor mesh owner at one normalized Advanced viewport coordinate with an asynchronous GPU readback.")]
    public static async Task<McpToolResponse> QueryEditorPickAsync(
        McpToolContext context,
        [McpName("x"), Description("Normalized viewport X coordinate in [0,1].")] float x,
        [McpName("y"), Description("Normalized viewport Y coordinate in [0,1].")] float y,
        [McpName("view_index"), Description("Logical visibility view/layer index.")] uint viewIndex = 0u,
        [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
        [McpName("vr_eye"), Description("Optional runtime VR eye viewport: left or right.")] string? vrEye = null,
        [McpName("window_index"), Description("Optional window index.")] int windowIndex = 0,
        [McpName("viewport_index"), Description("Optional viewport index.")] int viewportIndex = 0,
        CancellationToken token = default)
    {
        XRViewport? viewport = ResolveViewport(context.World, cameraNodeId,
            vrEye, windowIndex, viewportIndex, out string? viewportError);
        if (viewport is null)
            return new McpToolResponse(
                viewportError ?? "No viewport found for editor picking.", isError: true);
        if (!viewport.RenderPipelineInstance.EditorPickingModuleEnabled)
            return new McpToolResponse(
                "The selected viewport has no Advanced editor identity output.", isError: true);

        var completion = new TaskCompletionSource<EditorPickingResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!viewport.TryPickEditorAsync(new Vector2(x, y), viewIndex,
                result => completion.TrySetResult(result),
                out EditorPickingRequest? request, out string? failure))
            return new McpToolResponse(
                failure ?? "The backend rejected the editor identity readback.", isError: true);

        EditorPickingResult pick;
        try
        {
            pick = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            request?.Dispose();
            return new McpToolResponse(
                "Timed out waiting for the asynchronous editor identity readback.", isError: true);
        }
        catch (OperationCanceledException)
        {
            request?.Dispose();
            throw;
        }

        return new McpToolResponse(
            pick.IsHit ? "Resolved an editor mesh owner." :
                "The editor identity pixel contains no live mesh owner.",
            new
            {
                hit = pick.IsHit,
                pick.RequestGeneration,
                pick.PackageIdentity,
                pick.PackageGeneration,
                pick.ViewIndex,
                sourceKind = pick.SourceKind.ToString(),
                pick.SourceKey,
                hasMeshOwner = pick.RenderableMesh is not null,
                currentMeshName = pick.RenderableMesh?.CurrentLODMesh?.Name,
                sceneNodeId = pick.RenderInfo?.Owner switch
                {
                    XREngine.Components.XRComponent component =>
                        component.SceneNode?.ID.ToString(),
                    XREngine.Scene.Transforms.TransformBase transform =>
                        transform.SceneNode?.ID.ToString(),
                    _ => null,
                },
                sceneNodeName = pick.RenderInfo?.Owner switch
                {
                    XREngine.Components.XRComponent component =>
                        component.SceneNode?.Name,
                    XREngine.Scene.Transforms.TransformBase transform =>
                        transform.SceneNode?.Name,
                    _ => null,
                },
            });
    }
}
