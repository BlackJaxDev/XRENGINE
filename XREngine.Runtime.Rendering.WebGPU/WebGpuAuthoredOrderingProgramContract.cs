using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Validates the bounded authored source ranking and indexed argument masking ABIs.</summary>
internal static class WebGpuAuthoredOrderingProgramContract
{
    internal const uint MaximumSourceCount = 64;
    internal const uint SourceByteSize = 64;
    internal const uint RankByteSize = 4;
    internal const uint ArgumentByteSize = 20;
    internal const uint RankParameterByteSize = 32;
    internal const uint MaskParameterByteSize = 16;

    private static readonly string[] RankResources = ["Sources", "Ranks", "Parameters"];
    private static readonly string[] MaskResources = ["Ranks", "OriginalArguments", "RankedArguments", "Parameters"];
    private static readonly string[] RankParameters = ["CameraPosition", "SourceCount", "SortPolicy", "UsePriority", "Reserved"];
    private static readonly string[] MaskParameters = ["SourceIndex", "SourceCount", "RankCount", "Reserved"];

    internal static void Validate(ShaderProgramArtifact artifact, string pass)
    {
        bool rank = pass == "rank-sources";
        if (!rank && pass != "mask-ranked-arguments")
            throw Invalid();

        string[] resources = rank ? RankResources : MaskResources;
        string[] parameters = rank ? RankParameters : MaskParameters;
        string name = rank ? "engine-authored-rank-sources" : "engine-authored-mask-ranked-arguments";
        string entryPoint = rank ? "authoredRankSources" : "authoredMaskRankedArguments";
        uint uniformSize = rank ? RankParameterByteSize : MaskParameterByteSize;
        int uniformBinding = resources.Length - 1;

        if (artifact.DescriptorBytes.IsDefaultOrEmpty || artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.SourceLanguage != "WGSL" ||
            artifact.Name != name || artifact.Pass != pass || artifact.SemanticSchemaIdentity != "xrengine.engine.compute.v1" ||
            artifact.Coordinates != "xrengine.webgpu.coordinates.v1" || artifact.ComputeEntryPoint != entryPoint ||
            artifact.VertexEntryPoint is not null || artifact.FragmentEntryPoint is not null ||
            artifact.ComputeWorkgroupSize != new ShaderComputeWorkgroupSize(64, 1, 1) ||
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
                bool writable = rank ? binding == 1 : binding == 2;
                if (resource.BindingType != (writable ? "storage" : "read-only-storage") || resource.DynamicOffset || !resource.RuntimeArray ||
                    contract.Kind != ShaderAbiResourceKind.StorageBuffer || contract.Frequency != ShaderAbiFrequency.Frame ||
                    contract.ByteSize != 4 || !contract.Members.IsEmpty)
                    throw Invalid();
                continue;
            }

            if (resource.BindingType != "uniform" || !resource.DynamicOffset || resource.RuntimeArray ||
                contract.Kind != ShaderAbiResourceKind.UniformBuffer || contract.Frequency != ShaderAbiFrequency.View ||
                contract.ByteSize != uniformSize || contract.Members.Length != parameters.Length)
                throw Invalid();
            for (int index = 0; index < parameters.Length; index++)
            {
                ShaderAbiMemberContract member = contract.Members[index];
                bool vector = rank && index == 0;
                uint offset = checked((uint)(vector ? 0 : rank ? 16 + (index - 1) * 4 : index * 4));
                if (member.ProviderName != parameters[index] || !MatchesPhysicalName(member.PhysicalName, parameters[index]) ||
                    member.Offset != offset || member.Size != (vector ? 16u : 4u) ||
                    member.PhysicalType != (vector ? "vec4<f32>" : "u32") || member.ArrayCount != 0 ||
                    member.ArrayStride != 0 || member.CpuFieldName is not null || member.MatrixOrder != ShaderAbiMatrixOrder.None ||
                    member.MatrixStride != 0)
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
        Limit(artifact, "maxComputeWorkgroupSizeX", 64);
        Limit(artifact, "maxComputeWorkgroupSizeY", 1);
        Limit(artifact, "maxComputeWorkgroupSizeZ", 1);
        Limit(artifact, "maxComputeInvocationsPerWorkgroup", 64);
        if (artifact.RequiredLimits.Count != 11)
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
        => new("WebGPU.AuthoredOrdering.ProgramAbiMismatch: the cooked program must implement the exact bounded authored source ranking or indexed argument masking contract.");
}
