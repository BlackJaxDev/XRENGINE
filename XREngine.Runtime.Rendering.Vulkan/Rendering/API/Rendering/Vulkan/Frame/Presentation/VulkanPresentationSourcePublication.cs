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
                CanRetainCurrentLogicalSource(_current, source))
            {
                return _current;
            }

            if (retainEquivalentCurrentSource &&
                _current.HasLogicalSource &&
                XREnvironment.IsEnabled(
                    XREngineEnvironmentVariables.VulkanRecordingDiag))
            {
                TraceLogicalSourceReplacement(in source);
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

    private void TraceLogicalSourceReplacement(
        in VulkanPresentationSourceTuple candidate)
    {
        Debug.VulkanEvery(
            $"Vulkan.PresentationSource.Replace.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[Vulkan] Presentation source identity replaced. " +
            "epoch={0} complete={1} textureSame={2} fboSame={3} " +
            "extent={4}x{5}->{6}x{7} pipeline={8}->{9} viewport={10}->{11} " +
            "resourceGeneration={12}->{13} registrySame={14} " +
            "image=0x{15:X}/{16}->0x{17:X}/{18} " +
            "view=0x{19:X}/{20}->0x{21:X}/{22} " +
            "sampler=0x{23:X}/{24}->0x{25:X}/{26}.",
            _current.LogicalEpoch,
            HasCompleteBindingForCurrentSource(),
            ReferenceEquals(_current.ColorTexture, candidate.ColorTexture),
            ReferenceEquals(_current.FrameBuffer, candidate.FrameBuffer),
            _current.Width,
            _current.Height,
            candidate.Width,
            candidate.Height,
            _current.Context.PipelineIdentity,
            candidate.Context.PipelineIdentity,
            _current.Context.ViewportIdentity,
            candidate.Context.ViewportIdentity,
            _current.Context.ResourceGeneration,
            candidate.Context.ResourceGeneration,
            ReferenceEquals(
                _current.Context.ResourceRegistry,
                candidate.Context.ResourceRegistry),
            _current.Image.Handle,
            _current.ImageAllocationGeneration,
            candidate.Image.Handle,
            candidate.ImageAllocationGeneration,
            _current.ImageView.Handle,
            _current.ImageViewGeneration,
            candidate.ImageView.Handle,
            candidate.ImageViewGeneration,
            _current.Sampler.Handle,
            _current.SamplerGeneration,
            candidate.Sampler.Handle,
            candidate.SamplerGeneration);
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
                if (XREnvironment.IsEnabled(
                        XREngineEnvironmentVariables.VulkanRecordingDiag))
                {
                    Debug.VulkanEvery(
                        $"Vulkan.PresentationSource.BindRejected.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Presentation descriptor binding rejected. " +
                        "expectedEpoch={0} currentEpoch={1} pending={2} slot={3} " +
                        "view=0x{4:X}->0x{5:X} sampler=0x{6:X}->0x{7:X}.",
                        expectedLogicalEpoch,
                        logicalSource.LogicalEpoch,
                        bindsPending,
                        descriptorSlot,
                        logicalSource.ImageView.Handle,
                        imageInfo.ImageView.Handle,
                        logicalSource.Sampler.Handle,
                        imageInfo.Sampler.Handle);
                }
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
            if (XREnvironment.IsEnabled(
                    XREngineEnvironmentVariables.VulkanRecordingDiag))
            {
                Debug.VulkanEvery(
                    $"Vulkan.PresentationSource.Bound.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Presentation descriptor binding published. " +
                    "epoch={0} slot={1} descriptor=0x{2:X}/{3} " +
                    "command=0x{4:X}/{5} complete={6}.",
                    binding.LogicalEpoch,
                    descriptorSlot,
                    binding.DescriptorSet.Handle,
                    binding.DescriptorSetGeneration,
                    binding.OwningCommandArtifact.Handle,
                    binding.OwningCommandArtifactGeneration,
                    binding.IsComplete);
            }
            return true;
        }
    }

    internal VulkanPresentationSourceTuple CaptureLogical()
    {
        lock (_sync)
            return _pending.LogicalEpoch != 0 ? _pending : _current;
    }

    /// <summary>
    /// Attaches the primary artifact selected for this acquired image after
    /// command recording. Descriptor preparation intentionally runs before a
    /// primary recording generation exists, so it can publish descriptor
    /// identity but cannot truthfully publish command ownership.
    /// </summary>
    internal bool TryBindCommandArtifact(
        ulong expectedLogicalEpoch,
        int descriptorSlot,
        CommandBuffer commandArtifact,
        ulong commandArtifactGeneration,
        out VulkanPresentationSourceTuple source)
    {
        lock (_sync)
        {
            if (descriptorSlot < 0 ||
                commandArtifact.Handle == 0 ||
                commandArtifactGeneration == 0 ||
                (uint)descriptorSlot >= (uint)_slotBindings.Length)
            {
                source = _current;
                return false;
            }

            VulkanPresentationSourceTuple binding = _slotBindings[descriptorSlot];
            if (binding.LogicalEpoch == 0 ||
                binding.LogicalEpoch != expectedLogicalEpoch ||
                binding.LogicalEpoch != _current.LogicalEpoch)
            {
                source = _current;
                return false;
            }

            binding = binding with
            {
                OwningCommandArtifact = commandArtifact,
                OwningCommandArtifactGeneration = commandArtifactGeneration,
            };
            _slotBindings[descriptorSlot] = binding;
            source = binding;
            return binding.IsComplete;
        }
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
