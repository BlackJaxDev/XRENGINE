using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Advances Vulkan recording and resource generations without publishing the
/// reserved zero value.
/// </summary>
internal static class VulkanGeneration
{
    internal static ulong NextNonZero(ulong current)
    {
        ulong next = unchecked(current + 1UL);
        return next == 0UL ? 1UL : next;
    }

    internal static ulong IncrementNonZero(ref long counter)
    {
        long next = Interlocked.Increment(ref counter);
        if (next != 0L)
            return unchecked((ulong)next);

        return unchecked((ulong)Interlocked.Increment(ref counter));
    }
}
