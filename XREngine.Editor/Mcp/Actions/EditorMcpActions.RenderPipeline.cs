using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
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
        [Description("Capture a named live render-pipeline texture to PNG, EXR, or Radiance HDR and report pixel statistics.")]
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
            [McpName("flip_vertically"), Description("Optional override for vertical export orientation. Omit for automatic render API + clip-space policy handling.")] bool? flipVertically = null,
            [McpName("preserve_alpha"), Description("Preserve captured alpha in the PNG. Defaults to opaque output for easier inspection.")] bool preserveAlpha = false,
            [McpName("output_format"), Description("Output format: png, exr, or hdr. PNG is LDR; EXR/HDR preserve direct HDR values.")] string outputFormat = "png",
            [McpName("tonemap"), Description("Optional PNG LDR tonemap: Linear, Gamma, Clip, Reinhard, Hable, Mobius, ACES, Neutral, Filmic, AgX, or GT7. Omit to keep clamp/normalize debug output.")] string? tonemap = null,
            [McpName("exposure"), Description("Exposure multiplier used when tonemap is supplied.")] float exposure = 1.0f,
            [McpName("gamma"), Description("Gamma used by Gamma tonemapping and optional sRGB encoding.")] float gamma = 2.2f,
            [McpName("mobius_transition"), Description("Mobius transition value used by Mobius tonemapping.")] float mobiusTransition = 0.6f,
            [McpName("encode_srgb"), Description("For tonemapped PNG output, encode display-linear values to sRGB/gamma. Defaults on.")] bool encodeSrgb = true,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(textureName))
                return new McpToolResponse("texture_name is required.", isError: true);

            if (!TryParsePipelineCaptureFormat(outputFormat, out PipelineTextureOutputFormat format, out string formatFailure))
                return new McpToolResponse(formatFailure, isError: true);

            ETonemappingType? tonemapType = null;
            if (!string.IsNullOrWhiteSpace(tonemap))
            {
                if (format != PipelineTextureOutputFormat.Png)
                    return new McpToolResponse("tonemap is only valid with output_format='png'. Use output_format='exr' or 'hdr' for direct HDR export.", isError: true);

                if (!Enum.TryParse(tonemap, ignoreCase: true, out ETonemappingType parsedTonemap))
                    return new McpToolResponse($"Unsupported tonemap '{tonemap}'. Supported values: {string.Join(", ", Enum.GetNames<ETonemappingType>())}.", isError: true);

                tonemapType = parsedTonemap;
            }

            XRViewport? viewport = ResolveViewport(context.WorldInstance, cameraNodeId, windowIndex, viewportIndex);
            if (viewport is null)
                return new McpToolResponse("No viewport found.", isError: true);

            string folder = outputDir ?? Path.Combine(Environment.CurrentDirectory, "McpCaptures", "RenderPipeline");
            string safeTextureName = SanitizeFileName(textureName);
            string fileName = $"RenderPipeline_{safeTextureName}_{DateTime.Now:yyyyMMdd_HHmmss}.{GetPipelineCaptureExtension(format)}";
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
                    PipelineCaptureOrientation orientation = ResolvePipelineCaptureOrientation(renderer, window, flipVertically);
                    PipelineTextureCaptureResult result = CapturePipelineTexture(
                        renderer,
                        viewport.RenderPipelineInstance,
                        textureName,
                        path,
                        mipLevel,
                        layerIndex,
                        normalize,
                        orientation,
                        preserveAlpha,
                        format,
                        tonemapType,
                        exposure,
                        gamma,
                        mobiusTransition,
                        encodeSrgb);

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
                        output_format = format.ToString().ToLowerInvariant(),
                        normalized = normalize,
                        flipped_vertically = result.Orientation.FlipVertically,
                        auto_flip_vertically = result.Orientation.AutoFlipVertically,
                        flip_vertically_override = result.Orientation.OverrideRequested,
                        render_backend = result.Orientation.Backend.ToString(),
                        clip_space_y_direction = result.Orientation.ClipSpaceYDirection.ToString(),
                        framebuffer_texture_y_direction = result.Orientation.FramebufferTextureYDirection.ToString(),
                        preserve_alpha = preserveAlpha,
                        tonemap = tonemapType?.ToString(),
                        exposure,
                        gamma,
                        mobius_transition = mobiusTransition,
                        encode_srgb = encodeSrgb,
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
            PipelineCaptureOrientation orientation,
            bool preserveAlpha,
            PipelineTextureOutputFormat outputFormat,
            ETonemappingType? tonemap,
            float exposure,
            float gamma,
            float mobiusTransition,
            bool encodeSrgb)
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
            switch (outputFormat)
            {
                case PipelineTextureOutputFormat.Exr:
                    WriteExr(path, rgbaFloats, width, height, orientation.FlipVertically);
                    break;
                case PipelineTextureOutputFormat.Hdr:
                    WriteRadianceHdr(path, rgbaFloats, width, height, orientation.FlipVertically);
                    break;
                default:
                    using (MagickImage image = CreateDebugImage(
                        rgbaFloats,
                        width,
                        height,
                        stats,
                        normalize,
                        preserveAlpha,
                        tonemap,
                        exposure,
                        gamma,
                        mobiusTransition,
                        encodeSrgb))
                    {
                        if (orientation.FlipVertically)
                            image.Flip();

                        image.Write(path);
                    }
                    break;
            }

            return new PipelineTextureCaptureResult(path, width, height, stats, orientation);
        }

        private static PipelineCaptureOrientation ResolvePipelineCaptureOrientation(
            AbstractRenderer renderer,
            XRWindow window,
            bool? flipVerticallyOverride)
        {
            RuntimeGraphicsApiKind backend = RuntimeRenderingHostServices.Current.GetWindowRenderBackend(window);
            if (backend == RuntimeGraphicsApiKind.Unknown)
                backend = RuntimeRenderingHostServices.Current.CurrentRenderBackend;

            ERenderClipSpaceYDirection clipY = RuntimeRenderingHostServices.Current.ClipSpaceYDirection;
            ERenderClipSpaceYDirection framebufferY = backend == RuntimeGraphicsApiKind.Unknown
                ? (renderer.ScreenshotRequiresVerticalFlip ? ERenderClipSpaceYDirection.YUp : ERenderClipSpaceYDirection.YDown)
                : RenderClipSpacePolicy.FramebufferTextureYDirection(backend);

            bool autoFlip = framebufferY == ERenderClipSpaceYDirection.YUp;
            return new PipelineCaptureOrientation(
                backend,
                clipY,
                framebufferY,
                flipVerticallyOverride ?? autoFlip,
                autoFlip,
                flipVerticallyOverride.HasValue);
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
            bool preserveAlpha,
            ETonemappingType? tonemap,
            float exposure,
            float gamma,
            float mobiusTransition,
            bool encodeSrgb)
        {
            byte[] rgba8 = new byte[rgbaFloats.Length];
            bool canNormalize = tonemap is null && normalize && stats.MaxRgb > stats.MinRgb;
            float normalizeScale = canNormalize ? 1.0f / (stats.MaxRgb - stats.MinRgb) : 1.0f;
            float safeGamma = MathF.Max(0.0001f, gamma);

            for (int i = 0; i < rgbaFloats.Length; i += 4)
            {
                Vector3 rgb = new(
                    SanitizeFinite(rgbaFloats[i]),
                    SanitizeFinite(rgbaFloats[i + 1]),
                    SanitizeFinite(rgbaFloats[i + 2]));

                if (tonemap is { } tonemapType)
                {
                    rgb = ApplyTonemap(rgb, tonemapType, exposure, safeGamma, mobiusTransition);
                    if (encodeSrgb)
                        rgb = LinearToGamma(rgb, safeGamma);
                }
                else if (canNormalize)
                {
                    rgb = (rgb - new Vector3(stats.MinRgb)) * normalizeScale;
                }

                rgba8[i] = ToByte(rgb.X);
                rgba8[i + 1] = ToByte(rgb.Y);
                rgba8[i + 2] = ToByte(rgb.Z);

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

        private static void WriteExr(string path, float[] rgbaFloats, int width, int height, bool flipVertically)
        {
            float[] source = flipVertically
                ? CreateVerticallyFlippedCopy(rgbaFloats, width, height)
                : SanitizeHdrCopy(rgbaFloats);

            using MagickImage image = new(MagickColors.Transparent, (uint)width, (uint)height);
            image.ImportPixels(source, new PixelImportSettings((uint)width, (uint)height, StorageType.Quantum, PixelMapping.RGBA));
            image.Depth = 32;
            image.ColorSpace = ColorSpace.RGB;
            image.Write(path, MagickFormat.Exr);
        }

        private static void WriteRadianceHdr(string path, float[] rgbaFloats, int width, int height, bool flipVertically)
        {
            using FileStream stream = File.Create(path);
            byte[] header = Encoding.ASCII.GetBytes($"#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {height} +X {width}\n");
            stream.Write(header);

            Span<byte> rgbe = stackalloc byte[4];
            for (int y = 0; y < height; ++y)
            {
                int sourceY = flipVertically ? height - 1 - y : y;
                int rowOffset = sourceY * width * 4;
                for (int x = 0; x < width; ++x)
                {
                    int i = rowOffset + x * 4;
                    EncodeRadianceRgbe(
                        SanitizeFinite(rgbaFloats[i]),
                        SanitizeFinite(rgbaFloats[i + 1]),
                        SanitizeFinite(rgbaFloats[i + 2]),
                        rgbe);
                    stream.Write(rgbe);
                }
            }
        }

        private static void EncodeRadianceRgbe(float r, float g, float b, Span<byte> rgbe)
        {
            r = MathF.Max(0.0f, r);
            g = MathF.Max(0.0f, g);
            b = MathF.Max(0.0f, b);

            float max = MathF.Max(r, MathF.Max(g, b));
            if (max < 1.0e-32f)
            {
                rgbe.Clear();
                return;
            }

            int exponent = (int)MathF.Floor(MathF.Log2(max)) + 1;
            float scale = MathF.Pow(2.0f, -exponent) * 256.0f;
            rgbe[0] = ToHdrByte(r * scale);
            rgbe[1] = ToHdrByte(g * scale);
            rgbe[2] = ToHdrByte(b * scale);
            rgbe[3] = (byte)Math.Clamp(exponent + 128, 0, 255);
        }

        private static byte ToHdrByte(float value)
            => (byte)Math.Clamp((int)value, 0, 255);

        private static float[] CreateVerticallyFlippedCopy(float[] source, int width, int height)
        {
            int rowLength = width * 4;
            float[] copy = new float[source.Length];
            for (int y = 0; y < height; ++y)
            {
                int sourceOffset = y * rowLength;
                int destinationOffset = (height - 1 - y) * rowLength;
                for (int i = 0; i < rowLength; ++i)
                    copy[destinationOffset + i] = SanitizeFinite(source[sourceOffset + i]);
            }

            return copy;
        }

        private static float[] SanitizeHdrCopy(float[] source)
        {
            float[] copy = new float[source.Length];
            for (int i = 0; i < source.Length; ++i)
                copy[i] = SanitizeFinite(source[i]);
            return copy;
        }

        private static float SanitizeFinite(float value)
            => float.IsFinite(value) ? value : 0.0f;

        private static Vector3 ApplyTonemap(Vector3 hdr, ETonemappingType type, float exposure, float gamma, float mobiusTransition)
        {
            hdr = Vector3.Max(hdr, Vector3.Zero);
            return type switch
            {
                ETonemappingType.Linear => hdr * exposure,
                ETonemappingType.Gamma => LinearToGamma(hdr * exposure, gamma),
                ETonemappingType.Clip => Vector3.Clamp(hdr * exposure, Vector3.Zero, Vector3.One),
                ETonemappingType.Reinhard => Reinhard(hdr, exposure),
                ETonemappingType.Hable => Hable(hdr, exposure),
                ETonemappingType.Mobius => Mobius(hdr, exposure, mobiusTransition),
                ETonemappingType.ACES => Aces(hdr, exposure),
                ETonemappingType.Neutral => Neutral(hdr, exposure),
                ETonemappingType.Filmic => Filmic(hdr, exposure),
                ETonemappingType.AgX => AgX(hdr, exposure),
                ETonemappingType.GT7 => Gt7(hdr, exposure),
                _ => Reinhard(hdr, exposure),
            };
        }

        private static Vector3 LinearToGamma(Vector3 linear, float gamma)
        {
            float invGamma = 1.0f / MathF.Max(0.0001f, gamma);
            return new Vector3(
                MathF.Pow(MathF.Max(0.0f, linear.X), invGamma),
                MathF.Pow(MathF.Max(0.0f, linear.Y), invGamma),
                MathF.Pow(MathF.Max(0.0f, linear.Z), invGamma));
        }

        private static Vector3 Reinhard(Vector3 hdr, float exposure)
        {
            Vector3 x = hdr * exposure;
            return x / (x + Vector3.One);
        }

        private static Vector3 Hable(Vector3 hdr, float exposure)
        {
            const float a = 0.15f, b = 0.50f, c = 0.10f, d = 0.20f, e = 0.02f, f = 0.30f;
            Vector3 x = Vector3.Max(hdr * exposure - new Vector3(e), Vector3.Zero);
            return ((x * (a * x + new Vector3(c * b)) + new Vector3(d * e)) / (x * (a * x + new Vector3(b)) + new Vector3(d * f))) - new Vector3(e / f);
        }

        private static Vector3 Mobius(Vector3 hdr, float exposure, float transition)
        {
            float a = MathF.Max(transition, 0.0001f);
            Vector3 x = hdr * exposure;
            return (x * (a + 1.0f)) / (x + new Vector3(a));
        }

        private static Vector3 Aces(Vector3 hdr, float exposure)
        {
            Vector3 x = hdr * exposure;
            return (x * (2.51f * x + new Vector3(0.03f))) / (x * (2.43f * x + new Vector3(0.59f)) + new Vector3(0.14f));
        }

        private static Vector3 Neutral(Vector3 hdr, float exposure)
        {
            Vector3 x = hdr * exposure;
            return (x * (x + new Vector3(0.0245786f))) / (x * (0.983729f * x + new Vector3(0.432951f)) + new Vector3(0.238081f));
        }

        private static Vector3 Filmic(Vector3 hdr, float exposure)
        {
            Vector3 x = Vector3.Max(hdr * exposure - new Vector3(0.004f), Vector3.Zero);
            return (x * (6.2f * x + new Vector3(0.5f))) / (x * (6.2f * x + new Vector3(1.7f)) + new Vector3(0.06f));
        }

        private static Vector3 AgX(Vector3 hdr, float exposure)
        {
            Vector3 color = Vector3.Max(hdr * exposure, Vector3.Zero);
            color = Transform(color, new Matrix3(
                0.6274f, 0.0691f, 0.0164f,
                0.3293f, 0.9195f, 0.0880f,
                0.0433f, 0.0113f, 0.8956f));
            color = Transform(color, new Matrix3(
                0.856627153f, 0.137318973f, 0.111898213f,
                0.095121241f, 0.761241991f, 0.076799419f,
                0.048251606f, 0.101439036f, 0.811302369f));
            color = new Vector3(
                MathF.Log2(MathF.Max(color.X, 1.0e-10f)),
                MathF.Log2(MathF.Max(color.Y, 1.0e-10f)),
                MathF.Log2(MathF.Max(color.Z, 1.0e-10f)));

            const float minEv = -12.47393f;
            const float maxEv = 4.026069f;
            color = Vector3.Clamp((color - new Vector3(minEv)) / (maxEv - minEv), Vector3.Zero, Vector3.One);
            color = AgXDefaultContrast(color);
            color = Transform(color, new Matrix3(
                 1.127100582f, -0.141329763f, -0.141329763f,
                -0.110606643f,  1.157823702f, -0.110606643f,
                -0.016493939f, -0.016493939f,  1.251936407f));
            color = new Vector3(
                MathF.Pow(MathF.Max(color.X, 0.0f), 2.2f),
                MathF.Pow(MathF.Max(color.Y, 0.0f), 2.2f),
                MathF.Pow(MathF.Max(color.Z, 0.0f), 2.2f));
            color = Transform(color, new Matrix3(
                 1.6605f, -0.1246f, -0.0182f,
                -0.5876f,  1.1329f, -0.1006f,
                -0.0728f, -0.0083f,  1.1187f));
            return Vector3.Clamp(color, Vector3.Zero, Vector3.One);
        }

        private static Vector3 AgXDefaultContrast(Vector3 x)
        {
            Vector3 x2 = x * x;
            Vector3 x4 = x2 * x2;
            return 15.5f * x4 * x2
                - 40.14f * x4 * x
                + 31.96f * x4
                - 6.868f * x2 * x
                + 0.4298f * x2
                + 0.1191f * x
                - new Vector3(0.00232f);
        }

        private static Vector3 Gt7(Vector3 hdr, float exposure)
        {
            const float p = 1.0f;
            const float a = 1.0f;
            const float m = 0.22f;
            const float l = 0.4f;
            const float c = 1.33f;
            const float b = 0.0f;

            Vector3 x = Vector3.Max(hdr * exposure, Vector3.Zero);
            float l0 = ((p - m) * l) / a;
            float s0 = m + l0;
            float s1 = m + a * l0;
            float c2 = (a * p) / MathF.Max(p - s1, 1.0e-5f);
            float cp = -c2 / p;

            Vector3 w0 = Vector3.One - SmoothStep(Vector3.Zero, new Vector3(m), x);
            Vector3 w2 = Step(new Vector3(m + l0), x);
            Vector3 w1 = Vector3.One - w0 - w2;

            Vector3 toe = new(
                m * MathF.Pow(MathF.Max(x.X / m, 0.0f), c) + b,
                m * MathF.Pow(MathF.Max(x.Y / m, 0.0f), c) + b,
                m * MathF.Pow(MathF.Max(x.Z / m, 0.0f), c) + b);
            Vector3 shoulder = new(
                p - ((p - s1) * MathF.Exp(cp * (x.X - s0))),
                p - ((p - s1) * MathF.Exp(cp * (x.Y - s0))),
                p - ((p - s1) * MathF.Exp(cp * (x.Z - s0))));
            Vector3 linear = new Vector3(m) + a * (x - new Vector3(m));

            return Vector3.Clamp(toe * w0 + linear * w1 + shoulder * w2, Vector3.Zero, new Vector3(p));
        }

        private static Vector3 SmoothStep(Vector3 edge0, Vector3 edge1, Vector3 x)
        {
            Vector3 t = Vector3.Clamp((x - edge0) / (edge1 - edge0), Vector3.Zero, Vector3.One);
            return t * t * (new Vector3(3.0f) - 2.0f * t);
        }

        private static Vector3 Step(Vector3 edge, Vector3 x)
            => new(x.X < edge.X ? 0.0f : 1.0f, x.Y < edge.Y ? 0.0f : 1.0f, x.Z < edge.Z ? 0.0f : 1.0f);

        private static Vector3 Transform(Vector3 v, Matrix3 m)
            => new(
                m.M11 * v.X + m.M21 * v.Y + m.M31 * v.Z,
                m.M12 * v.X + m.M22 * v.Y + m.M32 * v.Z,
                m.M13 * v.X + m.M23 * v.Y + m.M33 * v.Z);

        private readonly record struct Matrix3(
            float M11, float M12, float M13,
            float M21, float M22, float M23,
            float M31, float M32, float M33);

        private static byte ToByte(float value)
            => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f), 0, 255);

        private static bool TryParsePipelineCaptureFormat(string? value, out PipelineTextureOutputFormat format, out string failure)
        {
            failure = string.Empty;
            string normalized = string.IsNullOrWhiteSpace(value)
                ? "png"
                : value.Trim().TrimStart('.').ToLowerInvariant();

            switch (normalized)
            {
                case "png":
                    format = PipelineTextureOutputFormat.Png;
                    return true;
                case "exr":
                    format = PipelineTextureOutputFormat.Exr;
                    return true;
                case "hdr":
                    format = PipelineTextureOutputFormat.Hdr;
                    return true;
                default:
                    format = PipelineTextureOutputFormat.Png;
                    failure = $"Unsupported output_format '{value}'. Supported values: png, exr, hdr.";
                    return false;
            }
        }

        private static string GetPipelineCaptureExtension(PipelineTextureOutputFormat format)
            => format switch
            {
                PipelineTextureOutputFormat.Exr => "exr",
                PipelineTextureOutputFormat.Hdr => "hdr",
                _ => "png",
            };

        private static string SanitizeFileName(string value)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            string sanitized = new(value.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? "texture" : sanitized;
        }

        private sealed record PipelineTextureCaptureResult(
            string Path,
            int Width,
            int Height,
            RgbaFloatStats Stats,
            PipelineCaptureOrientation Orientation);

        private readonly record struct PipelineCaptureOrientation(
            RuntimeGraphicsApiKind Backend,
            ERenderClipSpaceYDirection ClipSpaceYDirection,
            ERenderClipSpaceYDirection FramebufferTextureYDirection,
            bool FlipVertically,
            bool AutoFlipVertically,
            bool OverrideRequested);

        private enum PipelineTextureOutputFormat
        {
            Png,
            Exr,
            Hdr,
        }

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
