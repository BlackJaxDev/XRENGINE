using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns sampler registration, deferred retirement, and shutdown destruction for one
/// Vulkan resource lifetime. The frame loop publishes the active slot explicitly;
/// backend wrappers never reach back into the renderer to retire a sampler.
/// </summary>
internal sealed class VulkanSamplerResourceService(
    VulkanResourceRuntime resources,
    VulkanDescriptorManager descriptors,
    VulkanLifetimeAuthority lifetime)
{
    private int _frameSlot;
    private VulkanCommandRuntime? _commandRuntime;

    internal void ConfigureCommandRuntime(VulkanCommandRuntime commandRuntime)
    {
        ArgumentNullException.ThrowIfNull(commandRuntime);
        if (_commandRuntime is not null && !ReferenceEquals(_commandRuntime, commandRuntime))
            throw new InvalidOperationException(
                "The Vulkan sampler resource service cannot be rebound to a different command runtime.");

        _commandRuntime = commandRuntime;
    }

    internal void PublishFrameSlot(int frameSlot)
    {
        if ((uint)frameSlot >= (uint)lifetime.Retirement.Images.Length)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));

        Volatile.Write(ref _frameSlot, frameSlot);
    }

    internal void Register(Sampler sampler, in SamplerCreateInfo createInfo, string owner)
    {
        if (sampler.Handle == 0)
            return;

        descriptors.RegisterLiveSampler(sampler, in createInfo);
        lifetime.Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.Sampler, sampler.Handle),
            owner,
            externallyOwned: false);
    }

    internal void Retire(Sampler sampler, string owner)
    {
        if (sampler.Handle == 0)
            return;

        VulkanResourceLifetimeKey key = new(ObjectType.Sampler, sampler.Handle);
        CommandRuntime.PublishTrackingDependenciesBeforeResourceRetirement(key);
        VulkanRetirementTicket ticket = resources.CaptureRetirementTicket(key, owner);
        int frameSlot = Volatile.Read(ref _frameSlot);
        lock (lifetime.Retirement.SyncRoot)
        {
            if (!lifetime.Retirement.AllSamplerHandles.Add(sampler.Handle))
                return;

            lifetime.Retirement.SamplerHandles[frameSlot].Add(sampler.Handle);
            lifetime.Retirement.Images[frameSlot].Add(new RetiredImageResourceEntry(
                new RetiredImageResources(default, default, default, [], sampler, 0),
                ticket,
                0,
                0,
                [],
                ticket.ResourceGeneration));
        }
    }

    internal unsafe int DestroyRemaining(Vk api, Device device)
    {
        ulong[] handles = descriptors.TakeLiveSamplerHandles();
        for (int index = 0; index < handles.Length; index++)
        {
            Sampler sampler = new() { Handle = handles[index] };
            api.DestroySampler(device, sampler, null);
            CompleteDestruction(sampler);
        }

        return handles.Length;
    }

    private void CompleteDestruction(Sampler sampler)
        => resources.CompleteResourceDestruction(ObjectType.Sampler, sampler.Handle);

    private VulkanCommandRuntime CommandRuntime
        => _commandRuntime ?? throw new InvalidOperationException(
            "The Vulkan sampler resource service has not been configured with the command runtime.");
}
