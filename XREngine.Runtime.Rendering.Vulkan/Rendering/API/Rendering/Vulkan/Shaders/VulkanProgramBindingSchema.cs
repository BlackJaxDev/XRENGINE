namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Versioned value and resource binding contract published by one successful
/// Vulkan program link.
/// </summary>
internal sealed class VulkanProgramBindingSchema
{
    private readonly Dictionary<string, VulkanAutoUniformBindingSchema> _autoUniformBlocks;
    private readonly ulong[] _frequencyPublicationLayoutSignatures;

    private VulkanProgramBindingSchema(
        ulong programLinkGeneration,
        Dictionary<string, VulkanAutoUniformBindingSchema> autoUniformBlocks,
        VulkanDescriptorBindingSchemaEntry[] descriptorBindings)
    {
        ProgramLinkGeneration = programLinkGeneration;
        _autoUniformBlocks = autoUniformBlocks;
        _frequencyPublicationLayoutSignatures =
            BuildFrequencyPublicationLayoutSignatures(autoUniformBlocks);
        DescriptorBindings = descriptorBindings;
    }

    internal ulong ProgramLinkGeneration { get; }
    internal IReadOnlyDictionary<string, VulkanAutoUniformBindingSchema> AutoUniformBlocks
        => _autoUniformBlocks;
    internal VulkanDescriptorBindingSchemaEntry[] DescriptorBindings { get; }

    internal static VulkanProgramBindingSchema Compile(
        ulong programLinkGeneration,
        IReadOnlyDictionary<string, AutoUniformBlockInfo> autoUniformBlocks,
        IReadOnlyList<DescriptorBindingInfo> descriptorBindings)
    {
        Dictionary<string, VulkanAutoUniformBindingSchema> valueSchemas =
            new(autoUniformBlocks.Count, StringComparer.Ordinal);
        int valueOperationCount = 0;
        int fallbackOperationCount = 0;
        foreach (KeyValuePair<string, AutoUniformBlockInfo> pair in autoUniformBlocks)
        {
            VulkanAutoUniformBindingSchema valueSchema =
                VulkanAutoUniformBindingSchema.Compile(
                    pair.Value,
                    programLinkGeneration);
            valueSchemas.Add(pair.Key, valueSchema);
            valueOperationCount += valueSchema.Operations.Length;
            for (int operationIndex = 0;
                 operationIndex < valueSchema.Operations.Length;
                 operationIndex++)
            {
                if (!valueSchema.Operations[operationIndex].IsFastPathEligible)
                {
                    fallbackOperationCount++;
                    RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformFallbackReason(
                        valueSchema.Operations[operationIndex].FallbackKind);
                }
            }
        }

        VulkanDescriptorBindingSchemaEntry[] resourceSchemas =
            new VulkanDescriptorBindingSchemaEntry[descriptorBindings.Count];
        for (int bindingIndex = 0; bindingIndex < descriptorBindings.Count; bindingIndex++)
        {
            DescriptorBindingInfo binding = descriptorBindings[bindingIndex];
            resourceSchemas[bindingIndex] = new VulkanDescriptorBindingSchemaEntry(
                binding,
                ResolveDescriptorOwner(binding, autoUniformBlocks),
                binding.Count > 1
                    ? EVulkanDescriptorArrayPolicy.FixedCount
                    : EVulkanDescriptorArrayPolicy.Single,
                DependsOnTopologyGeneration: true,
                DependsOnContentGeneration: true);
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanBindingSchemaCompiled(
            valueOperationCount,
            resourceSchemas.Length,
            fallbackOperationCount);
        return new VulkanProgramBindingSchema(
            programLinkGeneration,
            valueSchemas,
            resourceSchemas);
    }

    internal bool TryGetAutoUniformBlock(
        string instanceName,
        out VulkanAutoUniformBindingSchema schema)
        => _autoUniformBlocks.TryGetValue(instanceName, out schema!);

    internal ulong GetFrequencyPublicationLayoutSignature(
        EVulkanBindingFrequency frequency)
    {
        int index = (int)frequency;
        return (uint)index < (uint)_frequencyPublicationLayoutSignatures.Length
            ? _frequencyPublicationLayoutSignatures[index]
            : 0UL;
    }

    private static ulong[] BuildFrequencyPublicationLayoutSignatures(
        Dictionary<string, VulkanAutoUniformBindingSchema> schemas)
    {
        int frequencyCount = (int)EVulkanBindingFrequency.Count;
        List<ulong>[] signaturesByFrequency = new List<ulong>[frequencyCount];
        foreach (VulkanAutoUniformBindingSchema schema in schemas.Values)
        {
            int index = (int)schema.Block.Frequency;
            if ((uint)index >= (uint)frequencyCount)
                continue;

            (signaturesByFrequency[index] ??= [])
                .Add(schema.PublicationLayoutSignature);
        }

        ulong[] signatures = new ulong[frequencyCount];
        for (int frequencyIndex = 0;
             frequencyIndex < frequencyCount;
             frequencyIndex++)
        {
            List<ulong>? frequencySignatures =
                signaturesByFrequency[frequencyIndex];
            if (frequencySignatures is null)
                continue;

            frequencySignatures.Sort();
            FrameOpSignatureHasher hash = new();
            hash.Add(frequencySignatures.Count);
            for (int index = 0;
                 index < frequencySignatures.Count;
                 index++)
            {
                hash.Add(frequencySignatures[index]);
            }

            signatures[frequencyIndex] = hash.ToHash();
        }

        return signatures;
    }

    private static EVulkanDescriptorOwner ResolveDescriptorOwner(
        DescriptorBindingInfo binding,
        IReadOnlyDictionary<string, AutoUniformBlockInfo> autoUniformBlocks)
    {
        foreach (AutoUniformBlockInfo block in autoUniformBlocks.Values)
        {
            if (block.Set != binding.Set ||
                block.Binding != binding.Binding)
            {
                continue;
            }

            return block.Frequency switch
            {
                EVulkanBindingFrequency.Frame =>
                    EVulkanDescriptorOwner.Frame,
                EVulkanBindingFrequency.View =>
                    EVulkanDescriptorOwner.View,
                EVulkanBindingFrequency.Pass =>
                    EVulkanDescriptorOwner.Pass,
                EVulkanBindingFrequency.Material =>
                    EVulkanDescriptorOwner.Material,
                EVulkanBindingFrequency.Object =>
                    EVulkanDescriptorOwner.Object,
                EVulkanBindingFrequency.Instance =>
                    EVulkanDescriptorOwner.Instance,
                EVulkanBindingFrequency.RuntimeCallback =>
                    EVulkanDescriptorOwner.RuntimeCallback,
                _ => EVulkanDescriptorOwner.Globals,
            };
        }

        return binding.Set switch
        {
            VulkanRenderer.DescriptorSetGlobals => EVulkanDescriptorOwner.Globals,
            VulkanRenderer.DescriptorSetCompute => EVulkanDescriptorOwner.Compute,
            VulkanRenderer.DescriptorSetMaterial => EVulkanDescriptorOwner.Material,
            VulkanRenderer.DescriptorSetPerPass => EVulkanDescriptorOwner.Pass,
            _ => throw new InvalidOperationException(
                $"Descriptor set {binding.Set} is outside the linked Vulkan tier contract."),
        };
    }
}
