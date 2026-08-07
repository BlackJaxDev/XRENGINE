using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free atomic publication for the final presentation tuple.
/// </summary>
internal sealed class VulkanPresentationSourcePublication
{
    private readonly object _sync = new();
    private VulkanPresentationSourceTuple _current;
    private VulkanPresentationSourceTuple _pending;
    private VulkanPresentationSourceTuple[] _slotBindings = [];
    private ulong _nextEpoch;

    internal VulkanPresentationSourceTuple PublishLogical(
        in VulkanPresentationSourceTuple source,
        bool retainEquivalentCurrentSource = false)
    {
        lock (_sync)
        {
            if (_pending.LogicalEpoch != 0 &&
                CanRetainCurrentLogicalSource(_pending, source))
            {
                return _pending;
            }

            if (retainEquivalentCurrentSource &&
                HasCompleteBindingForCurrentSource() &&
                CanRetainCurrentLogicalSource(_current, source))
            {
                return _current;
            }

            ulong epoch = ++_nextEpoch;
            if (epoch == 0)
                epoch = ++_nextEpoch;

            VulkanPresentationSourceTuple candidate = source with { LogicalEpoch = epoch };
            if (HasCompleteBindingForCurrentSource() && candidate.HasLogicalSource)
            {
                _pending = candidate;
                return _pending;
            }

            _pending = default;
            _current = candidate;
            Array.Clear(_slotBindings);
            return _current;
        }
    }

    private static bool CanRetainCurrentLogicalSource(
        in VulkanPresentationSourceTuple current,
        in VulkanPresentationSourceTuple candidate)
    {
        if (!candidate.HasLogicalSource ||
            !ReferenceEquals(current.ColorTexture, candidate.ColorTexture) ||
            !ReferenceEquals(current.FrameBuffer, candidate.FrameBuffer) ||
            current.Width != candidate.Width ||
            current.Height != candidate.Height)
        {
            return false;
        }

        if (current.Context.PipelineIdentity != candidate.Context.PipelineIdentity ||
            current.Context.ViewportIdentity != candidate.Context.ViewportIdentity ||
            current.Context.ResourceGeneration != candidate.Context.ResourceGeneration ||
            !ReferenceEquals(current.Context.ResourceRegistry, candidate.Context.ResourceRegistry))
        {
            return false;
        }

        // Descriptor slot/publication and output-target identity are per-frame state.
        // Native image identity is not: replace the logical source as soon as the
        // active resource plan swaps its sampled image, view, or sampler.
        if (candidate.Image.Handle == 0)
            return true;

        return current.Image.Handle == candidate.Image.Handle &&
            current.ImageAllocationGeneration == candidate.ImageAllocationGeneration &&
            current.ImageView.Handle == candidate.ImageView.Handle &&
            current.ImageViewGeneration == candidate.ImageViewGeneration &&
            current.Sampler.Handle == candidate.Sampler.Handle &&
            current.SamplerGeneration == candidate.SamplerGeneration &&
            current.Format == candidate.Format &&
            current.Aspect == candidate.Aspect &&
            current.Samples == candidate.Samples;
    }

    private bool HasCompleteBindingForCurrentSource()
    {
        for (int index = 0; index < _slotBindings.Length; index++)
        {
            VulkanPresentationSourceTuple binding = _slotBindings[index];
            if (binding.LogicalEpoch == _current.LogicalEpoch && binding.IsComplete)
                return true;
        }

        return false;
    }

    internal bool TryBindDescriptor(
        ulong expectedLogicalEpoch,
        in DescriptorImageInfo imageInfo,
        DescriptorSet descriptorSet,
        ulong descriptorSetGeneration,
        int descriptorSlot,
        ulong descriptorPublicationGeneration,
        CommandBuffer commandArtifact,
        ulong commandArtifactGeneration,
        out VulkanPresentationSourceTuple source)
    {
        lock (_sync)
        {
            bool bindsPending = _pending.LogicalEpoch != 0 &&
                _pending.LogicalEpoch == expectedLogicalEpoch;
            VulkanPresentationSourceTuple logicalSource = bindsPending
                ? _pending
                : _current;
            if (descriptorSlot < 0 ||
                logicalSource.LogicalEpoch != expectedLogicalEpoch ||
                logicalSource.ImageView.Handle != imageInfo.ImageView.Handle ||
                logicalSource.Sampler.Handle != imageInfo.Sampler.Handle)
            {
                source = _current;
                return false;
            }

            if (bindsPending)
            {
                _current = _pending;
                _pending = default;
                Array.Clear(_slotBindings);
                logicalSource = _current;
            }

            EnsureSlotCapacity(descriptorSlot);
            VulkanPresentationSourceTuple binding = logicalSource with
            {
                ExpectedLayout = imageInfo.ImageLayout,
                DescriptorSet = descriptorSet,
                DescriptorSetGeneration = descriptorSetGeneration,
                DescriptorSlot = descriptorSlot,
                DescriptorPublicationGeneration = descriptorPublicationGeneration,
                OwningCommandArtifact = commandArtifact,
                OwningCommandArtifactGeneration = commandArtifactGeneration,
            };
            _slotBindings[descriptorSlot] = binding;
            source = binding;
            return true;
        }
    }

    internal VulkanPresentationSourceTuple CaptureLogical()
    {
        lock (_sync)
            return _pending.LogicalEpoch != 0 ? _pending : _current;
    }

    internal VulkanPresentationSourceTuple CaptureForDescriptorSlot(int descriptorSlot)
    {
        lock (_sync)
        {
            if ((uint)descriptorSlot < (uint)_slotBindings.Length)
            {
                VulkanPresentationSourceTuple binding = _slotBindings[descriptorSlot];
                if (binding.LogicalEpoch == _current.LogicalEpoch &&
                    binding.LogicalEpoch != 0)
                {
                    return binding;
                }
            }

            return _current;
        }
    }

    internal bool HasAnyCompleteBinding()
    {
        lock (_sync)
            return HasCompleteBindingForCurrentSource();
    }

    internal VulkanPresentationSourceTuple CaptureAnyCompleteBinding()
    {
        lock (_sync)
        {
            for (int index = 0; index < _slotBindings.Length; index++)
            {
                VulkanPresentationSourceTuple binding = _slotBindings[index];
                if (binding.LogicalEpoch == _current.LogicalEpoch && binding.IsComplete)
                    return binding;
            }

            return _current;
        }
    }

    private void EnsureSlotCapacity(int descriptorSlot)
    {
        if (descriptorSlot < _slotBindings.Length)
            return;

        int requiredLength = checked(descriptorSlot + 1);
        int newLength = Math.Max(requiredLength, Math.Max(_slotBindings.Length * 2, 4));
        Array.Resize(ref _slotBindings, newLength);
    }
}
