using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMaterial
{
    private EngineMaterialVariantKey? _authoredShadowReceiverKey;

    private ShaderProgramArtifact ResolveAuthoredLitArtifact()
    {
        ShaderProgramArtifact artifact = ResolveAuthoredLitSource(Data,
            out StandardLitColorSurfaceBinding? color, out StandardLitTextureSurfaceBinding? texture);
        SetField(ref _litSurface, color);
        SetField(ref _litTextureSurface, texture);
        SetField(ref _litTextureVertexProfile, texture is not null && texture.TryRead(out StandardLitTextureSurface surface, out _)
            ? surface.VertexProfile : null);
        bool coverage = Data.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitV2;
        bool local = coverage, directional = coverage;
        if (!coverage && EngineLitShadowCompanionContract.TryGetReceiverKey(Data, Renderer.ShaderArtifacts,
            localShadows: true, out EngineMaterialVariantKey key, out _))
        {
            ShaderProgramArtifact? receiver = null;
            local = Renderer.MaterialVariants?.TryResolve(key, out receiver) == true;
            if (!local) key = key with { OutputProfile = "linear-hdr-directional-shadow-v1" };
            directional = local || Renderer.MaterialVariants?.TryResolve(key, out receiver) == true;
            if (directional)
            {
                if (!EngineLitShadowCompanionContract.TryValidate(receiver!, key, out string reason))
                    throw new NotSupportedException($"WebGPU.Material.AuthoredShadowCompanionUnsupported: '{Data.Name}', '{key}': {reason}");
                artifact = receiver!;
                SetField(ref _authoredShadowReceiverKey, key);
            }
        }
        SetField(ref _directionalShadowReceiver, directional);
        SetField(ref _localShadowReceiver, local);
        return artifact;
    }

    private void ValidateAuthoredShadowReceiver()
    {
        if (_authoredShadowReceiverKey is { } selected)
        {
            if (!EngineLitShadowCompanionContract.TryGetReceiverKey(Data, Renderer.ShaderArtifacts,
                _localShadowReceiver, out EngineMaterialVariantKey current, out string reason) || current != selected)
                throw new NotSupportedException($"WebGPU.Material.AuthoredShadowSourceChanged: '{Data.Name}': {reason} Replace the material at a resource-generation boundary.");
            if (_localShadowReceiver) return;
        }
        if (Data.EngineSemantic != EngineMaterialSemanticIdentity.AuthoredLitV1 ||
            RuntimeEngine.Rendering.State.RenderingWorld is not { } world) return;
        bool local = false, directional = false;
        for (int index = 0; index < world.Lights.DynamicPointLights.Count; index++)
            local |= world.Lights.DynamicPointLights[index].CastsShadows;
        for (int index = 0; index < world.Lights.DynamicSpotLights.Count; index++)
            local |= world.Lights.DynamicSpotLights[index].CastsShadows;
        for (int index = 0; index < world.Lights.DynamicDirectionalLights.Count; index++)
            directional |= world.Lights.DynamicDirectionalLights[index].CastsShadows;
        if (!local && (!directional || _directionalShadowReceiver)) return;
        if (!EngineLitShadowCompanionContract.TryGetReceiverKey(Data, Renderer.ShaderArtifacts,
            local, out EngineMaterialVariantKey required, out string sourceReason))
            throw new NotSupportedException($"WebGPU.Material.AuthoredShadowSourceUnsupported: '{Data.Name}': {sourceReason}");
        throw new NotSupportedException($"WebGPU.Material.AuthoredShadowVariantMissing: '{Data.Name}' requires '{required}'; recook the shadow catalog.");
    }

    private static void ValidateOpaqueShadowCompanion(ShaderProgramArtifact artifact, EngineMaterialVariantKey key)
    {
        if (!EngineLitShadowCompanionContract.TryValidate(artifact, key, out string reason))
            throw new NotSupportedException($"WebGPU.Material.ShadowCompanionUnsupported: '{key}': {reason}");
    }

    private ShaderProgramArtifact ResolveAuthoredLitSource(XRMaterial source,
        out StandardLitColorSurfaceBinding? color, out StandardLitTextureSurfaceBinding? texture)
    {
        color = null;
        texture = null;
        if (source.Shaders.Count is < 1 or > 2 ||
            source.Shaders.Any(shader => shader.Type is not (EShaderType.Vertex or EShaderType.Fragment)))
            throw new NotSupportedException($"WebGPU.Material.AuthoredStagesUnsupported: '{source.Name}' requires only authored vertex/fragment stages sharing one whole-program companion.");
        ShaderProgramArtifact? artifact = null;
        foreach (XRShader shader in source.Shaders)
        {
            if (!shader.TryGetCookedArtifact(ShaderCompileTarget.WebGPUWgsl, Renderer.ShaderArtifacts, out ShaderProgramArtifact? stage) ||
                artifact is not null && stage.Identity != artifact.Identity)
                throw new NotSupportedException($"WebGPU.Material.AuthoredCompanionMissing: '{source.Name}' requires identical exact WebGPU descriptor identities on every retained stage.");
            artifact = stage;
        }
        string? reason = null;
        if (artifact is null || artifact.DescriptorBytes.IsDefaultOrEmpty ||
            !EngineAuthoredLitMaterialAdmission.TryAdmit(source, artifact, out color, out texture, out reason))
            throw new NotSupportedException($"WebGPU.Material.AuthoredLitUnsupported: '{source.Name}': {reason ?? "a verified PBR companion is required"}.");
        return artifact;
    }
}
