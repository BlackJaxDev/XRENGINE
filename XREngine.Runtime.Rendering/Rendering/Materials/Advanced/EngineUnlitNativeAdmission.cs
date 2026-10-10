using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering;

/// <summary>Reads the native unlit surface only after resolving its exact target-cooked raster companion.</summary>
public static class EngineUnlitNativeAdmission
{
    public static bool TryRead(XRMaterial material, out EngineUnlitSurface surface, out string reason)
    {
        RuntimeEngineMaterialArtifactServices.GetCurrent(out IShaderProgramArtifactResolver? resolver,
            out IEngineMaterialVariantResolver? variants);
        return TryRead(material, resolver, variants, out surface, out reason);
    }

    public static bool TryRead(XRMaterial material, IShaderProgramArtifactResolver? resolver,
        out EngineUnlitSurface surface, out string reason)
    {
        RuntimeEngineMaterialArtifactServices.GetCurrent(out IShaderProgramArtifactResolver? installed,
            out IEngineMaterialVariantResolver? variants);
        return TryRead(material, resolver, ReferenceEquals(resolver, installed) ? variants : null,
            out surface, out reason);
    }

    /// <summary>Requires the identity and variant views from one loaded catalog for source-free factories.</summary>
    public static bool TryRead(XRMaterial material, IShaderProgramArtifactResolver? resolver,
        IEngineMaterialVariantResolver? variants, out EngineUnlitSurface surface, out string reason)
    {
        surface = default;
        reason = "Native unlit shading requires the loaded exact engine-generated raster companion.";
        string? admissionReason = null;
        if (!material.EngineSemantic.IsUnlit() || resolver is null) return false;
        if (material.Shaders.Count == 0)
        {
            EngineMaterialVariantKey key = EngineUnlitMaterialShaderGenerator.BuiltInKey(material.EngineSemantic);
            if (variants?.TryResolve(key, out ShaderProgramArtifact? builtIn) != true || builtIn is null ||
                !resolver.TryResolve(builtIn.Identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? resident) ||
                resident?.Identity != builtIn.Identity ||
                !EngineUnlitMaterialAdmission.TryAdmitBuiltIn(material, builtIn, key,
                    out EngineUnlitSurfaceBinding? builtInBinding, out admissionReason) || builtInBinding is null ||
                !builtInBinding.TryRead(out surface, out admissionReason))
            {
                reason = admissionReason ?? reason;
                return false;
            }
            reason = string.Empty;
            return true;
        }
        if (material.Shaders.Count != 1 ||
            material.Shaders[0].CookedArtifactIdentity is not { } identity ||
            !resolver.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact) ||
            artifact.Identity != identity ||
            !EngineUnlitMaterialAdmission.TryAdmit(material, artifact, out EngineUnlitSurfaceBinding? binding,
                out admissionReason) || binding is null ||
            !binding.TryRead(out surface, out admissionReason))
        {
            reason = admissionReason ?? reason;
            return false;
        }
        reason = string.Empty;
        return true;
    }
}
