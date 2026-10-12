using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Exact source-sampling, full-float export and native vertex-pull resource contract.</summary>
internal static class WebGpuAdvancedUberRasterProgramContract
{
    internal static void Validate(ShaderProgramArtifact artifact, string pass)
    {
        bool exports = pass is "uber-raster-surface" or "uber-raster-surface-msaa";
        bool multisample = pass.EndsWith("-msaa", StringComparison.Ordinal);
        if (!exports && pass is not ("uber-visibility" or "uber-visibility-msaa") ||
            artifact.Pass != pass || artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.SourceLanguage != "Slang" ||
            artifact.SemanticSchemaIdentity != "xrengine.engine.uber-native-raster.v2" || artifact.ComputeEntryPoint is not null ||
            artifact.VertexEntryPoint != "advancedUberVertex" || artifact.FragmentEntryPoint != (exports ? "advancedUberSurface" : "advancedUberVisibility") ||
            !artifact.VertexBuffers.IsDefaultOrEmpty || artifact.Resources.Length != (exports ? 45 : 12)) throw Invalid();
        for (uint binding = 0; binding < 4; binding++) Require(artifact, 0, binding, "read-only-storage", 4);
        ShaderStageResourceLayout parameters = Require(artifact, 0, 4, "uniform", 256);
        if (parameters.Contract.Members.Length != 17 || parameters.Contract.Members[15].ProviderName != "CameraPosition" ||
            parameters.Contract.Members[15].Offset != 240 || parameters.Contract.Members[16].ProviderName != "UberFeatures" ||
            parameters.Contract.Members[16].Offset != 252) throw Invalid();
        for (uint binding = 0; binding < (exports ? 14u : 6u); binding++)
            Require(artifact, 1, binding, (binding & 1u) == 0 ? "texture-2d-float" : "filtering-sampler");
        ShaderStageResourceLayout material = Require(artifact, 1, 14, "uniform", UberBaseParameterSchema.ByteSize);
        if (!material.Contract.Members.AsSpan().SequenceEqual(UberBaseParameterSchema.Members)) throw Invalid();
        if (!exports) return;
        Require(artifact, 2, 0, multisample ? "texture-multisampled-2d-uint" : "texture-2d-uint");
        Require(artifact, 2, 1, multisample ? "texture-multisampled-2d-uint" : "texture-2d-uint");
        Require(artifact, 2, 2, "storage-texture-2d-array-write-rgba32float");
        Require(artifact, 0, 5, "uniform", 1088);
        Require(artifact, 2, 3, "uniform", 48);
        for (uint binding = 4; binding < 11; binding++)
            Require(artifact, 2, binding, (binding & 1u) != 0 ? "filtering-sampler" :
                binding is 6 or 8 ? "texture-2d-array-float" : "texture-2d-float");
        for (uint binding = 20; binding < 25; binding++) Require(artifact, 2, binding, "read-only-storage", 4);
        Require(artifact, 3, 0, "uniform", 144);
        Require(artifact, 3, 1, "texture-depth-2d");
        Require(artifact, 3, 2, "comparison-sampler");
        Require(artifact, 3, 3, "uniform", 240);
        Require(artifact, 3, 4, "texture-2d-float");
        Require(artifact, 3, 5, "filtering-sampler");
        Require(artifact, 3, 6, "texture-cube-float");
        Require(artifact, 3, 7, "filtering-sampler");
    }

    private static ShaderStageResourceLayout Require(ShaderProgramArtifact artifact, uint group, uint binding, string type, uint bytes = 0)
    {
        foreach (ShaderStageResourceLayout resource in artifact.Resources)
            if (resource.Contract.Set == group && resource.Contract.Binding == binding)
            {
                if (resource.BindingType != type || resource.Contract.ByteSize != bytes || resource.DynamicOffset != (type == "uniform") ||
                    (resource.Visibility & ShaderStageVisibility.Compute) != 0 ||
                    type == "read-only-storage" && (!resource.RuntimeArray || !resource.Contract.Members.IsEmpty)) throw Invalid();
                return resource;
            }
        throw Invalid();
    }
    private static NotSupportedException Invalid()
        => new("WebGPU.Advanced.UberRasterAbiMismatch: recook the exact shared fragment sampling and full-float raster export family.");
}
