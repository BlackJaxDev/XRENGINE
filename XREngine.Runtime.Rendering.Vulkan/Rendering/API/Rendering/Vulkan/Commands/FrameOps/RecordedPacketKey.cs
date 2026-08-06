using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable physical Vulkan state captured by packet lowering. A recorded
/// command chain is reusable only when this complete key is unchanged.
/// </summary>
internal readonly record struct RecordedPacketKey(
    RenderPacketExecutionDomain ExecutionDomain,
    VulkanRecordedRenderTargetSnapshot RenderTarget,
    ulong RenderArea,
    uint QueueFamily,
    VulkanRecordedDescriptorSetIdentityBuffer DescriptorSets,
    VulkanRecordedProgramIdentityBuffer Programs,
    VulkanRecordedBufferIdentity IndexBuffer,
    VulkanRecordedBufferIdentityBuffer VertexBuffers,
    VulkanRecordedBufferIdentityBuffer AuxiliaryBuffers)
{
    private readonly VulkanRecordedDescriptorSetIdentityBuffer _descriptorSets =
        DescriptorSets;

    public VulkanRecordedDescriptorSetIdentityBuffer DescriptorSets
    {
        get => _descriptorSets;
        init => _descriptorSets = value;
    }

    private static ref readonly VulkanRecordedDescriptorSetIdentityBuffer
        GetDescriptorSetsReference(in RecordedPacketKey key)
        => ref key._descriptorSets;

    /// <summary>
    /// Compares the large bounded native-identity buffers by reference. The
    /// synthesized record operator copies this packet (including up to 1,024
    /// descriptor resources) by value and can exhaust the render-thread stack.
    /// </summary>
    internal bool Matches(in RecordedPacketKey other)
    {
        ref readonly VulkanRecordedDescriptorSetIdentityBuffer descriptorSets =
            ref GetDescriptorSetsReference(in this);
        ref readonly VulkanRecordedDescriptorSetIdentityBuffer otherDescriptorSets =
            ref GetDescriptorSetsReference(in other);
        return ExecutionDomain == other.ExecutionDomain &&
            RenderTarget == other.RenderTarget &&
            RenderArea == other.RenderArea &&
            QueueFamily == other.QueueFamily &&
            descriptorSets.Equals(in otherDescriptorSets) &&
            Programs.Equals(other.Programs) &&
            IndexBuffer == other.IndexBuffer &&
            VertexBuffers.Equals(other.VertexBuffers) &&
            AuxiliaryBuffers.Equals(other.AuxiliaryBuffers);
    }

    /// <summary>
    /// Identifies the first native packet field that differs. This is used only
    /// on command-recording failure paths so an identity race can be diagnosed
    /// without expanding the large descriptor-resource buffers in every log.
    /// </summary>
    internal string DescribeFirstMismatch(in RecordedPacketKey other)
    {
        if (ExecutionDomain != other.ExecutionDomain)
            return nameof(ExecutionDomain);
        if (RenderTarget != other.RenderTarget)
            return nameof(RenderTarget);
        if (RenderArea != other.RenderArea)
            return nameof(RenderArea);
        if (QueueFamily != other.QueueFamily)
            return nameof(QueueFamily);

        ref readonly VulkanRecordedDescriptorSetIdentityBuffer descriptorSets =
            ref GetDescriptorSetsReference(in this);
        ref readonly VulkanRecordedDescriptorSetIdentityBuffer otherDescriptorSets =
            ref GetDescriptorSetsReference(in other);
        if (!descriptorSets.Equals(in otherDescriptorSets))
            return nameof(DescriptorSets);
        if (!Programs.Equals(other.Programs))
            return nameof(Programs);
        if (IndexBuffer != other.IndexBuffer)
            return nameof(IndexBuffer);
        if (!VertexBuffers.Equals(other.VertexBuffers))
            return nameof(VertexBuffers);
        if (!AuxiliaryBuffers.Equals(other.AuxiliaryBuffers))
            return nameof(AuxiliaryBuffers);

        return "<none>";
    }

    public ulong PipelineLayoutGeneration
    {
        get
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(Programs.Count);
            for (int i = 0; i < Programs.Count; i++)
                hash.Add(Programs.Get(i).PipelineLayoutGeneration);
            return hash.ToHash();
        }
    }

    public ulong PipelineGeneration
    {
        get
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(Programs.Count);
            for (int i = 0; i < Programs.Count; i++)
                hash.Add(Programs.Get(i).PipelineGeneration);
            return hash.ToHash();
        }
    }

    public bool IsComplete =>
        (ExecutionDomain != RenderPacketExecutionDomain.GraphicsRendering ||
         RenderTarget.IsComplete && RenderArea != 0UL) &&
        (ExecutionDomain == RenderPacketExecutionDomain.StandaloneSynchronization ||
         Programs.IsComplete) &&
        DescriptorSets.IsComplete &&
        IndexBuffer.IsComplete &&
        VertexBuffers.IsComplete &&
        AuxiliaryBuffers.IsComplete;

    internal void AddIdentityComponents(ref FrameOpSignatureHasher hash)
    {
        hash.Add((int)ExecutionDomain);
        hash.Add(RenderTarget.FramebufferHandle);
        hash.Add(RenderTarget.FramebufferGeneration);
        hash.Add(RenderArea);
        hash.Add(QueueFamily);
        hash.Add(DescriptorSets.Count);
        hash.Add(DescriptorSets.IsComplete);
        for (int i = 0; i < DescriptorSets.Count; i++)
        {
            VulkanRecordedDescriptorSetIdentity set = DescriptorSets.Get(i);
            hash.Add(set.SetIndex); hash.Add(set.DescriptorSetHandle);
            hash.Add(set.DescriptorSetLifetimeGeneration); hash.Add(set.PayloadGeneration);
            hash.Add(set.PublicationGeneration); hash.Add(set.Resources.Count); hash.Add(set.Resources.IsComplete);
            for (int j = 0; j < set.Resources.Count; j++) { VulkanRecordedDescriptorResourceIdentity resource = set.Resources.Get(j); hash.Add((int)resource.Type); hash.Add(resource.Handle); hash.Add(resource.Generation); hash.Add((int)resource.Layout); }
        }
        hash.Add(Programs.Count);
        hash.Add(Programs.IsComplete);
        for (int i = 0; i < Programs.Count; i++)
        {
            VulkanRecordedProgramIdentity program = Programs.Get(i);
            hash.Add(program.ProgramBindingId);
            hash.Add(program.ProgramLinkGeneration);
            hash.Add(program.PipelineLayoutHandle);
            hash.Add(program.PipelineLayoutGeneration);
            hash.Add(program.PipelineHandle);
            hash.Add(program.PipelineGeneration);
        }
        AddBuffer(ref hash, IndexBuffer);
        AddBufferBuffer(ref hash, VertexBuffers);
        AddBufferBuffer(ref hash, AuxiliaryBuffers);

        for (int i = 0; i < RenderTarget.AttachmentCount; i++)
        {
            VulkanNativeAttachmentIdentity attachment = RenderTarget.GetAttachment(i);
            hash.Add(attachment.ImageHandle);
            hash.Add(attachment.ImageGeneration);
            hash.Add(attachment.ImageViewHandle);
            hash.Add(attachment.ImageViewGeneration);
            hash.Add((int)attachment.ExpectedLayout);
        }
    }

    private static void AddBuffer(ref FrameOpSignatureHasher hash, in VulkanRecordedBufferIdentity buffer)
    {
        hash.Add(buffer.BufferHandle);
        hash.Add((int)buffer.Kind);
        hash.Add(buffer.Binding);
        hash.Add(buffer.AllocationGeneration);
        hash.Add(buffer.Offset);
        hash.Add(buffer.Range);
    }

    private static void AddBufferBuffer(
        ref FrameOpSignatureHasher hash,
        in VulkanRecordedBufferIdentityBuffer buffers)
    {
        hash.Add(buffers.Count);
        hash.Add(buffers.IsComplete);
        for (int i = 0; i < buffers.Count; i++)
            AddBuffer(ref hash, buffers.Get(i));
    }

}
