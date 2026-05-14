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
    public override void ApplyRenderParameters(RenderingParameters parameters)
    {
        if (parameters is null)
            return;

        //Api.PointSize(r.PointSize);
        //Api.LineWidth(r.LineWidth.Clamp(0.0f, 1.0f));
        Api.ColorMask(parameters.WriteRed, parameters.WriteGreen, parameters.WriteBlue, parameters.WriteAlpha);

        var winding = parameters.Winding;
        if (RuntimeEngine.Rendering.State.ReverseWinding)
            winding = winding == EWinding.Clockwise ? EWinding.CounterClockwise : EWinding.Clockwise;
        Api.FrontFace(ToGLEnum(winding));

        ApplyCulling(parameters);
        ApplyDepth(parameters);
        ApplyBlending(parameters);
        ApplyAlphaToCoverage(parameters);
        ApplyStencil(parameters);
        //Alpha testing is done in-shader
    }

    private GLEnum ToGLEnum(EWinding winding)
        => winding switch
        {
            EWinding.Clockwise => GLEnum.CW,
            EWinding.CounterClockwise => GLEnum.Ccw,
            _ => GLEnum.Ccw
        };

    private void ApplyStencil(RenderingParameters r)
    {
        switch (r.StencilTest.Enabled)
        {
            case ERenderParamUsage.Enabled:
                {
                    Api.Enable(EnableCap.StencilTest);

                    StencilTest st = r.StencilTest;
                    StencilTestFace b = st.BackFace;
                    StencilTestFace f = st.FrontFace;

                    Api.StencilOpSeparate(GLEnum.Back,
                        (StencilOp)(int)b.BothFailOp,
                        (StencilOp)(int)b.StencilPassDepthFailOp,
                        (StencilOp)(int)b.BothPassOp);

                    Api.StencilOpSeparate(GLEnum.Front,
                        (StencilOp)(int)f.BothFailOp,
                        (StencilOp)(int)f.StencilPassDepthFailOp,
                        (StencilOp)(int)f.BothPassOp);

                    Api.StencilMaskSeparate(GLEnum.Back, b.WriteMask);
                    Api.StencilMaskSeparate(GLEnum.Front, f.WriteMask);

                    Api.StencilFuncSeparate(GLEnum.Back,
                        StencilFunction.Never + (int)b.Function, b.Reference, b.ReadMask);
                    Api.StencilFuncSeparate(GLEnum.Front,
                        StencilFunction.Never + (int)f.Function, f.Reference, f.ReadMask);

                    break;
                }

            case ERenderParamUsage.Disabled:
                Api.Disable(EnableCap.StencilTest);
                Api.StencilMask(0);
                Api.StencilOp(GLEnum.Keep, GLEnum.Keep, GLEnum.Keep);
                Api.StencilFunc(StencilFunction.Always, 0, 0);
                break;
        }
    }

    private void ApplyBlending(RenderingParameters r)
    {
        if (r.BlendModeAllDrawBuffers is not null)
        {
            var x = r.BlendModeAllDrawBuffers;
            if (x.Enabled == ERenderParamUsage.Enabled)
            {
                Api.Enable(EnableCap.Blend);

                Api.BlendEquationSeparate(
                    ToGLEnum(x.RgbEquation),
                    ToGLEnum(x.AlphaEquation));

                Api.BlendFuncSeparate(
                    ToGLEnum(x.RgbSrcFactor),
                    ToGLEnum(x.RgbDstFactor),
                    ToGLEnum(x.AlphaSrcFactor),
                    ToGLEnum(x.AlphaDstFactor));
            }
            else if (x.Enabled == ERenderParamUsage.Disabled)
                Api.Disable(EnableCap.Blend);
        }
        else if (r.BlendModesPerDrawBuffer is not null)
        {
            if (r.BlendModesPerDrawBuffer.Any(r => r.Value.Enabled == ERenderParamUsage.Enabled))
            {
                Api.Enable(EnableCap.Blend);
                foreach (KeyValuePair<uint, BlendMode> pair in r.BlendModesPerDrawBuffer)
                {
                    uint drawBuffer = pair.Key;
                    BlendMode x = pair.Value;
                    if (x.Enabled == ERenderParamUsage.Enabled)
                    {
                        Api.BlendEquationSeparate(
                            drawBuffer,
                            ToGLEnum(x.RgbEquation),
                            ToGLEnum(x.AlphaEquation));

                        Api.BlendFuncSeparate(
                            drawBuffer,
                            ToGLEnum(x.RgbSrcFactor),
                            ToGLEnum(x.RgbDstFactor),
                            ToGLEnum(x.AlphaSrcFactor),
                            ToGLEnum(x.AlphaDstFactor));
                    }
                    else
                    {
                        //Apply a blend mode that mimics non-blending for this draw buffer

                        Api.BlendEquationSeparate(
                            drawBuffer,
                            GLEnum.FuncAdd,
                            GLEnum.FuncAdd);

                        Api.BlendFuncSeparate(
                            drawBuffer,
                            GLEnum.One,
                            GLEnum.Zero,
                            GLEnum.One,
                            GLEnum.Zero);
                    }
                }
            }
            else if (r.BlendModesPerDrawBuffer.Count == 0 || r.BlendModesPerDrawBuffer.Any(r => r.Value.Enabled == ERenderParamUsage.Disabled))
                Api.Disable(EnableCap.Blend);
        }
        else
            Api.Disable(EnableCap.Blend);
    }

    private void ApplyAlphaToCoverage(RenderingParameters r)
    {
        bool enabled = r.AlphaToCoverage == ERenderParamUsage.Enabled && IsAlphaToCoverageSupportedForCurrentTarget();
        if (enabled)
            Api.Enable(EnableCap.SampleAlphaToCoverage);
        else
            Api.Disable(EnableCap.SampleAlphaToCoverage);
    }

    private static bool IsAlphaToCoverageSupportedForCurrentTarget()
    {
        XRFrameBuffer? currentDrawFbo = XRFrameBuffer.BoundForWriting;
        if (currentDrawFbo is not null)
            return currentDrawFbo.IsMultisampled;

        XRFrameBuffer? outputFbo = RuntimeEngine.Rendering.State.RenderingTargetOutputFBO;
        if (outputFbo is not null)
            return outputFbo.IsMultisampled;

        // Resolve AA through the current pipeline's latched per-frame state when available.
        var aaMode = XREngine.Rendering.RenderPipeline.ResolveEffectiveAntiAliasingModeForFrame();
        var msaaSamples = XREngine.Rendering.RenderPipeline.ResolveEffectiveMsaaSampleCountForFrame();
        return aaMode == XREngine.EAntiAliasingMode.Msaa
            && msaaSamples > 1u;
    }

    private void ApplyCulling(RenderingParameters r)
    {
        if (r.CullMode == ECullMode.None)
            Api.Disable(EnableCap.CullFace);
        else
        {
            Api.Enable(EnableCap.CullFace);
            var cullMode = r.CullMode;
            if (RuntimeEngine.Rendering.State.ReverseCulling)
                cullMode = cullMode switch
                {
                    ECullMode.Front => ECullMode.Back,
                    ECullMode.Back => ECullMode.Front,
                    _ => cullMode
                };
            Api.CullFace(ToGLEnum(cullMode));
        }
    }

    private void ApplyDepth(RenderingParameters r)
    {
        switch (r.DepthTest.Enabled)
        {
            case ERenderParamUsage.Enabled:
                Api.Enable(EnableCap.DepthTest);
                Api.DepthFunc(ToGLEnum(RuntimeEngine.Rendering.State.MapDepthComparison(r.DepthTest.Function)));
                Api.DepthMask(r.DepthTest.UpdateDepth);
                break;

            case ERenderParamUsage.Disabled:
                Api.Disable(EnableCap.DepthTest);
                break;
        }
    }

    private static GLEnum ToGLEnum(EBlendingFactor factor)
        => factor switch
        {
            EBlendingFactor.Zero => GLEnum.Zero,
            EBlendingFactor.One => GLEnum.One,
            EBlendingFactor.SrcColor => GLEnum.SrcColor,
            EBlendingFactor.OneMinusSrcColor => GLEnum.OneMinusSrcColor,
            EBlendingFactor.DstColor => GLEnum.DstColor,
            EBlendingFactor.OneMinusDstColor => GLEnum.OneMinusDstColor,
            EBlendingFactor.SrcAlpha => GLEnum.SrcAlpha,
            EBlendingFactor.OneMinusSrcAlpha => GLEnum.OneMinusSrcAlpha,
            EBlendingFactor.DstAlpha => GLEnum.DstAlpha,
            EBlendingFactor.OneMinusDstAlpha => GLEnum.OneMinusDstAlpha,
            EBlendingFactor.ConstantColor => GLEnum.ConstantColor,
            EBlendingFactor.OneMinusConstantColor => GLEnum.OneMinusConstantColor,
            EBlendingFactor.ConstantAlpha => GLEnum.ConstantAlpha,
            EBlendingFactor.OneMinusConstantAlpha => GLEnum.OneMinusConstantAlpha,
            EBlendingFactor.SrcAlphaSaturate => GLEnum.SrcAlphaSaturate,
            _ => GLEnum.Zero,
        };

    private static GLEnum ToGLEnum(EBlendEquationMode equation)
        => equation switch
        {
            EBlendEquationMode.FuncAdd => GLEnum.FuncAdd,
            EBlendEquationMode.FuncSubtract => GLEnum.FuncSubtract,
            EBlendEquationMode.FuncReverseSubtract => GLEnum.FuncReverseSubtract,
            EBlendEquationMode.Min => GLEnum.Min,
            EBlendEquationMode.Max => GLEnum.Max,
            _ => GLEnum.FuncAdd,
        };

    private static GLEnum ToGLEnum(EComparison function)
        => function switch
        {
            EComparison.Never => GLEnum.Never,
            EComparison.Less => GLEnum.Less,
            EComparison.Equal => GLEnum.Equal,
            EComparison.Lequal => GLEnum.Lequal,
            EComparison.Greater => GLEnum.Greater,
            EComparison.Nequal => GLEnum.Notequal,
            EComparison.Gequal => GLEnum.Gequal,
            EComparison.Always => GLEnum.Always,
            _ => GLEnum.Never,
        };

    private static GLEnum ToGLEnum(ECullMode cullMode)
        => cullMode switch
        {
            ECullMode.Front => GLEnum.Front,
            ECullMode.Back => GLEnum.Back,
            _ => GLEnum.FrontAndBack,
        };

    private static GLEnum ToGLEnum(IndexSize elementType)
        => elementType switch
        {
            IndexSize.Byte => GLEnum.UnsignedByte,
            IndexSize.TwoBytes => GLEnum.UnsignedShort,
            IndexSize.FourBytes => GLEnum.UnsignedInt,
            _ => GLEnum.UnsignedInt,
        };

    private static GLEnum ToGLEnum(EPrimitiveType type)
        => type switch
        {
            EPrimitiveType.Points => GLEnum.Points,
            EPrimitiveType.Lines => GLEnum.Lines,
            EPrimitiveType.LineLoop => GLEnum.LineLoop,
            EPrimitiveType.LineStrip => GLEnum.LineStrip,
            EPrimitiveType.Triangles => GLEnum.Triangles,
            EPrimitiveType.TriangleStrip => GLEnum.TriangleStrip,
            EPrimitiveType.TriangleFan => GLEnum.TriangleFan,
            EPrimitiveType.LinesAdjacency => GLEnum.LinesAdjacency,
            EPrimitiveType.LineStripAdjacency => GLEnum.LineStripAdjacency,
            EPrimitiveType.TrianglesAdjacency => GLEnum.TrianglesAdjacency,
            EPrimitiveType.TriangleStripAdjacency => GLEnum.TriangleStripAdjacency,
            EPrimitiveType.Patches => GLEnum.Patches,
            _ => GLEnum.Triangles,
        };

    public int GetInteger(GLEnum value)
        => Api.GetInteger(value);

    public unsafe bool IsExtensionSupported(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        //Check if the extension is already loaded
        if (Api.IsExtensionPresent(name))
            return true;

        //Check if the extension is supported by the OpenGL context
        byte* extensions = Api.GetString(GLEnum.Extensions);
        if (extensions is null)
            return false;

        //Split the extensions string into individual extensions
        string str = new((sbyte*)extensions);
        string[] extList = str.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        //Check if the requested extension is in the list
        foreach (string ext in extList)
            if (ext.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;

        //If we reach here, the extension is not supported
        return false;
    }
}
