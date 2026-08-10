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
    ulong VolatileSuffixGeneration,
    VulkanRecordedRenderTargetSnapshot RenderTargetSnapshot = default,
    RecordedPacketKey RecordedPacketKey = default)
{
    private readonly RecordedPacketKey _recordedPacketKey = RecordedPacketKey;

    public RecordedPacketKey RecordedPacketKey
    {
        get => _recordedPacketKey;
        init => _recordedPacketKey = value;
    }

    internal static ref readonly RecordedPacketKey GetRecordedPacketKeyReference(
        in CommandRecordingDependencySignature signature)
        => ref signature._recordedPacketKey;

    /// <summary>
    /// Captures the identity components of this dependency signature,
    /// which can be used for comparison with other signatures to determine
    /// if they are equivalent in terms of their resource generations,
    /// render scope inheritance, queue assumptions, and other relevant factors.
    /// </summary>
    /// <returns>The captured identity components of this dependency signature.</returns>
    internal VulkanCommandIdentityComponents CaptureIdentityComponents()
    {
        FrameOpSignatureHasher resourceGenerations = new();
        resourceGenerations.Add(PipelineGeneration);
        resourceGenerations.Add(PipelineLayoutGeneration);
        resourceGenerations.Add(BufferAllocationGeneration);
        resourceGenerations.Add(ImageAllocationGeneration);
        resourceGenerations.Add(ImageViewGeneration);
        resourceGenerations.Add(SamplerAllocationGeneration);
        resourceGenerations.Add(DescriptorLayoutGeneration);
        resourceGenerations.Add(DescriptorSetGeneration);
        ref readonly RecordedPacketKey recordedPacketKey =
            ref GetRecordedPacketKeyReference(in this);
        recordedPacketKey.AddIdentityComponents(ref resourceGenerations);

        FrameOpSignatureHasher renderScopeInheritance = new();
        renderScopeInheritance.Add(OutputPassAttachment);
        renderScopeInheritance.Add(RenderArea);
        renderScopeInheritance.Add(ViewMask);
        renderScopeInheritance.Add(DynamicRenderingInheritance);

        FrameOpSignatureHasher queueAssumptions = new();
        queueAssumptions.Add(QueueFamily);
        queueAssumptions.Add(ExternalTargetVariant);
        queueAssumptions.Add(FrameSlotVariant);

        FrameOpSignatureHasher primaryOnly = new();
        primaryOnly.Add(OutputPassAttachment);
        primaryOnly.Add(RenderArea);
        primaryOnly.Add(ViewMask);
        primaryOnly.Add(ResourcePlanGeneration);
        primaryOnly.Add(ExternalTargetVariant);

        FrameOpSignatureHasher secondaryOnly = new();
        secondaryOnly.Add(PipelineGeneration);
        secondaryOnly.Add(PipelineLayoutGeneration);
        secondaryOnly.Add(MeshBindingIdentity);
        secondaryOnly.Add(IndexBufferBindingIdentity);
        secondaryOnly.Add(VertexBufferBindingIdentity);
        secondaryOnly.Add(DescriptorPublicationGeneration);

        FrameOpSignatureHasher dataContent = new();
        dataContent.Add(DataPublicationGeneration);
        dataContent.Add(VolatileSuffixGeneration);

        return new VulkanCommandIdentityComponents(
            OrderedNodes: 0,
            resourceGenerations.ToHash(),
            renderScopeInheritance.ToHash(),
            queueAssumptions.ToHash(),
            NestedArtifacts: 0,
            primaryOnly.ToHash(),
            secondaryOnly.ToHash(),
            dataContent.ToHash());
    }

    /// <summary>
    /// Compares this dependency signature with another signature to determine if they are equivalent
    /// in terms of their resource generations, render scope inheritance, queue assumptions, and other relevant factors.
    /// </summary>
    /// <param name="current">The dependency signature to compare against.</param>
    /// <returns>A CommandRecordingDependencyMismatch indicating the differences between the signatures, if any.</returns>
    public CommandRecordingDependencyMismatch Compare(in CommandRecordingDependencySignature current)
        => Compare(
            current,
            commandChainPrimaryTopologyValidatedSeparately: false,
            compareResourcePlanGeneration: true);

    /// <summary>
    /// Compares dependencies for a command-chain secondary. Concrete packet and
    /// native-resource identities remain recording dependencies, while the coarse
    /// renderer-wide planning epoch is ignored because it can advance when an
    /// unrelated logical resource or visibility cohort changes.
    /// </summary>
    /// <param name="current">The dependency signature to compare against.</param>
    /// <returns>A mismatch for the first recording-visible dependency that changed.</returns>
    public CommandRecordingDependencyMismatch CompareCommandChainSecondary(
        in CommandRecordingDependencySignature current)
        => Compare(
            current,
            commandChainPrimaryTopologyValidatedSeparately: false,
            compareResourcePlanGeneration: false);

    /// <summary>
    /// Compares this dependency signature with another signature to determine if they are equivalent
    /// in terms of their resource generations, render scope inheritance, queue assumptions,
    /// and other relevant factors, specifically for command-chain primary variants where the topology is validated separately.
    /// </summary>
    /// <param name="current">The dependency signature to compare against.</param>
    /// <returns>A CommandRecordingDependencyMismatch indicating the differences between the signatures, if any.</returns>
    public CommandRecordingDependencyMismatch CompareCommandChainPrimary(
        in CommandRecordingDependencySignature current)
        => Compare(
            current,
            commandChainPrimaryTopologyValidatedSeparately: true,
            compareResourcePlanGeneration: false);

    /// <summary>
    /// Compares this dependency signature with another signature to determine if they are equivalent
    /// in terms of their resource generations, render scope inheritance, queue assumptions,
    /// and other relevant factors.
    /// </summary>
    /// <param name="current">The dependency signature to compare against.</param>
    /// <param name="commandChainPrimaryTopologyValidatedSeparately">Indicates whether the command-chain primary topology is validated separately.</param>
    /// <param name="compareResourcePlanGeneration">Indicates whether the coarse renderer-wide resource-plan epoch is itself a recording dependency.</param>
    /// <returns>A CommandRecordingDependencyMismatch indicating the differences between the signatures, if any.</returns>
    private CommandRecordingDependencyMismatch Compare(
        in CommandRecordingDependencySignature current,
        bool commandChainPrimaryTopologyValidatedSeparately,
        bool compareResourcePlanGeneration)
    {
        ref readonly RecordedPacketKey recordedPacketKey =
            ref GetRecordedPacketKeyReference(in this);
        ref readonly RecordedPacketKey currentRecordedPacketKey =
            ref GetRecordedPacketKeyReference(in current);
        if (!recordedPacketKey.Matches(in currentRecordedPacketKey))
            return Binding(CommandRecordingDependencyField.RecordedPacketKey);

        if (!commandChainPrimaryTopologyValidatedSeparately &&
            OutputPassAttachment != current.OutputPassAttachment)
            return Structural(CommandRecordingDependencyField.OutputPassAttachment);

        if (RenderTargetSnapshot != current.RenderTargetSnapshot)
            return Binding(CommandRecordingDependencyField.RenderTargetSnapshot);

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

        // Command-chain primaries validate concrete topology separately, and
        // secondaries compare exact packet/native-resource identities above. A
        // renderer-wide planner revision can advance when streaming or camera
        // visibility changes an unrelated logical resource; treating that coarse
        // epoch as a binding dependency turns camera motion into a full-frame
        // secondary re-record without improving native-resource safety.
        if (compareResourcePlanGeneration &&
            ResourcePlanGeneration != current.ResourcePlanGeneration)
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

    /// <summary>
    /// Creates a CommandRecordingDependencyMismatch indicating a structural mismatch for the specified field.
    /// </summary>
    /// <param name="field">The field that caused the structural mismatch.</param>
    /// <returns>A CommandRecordingDependencyMismatch representing the structural mismatch.</returns>
    private static CommandRecordingDependencyMismatch Structural(CommandRecordingDependencyField field)
        => new(field, CommandRecordingInvalidationClass.Structural);

    /// <summary>
    /// Creates a CommandRecordingDependencyMismatch indicating a binding identity mismatch for the specified field.
    /// </summary>
    /// <param name="field">The field that caused the binding identity mismatch.</param>
    /// <returns>A CommandRecordingDependencyMismatch representing the binding identity mismatch.</returns>
    private static CommandRecordingDependencyMismatch Binding(CommandRecordingDependencyField field)
        => new(field, CommandRecordingInvalidationClass.BindingIdentity);

    /// <summary>
    /// Creates a CommandRecordingDependencyMismatch indicating a data-only mismatch for the specified field.
    /// </summary>
    /// <param name="field">The field that caused the data-only mismatch.</param>
    /// <returns>A CommandRecordingDependencyMismatch representing the data-only mismatch.</returns>
    private static CommandRecordingDependencyMismatch Data(CommandRecordingDependencyField field)
        => new(field, CommandRecordingInvalidationClass.DataOnly);
}
