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
        ValidateSkyboxViewContract(artifact);
        return artifact;
    }

    private static void ValidateSkyboxViewContract(ShaderProgramArtifact artifact)
    {
        if (artifact.SemanticSchemaIdentity == "xrengine.engine.skybox.v2")
            foreach (ShaderStageResourceLayout resource in artifact.Resources)
                if (resource.Contract.Set == 0 && resource.Contract.Binding == 0 &&
                    resource.Contract.Name == "View" && resource.BindingType == "uniform" &&
                    resource.Contract.Kind == ShaderAbiResourceKind.UniformBuffer && resource.DynamicOffset &&
                    resource.Visibility == ShaderStageVisibility.Vertex && resource.Contract.ByteSize == 144 &&
                    resource.Contract.Members.Length == 3)
                {
                    bool inverseView = false, inverseProjection = false, depthRange = false;
                    foreach (ShaderAbiMemberContract member in resource.Contract.Members)
                    {
                        if (member.ArrayCount != 0) continue;
                        if (member.Size == 64 && member.PhysicalType == "mat4x4<f32>" &&
                            member.MatrixOrder == ShaderAbiMatrixOrder.ColumnMajor && member.MatrixStride == 16)
                        {
                            inverseView |= member.ProviderName == "InverseViewMatrix" && member.Offset == 0;
                            inverseProjection |= member.ProviderName == "InverseProjMatrix" && member.Offset == 64;
                        }
                        depthRange |= member.ProviderName == "CameraDepthRange" && member.Offset == 128 &&
                            member.Size == 16 && member.PhysicalType == "vec4<f32>";
                    }
                    if (inverseView && inverseProjection && depthRange) return;
                }
        throw new NotSupportedException("WebGPU.Skybox.ViewContractMismatch: recook the sky with its frozen camera near/far depth and inverse-matrix contract.");
    }

    private bool TryPublishSkybox()
    {
        if (!Data.EngineSemantic.IsSkybox())
            return false;
        WebGpuFrameBuffer? target = Renderer.GetBoundEngineFrameBuffer();
        if (target is null || !target.HasDepth || target.SampleCount is not (1 or 4) ||
            target.ColorFormats.Length != 1 || target.ColorFormats[0] != "rgba16float")
            throw new NotSupportedException("WebGPU.Skybox.OutputUnsupported: sky backgrounds require one linear RGBA16F attachment with matching one- or four-sample depth before bloom and tonemapping.");
        WebGpuRasterState state = Renderer.RasterState;
        bool multisampleBackground = RuntimeEngine.Rendering.State.RenderingPipelineState?.AdvancedMultisampleBackground == true;
        bool validState = multisampleBackground
            ? !state.DepthEnabled && !state.DepthWrite && state.CullMode == ECullMode.None && state.ColorWriteMask == 15 &&
                state.BlendEnabled && state.SourceRgb == EBlendingFactor.OneMinusDstAlpha && state.DestinationRgb == EBlendingFactor.One &&
                state.SourceAlpha == EBlendingFactor.OneMinusDstAlpha && state.DestinationAlpha == EBlendingFactor.One &&
                state.RgbEquation == EBlendEquationMode.FuncAdd && state.AlphaEquation == EBlendEquationMode.FuncAdd
            : state.DepthEnabled && !state.DepthWrite &&
                state.DepthComparison == Renderer.MapAuthoredDepthComparison(EComparison.Lequal) &&
                state.CullMode == ECullMode.None && !state.BlendEnabled && state.ColorWriteMask == 15;
        if (!validState)
            throw new NotSupportedException(multisampleBackground
                ? "WebGPU.Skybox.RasterUnsupported: Advanced sample coverage requires disabled depth, no culling, RGBA writes, and the exact uncovered-alpha additive blend."
                : "WebGPU.Skybox.RasterUnsupported: sky requires authored less-equal depth, no depth writes, no culling, no blending, and opaque RGBA output.");
        if (RuntimeEngine.Rendering.State.IsSceneCapturePass || RuntimeEngine.Rendering.State.IsLightProbePass)
            throw new NotSupportedException("WebGPU.Skybox.CaptureUnsupported: environment capture and probe convolution require their own cooked capture outputs.");
        return true;
    }
}
