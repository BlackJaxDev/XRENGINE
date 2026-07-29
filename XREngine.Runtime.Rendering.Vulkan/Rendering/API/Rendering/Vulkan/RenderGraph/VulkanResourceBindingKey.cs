using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Parsed, immutable identity for the render-graph resource binding grammar.
/// </summary>
internal readonly record struct VulkanResourceBindingKey(
    EVulkanResourceBindingKind Kind,
    string Name,
    string Slot)
{
    private const string TexturePrefix = "tex::";
    private const string FrameBufferPrefix = "fbo::";
    private const string BufferPrefix = "buf::";

    public bool IsExplicit
        => Kind is EVulkanResourceBindingKind.Texture
            or EVulkanResourceBindingKind.FrameBuffer
            or EVulkanResourceBindingKind.Buffer;

    public static bool TryParse(string? binding, out VulkanResourceBindingKey key)
    {
        if (string.IsNullOrWhiteSpace(binding))
        {
            key = default;
            return false;
        }

        if (binding.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
        {
            key = new(EVulkanResourceBindingKind.Output, RenderGraphResourceNames.OutputRenderTarget, string.Empty);
            return true;
        }

        if (binding.StartsWith(TexturePrefix, StringComparison.OrdinalIgnoreCase))
            return TryParsePrefixed(binding, TexturePrefix, EVulkanResourceBindingKind.Texture, out key);

        if (binding.StartsWith(BufferPrefix, StringComparison.OrdinalIgnoreCase))
            return TryParsePrefixed(binding, BufferPrefix, EVulkanResourceBindingKind.Buffer, out key);

        if (binding.StartsWith(FrameBufferPrefix, StringComparison.OrdinalIgnoreCase))
            return TryParsePrefixed(binding, FrameBufferPrefix, EVulkanResourceBindingKind.FrameBuffer, out key);

        key = new(EVulkanResourceBindingKind.Unqualified, binding, string.Empty);
        return true;
    }

    private static bool TryParsePrefixed(
        string binding,
        string prefix,
        EVulkanResourceBindingKind kind,
        out VulkanResourceBindingKey key)
    {
        if (!binding.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            key = default;
            return false;
        }

        int nameStart = prefix.Length;
        int slotSeparator = binding.IndexOf("::", nameStart, StringComparison.Ordinal);
        int nameLength = (slotSeparator < 0 ? binding.Length : slotSeparator) - nameStart;
        if (nameLength <= 0)
        {
            key = default;
            return false;
        }

        string name = binding.Substring(nameStart, nameLength);
        string slot = kind == EVulkanResourceBindingKind.FrameBuffer
            ? slotSeparator >= 0 && slotSeparator + 2 < binding.Length
                ? binding[(slotSeparator + 2)..]
                : "color"
            : string.Empty;

        key = new(kind, name, slot);
        return true;
    }
}
