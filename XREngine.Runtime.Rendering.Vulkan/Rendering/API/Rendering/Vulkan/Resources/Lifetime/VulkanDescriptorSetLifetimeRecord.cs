using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Mutable descriptor-set generation owned by the descriptor lifetime authority.
/// Access is serialized by <see cref="VulkanResourceLifetimeTracker.SyncRoot"/>.
/// </summary>
internal sealed class VulkanDescriptorSetLifetimeRecord
{
    internal readonly Dictionary<(uint Binding, uint Element), VulkanDescriptorReferencePair> References = new();
    internal readonly Dictionary<(uint Binding, uint Element), VulkanDescriptorImageReference> ImageReferences = new();
    internal readonly Dictionary<(uint Binding, uint Element), VulkanDescriptorPayload> Payloads = new();
    internal readonly HashSet<uint> ReflectedImageBindings = [];
    internal readonly Dictionary<VulkanResourceLifetimeKey, ulong> PinnedReferences = [];
    internal readonly HashSet<VulkanResourceLifetimeKey> IndexedReferences = [];
    internal DescriptorPool Pool;
    internal bool UsesUpdateAfterBind;
    internal bool HasReflection;
    internal string Owner = string.Empty;
    internal ulong Generation;
    /// <summary>
    /// Changes only when the image view/layout/type payload changes. Secondary
    /// command buffers use this narrower generation to validate their frozen
    /// image-layout requirements; update-after-bind buffer writes do not alter
    /// those requirements and must not invalidate otherwise reusable commands.
    /// </summary>
    internal ulong ImagePayloadGeneration;
}
