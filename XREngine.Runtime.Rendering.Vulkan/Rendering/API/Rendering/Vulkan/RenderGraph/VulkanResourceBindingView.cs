using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Allocation-free parsed view over one render-graph binding expression.
/// The view may not outlive the source span.
/// </summary>
internal readonly ref struct VulkanResourceBindingView(
    EVulkanResourceBindingKind kind,
    ReadOnlySpan<char> name,
    ReadOnlySpan<char> slot)
{
    private const string TexturePrefix = "tex::";
    private const string FrameBufferPrefix = "fbo::";
    private const string BufferPrefix = "buf::";

    public EVulkanResourceBindingKind Kind { get; } = kind;
    public ReadOnlySpan<char> Name { get; } = name;
    public ReadOnlySpan<char> Slot { get; } = slot;

    public static bool TryParse(ReadOnlySpan<char> binding, out VulkanResourceBindingView view)
    {
        binding = binding.Trim();
        if (binding.IsEmpty)
        {
            view = default;
            return false;
        }

        if (binding.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
        {
            view = new(EVulkanResourceBindingKind.Output, binding, []);
            return true;
        }

        if (binding.StartsWith(TexturePrefix, StringComparison.OrdinalIgnoreCase))
            return TryParsePrefixed(binding, TexturePrefix, EVulkanResourceBindingKind.Texture, out view);
        if (binding.StartsWith(BufferPrefix, StringComparison.OrdinalIgnoreCase))
            return TryParsePrefixed(binding, BufferPrefix, EVulkanResourceBindingKind.Buffer, out view);
        if (binding.StartsWith(FrameBufferPrefix, StringComparison.OrdinalIgnoreCase))
            return TryParsePrefixed(binding, FrameBufferPrefix, EVulkanResourceBindingKind.FrameBuffer, out view);
        view = new(EVulkanResourceBindingKind.Unqualified, binding, []);
        return true;
    }

    private static bool TryParsePrefixed(
        ReadOnlySpan<char> binding,
        ReadOnlySpan<char> prefix,
        EVulkanResourceBindingKind kind,
        out VulkanResourceBindingView view)
    {
        if (!binding.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            view = default;
            return false;
        }

        ReadOnlySpan<char> remainder = binding[prefix.Length..];
        int slotSeparator = remainder.IndexOf("::", StringComparison.Ordinal);
        ReadOnlySpan<char> name = slotSeparator < 0 ? remainder : remainder[..slotSeparator];
        if (name.IsEmpty || name.IsWhiteSpace())
        {
            view = default;
            return false;
        }

        ReadOnlySpan<char> slot = kind == EVulkanResourceBindingKind.FrameBuffer
            ? slotSeparator >= 0 && slotSeparator + 2 < remainder.Length
                ? remainder[(slotSeparator + 2)..]
                : "color"
            : [];
        view = new(kind, name, slot);
        return true;
    }
}