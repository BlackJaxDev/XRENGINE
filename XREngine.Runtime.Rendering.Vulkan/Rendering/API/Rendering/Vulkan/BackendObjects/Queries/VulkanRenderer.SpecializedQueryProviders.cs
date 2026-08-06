namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal void RegisterSpecializedQueryProvider(IVulkanSpecializedQueryProvider provider)
        => ResourceRuntime.Queries.Register(provider);

    internal void UnregisterSpecializedQueryProvider(IVulkanSpecializedQueryProvider provider)
        => ResourceRuntime.Queries.Unregister(provider);

    internal bool TryGetSpecializedQueryProvider(
        ERenderQueryKind kind,
        out IVulkanSpecializedQueryProvider provider)
        => ResourceRuntime.Queries.TryGet(kind, out provider);
}
