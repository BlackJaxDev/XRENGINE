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
        [XRMCP]
        [DisplayName("capture_viewport_screenshot")]
        [Description("Capture a screenshot from a viewport or camera for LLM context.")]
        public static async Task<McpToolResponse> CaptureViewportScreenshotAsync(
            McpToolContext context,
            [DisplayName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
            [DisplayName("window_index"), Description("Optional window index to target.")] int windowIndex = 0,
            [DisplayName("viewport_index"), Description("Optional viewport index to target.")] int viewportIndex = 0,
            [DisplayName("output_dir"), Description("Optional directory to write the screenshot into.")] string? outputDir = null,
            CancellationToken token = default)
        {
            var viewport = ResolveViewport(context.WorldInstance, cameraNodeId, windowIndex, viewportIndex);
            if (viewport is null)
                return new McpToolResponse("No viewport found to capture.", isError: true);

            if (AbstractRenderer.Current is null)
                return new McpToolResponse("No active renderer available for screenshot capture.", isError: true);

            string folder = outputDir ?? Path.Combine(Environment.CurrentDirectory, "McpCaptures");
            string fileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            string path = Path.Combine(folder, fileName);

            Utility.EnsureDirPathExists(path);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            AbstractRenderer.Current.GetScreenshotAsync(viewport.Region, false, (img, _) =>
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

            using var reg = token.Register(() => tcs.TrySetCanceled(token));
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
