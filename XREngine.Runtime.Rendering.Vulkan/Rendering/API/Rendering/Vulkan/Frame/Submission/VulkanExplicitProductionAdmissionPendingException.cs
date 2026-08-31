namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Signals that an explicit production frame was not admitted because a required
/// native resource is still preparing or a required publication budget was
/// exhausted. No target was acquired or command buffer submitted, so
/// the caller may retry the same logical step after scheduled work advances.
/// </summary>
public sealed class VulkanExplicitProductionAdmissionPendingException(
    string admissionStage,
    string detail) : InvalidOperationException(detail)
{
    /// <summary>Logical admission boundary that deferred the unsubmitted frame.</summary>
    public string AdmissionStage { get; } = admissionStage;
}
