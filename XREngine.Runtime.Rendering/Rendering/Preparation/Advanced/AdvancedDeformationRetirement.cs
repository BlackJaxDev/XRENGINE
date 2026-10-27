namespace XREngine.Rendering;

/// <summary>Scans preallocated deformation storage and completion values.</summary>
internal static class AdvancedDeformationRetirement
{
    /// <summary>Releases retired storage after its completion value has passed.</summary>
    /// <remarks>The arrays must have equal lengths. The caller must own the arrays and count during this scan.</remarks>
    [global::Engine.Diagnostics.HotPath]
    internal static void Drain(byte[][][] storage, ulong[] completionValues,
        ulong completedValue, ref int generationCount)
    {
        for (int index = 0; index < storage.Length; index++)
        {
            if (storage[index] is null || completionValues[index] > completedValue)
                continue;

            storage[index] = null!;
            completionValues[index] = 0UL;
            generationCount--;
        }
    }

    /// <summary>Finds the first unused retired-storage slot.</summary>
    [global::Engine.Diagnostics.HotPath]
    internal static int FindEmptySlot(byte[][][] storage)
    {
        for (int index = 0; index < storage.Length; index++)
            if (storage[index] is null)
                return index;
        return -1;
    }

    /// <summary>Finds the completion value that covers all submitted frame slots.</summary>
    [global::Engine.Diagnostics.HotPath]
    internal static ulong MaximumCompletionValue(ulong[] submissionValues)
    {
        ulong maximum = 0UL;
        for (int index = 0; index < submissionValues.Length; index++)
            if (submissionValues[index] > maximum)
                maximum = submissionValues[index];
        return maximum;
    }
}
