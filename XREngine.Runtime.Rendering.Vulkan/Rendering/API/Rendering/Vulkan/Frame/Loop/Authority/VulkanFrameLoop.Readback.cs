using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using ImageMagick;
using Silk.NET.Vulkan;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanFrameLoop
    {
        internal bool TryReadDepthPixelDebug(
            XRFrameBuffer? frameBuffer,
            int x,
            int y,
            out object? diagnostic)
        {
            VulkanCommandRuntime.VulkanDepthReadbackDebugInfo info =
                VulkanCommandRuntime.VulkanDepthReadbackDebugInfo.Failed("No framebuffer supplied.", x, y);
            if (frameBuffer is null)
            {
                diagnostic = info;
                return false;
            }

            x = Math.Clamp(x, 0, Math.Max((int)frameBuffer.Width - 1, 0));
            y = Math.Clamp(y, 0, Math.Max((int)frameBuffer.Height - 1, 0));
            if (!TryResolveBlitImage(
                    frameBuffer,
                    OutputRuntime.Desktop.LastPresentedImageIndex,
                    _commandRuntime.ActiveReadBufferMode,
                    wantColor: false,
                    wantDepth: true,
                    wantStencil: false,
                    out BlitImageInfo depthSource,
                    isSource: true))
            {
                diagnostic = VulkanCommandRuntime.VulkanDepthReadbackDebugInfo.Failed(
                    "Could not resolve a depth attachment image for the framebuffer.", x, y);
                return false;
            }

            bool success = _commandRuntime.TryReadDepthPixelDebug(depthSource, x, y, out info);
            diagnostic = info;
            return success;
        }

        // =========== Luminance Readback ===========

        internal void CalcDotLuminanceAsync(XRTexture2D texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true)
        {
            // Compute luminance by reading back the smallest mipmap level (ideally 1x1).
            if (texture is null)
            {
                callback?.Invoke(false, 0f);
                return;
            }

            var vkTex = GenericToAPI<VkTexture2D>(texture);
            if (vkTex is null || !vkTex.IsGenerated)
            {
                callback?.Invoke(false, 0f);
                return;
            }

            if (genMipmapsNow && !vkTex.UsesAllocatorImage)
                texture.GenerateMipmapsGPU();
            else if (genMipmapsNow)
                LogPlannerMipReadbackFallback(texture.Name, isArray: false);

            // Synchronous path: read smallest mip and compute luminance
            if (CalcDotLuminance(texture, luminance, out float dotLuminance, false))
                callback?.Invoke(true, dotLuminance);
            else
                callback?.Invoke(false, 0f);
        }
        internal void CalcDotLuminanceAsync(XRTexture2DArray texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true)
        {
            // Compute luminance by reading back the smallest mipmap level from all layers.
            if (texture is null)
            {
                callback?.Invoke(false, 0f);
                return;
            }

            var vkTex = GenericToAPI<VkTexture2DArray>(texture);
            if (vkTex is null || !vkTex.IsGenerated)
            {
                callback?.Invoke(false, 0f);
                return;
            }

            if (genMipmapsNow && !vkTex.UsesAllocatorImage)
                texture.GenerateMipmapsGPU();
            else if (genMipmapsNow)
                LogPlannerMipReadbackFallback(texture.Name, isArray: true);

            // Synchronous path: read smallest mip and compute luminance
            if (CalcDotLuminance(texture, luminance, out float dotLuminance, false))
                callback?.Invoke(true, dotLuminance);
            else
                callback?.Invoke(false, 0f);
        }
        internal void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
        {
            // Vulkan doesn't have a separate compute path for this; delegate to the standard async implementation
            CalcDotLuminanceFrontAsync(region, withTransparency, luminance, callback);
        }
        internal void CalcDotLuminanceFrontAsync(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
        {
            _ = withTransparency; // Vulkan path reads the opaque presented color source.

            if (!TryReadLastWindowPresentColorRegionRgba8(region, out byte[] rgba, out int width, out int height))
            {
                WarnUnsupportedPostPresentSwapchainReadback(nameof(CalcDotLuminanceFrontAsync));
                callback?.Invoke(false, 0f);
                return;
            }

            int pixelCount = width * height;
            if (pixelCount <= 0)
            {
                callback?.Invoke(false, 0f);
                return;
            }

            float accum = 0f;
            for (int i = 0; i < pixelCount; i++)
            {
                int index = i * 4;
                byte r = rgba[index + 0];
                byte g = rgba[index + 1];
                byte b = rgba[index + 2];
                accum += (r * luminance.X + g * luminance.Y + b * luminance.Z) / 255f;
            }

            callback?.Invoke(true, accum / pixelCount);
        }

        // =========== Depth Readback ===========

        internal float GetDepth(int x, int y)
        {
            XRFrameBuffer? boundReadFrameBuffer = _commandRuntime.ActiveBoundReadFrameBuffer;
            if (boundReadFrameBuffer is not null)
            {
                x = Math.Clamp(x, 0, Math.Max((int)boundReadFrameBuffer.Width - 1, 0));
                y = Math.Clamp(y, 0, Math.Max((int)boundReadFrameBuffer.Height - 1, 0));

                if (TryResolveBlitImage(
                        boundReadFrameBuffer,
                        OutputRuntime.Desktop.LastPresentedImageIndex,
                        _commandRuntime.ActiveReadBufferMode,
                        wantColor: false,
                        wantDepth: true,
                        wantStencil: false,
                        out BlitImageInfo depthSource,
                        isSource: true) &&
                    _commandRuntime.TryReadDepthPixel(depthSource, x, y, out float fboDepth))
                {
                    return fboDepth;
                }

                Debug.VulkanWarningEvery(
                    "Vulkan.Readback.DepthBoundFboFailed",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] GetDepth fallback to swapchain: unable to resolve/read bound read framebuffer '{0}'.",
                    boundReadFrameBuffer.Name ?? "<unnamed>");
            }

            return TryReadSwapchainDepthPixel(x, y, out float depth)
                ? depth
                : 1.0f;
        }

        private bool TryReadSwapchainDepthPixel(int x, int y, out float depth)
        {
            depth = 1.0f;

            VulkanSwapchainDepthResources? resources = OutputRuntime.DesktopDepthResources;
            if (resources is null)
                return false;

            x = Math.Clamp(x, 0, Math.Max((int)resources.Extent.Width - 1, 0));
            y = Math.Clamp(y, 0, Math.Max((int)resources.Extent.Height - 1, 0));
            BlitImageInfo source = CreateSwapchainDepthReadbackSource(resources);
            return _commandRuntime.TryReadDepthPixel(source, x, y, out depth);
        }

        internal void GetDepthAsync(XRFrameBuffer fbo, int x, int y, Action<float> depthCallback)
        {
            // Prefer reading from the provided FBO depth attachment when available.
            if (fbo is not null)
            {
                x = Math.Clamp(x, 0, Math.Max((int)fbo.Width - 1, 0));
                y = Math.Clamp(y, 0, Math.Max((int)fbo.Height - 1, 0));

                if (TryResolveBlitImage(
                        fbo,
                        OutputRuntime.Desktop.LastPresentedImageIndex,
                        _commandRuntime.ActiveReadBufferMode,
                        wantColor: false,
                        wantDepth: true,
                        wantStencil: false,
                        out BlitImageInfo depthSource,
                        isSource: true) &&
                    _commandRuntime.TryReadDepthPixel(depthSource, x, y, out float depth))
                {
                    depthCallback?.Invoke(depth);
                    return;
                }
            }

            VulkanSwapchainDepthResources? resources = OutputRuntime.DesktopDepthResources;
            if (resources is null)
            {
                depthCallback?.Invoke(1.0f);
                return;
            }

            x = Math.Clamp(x, 0, Math.Max((int)resources.Extent.Width - 1, 0));
            y = Math.Clamp(y, 0, Math.Max((int)resources.Extent.Height - 1, 0));
            BlitImageInfo source = CreateSwapchainDepthReadbackSource(resources);
            _commandRuntime.BeginDepthReadbackAsync(
                source,
                x,
                y,
                depthCallback,
                ReadbackOutputResources,
                CurrentFrameSlot);
        }

        private static BlitImageInfo CreateSwapchainDepthReadbackSource(
            VulkanSwapchainDepthResources resources)
            => new(
                resources.Image,
                resources.Format,
                ImageAspectFlags.DepthBit,
                0,
                1,
                0,
                resources.Extent,
                ImageLayout.DepthStencilAttachmentOptimal,
                PipelineStageFlags.EarlyFragmentTestsBit |
                PipelineStageFlags.LateFragmentTestsBit,
                AccessFlags.DepthStencilAttachmentWriteBit |
                AccessFlags.DepthStencilAttachmentReadBit);

        // =========== Pixel Readback ===========

        private ImageLayout GetSwapchainReadbackLayout(uint readIndex)
        {
            bool wasPresented = OutputRuntime.Desktop.ImageEverPresented is not null
                && readIndex < OutputRuntime.Desktop.ImageEverPresented.Length
                && OutputRuntime.Desktop.ImageEverPresented[readIndex];

            return wasPresented
                ? ImageLayout.PresentSrcKhr
                : ImageLayout.Undefined;
        }

        internal void GetPixelAsync(int x, int y, bool withTransparency, Action<ColorF4> colorCallback)
        {
            XRFrameBuffer? boundReadFrameBuffer = _commandRuntime.ActiveBoundReadFrameBuffer;
            if (boundReadFrameBuffer is not null)
            {
                x = Math.Clamp(x, 0, Math.Max((int)boundReadFrameBuffer.Width - 1, 0));
                y = Math.Clamp(y, 0, Math.Max((int)boundReadFrameBuffer.Height - 1, 0));

                if (TryResolveBlitImage(
                        boundReadFrameBuffer,
                        OutputRuntime.Desktop.LastPresentedImageIndex,
                        _commandRuntime.ActiveReadBufferMode,
                        wantColor: true,
                        wantDepth: false,
                        wantStencil: false,
                        out BlitImageInfo colorSource,
                        isSource: true) &&
                    _commandRuntime.TryReadColorPixel(colorSource, x, y, out ColorF4 color))
                {
                    colorCallback?.Invoke(color);
                    return;
                }

                Debug.VulkanWarningEvery(
                    "Vulkan.Readback.PixelBoundFboFailed",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] GetPixelAsync fallback to swapchain: unable to resolve/read bound read framebuffer '{0}'.",
                    boundReadFrameBuffer.Name ?? "<unnamed>");
            }

            if (!TryReadLastWindowPresentColorPixel(x, y, out ColorF4 fallbackColor))
            {
                WarnUnsupportedPostPresentSwapchainReadback(nameof(GetPixelAsync));
                colorCallback?.Invoke(ColorF4.Transparent);
                return;
            }

            if (!withTransparency)
                fallbackColor.A = 1.0f;

            colorCallback?.Invoke(fallbackColor);
        }

        // =========== Screenshot Readback ===========

        // Vulkan readback copies image rows into the order expected by Magick/PNG export.
        // Do not reuse FramebufferTextureYDirection here; that is shader sampling policy.
        internal bool ScreenshotRequiresVerticalFlip => false;

        internal void GetScreenshotAsync(BoundingRectangle region, bool withTransparency, Action<MagickImage, int> imageCallback)
        {
            if (TryQueueScreenshotReadback(
                    region,
                    withTransparency,
                    result => imageCallback?.Invoke(result.Image!, result.PixelCount),
                    out string? failure))
            {
                return;
            }

            Debug.VulkanWarning("[Vulkan] GetScreenshotAsync could not queue a nonblocking readback: {0}", failure ?? "unknown failure");
            imageCallback?.Invoke(null!, 0);
        }

        private bool TryReadLastWindowPresentColorRegionRgba8(BoundingRectangle region, out byte[] rgbaPixels, out int width, out int height)
        {
            rgbaPixels = [];
            width = 0;
            height = 0;
            using IDisposable? plannerScope = _lastWindowPresentFrameOpContext is { } context
                ? EnterFrameOpResourcePlannerReadbackScope(in context)
                : null;

            if (_lastWindowPresentFrameBuffer is not null)
            {
                VulkanCommandRuntime.ClampReadbackRegion(region, _lastWindowPresentFrameBuffer.Width, _lastWindowPresentFrameBuffer.Height, out int fboX, out int fboY, out int fboW, out int fboH);
                if (TryResolveBlitImage(
                        _lastWindowPresentFrameBuffer,
                        OutputRuntime.Desktop.LastPresentedImageIndex,
                        EReadBufferMode.ColorAttachment0,
                        wantColor: true,
                        wantDepth: false,
                        wantStencil: false,
                        out BlitImageInfo colorSource,
                        isSource: true) &&
                    _commandRuntime.TryReadColorRegionRgba8(colorSource, fboX, fboY, fboW, fboH, out rgbaPixels))
                {
                    width = fboW;
                    height = fboH;
                    return true;
                }
            }

            if (_lastWindowPresentColorTexture is not IFrameBufferAttachement textureAttachment)
                return false;

            VulkanCommandRuntime.ClampReadbackRegion(region, textureAttachment.Width, textureAttachment.Height, out int texX, out int texY, out int texW, out int texH);
            if (!TryResolveTextureBlitImage(
                    _lastWindowPresentColorTexture,
                    mipLevel: 0,
                    layerIndex: 0,
                    ImageAspectFlags.ColorBit,
                    ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags.FragmentShaderBit,
                    AccessFlags.ShaderReadBit,
                    out BlitImageInfo textureSource) ||
                !_commandRuntime.TryReadColorRegionRgba8(textureSource, texX, texY, texW, texH, out rgbaPixels))
            {
                return false;
            }

            width = texW;
            height = texH;
            return true;
        }

        private bool TryReadLastWindowPresentColorPixel(int x, int y, out ColorF4 color)
        {
            color = ColorF4.Transparent;
            using IDisposable? plannerScope = _lastWindowPresentFrameOpContext is { } context
                ? EnterFrameOpResourcePlannerReadbackScope(in context)
                : null;

            if (_lastWindowPresentFrameBuffer is not null)
            {
                x = Math.Clamp(x, 0, Math.Max((int)_lastWindowPresentFrameBuffer.Width - 1, 0));
                y = Math.Clamp(y, 0, Math.Max((int)_lastWindowPresentFrameBuffer.Height - 1, 0));
                if (TryResolveBlitImage(
                        _lastWindowPresentFrameBuffer,
                        OutputRuntime.Desktop.LastPresentedImageIndex,
                        EReadBufferMode.ColorAttachment0,
                        wantColor: true,
                        wantDepth: false,
                        wantStencil: false,
                        out BlitImageInfo colorSource,
                        isSource: true) &&
                    _commandRuntime.TryReadColorPixel(colorSource, x, y, out color))
                {
                    return true;
                }
            }

            if (_lastWindowPresentColorTexture is not IFrameBufferAttachement textureAttachment)
                return false;

            x = Math.Clamp(x, 0, Math.Max((int)textureAttachment.Width - 1, 0));
            y = Math.Clamp(y, 0, Math.Max((int)textureAttachment.Height - 1, 0));
            return TryResolveTextureBlitImage(
                    _lastWindowPresentColorTexture,
                    mipLevel: 0,
                    layerIndex: 0,
                    ImageAspectFlags.ColorBit,
                    ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags.FragmentShaderBit,
                    AccessFlags.ShaderReadBit,
                    out BlitImageInfo textureSource) &&
                _commandRuntime.TryReadColorPixel(textureSource, x, y, out color);
        }

        private static void WarnUnsupportedPostPresentSwapchainReadback(string operation)
            => Debug.VulkanWarningEvery(
                $"Vulkan.Readback.{operation}.PostPresentSwapchainUnsupported",
                TimeSpan.FromSeconds(2),
                "[Vulkan] {0} skipped post-present swapchain readback: presentable images cannot be used after vkQueuePresentKHR without a fresh acquire. Capture from a tracked render target instead.",
                operation);

        private static void ForceOpaqueAlpha(byte[] rgbaPixels)
        {
            for (int i = 3; i < rgbaPixels.Length; i += 4)
                rgbaPixels[i] = 255;
        }

        // =========== Dot Luminance Computation ===========

        internal bool CalcDotLuminance(XRTexture2DArray texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow)
        {
            dotLuminance = 0f;

            var vkTex = GenericToAPI<VkTexture2DArray>(texture);
            if (vkTex is null || !vkTex.IsGenerated)
                return false;

            if (genMipmapsNow && !vkTex.UsesAllocatorImage)
                texture.GenerateMipmapsGPU();
            else if (genMipmapsNow)
                LogPlannerMipReadbackFallback(texture.Name, isArray: true);

            int layerCount = (int)texture.Depth;
            if (layerCount <= 0)
                return false;

            int mipLevel = vkTex.UsesAllocatorImage
                ? 0
                : XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height, texture.SmallestAllowedMipmapLevel);
            Vector3 accumulatedRgb = Vector3.Zero;
            for (int layerIndex = 0; layerIndex < layerCount; layerIndex++)
            {
                if (!TryResolveTextureBlitImage(
                        texture,
                        mipLevel,
                        layerIndex,
                        ImageAspectFlags.ColorBit,
                        ImageLayout.ShaderReadOnlyOptimal,
                        PipelineStageFlags.FragmentShaderBit |
                        PipelineStageFlags.ComputeShaderBit,
                        AccessFlags.ShaderReadBit |
                        AccessFlags.MemoryReadBit,
                        out BlitImageInfo source) ||
                    !_commandRuntime.TryReadColorRegionRgbaFloat(source, 0, 0, 1, 1, out float[] rgba) ||
                    rgba.Length < 4 ||
                    float.IsNaN(rgba[0]) ||
                    float.IsNaN(rgba[1]) ||
                    float.IsNaN(rgba[2]))
                {
                    return false;
                }

                accumulatedRgb += new Vector3(rgba[0], rgba[1], rgba[2]);
            }

            dotLuminance = Vector3.Dot(accumulatedRgb / layerCount, luminance);
            return true;
        }

        internal bool CalcDotLuminance(XRTexture2D texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow)
        {
            dotLuminance = 0f;

            var vkTex = GenericToAPI<VkTexture2D>(texture);
            if (vkTex is null || !vkTex.IsGenerated)
                return false;

            if (genMipmapsNow && !vkTex.UsesAllocatorImage)
                texture.GenerateMipmapsGPU();
            else if (genMipmapsNow)
                LogPlannerMipReadbackFallback(texture.Name, isArray: false);

            int mipLevel = vkTex.UsesAllocatorImage
                ? 0
                : XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height, texture.SmallestAllowedMipmapLevel);
            if (!TryResolveTextureBlitImage(
                    texture,
                    mipLevel,
                    0,
                    ImageAspectFlags.ColorBit,
                    ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags.FragmentShaderBit |
                    PipelineStageFlags.ComputeShaderBit,
                    AccessFlags.ShaderReadBit |
                    AccessFlags.MemoryReadBit,
                    out BlitImageInfo source) ||
                !_commandRuntime.TryReadColorRegionRgbaFloat(source, 0, 0, 1, 1, out float[] rgba) ||
                rgba.Length < 4 ||
                float.IsNaN(rgba[0]) ||
                float.IsNaN(rgba[1]) ||
                float.IsNaN(rgba[2]))
            {
                return false;
            }

            dotLuminance = Vector3.Dot(
                new Vector3(rgba[0], rgba[1], rgba[2]),
                luminance);
            return true;
        }

        private static void LogPlannerMipReadbackFallback(string? textureName, bool isArray)
            => Debug.VulkanWarningEvery(
                isArray
                    ? "Vulkan.LuminanceReadback.PlannerMip0Fallback2DArray"
                    : "Vulkan.LuminanceReadback.PlannerMip0Fallback2D",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Luminance readback is sampling mip 0 for planner-backed {0}source texture '{1}' because render-graph mip generation is not available yet.",
                isArray ? "array " : string.Empty,
                textureName ?? "<unnamed>");

        // =========== Texture Mip Readback ===========

        internal bool TryReadTextureMipRgbaFloat(
            XRTexture texture,
            int mipLevel,
            int layerIndex,
            out float[]? rgbaFloats,
            out int width,
            out int height,
            out string failure)
            => TryReadTextureMipRgbaFloat(
                texture,
                mipLevel,
                layerIndex,
                ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.MemoryReadBit,
                useExpectedLayoutWhenUntracked: false,
                out rgbaFloats,
                out width,
                out height,
                out failure);

        private bool TryReadTextureMipRgbaFloat(
            XRTexture texture,
            int mipLevel,
            int layerIndex,
            ImageLayout expectedSourceLayout,
            PipelineStageFlags expectedSourceStage,
            AccessFlags expectedSourceAccess,
            bool useExpectedLayoutWhenUntracked,
            out float[]? rgbaFloats,
            out int width,
            out int height,
            out string failure)
        {
            rgbaFloats = null;
            width = 0;
            height = 0;
            failure = string.Empty;

            if (!RuntimeEngine.IsRenderThread)
            {
                failure = "Readback unavailable off render thread";
                return false;
            }

            if (!TryResolveTextureMipReadbackSize(texture, out int baseWidth, out int baseHeight, out int layerCount, out bool multisample))
            {
                failure = "Unsupported texture type";
                return false;
            }

            if (multisample)
            {
                failure = "Multisample textures do not support mip readback";
                return false;
            }

            width = Math.Max(1, baseWidth >> Math.Max(0, mipLevel));
            height = Math.Max(1, baseHeight >> Math.Max(0, mipLevel));

            int clampedLayer = Math.Clamp(layerIndex, 0, Math.Max(0, layerCount - 1));

            if (!TryResolveTextureBlitImage(
                    texture,
                    Math.Max(0, mipLevel),
                    clampedLayer,
                    ImageAspectFlags.ColorBit,
                    expectedSourceLayout,
                    expectedSourceStage,
                    expectedSourceAccess,
                    out BlitImageInfo source))
            {
                failure = "Texture not uploaded";
                return false;
            }

            if (useExpectedLayoutWhenUntracked && source.PreferredLayout == ImageLayout.Undefined)
            {
                source = source.WithResolvedState(
                    source.Image,
                    expectedSourceLayout,
                    source.Extent);
            }

            // Explicit readbacks are diagnostic, not per-frame submission work.
            // Retain both identities so a capture can reveal a stale wrapper or
            // planner binding instead of presenting a black image as GPU proof.
            IVkImageDescriptorSource? wrapper =
                _resourceRuntime.WrapperLookup.GetOrCreate(texture, false) as IVkImageDescriptorSource;
            VulkanTextureReadbackDiagnostics.Publish(
                $"texture={texture.Name} sourceImage={source.Image.Handle} wrapperImage={wrapper?.DescriptorImage.Handle ?? 0} " +
                $"layout={source.PreferredLayout} format={source.Format} mip={source.MipLevel} layer={source.BaseArrayLayer}");

            if (IsDepthOrStencilFormat(source.Format))
            {
                if (!TryResolveTextureBlitImage(
                        texture,
                        Math.Max(0, mipLevel),
                        clampedLayer,
                        ImageAspectFlags.DepthBit,
                        ImageLayout.DepthStencilReadOnlyOptimal,
                        PipelineStageFlags.EarlyFragmentTestsBit |
                        PipelineStageFlags.LateFragmentTestsBit |
                        PipelineStageFlags.FragmentShaderBit |
                        PipelineStageFlags.ComputeShaderBit,
                        AccessFlags.DepthStencilAttachmentReadBit |
                        AccessFlags.ShaderReadBit |
                        AccessFlags.MemoryReadBit,
                        out source))
                {
                    failure = "Depth texture not uploaded";
                    return false;
                }

                if (!_commandRuntime.TryReadDepthRegionRgbaFloat(source, 0, 0, width, height, out rgbaFloats))
                {
                    failure = "Depth texture readback failed";
                    return false;
                }

                return true;
            }

            if (!_commandRuntime.TryReadColorRegionRgbaFloat(source, 0, 0, width, height, out rgbaFloats))
            {
                failure = "Texture readback failed";
                return false;
            }

            return true;
        }

        private static bool TryResolveTextureMipReadbackSize(
            XRTexture texture,
            out int width,
            out int height,
            out int layerCount,
            out bool multisample)
        {
            width = 0;
            height = 0;
            layerCount = 1;
            multisample = false;

            switch (texture)
            {
                case XRTexture2D tex2D:
                    width = checked((int)tex2D.Width);
                    height = checked((int)tex2D.Height);
                    multisample = tex2D.MultiSample;
                    return true;
                case XRTexture2DArray tex2DArray:
                    width = checked((int)tex2DArray.Width);
                    height = checked((int)tex2DArray.Height);
                    layerCount = checked((int)Math.Max(tex2DArray.Depth, 1u));
                    multisample = tex2DArray.MultiSample;
                    return true;
                case XRTexture2DView tex2DView:
                    width = checked((int)tex2DView.Width);
                    height = checked((int)tex2DView.Height);
                    multisample = tex2DView.Multisample;
                    return true;
                case XRTexture2DArrayView tex2DArrayView:
                    width = checked((int)tex2DArrayView.Width);
                    height = checked((int)tex2DArrayView.Height);
                    layerCount = checked((int)Math.Max(tex2DArrayView.NumLayers, 1u));
                    multisample = tex2DArrayView.Multisample;
                    return true;
                default:
                    return false;
            }
        }

        internal bool TryReadTexturePixelRgbaFloat(
            XRTexture texture,
            int mipLevel,
            int layerIndex,
            out Vector4 rgba,
            out string failure)
        {
            rgba = Vector4.Zero;
            if (!TryReadTextureMipRgbaFloat(texture, mipLevel, layerIndex, out float[]? rgbaFloats, out _, out _, out failure)
                || rgbaFloats is null
                || rgbaFloats.Length < 4)
            {
                failure = string.IsNullOrWhiteSpace(failure) ? "Texture readback failed" : failure;
                return false;
            }

            rgba = new Vector4(rgbaFloats[0], rgbaFloats[1], rgbaFloats[2], rgbaFloats[3]);
            return true;
        }

        internal bool TryCaptureTextureLayerToPng(
            XRTexture texture,
            int mipLevel,
            int layerIndex,
            ImageLayout expectedSourceLayout,
            PipelineStageFlags expectedSourceStage,
            AccessFlags expectedSourceAccess,
            string outputPath,
            out int width,
            out int height,
            out RenderedOutputCaptureMetrics? metrics,
            out string failure)
        {
            metrics = null;
            if (!TryReadTextureMipRgbaFloat(
                    texture,
                    mipLevel,
                    layerIndex,
                    expectedSourceLayout,
                    expectedSourceStage,
                    expectedSourceAccess,
                    useExpectedLayoutWhenUntracked: true,
                    out float[]? rgbaFloats,
                    out width,
                    out height,
                    out failure) ||
                rgbaFloats is null)
            {
                return false;
            }

            byte[] rgba8 = new byte[rgbaFloats.Length];
            for (int i = 0; i < rgbaFloats.Length; i++)
            {
                float value = Math.Clamp(rgbaFloats[i], 0.0f, 1.0f);
                rgba8[i] = (byte)MathF.Round(value * byte.MaxValue);
            }

            string fullPath = Path.GetFullPath(outputPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            try
            {
                using var image = new MagickImage(rgba8, new MagickReadSettings
                {
                    Width = checked((uint)width),
                    Height = checked((uint)height),
                    Format = MagickFormat.Rgba,
                    Depth = 8,
                });
                image.Write(fullPath);
                metrics = StereoRenderedOutputMetrics.MeasureCapture(
                    rgbaFloats,
                    width,
                    height);
                File.WriteAllText(
                    fullPath + ".metrics.json",
                    System.Text.Json.JsonSerializer.Serialize(
                        metrics,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                failure = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                failure = $"PNG write failed: {ex.Message}";
                return false;
            }
        }
    }
}

