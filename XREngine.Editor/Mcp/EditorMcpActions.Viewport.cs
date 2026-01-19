using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using XREngine;
using XREngine.Core;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering;

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
        [XRMcp]
        [McpName("capture_viewport_screenshot")]
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
                renderer.GetScreenshotAsync(viewport.Region, false, (img, _) =>
                {
                    if (img is null)
                    {
                        tcs.TrySetException(new InvalidOperationException("Screenshot capture returned null."));
                        return;
                    }

                    try
                    {
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

            var window = viewport.Window ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport));
            if (window is null)
                return new McpToolResponse("No window found to capture from.", isError: true);

            void ScheduleCaptureOnRenderThread()
            {
                if (AbstractRenderer.Current is not null)
                {
                    BeginCapture(AbstractRenderer.Current, viewport, path, tcs);
                    return;
                }

                int captureStarted = 0;
                deferredHandler = () =>
                {
                    var renderer = AbstractRenderer.Current;
                    if (renderer is null)
                        return;

                    if (Interlocked.CompareExchange(ref captureStarted, 1, 0) != 0)
                        return;

                    window.RenderViewportsCallback -= deferredHandler;
                    BeginCapture(renderer, viewport, path, tcs);
                };

                window.RenderViewportsCallback += deferredHandler;
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
                    var window = viewport.Window ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport));
                    window?.RenderViewportsCallback -= deferredHandler;
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

        private static XRViewport? ResolveViewport(XRWorldInstance world, string? cameraNodeId, int windowIndex, int viewportIndex)
        {
            if (!string.IsNullOrWhiteSpace(cameraNodeId) && TryGetNodeById(world, cameraNodeId, out var node, out _))
            {
                var camera = node?.GetComponent<CameraComponent>();
                if (camera is not null)
                {
                    foreach (var window in Engine.Windows)
                    {
                        foreach (var viewport in window.Viewports)
                        {
                            if (ReferenceEquals(viewport.CameraComponent, camera))
                                return viewport;
                        }
                    }
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
