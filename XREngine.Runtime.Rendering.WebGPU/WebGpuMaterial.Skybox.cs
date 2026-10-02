using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMaterial
{
    private ShaderProgramArtifact ResolveSkyboxArtifact()
    {
        if (Data.Shaders.Count != 0 || Data.Parameters.Length != 0 ||
            Data.RenderPass != (int)EDefaultRenderPass.Background)
            throw new NotSupportedException("WebGPU.Skybox.MaterialUnsupported: built-in sky variants require the source-free background material and its typed binding publisher.");
        EngineMaterialSemantic semantic = Data.EngineSemantic.Semantic;
        bool texture = semantic is EngineMaterialSemantic.SkyboxEquirectangular or
            EngineMaterialSemantic.SkyboxOctahedral or EngineMaterialSemantic.SkyboxCubemap;
        if (texture ? Data.Textures.Count != 1 ||
            (semantic == EngineMaterialSemantic.SkyboxCubemap ? Data.Textures[0] is not XRTextureCube
                : Data.Textures[0] is not XRTexture2D) : Data.Textures.Count != 0)
            throw new NotSupportedException("WebGPU.Skybox.TextureTopologyMismatch: the material must retain its exact authored sky image topology.");
        EngineMaterialVariantKey key = new(Data.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
            "background", "fullscreen-sky-v1", "linear-hdr-v1");
        if (Renderer.MaterialVariants?.TryResolve(key, out ShaderProgramArtifact? artifact) != true || artifact is null)
            throw new NotSupportedException($"WebGPU.Skybox.VariantMissing: '{Data.Name}' requires its declared '{key}' cooked program.");
        return artifact;
    }

    private bool TryPublishSkybox()
    {
        if (!Data.EngineSemantic.IsSkybox())
            return false;
        WebGpuFrameBuffer? target = Renderer.GetBoundEngineFrameBuffer();
        if (target is null || !target.HasDepth || target.SampleCount != 1 ||
            target.ColorFormats.Length != 1 || target.ColorFormats[0] != "rgba16float")
            throw new NotSupportedException("WebGPU.Skybox.OutputUnsupported: sky backgrounds require one linear RGBA16F attachment with single-sample depth before bloom and tonemapping.");
        WebGpuRasterState state = Renderer.RasterState;
        if (!state.DepthEnabled || state.DepthWrite || state.DepthComparison != EComparison.Lequal ||
            state.CullMode != ECullMode.None || state.BlendEnabled || state.ColorWriteMask != 15)
            throw new NotSupportedException("WebGPU.Skybox.RasterUnsupported: normal-Z sky requires less-equal depth, no depth writes, no culling, no blending, and opaque RGBA output.");
        if (RuntimeEngine.Rendering.State.IsSceneCapturePass || RuntimeEngine.Rendering.State.IsLightProbePass)
            throw new NotSupportedException("WebGPU.Skybox.CaptureUnsupported: environment capture and probe convolution require their own cooked capture outputs.");
        return true;
    }
}
