using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Components.Lights;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Imaging;
using XREngine.Rendering;
using XREngine.Rendering.Shadows;
using XREngine.Scene;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        /// <summary>
        /// Writes the directional shadow cascades that the Advanced shading samples to PNG files and a JSON file.
        /// For each selected cascade it reads the atlas page layer that holds the cascade tile, writes the full layer
        /// and a crop of the cascade UV rectangle as normalized grayscale depth, and records the matrix, split, UV
        /// rectangle, page, layer, residency, fallback and last render frame. The reads run on the render thread after
        /// the viewports render, so all cascades of one call come from the same frame.
        /// </summary>
        [XRMcp(Name = "export_directional_shadow_cascades", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Export the directional shadow cascades that the Advanced shading samples: for each cascade, the atlas page layer that holds it and a crop of its UV rectangle as normalized grayscale depth PNGs, plus a JSON file with the rendered and current matrix, split, UV rectangle, page, layer, residency, fallback and last render frame. Choose the Hmd or Desktop cascade set and one cascade or all.")]
        public static async Task<McpToolResponse> ExportDirectionalShadowCascadesAsync(
            McpToolContext context,
            [McpName("source"), Description("Cascade set to export: hmd or desktop.")] string source = "hmd",
            [McpName("cascade_index"), Description("Cascade index to export, or -1 for all cascades of the set.")] int cascadeIndex = -1,
            [McpName("light_node_id"), Description("Optional scene node ID of the directional light. Omit to use the first dynamic directional light.")] string? lightNodeId = null,
            [McpName("output_dir"), Description("Directory to write the PNG and JSON files into.")] string? outputDir = null,
            CancellationToken token = default)
        {
            ShadowRequestSource requestSource;
            if (string.Equals(source, "hmd", StringComparison.OrdinalIgnoreCase))
                requestSource = ShadowRequestSource.Hmd;
            else if (string.Equals(source, "desktop", StringComparison.OrdinalIgnoreCase))
                requestSource = ShadowRequestSource.Desktop;
            else
                return new McpToolResponse("source must be hmd or desktop.", isError: true);
            if (cascadeIndex < -1)
                return new McpToolResponse("cascade_index must be -1 or a cascade index.", isError: true);

            XRViewport? viewport = ResolveViewport(context.World, null, null, 0, 0, out string? viewportError);
            if (viewport?.World is not IRuntimeRenderWorld renderWorld)
                return new McpToolResponse(viewportError ?? "No viewport with a render world was found.", isError: true);
            Lights3DCollection lights = renderWorld.Lights;

            DirectionalLightComponent? light = null;
            foreach (DirectionalLightComponent candidate in lights.DynamicDirectionalLights)
            {
                if (string.IsNullOrWhiteSpace(lightNodeId) ||
                    string.Equals(candidate.SceneNode?.ID.ToString(), lightNodeId, StringComparison.OrdinalIgnoreCase))
                {
                    light = candidate;
                    break;
                }
            }
            if (light is null)
                return new McpToolResponse("No matching dynamic directional light was found.", isError: true);

            XRWindow? window = viewport.Window ?? RuntimeEngine.Windows.FirstOrDefault();
            if (window is null)
                return new McpToolResponse("No window found for the target viewport.", isError: true);

            string folder = outputDir ?? Path.Combine(Environment.CurrentDirectory, "McpCaptures", "DirectionalShadow");
            Directory.CreateDirectory(folder);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string prefix = $"DirectionalShadow_{requestSource}_{stamp}";

            var tcs = new TaskCompletionSource<List<object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action? deferredHandler = null;

            void Export(AbstractRenderer renderer)
            {
                try
                {
                    List<DirectionalCascadeShadowSnapshot> snapshots = [];
                    lights.CaptureDirectionalCascadeShadowSnapshots(light, requestSource, snapshots);
                    var exported = new List<object>();
                    var layerFiles = new Dictionary<(XRTexture2DArray Texture, int Layer), (string Path, float[] Rgba, int Width, int Height)>();
                    foreach (DirectionalCascadeShadowSnapshot snapshot in snapshots)
                    {
                        if (cascadeIndex >= 0 && snapshot.CascadeIndex != cascadeIndex)
                            continue;

                        string? layerPath = null;
                        string? cropPath = null;
                        string? readFailure = null;
                        int[]? cropRect = null;
                        object? depthStats = null;
                        if (snapshot.PageTexture is { } texture && snapshot.PageIndex >= 0)
                        {
                            if (!layerFiles.TryGetValue((texture, snapshot.PageIndex), out var layer))
                            {
                                if (renderer.TryReadTextureMipRgbaFloat(texture, 0, snapshot.PageIndex, out float[]? rgba, out int width, out int height, out string failure) &&
                                    rgba is not null)
                                {
                                    float[] gray = ToDepthGrayscale(rgba);
                                    string path = Path.Combine(folder, $"{prefix}_page{snapshot.PageIndex}.png");
                                    WriteDepthPng(path, gray, width, height);
                                    layer = (path, gray, width, height);
                                    layerFiles[(texture, snapshot.PageIndex)] = layer;
                                }
                                else
                                    readFailure = failure;
                            }

                            if (layer.Rgba is not null)
                            {
                                layerPath = layer.Path;
                                int x0 = Math.Clamp((int)MathF.Floor(snapshot.UvScaleBias.Z * layer.Width), 0, layer.Width - 1);
                                int y0 = Math.Clamp((int)MathF.Floor(snapshot.UvScaleBias.W * layer.Height), 0, layer.Height - 1);
                                int x1 = Math.Clamp((int)MathF.Ceiling((snapshot.UvScaleBias.Z + snapshot.UvScaleBias.X) * layer.Width), x0 + 1, layer.Width);
                                int y1 = Math.Clamp((int)MathF.Ceiling((snapshot.UvScaleBias.W + snapshot.UvScaleBias.Y) * layer.Height), y0 + 1, layer.Height);
                                cropRect = [x0, y0, x1 - x0, y1 - y0];
                                float[] crop = CropRgba(layer.Rgba, layer.Width, x0, y0, x1 - x0, y1 - y0);
                                depthStats = ComputeDepthStats(crop);
                                cropPath = Path.Combine(folder, $"{prefix}_cascade{snapshot.CascadeIndex}.png");
                                WriteDepthPng(cropPath, crop, x1 - x0, y1 - y0);
                            }
                        }

                        exported.Add(new
                        {
                            source = snapshot.Source.ToString(),
                            cascade_index = snapshot.CascadeIndex,
                            cascade_count = snapshot.CascadeCount,
                            rendered_world_to_light = MatrixRows(snapshot.RenderedWorldToLight),
                            current_world_to_light = MatrixRows(snapshot.CurrentWorldToLight),
                            rendered_split_blend_bias = new[] { snapshot.RenderedSplitBlendBias.X, snapshot.RenderedSplitBlendBias.Y, snapshot.RenderedSplitBlendBias.Z, snapshot.RenderedSplitBlendBias.W },
                            uv_scale_bias = new[] { snapshot.UvScaleBias.X, snapshot.UvScaleBias.Y, snapshot.UvScaleBias.Z, snapshot.UvScaleBias.W },
                            page_index = snapshot.PageIndex,
                            layer_index = snapshot.PageIndex,
                            crop_pixel_rect = cropRect,
                            crop_depth = depthStats,
                            encoding = snapshot.Encoding.ToString(),
                            resident = snapshot.Resident,
                            sampleable = snapshot.Sampleable,
                            static_cache_backed = snapshot.StaticCacheBacked,
                            active_fallback = snapshot.ActiveFallback.ToString(),
                            last_rendered_frame = snapshot.LastRenderedFrame,
                            atlas_frame = snapshot.AtlasFrame,
                            layer_png = layerPath,
                            cascade_png = cropPath,
                            read_failure = readFailure,
                        });
                    }

                    File.WriteAllText(Path.Combine(folder, $"{prefix}.json"),
                        JsonSerializer.Serialize(exported, new JsonSerializerOptions { WriteIndented = true }));
                    tcs.TrySetResult(exported);
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
                    AbstractRenderer? renderer = AbstractRenderer.Current;
                    if (renderer is null || Interlocked.CompareExchange(ref started, 1, 0) != 0)
                        return;
                    window.PostRenderViewportsCallback -= deferredHandler;
                    Export(renderer);
                };
                window.PostRenderViewportsCallback += deferredHandler;
            }

            if (Engine.IsRenderThread)
                Schedule();
            else
                Engine.InvokeOnMainThread(Schedule, "MCP: Export directional shadow cascades", executeNowIfAlreadyMainThread: true);

            using var registration = token.Register(() =>
            {
                if (deferredHandler is not null)
                    window.PostRenderViewportsCallback -= deferredHandler;
                tcs.TrySetCanceled(token);
            });

            List<object> result = await tcs.Task;
            return new McpToolResponse(
                $"Exported {result.Count} {requestSource} directional cascade(s) to '{folder}'.",
                new { output_dir = folder, json = Path.Combine(folder, $"{prefix}.json"), cascades = result });
        }

        /// <summary>
        /// Copies the depth channel (red) into RGB and sets alpha to one, so the debug image writer can normalize it.
        /// </summary>
        private static float[] ToDepthGrayscale(float[] rgba)
        {
            float[] gray = new float[rgba.Length];
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                float depth = rgba[i];
                gray[i] = depth;
                gray[i + 1] = depth;
                gray[i + 2] = depth;
                gray[i + 3] = 1.0f;
            }
            return gray;
        }

        /// <summary>
        /// Raw depth statistics of a grayscale crop: minimum, maximum and mean, and the share of texels at the
        /// minimum and at the maximum value, which shows a cleared or never written tile.
        /// </summary>
        private static object ComputeDepthStats(float[] grayRgba)
        {
            int count = grayRgba.Length / 4;
            float min = float.MaxValue, max = float.MinValue;
            double sum = 0.0;
            for (int i = 0; i < grayRgba.Length; i += 4)
            {
                float d = grayRgba[i];
                min = MathF.Min(min, d);
                max = MathF.Max(max, d);
                sum += d;
            }
            int atMin = 0, atMax = 0;
            for (int i = 0; i < grayRgba.Length; i += 4)
            {
                if (grayRgba[i] == min) atMin++;
                if (grayRgba[i] == max) atMax++;
            }
            return new
            {
                min,
                max,
                mean = count > 0 ? sum / count : 0.0,
                share_at_min = count > 0 ? (double)atMin / count : 0.0,
                share_at_max = count > 0 ? (double)atMax / count : 0.0,
            };
        }

        private static float[] CropRgba(float[] rgba, int width, int x, int y, int cropWidth, int cropHeight)
        {
            float[] crop = new float[cropWidth * cropHeight * 4];
            for (int row = 0; row < cropHeight; row++)
                Array.Copy(rgba, ((y + row) * width + x) * 4, crop, row * cropWidth * 4, cropWidth * 4);
            return crop;
        }

        /// <summary>Writes depth values as a grayscale PNG, normalized to the min and max of the image.</summary>
        private static void WriteDepthPng(string path, float[] grayRgba, int width, int height)
        {
            using RuntimeImage image = CreateDebugImage(
                grayRgba, width, height, ComputeRgbaStats(grayRgba),
                normalize: true, preserveAlpha: false, tonemap: null,
                exposure: 1.0f, gamma: 2.2f, mobiusTransition: 0.6f, encodeSrgb: false, flipVertically: false);
            File.WriteAllBytes(path, RuntimeImageCodecs.Require().EncodePng(image));
        }

        private static float[][] MatrixRows(System.Numerics.Matrix4x4 m)
            =>
            [
                [m.M11, m.M12, m.M13, m.M14],
                [m.M21, m.M22, m.M23, m.M24],
                [m.M31, m.M32, m.M33, m.M34],
                [m.M41, m.M42, m.M43, m.M44],
            ];
    }
}
