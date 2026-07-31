namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable combined generations for typed callback values grouped by their
/// declared publication owner.
/// </summary>
internal readonly record struct VulkanBindingFrequencyGenerations(
    ulong Frame,
    ulong View,
    ulong Pass,
    ulong Material,
    ulong Object,
    ulong Instance,
    ulong RuntimeCallback)
{
    internal ulong Get(EVulkanBindingFrequency frequency)
        => frequency switch
        {
            EVulkanBindingFrequency.Frame => Frame,
            EVulkanBindingFrequency.View => View,
            EVulkanBindingFrequency.Pass => Pass,
            EVulkanBindingFrequency.Material => Material,
            EVulkanBindingFrequency.Object => Object,
            EVulkanBindingFrequency.Instance => Instance,
            EVulkanBindingFrequency.RuntimeCallback => RuntimeCallback,
            _ => 0UL,
        };
}
