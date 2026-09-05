namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable native clear values for one advanced visibility target.</summary>
internal readonly record struct VulkanAdvancedVisibilityClearPolicy(bool ReversedDepth)
{
    internal static bool TryCreate(
        in RenderFrameViewSet views,
        out VulkanAdvancedVisibilityClearPolicy policy)
    {
        bool reversedDepth = views.GetView(0).ReversedDepth;
        for (int index = 1; index < views.ViewCount; index++)
            if (views.GetView(index).ReversedDepth != reversedDepth)
            {
                policy = default;
                return false;
            }

        policy = new(reversedDepth);
        return true;
    }
}
