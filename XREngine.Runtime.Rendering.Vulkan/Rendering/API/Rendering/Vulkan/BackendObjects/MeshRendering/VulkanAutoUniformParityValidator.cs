namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Validation-only byte equivalence for the legacy and frequency-owned
/// auto-uniform serializers.
/// </summary>
internal static class VulkanAutoUniformParityValidator
{
    internal static bool TryFindMismatch(
        ReadOnlySpan<byte> legacy,
        ReadOnlySpan<byte> packed,
        VulkanAutoUniformBindingSchema schema,
        out VulkanAutoUniformParityMismatch mismatch)
    {
        if (legacy.Length != packed.Length)
        {
            throw new ArgumentException(
                "Legacy and packed payloads must describe the same block size.");
        }

        for (int byteOffset = 0; byteOffset < legacy.Length; byteOffset++)
        {
            if (legacy[byteOffset] == packed[byteOffset])
                continue;

            ResolveSchemaEntry(
                schema,
                byteOffset,
                out EVulkanBindingFrequency frequency,
                out string schemaEntry);
            mismatch = new VulkanAutoUniformParityMismatch(
                byteOffset,
                legacy[byteOffset],
                packed[byteOffset],
                frequency,
                schemaEntry);
            return true;
        }

        mismatch = default;
        return false;
    }

    private static void ResolveSchemaEntry(
        VulkanAutoUniformBindingSchema schema,
        int byteOffset,
        out EVulkanBindingFrequency frequency,
        out string schemaEntry)
    {
        VulkanAutoUniformBindingOperation[] operations = schema.Operations;
        for (int i = 0; i < operations.Length; i++)
        {
            VulkanAutoUniformBindingOperation operation = operations[i];
            AutoUniformMember member = operation.Member;
            if ((uint)byteOffset < member.Offset ||
                (uint)byteOffset >= member.Offset + member.Size)
            {
                continue;
            }

            frequency = operation.Frequency;
            schemaEntry = member.Name;
            return;
        }

        frequency = schema.Block.Frequency;
        schemaEntry = "$padding-or-static-default";
    }
}
