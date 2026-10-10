using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Validates the canonical tuple and f32 sample reduction companion interfaces.</summary>
internal static class WebGpuAdvancedMsaaProgramContract
{
    internal static void Validate(ShaderProgramArtifact artifact, string pass)
    {
        bool visibility = pass == "visibility-msaa-resolve";
        if ((!visibility && pass != "shade-msaa-resolve") || artifact.Pass != pass ||
            artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.Resources.Length != (visibility ? 4 : 8) ||
            !artifact.VertexBuffers.IsDefaultOrEmpty ||
            (visibility ? artifact.VertexEntryPoint is null || artifact.FragmentEntryPoint is null || artifact.ComputeEntryPoint is not null
                : artifact.VertexEntryPoint is not null || artifact.FragmentEntryPoint is not null || artifact.ComputeEntryPoint is null ||
                  artifact.ComputeWorkgroupSize != new ShaderComputeWorkgroupSize(16, 16, 1)))
            throw Invalid();
        ShaderStageVisibility stage = visibility ? ShaderStageVisibility.Fragment : ShaderStageVisibility.Compute;
        Require(artifact, 0, "RawVisibilityIdentity", "texture-multisampled-2d-uint", stage);
        Require(artifact, 1, "RawVisibilityMetadataSelection", "texture-multisampled-2d-uint", stage);
        if (visibility)
        {
            Require(artifact, 2, "RawVisibilityDepth", "texture-depth-multisampled-2d", stage);
            Require(artifact, 3, "Parameters", "uniform", stage, 16);
        }
        else
        {
            Require(artifact, 2, "SampleRadianceReactive", "texture-2d-array-unfilterable-float", stage);
            Require(artifact, 3, "HDRSceneColor", "storage-texture-2d-write-rgba16float", stage);
            Require(artifact, 4, "ReactiveMask", "storage-texture-2d-write-r32float", stage);
            Require(artifact, 5, "Velocity", "storage-texture-2d-write-rgba16float", stage);
            Require(artifact, 6, "ShadingDiagnostics", "storage-texture-2d-write-r32uint", stage);
            Require(artifact, 7, "Parameters", "uniform", stage, 32);
        }
    }

    private static void Require(ShaderProgramArtifact artifact, uint binding, string name, string type,
        ShaderStageVisibility stage, uint bytes = 0)
    {
        foreach (ShaderStageResourceLayout resource in artifact.Resources)
        {
            if (resource.Contract.Set != 0 || resource.Contract.Binding != binding) continue;
            if (resource.Contract.Name != name || resource.BindingType != type || resource.Visibility != stage ||
                resource.Contract.ByteSize != bytes || resource.DynamicOffset != (type == "uniform")) throw Invalid();
            return;
        }
        throw Invalid();
    }

    private static NotSupportedException Invalid()
        => new("WebGPU.Advanced.MultisampleAbiMismatch: the cooked companion must preserve the exact packed visibility and f32 sample reduction contract.");
}
