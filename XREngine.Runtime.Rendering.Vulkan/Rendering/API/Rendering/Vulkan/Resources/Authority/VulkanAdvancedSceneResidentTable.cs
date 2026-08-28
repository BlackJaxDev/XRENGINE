using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Completed-slot resident SoA image. The retained Vulkan range is the data
/// authority and is re-opened only after the slot completion boundary has made
/// prior publication uses dead.
/// </summary>
internal sealed class VulkanAdvancedSceneResidentTable<T> where T : unmanaged
{
    private static readonly uint ElementSize = checked((uint)Unsafe.SizeOf<T>());

    internal VulkanFrameDataSlice Slice { get; private set; }
    internal int Count { get; private set; }
    internal ulong AppliedPublicationSequence { get; private set; }
    internal ulong DatabaseEpoch { get; private set; }
    internal AdvancedGpuOwnerGenerations OwnerGenerations { get; private set; }
    internal int Capacity => Slice.IsValid && Slice.Length % ElementSize == 0u
        ? checked((int)(Slice.Length / ElementSize))
        : 0;
    internal bool IsAllocated => Slice.IsValid;

    internal bool TryInitialize(
        VulkanFrameDataSlice slice,
        ReadOnlySpan<T> source)
    {
        if (!slice.IsValid)
            return false;

        uint sourceByteLength = checked((uint)source.Length * ElementSize);
        if (slice.Length < sourceByteLength ||
            slice.Length % ElementSize != 0u)
            return false;
        Slice = slice;
        Count = source.Length;
        return true;
    }

    internal bool MatchesCapacity(ReadOnlySpan<T> source)
    {
        if (!IsAllocated || source.Length > Capacity ||
            Slice.Length % ElementSize != 0u)
        {
            return false;
        }

        uint sourceByteLength = checked((uint)source.Length * ElementSize);
        return sourceByteLength <= Slice.Length;
    }

    internal bool CanPatch(
        ulong databaseEpoch,
        ulong publicationSequence,
        ulong retainedJournalFloor,
        bool hasRetainedJournal,
        ReadOnlySpan<T> source)
    {
        if (!MatchesCapacity(source) || DatabaseEpoch == 0u ||
            DatabaseEpoch != databaseEpoch || publicationSequence < AppliedPublicationSequence)
        {
            return false;
        }

        // If no deltas are retained, only an already exact image may be
        // reused. Otherwise the journal must start no later than the next
        // sequence this mirror needs to replay.
        if (!hasRetainedJournal)
            return AppliedPublicationSequence == publicationSequence;
        return AppliedPublicationSequence >= retainedJournalFloor - 1u;
    }

    internal bool CanReuseUnchanged(
        ulong databaseEpoch,
        in AdvancedGpuOwnerGenerations generations,
        ReadOnlySpan<T> source)
        => MatchesCapacity(source) && DatabaseEpoch == databaseEpoch &&
           OwnerGenerations.Topology == generations.Topology &&
           OwnerGenerations.Content == generations.Content;

    internal void Commit(ReadOnlySpan<T> source)
        => Count = source.Length;

    /// <summary>
    /// Advances journal metadata for rows already patched in the retained
    /// mapped slice. Ordinary unchanged publications therefore do no
    /// O(table-size) CPU work or managed allocation.
    /// </summary>
    internal void CommitPatched(
        ReadOnlySpan<T> source,
        ReadOnlySpan<AdvancedGpuRecordPublicationDelta> deltas)
    {
        for (int index = 0; index < deltas.Length; ++index)
        {
            AdvancedGpuRecordPublicationDelta delta = deltas[index];
            if (delta.PublicationGeneration <= AppliedPublicationSequence)
                continue;

            AppliedPublicationSequence = delta.PublicationGeneration;
        }

        Count = source.Length;
    }

    internal void StampPublication(
        ulong databaseEpoch,
        ulong publicationSequence,
        in AdvancedGpuOwnerGenerations ownerGenerations)
    {
        DatabaseEpoch = databaseEpoch;
        AppliedPublicationSequence = publicationSequence;
        OwnerGenerations = ownerGenerations;
    }

    internal VulkanFrameDataSlice CreateCurrentSlice(int count)
    {
        uint byteLength = checked((uint)Math.Max(count, 1) * ElementSize);
        if (!Slice.IsValid || byteLength > Slice.Length ||
            Slice.Length % ElementSize != 0u)
        {
            throw new InvalidOperationException(
                "A resident table cannot publish beyond its retained native allocation.");
        }

        return Slice with { Length = byteLength };
    }

    internal void Clear()
    {
        Slice = default;
        Count = 0;
        AppliedPublicationSequence = 0u;
        DatabaseEpoch = 0u;
        OwnerGenerations = default;
    }
}
