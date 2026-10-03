using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Checks fixed bank shape, storage access, uniform sizes, and GPU tile workgroup contracts before use.</summary>
internal static class WebGpuAdvancedShadingProgramContract
{
    internal static void Validate(ShaderProgramArtifact artifact, string pass)
    {
        bool native = pass is "shade-native" or "shade-surface-exports";
        bool exports = pass is "shade-surface-exports" or "shade-background-exports";
        bool classify = pass == "shade-classify", finalize = pass == "shade-finalize";
        bool background = pass is "shade-background" or "shade-background-exports";
        if (!native && !classify && !finalize && !background) throw Invalid();
        int count = native ? 41 : classify ? 7 : finalize ? 3 : exports ? 5 : 6;
        if (artifact.Pass != pass || artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.Resources.Length != count ||
            artifact.ComputeEntryPoint is null || artifact.VertexEntryPoint is not null || artifact.FragmentEntryPoint is not null ||
            artifact.ComputeWorkgroupSize != (finalize ? new ShaderComputeWorkgroupSize(64, 1, 1) : new ShaderComputeWorkgroupSize(16, 16, 1)))
            throw Invalid();
        if (native)
        {
            for (uint binding = 0; binding < 7; binding++) Require(artifact, 0, binding, "read-only-storage", 4);
            Require(artifact, 0, 7, "uniform", 944, "FrozenView");
            Require(artifact, 0, 8, "uniform", 160, "Parameters");
            Require(artifact, 1, 0, "texture-2d-uint", name: "VisibilityIdentity");
            Require(artifact, 1, 1, "texture-2d-uint", name: "VisibilityMetadata");
            Require(artifact, 1, 2, "texture-depth-2d", name: "VisibilityDepth");
            Require(artifact, 1, 3, "texture-2d-unfilterable-float", name: "AmbientOcclusion");
            for (uint slot = 0; slot < 12; slot++)
            {
                Require(artifact, 1, 4 + slot * 2, slot < 10 ? "texture-2d-float" : slot == 10 ? "texture-cube-float" : "texture-2d-array-float");
                Require(artifact, 1, 5 + slot * 2, "filtering-sampler");
            }
            for (uint binding = 0; binding < 4; binding++) Require(artifact, 2, binding, Output(binding, exports));
        }
        else if (classify)
        {
            Require(artifact, 0, 0, "read-only-storage", 4); Require(artifact, 0, 1, "read-only-storage", 4);
            Require(artifact, 0, 2, "storage", 4); Require(artifact, 0, 3, "storage", 4);
            Require(artifact, 0, 4, "uniform", 32, "Parameters");
            Require(artifact, 0, 5, "texture-2d-uint", name: "VisibilityIdentity");
            Require(artifact, 0, 6, "texture-2d-uint", name: "VisibilityMetadata");
        }
        else if (finalize)
        {
            Require(artifact, 0, 0, "read-only-storage", 4); Require(artifact, 0, 1, "storage", 4);
            Require(artifact, 0, 2, "uniform", 16, "Parameters");
        }
        else
        {
            if (!exports) Require(artifact, 0, 0, "texture-2d-uint", name: "VisibilityIdentity");
            for (uint binding = 0; binding < 4; binding++) Require(artifact, 0, binding + (exports ? 0u : 1u), Output(binding, exports));
            Require(artifact, 0, exports ? 4u : 5u, "uniform", exports ? 16u : 32u, "Parameters");
        }
    }
    private static string Output(uint binding, bool exports) => exports || binding < 2
        ? "storage-texture-2d-write-rgba16float" : binding == 2 ? "storage-texture-2d-write-r32float" : "storage-texture-2d-write-r32uint";
    private static void Require(ShaderProgramArtifact artifact, uint group, uint binding, string type, uint bytes = 0, string? name = null)
    {
        foreach (ShaderStageResourceLayout resource in artifact.Resources)
        {
            if (resource.Contract.Set != group || resource.Contract.Binding != binding) continue;
            bool buffer = type is "read-only-storage" or "storage";
            if (resource.BindingType != type || resource.Visibility != ShaderStageVisibility.Compute ||
                resource.Contract.ByteSize != bytes || resource.DynamicOffset != (type == "uniform") ||
                buffer && (!resource.RuntimeArray || !resource.Contract.Members.IsEmpty) || name is not null && resource.Contract.Name != name)
                throw Invalid();
            return;
        }
        throw Invalid();
    }
    private static NotSupportedException Invalid()
        => new("WebGPU.Advanced.ShadingAbiMismatch: the cooked program does not implement the exact native cohort and texture-bank contract.");
}
