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
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        [XRMcp(Name = "list_render_pipeline_resources", Permission = McpPermissionLevel.ReadOnly)]
        [Description("List live render-pipeline textures and framebuffers for the selected viewport.")]
        public static Task<McpToolResponse> ListRenderPipelineResourcesAsync(
            McpToolContext context,
            [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
            [McpName("window_index"), Description("Optional window index to target.")] int windowIndex = 0,
            [McpName("viewport_index"), Description("Optional viewport index to target.")] int viewportIndex = 0)
        {
            XRViewport? viewport = ResolveViewport(context.WorldInstance, cameraNodeId, windowIndex, viewportIndex);
            if (viewport is null)
                return Task.FromResult(new McpToolResponse("No viewport found.", isError: true));

            XRRenderPipelineInstance instance = viewport.RenderPipelineInstance;
            RenderResourceRegistry resources = instance.Resources;

            var textures = resources.TextureRecords
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => DescribeTextureRecord(pair.Key, pair.Value))
                .ToArray();

            var frameBuffers = resources.FrameBufferRecords
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => DescribeFrameBufferRecord(pair.Key, pair.Value))
                .ToArray();

            return Task.FromResult(new McpToolResponse(
                $"Listed {textures.Length} texture(s) and {frameBuffers.Length} framebuffer(s).",
                new
                {
                    viewport = new
                    {
                        index = viewport.Index,
                        width = viewport.Width,
                        height = viewport.Height,
                        internal_width = viewport.InternalWidth,
                        internal_height = viewport.InternalHeight,
                    },
                    pipeline = new
                    {
                        instance_id = instance.InstanceId,
                        debug_name = instance.DebugName,
                        type = instance.Pipeline?.GetType().FullName,
                        resource_generation = instance.ResourceGeneration,
                        descriptor_revision = resources.DescriptorRevision,
                        descriptor_signature = resources.DescriptorSignature,
                    },
                    texture_count = textures.Length,
                    framebuffer_count = frameBuffers.Length,
                    textures,
                    framebuffers = frameBuffers,
                }));
        }

        [XRMcp(Name = "capture_render_pipeline_texture", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Capture a named live render-pipeline texture to a PNG and report pixel statistics.")]
        public static async Task<McpToolResponse> CaptureRenderPipelineTextureAsync(
            McpToolContext context,
            [McpName("texture_name"), Description("Render pipeline texture resource name to capture.")] string textureName,
            [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
            [McpName("window_index"), Description("Optional window index to target.")] int windowIndex = 0,
            [McpName("viewport_index"), Description("Optional viewport index to target.")] int viewportIndex = 0,
            [McpName("output_dir"), Description("Optional directory to write the texture capture into.")] string? outputDir = null,
            [McpName("mip_level"), Description("Mip level to capture.")] int mipLevel = 0,
            [McpName("layer_index"), Description("Array/cube layer index to capture.")] int layerIndex = 0,
            [McpName("normalize"), Description("Normalize RGB values to the captured min/max range before writing.")] bool normalize = false,
            [McpName("flip_vertically"), Description("Flip the exported image vertically.")] bool flipVertically = true,
            [McpName("preserve_alpha"), Description("Preserve captured alpha in the PNG. Defaults to opaque output for easier inspection.")] bool preserveAlpha = false,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(textureName))
                return new McpToolResponse("texture_name is required.", isError: true);

            XRViewport? viewport = ResolveViewport(context.WorldInstance, cameraNodeId, windowIndex, viewportIndex);
            if (viewport is null)
                return new McpToolResponse("No viewport found.", isError: true);

            string folder = outputDir ?? Path.Combine(Environment.CurrentDirectory, "McpCaptures", "RenderPipeline");
            string safeTextureName = SanitizeFileName(textureName);
            string fileName = $"RenderPipeline_{safeTextureName}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            string path = Path.Combine(folder, fileName);

            Utility.EnsureDirPathExists(path);

            var tcs = new TaskCompletionSource<PipelineTextureCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action? deferredHandler = null;

            XRWindow? window = viewport.Window ?? Engine.Windows.FirstOrDefault(w => w.Viewports.Contains(viewport));
            if (window is null)
                return new McpToolResponse("No window found for the target viewport.", isError: true);

            void BeginCapture(AbstractRenderer renderer)
            {
                try
                {
                    PipelineTextureCaptureResult result = CapturePipelineTexture(
                        renderer,
                        viewport.RenderPipelineInstance,
                        textureName,
                        path,
                        mipLevel,
                        layerIndex,
                        normalize,
                        flipVertically,
                        preserveAlpha);

                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }

            void ScheduleCaptureOnRenderThread()
            {
                if (AbstractRenderer.Current is { } currentRenderer)
                {
                    BeginCapture(currentRenderer);
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
                    BeginCapture(renderer);
                };

                window.RenderViewportsCallback += deferredHandler;
            }

            if (Engine.IsRenderThread)
                ScheduleCaptureOnRenderThread();
            else
                Engine.InvokeOnMainThread(ScheduleCaptureOnRenderThread, "MCP: Capture render pipeline texture", executeNowIfAlreadyMainThread: true);

            using var reg = token.Register(() =>
            {
                if (deferredHandler is not null)
                    window.RenderViewportsCallback -= deferredHandler;

                tcs.TrySetCanceled(token);
            });

            try
            {
                PipelineTextureCaptureResult result = await tcs.Task;
                return new McpToolResponse(
                    $"Captured render pipeline texture '{textureName}' to '{result.Path}'.",
                    new
                    {
                        texture_name = textureName,
                        path = result.Path,
                        width = result.Width,
                        height = result.Height,
                        mip_level = mipLevel,
                        layer_index = layerIndex,
                        normalized = normalize,
                        flipped_vertically = flipVertically,
                        preserve_alpha = preserveAlpha,
                        stats = result.Stats,
                    });
            }
            catch (Exception ex)
            {
                return new McpToolResponse($"Failed to capture render pipeline texture '{textureName}': {ex.Message}", isError: true);
            }
        }

        private static object DescribeTextureRecord(string name, RenderTextureResource record)
        {
            XRTexture? texture = record.Instance;
            object? size = null;
            string? attachment = null;

            if (texture is not null)
            {
                Vector3 dims = texture.WidthHeightDepth;
                size = new
                {
                    width = dims.X,
                    height = dims.Y,
                    depth = dims.Z,
                };

                if (texture.FrameBufferAttachment is { } fboAttachment)
                    attachment = fboAttachment.ToString();
            }

            TextureResourceDescriptor descriptor = record.Descriptor;
            return new
            {
                name,
                has_instance = texture is not null,
                instance_name = texture?.Name,
                instance_type = texture?.GetType().FullName,
                size,
                framebuffer_attachment = attachment,
                descriptor = new
                {
                    lifetime = descriptor.Lifetime.ToString(),
                    size_class = descriptor.SizePolicy.SizeClass.ToString(),
                    scale_x = descriptor.SizePolicy.ScaleX,
                    scale_y = descriptor.SizePolicy.ScaleY,
                    width = descriptor.SizePolicy.Width,
                    height = descriptor.SizePolicy.Height,
                    format_label = descriptor.FormatLabel,
                    internal_format = descriptor.InternalFormat?.ToString(),
                    pixel_format = descriptor.PixelFormat?.ToString(),
                    pixel_type = descriptor.PixelType?.ToString(),
                    sized_internal_format = descriptor.SizedInternalFormat?.ToString(),
                    samples = descriptor.Samples,
                    array_layers = descriptor.ArrayLayers,
                    kind = descriptor.Kind.ToString(),
                    usage = descriptor.Usage.ToString(),
                    supports_aliasing = descriptor.SupportsAliasing,
                    requires_storage_usage = descriptor.RequiresStorageUsage,
                    source_texture_name = descriptor.SourceTextureName,
                    base_mip_level = descriptor.BaseMipLevel,
                    mip_level_count = descriptor.MipLevelCount,
                    base_layer = descriptor.BaseLayer,
                    layer_count = descriptor.LayerCount,
                    depth_stencil_aspect = descriptor.DepthStencilAspect.ToString(),
                    array_target = descriptor.ArrayTarget,
                    multisample = descriptor.Multisample,
                },
            };
        }

        private static object DescribeFrameBufferRecord(string name, RenderFrameBufferResource record)
        {
            XRFrameBuffer? frameBuffer = record.Instance;
            FrameBufferResourceDescriptor descriptor = record.Descriptor;
            return new
            {
                name,
                has_instance = frameBuffer is not null,
                instance_name = frameBuffer?.Name,
                instance_type = frameBuffer?.GetType().FullName,
                target_count = frameBuffer?.Targets?.Length ?? 0,
                descriptor = new
                {
                    lifetime = descriptor.Lifetime.ToString(),
                    size_class = descriptor.SizePolicy.SizeClass.ToString(),
                    scale_x = descriptor.SizePolicy.ScaleX,
                    scale_y = descriptor.SizePolicy.ScaleY,
                    width = descriptor.SizePolicy.Width,
                    height = descriptor.SizePolicy.Height,
                    attachments = descriptor.Attachments
                        .Select(static attachment => new
                        {
                            resource_name = attachment.ResourceName,
                            attachment = attachment.Attachment.ToString(),
                            mip_level = attachment.MipLevel,
                            layer_index = attachment.LayerIndex,
                        })
                        .ToArray(),
                },
            };
        }

        private static PipelineTextureCaptureResult CapturePipelineTexture(
            AbstractRenderer renderer,
            XRRenderPipelineInstance instance,
            string textureName,
            string path,
            int mipLevel,
            int layerIndex,
            bool normalize,
            bool flipVertically,
            bool preserveAlpha)
        {
            if (!instance.Resources.TextureRecords.TryGetValue(textureName, out RenderTextureResource? record))
                throw new InvalidOperationException($"Texture resource '{textureName}' was not found.");

            if (record.Instance is not XRTexture texture)
                throw new InvalidOperationException($"Texture resource '{textureName}' has no live texture instance.");

            if (!renderer.TryReadTextureMipRgbaFloat(texture, mipLevel, layerIndex, out float[]? rgbaFloats, out int width, out int height, out string failure) ||
                rgbaFloats is null)
            {
                throw new InvalidOperationException(failure);
            }

            RgbaFloatStats stats = ComputeRgbaStats(rgbaFloats);
            using MagickImage image = CreateDebugImage(rgbaFloats, width, height, stats, normalize, preserveAlpha);

            if (flipVertically)
                image.Flip();

            image.Write(path);

            return new PipelineTextureCaptureResult(path, width, height, stats);
        }

        private static RgbaFloatStats ComputeRgbaStats(float[] rgbaFloats)
        {
            float minRgb = float.PositiveInfinity;
            float maxRgb = float.NegativeInfinity;
            double rgbSum = 0.0;
            int finiteRgbSamples = 0;
            int nonFiniteSamples = 0;
            float minAlpha = float.PositiveInfinity;
            float maxAlpha = float.NegativeInfinity;
            double alphaSum = 0.0;
            int finiteAlphaSamples = 0;

            for (int i = 0; i < rgbaFloats.Length; i += 4)
            {
                for (int c = 0; c < 3; ++c)
                {
                    float value = rgbaFloats[i + c];
                    if (!float.IsFinite(value))
                    {
                        nonFiniteSamples++;
                        continue;
                    }

                    minRgb = MathF.Min(minRgb, value);
                    maxRgb = MathF.Max(maxRgb, value);
                    rgbSum += value;
                    finiteRgbSamples++;
                }

                float alpha = rgbaFloats[i + 3];
                if (!float.IsFinite(alpha))
                {
                    nonFiniteSamples++;
                    continue;
                }

                minAlpha = MathF.Min(minAlpha, alpha);
                maxAlpha = MathF.Max(maxAlpha, alpha);
                alphaSum += alpha;
                finiteAlphaSamples++;
            }

            if (finiteRgbSamples == 0)
            {
                minRgb = 0.0f;
                maxRgb = 0.0f;
            }

            if (finiteAlphaSamples == 0)
            {
                minAlpha = 0.0f;
                maxAlpha = 0.0f;
            }

            return new RgbaFloatStats(
                rgbaFloats.Length / 4,
                finiteRgbSamples,
                nonFiniteSamples,
                minRgb,
                maxRgb,
                finiteRgbSamples == 0 ? 0.0f : (float)(rgbSum / finiteRgbSamples),
                minAlpha,
                maxAlpha,
                finiteAlphaSamples == 0 ? 0.0f : (float)(alphaSum / finiteAlphaSamples));
        }

        private static MagickImage CreateDebugImage(
            float[] rgbaFloats,
            int width,
            int height,
            RgbaFloatStats stats,
            bool normalize,
            bool preserveAlpha)
        {
            byte[] rgba8 = new byte[rgbaFloats.Length];
            bool canNormalize = normalize && stats.MaxRgb > stats.MinRgb;
            float normalizeScale = canNormalize ? 1.0f / (stats.MaxRgb - stats.MinRgb) : 1.0f;

            for (int i = 0; i < rgbaFloats.Length; i += 4)
            {
                for (int c = 0; c < 3; ++c)
                {
                    float value = rgbaFloats[i + c];
                    if (!float.IsFinite(value))
                        value = 0.0f;

                    value = canNormalize
                        ? (value - stats.MinRgb) * normalizeScale
                        : Math.Clamp(value, 0.0f, 1.0f);

                    rgba8[i + c] = ToByte(value);
                }

                if (preserveAlpha)
                {
                    float alpha = rgbaFloats[i + 3];
                    rgba8[i + 3] = ToByte(float.IsFinite(alpha) ? Math.Clamp(alpha, 0.0f, 1.0f) : 1.0f);
                }
                else
                {
                    rgba8[i + 3] = byte.MaxValue;
                }
            }

            return new MagickImage(rgba8, new MagickReadSettings
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = MagickFormat.Rgba,
                Depth = 8,
            });
        }

        private static byte ToByte(float value)
            => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f), 0, 255);

        private static string SanitizeFileName(string value)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            string sanitized = new(value.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? "texture" : sanitized;
        }

        private sealed record PipelineTextureCaptureResult(string Path, int Width, int Height, RgbaFloatStats Stats);

        private readonly record struct RgbaFloatStats(
            int PixelCount,
            int FiniteRgbSamples,
            int NonFiniteSamples,
            float MinRgb,
            float MaxRgb,
            float AverageRgb,
            float MinAlpha,
            float MaxAlpha,
            float AverageAlpha);
    }
}
