using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering;

/// <summary>Captures the verified authored local-vertex producer and its four exact material inputs.</summary>
public static class AdvancedNativeVertexMaterialSource
{
    public const string Schema = "xrengine.engine.authored-lit-color-native-vertex.v1";
    public const string Input0 = "NativeVertexInput0";
    public const string Input1 = "NativeVertexInput1";
    public const string Input2 = "NativeVertexInput2";
    public const string Input3 = "NativeVertexInput3";

    public static bool IsInputName(string? name)
        => name is Input0 or Input1 or Input2 or Input3;

    /// <summary>Detects the requested profile even when its required compute companion is missing.</summary>
    public static bool IsRequested(XRMaterial? material, IShaderProgramArtifactResolver? resolver = null)
    {
        resolver ??= RuntimeEngineMaterialArtifactServices.Resolver;
        if (material is null || resolver is null) return false;
        for (int index = 0; index < material.Shaders.Count; index++)
            if (material.Shaders[index].CookedArtifactIdentity is { } identity &&
                resolver.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact) &&
                artifact.NativeVertexCompanion?.Profile == EngineNativeVertexShaderGenerator.Profile)
                return true;
        return false;
    }

    /// <summary>Whether generic skin/morph would supply a different normal domain from canonical aggregate geometry.</summary>
    public static bool RequiresCanonicalDeformationSource(XRMesh mesh)
        => mesh.HasSkinning && RuntimeEngine.Rendering.Settings.AllowSkinning ||
           mesh.HasBlendshapes && RuntimeEngine.Rendering.Settings.AllowBlendshapes;

    /// <summary>Resolves an auxiliary only through the same verified source material and local function closure.</summary>
    public static bool TryResolveAuxiliary(XRMaterial material, IShaderProgramArtifactResolver? resolver,
        EngineNativeVertexAuxiliaryPass pass, out ShaderProgramArtifact? artifact, out string reason)
    {
        artifact = null;
        if (!TryCapture(material, resolver, out _, out reason)) return false;
        string identity = material.Shaders[0].CookedArtifactIdentity!;
        if (!resolver!.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? raster))
        {
            reason = "The exact native vertex raster source is missing from the current catalog.";
            return false;
        }
        return EngineNativeVertexShaderProvenance.TryResolveAuxiliary(raster, resolver, pass, out artifact, out reason);
    }

    public static bool TryCapture(XRMaterial? material, out AdvancedNativeVertexMaterial vertex, out string reason)
        => TryCapture(material, RuntimeEngineMaterialArtifactServices.Resolver, out vertex, out reason);

    /// <summary>Requires the exact loaded raster and compute closure; a semantic label does not establish support.</summary>
    public static bool TryCapture(XRMaterial? material, IShaderProgramArtifactResolver? resolver,
        out AdvancedNativeVertexMaterial vertex, out string reason)
    {
        vertex = default;
        reason = "The material has no exact generated native vertex companion; recook its shared local-vertex function.";
        if (material is null || material.GetType() != typeof(XRMaterial) || resolver is null || material.EngineSemantic != EngineMaterialSemanticIdentity.AuthoredLitV1 ||
            material.Shaders.Count is < 1 or > 2 || material.BillboardMode != EMeshBillboardMode.None ||
            material.HasSettingVertexUniformHandlers || material.BindingPublishers.Count != 0)
            return false;
        ShaderProgramArtifact? raster = null;
        int fragments = 0;
        for (int index = 0; index < material.Shaders.Count; index++)
        {
            XRShader shader = material.Shaders[index];
            if (shader.Type is not (EShaderType.Vertex or EShaderType.Fragment) ||
                !string.IsNullOrWhiteSpace(shader.Source?.Text) || !string.IsNullOrWhiteSpace(shader.Source?.FilePath) ||
                shader.CookedArtifactIdentity is not { } identity ||
                !resolver.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact) ||
                artifact.Identity != identity || raster is not null && raster.Identity != artifact.Identity)
                return false;
            if (shader.Type == EShaderType.Fragment) fragments++;
            raster = artifact;
        }
        ShaderProgramArtifact? compute = null;
        if (fragments != 1 || raster is null || raster.SemanticSchemaIdentity != Schema ||
            !raster.Name.StartsWith("mat-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(raster.Name.AsSpan(4), "N", out Guid materialId) || materialId != material.ID ||
            !EngineNativeVertexShaderProvenance.TryValidate(raster, resolver, out compute, out reason))
            return false;
        if (material.Parameter<ShaderVector4>(Input0) is not { } input0 ||
            material.Parameter<ShaderVector4>(Input1) is not { } input1 ||
            material.Parameter<ShaderVector4>(Input2) is not { } input2 ||
            material.Parameter<ShaderVector4>(Input3) is not { } input3)
        {
            reason = "The native local-vertex profile requires NativeVertexInput0 through NativeVertexInput3 as four vec4 material parameters.";
            return false;
        }
        AdvancedNativeVertexInputs inputs = new(input0.Value, input1.Value, input2.Value, input3.Value);
        if (!inputs.IsFinite)
        {
            reason = "Native local-vertex material inputs must be finite.";
            return false;
        }
        vertex = new(compute, inputs);
        reason = string.Empty;
        return true;
    }
}
