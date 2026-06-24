using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly object _samplerLifetimeLock = new();
    private readonly HashSet<ulong> _liveSamplerHandles = [];

    internal void RegisterLiveSampler(Sampler sampler)
    {
        if (sampler.Handle == 0)
            return;

        lock (_samplerLifetimeLock)
            _liveSamplerHandles.Add(sampler.Handle);
    }

    internal void UnregisterLiveSampler(Sampler sampler)
    {
        if (sampler.Handle == 0)
            return;

        lock (_samplerLifetimeLock)
            _liveSamplerHandles.Remove(sampler.Handle);
    }

    internal bool IsLiveSampler(Sampler sampler)
    {
        if (sampler.Handle == 0)
            return false;

        lock (_samplerLifetimeLock)
            return _liveSamplerHandles.Contains(sampler.Handle);
    }
}
