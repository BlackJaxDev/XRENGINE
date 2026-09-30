using XREngine.Extensions;
using XREngine.Imaging;
using System.Buffers.Binary;
using ImGuiNET;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ARB;
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
    /// <summary>Reads the completed front buffer, including the window's UI composition.</summary>
    public override bool TryQueueCompositedScreenshotReadback(
        BoundingRectangle region,
        Action<ScreenshotReadbackResult> callback,
        out string? failure)
    {
        int previousReadFramebuffer = Api.GetInteger(GetPName.ReadFramebufferBinding);
        try
        {
            Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            return TryQueueScreenshotReadback(region, false, callback, out failure);
        }
        finally
        {
            Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, unchecked((uint)previousReadFramebuffer));
        }
    }

    public override void GetScreenshotAsync(BoundingRectangle region, bool withTransparency, Action<RuntimeImage, int> imageCallback)
    {
        //TODO: render to an FBO with the desired render size and capture from that, instead of using the window size.

        //TODO: multi-glcontext readback.
        //This method is async on the CPU, but still executes synchronously on the GPU.
        //https://developer.download.nvidia.com/GTC/PDF/GTC2012/PresentationPDF/S0356-GTC2012-Texture-Transfers.pdf

        // Honor the viewport read scope; composited capture explicitly selects the window.
        uint readFramebuffer = unchecked((uint)Api.GetInteger(GetPName.ReadFramebufferBinding));
        ReadBufferMode readBuffer = readFramebuffer == 0
            ? ReadBufferMode.Front
            : (ReadBufferMode)Api.GetInteger(GetPName.ReadBuffer);
        CaptureFBOColorAttachment(region, withTransparency,
            (image, _) => imageCallback(image, image is null ? 0 : checked((int)((long)image.Width * image.Height))),
            readFramebuffer, readBuffer, -1, true);
    }

    public void CaptureFBOAttachment(
        BoundingRectangle region,
        bool withTransparency,
        Action<RuntimeImage, int> imageCallback,
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
        Action<RuntimeImage, int> imageCallback,
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
        Action<RuntimeImage, int> imageCallback,
        uint readFBOBindingId,
        ReadBufferMode readBuffer,
        EPixelFormat format,
        EPixelType pixelType,
        int layer = -1,
        bool async = true)
    {
        int previousReadFramebuffer = Api.GetInteger(GetPName.ReadFramebufferBinding);
        int previousReadBuffer = Api.GetInteger(GetPName.ReadBuffer);
        try
        {
            Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFBOBindingId);
            Api.ReadBuffer(readBuffer);
            CaptureCurrentlyBoundFBOAttachment(region, imageCallback, format, pixelType, async);
        }
        finally
        {
            Api.BindFramebuffer(FramebufferTarget.ReadFramebuffer, unchecked((uint)previousReadFramebuffer));
            Api.ReadBuffer((ReadBufferMode)previousReadBuffer);
        }
    }

    public delegate void DelImageCallback(RuntimeImage image, int layer, int channelIndex);

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
            uint pbo = ReadTextureToPBO(textureBindingId, mipLevel, region, layer, 1, pixelFormat, pixelType, size, out IntPtr sync);
            bool FenceCheck()
            {
                bool complete = false;
                try
                {
                    if (!GetData(size, data, sync, pbo, terminalOnWaitFailed: true))
                        return false;
                    complete = true;

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
                catch (Exception ex)
                {
                    complete = true;
                    Debug.OpenGLWarning($"Texture readback failed: {ex.Message}");
                    Task.Run(() => imageCallback(null!, layer, 0));
                    return true;
                }
                finally
                {
                    if (complete)
                    {
                        Api.DeleteSync(sync);
                        Api.DeleteBuffer(pbo);
                    }
                }
            }
            RuntimeEngine.AddMainThreadCoroutine(FenceCheck);
        }
        else
        {
            int previousPackBuffer = Api.GetInteger(GLEnum.PixelPackBufferBinding);
            int previousPackAlignment = Api.GetInteger(GetPName.PackAlignment);
            try
            {
                Api.BindBuffer(GLEnum.PixelPackBuffer, 0);
                Api.PixelStore(PixelStoreParameter.PackAlignment, 1);
                fixed (byte* ptr = data)
                    Api.GetTextureSubImage(textureBindingId, mipLevel, region.X, region.Y, layer, w, h, 1, GLObjectBase.ToGLEnum(pixelFormat), GLObjectBase.ToGLEnum(pixelType), (uint)data.Length, ptr);
            }
            finally
            {
                Api.PixelStore(PixelStoreParameter.PackAlignment, previousPackAlignment);
                Api.BindBuffer(GLEnum.PixelPackBuffer, unchecked((uint)previousPackBuffer));
            }
            Task.Run(() => imageCallback(MakeImage(pixelFormat, pixelType, w, h, data), layer, 0));
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
        int previousPackBuffer = Api.GetInteger(GLEnum.PixelPackBufferBinding);
        int previousPackAlignment = Api.GetInteger(GetPName.PackAlignment);
        try
        {
            Api.BindBuffer(GLEnum.PixelPackBuffer, 0);
            Api.PixelStore(PixelStoreParameter.PackAlignment, 1);
            fixed (byte* ptr = data)
                Api.GetTextureSubImage(
                    textureBindingId, mipLevel, 0, 0, layer, width, height, 1,
                    GLObjectBase.ToGLEnum(pixelFormat), GLObjectBase.ToGLEnum(pixelType),
                    (uint)data.Length, ptr);
        }
        finally
        {
            Api.PixelStore(PixelStoreParameter.PackAlignment, previousPackAlignment);
            Api.BindBuffer(GLEnum.PixelPackBuffer, unchecked((uint)previousPackBuffer));
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
        Action<RuntimeImage, int> imageCallback,
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
                bool complete = false;
                try
                {
                    if (!GetData(size, data, sync, pbo, terminalOnWaitFailed: true))
                        return false;
                    complete = true;

                    void MakeImage()
                    {
                        if (pixelType == EPixelType.Float32UnsignedInt248Rev || pixelType == EPixelType.UnsignedInt248)
                        {
                            MakeDepthStencilImages(pixelType, w, h, data, out RuntimeImage depth, out RuntimeImage stencil);
                            RuntimeImage? pendingDepth = depth;
                            RuntimeImage? pendingStencil = stencil;
                            try
                            {
                                pendingDepth = null;
                                imageCallback(depth, 0);
                                pendingStencil = null;
                                imageCallback(stencil, 1);
                            }
                            finally
                            {
                                pendingDepth?.Dispose();
                                pendingStencil?.Dispose();
                            }
                        }
                        else
                            imageCallback(OpenGLRenderer.MakeImage(pixelFormat, pixelType, w, h, data), 0);
                    }
                    Task.Run(MakeImage);

                    return true;
                }
                catch (Exception ex)
                {
                    complete = true;
                    Debug.OpenGLWarning($"Framebuffer readback failed: {ex.Message}");
                    Task.Run(() => imageCallback(null!, 0));
                    return true;
                }
                finally
                {
                    if (complete)
                    {
                        Api.DeleteSync(sync);
                        Api.DeleteBuffer(pbo);
                    }
                }
            }
            RuntimeEngine.AddMainThreadCoroutine(FenceCheck);
        }
        else
        {
            int previousPackBuffer = Api.GetInteger(GLEnum.PixelPackBufferBinding);
            int previousPackAlignment = Api.GetInteger(GetPName.PackAlignment);
            try
            {
                Api.BindBuffer(GLEnum.PixelPackBuffer, 0);
                Api.PixelStore(PixelStoreParameter.PackAlignment, 1);
                fixed (byte* ptr = data)
                    Api.ReadPixels(region.X, region.Y, w, h, GLObjectBase.ToGLEnum(pixelFormat), GLObjectBase.ToGLEnum(pixelType), ptr);
            }
            finally
            {
                Api.PixelStore(PixelStoreParameter.PackAlignment, previousPackAlignment);
                Api.BindBuffer(GLEnum.PixelPackBuffer, unchecked((uint)previousPackBuffer));
            }
            Task.Run(() => imageCallback(MakeImage(pixelFormat, pixelType, w, h, data), 0));
        }
    }

    private static RuntimeImage MakeImage(EPixelFormat format, EPixelType pixelType, uint w, uint h, byte[] data)
        => new(w, h, format, pixelType, data, origin: RuntimeImageOrigin.BottomLeft);

    private static void MakeDepthStencilImages(EPixelType pixelType, uint w, uint h, byte[] data,
        out RuntimeImage depth, out RuntimeImage stencil)
    {
        depth = MakeDepthImage(pixelType, w, h, data);
        try
        {
            stencil = MakeStencilImage(pixelType, w, h, data);
        }
        catch
        {
            depth.Dispose();
            throw;
        }
    }

    private static RuntimeImage MakeDepthImage(EPixelType pixelType, uint w, uint h, byte[] data)
        => new(w, h, EPixelFormat.DepthComponent, EPixelType.Float,
            ExtractDepthData(pixelType == EPixelType.Float32UnsignedInt248Rev, data),
            origin: RuntimeImageOrigin.BottomLeft);

    private static RuntimeImage MakeStencilImage(EPixelType pixelType, uint w, uint h, byte[] data)
        => new(w, h, EPixelFormat.StencilIndex, EPixelType.UnsignedByte,
            ExtractStencilData(pixelType == EPixelType.Float32UnsignedInt248Rev, data),
            origin: RuntimeImageOrigin.BottomLeft);

    private static byte[] ExtractStencilData(bool floatingPoint, byte[] data)
    {
        int bytesPerPixel = floatingPoint ? 8 : 4;
        int pixelCount = data.Length / bytesPerPixel;
        byte[] stencil = new byte[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            ReadOnlySpan<byte> word = data.AsSpan(i * bytesPerPixel + (floatingPoint ? 4 : 0), 4);
            stencil[i] = (byte)(BinaryPrimitives.ReadUInt32LittleEndian(word) & 0xFF);
        }
        return stencil;
    }

    private static byte[] ExtractDepthData(bool floatingPoint, byte[] data)
    {
        int bytesPerPixel = floatingPoint ? 8 : 4;
        int pixelCount = data.Length / bytesPerPixel;
        byte[] depth = new byte[checked(pixelCount * sizeof(float))];
        for (int i = 0; i < pixelCount; i++)
        {
            ReadOnlySpan<byte> word = data.AsSpan(i * bytesPerPixel, 4);
            float value = floatingPoint
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(word))
                : (BinaryPrimitives.ReadUInt32LittleEndian(word) >> 8) / 16777215.0f;
            BinaryPrimitives.WriteInt32LittleEndian(
                depth.AsSpan(i * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(value));
        }
        return depth;
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
            bool complete = false;
            try
            {
                if (GetData(size, data, sync, pbo, terminalOnWaitFailed: true))
                {
                    complete = true;
                    ColorF4 color = new(data[2] / 255.0f, data[1] / 255.0f, data[0] / 255.0f,
                        withTransparency ? data[3] / 255.0f : 1.0f);
                    Task.Run(() => pixelCallback(color));
                }
                else
                {
                    RuntimeEngine.EnqueueMainThreadTask(FenceCheck);
                }
            }
            catch
            {
                complete = true;
                throw;
            }
            finally
            {
                if (complete)
                {
                    Api.DeleteSync(sync);
                    Api.DeleteBuffer(pbo);
                }
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
            bool complete = false;
            try
            {
                if (GetData(size, data, sync, pbo, terminalOnWaitFailed: true))
                {
                    complete = true;
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
            catch
            {
                complete = true;
                throw;
            }
            finally
            {
                if (complete)
                {
                    Api.DeleteSync(sync);
                    Api.DeleteBuffer(pbo);
                }
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

        if (texture is XRTexture2D { MultiSample: true } or
            XRTexture2DArray { MultiSample: true } or
            XRTexture2DView { Multisample: true } or
            XRTexture2DArrayView { Multisample: true })
        {
            failure = "Multisample textures do not support mip readback";
            return false;
        }

        if (texture is not (XRTexture2D or XRTexture2DArray or XRTexture2DView or XRTexture2DArrayView or
            XRTextureCube or XRTextureCubeArray or XRTextureCubeView or XRTextureCubeArrayView))
        {
            failure = "Readback requires a 2D or cube texture, array, or view.";
            return false;
        }

        if (mipLevel < 0 || layerIndex < 0)
        {
            failure = "Texture mip/layer readback is outside the requested image.";
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

        GL gl = RawGL;
        // A resource can retain an API wrapper while its prior storage is being
        // replaced. A generated name alone is not an instantiated GL texture.
        if (!gl.IsTexture(binding))
        {
            failure = "Texture storage is not live in the current OpenGL context.";
            return false;
        }
        // Query the view's actual mip extent, rather than the parent texture's
        // dimensions; view level/layer offsets are already encoded by GL.
        gl.GetTextureLevelParameter(binding, mipLevel, GLEnum.TextureWidth, out width);
        gl.GetTextureLevelParameter(binding, mipLevel, GLEnum.TextureHeight, out height);
        gl.GetTextureLevelParameter(binding, mipLevel, GLEnum.TextureDepth, out int layers);
        // A cube level reports a single 2D face's depth, while subimage readback
        // addresses the six faces as z slices. Cube arrays already report all faces.
        if (texture is XRTextureCube or XRTextureCubeView)
            layers = 6;
        if (width <= 0 || height <= 0 || layerIndex >= Math.Max(1, layers))
        {
            failure = "Texture mip/layer readback is outside the requested image.";
            return false;
        }
        gl.GetTextureLevelParameter(binding, mipLevel, GLEnum.TextureRedType, out int redType);
        gl.GetTextureLevelParameter(binding, mipLevel, GLEnum.TextureDepthSize, out int depthBits);
        gl.GetTextureLevelParameter(binding, mipLevel, GLEnum.TextureStencilSize, out int stencilBits);
        gl.GetTextureParameterI(binding, GLEnum.DepthStencilTextureMode, out int depthStencilMode);
        bool readStencil = stencilBits > 0 && (depthBits == 0 || depthStencilMode == (int)GLEnum.StencilIndex);
        bool readDepth = depthBits > 0 && !readStencil;
        bool scalar = readDepth || readStencil;
        bool unsignedInteger = redType == (int)GLEnum.UnsignedInt;
        bool signedInteger = redType == (int)GLEnum.Int;
        int floatCount = checked(width * height * 4);
        rgbaFloats = new float[floatCount];
        int oldPackBuffer = gl.GetInteger(GLEnum.PixelPackBufferBinding);
        gl.BindBuffer(GLEnum.PixelPackBuffer, 0u);
        // Compute image writes require texture-update visibility for readback.
        gl.MemoryBarrier(MemoryBarrierMask.TextureUpdateBarrierBit);
        try
        {
        fixed (float* ptr = rgbaFloats)
        {
            gl.GetTextureSubImage(binding, mipLevel, 0, 0, layerIndex, (uint)width, (uint)height, 1u,
                readStencil ? GLEnum.StencilIndex : readDepth ? GLEnum.DepthComponent : unsignedInteger || signedInteger ? GLEnum.RgbaInteger : GLEnum.Rgba,
                readStencil || unsignedInteger ? GLEnum.UnsignedInt : signedInteger ? GLEnum.Int : GLEnum.Float,
                checked((uint)(sizeof(float) * floatCount)), ptr);
            GLEnum error = gl.GetError();
            if (error != GLEnum.NoError)
            {
                rgbaFloats = null;
                failure = $"OpenGL texture readback failed: {error}.";
                return false;
            }
            // Integer attachments cannot be read with GL_RGBA/GL_FLOAT. Read
            // their native scalar type, then numerically convert for tooling.
            // Scalar depth/stencil transfers occupy the start of the same buffer.
            // Expand backwards so conversion cannot overwrite unread samples.
            if (scalar)
                for (int index = width * height - 1; index >= 0; index--)
                {
                    float value = readStencil ? ((uint*)ptr)[index] : ptr[index];
                    ptr[index * 4] = ptr[index * 4 + 1] = ptr[index * 4 + 2] = value;
                    ptr[index * 4 + 3] = 1.0f;
                }
            else if (unsignedInteger)
                for (int index = 0; index < floatCount; index++) ptr[index] = ((uint*)ptr)[index];
            else if (signedInteger)
                for (int index = 0; index < floatCount; index++) ptr[index] = ((int*)ptr)[index];
        }
        return true;
        }
        finally { gl.BindBuffer(GLEnum.PixelPackBuffer, (uint)oldPackBuffer); }
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
        int previousPackBuffer = Api.GetInteger(GLEnum.PixelPackBufferBinding);
        int previousPackAlignment = Api.GetInteger(GetPName.PackAlignment);
        uint pbo = Api.GenBuffer();
        try
        {
            Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
            Api.PixelStore(PixelStoreParameter.PackAlignment, 1);
            Api.BufferData(GLEnum.PixelPackBuffer, size, null, GLEnum.StreamRead);
            Api.ReadPixels(region.X, region.Y, (uint)region.Width, (uint)region.Height, GLObjectBase.ToGLEnum(format), GLObjectBase.ToGLEnum(type), null);
            sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
            if (sync == IntPtr.Zero)
                throw new InvalidOperationException("OpenGL failed to create the framebuffer readback fence.");
            return pbo;
        }
        catch
        {
            Api.DeleteBuffer(pbo);
            throw;
        }
        finally
        {
            Api.PixelStore(PixelStoreParameter.PackAlignment, previousPackAlignment);
            Api.BindBuffer(GLEnum.PixelPackBuffer, unchecked((uint)previousPackBuffer));
        }
    }

    private unsafe uint ReadTextureToPBO(uint textureId, int mipLevel, BoundingRectangle region, int layerOffset, uint layerCount, EPixelFormat format, EPixelType type, uint size, out IntPtr sync)
    {
        int previousPackBuffer = Api.GetInteger(GLEnum.PixelPackBufferBinding);
        int previousPackAlignment = Api.GetInteger(GetPName.PackAlignment);
        uint pbo = Api.GenBuffer();
        try
        {
            Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
            Api.PixelStore(PixelStoreParameter.PackAlignment, 1);
            Api.BufferData(GLEnum.PixelPackBuffer, size, null, GLEnum.StreamRead);
            Api.GetTextureSubImage(textureId, mipLevel, region.X, region.Y, layerOffset, (uint)region.Width, (uint)region.Height, layerCount, GLObjectBase.ToGLEnum(format), GLObjectBase.ToGLEnum(type), size, null);
            sync = Api.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
            if (sync == IntPtr.Zero)
                throw new InvalidOperationException("OpenGL failed to create the texture readback fence.");
            return pbo;
        }
        catch
        {
            Api.DeleteBuffer(pbo);
            throw;
        }
        finally
        {
            Api.PixelStore(PixelStoreParameter.PackAlignment, previousPackAlignment);
            Api.BindBuffer(GLEnum.PixelPackBuffer, unchecked((uint)previousPackBuffer));
        }
    }

    private unsafe bool GetData(nuint size, byte[] data, IntPtr sync, uint pbo, bool terminalOnWaitFailed = false)
    {
        var result = Api.ClientWaitSync(sync, 0u, 0u);
        if (result == GLEnum.WaitFailed && terminalOnWaitFailed)
            throw new InvalidOperationException("OpenGL readback fence wait failed.");
        if (!(result == GLEnum.AlreadySignaled || result == GLEnum.ConditionSatisfied))
            return false;

        int previousPackBuffer = Api.GetInteger(GLEnum.PixelPackBufferBinding);
        try
        {
            Api.BindBuffer(GLEnum.PixelPackBuffer, pbo);
            fixed (byte* ptr = data)
                Api.GetBufferSubData(GLEnum.PixelPackBuffer, IntPtr.Zero, size, ptr);
        }
        finally
        {
            Api.BindBuffer(GLEnum.PixelPackBuffer, unchecked((uint)previousPackBuffer));
        }
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
