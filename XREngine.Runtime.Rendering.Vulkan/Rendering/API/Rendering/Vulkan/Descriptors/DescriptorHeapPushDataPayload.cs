using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class DescriptorHeapPushDataPayload(uint[] dwords)
{
    public static DescriptorHeapPushDataPayload Empty { get; } = new([]);

    public uint[] Dwords { get; } = dwords;

    // Heap indices alone do not retain the native objects whose descriptor
    // bytes were written into the heap. Keep the exact published generations
    // per binding so a later command can both pin them and reject a recycled
    // native handle before it is submitted.
    private readonly Dictionary<DescriptorHeapBindingKey, VulkanPinnedResourceGeneration[]> _resourceGenerations = [];
    private readonly Dictionary<DescriptorHeapBindingKey, int> _resourceGenerationEpochs = [];
    private VulkanPinnedResourceGeneration[] _resourceGenerationSnapshot = [];
    private bool _resourceGenerationSnapshotDirty = true;
    private DescriptorHeapProgramLayout? _reuseLayout;
    private int _reuseEpoch;

    public void SetDword(uint byteOffset, uint value)
    {
        if (byteOffset == uint.MaxValue)
            return;

        if ((byteOffset & 3u) != 0 || byteOffset / sizeof(uint) >= (uint)Dwords.Length)
            throw new ArgumentOutOfRangeException(nameof(byteOffset), "Heap root writes must be aligned and within the payload.");
        Dwords[checked((int)(byteOffset / sizeof(uint)))] = value;
    }

    public bool IsValidFor(DescriptorHeapProgramLayout layout)
        => Dwords.Length >= layout.PushDwordCount;

    /// <summary>
    /// Begins one reusable recording. Binding arrays survive unchanged recordings;
    /// the epoch excludes bindings omitted by this recording or a failed write.
    /// </summary>
    internal void ResetForReuse(DescriptorHeapProgramLayout? layout)
    {
        Array.Clear(Dwords);
        if (!ReferenceEquals(_reuseLayout, layout))
        {
            _resourceGenerations.Clear();
            _resourceGenerationEpochs.Clear();
            _reuseLayout = layout;
        }

        if (_reuseEpoch == int.MaxValue)
        {
            _resourceGenerations.Clear();
            _resourceGenerationEpochs.Clear();
            _reuseEpoch = 0;
        }
        _reuseEpoch++;
        _resourceGenerationSnapshotDirty = true;
    }

    internal void SetResourceGenerations(
        DescriptorHeapBindingKey binding,
        scoped ReadOnlySpan<VulkanPinnedResourceGeneration> generations)
    {
        if (generations.IsEmpty)
        {
            if (_resourceGenerationEpochs.Remove(binding))
                _resourceGenerationSnapshotDirty = true;
            return;
        }

        _resourceGenerationEpochs[binding] = _reuseEpoch;
        if (_resourceGenerations.TryGetValue(binding, out VulkanPinnedResourceGeneration[]? existing) &&
            generations.SequenceEqual(existing))
        {
            return;
        }

        _resourceGenerations[binding] = generations.ToArray();
        _resourceGenerationSnapshotDirty = true;
    }

    internal VulkanPinnedResourceGeneration[] SnapshotResourceGenerations()
    {
        if (!_resourceGenerationSnapshotDirty)
            return _resourceGenerationSnapshot;

        if (_resourceGenerationEpochs.Count == 0)
        {
            _resourceGenerationSnapshot = [];
            _resourceGenerationSnapshotDirty = false;
            return _resourceGenerationSnapshot;
        }

        int count = 0;
        foreach ((DescriptorHeapBindingKey binding, int epoch) in _resourceGenerationEpochs)
        {
            if (epoch == _reuseEpoch)
                count = checked(count + _resourceGenerations[binding].Length);
        }
        if (count == 0)
        {
            _resourceGenerationSnapshot = [];
            _resourceGenerationSnapshotDirty = false;
            return _resourceGenerationSnapshot;
        }

        VulkanPinnedResourceGeneration[] snapshot = new VulkanPinnedResourceGeneration[count];
        int destination = 0;
        foreach ((DescriptorHeapBindingKey binding, int epoch) in _resourceGenerationEpochs)
        {
            if (epoch != _reuseEpoch)
                continue;
            VulkanPinnedResourceGeneration[] generations = _resourceGenerations[binding];
            generations.CopyTo(snapshot, destination);
            destination += generations.Length;
        }
        _resourceGenerationSnapshot = snapshot;
        _resourceGenerationSnapshotDirty = false;
        return _resourceGenerationSnapshot;
    }

    internal bool TryTrackResourceGenerations(
        VulkanTrackedCommandEncoder encoder,
        CommandBuffer commandBuffer,
        out string reason)
    {
        foreach ((DescriptorHeapBindingKey binding, int epoch) in _resourceGenerationEpochs)
        {
            if (epoch != _reuseEpoch)
                continue;
            VulkanPinnedResourceGeneration[] generations = _resourceGenerations[binding];
            for (int index = 0; index < generations.Length; index++)
            {
                VulkanPinnedResourceGeneration expected = generations[index];
                try
                {
                    encoder.Track(commandBuffer, expected.Key.Type, expected.Key.Handle, expected.Generation);
                }
                catch (VulkanPlanPreconditionException exception)
                {
                    reason = exception.Message;
                    return false;
                }
            }
        }

        reason = string.Empty;
        return true;
    }
}
