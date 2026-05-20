using XREngine.Extensions;
using ImageMagick;
using ImGuiNET;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.OpenGL.Extensions.NV;
using Silk.NET.OpenGL.Extensions.OVR;
using Silk.NET.OpenGLES.Extensions.EXT;
using Silk.NET.OpenGLES.Extensions.NV;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.UI;
using XREngine.Rendering.Shaders.Generator;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;
using XREngine.Components;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    public override void GetScreenshotAsync(BoundingRectangle region, bool withTransparency, Action<MagickImage, int> imageCallback)
    {
        //TODO: render to an FBO with the desired render size and capture from that, instead of using the window size.

        //TODO: multi-glcontext readback.
        //This method is async on the CPU, but still executes synchronously on the GPU.
        //https://developer.download.nvidia.com/GTC/PDF/GTC2012/PresentationPDF/S0356-GTC2012-Texture-Transfers.pdf

        CaptureFBOColorAttachment(region, withTransparency, imageCallback, 0u, ReadBufferMode.Front, -1, true);
    }

    public void CaptureFBOAttachment(
        BoundingRectangle region,
        bool withTransparency,
        Action<MagickImage, int> imageCallback,
        uint readFBOBindingId,
        EFrameBufferAttachment attachment,
        int layer = -1,
        bool async = true)
    {
        switch (attachment)
        {
            case EFrameBufferAttachment.DepthAttachment:
                CaptureFBOAttachment(
                    region,
                    imageCallback,
                    readFBOBindingId,
                    ReadBufferMode.None,
                    EPixelFormat.DepthComponent,
                    EPixelType.Float,
                    layer,
                    async);
                break;
            case EFrameBufferAttachment.StencilAttachment:
                CaptureFBOAttachment(
                    region,
                    imageCallback,
                    readFBOBindingId,
                    ReadBufferMode.None,
                    EPixelFormat.StencilIndex,
                    EPixelType.UnsignedByte,
                    layer,
                    async);

                break;
            case EFrameBufferAttachment.DepthStencilAttachment:
                CaptureFBOAttachment(
                    region,
                    imageCallback,
                    readFBOBindingId,
                    ReadBufferMode.None,
                    EPixelFormat.DepthStencil,
                    EPixelType.UnsignedInt248,
                    layer,
                    async);
                break;
            default:
                CaptureFBOColorAttachment(
                    region,
                    withTransparency,
                    imageCallback,
                    readFBOBindingId,
                    GLObjectBase.ToReadBufferMode(attachment),
                    layer,
                    async);
                break;
        }
    }

    public void CaptureFBOColorAttachment(
        BoundingRectangle region,
        bool withTransparency,
        Action<MagickImage, int> imageCallback,
        uint readFBOBindingId,
        ReadBufferMode readBuffer,
        int layer = -1,
        bool async = true)
    {
        EPixelFormat format = withTransparency ? EPixelFormat.Bgra : EPixelFormat.Bgr;
        EPixelType pixelType = EPixelType.UnsignedByte;
        CaptureFBOAttachment(
            region,
            imageCallback,
            readFBOBindingId,
            readBuffer,
            format,
            pixelType,
            layer,
            async);
    }

    public void CaptureFBOAttachment(
        BoundingRectangle region,
        Action<MagickImage, int> imageCallback,
        uint readFBOBindingId,
        ReadBufferMode readBuffer,
        EPixelFormat format,
        EPixelType pixelType,
        int layer = -1,
        bool async = true)
    {
        //Specify which FBO to read from
        Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFBOBindingId);

        //Specify which attachment buffer to read from
        Api.ReadBuffer(readBuffer);

        CaptureCurrentlyBoundFBOAttachment(region, imageCallback, format, pixelType, async);
    }

    public delegate void DelImageCallback(MagickImage image, int layer, int channelIndex);

    public unsafe void CaptureTexture(
        BoundingRectangle region,
        DelImageCallback imageCallback,
        uint textureBindingId,
        int mipLevel,
        int layer,
        bool async = true)
    {
        uint w = (uint)region.Width;
        uint h = (uint)region.Height;

        Api.GetTextureLevelParameter(textureBindingId, mipLevel, GLEnum.TextureInternalFormat, out int format);
        InternalFormat internalFormat = (InternalFormat)format;
        //int bpp = GetBytesPerPixel(internalFormat);

        Api.GetTextureParameterI(textureBindingId, GLEnum.DepthStencilTextureMode, out int depthStencilMode);
        GLEnum mode = (GLEnum)depthStencilMode;

        EPixelFormat pixelFormat = EPixelFormat.Rgba;
        EPixelType pixelType = EPixelType.UnsignedByte;
        switch (internalFormat)
        {
            case InternalFormat.Depth24Stencil8:
                pixelFormat = EPixelFormat.DepthStencil;
                pixelType = EPixelType.UnsignedInt248;
                break;
            case InternalFormat.Depth32fStencil8:
                pixelFormat = EPixelFormat.DepthStencil;
                pixelType = EPixelType.Float32UnsignedInt248Rev;
                break;
        }

        var data = XRTexture.AllocateBytes(w, h, pixelFormat, pixelType);

        if (async)
        {
            uint size = (uint)data.Length;
            uint pbo = ReadTextureToPBO(textureBindingId, region, layer, 1, pixelFormat, pixelType, size, out IntPtr sync);
            bool FenceCheck()
            {
                if (!GetData(size, data, sync, pbo))
                    return false;
                else
                {
                    Api.DeleteSync(sync);
                    Api.DeleteBuffer(pbo);

                    void MakeImage()
                    {
                        if (IsDepthStencilFormat(internalFormat))
                        {
                            switch (mode)
                            {
                                case GLEnum.StencilIndex:
                                    imageCallback(MakeStencilImage(pixelType, w, h, data), layer, 0);
                                    break;
                                case GLEnum.DepthComponent:
                                    imageCallback(MakeDepthImage(pixelType, w, h, data), layer, 0);
                                    break;
                                default:
                                    imageCallback(OpenGLRenderer.MakeImage(pixelFormat, pixelType, w, h, data), layer, 0);
                                    break;
                            }
                        }
                        else
                            imageCallback(OpenGLRenderer.MakeImage(pixelFormat, pixelType, w, h, data), layer, 0);
                    }
                    Task.Run(MakeImage);

                    return true;
                }
            }
            RuntimeEngine.AddMainThreadCoroutine(FenceCheck);
        }
        else
        {
            fixed (byte* ptr = data)
            {
                Api.GetTextureSubImage(textureBindingId, mipLevel, region.X, region.Y, layer, w, h, 1, GLObjectBase.ToGLEnum(pixelFormat), GLObjectBase.ToGLEnum(pixelType), (uint)data.Length, ptr);
            }
            Task.Run(() => imageCallback(XRTexture.NewImage(w, h, pixelFormat, pixelType, data), layer, 0));
        }
    }

    public unsafe bool TryCaptureTextureBytes(
        uint textureBindingId,
        int mipLevel,
        int layer,
        out byte[] data,
        out EPixelFormat pixelFormat,
        out EPixelType pixelType,
        out uint width,
        out uint height)
    {
        data = [];
        pixelFormat = EPixelFormat.Rgba;
        pixelType = EPixelType.UnsignedByte;
        width = 0;
        height = 0;

        Api.GetTextureLevelParameter(textureBindingId, mipLevel, GLEnum.TextureWidth, out int levelWidth);
        Api.GetTextureLevelParameter(textureBindingId, mipLevel, GLEnum.TextureHeight, out int levelHeight);
        if (levelWidth <= 0 || levelHeight <= 0)
            return false;

        width = (uint)levelWidth;
        height = (uint)levelHeight;

        Api.GetTextureLevelParameter(textureBindingId, mipLevel, GLEnum.TextureInternalFormat, out int format);
        InternalFormat internalFormat = (InternalFormat)format;
        Api.GetTextureParameterI(textureBindingId, GLEnum.DepthStencilTextureMode, out int depthStencilMode);
        GLEnum mode = (GLEnum)depthStencilMode;

        switch (internalFormat)
        {
            case InternalFormat.Depth24Stencil8:
                pixelFormat = EPixelFormat.DepthStencil;
                pixelType = EPixelType.UnsignedInt248;
                break;
            case InternalFormat.Depth32fStencil8:
            case InternalFormat.Depth32fStencil8NV:
                pixelFormat = EPixelFormat.DepthStencil;
                pixelType = EPixelType.Float32UnsignedInt248Rev;
                break;
        }

        if (pixelFormat == EPixelFormat.DepthStencil && mode == GLEnum.StencilIndex)
        {
            pixelFormat = EPixelFormat.StencilIndex;
            pixelType = EPixelType.UnsignedByte;
        }

        data = XRTexture.AllocateBytes(width, height, pixelFormat, pixelType);
        fixed (byte* ptr = data)
        {
            Api.GetTextureSubImage(
                textureBindingId,
                mipLevel,
                0,
                0,
                layer,
                width,
                height,
                1,
                GLObjectBase.ToGLEnum(pixelFormat),
                GLObjectBase.ToGLEnum(pixelType),
                (uint)data.Length,
                ptr);
        }

        return true;
    }

    private bool IsDepthStencilFormat(InternalFormat internalFormat) => internalFormat switch
    {
        InternalFormat.Depth24Stencil8 or
        InternalFormat.Depth32fStencil8 or
        InternalFormat.Depth32fStencil8NV => true,
        _ => false,
    };

    public unsafe void CaptureCurrentlyBoundFBOAttachment(
        BoundingRectangle region,
        Action<MagickImage, int> imageCallback,
        EPixelFormat pixelFormat,
        EPixelType pixelType,
        bool async = true)
    {
        uint w = (uint)region.Width;
        uint h = (uint)region.Height;
        var data = XRTexture.AllocateBytes(w, h, pixelFormat, pixelType);

        if (async)
        {
            nuint size = (uint)data.Length;
            uint pbo = ReadFBOToPBO(region, pixelFormat, pixelType, size, out IntPtr sync);
            bool FenceCheck()
            {
                if (!GetData(size, data, sync, pbo))
                    return false;
                else
                {
                    Api.DeleteSync(sync);
                    Api.DeleteBuffer(pbo);

                    void MakeImage()
                    {
                        if (pixelType == EPixelType.Float32UnsignedInt248Rev || pixelType == EPixelType.UnsignedInt248)
                        {
                            MakeDepthStencilImages(pixelType, w, h, data, out MagickImage depth, out MagickImage stencil);
                            imageCallback(depth, 0);
                            imageCallback(stencil, 1);
                        }
                        else
                            imageCallback(OpenGLRenderer.MakeImage(pixelFormat, pixelType, w, h, data), 0);
                    }
                    Task.Run(MakeImage);

                    return true;
                }
            }
            RuntimeEngine.AddMainThreadCoroutine(FenceCheck);
        }
        else
        {
            fixed (byte* ptr = data)
            {
                Api.ReadPixels(region.X, region.Y, w, h, GLObjectBase.ToGLEnum(pixelFormat), GLObjectBase.ToGLEnum(pixelType), ptr);
            }
            Task.Run(() => imageCallback(XRTexture.NewImage(w, h, pixelFormat, pixelType, data), 0));
        }
    }

    private static unsafe MagickImage MakeImage(EPixelFormat format, EPixelType pixelType, uint w, uint h, byte[] data)
        => XRTexture.NewImage(w, h, format, pixelType, data);

    private unsafe void MakeDepthStencilImages(EPixelType pixelType, uint w, uint h, byte[] data, out MagickImage depth, out MagickImage stencil)
    {
        bool floatType = pixelType == EPixelType.Float32UnsignedInt248Rev;
        depth = XRTexture.NewImage(w, h, EPixelFormat.Rgb, EPixelType.UnsignedByte, ExtractDepthData(floatType, data));
        stencil = XRTexture.NewImage(w, h, EPixelFormat.Rgb, EPixelType.UnsignedByte, ExtractStencilData(floatType, data));
    }
    private unsafe MagickImage MakeDepthImage(EPixelType pixelType, uint w, uint h, byte[] data)
    {
        bool floatType = pixelType == EPixelType.Float32UnsignedInt248Rev;
        return XRTexture.NewImage(w, h, EPixelFormat.Rgb, EPixelType.UnsignedByte, ExtractDepthData(floatType, data));
    }
    private unsafe MagickImage MakeStencilImage(EPixelType pixelType, uint w, uint h, byte[] data)
    {
        bool floatType = pixelType == EPixelType.Float32UnsignedInt248Rev;
        return XRTexture.NewImage(w, h, EPixelFormat.Rgb, EPixelType.UnsignedByte, ExtractStencilData(floatType, data));
    }

    private byte[] ExtractStencilData(bool floatingPoint, byte[] data)
    {
        //every 3 bytes is the depth, and the last byte is the stencil
        //we're converting that last byte into grayscale rgb -> 3 bytes with same value
        int bytesPerPixel = floatingPoint ? 8 : 4;
        int stencilOffset = floatingPoint ? 4 : 3;
        int pixelCount = data.Length / bytesPerPixel;
        byte[] newData = new byte[pixelCount * 3];
        Parallel.For(0, pixelCount, i =>
        {
            int index = i * bytesPerPixel;
            int newIndex = i * 3;
            byte stencil = data[index + stencilOffset];
            newData[newIndex] = stencil;
            newData[newIndex + 1] = stencil;
            newData[newIndex + 2] = stencil;
        });
        return newData;
    }

    private byte[] ExtractDepthData(bool floatingPoint, byte[] data)
    {
        //every 3 bytes is the depth, and the last byte is the stencil
        //if float, 4 bytes are used for the depth, a byte for stencil, and 3 bytes to align
        //we're converting that depth value down into a byte and then into grayscale rgb -> 3 bytes with same value
        int bytesPerPixel = floatingPoint ? 8 : 4;
        int pixelCount = data.Length / bytesPerPixel;
        byte[] newData = new byte[pixelCount * 3];
        Parallel.For(0, pixelCount, i =>
        {
            int index = i * bytesPerPixel;
            int newIndex = i * 3;

            float depth = floatingPoint
                ? BitConverter.Int32BitsToSingle((data[index] << 24) | (data[index + 1] << 16) | data[index + 2] << 8 | data[index + 3])
                : ((data[index] << 16) | (data[index + 1] << 8) | data[index + 2]) / (float)0xFFFFFF;

            byte compressedDepth = (byte)(depth * 255.0f);

            newData[newIndex] = compressedDepth;
            newData[newIndex + 1] = compressedDepth;
            newData[newIndex + 2] = compressedDepth;
        });
        return newData;
    }

    public override void GetPixelAsync(int x, int y, bool withTransparency, Action<ColorF4> pixelCallback)
    {
        //TODO: render to an FBO with the desired render size and capture from that, instead of using the window size.

        //TODO: multi-glcontext readback.
        //This method is async on the CPU, but still executes synchronously on the GPU.
        //https://developer.download.nvidia.com/GTC/PDF/GTC2012/PresentationPDF/S0356-GTC2012-Texture-Transfers.pdf

        EPixelFormat format = withTransparency ? EPixelFormat.Bgra : EPixelFormat.Bgr;
        EPixelType pixelType = EPixelType.UnsignedByte;
        var data = XRTexture.AllocateBytes(1, 1, format, pixelType);

        Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        Api.ReadBuffer(ReadBufferMode.Front);

        nuint size = (uint)data.Length;
        uint pbo = ReadFBOToPBO(new BoundingRectangle(x, y, 1, 1), format, pixelType, size, out IntPtr sync);
        void FenceCheck()
        {
            if (GetData(size, data, sync, pbo))
            {
                Api.DeleteSync(sync);
                Api.DeleteBuffer(pbo);
                ColorF4 color = new(data[0] / 255.0f, data[1] / 255.0f, data[2] / 255.0f, data[3] / 255.0f);
                Task.Run(() => pixelCallback(color));
            }
            else
            {
                RuntimeEngine.EnqueueMainThreadTask(FenceCheck);
            }
        }
        RuntimeEngine.EnqueueMainThreadTask(FenceCheck);
    }
    public override unsafe void GetDepthAsync(XRFrameBuffer fbo, int x, int y, Action<float> depthCallback)
    {
        //TODO: render to an FBO with the desired render size and capture from that, instead of using the window size.

        //TODO: multi-glcontext readback.
        //This method is async on the CPU, but still executes synchronously on the GPU.
        //https://developer.download.nvidia.com/GTC/PDF/GTC2012/PresentationPDF/S0356-GTC2012-Texture-Transfers.pdf

        EPixelFormat format = EPixelFormat.DepthComponent;
        EPixelType pixelType = EPixelType.Float;
        var data = XRTexture.AllocateBytes(1, 1, format, pixelType);

        using var t = fbo.BindForReadingState();
        Api.ReadBuffer(ReadBufferMode.None);

        nuint size = (uint)data.Length;
        uint pbo = ReadFBOToPBO(new BoundingRectangle(x, y, 1, 1), format, pixelType, size, out IntPtr sync);
        void FenceCheck()
        {
            if (GetData(size, data, sync, pbo))
            {
                Api.DeleteSync(sync);
                Api.DeleteBuffer(pbo);
                fixed (byte* ptr = data)
                {
                    float depth = *(float*)ptr;
                    Task.Run(() => depthCallback(depth));
                }
            }
            else
            {
                RuntimeEngine.EnqueueMainThreadTask(FenceCheck);
            }
        }
        RuntimeEngine.EnqueueMainThreadTask(FenceCheck);
    }

    public override unsafe bool TryReadTextureMipRgbaFloat(
        XRTexture texture,
        int mipLevel,
        int layerIndex,
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

        if (texture is XRTexture2D tex2D && tex2D.MultiSample)
        {
            failure = "Multisample textures do not support mip readback";
            return false;
        }

        if (texture is XRTexture2DArray tex2DArray && tex2DArray.MultiSample)
        {
            failure = "Multisample textures do not support mip readback";
            return false;
        }

        AbstractRenderAPIObject? apiRenderObject = GetOrCreateAPIRenderObject(texture);
        if (apiRenderObject is not GLObjectBase apiObject)
        {
            failure = "Texture not uploaded";
            return false;
        }

        uint binding = apiObject.BindingId;
        if (binding == GLObjectBase.InvalidBindingId || binding == 0)
        {
            failure = "Texture not ready";
            return false;
        }

        int baseWidth;
        int baseHeight;
        switch (texture)
        {
            case XRTexture2D t2d:
                baseWidth = (int)t2d.Width;
                baseHeight = (int)t2d.Height;
                break;
            case XRTexture2DArray t2da:
                baseWidth = (int)t2da.Width;
                baseHeight = (int)t2da.Height;
                break;
            default:
                failure = "Unsupported texture type";
                return false;
        }

        width = Math.Max(1, baseWidth >> Math.Max(0, mipLevel));
        height = Math.Max(1, baseHeight >> Math.Max(0, mipLevel));

        GL gl = RawGL;
        if (texture is XRTexture2DArray array)
        {
            int layers = Math.Max(1, (int)array.Depth);
            int clampedLayer = Math.Clamp(layerIndex, 0, layers - 1);
            int floatCountAll = width * height * 4 * layers;
            float[] allLayers = new float[floatCountAll];

            fixed (float* ptr = allLayers)
            {
                gl.GetTextureImage(binding, mipLevel, GLEnum.Rgba, GLEnum.Float, (uint)(sizeof(float) * floatCountAll), ptr);
            }

            int floatCountLayer = width * height * 4;
            rgbaFloats = new float[floatCountLayer];
            Array.Copy(allLayers, clampedLayer * floatCountLayer, rgbaFloats, 0, floatCountLayer);
            return true;
        }

        int floatCount = width * height * 4;
        rgbaFloats = new float[floatCount];
        fixed (float* ptr = rgbaFloats)
        {
            gl.GetTextureImage(binding, mipLevel, GLEnum.Rgba, GLEnum.Float, (uint)(sizeof(float) * floatCount), ptr);
        }

        return true;
    }

    public override bool TryReadTexturePixelRgbaFloat(
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

    private unsafe uint ReadFBOToPBO(BoundingRectangle region, EPixelFormat format, EPixelType type, nuint size, out IntPtr sync)
    {
        uint pbo = Api.GenBuffer();
        Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
        Api.BufferData(GLEnum.PixelPackBuffer, size, null, GLEnum.StreamRead);
        Api.ReadPixels(region.X, region.Y, (uint)region.Width, (uint)region.Height, GLObjectBase.ToGLEnum(format), GLObjectBase.ToGLEnum(type), null);
        sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        Api.BindBuffer(GLEnum.PixelPackBuffer, 0);
        return pbo;
    }

    private unsafe uint ReadTextureToPBO(uint textureId, BoundingRectangle region, int layerOffset, uint layerCount, EPixelFormat format, EPixelType type, uint size, out IntPtr sync)
    {
        uint pbo = Api.GenBuffer();
        Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
        Api.BufferData(GLEnum.PixelPackBuffer, size, null, GLEnum.StreamRead);
        Api.GetTextureSubImage(textureId, 0, region.X, region.Y, layerOffset, (uint)region.Width, (uint)region.Height, layerCount, GLObjectBase.ToGLEnum(format), GLObjectBase.ToGLEnum(type), size, null);
        sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        Api.BindBuffer(GLEnum.PixelPackBuffer, 0);
        return pbo;
    }

    private unsafe bool GetData(nuint size, byte[] data, IntPtr sync, uint pbo)
    {
        var result = Api.ClientWaitSync(sync, 0u, 0u);
        if (!(result == GLEnum.AlreadySignaled || result == GLEnum.ConditionSatisfied))
            return false;

        Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
        fixed (byte* ptr = data)
        {
            Api.GetBufferSubData(GLEnum.PixelPackBuffer, IntPtr.Zero, size, ptr);
        }
        Api.BindBuffer(GLEnum.PixelPackBuffer, 0);
        RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes((long)size);

        return true;
    }

    public override unsafe float GetDepth(int x, int y)
    {
        float depth = 0.0f;
        Api.ReadPixels(x, y, 1, 1, PixelFormat.DepthComponent, PixelType.Float, &depth);
        return depth;
    }
}
