using System;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns a bounded diagnostic ring. The render hot path enters its lock only while
/// explicitly enabled; snapshot allocations are restricted to tooling reads.
/// </summary>
internal sealed class VulkanFinalPresentationLedgerState
{
    private const int Capacity = 128;
    private readonly object _sync = new();
    private readonly VulkanFinalPresentationLedgerEntry[] _entries = new VulkanFinalPresentationLedgerEntry[Capacity];
    private int _count;
    private int _next;
    private bool _enabled;
    private bool _frozen;
    private string? _freezeReason;
    private ulong _descriptorSequence;
    private readonly VulkanFinalPresentationDescriptorObservation[] _latestDescriptors =
        new VulkanFinalPresentationDescriptorObservation[Capacity];

    internal VulkanFinalPresentationLedgerState(bool enabled)
        => _enabled = enabled;

    internal bool Enabled
        => Volatile.Read(ref _enabled);

    internal void Configure(bool enabled, bool frozen, bool clear)
    {
        lock (_sync)
        {
            if (clear)
            {
                Array.Clear(_entries);
                _count = 0;
                _next = 0;
                _freezeReason = null;
                Array.Clear(_latestDescriptors);
            }

            Volatile.Write(ref _enabled, enabled);
            _frozen = frozen;
            if (!frozen)
                _freezeReason = null;
        }
    }

    internal void ObserveDescriptor(
        ulong frameNumber,
        int descriptorSlot,
        ulong commandBuffer,
        ulong descriptorSet,
        uint set,
        uint binding,
        string? bindingName,
        Silk.NET.Vulkan.DescriptorImageInfo imageInfo,
        ulong resourceSignature,
        bool writeMatched,
        bool writeSucceeded)
    {
        if (!Volatile.Read(ref _enabled))
            return;

        lock (_sync)
        {
            if (!_enabled || _frozen)
                return;
            if ((uint)descriptorSlot >= (uint)_latestDescriptors.Length)
                return;

            ref VulkanFinalPresentationDescriptorObservation latest =
                ref _latestDescriptors[descriptorSlot];

            if (commandBuffer == 0 &&
                latest.FrameNumber == frameNumber &&
                latest.CommandBuffer != 0 &&
                latest.DescriptorSet == descriptorSet &&
                latest.ImageView == imageInfo.ImageView.Handle &&
                latest.ResourceSignature == resourceSignature)
            {
                return;
            }

            latest = new VulkanFinalPresentationDescriptorObservation(
                ++_descriptorSequence,
                frameNumber,
                descriptorSlot,
                commandBuffer,
                descriptorSet,
                set,
                binding,
                bindingName,
                imageInfo.ImageView.Handle,
                imageInfo.Sampler.Handle,
                imageInfo.ImageLayout,
                resourceSignature,
                writeMatched,
                writeSucceeded);
        }
    }

    internal VulkanFinalPresentationDescriptorObservation CaptureLatestDescriptor(
        int descriptorSlot)
    {
        lock (_sync)
            return (uint)descriptorSlot < (uint)_latestDescriptors.Length
                ? _latestDescriptors[descriptorSlot]
                : default;
    }

    internal void Append(in VulkanFinalPresentationLedgerEntry entry)
    {
        if (!Volatile.Read(ref _enabled))
            return;

        lock (_sync)
        {
            if (!_enabled || _frozen)
                return;

            _entries[_next] = entry;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity)
                _count++;

            if (!entry.InvariantFailed)
                return;

            _frozen = true;
            _freezeReason = entry.InvariantFailure;
        }
    }

    internal VulkanFinalPresentationLedgerEntry[] Snapshot(int limit)
    {
        lock (_sync)
        {
            int returnedCount = Math.Min(Math.Clamp(limit, 1, Capacity), _count);
            VulkanFinalPresentationLedgerEntry[] snapshot = new VulkanFinalPresentationLedgerEntry[returnedCount];
            for (int i = 0; i < returnedCount; i++)
            {
                int sourceIndex = (_next - 1 - i + Capacity) % Capacity;
                snapshot[i] = _entries[sourceIndex];
            }

            return snapshot;
        }
    }

    internal void CaptureStatus(
        out bool enabled,
        out bool frozen,
        out int count,
        out string? freezeReason)
    {
        lock (_sync)
        {
            enabled = _enabled;
            frozen = _frozen;
            count = _count;
            freezeReason = _freezeReason;
        }
    }
}
