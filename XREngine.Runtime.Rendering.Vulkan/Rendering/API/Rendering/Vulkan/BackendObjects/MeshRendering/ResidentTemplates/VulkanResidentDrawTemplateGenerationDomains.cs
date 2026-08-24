namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Independently invalidated generations captured by a resident draw template.
/// Data-content changes deliberately do not require a structural template rebuild.
/// </summary>
internal readonly record struct VulkanResidentDrawTemplateGenerationDomains(
    ulong DataContent,
    ulong ResourceTable,
    ulong LayoutTopology,
    ulong Recording)
{
    internal bool IsStructurallyCompatibleWith(
        in VulkanResidentDrawTemplateGenerationDomains other)
        => ResourceTable == other.ResourceTable &&
           LayoutTopology == other.LayoutTopology &&
           Recording == other.Recording;

    internal bool HasOnlyDataContentChangedFrom(
        in VulkanResidentDrawTemplateGenerationDomains other)
        => IsStructurallyCompatibleWith(in other) &&
           DataContent != other.DataContent;
}
