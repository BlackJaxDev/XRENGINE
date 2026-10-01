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

    public override void CalcDotLuminanceAsync(XRTexture2D texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true)
        => throw UnsupportedEngineOperation(nameof(CalcDotLuminanceAsync));

    public override void CalcDotLuminanceAsync(XRTexture2DArray texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true)
        => throw UnsupportedEngineOperation(nameof(CalcDotLuminanceAsync));

    public override void CalcDotLuminanceFrontAsync(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
        => throw UnsupportedEngineOperation(nameof(CalcDotLuminanceFrontAsync));

    public override void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
        => throw UnsupportedEngineOperation(nameof(CalcDotLuminanceFrontAsyncCompute));

    public override void SetReadBuffer(EReadBufferMode mode)
        => throw UnsupportedEngineOperation(nameof(SetReadBuffer));

    public override void SetReadBuffer(XRFrameBuffer? fbo, EReadBufferMode mode)
        => throw UnsupportedEngineOperation(nameof(SetReadBuffer));

    public override float GetDepth(int x, int y)
        => throw UnsupportedEngineOperation(nameof(GetDepth));

    public override void GetPixelAsync(int x, int y, bool withTransparency, Action<ColorF4> colorCallback)
        => throw UnsupportedEngineOperation(nameof(GetPixelAsync));

    public override void GetDepthAsync(XRFrameBuffer fbo, int x, int y, Action<float> depthCallback)
        => throw UnsupportedEngineOperation(nameof(GetDepthAsync));

    public override byte GetStencilIndex(float x, float y)
        => throw UnsupportedEngineOperation(nameof(GetStencilIndex));

    public override void StencilMask(uint mask)
        => throw UnsupportedEngineOperation(nameof(StencilMask));

    public override void ClearStencil(int value)
        => throw UnsupportedEngineOperation(nameof(ClearStencil));

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

    public override void DispatchCompute(XRRenderProgram program, int numGroupsX, int numGroupsY, int numGroupsZ)
        => throw UnsupportedEngineOperation(nameof(DispatchCompute));

    public override void GetScreenshotAsync(BoundingRectangle region, bool withTransparency, Action<RuntimeImage, int> imageCallback)
        => throw UnsupportedEngineOperation(nameof(GetScreenshotAsync));

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

    public override void Blit(
        XRFrameBuffer? inFBO,
        XRFrameBuffer? outFBO,
        int inX, int inY, uint inW, uint inH,
        int outX, int outY, uint outW, uint outH,
        EReadBufferMode readBufferMode,
        bool colorBit, bool depthBit, bool stencilBit,
        bool linearFilter)
        => throw UnsupportedEngineOperation(nameof(Blit));

    public override void BlitWithDrawBuffer(
        XRFrameBuffer? inFBO,
        XRFrameBuffer? outFBO,
        uint inW, uint inH,
        uint outW, uint outH,
        EReadBufferMode readBufferMode,
        EReadBufferMode drawBufferMode,
        bool colorBit, bool depthBit, bool stencilBit,
        bool linearFilter)
        => throw UnsupportedEngineOperation(nameof(BlitWithDrawBuffer));

    public override void MemoryBarrier(EMemoryBarrierMask mask)
        => throw UnsupportedEngineOperation(nameof(MemoryBarrier));

    public override void BindVAOForRenderer(XRMeshRenderer.BaseVersion? version)
        => throw UnsupportedEngineOperation(nameof(BindVAOForRenderer));

    public override bool ValidateIndexedVAO(XRMeshRenderer.BaseVersion? version)
        => throw UnsupportedEngineOperation(nameof(ValidateIndexedVAO));

    public override bool TryGetIndexBufferInfo(XRMeshRenderer.BaseVersion? version, out IndexSize indexElementSize, out uint indexCount)
        => throw UnsupportedEngineOperation(nameof(TryGetIndexBufferInfo));

    public override bool TrySyncMeshRendererIndexBuffer(XRMeshRenderer meshRenderer, XRDataBuffer indexBuffer, IndexSize elementSize)
        => throw UnsupportedEngineOperation(nameof(TrySyncMeshRendererIndexBuffer));

    public override void ConfigureVAOAttributesForProgram(XRRenderProgram program, XRMeshRenderer.BaseVersion? version)
        => throw UnsupportedEngineOperation(nameof(ConfigureVAOAttributesForProgram));

    public override void BindDrawIndirectBuffer(XRDataBuffer buffer)
        => throw UnsupportedEngineOperation(nameof(BindDrawIndirectBuffer));

    public override void UnbindDrawIndirectBuffer()
        => throw UnsupportedEngineOperation(nameof(UnbindDrawIndirectBuffer));

    public override void BindParameterBuffer(XRDataBuffer buffer)
        => throw UnsupportedEngineOperation(nameof(BindParameterBuffer));

    public override void UnbindParameterBuffer()
        => throw UnsupportedEngineOperation(nameof(UnbindParameterBuffer));

    public override void MultiDrawElementsIndirect(uint drawCount, uint stride)
        => throw UnsupportedEngineOperation(nameof(MultiDrawElementsIndirect));

    public override void MultiDrawElementsIndirectWithOffset(uint drawCount, uint stride, nuint byteOffset)
        => throw UnsupportedEngineOperation(nameof(MultiDrawElementsIndirectWithOffset));

    public override void MultiDrawElementsIndirectCount(uint maxDrawCount, uint stride, nuint byteOffset = 0, nuint countByteOffset = 0)
        => throw UnsupportedEngineOperation(nameof(MultiDrawElementsIndirectCount));

}
