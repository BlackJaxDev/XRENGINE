namespace XREngine;

public readonly record struct ViewFoveationAttachmentContext(
    EVrFoveationAttachmentKind Kind,
    ulong ResourceKey,
    string? ResourceName,
    bool OwnedByResourcePlanner)
{
    public bool IsActive => Kind != EVrFoveationAttachmentKind.None && ResourceKey != 0UL;

    public static ViewFoveationAttachmentContext None => default;

    public static ViewFoveationAttachmentContext FromCapability(
        EVrFoveationCapabilityPath capabilityPath,
        ulong backendResourceKey)
    {
        EVrFoveationAttachmentKind kind = capabilityPath switch
        {
            EVrFoveationCapabilityPath.VulkanFragmentShadingRate => EVrFoveationAttachmentKind.VulkanFragmentShadingRate,
            EVrFoveationCapabilityPath.VulkanFragmentDensityMap => EVrFoveationAttachmentKind.VulkanFragmentDensityMap,
            _ => EVrFoveationAttachmentKind.None,
        };

        if (kind == EVrFoveationAttachmentKind.None || backendResourceKey == 0UL)
            return None;

        return new(
            kind,
            backendResourceKey,
            $"OpenXR.Foveation.{kind}.0x{backendResourceKey:X16}",
            OwnedByResourcePlanner: true);
    }
}
