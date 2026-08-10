using System;
using System.Diagnostics;
using System.Numerics;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    internal unsafe partial class VulkanCommandRuntime
    {
        // =========== Readback Region Helpers ===========

        internal static void ClampReadbackRegion(BoundingRectangle region, uint sourceWidth, uint sourceHeight, out int x, out int y, out int width, out int height)
        {
            int maxX = Math.Max((int)sourceWidth - 1, 0);
            int maxY = Math.Max((int)sourceHeight - 1, 0);
            x = Math.Clamp(region.X, 0, maxX);
            y = Math.Clamp(region.Y, 0, maxY);

            int requestedWidth = region.Width > 0 ? region.Width : (int)sourceWidth;
            int requestedHeight = region.Height > 0 ? region.Height : (int)sourceHeight;
            int availableWidth = Math.Max((int)sourceWidth - x, 1);
            int availableHeight = Math.Max((int)sourceHeight - y, 1);

            width = Math.Clamp(requestedWidth, 1, availableWidth);
            height = Math.Clamp(requestedHeight, 1, availableHeight);
        }

        private static bool IsPixelInsideExtent(int x, int y, Extent2D extent)
            => x >= 0 &&
               y >= 0 &&
               extent.Width > 0 &&
               extent.Height > 0 &&
               (uint)x < extent.Width &&
               (uint)y < extent.Height;

        internal static bool IsRegionInsideExtent(int x, int y, int width, int height, Extent2D extent)
        {
            if (x < 0 || y < 0 || width <= 0 || height <= 0 || extent.Width == 0 || extent.Height == 0)
                return false;

            long right = (long)x + width;
            long bottom = (long)y + height;
            return right <= extent.Width && bottom <= extent.Height;
        }

        // =========== Color Pixel Reading ===========

        internal bool TryReadColorPixel(in BlitImageInfo source, int x, int y, out ColorF4 color)
        {
            color = ColorF4.Transparent;

            if (!TryReadColorRegionRgba8(source, x, y, 1, 1, out byte[] rgba) || rgba.Length < 4)
                return false;

            color = new ColorF4(
                rgba[0] / 255f,
                rgba[1] / 255f,
                rgba[2] / 255f,
                rgba[3] / 255f);
            return true;
        }

        internal bool TryReadColorRegionRgba8(in BlitImageInfo source, int x, int y, int width, int height, out byte[] rgbaPixels)
        {
            rgbaPixels = [];

            if (!source.IsValid || (source.AspectMask & ImageAspectFlags.ColorBit) == 0)
                return false;
            if (!TryResolveLiveBlitImage(source, out BlitImageInfo liveSource))
                return false;
            if (!IsRegionInsideExtent(x, y, width, height, liveSource.Extent))
                return false;

            uint sourcePixelSize = GetColorFormatPixelSize(liveSource.Format);
            if (sourcePixelSize == 0)
                return false;

            ulong rawByteCount = (ulong)(width * height) * sourcePixelSize;
            var (stagingBuffer, stagingMemory) = CreateReadbackBuffer(rawByteCount);
            ImageLayout postTransferLayout = VulkanReadbackLayoutPolicy.ResolvePostTransfer(liveSource);

            try
            {
                using var scope = _commandRuntime.NewCommandScope();

                ImageLayout preTransferLayout = liveSource.PreferredLayout;

                TransitionPreparedImageForBlit(
                    scope.CommandBuffer,
                    liveSource,
                    preTransferLayout,
                    ImageLayout.TransferSrcOptimal,
                    liveSource.AccessMask,
                    AccessFlags.TransferReadBit,
                    liveSource.StageMask,
                    PipelineStageFlags.TransferBit);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = liveSource.MipLevel,
                        BaseArrayLayer = liveSource.BaseArrayLayer,
                        LayerCount = liveSource.LayerCount,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 }
                };

                _commandRuntime.CopyImageToBufferTracked(
                    scope.CommandBuffer,
                    liveSource.Image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                TransitionPreparedImageForBlit(
                    scope.CommandBuffer,
                    liveSource,
                    ImageLayout.TransferSrcOptimal,
                    postTransferLayout,
                    AccessFlags.TransferReadBit,
                    liveSource.AccessMask,
                    PipelineStageFlags.TransferBit,
                    liveSource.StageMask);
            }
            catch
            {
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            VulkanReadbackLayoutPolicy.PublishRestoredAttachmentLayout(liveSource, postTransferLayout);

            if (!TryMapReadbackMemory(stagingBuffer, stagingMemory, 0, rawByteCount, out void* mappedPtr))
            {
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            try
            {
                rgbaPixels = new byte[width * height * 4];
                return TryConvertColorPixelsToRgba8(mappedPtr, liveSource.Format, width * height, rgbaPixels);
            }
            finally
            {
                UnmapReadbackMemory(stagingBuffer, stagingMemory);
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
            }
        }

        internal bool TryReadColorRegionRgbaFloat(in BlitImageInfo source, int x, int y, int width, int height, out float[] rgbaFloats)
        {
            rgbaFloats = [];

            if (!source.IsValid || (source.AspectMask & ImageAspectFlags.ColorBit) == 0)
                return false;
            if (!TryResolveLiveBlitImage(source, out BlitImageInfo liveSource))
                return false;
            if (!IsRegionInsideExtent(x, y, width, height, liveSource.Extent))
                return false;

            uint sourcePixelSize = GetColorFormatPixelSize(liveSource.Format);
            if (sourcePixelSize == 0)
                return false;

            ulong rawByteCount = (ulong)(width * height) * sourcePixelSize;
            var (stagingBuffer, stagingMemory) = CreateReadbackBuffer(rawByteCount);
            ImageLayout postTransferLayout = VulkanReadbackLayoutPolicy.ResolvePostTransfer(liveSource);

            try
            {
                using var scope = _commandRuntime.NewCommandScope();

                ImageLayout preTransferLayout = liveSource.PreferredLayout;

                TransitionPreparedImageForBlit(
                    scope.CommandBuffer,
                    liveSource,
                    preTransferLayout,
                    ImageLayout.TransferSrcOptimal,
                    liveSource.AccessMask,
                    AccessFlags.TransferReadBit,
                    liveSource.StageMask,
                    PipelineStageFlags.TransferBit);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = liveSource.MipLevel,
                        BaseArrayLayer = liveSource.BaseArrayLayer,
                        LayerCount = liveSource.LayerCount,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 }
                };

                _commandRuntime.CopyImageToBufferTracked(
                    scope.CommandBuffer,
                    liveSource.Image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                TransitionPreparedImageForBlit(
                    scope.CommandBuffer,
                    liveSource,
                    ImageLayout.TransferSrcOptimal,
                    postTransferLayout,
                    AccessFlags.TransferReadBit,
                    liveSource.AccessMask,
                    PipelineStageFlags.TransferBit,
                    liveSource.StageMask);
            }
            catch
            {
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            VulkanReadbackLayoutPolicy.PublishRestoredAttachmentLayout(liveSource, postTransferLayout);

            if (!TryMapReadbackMemory(stagingBuffer, stagingMemory, 0, rawByteCount, out void* mappedPtr))
            {
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            try
            {
                int pixelCount = width * height;
                rgbaFloats = new float[pixelCount * 4];
                return TryConvertColorPixelsToRgbaFloat(mappedPtr, liveSource.Format, pixelCount, rgbaFloats);
            }
            finally
            {
                UnmapReadbackMemory(stagingBuffer, stagingMemory);
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
            }
        }

        internal bool TryReadDepthRegionRgbaFloat(in BlitImageInfo source, int x, int y, int width, int height, out float[] rgbaFloats)
        {
            rgbaFloats = [];

            if (width <= 0 || height <= 0)
                return false;
            if (!source.IsValid || (source.AspectMask & ImageAspectFlags.DepthBit) == 0)
                return false;
            if (!TryResolveLiveBlitImage(source, out BlitImageInfo liveSource))
                return false;
            if (!IsRegionInsideExtent(x, y, width, height, liveSource.Extent))
                return false;

            uint pixelSize = GetDepthFormatPixelSize(liveSource.Format);
            if (pixelSize == 0)
                return false;

            int pixelCount = width * height;
            ulong rawByteCount = (ulong)pixelCount * pixelSize;
            var (stagingBuffer, stagingMemory) = CreateReadbackBuffer(rawByteCount);

            try
            {
                using var scope = _commandRuntime.NewCommandScope();

                ImageLayout preTransferLayout = liveSource.PreferredLayout;
                ImageLayout postTransferLayout = VulkanReadbackLayoutPolicy.ResolvePostTransfer(liveSource);

                TransitionPreparedImageForBlit(
                    scope.CommandBuffer,
                    liveSource,
                    preTransferLayout,
                    ImageLayout.TransferSrcOptimal,
                    liveSource.AccessMask,
                    AccessFlags.TransferReadBit,
                    liveSource.StageMask,
                    PipelineStageFlags.TransferBit);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.DepthBit,
                        MipLevel = liveSource.MipLevel,
                        BaseArrayLayer = liveSource.BaseArrayLayer,
                        LayerCount = liveSource.LayerCount,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 }
                };

                _commandRuntime.CopyImageToBufferTracked(
                    scope.CommandBuffer,
                    liveSource.Image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                TransitionPreparedImageForBlit(
                    scope.CommandBuffer,
                    liveSource,
                    ImageLayout.TransferSrcOptimal,
                    postTransferLayout,
                    AccessFlags.TransferReadBit,
                    liveSource.AccessMask,
                    PipelineStageFlags.TransferBit,
                    liveSource.StageMask);
            }
            catch
            {
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            if (!TryMapReadbackMemory(stagingBuffer, stagingMemory, 0, rawByteCount, out void* mappedPtr))
            {
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            try
            {
                rgbaFloats = new float[pixelCount * 4];
                byte* depthPtr = (byte*)mappedPtr;
                int depthStride = checked((int)pixelSize);
                for (int i = 0; i < pixelCount; i++)
                {
                    float depth = ReadDepthValue(depthPtr + (i * depthStride), liveSource.Format);
                    int dst = i * 4;
                    rgbaFloats[dst + 0] = depth;
                    rgbaFloats[dst + 1] = depth;
                    rgbaFloats[dst + 2] = depth;
                    rgbaFloats[dst + 3] = 1.0f;
                }

                return true;
            }
            finally
            {
                UnmapReadbackMemory(stagingBuffer, stagingMemory);
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
            }
        }

        // =========== Pixel Format Conversion ===========

        internal static bool TryConvertColorPixelsToRgba8(void* srcPtr, Format format, int pixelCount, byte[] dstRgba)
        {
            if (pixelCount <= 0 || dstRgba.Length < pixelCount * 4)
                return false;

            static byte FloatToByte(float v)
            {
                float clamped = Math.Clamp(v, 0.0f, 1.0f);
                return (byte)Math.Clamp((int)MathF.Round(clamped * 255.0f), 0, 255);
            }

            byte* src = (byte*)srcPtr;

            switch (format)
            {
                case Format.R8G8B8A8Unorm:
                case Format.R8G8B8A8Srgb:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        dstRgba[dstIndex + 0] = src[srcIndex + 0];
                        dstRgba[dstIndex + 1] = src[srcIndex + 1];
                        dstRgba[dstIndex + 2] = src[srcIndex + 2];
                        dstRgba[dstIndex + 3] = src[srcIndex + 3];
                    }
                    return true;

                case Format.B8G8R8A8Unorm:
                case Format.B8G8R8A8Srgb:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        dstRgba[dstIndex + 0] = src[srcIndex + 2];
                        dstRgba[dstIndex + 1] = src[srcIndex + 1];
                        dstRgba[dstIndex + 2] = src[srcIndex + 0];
                        dstRgba[dstIndex + 3] = src[srcIndex + 3];
                    }
                    return true;

                case Format.R16G16B16A16Unorm:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 8;
                        int dstIndex = i * 4;
                        ushort* p = (ushort*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = FloatToByte(p[0] / 65535.0f);
                        dstRgba[dstIndex + 1] = FloatToByte(p[1] / 65535.0f);
                        dstRgba[dstIndex + 2] = FloatToByte(p[2] / 65535.0f);
                        dstRgba[dstIndex + 3] = FloatToByte(p[3] / 65535.0f);
                    }
                    return true;

                case Format.R16G16B16A16Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 8;
                        int dstIndex = i * 4;
                        ushort* p = (ushort*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[0]));
                        dstRgba[dstIndex + 1] = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[1]));
                        dstRgba[dstIndex + 2] = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[2]));
                        dstRgba[dstIndex + 3] = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[3]));
                    }
                    return true;

                case Format.R16Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 2;
                        int dstIndex = i * 4;
                        ushort* p = (ushort*)(src + srcIndex);
                        byte value = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[0]));
                        dstRgba[dstIndex + 0] = value;
                        dstRgba[dstIndex + 1] = value;
                        dstRgba[dstIndex + 2] = value;
                        dstRgba[dstIndex + 3] = 255;
                    }
                    return true;

                case Format.R16G16Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        ushort* p = (ushort*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[0]));
                        dstRgba[dstIndex + 1] = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[1]));
                        dstRgba[dstIndex + 2] = 0;
                        dstRgba[dstIndex + 3] = 255;
                    }
                    return true;

                case Format.R32G32B32A32Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 16;
                        int dstIndex = i * 4;
                        float* p = (float*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = FloatToByte(p[0]);
                        dstRgba[dstIndex + 1] = FloatToByte(p[1]);
                        dstRgba[dstIndex + 2] = FloatToByte(p[2]);
                        dstRgba[dstIndex + 3] = FloatToByte(p[3]);
                    }
                    return true;

                case Format.R32Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        float value = *(float*)(src + srcIndex);
                        byte encoded = FloatToByte(value);
                        dstRgba[dstIndex + 0] = encoded;
                        dstRgba[dstIndex + 1] = encoded;
                        dstRgba[dstIndex + 2] = encoded;
                        dstRgba[dstIndex + 3] = 255;
                    }
                    return true;

                case Format.B10G11R11UfloatPack32:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        uint packed = *(uint*)(src + srcIndex);
                        DecodeB10G11R11Ufloat(packed, out float r, out float g, out float b);
                        dstRgba[dstIndex + 0] = FloatToByte(r);
                        dstRgba[dstIndex + 1] = FloatToByte(g);
                        dstRgba[dstIndex + 2] = FloatToByte(b);
                        dstRgba[dstIndex + 3] = 255;
                    }
                    return true;
            }

            return false;
        }

        private static bool TryConvertColorPixelsToRgbaFloat(void* srcPtr, Format format, int pixelCount, float[] dstRgba)
        {
            if (pixelCount <= 0 || dstRgba.Length < pixelCount * 4)
                return false;

            byte* src = (byte*)srcPtr;

            switch (format)
            {
                case Format.R8G8B8A8Unorm:
                case Format.R8G8B8A8Srgb:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        dstRgba[dstIndex + 0] = src[srcIndex + 0] / 255.0f;
                        dstRgba[dstIndex + 1] = src[srcIndex + 1] / 255.0f;
                        dstRgba[dstIndex + 2] = src[srcIndex + 2] / 255.0f;
                        dstRgba[dstIndex + 3] = src[srcIndex + 3] / 255.0f;
                    }
                    return true;

                case Format.B8G8R8A8Unorm:
                case Format.B8G8R8A8Srgb:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        dstRgba[dstIndex + 0] = src[srcIndex + 2] / 255.0f;
                        dstRgba[dstIndex + 1] = src[srcIndex + 1] / 255.0f;
                        dstRgba[dstIndex + 2] = src[srcIndex + 0] / 255.0f;
                        dstRgba[dstIndex + 3] = src[srcIndex + 3] / 255.0f;
                    }
                    return true;

                case Format.R16G16B16A16Unorm:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 8;
                        int dstIndex = i * 4;
                        ushort* p = (ushort*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = p[0] / 65535.0f;
                        dstRgba[dstIndex + 1] = p[1] / 65535.0f;
                        dstRgba[dstIndex + 2] = p[2] / 65535.0f;
                        dstRgba[dstIndex + 3] = p[3] / 65535.0f;
                    }
                    return true;

                case Format.R16G16B16A16Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 8;
                        int dstIndex = i * 4;
                        ushort* p = (ushort*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = (float)BitConverter.UInt16BitsToHalf(p[0]);
                        dstRgba[dstIndex + 1] = (float)BitConverter.UInt16BitsToHalf(p[1]);
                        dstRgba[dstIndex + 2] = (float)BitConverter.UInt16BitsToHalf(p[2]);
                        dstRgba[dstIndex + 3] = (float)BitConverter.UInt16BitsToHalf(p[3]);
                    }
                    return true;

                case Format.R16Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 2;
                        int dstIndex = i * 4;
                        ushort* p = (ushort*)(src + srcIndex);
                        float value = (float)BitConverter.UInt16BitsToHalf(p[0]);
                        dstRgba[dstIndex + 0] = value;
                        dstRgba[dstIndex + 1] = value;
                        dstRgba[dstIndex + 2] = value;
                        dstRgba[dstIndex + 3] = 1.0f;
                    }
                    return true;

                case Format.R16G16Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        ushort* p = (ushort*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = (float)BitConverter.UInt16BitsToHalf(p[0]);
                        dstRgba[dstIndex + 1] = (float)BitConverter.UInt16BitsToHalf(p[1]);
                        dstRgba[dstIndex + 2] = 0.0f;
                        dstRgba[dstIndex + 3] = 1.0f;
                    }
                    return true;

                case Format.R32G32B32A32Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 16;
                        int dstIndex = i * 4;
                        float* p = (float*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = p[0];
                        dstRgba[dstIndex + 1] = p[1];
                        dstRgba[dstIndex + 2] = p[2];
                        dstRgba[dstIndex + 3] = p[3];
                    }
                    return true;

                case Format.R32Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        float value = *(float*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = value;
                        dstRgba[dstIndex + 1] = value;
                        dstRgba[dstIndex + 2] = value;
                        dstRgba[dstIndex + 3] = 1.0f;
                    }
                    return true;

                case Format.R32Uint:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        float value = *(uint*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = value;
                        dstRgba[dstIndex + 1] = value;
                        dstRgba[dstIndex + 2] = value;
                        dstRgba[dstIndex + 3] = 1.0f;
                    }
                    return true;

                case Format.B10G11R11UfloatPack32:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        DecodeB10G11R11Ufloat(*(uint*)(src + srcIndex), out float r, out float g, out float b);
                        dstRgba[dstIndex + 0] = r;
                        dstRgba[dstIndex + 1] = g;
                        dstRgba[dstIndex + 2] = b;
                        dstRgba[dstIndex + 3] = 1.0f;
                    }
                    return true;
            }

            return false;
        }

        private static void DecodeB10G11R11Ufloat(uint packed, out float r, out float g, out float b)
        {
            r = DecodeUnsignedFloat(packed & 0x7FFu, 6);
            g = DecodeUnsignedFloat((packed >> 11) & 0x7FFu, 6);
            b = DecodeUnsignedFloat((packed >> 22) & 0x3FFu, 5);
        }

        private static float DecodeUnsignedFloat(uint bits, int mantissaBits)
        {
            const int exponentBias = 15;
            const int maxExponent = 31;

            uint mantissaMask = (1u << mantissaBits) - 1u;
            uint mantissa = bits & mantissaMask;
            uint exponent = bits >> mantissaBits;
            if (exponent == 0u)
                return mantissa == 0u
                    ? 0.0f
                    : MathF.Pow(2.0f, 1 - exponentBias - mantissaBits) * mantissa;

            if (exponent == maxExponent)
                return float.PositiveInfinity;

            float normalizedMantissa = 1.0f + mantissa / (float)(1u << mantissaBits);
            return normalizedMantissa * MathF.Pow(2.0f, (int)exponent - exponentBias);
        }

        internal static uint GetColorFormatPixelSize(Format format)
            => format switch
            {
                Format.R8G8B8A8Unorm => 4,
                Format.R8G8B8A8Srgb => 4,
                Format.B8G8R8A8Unorm => 4,
                Format.B8G8R8A8Srgb => 4,
                Format.R16Sfloat => 2,
                Format.R16G16Sfloat => 4,
                Format.R16G16B16A16Unorm => 8,
                Format.R16G16B16A16Sfloat => 8,
                Format.R32Sfloat => 4,
                Format.R32Uint => 4,
                Format.R32G32B32A32Sfloat => 16,
                Format.B10G11R11UfloatPack32 => 4,
                _ => 0,
            };

        // =========== Depth Pixel Reading ===========

        internal bool TryReadDepthPixel(in BlitImageInfo source, int x, int y, out float depth)
        {
            depth = 1.0f;

            if (!source.IsValid || !IsDepthOrStencilAspect(source.AspectMask))
                return false;
            if (!TryResolveLiveBlitImage(source, out BlitImageInfo liveSource))
                return false;
            if (!IsPixelInsideExtent(x, y, liveSource.Extent))
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.Readback.DepthPixelOutOfBounds",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Depth pixel readback skipped: coordinate {0},{1} is outside live image extent {2}x{3}.",
                    x,
                    y,
                    liveSource.Extent.Width,
                    liveSource.Extent.Height);
                return false;
            }

            uint pixelSize = GetDepthFormatPixelSize(liveSource.Format);
            if (pixelSize == 0)
                return false;

            ulong bufferSize = pixelSize;
            var (stagingBuffer, stagingMemory) = CreateReadbackBuffer(bufferSize);

            try
            {
                using var scope = _commandRuntime.NewCommandScope();

                ImageLayout preTransferLayout = liveSource.PreferredLayout;
                ImageLayout postTransferLayout = VulkanReadbackLayoutPolicy.ResolvePostTransfer(liveSource);

                TransitionPreparedImageForBlit(
                    scope.CommandBuffer,
                    liveSource,
                    preTransferLayout,
                    ImageLayout.TransferSrcOptimal,
                    liveSource.AccessMask,
                    AccessFlags.TransferReadBit,
                    liveSource.StageMask,
                    PipelineStageFlags.TransferBit);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = (liveSource.AspectMask & ImageAspectFlags.DepthBit) != 0
                            ? ImageAspectFlags.DepthBit
                            : liveSource.AspectMask,
                        MipLevel = liveSource.MipLevel,
                        BaseArrayLayer = liveSource.BaseArrayLayer,
                        LayerCount = liveSource.LayerCount,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = 1, Height = 1, Depth = 1 }
                };

                _commandRuntime.CopyImageToBufferTracked(
                    scope.CommandBuffer,
                    liveSource.Image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                TransitionPreparedImageForBlit(
                    scope.CommandBuffer,
                    liveSource,
                    ImageLayout.TransferSrcOptimal,
                    postTransferLayout,
                    AccessFlags.TransferReadBit,
                    liveSource.AccessMask,
                    PipelineStageFlags.TransferBit,
                    liveSource.StageMask);
            }
            catch
            {
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            if (!TryMapReadbackMemory(stagingBuffer, stagingMemory, 0, bufferSize, out void* mappedPtr))
            {
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            depth = ReadDepthValue(mappedPtr, liveSource.Format);
            UnmapReadbackMemory(stagingBuffer, stagingMemory);
            DestroyReadbackBuffer(stagingBuffer, stagingMemory);
            return true;
        }

        internal bool TryReadDepthPixelDebug(in BlitImageInfo source, int x, int y, out VulkanDepthReadbackDebugInfo info)
        {
            info = VulkanDepthReadbackDebugInfo.Failed("Depth readback was not attempted.", x, y);

            if (!source.IsValid || !IsDepthOrStencilAspect(source.AspectMask))
            {
                info = VulkanDepthReadbackDebugInfo.Failed("Source is not a depth/stencil image.", x, y);
                return false;
            }

            if (!TryResolveLiveBlitImage(source, out BlitImageInfo liveSource))
            {
                info = VulkanDepthReadbackDebugInfo.Failed("Could not resolve the live depth image handle.", x, y);
                return false;
            }

            if (!IsPixelInsideExtent(x, y, liveSource.Extent))
            {
                info = VulkanDepthReadbackDebugInfo.Failed(
                    $"Coordinate is outside live image extent {liveSource.Extent.Width}x{liveSource.Extent.Height}.",
                    x,
                    y);
                return false;
            }

            uint pixelSize = GetDepthFormatPixelSize(liveSource.Format);
            if (pixelSize == 0)
            {
                info = VulkanDepthReadbackDebugInfo.Failed($"Unsupported depth format '{liveSource.Format}'.", x, y);
                return false;
            }

            ulong bufferSize = pixelSize;
            var (stagingBuffer, stagingMemory) = CreateReadbackBuffer(bufferSize);
            ImageLayout preTransferLayout = liveSource.PreferredLayout;
            ImageLayout postTransferLayout = VulkanReadbackLayoutPolicy.ResolvePostTransfer(liveSource);

            try
            {
            using var scope = _commandRuntime.NewCommandScope();

                TransitionPreparedImageForBlit(
                    scope.CommandBuffer,
                    liveSource,
                    preTransferLayout,
                    ImageLayout.TransferSrcOptimal,
                    liveSource.AccessMask,
                    AccessFlags.TransferReadBit,
                    liveSource.StageMask,
                    PipelineStageFlags.TransferBit);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.DepthBit,
                        MipLevel = liveSource.MipLevel,
                        BaseArrayLayer = liveSource.BaseArrayLayer,
                        LayerCount = liveSource.LayerCount,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = 1, Height = 1, Depth = 1 }
                };

                _commandRuntime.CopyImageToBufferTracked(
                    scope.CommandBuffer,
                    liveSource.Image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                TransitionPreparedImageForBlit(
                    scope.CommandBuffer,
                    liveSource,
                    ImageLayout.TransferSrcOptimal,
                    postTransferLayout,
                    AccessFlags.TransferReadBit,
                    liveSource.AccessMask,
                    PipelineStageFlags.TransferBit,
                    liveSource.StageMask);
            }
            catch (Exception ex)
            {
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
                info = VulkanDepthReadbackDebugInfo.Failed($"Depth copy command failed: {ex.Message}", x, y);
                return false;
            }

            if (!TryMapReadbackMemory(stagingBuffer, stagingMemory, 0, bufferSize, out void* mappedPtr))
            {
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
                info = VulkanDepthReadbackDebugInfo.Failed("Could not map depth readback staging memory.", x, y);
                return false;
            }

            byte[] rawBytes = new byte[pixelSize];
            byte* src = (byte*)mappedPtr;
            for (int i = 0; i < rawBytes.Length; i++)
                rawBytes[i] = src[i];

            float decodedDepth = ReadDepthValue(mappedPtr, liveSource.Format);
            UnmapReadbackMemory(stagingBuffer, stagingMemory);
            DestroyReadbackBuffer(stagingBuffer, stagingMemory);

            info = VulkanDepthReadbackDebugInfo.FromRawBytes(
                x,
                y,
                liveSource.Format.ToString(),
                liveSource.AspectMask.ToString(),
                preTransferLayout.ToString(),
                postTransferLayout.ToString(),
                liveSource.StageMask.ToString(),
                liveSource.AccessMask.ToString(),
                rawBytes,
                decodedDepth);
            return true;
        }

        internal bool TryReadStencilPixel(in BlitImageInfo source, int x, int y, out byte stencil)
        {
            stencil = 0;

            if (!source.IsValid || (source.AspectMask & ImageAspectFlags.StencilBit) == 0)
                return false;
            if (!TryResolveLiveBlitImage(source, out BlitImageInfo liveSource))
                return false;
            if (!IsPixelInsideExtent(x, y, liveSource.Extent))
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.Readback.StencilPixelOutOfBounds",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Stencil pixel readback skipped: coordinate {0},{1} is outside live image extent {2}x{3}.",
                    x,
                    y,
                    liveSource.Extent.Width,
                    liveSource.Extent.Height);
                return false;
            }

            uint pixelSize = GetDepthFormatPixelSize(liveSource.Format);
            if (pixelSize == 0)
                return false;

            ulong bufferSize = pixelSize;
            var (stagingBuffer, stagingMemory) = CreateReadbackBuffer(bufferSize);

            try
            {
                using var scope = _commandRuntime.NewCommandScope();

                ImageLayout preTransferLayout = liveSource.PreferredLayout;
                ImageLayout postTransferLayout = VulkanReadbackLayoutPolicy.ResolvePostTransfer(liveSource);

                TransitionPreparedImageForBlit(
                    scope.CommandBuffer,
                    liveSource,
                    preTransferLayout,
                    ImageLayout.TransferSrcOptimal,
                    liveSource.AccessMask,
                    AccessFlags.TransferReadBit,
                    liveSource.StageMask,
                    PipelineStageFlags.TransferBit);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.StencilBit,
                        MipLevel = liveSource.MipLevel,
                        BaseArrayLayer = liveSource.BaseArrayLayer,
                        LayerCount = liveSource.LayerCount,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = 1, Height = 1, Depth = 1 }
                };

                _commandRuntime.CopyImageToBufferTracked(
                    scope.CommandBuffer,
                    liveSource.Image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                TransitionPreparedImageForBlit(
                    scope.CommandBuffer,
                    liveSource,
                    ImageLayout.TransferSrcOptimal,
                    postTransferLayout,
                    AccessFlags.TransferReadBit,
                    liveSource.AccessMask,
                    PipelineStageFlags.TransferBit,
                    liveSource.StageMask);
            }
            catch
            {
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            if (!TryMapReadbackMemory(stagingBuffer, stagingMemory, 0, bufferSize, out void* mappedPtr))
            {
                DestroyReadbackBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            stencil = ReadStencilValue(mappedPtr, liveSource.Format);
            UnmapReadbackMemory(stagingBuffer, stagingMemory);
            DestroyReadbackBuffer(stagingBuffer, stagingMemory);
            return true;
        }

        // =========== Depth Format Helpers ===========

        /// <summary>
        /// Gets the byte size of a single pixel for a given depth format.
        /// </summary>
        internal static uint GetDepthFormatPixelSize(Format format) => format switch
        {
            Format.D16Unorm => 2,
            Format.D32Sfloat => 4,
            Format.D24UnormS8Uint => 4, // 3 bytes depth + 1 byte stencil
            Format.D32SfloatS8Uint => 5, // 4 bytes depth + 1 byte stencil (may be 8 with padding)
            _ => 0, // Unknown format
        };

        /// <summary>
        /// Reads a depth value from a mapped buffer based on the depth format.
        /// </summary>
        internal static float ReadDepthValue(void* ptr, Format format)
        {
            return format switch
            {
                Format.D16Unorm => *(ushort*)ptr / 65535f,
                Format.D32Sfloat => *(float*)ptr,
                Format.D24UnormS8Uint => (*(uint*)ptr & 0x00FFFFFF) / 16777215f,
                Format.D32SfloatS8Uint => *(float*)ptr,
                _ => 1.0f,
            };
        }

        private (Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory) CreateReadbackBuffer(
            ulong byteCount)
            => ResourceRuntime.Buffers.CreateRaw(
                ReadbackContext,
                byteCount,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit,
                owner: "CommandRuntime.PixelReadback");

        private void DestroyReadbackBuffer(
            Silk.NET.Vulkan.Buffer buffer,
            DeviceMemory memory)
            => ResourceRuntime.Buffers.Destroy(
                ReadbackContext,
                buffer,
                memory,
                "CommandRuntime.PixelReadback");

        private bool TryMapReadbackMemory(
            Silk.NET.Vulkan.Buffer buffer,
            DeviceMemory memory,
            ulong offset,
            ulong length,
            out void* mapped)
        {
            mapped = null;
            if (!ResourceRuntime.Buffers.TryMap(
                    ReadbackContext,
                    buffer,
                    memory,
                    offset,
                    length,
                    out void* local))
                return false;

            ulong allocationOffset =
                ResourceRuntime.Buffers.GetAllocationOffset(buffer) + offset;
            ResourceRuntime.Buffers.Invalidate(
                ReadbackContext,
                memory,
                allocationOffset,
                Math.Max(length, 1UL));
            mapped = local;
            RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
            RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(
                checked((long)length));
            return true;
        }

        private void UnmapReadbackMemory(
            Silk.NET.Vulkan.Buffer buffer,
            DeviceMemory memory)
            => ResourceRuntime.Buffers.Unmap(ReadbackContext, buffer, memory);

        private VulkanBackendObjectContext ReadbackContext
            => ResourceRuntime.BackendObjectContext ?? throw new InvalidOperationException(
                "Pixel readback requires an initialized Vulkan backend-object context.");

        private static byte ReadStencilValue(void* ptr, Format format)
        {
            return format switch
            {
                Format.D24UnormS8Uint => (byte)((*(uint*)ptr >> 24) & 0xFF),
                Format.D32SfloatS8Uint => *((byte*)ptr + 4),
                Format.S8Uint => *(byte*)ptr,
                _ => 0,
            };
        }

        public sealed record VulkanDepthReadbackDebugInfo(
            bool Success,
            string? Failure,
            int X,
            int Y,
            string Format,
            string AspectMask,
            string PreferredLayout,
            string PostTransferLayout,
            string StageMask,
            string AccessMask,
            int PixelSize,
            string RawBytesHex,
            uint RawUInt32,
            ushort RawUInt16,
            float DecodedDepth,
            float D24Low24Depth,
            float D24High24Depth,
            float D16Depth,
            float? D32FloatDepth,
            byte LowByte,
            byte HighByte)
        {
            public static VulkanDepthReadbackDebugInfo Failed(string failure, int x, int y)
                => new(
                    false,
                    failure,
                    x,
                    y,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    string.Empty,
                    0u,
                    0,
                    1.0f,
                    1.0f,
                    1.0f,
                    1.0f,
                    null,
                    0,
                    0);

            public static VulkanDepthReadbackDebugInfo FromRawBytes(
                int x,
                int y,
                string format,
                string aspectMask,
                string preferredLayout,
                string postTransferLayout,
                string stageMask,
                string accessMask,
                byte[] rawBytes,
                float decodedDepth)
            {
                uint raw32 = rawBytes.Length >= 4
                    ? rawBytes[0] | ((uint)rawBytes[1] << 8) | ((uint)rawBytes[2] << 16) | ((uint)rawBytes[3] << 24)
                    : 0u;
                ushort raw16 = rawBytes.Length >= 2
                    ? (ushort)(rawBytes[0] | (rawBytes[1] << 8))
                    : (ushort)0;
                float? d32Float = rawBytes.Length >= 4
                    ? BitConverter.ToSingle(rawBytes, 0)
                    : null;
                if (d32Float is { } value && !float.IsFinite(value))
                    d32Float = null;

                return new VulkanDepthReadbackDebugInfo(
                    true,
                    null,
                    x,
                    y,
                    format,
                    aspectMask,
                    preferredLayout,
                    postTransferLayout,
                    stageMask,
                    accessMask,
                    rawBytes.Length,
                    BitConverter.ToString(rawBytes),
                    raw32,
                    raw16,
                    decodedDepth,
                    (raw32 & 0x00FF_FFFFu) / 16777215.0f,
                    ((raw32 >> 8) & 0x00FF_FFFFu) / 16777215.0f,
                    raw16 / 65535.0f,
                    d32Float,
                    rawBytes.Length > 0 ? rawBytes[0] : (byte)0,
                    rawBytes.Length > 0 ? rawBytes[^1] : (byte)0);
            }
        }
    }
}
