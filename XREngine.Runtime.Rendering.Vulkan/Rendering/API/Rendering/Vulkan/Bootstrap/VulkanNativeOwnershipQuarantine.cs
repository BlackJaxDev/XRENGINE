namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Keeps failed native teardown reachable until a successful retry or process
/// exit. Admission stops while ownership is unresolved so repeated failures
/// cannot accumulate abandoned devices.
/// </summary>
internal static class VulkanNativeOwnershipQuarantine
{
    private static readonly object Gate = new();
    private static readonly HashSet<VulkanRenderer> Owners = [];

    internal static void Retain(VulkanRenderer renderer)
    {
        lock (Gate)
            Owners.Add(renderer);
    }

    internal static void Release(VulkanRenderer renderer)
    {
        lock (Gate)
            Owners.Remove(renderer);
    }

    internal static void ThrowIfOccupied()
    {
        lock (Gate)
            if (Owners.Count != 0)
                throw new InvalidOperationException(
                    "A Vulkan renderer retains native ownership after failed teardown. " +
                    "Retry its cleanup after completion or restart the process before creating another Vulkan renderer.");
    }
}
