using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMaterial
{
    private ShaderProgramArtifact ResolveAuthoredLitArtifact()
    {
        if (Data.Shaders.Count is < 1 or > 2 ||
            Data.Shaders.Any(shader => shader.Type is not (EShaderType.Vertex or EShaderType.Fragment)))
            throw new NotSupportedException($"WebGPU.Material.AuthoredStagesUnsupported: '{Data.Name}' requires only authored vertex/fragment stages sharing one whole-program companion.");
        ShaderProgramArtifact? artifact = null;
        foreach (XRShader shader in Data.Shaders)
        {
            if (!shader.TryGetCookedArtifact(ShaderCompileTarget.WebGPUWgsl, Renderer.ShaderArtifacts, out ShaderProgramArtifact? stage) ||
                artifact is not null && stage.Identity != artifact.Identity)
                throw new NotSupportedException($"WebGPU.Material.AuthoredCompanionMissing: '{Data.Name}' requires identical exact WebGPU descriptor identities on every retained stage.");
            artifact = stage;
        }
        string? reason = null;
        if (artifact is null || artifact.DescriptorBytes.IsDefaultOrEmpty ||
            !EngineAuthoredLitMaterialAdmission.TryAdmit(Data, artifact,
                out StandardLitColorSurfaceBinding? color, out StandardLitTextureSurfaceBinding? texture, out reason))
            throw new NotSupportedException($"WebGPU.Material.AuthoredLitUnsupported: '{Data.Name}': {reason ?? "a verified opaque PBR companion is required"}.");
        SetField(ref _litSurface, color);
        SetField(ref _litTextureSurface, texture);
        SetField(ref _litTextureVertexProfile, texture is not null && texture.TryRead(out StandardLitTextureSurface surface, out _)
            ? surface.VertexProfile : null);
        return artifact;
    }
}
