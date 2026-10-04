using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Admits a retained canonical Uber source only against its exact prepared offline companion.</summary>
public static class EngineUberBaseMaterialAdmission
{
    private sealed class PreparedBinding(UberBaseMaterialProfile profile, string identity, UberBaseSurfaceBinding binding)
    {
        public readonly UberBaseMaterialProfile Profile = profile;
        public readonly string Identity = identity;
        public readonly UberBaseSurfaceBinding Binding = binding;
    }
    private static readonly ConditionalWeakTable<XRMaterial, PreparedBinding> Bindings = new();

    public static bool TryAdmit(XRMaterial source, ShaderProgramArtifact artifact,
        out UberBaseSurfaceBinding? binding, out string? reason)
    {
        binding = null;
        reason = "UberBase.CompanionMissing: the original material requires its exact prepared Uber base target artifact.";
        if (source.EngineSemantic != EngineMaterialSemanticIdentity.UberBaseV1 || source.CookedUberBaseProfile is not { } profile ||
            source.ID == Guid.Empty || artifact.Identity != profile.CookedArtifactIdentity || source.Shaders.Count is < 1 or > 2) return false;
        for (int index = 0; index < source.Shaders.Count; index++)
        {
            XRShader shader = source.Shaders[index];
            if (shader.Type is not (EShaderType.Vertex or EShaderType.Fragment) || shader.CookedArtifactIdentity != artifact.Identity ||
                !string.IsNullOrEmpty(shader.Source?.Text) || !string.IsNullOrEmpty(shader.Source?.FilePath)) return false;
        }
        if (Bindings.TryGetValue(source, out PreparedBinding? cached))
        {
            if (!ReferenceEquals(cached.Profile, profile) || cached.Identity != artifact.Identity)
            {
                reason = "UberBase.ProfileChanged: replace the target material at a generation boundary after recooking its source.";
                return false;
            }
            if (!cached.Binding.TryGetValues(out _, out reason)) return false;
            binding = cached.Binding;
            return true;
        }
        if (!TryValidateArtifact(source.ID, profile, artifact, EngineUberBaseShaderContract.Pass, out reason)) return false;
        if (profile.SourceShaders.Length != 2 || string.IsNullOrEmpty(profile.PreparedFragmentSource) ||
            EngineUberBaseShaderContract.NormalizedHash(profile.PreparedFragmentSource) != profile.SourceIdentity)
        {
            reason = "UberBase.AuthoredSourceMissing: original canonical stages and the exact prepared GLSL source must survive target hydration.";
            return false;
        }
        bool vertex = false, fragment = false;
        foreach (XRShader shader in profile.SourceShaders)
        {
            string path;
            if (shader.Type == EShaderType.Vertex && !vertex) { path = "Uber/UberShader.vert"; vertex = true; }
            else if (shader.Type == EShaderType.Fragment && !fragment) { path = "Uber/UberShader.frag"; fragment = true; }
            else return false;
            if (shader.SourceLanguage != ShaderSourceLanguage.Glsl || shader.EntryPoint != "main" || shader.Source?.Text is not { } text ||
                !MatchesCanonical(path, text))
            {
                reason = $"UberBase.AuthoredStageUnsupported: '{path}' must retain the unchanged canonical source and entry point.";
                return false;
            }
        }
        if (!UberBaseSurfaceBinding.TryCreate(source, profile, out binding, out reason)) return false;
        Bindings.Add(source, new(profile, artifact.Identity, binding!));
        return true;
    }

    public static bool TryValidateArtifact(Guid materialId, UberBaseMaterialProfile profile,
        ShaderProgramArtifact artifact, string pass, out string? reason)
    {
        try
        {
            string expected = EngineUberBaseShaderContract.GenerateSource(profile.Features, profile.SourceIdentity,
                profile.Variant.VariantHash, profile.Variant.StaticProperties, profile.Variant.PipelineMacros, pass);
            if (!EngineUberBaseShaderContract.TryValidateSource(artifact,
                    EngineUberBaseShaderContract.CookName(materialId, profile.Variant.VariantHash, pass), expected, pass, out string sourceReason))
            { reason = sourceReason; return false; }
            if (!EngineUberBaseShaderContract.TryValidateLayout(artifact, profile.Features, pass, out string layoutReason))
            { reason = layoutReason; return false; }
            reason = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or NotSupportedException)
        {
            reason = exception.Message;
            return false;
        }
    }

    private static bool MatchesCanonical(string path, string text)
    {
        foreach (EngineLitMaterialShaderSource source in EngineUberBaseShaderContract.RequiredDesktopSources)
            if (source.Path == path) return EngineUberBaseShaderContract.NormalizedHash(text) == source.Sha256;
        return false;
    }
}
