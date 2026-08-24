using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable producer-visible publication of one generation-checked resident
/// template address. Replacing the reference atomically avoids torn multi-word
/// handle reads between render staging and visibility collection.
/// </summary>
internal sealed class VulkanResidentDrawTemplatePublication
{
    internal VulkanResidentDrawTemplatePublication(
        in VulkanResidentDrawTemplateHandle handle,
        in VulkanResidentDrawTemplateVariantKey variant)
    {
        Handle = handle;
        Variant = variant;
    }

    internal VulkanResidentDrawTemplateHandle Handle { get; }
    internal VulkanResidentDrawTemplateVariantKey Variant { get; }

    internal bool Matches(
        in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw,
        in VulkanResidentDrawTemplateVariantKey expectedVariant)
    {
        AdvancedGpuSceneDrawIdentity primary = canonicalDraw.Primary;
        return Handle.IsValid &&
            primary.IsValid &&
            Handle.PrimaryIndex == primary.Handle.Index &&
            Handle.CanonicalHandleGeneration == primary.Handle.Generation &&
            Handle.DatabaseEpoch == primary.DatabaseEpoch &&
            Variant == expectedVariant;
    }
}
