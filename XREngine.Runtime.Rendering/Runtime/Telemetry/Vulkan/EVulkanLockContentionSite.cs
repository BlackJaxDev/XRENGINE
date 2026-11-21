namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Vulkan locks whose contended waits <see cref="VulkanLockContentionTelemetry"/> counts. They are
/// the locks that the two eye lanes of parallel OpenXR recording can share.
/// </summary>
public enum EVulkanLockContentionSite
{
    /// <summary>Not counted.</summary>
    None,
    /// <summary>
    /// The command-buffer bind state gate: one monitor for the bind state of every command buffer.
    /// Each tracked bind (pipeline, viewport and scissor, descriptor sets, push constants, vertex and
    /// index buffers) takes it.
    /// </summary>
    BindState,
    /// <summary>The draw recording lock of one mesh renderer. Both eyes record the same meshes.</summary>
    MeshRecord,
    /// <summary>The reservation lock of the mapped uniform arena (auto-uniform ranges).</summary>
    MappedArenaReservation,
    /// <summary>Descriptor publication locks (wait reason <c>DescriptorPublicationLock</c>).</summary>
    DescriptorPublication,
    /// <summary>The state gate of the OpenXR submission tracker.</summary>
    SubmissionTracker,
    /// <summary>Number of sites.</summary>
    Count,
}
