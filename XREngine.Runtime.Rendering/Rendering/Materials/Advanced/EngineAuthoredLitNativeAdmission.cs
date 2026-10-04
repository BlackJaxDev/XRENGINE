using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering;

/// <summary>Resolves exact generated programs before exposing their modeled surface to native publication.</summary>
public static class EngineAuthoredLitNativeAdmission
{
    private static readonly ConditionalWeakTable<ShaderProgramArtifact, ProgramProof> Proofs = new();
    private static readonly ConditionalWeakTable<ShaderProgramArtifact, NativeProgramProof> NativeProofs = new();

    /// <summary>Reads the separate red-opacity family only after its exact cooked raster program is admitted.</summary>
    public static bool TryReadTexturedAlpha(XRMaterial material, out TexturedAlphaSurface surface, out string reason)
        => TryReadTexturedAlpha(material, RuntimeEngineMaterialArtifactServices.Resolver, out surface, out reason);

    /// <summary>Uses the selected content catalog without interpreting authored desktop GLSL as native code.</summary>
    public static bool TryReadTexturedAlpha(XRMaterial material, IShaderProgramArtifactResolver? resolver,
        out TexturedAlphaSurface surface, out string reason)
    {
        surface = default;
        reason = "Native textured-alpha shading requires the exact loaded engine-generated raster companion.";
        if (material.EngineSemantic != EngineMaterialSemanticIdentity.AuthoredLitTextureAlphaV1 ||
            material.Shaders.Count is < 1 or > 2 || resolver is null)
            return false;
        ShaderProgramArtifact? program = null;
        int fragments = 0;
        for (int index = 0; index < material.Shaders.Count; index++)
        {
            XRShader shader = material.Shaders[index];
            if (shader.Type is not (EShaderType.Vertex or EShaderType.Fragment) ||
                !string.IsNullOrWhiteSpace(shader.Source?.Text) || !string.IsNullOrWhiteSpace(shader.Source?.FilePath) ||
                shader.CookedArtifactIdentity is not { } identity ||
                !resolver.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact) ||
                artifact.Identity != identity || program is not null && program.Identity != artifact.Identity)
                return false;
            if (shader.Type == EShaderType.Fragment) fragments++;
            program = artifact;
        }
        if (fragments != 1 || program is null) return false;
        if (!EngineTexturedAlphaMaterialAdmission.TryAdmit(material, program,
                out TexturedAlphaSurfaceBinding? binding, out string? admissionReason) || binding is null ||
            !binding.TryRead(out surface, out admissionReason))
        {
            reason = admissionReason ?? reason;
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>Reads the exact authored normal/specular family only after its exact cooked raster program is admitted.</summary>
    public static bool TryReadAuthoredTextured(XRMaterial material, out AuthoredTexturedSurface surface, out string reason)
        => TryReadAuthoredTextured(material, RuntimeEngineMaterialArtifactServices.Resolver, out surface, out reason);

    /// <summary>Uses the selected content catalog without interpreting authored desktop GLSL as native code.</summary>
    public static bool TryReadAuthoredTextured(XRMaterial material, IShaderProgramArtifactResolver? resolver,
        out AuthoredTexturedSurface surface, out string reason)
    {
        surface = default;
        reason = "Native authored textured shading requires the exact loaded engine-generated raster companion.";
        if (material.EngineSemantic != EngineMaterialSemanticIdentity.AuthoredLitTexturedV1 ||
            material.Shaders.Count is < 1 or > 2 || resolver is null)
            return false;
        ShaderProgramArtifact? program = null;
        int fragments = 0;
        for (int index = 0; index < material.Shaders.Count; index++)
        {
            XRShader shader = material.Shaders[index];
            if (shader.Type is not (EShaderType.Vertex or EShaderType.Fragment) ||
                !string.IsNullOrWhiteSpace(shader.Source?.Text) || !string.IsNullOrWhiteSpace(shader.Source?.FilePath) ||
                shader.CookedArtifactIdentity is not { } identity ||
                !resolver.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact) ||
                artifact.Identity != identity || program is not null && program.Identity != artifact.Identity)
                return false;
            if (shader.Type == EShaderType.Fragment) fragments++;
            program = artifact;
        }
        if (fragments != 1 || program is null) return false;
        if (!EngineAuthoredTexturedMaterialAdmission.TryAdmit(material, program,
                out AuthoredTexturedSurfaceBinding? binding, out string? admissionReason) || binding is null ||
            !binding.TryRead(out surface, out admissionReason))
        {
            reason = admissionReason ?? reason;
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>Reads current factors and roles without allocating after catalog admission.</summary>
    public static bool TryRead(XRMaterial material, out StandardLitColorSurface values,
        out StandardLitTextureSurface texture, out bool textured, out string reason)
        => TryRead(material, RuntimeEngineMaterialArtifactServices.Resolver, out values, out texture, out textured, out reason);

    /// <summary>Uses the selected content owner's catalog for an exact generated surface read.</summary>
    public static bool TryRead(XRMaterial material, IShaderProgramArtifactResolver? resolver,
        out StandardLitColorSurface values, out StandardLitTextureSurface texture, out bool textured, out string reason)
    {
        values = default;
        texture = default;
        textured = false;
        reason = "Native authored shading requires the loaded exact engine-generated whole-program companion.";
        if (!material.EngineSemantic.IsAuthoredLit() || material.Shaders.Count is < 1 or > 2 ||
            material.BillboardMode != EMeshBillboardMode.None || material.HasSettingVertexUniformHandlers ||
            resolver is null)
            return false;
        ShaderProgramArtifact? program = null;
        int fragments = 0;
        for (int index = 0; index < material.Shaders.Count; index++)
        {
            XRShader shader = material.Shaders[index];
            if (shader.Type is not (EShaderType.Vertex or EShaderType.Fragment) ||
                !string.IsNullOrWhiteSpace(shader.Source?.Text) || !string.IsNullOrWhiteSpace(shader.Source?.FilePath) ||
                shader.CookedArtifactIdentity is not { } identity ||
                !resolver.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact) ||
                artifact.Identity != identity || program is not null && program.Identity != artifact.Identity)
                return false;
            if (shader.Type == EShaderType.Fragment) fragments++;
            program = artifact;
        }
        if (fragments != 1 || program is null) return false;
        if (program.SemanticSchemaIdentity == AdvancedNativeVertexMaterialSource.Schema)
        {
            if (!AdvancedNativeVertexMaterialSource.TryCapture(material, resolver, out _, out reason))
                return false;
            if (!NativeProofs.GetValue(program, static artifact => new NativeProgramProof(artifact)).Valid)
            {
                reason = "The native local-vertex raster companion does not declare the exact canonical PBR and four-input physical ABI; recook the material.";
                return false;
            }
            if (!StandardLitColorSurfaceBinding.TryReadAuthoredCooked(material, out values, out string? nativeReason))
            {
                reason = nativeReason!;
                return false;
            }
            reason = string.Empty;
            return true;
        }
        ProgramProof proof = Proofs.GetValue(program, static artifact => new ProgramProof(artifact));
        if (proof.Reason is { } proofReason) { reason = proofReason; return false; }
        ShaderProgramArtifact verified = proof.Artifact!;
        if (!verified.Name.StartsWith("mat-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(verified.Name.AsSpan(4), "N", out Guid materialId) || materialId != material.ID ||
            (material.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitV2) != proof.Coverage)
        {
            reason = "Native generated provenance does not match this material's persistent identity and semantic version.";
            return false;
        }
        textured = proof.Textured;
        string? surfaceReason;
        if (textured)
        {
            if (!StandardLitTextureSurfaceBinding.TryReadAuthoredCooked(material, out texture, out surfaceReason))
            { reason = surfaceReason!; return false; }
            if ((texture.Normal is not null) != proof.NormalTexture)
            { reason = "The generated native companion's normal-map ABI differs from the current authored roles."; return false; }
            values = texture.Values;
        }
        else if (!StandardLitColorSurfaceBinding.TryReadAuthoredCooked(material, out values, out surfaceReason))
        { reason = surfaceReason!; return false; }
        reason = string.Empty;
        return true;
    }

    private sealed class ProgramProof
    {
        internal ShaderProgramArtifact? Artifact { get; }
        internal bool Coverage { get; }
        internal bool Textured { get; }
        internal bool NormalTexture { get; }
        internal string? Reason { get; }

        internal ProgramProof(ShaderProgramArtifact artifact)
        {
            try
            {
                ShaderProgramArtifact verified = ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes);
                if (verified.Identity != artifact.Identity)
                { Reason = "The generated native descriptor identity is stale; recook the authored material."; return; }
                if (!EngineLitMaterialShaderProvenance.TryValidate(verified, out string reason))
                { Reason = reason; return; }
                Coverage = verified.SemanticSchemaIdentity == EngineLitMaterialShaderGenerator.ColorCoverageSchema;
                Textured = verified.SemanticSchemaIdentity is EngineLitMaterialShaderGenerator.TextureSchema or EngineLitMaterialShaderGenerator.NormalTextureSchema;
                NormalTexture = verified.SemanticSchemaIdentity == EngineLitMaterialShaderGenerator.NormalTextureSchema;
                if (!EngineAuthoredLitMaterialAdmission.HasPhysicalPbrAbi(verified, Textured, NormalTexture, Coverage))
                { Reason = "The generated native companion does not declare the complete exact engine PBR physical ABI."; return; }
                Artifact = verified;
            }
            catch (InvalidDataException)
            { Reason = "The generated native companion is incomplete or stale; recook its verified descriptor and WGSL module."; }
        }
    }

    private sealed class NativeProgramProof(ShaderProgramArtifact artifact)
    {
        internal bool Valid { get; } = EngineAuthoredLitMaterialAdmission.HasPhysicalPbrAbi(
            artifact, false, false, false, nativeVertex: true);
    }
}
