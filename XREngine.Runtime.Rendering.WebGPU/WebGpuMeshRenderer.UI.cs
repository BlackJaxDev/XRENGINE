using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMeshRenderer
{
    /// <summary>Resolves the physical alpha contract without mutating the shared UI material.</summary>
    private static WebGpuRasterState ResolveUiRasterState(WebGpuMaterial material,
        WebGpuRasterState state, WebGpuFrameBuffer? frameBuffer, in RenderFrameOutputDescription output)
    {
        if (material.UISemantic == EngineMaterialSemantic.None)
            return state;

        if (state.DepthEnabled || state.DepthWrite || state.CullMode != ECullMode.None ||
            !state.BlendEnabled || state.SourceRgb != EBlendingFactor.SrcAlpha ||
            state.DestinationRgb != EBlendingFactor.OneMinusSrcAlpha ||
            state.SourceAlpha != EBlendingFactor.SrcAlpha ||
            state.DestinationAlpha != EBlendingFactor.OneMinusSrcAlpha ||
            state.RgbEquation != EBlendEquationMode.FuncAdd || state.AlphaEquation != EBlendEquationMode.FuncAdd)
            throw Unsupported("UI batches require their exact straight-alpha, no-depth, no-cull authored raster state");

        if (frameBuffer is null)
        {
            if (output.Properties.SampleCount != 1 ||
                output.Properties.ColorEncoding is not ("rgba8unorm" or "bgra8unorm"))
                throw Unsupported("screen UI requires a single-sample display RGBA8/BGRA8 target");
            return state;
        }

        if (material.Data.EngineSemantic.Version != 2 || frameBuffer.SampleCount != 1 ||
            frameBuffer.HasDepth || frameBuffer.ColorFormats.Length != 1 || frameBuffer.ColorFormats[0] != "rgba16float" ||
            frameBuffer.Data is not XRMaterialFrameBuffer { Material.EngineSemantic: var semantic } ||
            semantic != EngineMaterialSemanticIdentity.UICanvasSurfaceV1)
            throw Unsupported("offscreen UI requires V2 cooked batches and an owned single-sample linear RGBA16F canvas target; recook older UI artifacts");

        return state with { SourceRgb = EBlendingFactor.One, SourceAlpha = EBlendingFactor.One };
    }
}
