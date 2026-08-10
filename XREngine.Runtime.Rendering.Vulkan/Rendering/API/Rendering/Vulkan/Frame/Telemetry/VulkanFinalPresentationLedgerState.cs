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
    private VulkanFinalPresentationDescriptorObservation _latestDescriptor;

    internal VulkanFinalPresentationLedgerState(bool enabled)
        => _enabled = enabled;

    internal bool Enabled
    {
        get
        {
            lock (_sync)
                return _enabled;
        }
    }

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
                _latestDescriptor = default;
            }

            _enabled = enabled;
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
        lock (_sync)
        {
            if (!_enabled || _frozen)
                return;

            if (commandBuffer == 0 &&
                _latestDescriptor.FrameNumber == frameNumber &&
                _latestDescriptor.DescriptorSlot == descriptorSlot &&
                _latestDescriptor.CommandBuffer != 0 &&
                _latestDescriptor.DescriptorSet == descriptorSet &&
                _latestDescriptor.ImageView == imageInfo.ImageView.Handle &&
                _latestDescriptor.ResourceSignature == resourceSignature)
            {
                return;
            }

            _latestDescriptor = new VulkanFinalPresentationDescriptorObservation(
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

    internal VulkanFinalPresentationDescriptorObservation CaptureLatestDescriptor()
    {
        lock (_sync)
            return _latestDescriptor;
    }

    internal void Append(in VulkanFinalPresentationLedgerEntry entry)
    {
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
