using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering;

/// <summary>Proves the selected material's complete cooked artifact before native Uber publication.</summary>
public static class EngineUberBaseNativeAdmission
{
    public static bool TryRead(XRMaterial material, out UberBaseSurfaceBinding? binding, out string reason)
    {
        binding = null;
        reason = "UberBase.NativeArtifactMissing: every retained stage requires its exact verified per-material cooked program.";
        IShaderProgramArtifactResolver? resolver = RuntimeEngineMaterialArtifactServices.Resolver;
        if (material.EngineSemantic != EngineMaterialSemanticIdentity.UberBaseV1 || resolver is null || material.Shaders.Count is < 1 or > 2) return false;
        ShaderProgramArtifact? program = null;
        int fragments = 0;
        for (int index = 0; index < material.Shaders.Count; index++)
        {
            XRShader stage = material.Shaders[index];
            if (stage.Type is not (EShaderType.Vertex or EShaderType.Fragment) || stage.CookedArtifactIdentity is not { } identity ||
                !resolver.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact) || artifact.Identity != identity ||
                program is not null && program.Identity != artifact.Identity) return false;
            if (stage.Type == EShaderType.Fragment) fragments++;
            program = artifact;
        }
        if (program is null || fragments != 1) return false;
        if (!EngineUberBaseMaterialAdmission.TryAdmit(material, program, out binding, out string? admissionReason))
        {
            reason = admissionReason ?? reason;
            return false;
        }
        reason = string.Empty;
        return true;
    }
}
