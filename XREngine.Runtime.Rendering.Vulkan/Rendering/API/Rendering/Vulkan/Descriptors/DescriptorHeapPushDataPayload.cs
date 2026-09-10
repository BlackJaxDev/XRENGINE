using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed class DescriptorHeapPushDataPayload
{
    private static long s_nextOwnerIdentity;

    public static DescriptorHeapPushDataPayload Empty { get; } = new([]);

    private readonly uint[] _dwords;
    public ReadOnlySpan<uint> Dwords => _dwords;
    internal ulong OwnerIdentity { get; } = unchecked((ulong)Interlocked.Increment(ref s_nextOwnerIdentity));
    internal ulong ContentGeneration { get; private set; } = 1UL;

    public DescriptorHeapPushDataPayload(uint[] dwords)
    {
        // The caller transfers this newly allocated array to the payload. All
        // subsequent writes go through SetDword/ResetForReuse so cache tokens
        // cannot outlive an unversioned mutation.
        _dwords = dwords;
        RuntimeEngine.Rendering.Stats.Vulkan.RecordDescriptorHeapPayloadAllocation();
    }

    // Heap indices alone do not retain the native objects whose descriptor
    // bytes were written into the heap. Keep the exact published generations
    // per binding so a later command can both pin them and reject a recycled
    // native handle before it is submitted.
    private readonly Dictionary<DescriptorHeapBindingKey, VulkanPinnedResourceGeneration[]> _resourceGenerations = [];
    private readonly Dictionary<DescriptorHeapBindingKey, int> _resourceGenerationEpochs = [];
    private VulkanPinnedResourceGeneration[] _resourceGenerationSnapshot = [];
    private bool _resourceGenerationSnapshotDirty = true;
    private DescriptorHeapProgramLayout? _resourceGenerationSnapshotLayout;
    private DescriptorHeapProgramLayout? _reuseLayout;
    private int _reuseEpoch;

    public void SetDword(uint byteOffset, uint value)
    {
        if (byteOffset == uint.MaxValue)
            return;

        if ((byteOffset & 3u) != 0 || byteOffset / sizeof(uint) >= (uint)Dwords.Length)
            throw new ArgumentOutOfRangeException(nameof(byteOffset), "Heap root writes must be aligned and within the payload.");
        int index = checked((int)(byteOffset / sizeof(uint)));
        if (Dwords[index] == value)
            return;

        _dwords[index] = value;
        AdvanceContentGeneration();
    }

    public bool IsValidFor(DescriptorHeapProgramLayout layout)
        => Dwords.Length >= layout.PushDwordCount;

    internal DescriptorHeapPushDataIdentity CaptureIdentity(DescriptorHeapProgramLayout? layout)
        => layout is not null && IsValidFor(layout)
            ? new DescriptorHeapPushDataIdentity(
                OwnerIdentity,
                ContentGeneration,
                layout.Identity,
                layout.ShaderConstantByteCount,
                layout.PushByteCount)
            : default;

    /// <summary>
    /// Begins one reusable recording. Binding arrays survive unchanged recordings;
    /// the epoch excludes bindings omitted by this recording or a failed write.
    /// </summary>
    internal void ResetForReuse(DescriptorHeapProgramLayout? layout)
    {
        Array.Clear(_dwords);
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
        AdvanceContentGeneration();
    }

    internal void SetResourceGenerations(
        DescriptorHeapBindingKey binding,
        scoped ReadOnlySpan<VulkanPinnedResourceGeneration> generations)
    {
        if (generations.IsEmpty)
        {
            if (_resourceGenerationEpochs.Remove(binding))
            {
                _resourceGenerationSnapshotDirty = true;
                AdvanceContentGeneration();
            }
            return;
        }

        bool wasActive = _resourceGenerationEpochs.TryGetValue(binding, out int previousEpoch) &&
            previousEpoch == _reuseEpoch;
        _resourceGenerationEpochs[binding] = _reuseEpoch;
        if (_resourceGenerations.TryGetValue(binding, out VulkanPinnedResourceGeneration[]? existing) &&
            generations.SequenceEqual(existing))
        {
            if (!wasActive)
            {
                _resourceGenerationSnapshotDirty = true;
                AdvanceContentGeneration();
            }
            return;
        }

        _resourceGenerations[binding] = generations.ToArray();
        _resourceGenerationSnapshotDirty = true;
        AdvanceContentGeneration();
    }

    internal VulkanPinnedResourceGeneration[] SnapshotResourceGenerations(
        DescriptorHeapProgramLayout layout)
    {
        if (!_resourceGenerationSnapshotDirty && ReferenceEquals(_resourceGenerationSnapshotLayout, layout))
            return _resourceGenerationSnapshot;

        if (_resourceGenerationEpochs.Count == 0)
        {
            _resourceGenerationSnapshot = [];
            _resourceGenerationSnapshotDirty = false;
            _resourceGenerationSnapshotLayout = layout;
            return _resourceGenerationSnapshot;
        }

        int count = 0;
        foreach (DescriptorHeapBindingLayout bindingLayout in layout.Bindings)
        {
            DescriptorHeapBindingKey binding = bindingLayout.Key;
            if (_resourceGenerationEpochs.TryGetValue(binding, out int epoch) && epoch == _reuseEpoch)
                count = checked(count + _resourceGenerations[binding].Length);
        }
        if (count == 0)
        {
            _resourceGenerationSnapshot = [];
            _resourceGenerationSnapshotDirty = false;
            _resourceGenerationSnapshotLayout = layout;
            return _resourceGenerationSnapshot;
        }

        // Resetting scratch marks the snapshot dirty, but unchanged finalized
        // bindings can retain the previous immutable array. Never refill an
        // array that an already prepared draw may still reference.
        if (_resourceGenerationSnapshot.Length == count)
        {
            int compared = 0;
            bool unchanged = true;
            foreach (DescriptorHeapBindingLayout bindingLayout in layout.Bindings)
            {
                DescriptorHeapBindingKey binding = bindingLayout.Key;
                if (!_resourceGenerationEpochs.TryGetValue(binding, out int epoch) || epoch != _reuseEpoch)
                    continue;
                VulkanPinnedResourceGeneration[] generations = _resourceGenerations[binding];
                if (!generations.AsSpan().SequenceEqual(_resourceGenerationSnapshot.AsSpan(compared, generations.Length)))
                {
                    unchanged = false;
                    break;
                }
                compared += generations.Length;
            }
            if (unchanged)
            {
                _resourceGenerationSnapshotDirty = false;
                _resourceGenerationSnapshotLayout = layout;
                return _resourceGenerationSnapshot;
            }
        }

        VulkanPinnedResourceGeneration[] snapshot = new VulkanPinnedResourceGeneration[count];
        int destination = 0;
        foreach (DescriptorHeapBindingLayout bindingLayout in layout.Bindings)
        {
            DescriptorHeapBindingKey binding = bindingLayout.Key;
            if (!_resourceGenerationEpochs.TryGetValue(binding, out int epoch) || epoch != _reuseEpoch)
                continue;
            VulkanPinnedResourceGeneration[] generations = _resourceGenerations[binding];
            generations.CopyTo(snapshot, destination);
            destination += generations.Length;
        }
        _resourceGenerationSnapshot = snapshot;
        _resourceGenerationSnapshotDirty = false;
        _resourceGenerationSnapshotLayout = layout;
        return _resourceGenerationSnapshot;
    }

    private void AdvanceContentGeneration()
    {
        if (ContentGeneration == ulong.MaxValue)
            throw new InvalidOperationException("Descriptor-heap payload content generation overflowed.");
        ContentGeneration++;
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
