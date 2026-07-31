namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Compiled write plan for one material revision and reflected auto-uniform
/// block. <see cref="StaticBytes"/> contains only material-owned values; the
/// small dynamic member list is patched for each draw after the bytes are
/// copied into its stable frame-slot range.
/// </summary>
internal sealed class AutoUniformMaterialWritePlan(
    VulkanAutoUniformBindingSchema schema,
    ulong materialLayoutVersion,
    ulong materialValueVersion,
    ulong runtimeUniformNameSignature,
    ulong runtimeUniformPublicationLayoutSignature,
    byte[] staticBytes,
    VulkanAutoUniformBindingOperation[] dynamicOperations)
{
    private readonly VulkanAutoUniformFrequencyPlan[] _frequencyPlans =
        BuildFrequencyPlans(dynamicOperations);

    internal VulkanAutoUniformBindingSchema Schema { get; } = schema;
    internal ulong ProgramLinkGeneration => Schema.ProgramLinkGeneration;
    internal ulong PublicationLayoutSignature
        => Schema.PublicationLayoutSignature;
    internal ulong PublicationIdentity { get; } =
        ComputePublicationIdentity(
            schema,
            materialLayoutVersion,
            materialValueVersion,
            runtimeUniformNameSignature,
            runtimeUniformPublicationLayoutSignature);
    internal ulong MaterialLayoutVersion { get; } = materialLayoutVersion;
    internal ulong MaterialValueVersion { get; } = materialValueVersion;
    internal ulong RuntimeUniformNameSignature { get; } = runtimeUniformNameSignature;
    internal ulong RuntimeUniformPublicationLayoutSignature { get; } =
        runtimeUniformPublicationLayoutSignature;
    internal byte[] StaticBytes { get; } = staticBytes;
    internal VulkanAutoUniformBindingOperation[] DynamicOperations { get; } = dynamicOperations;

    internal ReadOnlySpan<VulkanAutoUniformBindingOperation> GetOperations(
        EVulkanBindingFrequency frequency)
        => GetFrequencyPlan(frequency).Operations;

    internal VulkanAutoUniformFrequencyPlan GetFrequencyPlan(
        EVulkanBindingFrequency frequency)
    {
        int index = (int)frequency;
        if ((uint)index >= (uint)_frequencyPlans.Length)
            throw new ArgumentOutOfRangeException(nameof(frequency));

        return _frequencyPlans[index];
    }

    private static ulong ComputePublicationIdentity(
        VulkanAutoUniformBindingSchema schema,
        ulong materialLayoutVersion,
        ulong materialValueVersion,
        ulong runtimeUniformNameSignature,
        ulong runtimeUniformPublicationLayoutSignature)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(schema.PublicationLayoutSignature);
        bool materialOwned =
            schema.Block.Frequency is EVulkanBindingFrequency.Unknown or
                EVulkanBindingFrequency.Material;
        hash.Add(materialOwned);
        if (materialOwned)
        {
            hash.Add(materialLayoutVersion);
            hash.Add(materialValueVersion);
            hash.Add(runtimeUniformNameSignature);
            hash.Add(runtimeUniformPublicationLayoutSignature);
        }

        return hash.ToHash();
    }

    private static VulkanAutoUniformFrequencyPlan[] BuildFrequencyPlans(
        VulkanAutoUniformBindingOperation[] operations)
    {
        int frequencyCount = (int)EVulkanBindingFrequency.Count;
        int[] counts = new int[frequencyCount];
        for (int operationIndex = 0;
             operationIndex < operations.Length;
             operationIndex++)
        {
            int frequencyIndex = (int)operations[operationIndex].Frequency;
            if ((uint)frequencyIndex >= (uint)frequencyCount)
                frequencyIndex = (int)EVulkanBindingFrequency.RuntimeCallback;
            counts[frequencyIndex]++;
        }

        VulkanAutoUniformBindingOperation[][] grouped =
            new VulkanAutoUniformBindingOperation[frequencyCount][];
        for (int frequencyIndex = 0;
             frequencyIndex < frequencyCount;
             frequencyIndex++)
        {
            grouped[frequencyIndex] =
                new VulkanAutoUniformBindingOperation[counts[frequencyIndex]];
        }

        Array.Clear(counts);
        for (int operationIndex = 0;
             operationIndex < operations.Length;
             operationIndex++)
        {
            VulkanAutoUniformBindingOperation operation =
                operations[operationIndex];
            int frequencyIndex = (int)operation.Frequency;
            if ((uint)frequencyIndex >= (uint)frequencyCount)
                frequencyIndex = (int)EVulkanBindingFrequency.RuntimeCallback;
            grouped[frequencyIndex][counts[frequencyIndex]++] = operation;
        }

        VulkanAutoUniformFrequencyPlan[] plans =
            new VulkanAutoUniformFrequencyPlan[frequencyCount];
        for (int frequencyIndex = 0;
             frequencyIndex < frequencyCount;
             frequencyIndex++)
        {
            VulkanAutoUniformBindingOperation[] frequencyOperations =
                grouped[frequencyIndex];
            plans[frequencyIndex] = new VulkanAutoUniformFrequencyPlan(
                (EVulkanBindingFrequency)frequencyIndex,
                frequencyOperations,
                BuildDirtyRanges(frequencyOperations));
        }

        return plans;
    }

    private static VulkanAutoUniformDirtyRange[] BuildDirtyRanges(
        VulkanAutoUniformBindingOperation[] operations)
    {
        if (operations.Length == 0)
            return [];

        VulkanAutoUniformDirtyRange[] sorted =
            new VulkanAutoUniformDirtyRange[operations.Length];
        for (int operationIndex = 0;
             operationIndex < operations.Length;
             operationIndex++)
        {
            AutoUniformMember member = operations[operationIndex].Member;
            sorted[operationIndex] = new VulkanAutoUniformDirtyRange(
                member.Offset,
                member.Size);
        }

        Array.Sort(
            sorted,
            static (left, right) => left.Offset.CompareTo(right.Offset));

        int rangeCount = 0;
        for (int sourceIndex = 0;
             sourceIndex < sorted.Length;
             sourceIndex++)
        {
            VulkanAutoUniformDirtyRange source = sorted[sourceIndex];
            if (source.Size == 0)
                continue;

            if (rangeCount == 0)
            {
                sorted[rangeCount++] = source;
                continue;
            }

            ref VulkanAutoUniformDirtyRange previous =
                ref sorted[rangeCount - 1];
            if (source.Offset > previous.End)
            {
                sorted[rangeCount++] = source;
                continue;
            }

            uint mergedEnd = Math.Max(previous.End, source.End);
            previous = new VulkanAutoUniformDirtyRange(
                previous.Offset,
                mergedEnd - previous.Offset);
        }

        if (rangeCount == sorted.Length)
            return sorted;

        VulkanAutoUniformDirtyRange[] compact =
            new VulkanAutoUniformDirtyRange[rangeCount];
        sorted.AsSpan(0, rangeCount).CopyTo(compact);
        return compact;
    }
}
