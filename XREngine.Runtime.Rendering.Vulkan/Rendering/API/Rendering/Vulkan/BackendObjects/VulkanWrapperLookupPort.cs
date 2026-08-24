using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Behavior-only wrapper lookup boundary. Consumers can request a generated
/// wrapper but cannot obtain the factory's deferred ports or authority roots.
/// </summary>
internal sealed class VulkanWrapperLookupPort(VulkanBackendObjectContext context)
{
    /// <summary>
    /// Preserves the existing call shape for wrappers while exposing only lookup
    /// behavior.  In particular, this is not a route back to the factory's
    /// deferred authority-port publisher.
    /// </summary>
    internal VulkanWrapperLookupPort Lookup => this;

    /// <summary>
    /// Returns a wrapper that has already been created for this resource generation.
    /// Creation is deliberately not available here: a retained lookup must not carry
    /// the cold multi-authority composition needed to bind a new wrapper family.
    /// </summary>
    internal AbstractRenderAPIObject GetOrCreate(GenericRenderObject renderObject, bool generateNow = false)
    {
        ArgumentNullException.ThrowIfNull(renderObject);
        AbstractRenderAPIObject? wrapper = context.Resources.BackendObjects.Get(renderObject);
        if (wrapper is not null && generateNow && !wrapper.IsGenerated)
            wrapper.Generate();
        return wrapper!;
    }
}
