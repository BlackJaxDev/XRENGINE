using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Owned, immutable identity for a parsed render-graph resource binding.
/// </summary>
internal readonly record struct VulkanResourceBindingKey(
    EVulkanResourceBindingKind Kind,
    string Name,
    string Slot)
{
    public bool IsExplicit
        => Kind is EVulkanResourceBindingKind.Texture
            or EVulkanResourceBindingKind.FrameBuffer
            or EVulkanResourceBindingKind.Buffer;

    public static bool TryParse(string? binding, out VulkanResourceBindingKey key)
    {
        if (binding is null || !VulkanResourceBindingView.TryParse(binding.AsSpan(), out VulkanResourceBindingView view))
        {
            key = default;
            return false;
        }

        key = new(view.Kind, view.Name.ToString(), view.Slot.ToString());
        return true;
    }
}