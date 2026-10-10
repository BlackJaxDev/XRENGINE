using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Checks the complete cooked binding ABI consumed by native Advanced depth primitives.</summary>
internal static class WebGpuAdvancedDepthProgramContract
{
    public static void Validate(ShaderProgramArtifact artifact, bool ambientOcclusion)
    {
        ShaderComputeWorkgroupSize workgroup = ambientOcclusion ? new(16, 16, 1) : new(256, 1, 1);
        if (artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.VertexEntryPoint is not null ||
            artifact.FragmentEntryPoint is not null || artifact.ComputeEntryPoint is null ||
            artifact.ComputeWorkgroupSize != workgroup || artifact.Resources.Length != 3)
            throw Invalid();
        ShaderStageResourceLayout depth = GetBinding(artifact, 0);
        ShaderStageResourceLayout output = GetBinding(artifact, 1);
        ShaderStageResourceLayout parameters = GetBinding(artifact, 2);
        if (depth.Contract.Name != "VisibilityDepth" || depth.BindingType != "texture-depth-2d" ||
            depth.Contract.Kind != ShaderAbiResourceKind.SampledImage || depth.DynamicOffset ||
            output.Contract.Name != "Output" || output.BindingType != "storage-texture-2d-write-r32float" ||
            output.Contract.Kind != ShaderAbiResourceKind.StorageImage || output.DynamicOffset ||
            parameters.BindingType != "uniform" || !parameters.DynamicOffset ||
            parameters.Contract.Kind != ShaderAbiResourceKind.UniformBuffer ||
            parameters.Contract.ByteSize != (ambientOcclusion ? 240u : 16u) ||
            parameters.Contract.Members.Length != (ambientOcclusion ? 7 : 3))
            throw Invalid();
        if (ambientOcclusion)
        {
            RequireMember(parameters, "View", 0, 64, "mat4x4<f32>");
            RequireMember(parameters, "InverseViewProjection", 64, 64, "mat4x4<f32>");
            RequireMember(parameters, "ProjectionUnjittered", 128, 64, "mat4x4<f32>");
            RequireMember(parameters, "RenderSizeAndInverse", 192, 16, "vec4<f32>");
            RequireMember(parameters, "DepthParams", 208, 16, "vec4<f32>");
            RequireMember(parameters, "ViewFlags", 224, 4, "u32");
            RequireMember(parameters, "AoEnabled", 228, 4, "u32");
        }
        else
        {
            RequireMember(parameters, "Extent", 0, 8, "vec2<f32>");
            RequireMember(parameters, "ReversedDepth", 8, 4, "u32");
            RequireMember(parameters, "Reserved", 12, 4, "u32");
        }
    }

    private static ShaderStageResourceLayout GetBinding(ShaderProgramArtifact artifact, uint binding)
    {
        foreach (ShaderStageResourceLayout resource in artifact.Resources)
            if (resource.Contract.Set == 0 && resource.Contract.Binding == binding &&
                resource.Visibility == ShaderStageVisibility.Compute)
                return resource;
        throw Invalid();
    }

    private static void RequireMember(ShaderStageResourceLayout resource, string provider, uint offset, uint size, string type)
    {
        foreach (ShaderAbiMemberContract member in resource.Contract.Members)
            if (member.ProviderName == provider && member.Offset == offset && member.Size == size &&
                member.PhysicalType == type && member.ArrayCount == 0 &&
                (type != "mat4x4<f32>" || member.MatrixOrder == ShaderAbiMatrixOrder.ColumnMajor && member.MatrixStride == 16))
                return;
        throw Invalid();
    }

    private static NotSupportedException Invalid()
        => new("WebGPU.Advanced.DepthProgramAbiMismatch: the cooked program does not implement the exact native depth primitive binding/workgroup contract.");
}
