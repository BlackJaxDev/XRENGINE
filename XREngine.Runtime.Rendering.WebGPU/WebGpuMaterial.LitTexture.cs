using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMaterial
{
    private StandardLitTextureSurfaceBinding? _litTextureSurface;
    private string? _litTextureVertexProfile;

    private ShaderProgramArtifact ResolveLitTextureArtifact()
    {
        XRMaterial source = Data.StandardLitColorSourceMaterial ?? Data;
        if (source.Shaders.Count != 0 || Data.Shaders.Count != 0)
            throw new NotSupportedException("WebGPU.Material.TexturedSourceUnsupported: the explicitly tagged deferred surface requires a serializer-owned source-free cooked companion.");
        if (!StandardLitTextureSurfaceBinding.TryCreate(source, out StandardLitTextureSurfaceBinding? binding, out string? reason) ||
            !binding!.TryRead(out StandardLitTextureSurface surface, out reason))
            throw new NotSupportedException($"WebGPU.Material.TexturedSurfaceUnsupported: '{Data.Name}': {reason}");
        EStandardLitColorAuxiliaryPass auxiliary = Data.StandardLitColorAuxiliaryPass;
        if (auxiliary is not (EStandardLitColorAuxiliaryPass.None or EStandardLitColorAuxiliaryPass.DepthNormal))
            throw new NotSupportedException("WebGPU.Material.TexturedAuxiliaryUnsupported: opaque textured surfaces use the shared opaque shadow casters.");
        EngineMaterialVariantKey key = new(Data.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
            auxiliary == EStandardLitColorAuxiliaryPass.DepthNormal ? "depth-normal" : "opaque-forward",
            surface.VertexProfile, auxiliary == EStandardLitColorAuxiliaryPass.DepthNormal ? "normal-rgba16f-v1" : "linear-hdr-v1");
        ShaderProgramArtifact? artifact = null;
        bool local = auxiliary == EStandardLitColorAuxiliaryPass.None &&
            Renderer.MaterialVariants?.TryResolve(key with { OutputProfile = "linear-hdr-local-shadows-v1" }, out artifact) == true;
        bool shadow = local || auxiliary == EStandardLitColorAuxiliaryPass.None &&
            Renderer.MaterialVariants?.TryResolve(key with { OutputProfile = "linear-hdr-directional-shadow-v1" }, out artifact) == true;
        if (!shadow && Renderer.MaterialVariants?.TryResolve(key, out artifact) != true || artifact is null)
            throw new NotSupportedException($"WebGPU.Material.VariantMissing: '{Data.Name}' requires '{key}'.");
        SetField(ref _litTextureSurface, binding);
        SetField(ref _litTextureVertexProfile, surface.VertexProfile);
        SetField(ref _litAuxiliaryPass, auxiliary);
        SetField(ref _directionalShadowReceiver, shadow);
        SetField(ref _localShadowReceiver, local);
        return artifact;
    }

    private bool TryPublishLitTexture()
    {
        if (_litTextureSurface is null) return false;
        if (!_litTextureSurface.TryRead(out StandardLitTextureSurface surface, out string? reason))
            throw new NotSupportedException($"WebGPU.Material.TexturedSurfaceUnsupported: '{Data.Name}': {reason}");
        if (surface.VertexProfile != _litTextureVertexProfile)
            throw new NotSupportedException("WebGPU.Material.TexturedLayoutChanged: normal-map presence requires replacement at a resource-generation boundary.");
        WebGpuFrameBuffer? target = Renderer.GetBoundEngineFrameBuffer();
        if (target is null || !target.HasDepth || target.SampleCount != 1 || target.ColorFormats.Length != 1 || target.ColorFormats[0] != "rgba16float")
            throw new NotSupportedException("WebGPU.Material.TexturedOutputUnsupported: the opaque textured surface requires one linear RGBA16F attachment with single-sample depth.");
        ValidateCoverageRasterState(surface.Values);
        if (_litAuxiliaryPass == EStandardLitColorAuxiliaryPass.DepthNormal)
        {
            Program.Data.Sampler("StandardLitNormalTexture", surface.Normal!.Texture, 1);
            return true;
        }
        StandardLitColorSurface values = surface.Values;
        Program.SetVector4("StandardLitBaseColorOpacity", new Vector4(values.BaseColor, values.Opacity));
        Program.SetVector4("StandardLitRoughnessMetallicSpecularEmission", new Vector4(values.Roughness, values.Metallic, values.Specular, values.Emission));
        Program.SetVector4("StandardLitTextureControls", new Vector4(surface.Roughness is null ? 0 : 1, surface.Metallic is null ? 0 : 1, 0, 0));
        Program.Data.Sampler("StandardLitBaseColorTexture", surface.BaseColor.Texture, 0);
        if (surface.Normal is not null) Program.Data.Sampler("StandardLitNormalTexture", surface.Normal.Texture, 1);
        // Disabled optional maps use an already owned binding; their values are never sampled.
        // This is the explicit uniform-factor contract, not a substituted authored texture.
        Program.Data.Sampler("StandardLitMetallicTexture", (surface.Metallic ?? surface.BaseColor).Texture, 2);
        Program.Data.Sampler("StandardLitRoughnessTexture", (surface.Roughness ?? surface.BaseColor).Texture, 3);
        Renderer.PublishForwardLights(Program, _directionalShadowReceiver, _localShadowReceiver);
        Renderer.PublishAmbientOcclusion(Program);
        return true;
    }
}
