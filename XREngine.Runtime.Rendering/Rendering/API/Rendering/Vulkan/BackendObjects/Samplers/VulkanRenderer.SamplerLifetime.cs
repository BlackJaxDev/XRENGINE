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

    private void DestroyRemainingTrackedSamplers()
    {
        ulong[] handles;
        lock (_samplerLifetimeLock)
        {
            if (_liveSamplerHandles.Count == 0)
                return;

            handles = [.. _liveSamplerHandles];
            _liveSamplerHandles.Clear();
        }

        for (int i = 0; i < handles.Length; i++)
        {
            Sampler sampler = new() { Handle = handles[i] };
            Debug.Vulkan(
                "[Vulkan] Destroying remaining tracked sampler 0x{0:X} during renderer shutdown.",
                handles[i]);
            Api!.DestroySampler(device, sampler, null);
        }

        Debug.Vulkan(
            "[Vulkan] Destroyed {0} remaining tracked sampler(s) during renderer shutdown.",
            handles.Length);
    }
}
