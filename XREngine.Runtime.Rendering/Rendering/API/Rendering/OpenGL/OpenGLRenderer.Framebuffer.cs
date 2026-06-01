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
    public override void DispatchCompute(XRRenderProgram program, int numGroupsX, int numGroupsY, int numGroupsZ)
    {
        GLRenderProgram? glProgram = GenericToAPI<GLRenderProgram>(program);
        if (glProgram is null)
            return;

        if (!glProgram.Use())
            return;

        Api.DispatchCompute((uint)numGroupsX, (uint)numGroupsY, (uint)numGroupsZ);
    }

    public override void AllowDepthWrite(bool allow)
    {
        Api.DepthMask(allow);
    }
    public override void BindFrameBuffer(EFramebufferTarget fboTarget, XRFrameBuffer? fbo)
    {
        Api.BindFramebuffer(GLObjectBase.ToGLEnum(fboTarget), GenericToAPI<GLFrameBuffer>(fbo)?.BindingId ?? 0u);
    }
    public override void Clear(bool color, bool depth, bool stencil)
    {
        uint mask = 0;
        if (color)
            mask |= (uint)GLEnum.ColorBufferBit;
        if (depth)
            mask |= (uint)GLEnum.DepthBufferBit;
        if (stencil)
            mask |= (uint)GLEnum.StencilBufferBit;
        if (mask == 0)
            return;

        // Some drivers emit KHR_debug spam when GL_BLEND is enabled and the currently bound
        // framebuffer has integer color attachments. Blending does not affect glClear, so
        // we temporarily disable it for the clear and restore the previous enable state.
        bool blendWasEnabled = color && Api.IsEnabled(EnableCap.Blend);
        if (blendWasEnabled)
            Api.Disable(EnableCap.Blend);

        Api.Clear(mask);

        if (blendWasEnabled)
            Api.Enable(EnableCap.Blend);
    }

    public override void ClearColor(ColorF4 color)
    {
        Api.ClearColor(color.R, color.G, color.B, color.A);
    }
    public override void ClearDepth(float depth)
    {
        Api.ClearDepth(depth);
    }
    public override void ClearStencil(int stencil)
    {
        Api.ClearStencil(stencil);
    }
    public override void StencilMask(uint v)
    {
        Api.StencilMask(v);
    }

    public override void EnableStencilTest(bool enable)
    {
        if (enable)
            Api.Enable(EnableCap.StencilTest);
        else
            Api.Disable(EnableCap.StencilTest);
    }

    public override void StencilFunc(EComparison function, int reference, uint mask)
    {
        Api.StencilFunc(StencilFunction.Never + (int)function, reference, mask);
    }

    public override void StencilOp(EStencilOp sfail, EStencilOp dpfail, EStencilOp dppass)
    {
        Api.StencilOp(
            (Silk.NET.OpenGL.StencilOp)(int)sfail,
            (Silk.NET.OpenGL.StencilOp)(int)dpfail,
            (Silk.NET.OpenGL.StencilOp)(int)dppass);
    }

    public override void EnableBlend(bool enable)
    {
        if (enable)
            Api.Enable(EnableCap.Blend);
        else
            Api.Disable(EnableCap.Blend);
    }

    public override void BlendFunc(EBlendingFactor src, EBlendingFactor dst)
    {
        Api.BlendFunc(ToGLEnum(src), ToGLEnum(dst));
    }

    public override void BlendFuncSeparate(EBlendingFactor srcRGB, EBlendingFactor dstRGB, EBlendingFactor srcAlpha, EBlendingFactor dstAlpha)
    {
        Api.BlendFuncSeparate(ToGLEnum(srcRGB), ToGLEnum(dstRGB), ToGLEnum(srcAlpha), ToGLEnum(dstAlpha));
    }

    public override void BlendEquation(EBlendEquationMode mode)
    {
        Api.BlendEquation(ToGLEnum(mode));
    }

    public override void BlendEquationSeparate(EBlendEquationMode modeRGB, EBlendEquationMode modeAlpha)
    {
        Api.BlendEquationSeparate(ToGLEnum(modeRGB), ToGLEnum(modeAlpha));
    }

    public override void EnableSampleShading(float minValue)
    {
        Api.Enable(EnableCap.SampleShading);
        Api.MinSampleShading(minValue);
    }
    public override void DisableSampleShading()
    {
        Api.Disable(EnableCap.SampleShading);
    }
    public override void DepthFunc(EComparison comparison)
    {
        var comp = comparison switch
        {
            EComparison.Never => GLEnum.Never,
            EComparison.Less => GLEnum.Less,
            EComparison.Equal => GLEnum.Equal,
            EComparison.Lequal => GLEnum.Lequal,
            EComparison.Greater => GLEnum.Greater,
            EComparison.Nequal => GLEnum.Notequal,
            EComparison.Gequal => GLEnum.Gequal,
            EComparison.Always => GLEnum.Always,
            _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, null),
        };
        Api.DepthFunc(comp);
    }
    public override void EnableDepthTest(bool enable)
    {
        if (enable)
            Api.Enable(EnableCap.DepthTest);
        else
            Api.Disable(EnableCap.DepthTest);
    }
    public override unsafe byte GetStencilIndex(float x, float y)
    {
        byte stencil = 0;
        Api.ReadPixels((int)x, (int)y, 1, 1, PixelFormat.StencilIndex, PixelType.UnsignedByte, &stencil);
        return stencil;
    }
    public override void SetReadBuffer(EReadBufferMode mode)
    {
        Api.ReadBuffer(ToGLEnum(mode));
    }
    public override void SetReadBuffer(XRFrameBuffer? fbo, EReadBufferMode mode)
    {
        Api.NamedFramebufferReadBuffer(GenericToAPI<GLFrameBuffer>(fbo)?.BindingId ?? 0, ToGLEnum(mode));
    }

    private static GLEnum ToGLEnum(EReadBufferMode mode)
    {
        return mode switch
        {
            EReadBufferMode.None => GLEnum.None,
            EReadBufferMode.Front => GLEnum.Front,
            EReadBufferMode.Back => GLEnum.Back,
            EReadBufferMode.Left => GLEnum.Left,
            EReadBufferMode.Right => GLEnum.Right,
            EReadBufferMode.FrontLeft => GLEnum.FrontLeft,
            EReadBufferMode.FrontRight => GLEnum.FrontRight,
            EReadBufferMode.BackLeft => GLEnum.BackLeft,
            EReadBufferMode.BackRight => GLEnum.BackRight,
            EReadBufferMode.ColorAttachment0 => GLEnum.ColorAttachment0,
            EReadBufferMode.ColorAttachment1 => GLEnum.ColorAttachment1,
            EReadBufferMode.ColorAttachment2 => GLEnum.ColorAttachment2,
            EReadBufferMode.ColorAttachment3 => GLEnum.ColorAttachment3,
            EReadBufferMode.ColorAttachment4 => GLEnum.ColorAttachment4,
            EReadBufferMode.ColorAttachment5 => GLEnum.ColorAttachment5,
            EReadBufferMode.ColorAttachment6 => GLEnum.ColorAttachment6,
            EReadBufferMode.ColorAttachment7 => GLEnum.ColorAttachment7,
            EReadBufferMode.ColorAttachment8 => GLEnum.ColorAttachment8,
            EReadBufferMode.ColorAttachment9 => GLEnum.ColorAttachment9,
            EReadBufferMode.ColorAttachment10 => GLEnum.ColorAttachment10,
            EReadBufferMode.ColorAttachment11 => GLEnum.ColorAttachment11,
            EReadBufferMode.ColorAttachment12 => GLEnum.ColorAttachment12,
            EReadBufferMode.ColorAttachment13 => GLEnum.ColorAttachment13,
            EReadBufferMode.ColorAttachment14 => GLEnum.ColorAttachment14,
            EReadBufferMode.ColorAttachment15 => GLEnum.ColorAttachment15,
            EReadBufferMode.ColorAttachment16 => GLEnum.ColorAttachment16,
            EReadBufferMode.ColorAttachment17 => GLEnum.ColorAttachment17,
            EReadBufferMode.ColorAttachment18 => GLEnum.ColorAttachment18,
            EReadBufferMode.ColorAttachment19 => GLEnum.ColorAttachment19,
            EReadBufferMode.ColorAttachment20 => GLEnum.ColorAttachment20,
            EReadBufferMode.ColorAttachment21 => GLEnum.ColorAttachment21,
            EReadBufferMode.ColorAttachment22 => GLEnum.ColorAttachment22,
            EReadBufferMode.ColorAttachment23 => GLEnum.ColorAttachment23,
            EReadBufferMode.ColorAttachment24 => GLEnum.ColorAttachment24,
            EReadBufferMode.ColorAttachment25 => GLEnum.ColorAttachment25,
            EReadBufferMode.ColorAttachment26 => GLEnum.ColorAttachment26,
            EReadBufferMode.ColorAttachment27 => GLEnum.ColorAttachment27,
            EReadBufferMode.ColorAttachment28 => GLEnum.ColorAttachment28,
            EReadBufferMode.ColorAttachment29 => GLEnum.ColorAttachment29,
            EReadBufferMode.ColorAttachment30 => GLEnum.ColorAttachment30,
            EReadBufferMode.ColorAttachment31 => GLEnum.ColorAttachment31,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    public override void SetRenderArea(BoundingRectangle region)
        => Api.Viewport(region.X, region.Y, (uint)region.Width, (uint)region.Height);

    public override void CropRenderArea(BoundingRectangle region)
        => Api.Scissor(region.X, region.Y, (uint)region.Width, (uint)region.Height);

    public override bool SetIndexedViewportScissors(
        ReadOnlySpan<BoundingRectangle> viewports,
        ReadOnlySpan<BoundingRectangle> scissors)
    {
        int count = Math.Min(viewports.Length, scissors.Length);
        if (count <= 0 ||
            !RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray ||
            count > RuntimeEngine.Rendering.State.MaxOpenGLViewports)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            BoundingRectangle viewport = viewports[i];
            BoundingRectangle scissor = scissors[i];
            Api.ViewportIndexed(
                (uint)i,
                viewport.X,
                viewport.Y,
                viewport.Width,
                viewport.Height);
            Api.ScissorIndexed(
                (uint)i,
                scissor.X,
                scissor.Y,
                (uint)scissor.Width,
                (uint)scissor.Height);
        }

        return true;
    }

    public override void ClearIndexedViewportScissors(int count)
    {
        if (count <= 1)
            return;

        BoundingRectangle region = RuntimeEngine.Rendering.State.RenderArea;
        if (region.Width <= 0 || region.Height <= 0)
            region = new BoundingRectangle(0, 0, Window.Size.X, Window.Size.Y);

        for (int i = 1; i < Math.Min(count, RuntimeEngine.Rendering.State.MaxOpenGLViewports); i++)
        {
            Api.ViewportIndexed((uint)i, region.X, region.Y, region.Width, region.Height);
            Api.ScissorIndexed((uint)i, region.X, region.Y, (uint)region.Width, (uint)region.Height);
        }
    }

    public override void SetCroppingEnabled(bool enabled)
    {
        if (enabled)
            Api.Enable(EnableCap.ScissorTest);
        else
            Api.Disable(EnableCap.ScissorTest);
    }

    public void CheckFrameBufferErrors(GLFrameBuffer fbo)
    {
        var result = Api.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        bool complete = result == GLEnum.FramebufferComplete;
        fbo.Data.IsLastCheckComplete = complete;
        if (!complete)
        {
            string debug = GetFBODebugInfo(fbo, Environment.NewLine);
            string name = fbo.GetDescribingName();
            string details = string.Empty;
            if (TryGetOneTimeFBODetailDump(fbo, out var dump))
                details = dump;

            Debug.OpenGLWarning($"FBO {name} is not complete. Status: {result}{debug}{details}");
        }
    }

    private readonly HashSet<uint> _fboDetailedDumped = new();
    private bool TryGetOneTimeFBODetailDump(GLFrameBuffer fbo, out string dump)
    {
        dump = string.Empty;
        if (!fbo.TryGetBindingId(out uint fboId) || fboId == 0)
            return false;

        // Avoid log spam: only dump details once per FBO binding id.
        if (_fboDetailedDumped.Contains(fboId))
            return false;
        _fboDetailedDumped.Add(fboId);

        dump = GetFBODetailDump(fbo, Environment.NewLine);
        return !string.IsNullOrWhiteSpace(dump);
    }

    private string GetFBODetailDump(GLFrameBuffer fbo, string splitter)
    {
        try
        {
            if (!fbo.TryGetBindingId(out uint fboId) || fboId == 0)
                return string.Empty;

            if (fbo.Data.Targets is null || fbo.Data.Targets.Length == 0)
                return $"{splitter}[FBO Detail] No targets.";

            string s = $"{splitter}[FBO Detail] NamedFramebuffer={fboId}";

            foreach (var (Target, Attachment, MipLevel, LayerIndex) in fbo.Data.Targets)
            {
                var glAttachment = (GLEnum)ToGLEnum(Attachment);

                Api.GetNamedFramebufferAttachmentParameter(fboId, glAttachment, GLEnum.FramebufferAttachmentObjectType, out int objType);
                Api.GetNamedFramebufferAttachmentParameter(fboId, glAttachment, GLEnum.FramebufferAttachmentObjectName, out int objName);
                GLEnum glObjType = (GLEnum)objType;
                int attachedLevel = 0;
                int attachedLayer = 0;
                if (glObjType == GLEnum.Texture)
                {
                    Api.GetNamedFramebufferAttachmentParameter(fboId, glAttachment, GLEnum.FramebufferAttachmentTextureLevel, out attachedLevel);
                    Api.GetNamedFramebufferAttachmentParameter(fboId, glAttachment, GLEnum.FramebufferAttachmentTextureLayer, out attachedLayer);
                }

                s += glObjType == GLEnum.Texture
                    ? $"{splitter}[FBO Detail] {Attachment}: GL objType={glObjType} objName={objName} texLevel={attachedLevel} texLayer={attachedLayer}"
                    : $"{splitter}[FBO Detail] {Attachment}: GL objType={glObjType} objName={objName}";

                // If we can resolve the GL texture wrapper, also dump texture params/level info.
                if (Target is XRTexture xrTex)
                {
                    IGLTexture? glTex = xrTex switch
                    {
                        XRTexture1D t => GenericToAPI<GLTexture1D>(t),
                        XRTexture1DArray t => GenericToAPI<GLTexture1DArray>(t),
                        XRTexture2D t => GenericToAPI<GLTexture2D>(t),
                        XRTexture2DArray t => GenericToAPI<GLTexture2DArray>(t),
                        XRTextureRectangle t => GenericToAPI<GLTextureRectangle>(t),
                        XRTexture3D t => GenericToAPI<GLTexture3D>(t),
                        XRTextureCube t => GenericToAPI<GLTextureCube>(t),
                        XRTextureCubeArray t => GenericToAPI<GLTextureCubeArray>(t),
                        XRTextureBuffer t => GenericToAPI<GLTextureBuffer>(t),
                        XRTextureViewBase t => GenericToAPI<GLTextureView>(t),
                        _ => null
                    };

                    uint texId = 0;
                    if (glTex is GLObjectBase glObj && glObj.TryGetBindingId(out texId) && texId != 0)
                    {
                        bool isTex = Api.IsTexture(texId);
                        s += $"{splitter}[FBO Detail]   XR={xrTex.GetDescribingName()} -> GL texId={texId} glIsTexture={isTex} xrResizable={xrTex.IsResizeable} xrWHD={xrTex.WidthHeightDepth}";

                        if (!isTex)
                        {
                            s += $"{splitter}[FBO Detail]   GL texture object is invalid; skipping parameter queries.";
                        }
                        else
                        {
                            // DSA queries (works regardless of current binding).
                            Api.GetTextureParameter(texId, GLEnum.TextureBaseLevel, out int baseLevel);
                            Api.GetTextureParameter(texId, GLEnum.TextureMaxLevel, out int maxLevel);
                            Api.GetTextureParameter(texId, GLEnum.TextureImmutableFormat, out int immutable);
                            Api.GetTextureLevelParameter(texId, attachedLevel, GLEnum.TextureWidth, out int levelW);
                            Api.GetTextureLevelParameter(texId, attachedLevel, GLEnum.TextureHeight, out int levelH);
                            Api.GetTextureLevelParameter(texId, attachedLevel, GLEnum.TextureInternalFormat, out int levelInternal);

                            s += $"{splitter}[FBO Detail]   GL baseLevel={baseLevel} maxLevel={maxLevel} immutable={(immutable != 0)} levelW={levelW} levelH={levelH} levelInternal=0x{levelInternal:X}";
                        }
                    }
                    else
                    {
                        s += $"{splitter}[FBO Detail]   XR={xrTex.GetDescribingName()} -> (no GL texture wrapper id)";
                    }
                }
            }

            // Also scan actual GL attachment points in case something is still attached but not represented
            // in `Data.Targets` (stale color attachments are a common cause of INCOMPLETE_ATTACHMENT).
            static bool IsInterestingAttachment(GLEnum att)
                => att == GLEnum.DepthAttachment || att == GLEnum.StencilAttachment ||
                   att == GLEnum.ColorAttachment0 || att == GLEnum.ColorAttachment1 || att == GLEnum.ColorAttachment2 || att == GLEnum.ColorAttachment3 ||
                   att == GLEnum.ColorAttachment4 || att == GLEnum.ColorAttachment5 || att == GLEnum.ColorAttachment6 || att == GLEnum.ColorAttachment7;

            s += $"{splitter}[FBO Detail] Attached objects (GL query):";
            foreach (var att in new[]
            {
                GLEnum.DepthAttachment,
                GLEnum.StencilAttachment,
                GLEnum.ColorAttachment0,
                GLEnum.ColorAttachment1,
                GLEnum.ColorAttachment2,
                GLEnum.ColorAttachment3,
                GLEnum.ColorAttachment4,
                GLEnum.ColorAttachment5,
                GLEnum.ColorAttachment6,
                GLEnum.ColorAttachment7,
            })
            {
                if (!IsInterestingAttachment(att))
                    continue;

                Api.GetNamedFramebufferAttachmentParameter(fboId, att, GLEnum.FramebufferAttachmentObjectType, out int objType);
                Api.GetNamedFramebufferAttachmentParameter(fboId, att, GLEnum.FramebufferAttachmentObjectName, out int objName);
                GLEnum glObjType = (GLEnum)objType;
                if (glObjType == GLEnum.None || objName == 0)
                    continue;

                if (glObjType == GLEnum.Texture)
                {
                    Api.GetNamedFramebufferAttachmentParameter(fboId, att, GLEnum.FramebufferAttachmentTextureLevel, out int level);
                    Api.GetNamedFramebufferAttachmentParameter(fboId, att, GLEnum.FramebufferAttachmentTextureLayer, out int layer);
                    s += $"{splitter}[FBO Detail]   {att}: objType={glObjType} objName={objName} texLevel={level} texLayer={layer}";
                }
                else
                {
                    s += $"{splitter}[FBO Detail]   {att}: objType={glObjType} objName={objName}";
                }
            }

            return s;
        }
        catch (Exception ex)
        {
            return $"{splitter}[FBO Detail] Failed to query details: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private GLEnum ToGLEnum(EFrameBufferAttachment attachment)
    {
        return attachment switch
        {
            EFrameBufferAttachment.ColorAttachment0 => GLEnum.ColorAttachment0,
            EFrameBufferAttachment.ColorAttachment1 => GLEnum.ColorAttachment1,
            EFrameBufferAttachment.ColorAttachment2 => GLEnum.ColorAttachment2,
            EFrameBufferAttachment.ColorAttachment3 => GLEnum.ColorAttachment3,
            EFrameBufferAttachment.ColorAttachment4 => GLEnum.ColorAttachment4,
            EFrameBufferAttachment.ColorAttachment5 => GLEnum.ColorAttachment5,
            EFrameBufferAttachment.ColorAttachment6 => GLEnum.ColorAttachment6,
            EFrameBufferAttachment.ColorAttachment7 => GLEnum.ColorAttachment7,
            EFrameBufferAttachment.ColorAttachment8 => GLEnum.ColorAttachment8,
            EFrameBufferAttachment.ColorAttachment9 => GLEnum.ColorAttachment9,
            EFrameBufferAttachment.ColorAttachment10 => GLEnum.ColorAttachment10,
            EFrameBufferAttachment.ColorAttachment11 => GLEnum.ColorAttachment11,
            EFrameBufferAttachment.ColorAttachment12 => GLEnum.ColorAttachment12,
            EFrameBufferAttachment.ColorAttachment13 => GLEnum.ColorAttachment13,
            EFrameBufferAttachment.ColorAttachment14 => GLEnum.ColorAttachment14,
            EFrameBufferAttachment.ColorAttachment15 => GLEnum.ColorAttachment15,
            EFrameBufferAttachment.DepthAttachment => GLEnum.DepthAttachment,
            EFrameBufferAttachment.StencilAttachment => GLEnum.StencilAttachment,
            EFrameBufferAttachment.DepthStencilAttachment => GLEnum.DepthStencilAttachment,
            _ => throw new ArgumentOutOfRangeException(nameof(attachment), attachment, null),
        };
    }

    private static string GetFBODebugInfo(GLFrameBuffer fbo, string splitter)
    {
        string debug = string.Empty;
        if (fbo.Data.Targets is null || fbo.Data.Targets.Length == 0)
        {
            debug += $"{splitter}This FBO has no targets.";
            return debug;
        }

        foreach (var (Target, Attachment, MipLevel, LayerIndex) in fbo.Data.Targets)
        {
            GenericRenderObject? gro = Target as GenericRenderObject;
            bool targetExists = gro is not null;
            string texName = targetExists ? gro!.GetDescribingName() : "<null>";
            debug += $"{splitter}{Attachment}: {texName} Mip{MipLevel}";
            if (LayerIndex >= 0)
                debug += $" Layer{LayerIndex}";
            if (targetExists)
                debug += $" / {GetTargetDebugInfo(gro!)}";
        }
        return debug;
    }

    private static string GetTargetDebugInfo(GenericRenderObject gro)
    {
        string debug = string.Empty;
        switch (gro)
        {
            case XRTexture2DView t2dv:
                debug += $"{t2dv.ViewedTexture.Width}x{t2dv.ViewedTexture.Height} | Viewing {t2dv.ViewedTexture.Name} | internal:{t2dv.InternalFormat}{FormatMipLevels(t2dv.ViewedTexture)}";
                break;
            case XRTexture2D t2d:
                debug += $"{t2d.Width}x{t2d.Height}{FormatMipLevels(t2d)}";
                break;
            case XRRenderBuffer rb:
                debug += $"{rb.Width}x{rb.Height} | {rb.Type}";
                break;
            case XRTextureCube tc:
                debug += $"{tc.MaxDimension}x{tc.MaxDimension}x{tc.MaxDimension}{FormatMipLevels(tc)}";
                break;
        }
        return debug;
    }

    private static string FormatMipLevels(XRTextureCube tc)
    {
        switch (tc.Mipmaps.Length)
        {
            case 0:
                return " | No mipmaps";
            case 1:
                return $" | {FormatMipmap(0, tc.Mipmaps)}";
            default:
                string mipmaps = $" | {tc.Mipmaps.Length} mipmaps";
                for (int i = 0; i < tc.Mipmaps.Length; i++)
                    mipmaps += $"{Environment.NewLine}{FormatMipmap(i, tc.Mipmaps)}";
                return mipmaps;
        }
    }

    private static string FormatMipLevels(XRTexture2D t2d)
    {
        switch (t2d.Mipmaps.Length)
        {
            case 0:
                return " | No mipmaps";
            case 1:
                return $" | {FormatMipmap(0, t2d.Mipmaps)}";
            default:
                string mipmaps = $" | {t2d.Mipmaps.Length} mipmaps";
                for (int i = 0; i < t2d.Mipmaps.Length; i++)
                    mipmaps += $"{Environment.NewLine}{FormatMipmap(i, t2d.Mipmaps)}";
                return mipmaps;
        }
    }

    private static string FormatMipmap(int i, CubeMipmap[] mipmaps)
    {
        if (i >= mipmaps.Length)
            return string.Empty;

        CubeMipmap m = mipmaps[i];
        //Format all sides
        string sides = string.Empty;
        for (int j = 0; j < m.Sides.Length; j++)
        {
            Mipmap2D side = m.Sides[j];
            sides += $"{side.Width}x{side.Height} | internal:{side.InternalFormat} | {side.PixelFormat}/{side.PixelType}";
            if (j < m.Sides.Length - 1)
                sides += Environment.NewLine;
        }
        return $"Mip{i} | {sides}";
    }

    private static string FormatMipmap(int i, XREngine.Rendering.Mipmap2D[] mipmaps)
    {
        if (i >= mipmaps.Length)
            return string.Empty;

        var m = mipmaps[i];
        return $"Mip{i} | {m.Width}x{m.Height} | internal:{m.InternalFormat} | {m.PixelFormat}/{m.PixelType}";
    }

    //public void SetMipmapParameters(uint bindingId, int minLOD, int maxLOD, int largestMipmapLevel, int smallestAllowedMipmapLevel)
    //{
    //    Api.TextureParameterI(bindingId, TextureParameterName.TextureBaseLevel, ref largestMipmapLevel);
    //    Api.TextureParameterI(bindingId, TextureParameterName.TextureMaxLevel, ref smallestAllowedMipmapLevel);
    //    Api.TextureParameterI(bindingId, TextureParameterName.TextureMinLod, ref minLOD);
    //    Api.TextureParameterI(bindingId, TextureParameterName.TextureMaxLod, ref maxLOD);
    //}

    //public void SetMipmapParameters(ETextureTarget target, int minLOD, int maxLOD, int largestMipmapLevel, int smallestAllowedMipmapLevel)
    //{
    //    TextureTarget t = ToTextureTarget(target);
    //    Api.TexParameterI(t, TextureParameterName.TextureBaseLevel, ref largestMipmapLevel);
    //    Api.TexParameterI(t, TextureParameterName.TextureMaxLevel, ref smallestAllowedMipmapLevel);
    //    Api.TexParameterI(t, TextureParameterName.TextureMinLod, ref minLOD);
    //    Api.TexParameterI(t, TextureParameterName.TextureMaxLod, ref maxLOD);
    //}

    public unsafe void ClearTexImage(uint bindingId, int level, ColorF4 color)
    {
        void* addr = color.Address;
        Api.ClearTexImage(bindingId, level, GLEnum.Rgba, GLEnum.Float, addr);
    }

    public unsafe void ClearTexImage(uint bindingId, int level, ColorF3 color)
    {
        void* addr = color.Address;
        Api.ClearTexImage(bindingId, level, GLEnum.Rgb, GLEnum.Float, addr);
    }

    public unsafe void ClearTexImage(uint bindingId, int level, RGBAPixel color)
    {
        void* addr = color.Address;
        Api.ClearTexImage(bindingId, level, GLEnum.Rgba, GLEnum.Byte, addr);
    }

    public static TextureTarget ToTextureTarget(ETextureTarget target)
        => target switch
        {
            ETextureTarget.Texture2D => TextureTarget.Texture2D,
            ETextureTarget.Texture3D => TextureTarget.Texture3D,
            ETextureTarget.TextureCubeMap => TextureTarget.TextureCubeMap,
            _ => TextureTarget.Texture2D
        };
}
