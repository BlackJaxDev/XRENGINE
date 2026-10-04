using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMaterial
{
    private bool IsCanvasSurface => Data.EngineSemantic == EngineMaterialSemanticIdentity.UICanvasSurfaceV1;

    private ShaderProgramArtifact ResolveCanvasSurfaceArtifact()
    {
        ValidateCanvasSurfaceTexture();
        EngineMaterialVariantKey key = new(EngineMaterialSemanticIdentity.UICanvasSurfaceV1,
            ShaderCompileTarget.WebGPUWgsl, "canvas-composite", "position-uv-v1", "linear-hdr-premultiplied-rgba-v1");
        if (Renderer.MaterialVariants?.TryResolve(key, out ShaderProgramArtifact? artifact) != true || artifact is null)
            throw new NotSupportedException($"WebGPU.Material.VariantMissing: '{Data.Name}' requires the declared {key} variant.");
        return artifact;
    }

    private XRTexture2D ValidateCanvasSurfaceTexture()
    {
        if (Data.Shaders.Count != 0 || Data.Parameters.Length != 0 || Data.Textures.Count != 1 ||
            Data.Textures[0] is not XRTexture2D texture || texture.SizedInternalFormat != ESizedInternalFormat.Rgba16f ||
            texture.MultiSampleCount != 1 || texture.FrameBufferAttachment != EFrameBufferAttachment.ColorAttachment0 ||
            texture.AutoGenerateMipmaps || texture.Mipmaps.Length != 1 || texture.EnableComparison ||
            texture.SamplerName is not (null or "Texture0"))
            throw new NotSupportedException("WebGPU.Material.CanvasSurfaceUnsupported: a source-free canvas surface requires its single-sample linear RGBA16F render texture and no authored parameters.");
        return texture;
    }

    private void PublishCanvasSurface()
    {
        XRTexture2D texture = ValidateCanvasSurfaceTexture();
        WebGpuFrameBuffer? target = Renderer.GetBoundEngineFrameBuffer();
        WebGpuRasterState state = Renderer.RasterState;
        if (target is null || target.ColorFormats.Length != 1 || target.ColorFormats[0] != "rgba16float" ||
            !target.HasDepth || target.SampleCount is not (1 or 4) || !state.DepthEnabled || state.DepthWrite ||
            state.CullMode != ECullMode.None || !state.BlendEnabled ||
            state.SourceRgb != EBlendingFactor.One || state.SourceAlpha != EBlendingFactor.One ||
            state.DestinationRgb != EBlendingFactor.OneMinusSrcAlpha || state.DestinationAlpha != EBlendingFactor.OneMinusSrcAlpha ||
            state.RgbEquation != EBlendEquationMode.FuncAdd || state.AlphaEquation != EBlendEquationMode.FuncAdd)
            throw new NotSupportedException("WebGPU.Material.CanvasOutputUnsupported: a canvas surface requires a linear HDR scene target with depth testing, no depth writes, and premultiplied source-over blending.");

        if (target.Data.Targets is { } attachments)
            foreach (var attachment in attachments)
                if (ReferenceEquals(attachment.Target, texture))
                    throw new NotSupportedException("WebGPU.Material.CanvasFeedbackUnsupported: a canvas cannot sample its own active render target.");

        WebGpuTexture2D image = (WebGpuTexture2D)Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true)!;
        if (!image.IsCurrentGpuAllocationForCopy || !image.WasProducedInFrame(Renderer.EngineFrameSequence))
            Renderer.MarkEngineDrawPending(image.IsCurrentGpuAllocationForCopy
                ? "CanvasTextureProducerPending" : "CanvasTextureAllocationPending", image);
    }
}
