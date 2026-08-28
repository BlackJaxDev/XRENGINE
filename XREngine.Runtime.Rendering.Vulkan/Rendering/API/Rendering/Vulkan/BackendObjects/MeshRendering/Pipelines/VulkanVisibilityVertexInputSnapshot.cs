using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable vertex-input interface captured for the dedicated visibility
/// raster program. It deliberately does not reuse the renderer's mutable
/// ordinary-program vertex-input cache because shader reflection can assign a
/// different interface to the same mesh buffers.
/// </summary>
internal readonly struct VulkanVisibilityVertexInputSnapshot
{
    private readonly VertexInputBindingDescription[] _bindings;
    private readonly VertexInputAttributeDescription[] _attributes;

    internal VulkanVisibilityVertexInputSnapshot(
        ReadOnlySpan<VertexInputBindingDescription> bindings,
        ReadOnlySpan<VertexInputAttributeDescription> attributes,
        ulong layoutHash)
    {
        _bindings = bindings.ToArray();
        _attributes = attributes.ToArray();
        LayoutHash = layoutHash;
    }

    internal ReadOnlySpan<VertexInputBindingDescription> Bindings => _bindings;
    internal ReadOnlySpan<VertexInputAttributeDescription> Attributes => _attributes;
    internal ulong LayoutHash { get; }

    internal bool IsValid
        => _bindings is { Length: > 0 } &&
           _attributes is { Length: > 0 } &&
           LayoutHash != 0UL;
}
