namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable dependency snapshot shared by primary variants, secondary ranges,
/// and command-chain schedules. Ordinary descriptor-set publication is binding
/// state because <c>vkUpdateDescriptorSets</c> invalidates command buffers that
/// recorded those sets without update-after-bind. Buffer-backed frame data remains
/// data-only and can refresh without rebuilding compatible command topology.
/// </summary>
internal readonly record struct CommandRecordingDependencySignature(
    ulong OutputPassAttachment,
    ulong RenderArea,
    uint ViewMask,
    uint QueueFamily,
    ulong DynamicRenderingInheritance,
    ulong PipelineGeneration,
    ulong PipelineLayoutGeneration,
    ulong MeshBindingIdentity,
    ulong IndexBufferBindingIdentity,
    ulong VertexBufferBindingIdentity,
    ulong BufferAllocationGeneration,
    ulong ImageAllocationGeneration,
    ulong ImageViewGeneration,
    ulong SamplerAllocationGeneration,
    ulong DescriptorLayoutGeneration,
    ulong DescriptorSetGeneration,
    ulong ResourcePlanGeneration,
    uint ExternalTargetVariant,
    int FrameSlotVariant,
    ulong DescriptorPublicationGeneration,
    ulong DataPublicationGeneration,
    ulong VolatileSuffixGeneration)
{
    public CommandRecordingDependencyMismatch Compare(in CommandRecordingDependencySignature current)
        => Compare(
            current,
            commandChainPrimaryTopologyValidatedSeparately: false,
            secondaryDrawBindingsOwnedElsewhere: false);

    public CommandRecordingDependencyMismatch CompareCommandChainPrimary(
        in CommandRecordingDependencySignature current,
        bool secondaryDrawBindingsOwnedElsewhere)
        => Compare(current, commandChainPrimaryTopologyValidatedSeparately: true, secondaryDrawBindingsOwnedElsewhere);

    private CommandRecordingDependencyMismatch Compare(
        in CommandRecordingDependencySignature current,
        bool commandChainPrimaryTopologyValidatedSeparately,
        bool secondaryDrawBindingsOwnedElsewhere)
    {
        if (!commandChainPrimaryTopologyValidatedSeparately &&
            OutputPassAttachment != current.OutputPassAttachment)
            return Structural(CommandRecordingDependencyField.OutputPassAttachment);
        if (!commandChainPrimaryTopologyValidatedSeparately &&
            RenderArea != current.RenderArea)
            return Structural(CommandRecordingDependencyField.RenderArea);
        if (!commandChainPrimaryTopologyValidatedSeparately &&
            ViewMask != current.ViewMask)
            return Structural(CommandRecordingDependencyField.ViewMask);
        if (QueueFamily != current.QueueFamily)
            return Structural(CommandRecordingDependencyField.QueueFamily);
        if (!commandChainPrimaryTopologyValidatedSeparately &&
            DynamicRenderingInheritance != current.DynamicRenderingInheritance)
            return Structural(CommandRecordingDependencyField.DynamicRenderingInheritance);
        if (!commandChainPrimaryTopologyValidatedSeparately &&
            PipelineGeneration != current.PipelineGeneration)
            return Structural(CommandRecordingDependencyField.PipelineGeneration);
        if (!secondaryDrawBindingsOwnedElsewhere &&
            PipelineLayoutGeneration != current.PipelineLayoutGeneration)
            return Structural(CommandRecordingDependencyField.PipelineLayoutGeneration);
        if (!secondaryDrawBindingsOwnedElsewhere &&
            MeshBindingIdentity != current.MeshBindingIdentity)
            return Binding(CommandRecordingDependencyField.MeshBindingIdentity);
        if (!secondaryDrawBindingsOwnedElsewhere &&
            IndexBufferBindingIdentity != current.IndexBufferBindingIdentity)
            return Binding(CommandRecordingDependencyField.IndexBufferBindingIdentity);
        if (!secondaryDrawBindingsOwnedElsewhere &&
            VertexBufferBindingIdentity != current.VertexBufferBindingIdentity)
            return Binding(CommandRecordingDependencyField.VertexBufferBindingIdentity);
        if (BufferAllocationGeneration != current.BufferAllocationGeneration)
            return Binding(CommandRecordingDependencyField.BufferAllocationGeneration);
        // Command-chain group/resource-plan signatures own the image, framebuffer,
        // sampler, descriptor-layout, and descriptor-set identities for every pass.
        // The aggregate primary snapshot contains only the first visible pass, so
        // comparing these fallback-context fields would invalidate unrelated groups.
        if (!commandChainPrimaryTopologyValidatedSeparately &&
            ImageAllocationGeneration != current.ImageAllocationGeneration)
            return Binding(CommandRecordingDependencyField.ImageAllocationGeneration);
        if (!commandChainPrimaryTopologyValidatedSeparately &&
            ImageViewGeneration != current.ImageViewGeneration)
            return Binding(CommandRecordingDependencyField.ImageViewGeneration);
        if (!commandChainPrimaryTopologyValidatedSeparately &&
            SamplerAllocationGeneration != current.SamplerAllocationGeneration)
            return Binding(CommandRecordingDependencyField.SamplerAllocationGeneration);
        if (!commandChainPrimaryTopologyValidatedSeparately &&
            DescriptorLayoutGeneration != current.DescriptorLayoutGeneration)
            return Binding(CommandRecordingDependencyField.DescriptorLayoutGeneration);
        if (!commandChainPrimaryTopologyValidatedSeparately &&
            DescriptorSetGeneration != current.DescriptorSetGeneration)
            return Binding(CommandRecordingDependencyField.DescriptorSetGeneration);
        if (ResourcePlanGeneration != current.ResourcePlanGeneration)
            return Binding(CommandRecordingDependencyField.ResourcePlanGeneration);
        if (ExternalTargetVariant != current.ExternalTargetVariant)
            return Binding(CommandRecordingDependencyField.ExternalTargetVariant);
        if (FrameSlotVariant != current.FrameSlotVariant)
            return Binding(CommandRecordingDependencyField.FrameSlotVariant);
        if (DescriptorPublicationGeneration != current.DescriptorPublicationGeneration)
            return Binding(CommandRecordingDependencyField.DescriptorPublicationGeneration);
        if (DataPublicationGeneration != current.DataPublicationGeneration)
            return Data(CommandRecordingDependencyField.DataPublicationGeneration);
        if (VolatileSuffixGeneration != current.VolatileSuffixGeneration)
            return Data(CommandRecordingDependencyField.VolatileSuffixGeneration);

        return CommandRecordingDependencyMismatch.None;
    }

    private static CommandRecordingDependencyMismatch Structural(CommandRecordingDependencyField field)
        => new(field, CommandRecordingInvalidationClass.Structural);

    private static CommandRecordingDependencyMismatch Binding(CommandRecordingDependencyField field)
        => new(field, CommandRecordingInvalidationClass.BindingIdentity);

    private static CommandRecordingDependencyMismatch Data(CommandRecordingDependencyField field)
        => new(field, CommandRecordingInvalidationClass.DataOnly);
}
