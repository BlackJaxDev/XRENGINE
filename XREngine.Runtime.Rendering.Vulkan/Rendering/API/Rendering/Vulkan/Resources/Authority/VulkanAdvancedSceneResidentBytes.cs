namespace XREngine.Rendering.Vulkan;

/// <summary>Completed-slot resident byte image backed by its retained mapped range.</summary>
internal sealed class VulkanAdvancedSceneResidentBytes
{
    internal VulkanFrameDataSlice Slice { get; private set; }
    internal int Count { get; private set; }
    internal AdvancedGpuHandle BufferHandle { get; private set; }
    internal ulong PublishedOwnerGeneration { get; private set; }
    internal ulong DatabaseEpoch { get; private set; }
    internal bool MatchesCapacity(ReadOnlySpan<byte> source)
        => Slice.IsValid && checked((uint)source.Length) <= Slice.Length;
    internal bool TryInitialize(VulkanFrameDataSlice slice, ReadOnlySpan<byte> source)
    {
        if (!slice.IsValid)
            return false;
        if (checked((uint)source.Length) > slice.Length)
            return false;
        Slice = slice;
        Count = source.Length;
        return true;
    }
    internal bool CanPatchAppend(
        in AdvancedImmutableByteArenaPublicationSnapshot snapshot,
        ulong databaseEpoch)
        => databaseEpoch != 0u && DatabaseEpoch == databaseEpoch &&
           snapshot.BufferHandle.IsValid &&
           snapshot.BufferHandle == BufferHandle &&
           snapshot.ByteCount >= (uint)Count &&
           MatchesCapacity(snapshot.Data);
    internal bool TryInitialize(
        VulkanFrameDataSlice slice,
        in AdvancedImmutableByteArenaPublicationSnapshot snapshot)
    {
        if (!TryInitialize(slice, snapshot.Data))
            return false;
        BufferHandle = snapshot.BufferHandle;
        return true;
    }
    internal void SetBufferHandle(AdvancedGpuHandle bufferHandle)
        => BufferHandle = bufferHandle;
    internal void SetPublishedOwnerGeneration(ulong generation)
        => PublishedOwnerGeneration = generation;
    internal void SetDatabaseEpoch(ulong databaseEpoch)
        => DatabaseEpoch = databaseEpoch;
    internal void Commit(ReadOnlySpan<byte> source)
        => Count = source.Length;
    internal void CommitPatched(
        ReadOnlySpan<byte> source,
        AdvancedGpuDirtyRange dirtyRange)
    {
        Count = source.Length;
    }
    internal VulkanFrameDataSlice CurrentSlice(int count)
    {
        uint byteLength = checked((uint)Math.Max(count, 1));
        if (!Slice.IsValid || byteLength > Slice.Length)
        {
            throw new InvalidOperationException(
                "A resident byte image cannot publish beyond its retained native allocation.");
        }

        return Slice with { Length = byteLength };
    }
    internal void Clear()
    {
        Slice = default;
        Count = 0;
        BufferHandle = default;
        PublishedOwnerGeneration = 0u;
        DatabaseEpoch = 0u;
    }
}
