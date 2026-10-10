using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Validates the exact bounded, mipless GPU exposure program consumed by shared pipeline commands.</summary>
internal static class WebGpuAutoExposureProgramContract
{
    internal static void Validate(ShaderProgramArtifact artifact)
    {
        if (artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.Pass != "auto-exposure" ||
            artifact.SemanticSchemaIdentity != "xrengine.engine.compute.v1" ||
            artifact.VertexEntryPoint is not null || artifact.FragmentEntryPoint is not null ||
            artifact.ComputeEntryPoint != "autoExposure" || artifact.ComputeWorkgroupSize != new ShaderComputeWorkgroupSize(256, 1, 1) ||
            artifact.Resources.Length != 4 || !artifact.VertexBuffers.IsEmpty)
            throw Invalid();
        ShaderStageResourceLayout source = Binding(artifact, 0);
        ShaderStageResourceLayout output = Binding(artifact, 1);
        ShaderStageResourceLayout history = Binding(artifact, 2);
        ShaderStageResourceLayout parameters = Binding(artifact, 3);
        if (source.Contract.Name != "SourceTex" || source.BindingType != "texture-2d-unfilterable-float" ||
            source.Contract.Kind != ShaderAbiResourceKind.SampledImage || source.DynamicOffset ||
            output.Contract.Name != "ExposureOut" || output.BindingType != "storage-texture-2d-write-r32float" ||
            output.Contract.Kind != ShaderAbiResourceKind.StorageImage || output.DynamicOffset ||
            history.Contract.Name != "History" || history.BindingType != "storage" ||
            history.Contract.Kind != ShaderAbiResourceKind.StorageBuffer || history.DynamicOffset ||
            !history.RuntimeArray || history.Contract.ByteSize != 4 || !history.Contract.Members.IsEmpty ||
            parameters.Contract.Kind != ShaderAbiResourceKind.UniformBuffer || parameters.BindingType != "uniform" ||
            !parameters.DynamicOffset || parameters.Contract.ByteSize != 64 || parameters.Contract.Members.Length != 14)
            throw Invalid();
        Member(parameters, "LuminanceWeights", 0, 12, "vec3<f32>");
        Member(parameters, "AutoExposureBias", 12);
        Member(parameters, "AutoExposureScale", 16);
        Member(parameters, "ExposureDividend", 20);
        Member(parameters, "MinExposure", 24);
        Member(parameters, "MaxExposure", 28);
        Member(parameters, "ExposureBase", 32);
        Member(parameters, "FallbackExposure", 36);
        Member(parameters, "ExposureTransitionSpeed", 40);
        Member(parameters, "MeteringMode", 44, 4, "u32");
        Member(parameters, "MeteringTargetSize", 48, 4, "u32");
        Member(parameters, "IgnoreTopPercent", 52);
        Member(parameters, "CenterWeightStrength", 56);
        Member(parameters, "CenterWeightPower", 60);
    }

    private static ShaderStageResourceLayout Binding(ShaderProgramArtifact artifact, uint binding)
    {
        foreach (ShaderStageResourceLayout resource in artifact.Resources)
            if (resource.Contract.Set == 0 && resource.Contract.Binding == binding && resource.Visibility == ShaderStageVisibility.Compute)
                return resource;
        throw Invalid();
    }

    private static void Member(ShaderStageResourceLayout resource, string provider, uint offset, uint size = 4, string type = "f32")
    {
        foreach (ShaderAbiMemberContract member in resource.Contract.Members)
            if (member.ProviderName == provider && member.Offset == offset && member.Size == size &&
                member.PhysicalType == type && member.ArrayCount == 0)
                return;
        throw Invalid();
    }

    private static NotSupportedException Invalid()
        => new("WebGPU.Exposure.ProgramAbiMismatch: the selected cooked program must implement the exact exposure metering, history and write-only R32F contract.");
}
