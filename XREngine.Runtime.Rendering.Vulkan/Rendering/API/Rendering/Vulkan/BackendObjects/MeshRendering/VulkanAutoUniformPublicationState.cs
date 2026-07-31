namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Per-buffer publication ledger for independently versioned binding domains.
/// A validity mask distinguishes a real zero generation from an unpublished
/// domain.
/// </summary>
internal struct VulkanAutoUniformPublicationState
{
    private ulong _planIdentity;
    private bool _hasPlan;
    private ulong _frameGeneration;
    private ulong _viewGeneration;
    private ulong _passGeneration;
    private ulong _materialGeneration;
    private ulong _objectGeneration;
    private ulong _instanceGeneration;
    private ulong _runtimeCallbackGeneration;
    private byte _publishedMask;
    private VulkanAutoUniformDirtyRangeQueue _dirtyRanges;

    internal bool IsPlanPublished(AutoUniformMaterialWritePlan plan)
        => _hasPlan && _planIdentity == plan.PublicationIdentity;

    internal void PublishPlan(AutoUniformMaterialWritePlan plan)
    {
        _planIdentity = plan.PublicationIdentity;
        _hasPlan = true;
        _publishedMask = 0;
    }

    internal bool IsFrequencyPublished(
        EVulkanBindingFrequency frequency,
        ulong generation)
    {
        int bitIndex = GetBitIndex(frequency);
        return (_publishedMask & (1 << bitIndex)) != 0 &&
            GetGeneration(frequency) == generation;
    }

    internal void PublishFrequency(
        EVulkanBindingFrequency frequency,
        ulong generation)
    {
        int bitIndex = GetBitIndex(frequency);
        SetGeneration(frequency, generation);
        _publishedMask |= (byte)(1 << bitIndex);
    }

    /// <summary>
    /// Publishes the exact precompiled ranges for a changed owner into bounded
    /// frame-slot storage. Returning <see langword="false"/> is the stable
    /// generation fast path and leaves the queue empty.
    /// </summary>
    internal bool TryBeginFrequencyPublication(
        EVulkanBindingFrequency frequency,
        ulong generation,
        ReadOnlySpan<VulkanAutoUniformDirtyRange> dirtyRanges,
        uint payloadSize)
    {
        _dirtyRanges.Reset();
        if (IsFrequencyPublished(frequency, generation))
            return false;

        _dirtyRanges.Publish(dirtyRanges, payloadSize);
        return true;
    }

    internal readonly int PendingDirtyRangeCount => _dirtyRanges.Count;

    internal readonly VulkanAutoUniformDirtyRange GetPendingDirtyRange(
        int index)
        => _dirtyRanges.GetRange(index);

    internal void CompleteFrequencyPublication(
        EVulkanBindingFrequency frequency,
        ulong generation)
    {
        PublishFrequency(frequency, generation);
        _dirtyRanges.Reset();
    }

    internal void Invalidate()
    {
        _planIdentity = 0;
        _hasPlan = false;
        _publishedMask = 0;
        _dirtyRanges.Reset();
    }

    private readonly ulong GetGeneration(EVulkanBindingFrequency frequency)
        => frequency switch
        {
            EVulkanBindingFrequency.Frame => _frameGeneration,
            EVulkanBindingFrequency.View => _viewGeneration,
            EVulkanBindingFrequency.Pass => _passGeneration,
            EVulkanBindingFrequency.Material => _materialGeneration,
            EVulkanBindingFrequency.Object => _objectGeneration,
            EVulkanBindingFrequency.Instance => _instanceGeneration,
            EVulkanBindingFrequency.RuntimeCallback =>
                _runtimeCallbackGeneration,
            _ => throw new ArgumentOutOfRangeException(nameof(frequency)),
        };

    private void SetGeneration(
        EVulkanBindingFrequency frequency,
        ulong generation)
    {
        switch (frequency)
        {
            case EVulkanBindingFrequency.Frame:
                _frameGeneration = generation;
                break;
            case EVulkanBindingFrequency.View:
                _viewGeneration = generation;
                break;
            case EVulkanBindingFrequency.Pass:
                _passGeneration = generation;
                break;
            case EVulkanBindingFrequency.Material:
                _materialGeneration = generation;
                break;
            case EVulkanBindingFrequency.Object:
                _objectGeneration = generation;
                break;
            case EVulkanBindingFrequency.Instance:
                _instanceGeneration = generation;
                break;
            case EVulkanBindingFrequency.RuntimeCallback:
                _runtimeCallbackGeneration = generation;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(frequency));
        }
    }

    private static int GetBitIndex(EVulkanBindingFrequency frequency)
    {
        int bitIndex = (int)frequency - 1;
        if ((uint)bitIndex >= 7u)
            throw new ArgumentOutOfRangeException(nameof(frequency));
        return bitIndex;
    }
}
