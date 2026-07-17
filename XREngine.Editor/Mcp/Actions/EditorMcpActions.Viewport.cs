using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using XREngine;
using XREngine.Core;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        /// <summary>
        /// Captures a screenshot from a viewport or camera and saves it to disk.
        /// Useful for providing visual context to AI assistants or for debugging.
        /// </summary>
        /// <param name="context">The MCP tool execution context.</param>
        /// <param name="cameraNodeId">Optional: target a specific camera by its scene node GUID.</param>
        /// <param name="windowIndex">Window index to capture from (default: 0).</param>
        /// <param name="viewportIndex">Viewport index within the window (default: 0).</param>
        /// <param name="outputDir">Output directory for the screenshot. Defaults to "McpCaptures" in the working directory.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>
        /// A response containing:
        /// <list type="bullet">
        /// <item><description><c>path</c> - The absolute file path where the screenshot was saved</description></item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// The screenshot is saved as a PNG file with a timestamp-based filename.
        /// If a camera node ID is provided, the viewport associated with that camera is used.
        /// Otherwise, the viewport is selected by window and viewport index.
        /// </remarks>
        [XRMcp(Name = "capture_viewport_screenshot")]
        [Description("Capture a screenshot from a viewport or camera for LLM context.")]
        public static async Task<McpToolResponse> CaptureViewportScreenshotAsync(
            McpToolContext context,
            [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
            [McpName("window_index"), Description("Optional window index to target.")] int windowIndex = 0,
            [McpName("viewport_index"), Description("Optional viewport index to target.")] int viewportIndex = 0,
            [McpName("output_dir"), Description("Optional directory to write the screenshot into.")] string? outputDir = null,
            CancellationToken token = default)
        {
            var viewport = ResolveViewport(context.WorldInstance, cameraNodeId, windowIndex, viewportIndex);
            if (viewport is null)
                return new McpToolResponse("No viewport found to capture.", isError: true);

            string folder = outputDir ?? Path.Combine(Environment.CurrentDirectory, "McpCaptures");
            string fileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            string path = Path.Combine(folder, fileName);

            Utility.EnsureDirPathExists(path);

            static void BeginCapture(AbstractRenderer renderer, XRViewport viewport, string path, TaskCompletionSource<string> tcs)
            {
                if (renderer is VulkanRenderer)
                {
                    tcs.TrySetException(new NotSupportedException(
                        "Vulkan viewport screenshot readback is temporarily disabled because its synchronous transfer path can trigger a GPU watchdog reset. Use an OS window capture until Vulkan readback synchronization is hardened."));
                    return;
                }

                using IDisposable? readbackScope = renderer is VulkanRenderer
                    ? viewport.EnterRenderPipelineReadbackScope()
                    : null;
                using IDisposable? targetReadScope = viewport.LastRenderedTargetFBO?.BindForReadingState();

                BoundingRectangle captureRegion = viewport.LastRenderedTargetFBO is { } targetFbo
                    ? new BoundingRectangle(0, 0, (int)targetFbo.Width, (int)targetFbo.Height)
                    : viewport.Region;
                if (targetReadScope is not null)
                {
                    renderer.SetReadBuffer(EReadBufferMode.ColorAttachment0);
                }
                else
                {
                    renderer.BindFrameBuffer(EFramebufferTarget.ReadFramebuffer, null);
                }

                renderer.GetScreenshotAsync(captureRegion, false, (img, _) =>
                {
                    if (img is null)
                    {
                        tcs.TrySetException(new InvalidOperationException("Screenshot capture returned null."));
                        return;
                    }

                    try
                    {
                        if (renderer.ScreenshotRequiresVerticalFlip)
                            img.Flip();
                        img.Write(path);
                        tcs.TrySetResult(path);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                });
            }

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action? deferredHandler = null;

            var window = viewport.Window
                ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport))
                ?? Engine.Windows.FirstOrDefault();
            if (window is null)
                return new McpToolResponse("No window found to capture from.", isError: true);

            void ScheduleCaptureOnRenderThread()
            {
                int captureStarted = 0;
                deferredHandler = () =>
                {
                    var renderer = AbstractRenderer.Current;
                    if (renderer is null)
                        return;

                    if (Interlocked.CompareExchange(ref captureStarted, 1, 0) != 0)
                        return;

                    window.PostRenderViewportsCallback -= deferredHandler;
                    BeginCapture(renderer, viewport, path, tcs);
                };

                window.PostRenderViewportsCallback += deferredHandler;
            }

            if (Engine.IsRenderThread)
            {
                ScheduleCaptureOnRenderThread();
            }
            else
            {
                Engine.InvokeOnMainThread(ScheduleCaptureOnRenderThread, "MCP: Capture viewport screenshot", executeNowIfAlreadyMainThread: true);
            }

            using var reg = token.Register(() =>
            {
                if (deferredHandler is not null)
                {
                    var window = viewport.Window
                        ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport))
                        ?? Engine.Windows.FirstOrDefault();
                    window?.PostRenderViewportsCallback -= deferredHandler;
                }

                tcs.TrySetCanceled(token);
            });

            try
            {
                string savedPath = await tcs.Task;
                return new McpToolResponse($"Captured screenshot to '{savedPath}'.", new { path = savedPath });
            }
            catch (Exception ex)
            {
                return new McpToolResponse($"Failed to capture screenshot: {ex.Message}", isError: true);
            }
        }

        [XRMcp(Name = "probe_editor_depth_hit", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Probe the editor camera pawn depth-hit state at a normalized viewport coordinate.")]
        public static async Task<McpToolResponse> ProbeEditorDepthHitAsync(
            McpToolContext context,
            [McpName("normalized_x"), Description("Normalized viewport X in bottom-left origin coordinates.")] float normalizedX = 0.5f,
            [McpName("normalized_y"), Description("Normalized viewport Y in bottom-left origin coordinates.")] float normalizedY = 0.5f,
            [McpName("include_raw"), Description("When true on Vulkan, include raw copied depth bytes and alternate decodes.")] bool includeRaw = false,
            [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
            [McpName("window_index"), Description("Optional window index to target.")] int windowIndex = 0,
            [McpName("viewport_index"), Description("Optional viewport index to target.")] int viewportIndex = 0,
            CancellationToken token = default)
        {
            if (!TryResolveEditorPawnViewport(context, cameraNodeId, windowIndex, viewportIndex, out XRViewport? viewport, out EditorFlyingCameraPawnComponent? pawn, out XRWindow? window, out string? error))
                return new McpToolResponse(error ?? "Unable to resolve editor camera pawn viewport.", isError: true);

            XRViewport targetViewport = viewport!;
            EditorFlyingCameraPawnComponent targetPawn = pawn!;
            XRWindow targetWindow = window!;
            try
            {
                var probe = await RunOnViewportRenderThreadAsync(
                    targetViewport,
                    targetWindow,
                    "MCP: Probe editor depth hit",
                    _ => targetPawn.ProbeDepthHitAtNormalizedViewport(targetViewport, new Vector2(normalizedX, normalizedY)),
                    token);

                return new McpToolResponse("Probed editor depth hit.", new { probe });
            }
            catch (Exception ex)
            {
                return new McpToolResponse($"Failed to probe editor depth hit: {ex.Message}", isError: true);
            }
        }

        [XRMcp(Name = "zoom_editor_camera_at_depth_hit", Permission = McpPermissionLevel.Mutate, PermissionReason = "Moves the editor camera through the editor pawn scroll-zoom path.")]
        [Description("Apply editor camera scroll zoom at a normalized viewport coordinate using the pawn depth-hit mechanic.")]
        public static async Task<McpToolResponse> ZoomEditorCameraAtDepthHitAsync(
            McpToolContext context,
            [McpName("normalized_x"), Description("Normalized viewport X in bottom-left origin coordinates.")] float normalizedX = 0.5f,
            [McpName("normalized_y"), Description("Normalized viewport Y in bottom-left origin coordinates.")] float normalizedY = 0.5f,
            [McpName("scroll_delta"), Description("Scroll delta to apply. Positive zooms toward the depth hit.")] float scrollDelta = 1.0f,
            [McpName("smooth_scroll"), Description("Use the pawn smooth-scroll path instead of forcing an immediate diagnostic move.")] bool smoothScroll = false,
            [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
            [McpName("window_index"), Description("Optional window index to target.")] int windowIndex = 0,
            [McpName("viewport_index"), Description("Optional viewport index to target.")] int viewportIndex = 0,
            CancellationToken token = default)
        {
            if (!TryResolveEditorPawnViewport(context, cameraNodeId, windowIndex, viewportIndex, out XRViewport? viewport, out EditorFlyingCameraPawnComponent? pawn, out XRWindow? window, out string? error))
                return new McpToolResponse(error ?? "Unable to resolve editor camera pawn viewport.", isError: true);

            XRViewport targetViewport = viewport!;
            EditorFlyingCameraPawnComponent targetPawn = pawn!;
            XRWindow targetWindow = window!;
            try
            {
                var result = await RunOnViewportRenderThreadAsync(
                    targetViewport,
                    targetWindow,
                    "MCP: Zoom editor camera at depth hit",
                    _ => targetPawn.DebugZoomAtNormalizedViewport(targetViewport, new Vector2(normalizedX, normalizedY), scrollDelta, smoothScroll),
                    token);

                return new McpToolResponse("Applied editor camera depth-hit zoom.", new { result });
            }
            catch (Exception ex)
            {
                return new McpToolResponse($"Failed to zoom editor camera at depth hit: {ex.Message}", isError: true);
            }
        }

        [XRMcp(Name = "probe_render_pipeline_depth", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Read depth from a named render-pipeline FBO at a normalized viewport coordinate.")]
        public static async Task<McpToolResponse> ProbeRenderPipelineDepthAsync(
            McpToolContext context,
            [McpName("fbo_name"), Description("Render pipeline FBO name to sample. Defaults to ForwardPassFBO.")] string fboName = DefaultRenderPipeline.ForwardPassFBOName,
            [McpName("normalized_x"), Description("Normalized viewport X in bottom-left origin coordinates.")] float normalizedX = 0.5f,
            [McpName("normalized_y"), Description("Normalized viewport Y in bottom-left origin coordinates.")] float normalizedY = 0.5f,
            [McpName("include_raw"), Description("When true on Vulkan, include raw copied depth bytes and alternate decodes.")] bool includeRaw = false,
            [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
            [McpName("window_index"), Description("Optional window index to target.")] int windowIndex = 0,
            [McpName("viewport_index"), Description("Optional viewport index to target.")] int viewportIndex = 0,
            CancellationToken token = default)
        {
            XRViewport? viewport = ResolveViewport(context.WorldInstance, cameraNodeId, windowIndex, viewportIndex);
            if (viewport is null)
                return new McpToolResponse("No viewport found.", isError: true);

            XRWindow? window = viewport.Window
                ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport))
                ?? Engine.Windows.FirstOrDefault();
            if (window is null)
                return new McpToolResponse("No window found for the target viewport.", isError: true);

            XRViewport targetViewport = viewport;
            XRWindow targetWindow = window;

            try
            {
                var probe = await RunOnViewportRenderThreadAsync(
                    targetViewport,
                    targetWindow,
                    "MCP: Probe render pipeline depth",
                    _ => ProbeDepthFbo(targetViewport, fboName, new Vector2(normalizedX, normalizedY), includeRaw),
                    token);

                return new McpToolResponse("Probed render-pipeline depth.", new { probe });
            }
            catch (Exception ex)
            {
                return new McpToolResponse($"Failed to probe render-pipeline depth: {ex.Message}", isError: true);
            }
        }

        [XRMcp(Name = "sweep_render_pipeline_depth", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Read depth from every live render-pipeline FBO that has a depth/stencil attachment at a normalized viewport coordinate.")]
        public static async Task<McpToolResponse> SweepRenderPipelineDepthAsync(
            McpToolContext context,
            [McpName("normalized_x"), Description("Normalized viewport X in bottom-left origin coordinates.")] float normalizedX = 0.5f,
            [McpName("normalized_y"), Description("Normalized viewport Y in bottom-left origin coordinates.")] float normalizedY = 0.5f,
            [McpName("include_raw"), Description("When true on Vulkan, include raw copied depth bytes and alternate decodes.")] bool includeRaw = false,
            [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
            [McpName("window_index"), Description("Optional window index to target.")] int windowIndex = 0,
            [McpName("viewport_index"), Description("Optional viewport index to target.")] int viewportIndex = 0,
            CancellationToken token = default)
        {
            XRViewport? viewport = ResolveViewport(context.WorldInstance, cameraNodeId, windowIndex, viewportIndex);
            if (viewport is null)
                return new McpToolResponse("No viewport found.", isError: true);

            XRWindow? window = viewport.Window
                ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport))
                ?? Engine.Windows.FirstOrDefault();
            if (window is null)
                return new McpToolResponse("No window found for the target viewport.", isError: true);

            XRViewport targetViewport = viewport;
            XRWindow targetWindow = window;

            try
            {
                var result = await RunOnViewportRenderThreadAsync(
                    targetViewport,
                    targetWindow,
                    "MCP: Sweep render pipeline depth",
                    _ =>
                    {
                        XRRenderPipelineInstance? instance = targetViewport.RenderPipelineInstance;
                        if (instance is null)
                            throw new InvalidOperationException("Viewport has no render pipeline instance.");

                        var probes = instance.Resources.EnumerateFrameBufferInstances()
                            .Where(HasDepthStencilAttachment)
                            .OrderBy(fbo => fbo.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(fbo => ProbeDepthFbo(targetViewport, fbo.Name ?? string.Empty, new Vector2(normalizedX, normalizedY), includeRaw))
                            .ToArray();

                        return new
                        {
                            requested_normalized_x = normalizedX,
                            requested_normalized_y = normalizedY,
                            fbo_count = probes.Length,
                            probes
                        };
                    },
                    token);

                return new McpToolResponse("Swept render-pipeline depth FBOs.", result);
            }
            catch (Exception ex)
            {
                return new McpToolResponse($"Failed to sweep render-pipeline depth: {ex.Message}", isError: true);
            }
        }

        private static bool TryResolveEditorPawnViewport(
            McpToolContext context,
            string? cameraNodeId,
            int windowIndex,
            int viewportIndex,
            out XRViewport? viewport,
            out EditorFlyingCameraPawnComponent? pawn,
            out XRWindow? window,
            out string? error)
        {
            viewport = ResolveViewport(context.WorldInstance, cameraNodeId, windowIndex, viewportIndex);
            pawn = null;
            window = null;
            error = null;

            if (viewport is null)
            {
                error = "No viewport found.";
                return false;
            }

            XRViewport resolvedViewport = viewport;
            window = resolvedViewport.Window ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(resolvedViewport));
            if (window is null)
            {
                error = "No window found for the target viewport.";
                return false;
            }

            if (Engine.State.MainPlayer?.ControlledPawnComponent is EditorFlyingCameraPawnComponent controlledPawn)
                pawn = controlledPawn;
            else
                pawn = viewport.CameraComponent?.SceneNode?.GetComponent<EditorFlyingCameraPawnComponent>();

            if (pawn is null)
            {
                error = "The active camera is not controlled by an EditorFlyingCameraPawnComponent.";
                return false;
            }

            return true;
        }

        private static object ProbeDepthFbo(XRViewport viewport, string fboName, Vector2 normalizedViewportPoint, bool includeRaw)
        {
            if (string.IsNullOrWhiteSpace(fboName))
                throw new ArgumentException("FBO name is required.", nameof(fboName));

            XRRenderPipelineInstance? instance = viewport.RenderPipelineInstance;
            if (instance is null)
                throw new InvalidOperationException("Viewport has no render pipeline instance.");

            XRFrameBuffer? fbo = instance.GetFBO<XRFrameBuffer>(fboName);
            if (fbo is null)
                throw new InvalidOperationException($"Viewport render pipeline has no '{fboName}' framebuffer.");

            Vector2 clamped = ClampNormalizedViewport(normalizedViewportPoint);
            Vector2 internalCoordinate = viewport.DenormalizeInternalCoordinate(clamped);
            IVector2 unflippedCoordinate = (IVector2)internalCoordinate;
            IVector2 flippedCoordinate = FlipDepthReadbackCoordinateY(fbo, unflippedCoordinate);
            bool policyFlipsY = ShouldFlipDepthReadbackY();
            IVector2 currentCoordinate = policyFlipsY ? flippedCoordinate : unflippedCoordinate;

            using IDisposable? readbackScope = viewport.EnterRenderPipelineReadbackScope();
            object unflipped = ReadDepthProbeSample(viewport, clamped, fbo, unflippedCoordinate, includeRaw);
            object flipped = CoordinatesEqual(unflippedCoordinate, flippedCoordinate)
                ? unflipped
                : ReadDepthProbeSample(viewport, clamped, fbo, flippedCoordinate, includeRaw);
            object current = CoordinatesEqual(currentCoordinate, flippedCoordinate) ? flipped : unflipped;

            RuntimeGraphicsApiKind backend = RuntimeRenderingHostServices.Current.CurrentRenderBackend;
            ERenderClipSpaceYDirection clipY = RuntimeRenderingHostServices.Current.ClipSpaceYDirection;
            ERenderClipSpaceYDirection framebufferY = backend == RuntimeGraphicsApiKind.Unknown
                ? clipY
                : RenderClipSpacePolicy.FramebufferTextureYDirection(backend);

            return new
            {
                fbo_name = fboName,
                framebuffer_width = (int)fbo.Width,
                framebuffer_height = (int)fbo.Height,
                requested_normalized_x = normalizedViewportPoint.X,
                requested_normalized_y = normalizedViewportPoint.Y,
                clamped_normalized_x = clamped.X,
                clamped_normalized_y = clamped.Y,
                internal_x = internalCoordinate.X,
                internal_y = internalCoordinate.Y,
                render_backend = backend.ToString(),
                clip_space_y_direction = clipY.ToString(),
                framebuffer_texture_y_direction = framebufferY.ToString(),
                current_policy_flips_y = policyFlipsY,
                attachments = DescribeFrameBufferDepthAttachments(fbo),
                current,
                unflipped,
                flipped
            };
        }

        private static bool HasDepthStencilAttachment(XRFrameBuffer fbo)
        {
            var targets = fbo.Targets;
            if (targets is null)
                return false;

            foreach (var (_, attachment, _, _) in targets)
            {
                if (attachment is EFrameBufferAttachment.DepthAttachment
                    or EFrameBufferAttachment.DepthStencilAttachment
                    or EFrameBufferAttachment.StencilAttachment)
                    return true;
            }

            return false;
        }

        private static object[] DescribeFrameBufferDepthAttachments(XRFrameBuffer fbo)
        {
            var targets = fbo.Targets;
            if (targets is null)
                return [];

            return targets
                .Where(t => t.Attachment is EFrameBufferAttachment.DepthAttachment
                    or EFrameBufferAttachment.DepthStencilAttachment
                    or EFrameBufferAttachment.StencilAttachment)
                .Select(t => new
                {
                    attachment = t.Attachment.ToString(),
                    target_type = t.Target.GetType().Name,
                    target_name = t.Target switch
                    {
                        XRTexture texture => texture.Name,
                        XRRenderBuffer renderBuffer => renderBuffer.Name,
                        _ => null
                    },
                    mip_level = t.MipLevel,
                    layer_index = t.LayerIndex
                })
                .ToArray<object>();
        }

        private static object ReadDepthProbeSample(
            XRViewport viewport,
            Vector2 normalizedViewportPoint,
            XRFrameBuffer fbo,
            IVector2 readbackCoordinate,
            bool includeRaw)
        {
            float depth = XRViewport.GetDepth(fbo, readbackCoordinate);
            bool validDepth = depth > 0.0f && depth < 1.0f;
            Vector3? worldPoint = validDepth
                ? viewport.NormalizedViewportToWorldCoordinate(new Vector3(normalizedViewportPoint, depth))
                : null;
            object? vulkanRaw = null;
            if (includeRaw && AbstractRenderer.Current is VulkanRenderer vulkan)
            {
                vulkan.TryReadDepthPixelDebug(fbo, readbackCoordinate.X, readbackCoordinate.Y, out var debugInfo);
                vulkanRaw = debugInfo;
            }

            return new
            {
                x = readbackCoordinate.X,
                y = readbackCoordinate.Y,
                depth,
                valid_depth = validDepth,
                world_x = worldPoint?.X,
                world_y = worldPoint?.Y,
                world_z = worldPoint?.Z,
                vulkan_raw = vulkanRaw
            };
        }

        private static Vector2 ClampNormalizedViewport(Vector2 normalizedViewportPoint)
            => new(
                Math.Clamp(normalizedViewportPoint.X, 0.0f, 1.0f),
                Math.Clamp(normalizedViewportPoint.Y, 0.0f, 1.0f));

        private static IVector2 FlipDepthReadbackCoordinateY(XRFrameBuffer fbo, IVector2 coordinate)
        {
            int maxY = Math.Max((int)fbo.Height - 1, 0);
            return new IVector2(coordinate.X, maxY - coordinate.Y);
        }

        private static bool CoordinatesEqual(IVector2 left, IVector2 right)
            => left.X == right.X && left.Y == right.Y;

        private static bool ShouldFlipDepthReadbackY()
        {
            RuntimeGraphicsApiKind backend = RuntimeRenderingHostServices.Current.CurrentRenderBackend;
            if (backend == RuntimeGraphicsApiKind.Unknown)
                return false;

            return RenderClipSpacePolicy.FramebufferTextureYDirection(backend) == ERenderClipSpaceYDirection.YDown;
        }

        private static Task<T> RunOnViewportRenderThreadAsync<T>(
            XRViewport viewport,
            XRWindow window,
            string reason,
            Func<AbstractRenderer, T> action,
            CancellationToken token)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action? deferredHandler = null;

            void Run(AbstractRenderer renderer)
            {
                try
                {
                    tcs.TrySetResult(action(renderer));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }

            void Schedule()
            {
                int started = 0;
                deferredHandler = () =>
                {
                    if (AbstractRenderer.Current is not { } renderer)
                        return;

                    if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
                        return;

                    window.PostRenderViewportsCallback -= deferredHandler;
                    Run(renderer);
                };

                window.PostRenderViewportsCallback += deferredHandler;
            }

            if (Engine.IsRenderThread)
            {
                if (AbstractRenderer.Current is { } renderer)
                    Run(renderer);
                else
                    Schedule();
            }
            else
            {
                Engine.InvokeOnMainThread(Schedule, reason, executeNowIfAlreadyMainThread: true);
            }

            if (token.CanBeCanceled)
            {
                token.Register(() =>
                {
                    if (deferredHandler is not null)
                        window.PostRenderViewportsCallback -= deferredHandler;

                    tcs.TrySetCanceled(token);
                });
            }

            return tcs.Task;
        }

        private static XRViewport? ResolveViewport(XRWorldInstance world, string? cameraNodeId, int windowIndex, int viewportIndex)
        {
            if (!string.IsNullOrWhiteSpace(cameraNodeId) && TryGetNodeById(world, cameraNodeId, out var node, out _))
            {
                var camera = node?.GetComponent<CameraComponent>();
                if (camera is not null)
                {
                    foreach (var viewport in Engine.EnumerateActiveViewports())
                    {
                        if (ReferenceEquals(viewport.CameraComponent, camera))
                            return viewport;
                    }

                    if (camera.Camera.Viewports.Count > 0)
                        return camera.Camera.Viewports[0];
                }
            }

            if (windowIndex < 0 || windowIndex >= Engine.Windows.Count)
                return Engine.Windows.FirstOrDefault()?.Viewports.FirstOrDefault();

            var windowTarget = Engine.Windows.ElementAt(windowIndex);
            if (windowTarget.Viewports.Count == 0)
                return null;

            if (viewportIndex < 0 || viewportIndex >= windowTarget.Viewports.Count)
                return windowTarget.Viewports.FirstOrDefault();

            return windowTarget.Viewports[viewportIndex];
        }
    }
}
