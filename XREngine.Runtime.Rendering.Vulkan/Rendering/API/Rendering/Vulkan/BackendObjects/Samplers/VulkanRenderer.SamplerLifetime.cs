using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal void RegisterLiveSampler(Sampler sampler)
    {
        if (sampler.Handle == 0)
            return;

        VulkanDescriptorManager descriptors = ResourceRuntime.Descriptors;
        lock (descriptors.SamplerLifetimeLock)
            descriptors.LiveSamplerHandles.Add(sampler.Handle);
        RegisterVulkanResource(ObjectType.Sampler, sampler.Handle, "Sampler");
    }

    internal void RegisterLiveSampler(Sampler sampler, in SamplerCreateInfo createInfo)
    {
        if (sampler.Handle == 0)
            return;

        VulkanDescriptorManager descriptors = ResourceRuntime.Descriptors;
        lock (descriptors.SamplerLifetimeLock)
        {
            descriptors.LiveSamplerHandles.Add(sampler.Handle);
            descriptors.DescriptorHeapSamplerCreateInfos[sampler.Handle] = createInfo with { PNext = null };
        }
        RegisterVulkanResource(ObjectType.Sampler, sampler.Handle, "Sampler");
    }

    internal void UnregisterLiveSampler(Sampler sampler)
    {
        if (sampler.Handle == 0)
            return;

        VulkanDescriptorManager descriptors = ResourceRuntime.Descriptors;
        lock (descriptors.SamplerLifetimeLock)
        {
            descriptors.LiveSamplerHandles.Remove(sampler.Handle);
            descriptors.DescriptorHeapSamplerCreateInfos.Remove(sampler.Handle);
        }
    }

    internal bool IsLiveSampler(Sampler sampler)
    {
        if (sampler.Handle == 0)
            return false;

        VulkanDescriptorManager descriptors = ResourceRuntime.Descriptors;
        lock (descriptors.SamplerLifetimeLock)
            return descriptors.LiveSamplerHandles.Contains(sampler.Handle);
    }

    internal bool TryGetDescriptorHeapSamplerCreateInfo(Sampler sampler, out SamplerCreateInfo createInfo)
    {
        if (sampler.Handle != 0)
        {
            VulkanDescriptorManager descriptors = ResourceRuntime.Descriptors;
            lock (descriptors.SamplerLifetimeLock)
            {
                if (descriptors.DescriptorHeapSamplerCreateInfos.TryGetValue(sampler.Handle, out createInfo))
                    return true;
            }
        }

        createInfo = default;
        return false;
    }

    private void DestroyRemainingTrackedSamplers()
    {
        ulong[] handles;
        VulkanDescriptorManager descriptors = ResourceRuntime.Descriptors;
        lock (descriptors.SamplerLifetimeLock)
        {
            if (descriptors.LiveSamplerHandles.Count == 0)
                return;

            handles = [.. descriptors.LiveSamplerHandles];
            descriptors.LiveSamplerHandles.Clear();
            descriptors.DescriptorHeapSamplerCreateInfos.Clear();
        }

        for (int i = 0; i < handles.Length; i++)
        {
            Sampler sampler = new() { Handle = handles[i] };
            Debug.Vulkan(
                "[Vulkan] Destroying remaining tracked sampler 0x{0:X} during renderer shutdown.",
                handles[i]);
            Api!.DestroySampler(_deviceContext.Device, sampler, null);
            CompleteVulkanResourceDestruction(ObjectType.Sampler, handles[i]);
        }

        Debug.Vulkan(
            "[Vulkan] Destroyed {0} remaining tracked sampler(s) during renderer shutdown.",
            handles.Length);
    }
}
