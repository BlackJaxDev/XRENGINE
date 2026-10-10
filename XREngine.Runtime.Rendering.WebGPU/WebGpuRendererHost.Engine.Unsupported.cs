using System.Numerics;
using XREngine.Imaging;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    public override bool CalcDotLuminance(XRTexture2D texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow)
        => throw UnsupportedEngineOperation(nameof(CalcDotLuminance));

    public override bool CalcDotLuminance(XRTexture2DArray texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow)
        => throw UnsupportedEngineOperation(nameof(CalcDotLuminance));

    public override void SetReadBuffer(EReadBufferMode mode)
        => throw UnsupportedEngineOperation(nameof(SetReadBuffer));

    public override void SetReadBuffer(XRFrameBuffer? fbo, EReadBufferMode mode)
        => throw UnsupportedEngineOperation(nameof(SetReadBuffer));

    public override float GetDepth(int x, int y)
        => throw UnsupportedEngineOperation(nameof(GetDepth));


    public override byte GetStencilIndex(float x, float y)
        => throw UnsupportedEngineOperation(nameof(GetStencilIndex));

    public override void StencilMask(uint mask)
        => throw UnsupportedEngineOperation(nameof(StencilMask));

    public override void EnableStencilTest(bool enable)
        => throw UnsupportedEngineOperation(nameof(EnableStencilTest));

    public override void StencilFunc(EComparison function, int reference, uint mask)
        => throw UnsupportedEngineOperation(nameof(StencilFunc));

    public override void StencilOp(EStencilOp sfail, EStencilOp dpfail, EStencilOp dppass)
        => throw UnsupportedEngineOperation(nameof(StencilOp));

    public override void EnableSampleShading(float minValue)
        => throw UnsupportedEngineOperation(nameof(EnableSampleShading));

    public override void DisableSampleShading()
        => throw UnsupportedEngineOperation(nameof(DisableSampleShading));

    public override bool TryReadTextureMipRgbaFloat(
        XRTexture texture,
        int mipLevel,
        int layerIndex,
        out float[]? rgbaFloats,
        out int width,
        out int height,
        out string failure)
        => throw UnsupportedEngineOperation(nameof(TryReadTextureMipRgbaFloat));

    public override bool TryReadTexturePixelRgbaFloat(
        XRTexture texture,
        int mipLevel,
        int layerIndex,
        out Vector4 rgba,
        out string failure)
        => throw UnsupportedEngineOperation(nameof(TryReadTexturePixelRgbaFloat));

}
