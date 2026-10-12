using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Validates the exact portable meshlet expansion, bounds-refit, and indexed-indirect publication ABIs.</summary>
internal static class WebGpuMeshletProgramContract
{
    internal const uint CullParameterByteSize = 224;
    internal const uint LodParameterByteSize = 96;
    internal const uint RefitParameterByteSize = 48;
    internal const uint FinalizeParameterByteSize = 16;
    internal const uint StateByteSize = 32;
    internal const uint DescriptorByteSize = 80;
    internal const uint PrimitiveMapEntriesPerMeshlet = 124;

    private static readonly string[] CullResources = ["Source", "Bounds", "Indices", "State", "SelectedLod", "Instances", "Parameters"];
    private static readonly string[] LodResources = ["SelectedLod", "Parameters"];
    private static readonly string[] RefitResources = ["Source", "Positions", "Bounds", "State", "Parameters"];
    private static readonly string[] FinalizeResources = ["State", "Parameters"];
    private static readonly string[] CullParameters =
    [
        "ModelMatrix", "ViewProjection", "MeshletCount", "SourceTriangleCount", "VertexCount", "IndexCapacity",
        "DescriptorWordOffset", "RemapWordOffset", "RemapCount", "TriangleWordOffset", "TriangleByteCount",
        "PrimitiveWordOffset", "PrimitiveCount", "BoundsWordOffset", "CullEnabled", "UseRefitBounds", "SphereExpansion", "DrawEnabled",
        "CandidateMeshId", "CandidateLod", "InstanceCount", "InstanceSourceEnabled", "InstanceStrideWords", "InstanceTransformOffsetWords", "InstanceBoundsOffsetWords", "InstanceWordCount",
    ];
    private static readonly string[] LodParameters =
    [
        "BoundsSphere", "CameraPosition", "ProjectionAndViewport", "Lod0MeshId", "Lod1MeshId", "Lod2MeshId", "Lod3MeshId",
        "Lod0MinRadius", "Lod1MinRadius", "Lod2MinRadius", "Lod3MinRadius", "LodCount", "CurrentMeshId", "CurrentLod", "Flags",
    ];
    private static readonly string[] RefitParameters =
    [
        "MeshletCount", "VertexCount", "PositionWordCount", "DescriptorWordOffset", "RemapWordOffset", "RemapCount",
        "BoundsWordOffset", "BoundsWordCount", "Reserved0", "Reserved1", "Reserved2", "Reserved3",
    ];
    private static readonly string[] FinalizeParameters = ["MeshletCount", "IndexCapacity", "SourceTriangleCount", "InstanceCount"];

    internal static void Validate(ShaderProgramArtifact artifact, string pass)
    {
        bool cull = pass == "cull-expand";
        bool lod = pass == "select-lod";
        bool refit = pass == "refit-bounds";
        if (!cull && !refit && !lod && pass != "finalize-indexed")
            throw Invalid();

        string name = lod ? "engine-meshlets-select-lod" : cull ? "engine-meshlets-cull-expand" : refit ? "engine-meshlets-refit-bounds" : "engine-meshlets-finalize-indexed";
        string entryPoint = lod ? "meshletsSelectLod" : cull ? "meshletsCullExpand" : refit ? "meshletsRefitBounds" : "meshletsFinalizeIndexed";
        string[] resources = lod ? LodResources : cull ? CullResources : refit ? RefitResources : FinalizeResources;
        string[] providers = lod ? LodParameters : cull ? CullParameters : refit ? RefitParameters : FinalizeParameters;
        uint uniformSize = lod ? LodParameterByteSize : cull ? CullParameterByteSize : refit ? RefitParameterByteSize : FinalizeParameterByteSize;
        uint workgroupSize = cull || refit ? 64u : 1u;
        int matrixCount = cull ? 2 : 0;
        int vectorCount = lod ? 3 : 0;
        int uniformBinding = resources.Length - 1;

        if (artifact.DescriptorBytes.IsDefaultOrEmpty || artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.SourceLanguage != "WGSL" ||
            artifact.Name != name || artifact.Pass != pass || artifact.SemanticSchemaIdentity != "xrengine.engine.compute.v1" ||
            artifact.Coordinates != "xrengine.webgpu.coordinates.v1" || artifact.ComputeEntryPoint != entryPoint ||
            artifact.VertexEntryPoint is not null || artifact.FragmentEntryPoint is not null ||
            artifact.ComputeWorkgroupSize != new ShaderComputeWorkgroupSize(workgroupSize, 1, 1) ||
            artifact.Resources.Length != resources.Length || !artifact.VertexBuffers.IsDefaultOrEmpty)
            throw Invalid();

        for (int binding = 0; binding < resources.Length; binding++)
        {
            ShaderStageResourceLayout resource = Binding(artifact, checked((uint)binding));
            ShaderAbiResourceContract contract = resource.Contract;
            if (contract.Name != resources[binding] || !MatchesPhysicalName(contract.PhysicalName, resources[binding]) ||
                contract.Owner != ShaderAbiResourceOwner.Engine || contract.DescriptorLifetime is not null)
                throw Invalid();
            if (binding != uniformBinding)
            {
                string bindingType = (cull || refit) && binding < 2 || cull && binding is 4 or 5 ? "read-only-storage" : "storage";
                if (resource.BindingType != bindingType || resource.DynamicOffset || !resource.RuntimeArray ||
                    contract.Kind != ShaderAbiResourceKind.StorageBuffer || contract.Frequency != ShaderAbiFrequency.Frame ||
                    contract.ByteSize != 4 || !contract.Members.IsEmpty)
                    throw Invalid();
                continue;
            }

            if (resource.BindingType != "uniform" || !resource.DynamicOffset || resource.RuntimeArray ||
                contract.Kind != ShaderAbiResourceKind.UniformBuffer || contract.Frequency != ShaderAbiFrequency.View ||
                contract.ByteSize != uniformSize || contract.Members.Length != providers.Length)
                throw Invalid();
            for (int index = 0; index < providers.Length; index++)
            {
                ShaderAbiMemberContract member = contract.Members[index];
                bool matrix = index < matrixCount;
                bool vector = index < vectorCount;
                uint offset = checked((uint)(matrix ? index * 64 : vector ? index * 16 :
                    matrixCount * 64 + vectorCount * 16 + (index - matrixCount - vectorCount) * 4));
                string physicalType = matrix ? "mat4x4<f32>" : vector ? "vec4<f32>" :
                    providers[index] == "SphereExpansion" || lod && index is >= 7 and <= 10 ? "f32" : "u32";
                if (member.ProviderName != providers[index] || !MatchesPhysicalName(member.PhysicalName, providers[index]) ||
                    member.Offset != offset || member.Size != (matrix ? 64u : vector ? 16u : 4u) || member.PhysicalType != physicalType ||
                    member.ArrayCount != 0 || member.ArrayStride != 0 || member.CpuFieldName is not null ||
                    member.MatrixOrder != (matrix ? ShaderAbiMatrixOrder.ColumnMajor : ShaderAbiMatrixOrder.None) ||
                    member.MatrixStride != (matrix ? 16u : 0u))
                    throw Invalid();
            }
        }

        Limit(artifact, "maxBindGroups", 1);
        Limit(artifact, "maxBindingsPerBindGroup", resources.Length);
        Limit(artifact, "maxUniformBufferBindingSize", checked((int)uniformSize));
        Limit(artifact, "maxDynamicUniformBuffersPerPipelineLayout", 1);
        Limit(artifact, "maxUniformBuffersPerShaderStage", 1);
        Limit(artifact, "maxStorageBuffersPerShaderStage", uniformBinding);
        Limit(artifact, "maxStorageBufferBindingSize", 4);
        Limit(artifact, "maxComputeWorkgroupSizeX", checked((int)workgroupSize));
        Limit(artifact, "maxComputeWorkgroupSizeY", 1);
        Limit(artifact, "maxComputeWorkgroupSizeZ", 1);
        Limit(artifact, "maxComputeInvocationsPerWorkgroup", checked((int)workgroupSize));
        if (cull || refit)
            Limit(artifact, "maxComputeWorkgroupStorageSize", cull ? 32 : 2320);
        if (artifact.RequiredLimits.Count != (cull || refit ? 12 : 11))
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
        => new("WebGPU.Meshlet.ProgramAbiMismatch: the cooked program must implement the exact canonical meshlet expansion, bounds-refit, or indexed-indirect finalization contract.");
}
