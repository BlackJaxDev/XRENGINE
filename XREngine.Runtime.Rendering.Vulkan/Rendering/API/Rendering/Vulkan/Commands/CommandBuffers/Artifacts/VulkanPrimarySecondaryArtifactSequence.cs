namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Retains the exact ordered command-chain secondary artifacts encoded by a
/// primary command buffer. Structural hashes remain useful diagnostics, but
/// cannot prove that a cached primary executes the current native artifacts.
/// </summary>
internal sealed class VulkanPrimarySecondaryArtifactSequence
{
    private VulkanPrimarySecondaryArtifactSequenceEntry[] _entries = [];

    public int Count { get; private set; }

    public void Clear() => Count = 0;

    public void Add(CommandChain chain)
    {
        EnsureCapacity(Count + 1);
        _entries[Count++] = new VulkanPrimarySecondaryArtifactSequenceEntry(
            chain.Key,
            chain.RecordedArtifact.CreateReference());
    }

    public void CopyFrom(VulkanPrimarySecondaryArtifactSequence source)
    {
        EnsureCapacity(source.Count);
        source._entries.AsSpan(0, source.Count).CopyTo(_entries);
        Count = source.Count;
    }

    public ref readonly VulkanPrimarySecondaryArtifactSequenceEntry GetEntry(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ref _entries[index];
    }

    public bool MatchesCurrentArtifacts(
        IReadOnlyDictionary<CommandChainKey, CommandChain> commandChainCache)
        => MatchesCurrentArtifacts(commandChainCache, out _);

    public bool MatchesCurrentArtifacts(
        IReadOnlyDictionary<CommandChainKey, CommandChain> commandChainCache,
        out string? mismatch)
    {
        for (int i = 0; i < Count; i++)
        {
            ref readonly VulkanPrimarySecondaryArtifactSequenceEntry entry =
                ref _entries[i];
            if (!commandChainCache.TryGetValue(entry.Key, out CommandChain? chain))
            {
                mismatch = $"encoded secondary sequence entry {i} no longer has a command chain";
                return false;
            }

            VulkanRecordedCommandArtifactReference current =
                chain.RecordedArtifact.CreateReference();
            if (entry.Artifact == current)
                continue;

            mismatch =
                $"encoded secondary sequence entry {i} changed artifact " +
                $"0x{entry.Artifact.NativeBuffer.Handle:X}/generation={entry.Artifact.ArtifactGeneration} " +
                $"to 0x{current.NativeBuffer.Handle:X}/generation={current.ArtifactGeneration}";
            return false;
        }

        mismatch = null;
        return true;
    }

    private void EnsureCapacity(int required)
    {
        if (_entries.Length >= required)
            return;

        int capacity = Math.Max(required, _entries.Length == 0 ? 32 : _entries.Length * 2);
        Array.Resize(ref _entries, capacity);
    }
}
