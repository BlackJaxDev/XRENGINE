using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Validates the exact whole-primitive authored indexed-indirect publication ABI.</summary>
internal static class WebGpuIndirectProgramContract
{
    internal const uint CullParameterByteSize = 208;
    internal const uint ArgumentByteSize = 20;
    internal const uint MaximumRefitVertexCount = 1 << 20;

    private static readonly string[] Resources = ["SelectedLod", "Arguments", "Positions", "Instances", "Parameters"];
    private static readonly string[] Parameters =
    [
        "ModelMatrix", "ViewProjection", "IndexCount", "CandidateMeshId", "CandidateLod", "DrawEnabled",
        "CullEnabled", "SphereExpansion", "VertexCount", "PositionWordCount", "PositionStrideWords", "PositionOffsetWords",
        "InstanceCount", "InstanceSourceEnabled", "InstanceStrideWords", "InstanceTransformOffsetWords", "InstanceBoundsOffsetWords", "InstanceWordCount",
        "Reserved0", "Reserved1", "Reserved2", "Reserved3",
    ];

    internal static void Validate(ShaderProgramArtifact artifact, string pass = "cull-primitive")
    {
        if (pass != "cull-primitive" || artifact.DescriptorBytes.IsDefaultOrEmpty ||
            artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.SourceLanguage != "WGSL" ||
            artifact.Name != "engine-indirect-cull-primitive" || artifact.Pass != pass ||
            artifact.SemanticSchemaIdentity != "xrengine.engine.compute.v1" ||
            artifact.Coordinates != "xrengine.webgpu.coordinates.v1" || artifact.ComputeEntryPoint != "indirectCullPrimitive" ||
            artifact.VertexEntryPoint is not null || artifact.FragmentEntryPoint is not null ||
            artifact.ComputeWorkgroupSize != new ShaderComputeWorkgroupSize(64, 1, 1) ||
            artifact.Resources.Length != Resources.Length || !artifact.VertexBuffers.IsDefaultOrEmpty)
            throw Invalid();

        for (int binding = 0; binding < Resources.Length; binding++)
        {
            ShaderStageResourceLayout resource = Binding(artifact, checked((uint)binding));
            ShaderAbiResourceContract contract = resource.Contract;
            if (contract.Name != Resources[binding] || !MatchesPhysicalName(contract.PhysicalName, Resources[binding]) ||
                contract.Owner != ShaderAbiResourceOwner.Engine || contract.DescriptorLifetime is not null)
                throw Invalid();
            if (binding < 4)
            {
                string bindingType = binding == 1 ? "storage" : "read-only-storage";
                if (resource.BindingType != bindingType || resource.DynamicOffset || !resource.RuntimeArray ||
                    contract.Kind != ShaderAbiResourceKind.StorageBuffer || contract.Frequency != ShaderAbiFrequency.Frame ||
                    contract.ByteSize != 4 || !contract.Members.IsEmpty)
                    throw Invalid();
                continue;
            }

            if (resource.BindingType != "uniform" || !resource.DynamicOffset || resource.RuntimeArray ||
                contract.Kind != ShaderAbiResourceKind.UniformBuffer || contract.Frequency != ShaderAbiFrequency.View ||
                contract.ByteSize != CullParameterByteSize || contract.Members.Length != Parameters.Length)
                throw Invalid();
            for (int index = 0; index < Parameters.Length; index++)
            {
                ShaderAbiMemberContract member = contract.Members[index];
                bool matrix = index < 2;
                uint offset = checked((uint)(matrix ? index * 64 : 128 + (index - 2) * 4));
                string physicalType = matrix ? "mat4x4<f32>" :
                    Parameters[index] == "SphereExpansion" ? "f32" : "u32";
                if (member.ProviderName != Parameters[index] || !MatchesPhysicalName(member.PhysicalName, Parameters[index]) ||
                    member.Offset != offset || member.Size != (matrix ? 64u : 4u) || member.PhysicalType != physicalType ||
                    member.ArrayCount != 0 || member.ArrayStride != 0 || member.CpuFieldName is not null ||
                    member.MatrixOrder != (matrix ? ShaderAbiMatrixOrder.ColumnMajor : ShaderAbiMatrixOrder.None) ||
                    member.MatrixStride != (matrix ? 16u : 0u))
                    throw Invalid();
            }
        }

        Limit(artifact, "maxBindGroups", 1);
        Limit(artifact, "maxBindingsPerBindGroup", Resources.Length);
        Limit(artifact, "maxUniformBufferBindingSize", checked((int)CullParameterByteSize));
        Limit(artifact, "maxDynamicUniformBuffersPerPipelineLayout", 1);
        Limit(artifact, "maxUniformBuffersPerShaderStage", 1);
        Limit(artifact, "maxStorageBuffersPerShaderStage", 4);
        Limit(artifact, "maxStorageBufferBindingSize", 4);
        Limit(artifact, "maxComputeWorkgroupSizeX", 64);
        Limit(artifact, "maxComputeWorkgroupSizeY", 1);
        Limit(artifact, "maxComputeWorkgroupSizeZ", 1);
        Limit(artifact, "maxComputeInvocationsPerWorkgroup", 64);
        Limit(artifact, "maxComputeWorkgroupStorageSize", 2304);
        if (artifact.RequiredLimits.Count != 12)
            throw Invalid();
    }

    private static bool MatchesPhysicalName(string physicalName, string providerName)
        => physicalName.Length == providerName.Length && physicalName[0] == char.ToLowerInvariant(providerName[0]) &&
           physicalName.AsSpan(1).SequenceEqual(providerName.AsSpan(1));

    private static ShaderStageResourceLayout Binding(ShaderProgramArtifact artifact, uint binding)
    {
        foreach (ShaderStageResourceLayout resource in artifact.Resources)
            if (resource.Contract.Set == 0 && resource.Contract.Binding == binding && resource.Visibility == ShaderStageVisibility.Compute)
                return resource;
        throw Invalid();
    }

    private static void Limit(ShaderProgramArtifact artifact, string name, int expected)
    {
        if (!artifact.RequiredLimits.TryGetValue(name, out int actual) || actual != expected)
            throw Invalid();
    }

    private static NotSupportedException Invalid()
        => new("WebGPU.Indirect.ProgramAbiMismatch: the cooked program must implement the exact whole-primitive authored indexed-indirect publication contract.");
}
