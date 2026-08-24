using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Represents a cached Vulkan descriptor update template, including its template, signature, and hash.
/// </summary>
internal sealed class CachedDescriptorUpdateTemplate
{
    /// <summary>
    /// The Vulkan descriptor update template associated with this cached entry.
    /// </summary>
    public required DescriptorUpdateTemplate Template;
    /// <summary>
    /// The signature of the descriptor update template, representing the bindings and their types.
    /// </summary>
    public required DescriptorUpdateTemplateSignature[] Signature;
    /// <summary>
    /// The hash of the descriptor update template, used for quick comparisons and cache validation.
    /// </summary>
    public required ulong Hash;
}
