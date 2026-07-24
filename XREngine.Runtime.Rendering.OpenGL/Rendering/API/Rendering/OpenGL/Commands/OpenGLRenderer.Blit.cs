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
    public override void Blit(
        XRFrameBuffer? inFBO,
        XRFrameBuffer? outFBO,
        int inX, int inY, uint inW, uint inH,
        int outX, int outY, uint outW, uint outH,
        EReadBufferMode readBufferMode,
        bool colorBit, bool depthBit, bool stencilBit,
        bool linearFilter)
    {
        ClearBufferMask mask = 0;
        if (colorBit)
            mask |= ClearBufferMask.ColorBufferBit;
        if (depthBit)
            mask |= ClearBufferMask.DepthBufferBit;
        if (stencilBit)
            mask |= ClearBufferMask.StencilBufferBit;

        var glIn = GenericToAPI<GLFrameBuffer>(inFBO);
        var glOut = GenericToAPI<GLFrameBuffer>(outFBO);
        var inID = glIn?.BindingId ?? 0u;
        var outID = glOut?.BindingId ?? 0u;

        // Guard: verify both FBOs are complete before blitting.
        // Incomplete FBOs can fail color resolves just as silently as depth/stencil blits,
        // so keep the same once-per-pair warning for all blit paths.
        {
            var srcStatus = Api.CheckNamedFramebufferStatus(inID, FramebufferTarget.ReadFramebuffer);
            var dstStatus = Api.CheckNamedFramebufferStatus(outID, FramebufferTarget.DrawFramebuffer);
            if (srcStatus != GLEnum.FramebufferComplete || dstStatus != GLEnum.FramebufferComplete)
            {
                LogBlitSkipOnce(inID, outID, srcStatus, dstStatus);
                return;
            }
        }

        Api.NamedFramebufferReadBuffer(inID, ToGLEnum(readBufferMode));

        // Drain any pre-existing GL errors so they don't falsely trigger the
        // post-blit check.
        while (Api.GetError() != GLEnum.NoError) { }

        Api.BlitNamedFramebuffer(
            inID,
            outID,
            inX,
            inY,
            inX + (int)inW,
            inY + (int)inH,
            outX,
            outY,
            outX + (int)outW,
            outY + (int)outH,
            mask,
            linearFilter ? BlitFramebufferFilter.Linear : BlitFramebufferFilter.Nearest);

        // Check for GL errors from the blit and log once per FBO pair to avoid
        // per-frame spam (e.g. NVIDIA "Depth formats do not match").
        var blitErr = Api.GetError();
        if (blitErr != GLEnum.NoError)
            LogBlitErrorOnce(inID, outID, blitErr);
    }

    private readonly HashSet<(uint, uint)> _blitErrorWarned = [];
    private void LogBlitErrorOnce(uint srcId, uint dstId, GLEnum error)
    {
        if (!_blitErrorWarned.Add((srcId, dstId)))
            return;
        Debug.OpenGLWarning(
            $"BlitNamedFramebuffer FBO {srcId}?{dstId} raised {error}. " +
            $"Subsequent errors for this pair will be suppressed.");
    }

    private readonly HashSet<(uint, uint)> _blitSkipWarned = [];
    private void LogBlitSkipOnce(uint srcId, uint dstId, GLEnum srcStatus, GLEnum dstStatus)
    {
        if (!_blitSkipWarned.Add((srcId, dstId)))
            return;
        Debug.OpenGLWarning(
            $"Skipping blit: FBO {srcId}?{dstId} incomplete " +
            $"(src={srcStatus}, dst={dstStatus}).");
    }

    public override void BlitWithDrawBuffer(
        XRFrameBuffer? inFBO,
        XRFrameBuffer? outFBO,
        uint inW, uint inH,
        uint outW, uint outH,
        EReadBufferMode readBufferMode,
        EReadBufferMode drawBufferMode,
        bool colorBit, bool depthBit, bool stencilBit,
        bool linearFilter)
    {
        ClearBufferMask mask = 0;
        if (colorBit)
            mask |= ClearBufferMask.ColorBufferBit;
        if (depthBit)
            mask |= ClearBufferMask.DepthBufferBit;
        if (stencilBit)
            mask |= ClearBufferMask.StencilBufferBit;

        var glIn = GenericToAPI<GLFrameBuffer>(inFBO);
        var glOut = GenericToAPI<GLFrameBuffer>(outFBO);
        var inID = glIn?.BindingId ?? 0u;
        var outID = glOut?.BindingId ?? 0u;

        var srcStatus = Api.CheckNamedFramebufferStatus(inID, FramebufferTarget.ReadFramebuffer);
        var dstStatus = Api.CheckNamedFramebufferStatus(outID, FramebufferTarget.DrawFramebuffer);
        if (srcStatus != GLEnum.FramebufferComplete || dstStatus != GLEnum.FramebufferComplete)
        {
            LogBlitSkipOnce(inID, outID, srcStatus, dstStatus);
            return;
        }

        Api.NamedFramebufferReadBuffer(inID, ToGLEnum(readBufferMode));
        Api.NamedFramebufferDrawBuffer(outID, ToGLEnum(drawBufferMode));

        while (Api.GetError() != GLEnum.NoError) { }

        Api.BlitNamedFramebuffer(
            inID,
            outID,
            0, 0, (int)inW, (int)inH,
            0, 0, (int)outW, (int)outH,
            mask,
            linearFilter ? BlitFramebufferFilter.Linear : BlitFramebufferFilter.Nearest);

        var blitErr = Api.GetError();
        if (blitErr != GLEnum.NoError)
            LogBlitErrorOnce(inID, outID, blitErr);
    }

    public static int GetBytesPerPixel(InternalFormat internalFormat) => internalFormat switch
    {
        // Standard formats
        InternalFormat.Rgba8 => 4,
        InternalFormat.Rgb8 => 3,
        InternalFormat.R8 => 1,
        InternalFormat.RG8 => 2,

        // Depth/Stencil formats
        InternalFormat.DepthComponent32f => 4,
        InternalFormat.DepthComponent24 => 3,
        InternalFormat.DepthComponent16 => 2,
        InternalFormat.DepthComponent32 => 4,
        InternalFormat.DepthComponent => 4, // Default to 4 bytes (32-bit float)
        InternalFormat.StencilIndex8 => 1,
        InternalFormat.StencilIndex1 => 1,
        InternalFormat.StencilIndex4 => 1,
        InternalFormat.StencilIndex16 => 2,
        InternalFormat.StencilIndex => 1, // Default to 1 byte (8-bit)
        InternalFormat.Depth24Stencil8 => 4,
        InternalFormat.Depth32fStencil8 => 5, // 4 bytes depth + 1 byte stencil
        InternalFormat.DepthStencil => 4,
        InternalFormat.DepthStencilMesa => 4,

        // Base formats
        InternalFormat.Red => 1,
        InternalFormat.RG => 2,
        InternalFormat.Rgb => 3,
        InternalFormat.Rgba => 4,

        // Higher bit depth formats
        InternalFormat.Rgba16 => 8,
        InternalFormat.Rgb16 => 6,
        InternalFormat.RG16 => 4,
        InternalFormat.R16 => 2,
        InternalFormat.Rgba16f => 8,
        InternalFormat.Rgb16f => 6,
        InternalFormat.RG16f => 4,
        InternalFormat.R16f => 2,
        InternalFormat.Rgba32f => 16,
        InternalFormat.Rgb32f => 12,
        InternalFormat.RG32f => 8,
        InternalFormat.R32f => 4,

        // Integer formats
        InternalFormat.Rgba8i => 4,
        InternalFormat.Rgb8i => 3,
        InternalFormat.RG8i => 2,
        InternalFormat.R8i => 1,
        InternalFormat.Rgba16i => 8,
        InternalFormat.Rgb16i => 6,
        InternalFormat.RG16i => 4,
        InternalFormat.R16i => 2,
        InternalFormat.Rgba32i => 16,
        InternalFormat.Rgb32i => 12,
        InternalFormat.RG32i => 8,
        InternalFormat.R32i => 4,
        InternalFormat.Rgba8ui => 4,
        InternalFormat.Rgb8ui => 3,
        InternalFormat.RG8ui => 2,
        InternalFormat.R8ui => 1,
        InternalFormat.Rgba16ui => 8,
        InternalFormat.Rgb16ui => 6,
        InternalFormat.RG16ui => 4,
        InternalFormat.R16ui => 2,
        InternalFormat.Rgba32ui => 16,
        InternalFormat.Rgb32ui => 12,
        InternalFormat.RG32ui => 8,
        InternalFormat.R32ui => 4,

        // Special formats
        InternalFormat.R3G3B2 => 1,
        InternalFormat.Rgb565Oes => 2,
        InternalFormat.Rgba4 => 2,
        InternalFormat.Rgb5A1 => 2,
        InternalFormat.Rgb10A2 => 4,
        InternalFormat.Rgb10A2ui => 4,
        InternalFormat.R11fG11fB10f => 4,
        InternalFormat.Rgb9E5 => 4,

        // sRGB formats
        InternalFormat.Srgb8 => 3,
        InternalFormat.Srgb8Alpha8 => 4,
        InternalFormat.Srgb => 3,
        InternalFormat.SrgbAlpha => 4,

        // Signed normalized formats
        InternalFormat.R8SNorm => 1,
        InternalFormat.RG8SNorm => 2,
        InternalFormat.Rgb8SNorm => 3,
        InternalFormat.Rgba8SNorm => 4,
        InternalFormat.R16SNorm => 2,
        InternalFormat.RG16SNorm => 4,
        InternalFormat.Rgb16SNorm => 6,
        InternalFormat.Rgba16SNorm => 8,

        // Compressed formats - return estimated bytes per block
        InternalFormat.CompressedRgbS3TCDxt1Ext => 1, // ~0.5 bytes per pixel
        InternalFormat.CompressedRgbaS3TCDxt1Ext => 1, // ~0.5 bytes per pixel
        InternalFormat.CompressedRgbaS3TCDxt3Angle => 1, // ~1 byte per pixel
        InternalFormat.CompressedRgbaS3TCDxt5Angle => 1, // ~1 byte per pixel
        InternalFormat.CompressedRed => 1, // Depends on actual compression
        InternalFormat.CompressedRG => 1, // Depends on actual compression
        InternalFormat.CompressedRgb => 1, // Depends on actual compression
        InternalFormat.CompressedRgba => 1, // Depends on actual compression
        InternalFormat.CompressedSrgb => 1, // Depends on actual compression
        InternalFormat.CompressedSrgbAlpha => 1, // Depends on actual compression
        InternalFormat.CompressedSrgbS3TCDxt1Ext => 1, // ~0.5 bytes per pixel
        InternalFormat.CompressedSrgbAlphaS3TCDxt1Ext => 1, // ~0.5 bytes per pixel
        InternalFormat.CompressedSrgbAlphaS3TCDxt3Ext => 1, // ~1 byte per pixel
        InternalFormat.CompressedSrgbAlphaS3TCDxt5Ext => 1, // ~1 byte per pixel
        InternalFormat.CompressedRedRgtc1 => 1, // ~0.5 bytes per pixel
        InternalFormat.CompressedSignedRedRgtc1 => 1, // ~0.5 bytes per pixel
        InternalFormat.CompressedRedGreenRgtc2Ext => 1, // ~1 byte per pixel
        InternalFormat.CompressedSignedRedGreenRgtc2Ext => 1, // ~1 byte per pixel
        InternalFormat.Etc1Rgb8Oes => 1, // ~0.5 bytes per pixel

        // ASTC and other modern compressed formats
        InternalFormat.CompressedRgbaBptcUnorm => 1, // ~1 byte per pixel
        InternalFormat.CompressedSrgbAlphaBptcUnorm => 1, // ~1 byte per pixel
        InternalFormat.CompressedRgbBptcSignedFloat => 1, // ~1 byte per pixel
        InternalFormat.CompressedRgbBptcUnsignedFloat => 1, // ~1 byte per pixel
        InternalFormat.CompressedR11Eac => 1, // ~0.5 bytes per pixel
        InternalFormat.CompressedSignedR11Eac => 1, // ~0.5 bytes per pixel
        InternalFormat.CompressedRG11Eac => 1, // ~1 byte per pixel
        InternalFormat.CompressedSignedRG11Eac => 1, // ~1 byte per pixel
        InternalFormat.CompressedRgb8Etc2 => 1, // ~0.5 bytes per pixel
        InternalFormat.CompressedSrgb8Etc2 => 1, // ~0.5 bytes per pixel
        InternalFormat.CompressedRgb8PunchthroughAlpha1Etc2 => 1, // ~0.5 bytes per pixel
        InternalFormat.CompressedSrgb8PunchthroughAlpha1Etc2 => 1, // ~0.5 bytes per pixel
        InternalFormat.CompressedRgba8Etc2Eac => 1, // ~1 byte per pixel
        InternalFormat.CompressedSrgb8Alpha8Etc2Eac => 1, // ~1 byte per pixel

        // ASTC formats (all approximately 1 byte per pixel or less)
        InternalFormat.CompressedRgbaAstc4x4 => 1,
        InternalFormat.CompressedRgbaAstc5x4 => 1,
        InternalFormat.CompressedRgbaAstc5x5 => 1,
        InternalFormat.CompressedRgbaAstc6x5 => 1,
        InternalFormat.CompressedRgbaAstc6x6 => 1,
        InternalFormat.CompressedRgbaAstc8x5 => 1,
        InternalFormat.CompressedRgbaAstc8x6 => 1,
        InternalFormat.CompressedRgbaAstc8x8 => 1,
        InternalFormat.CompressedRgbaAstc10x5 => 1,
        InternalFormat.CompressedRgbaAstc10x6 => 1,
        InternalFormat.CompressedRgbaAstc10x8 => 1,
        InternalFormat.CompressedRgbaAstc10x10 => 1,
        InternalFormat.CompressedRgbaAstc12x10 => 1,
        InternalFormat.CompressedRgbaAstc12x12 => 1,

        // 3D ASTC formats
        InternalFormat.CompressedRgbaAstc3x3x3Oes => 1,
        InternalFormat.CompressedRgbaAstc4x3x3Oes => 1,
        InternalFormat.CompressedRgbaAstc4x4x3Oes => 1,
        InternalFormat.CompressedRgbaAstc4x4x4Oes => 1,
        InternalFormat.CompressedRgbaAstc5x4x4Oes => 1,
        InternalFormat.CompressedRgbaAstc5x5x4Oes => 1,
        InternalFormat.CompressedRgbaAstc5x5x5Oes => 1,
        InternalFormat.CompressedRgbaAstc6x5x5Oes => 1,
        InternalFormat.CompressedRgbaAstc6x6x5Oes => 1,
        InternalFormat.CompressedRgbaAstc6x6x6Oes => 1,

        // sRGB ASTC formats
        InternalFormat.CompressedSrgb8Alpha8Astc4x4 => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc5x4 => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc5x5 => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc6x5 => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc6x6 => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc8x5 => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc8x6 => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc8x8 => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc10x5 => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc10x6 => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc10x8 => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc10x10 => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc12x10 => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc12x12 => 1,

        // 3D sRGB ASTC formats
        InternalFormat.CompressedSrgb8Alpha8Astc3x3x3Oes => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc4x3x3Oes => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc4x4x3Oes => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc4x4x4Oes => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc5x4x4Oes => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc5x5x4Oes => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc5x5x5Oes => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc6x5x5Oes => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc6x6x5Oes => 1,
        InternalFormat.CompressedSrgb8Alpha8Astc6x6x6Oes => 1,

        // Extension formats
        InternalFormat.Alpha4Ext => 1,
        InternalFormat.Alpha8Ext => 1,
        InternalFormat.Alpha12Ext => 2,
        InternalFormat.Alpha16Ext => 2,
        InternalFormat.Luminance4Ext => 1,
        InternalFormat.Luminance8Ext => 1,
        InternalFormat.Luminance12Ext => 2,
        InternalFormat.Luminance16Ext => 2,
        InternalFormat.Luminance4Alpha4Ext => 1,
        InternalFormat.Luminance6Alpha2Ext => 1,
        InternalFormat.Luminance8Alpha8Ext => 2,
        InternalFormat.Luminance12Alpha4Ext => 2,
        InternalFormat.Luminance12Alpha12Ext => 3,
        InternalFormat.Luminance16Alpha16Ext => 4,
        InternalFormat.Intensity4Ext => 1,
        InternalFormat.Intensity8Ext => 1,
        InternalFormat.Intensity12Ext => 2,
        InternalFormat.Intensity16Ext => 2,
        InternalFormat.Rgb2Ext => 1,
        InternalFormat.Rgb4 => 2,
        InternalFormat.Rgb5 => 2,
        InternalFormat.Rgb10 => 4,
        InternalFormat.Rgb12 => 5, // 36-bit, packed as 5 bytes
        InternalFormat.Rgba2 => 1,
        InternalFormat.Rgba12 => 6, // 48-bit, packed as 6 bytes

        // Dual formats
        InternalFormat.DualAlpha4Sgis => 1,
        InternalFormat.DualAlpha8Sgis => 1,
        InternalFormat.DualAlpha12Sgis => 2,
        InternalFormat.DualAlpha16Sgis => 2,
        InternalFormat.DualLuminance4Sgis => 1,
        InternalFormat.DualLuminance8Sgis => 1,
        InternalFormat.DualLuminance12Sgis => 2,
        InternalFormat.DualLuminance16Sgis => 2,
        InternalFormat.DualIntensity4Sgis => 1,
        InternalFormat.DualIntensity8Sgis => 1,
        InternalFormat.DualIntensity12Sgis => 2,
        InternalFormat.DualIntensity16Sgis => 2,
        InternalFormat.DualLuminanceAlpha4Sgis => 1,
        InternalFormat.DualLuminanceAlpha8Sgis => 2,
        InternalFormat.QuadAlpha4Sgis => 2,
        InternalFormat.QuadAlpha8Sgis => 4,
        InternalFormat.QuadLuminance4Sgis => 2,
        InternalFormat.QuadLuminance8Sgis => 4,
        InternalFormat.QuadIntensity4Sgis => 2,
        InternalFormat.QuadIntensity8Sgis => 4,

        // Ext formats
        InternalFormat.Alpha32uiExt => 4,
        InternalFormat.Intensity32uiExt => 4,
        InternalFormat.Luminance32uiExt => 4,
        InternalFormat.LuminanceAlpha32uiExt => 8,
        InternalFormat.Alpha16uiExt => 2,
        InternalFormat.Intensity16uiExt => 2,
        InternalFormat.Luminance16uiExt => 2,
        InternalFormat.LuminanceAlpha16uiExt => 4,
        InternalFormat.Alpha8uiExt => 1,
        InternalFormat.Intensity8uiExt => 1,
        InternalFormat.Luminance8uiExt => 1,
        InternalFormat.LuminanceAlpha8uiExt => 2,
        InternalFormat.Alpha32iExt => 4,
        InternalFormat.Intensity32iExt => 4,
        InternalFormat.Luminance32iExt => 4,
        InternalFormat.LuminanceAlpha32iExt => 8,
        InternalFormat.Alpha16iExt => 2,
        InternalFormat.Intensity16iExt => 2,
        InternalFormat.Luminance16iExt => 2,
        InternalFormat.LuminanceAlpha16iExt => 4,
        InternalFormat.Alpha8iExt => 1,
        InternalFormat.Intensity8iExt => 1,
        InternalFormat.Luminance8iExt => 1,
        InternalFormat.LuminanceAlpha8iExt => 2,
        InternalFormat.DepthComponent32fNV => 4,
        InternalFormat.Depth32fStencil8NV => 5,

        // SR formats
        InternalFormat.SR8Ext => 1,
        InternalFormat.Srg8Ext => 2,

        // Default for unknown formats - conservative 4 bytes
        _ => 4
    };
}
