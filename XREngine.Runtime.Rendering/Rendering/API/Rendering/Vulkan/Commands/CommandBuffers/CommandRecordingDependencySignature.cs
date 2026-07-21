namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable dependency snapshot shared by primary variants, secondary ranges,
/// and command-chain schedules. Data-publication fields are intentionally kept
/// separate from binding identity so completed frame slots can refresh bytes
/// without rebuilding compatible command topology.
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
    {
        if (OutputPassAttachment != current.OutputPassAttachment)
            return Structural(CommandRecordingDependencyField.OutputPassAttachment);
        if (RenderArea != current.RenderArea)
            return Structural(CommandRecordingDependencyField.RenderArea);
        if (ViewMask != current.ViewMask)
            return Structural(CommandRecordingDependencyField.ViewMask);
        if (QueueFamily != current.QueueFamily)
            return Structural(CommandRecordingDependencyField.QueueFamily);
        if (DynamicRenderingInheritance != current.DynamicRenderingInheritance)
            return Structural(CommandRecordingDependencyField.DynamicRenderingInheritance);
        if (PipelineGeneration != current.PipelineGeneration)
            return Structural(CommandRecordingDependencyField.PipelineGeneration);
        if (PipelineLayoutGeneration != current.PipelineLayoutGeneration)
            return Structural(CommandRecordingDependencyField.PipelineLayoutGeneration);
        if (MeshBindingIdentity != current.MeshBindingIdentity)
            return Binding(CommandRecordingDependencyField.MeshBindingIdentity);
        if (IndexBufferBindingIdentity != current.IndexBufferBindingIdentity)
            return Binding(CommandRecordingDependencyField.IndexBufferBindingIdentity);
        if (VertexBufferBindingIdentity != current.VertexBufferBindingIdentity)
            return Binding(CommandRecordingDependencyField.VertexBufferBindingIdentity);
        if (BufferAllocationGeneration != current.BufferAllocationGeneration)
            return Binding(CommandRecordingDependencyField.BufferAllocationGeneration);
        if (ImageAllocationGeneration != current.ImageAllocationGeneration)
            return Binding(CommandRecordingDependencyField.ImageAllocationGeneration);
        if (ImageViewGeneration != current.ImageViewGeneration)
            return Binding(CommandRecordingDependencyField.ImageViewGeneration);
        if (SamplerAllocationGeneration != current.SamplerAllocationGeneration)
            return Binding(CommandRecordingDependencyField.SamplerAllocationGeneration);
        if (DescriptorLayoutGeneration != current.DescriptorLayoutGeneration)
            return Binding(CommandRecordingDependencyField.DescriptorLayoutGeneration);
        if (DescriptorSetGeneration != current.DescriptorSetGeneration)
            return Binding(CommandRecordingDependencyField.DescriptorSetGeneration);
        if (ResourcePlanGeneration != current.ResourcePlanGeneration)
            return Binding(CommandRecordingDependencyField.ResourcePlanGeneration);
        if (ExternalTargetVariant != current.ExternalTargetVariant)
            return Binding(CommandRecordingDependencyField.ExternalTargetVariant);
        if (FrameSlotVariant != current.FrameSlotVariant)
            return Binding(CommandRecordingDependencyField.FrameSlotVariant);
        if (DescriptorPublicationGeneration != current.DescriptorPublicationGeneration)
            return Data(CommandRecordingDependencyField.DescriptorPublicationGeneration);
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
