using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal static unsafe class VulkanCanonicalImmutableSamplerService
{
    internal static void Initialize(VulkanResourceRuntime resources, Vk api, VulkanDeviceContext deviceContext)
    {
        Create(resources, api, deviceContext, VulkanCanonicalSampler.LinearClamp, Filter.Linear, SamplerMipmapMode.Linear, SamplerAddressMode.ClampToEdge, false, false);
        Create(resources, api, deviceContext, VulkanCanonicalSampler.NearestClamp, Filter.Nearest, SamplerMipmapMode.Nearest, SamplerAddressMode.ClampToEdge, false, false);
        Create(resources, api, deviceContext, VulkanCanonicalSampler.LinearRepeat, Filter.Linear, SamplerMipmapMode.Linear, SamplerAddressMode.Repeat, false, false);
        Create(resources, api, deviceContext, VulkanCanonicalSampler.Anisotropic, Filter.Linear, SamplerMipmapMode.Linear, SamplerAddressMode.Repeat, deviceContext.Capabilities.Supports(EVulkanDeviceCapability.Anisotropy), false);
        Create(resources, api, deviceContext, VulkanCanonicalSampler.ShadowComparison, Filter.Linear, SamplerMipmapMode.Linear, SamplerAddressMode.ClampToEdge, false, true);
    }

    internal static void Destroy(VulkanResourceRuntime resources, Vk api, Device device)
    {
        Sampler[] samplers = resources.Descriptors.CanonicalImmutableSamplers;
        for (int index = 0; index < samplers.Length; index++)
        {
            Sampler sampler = samplers[index];
            if (sampler.Handle == 0)
                continue;

            resources.Descriptors.UnregisterLiveSampler(sampler);
            api.DestroySampler(device, sampler, null);
            resources.CompleteResourceDestruction(ObjectType.Sampler, sampler.Handle);
            samplers[index] = default;
        }
    }

    private static void Create(VulkanResourceRuntime resources, Vk api, VulkanDeviceContext context, VulkanCanonicalSampler sampler, Filter filter, SamplerMipmapMode mipmapMode, SamplerAddressMode addressMode, bool anisotropy, bool comparison)
    {
        int index = (int)sampler;
        Sampler[] samplers = resources.Descriptors.CanonicalImmutableSamplers;
        if ((uint)index >= (uint)samplers.Length || samplers[index].Handle != 0)
            return;
        float maxAnisotropy = 1f;
        if (anisotropy)
        {
            api.GetPhysicalDeviceProperties(context.PhysicalDevice, out PhysicalDeviceProperties properties);
            maxAnisotropy = MathF.Max(1f, MathF.Min(16f, properties.Limits.MaxSamplerAnisotropy));
        }
        SamplerCreateInfo info = new()
        {
            SType = StructureType.SamplerCreateInfo, MagFilter = filter, MinFilter = filter, MipmapMode = mipmapMode,
            AddressModeU = addressMode, AddressModeV = addressMode, AddressModeW = addressMode,
            AnisotropyEnable = anisotropy ? Vk.True : Vk.False, MaxAnisotropy = maxAnisotropy,
            CompareEnable = comparison ? Vk.True : Vk.False, CompareOp = comparison ? CompareOp.LessOrEqual : CompareOp.Always,
            MaxLod = Vk.LodClampNone, BorderColor = BorderColor.FloatOpaqueWhite,
        };
        if (api.CreateSampler(context.Device, ref info, null, out Sampler handle) != Result.Success)
        {
            Debug.VulkanWarning($"[Vulkan] Failed to create canonical immutable sampler '{sampler}'.");
            return;
        }
        samplers[index] = handle;
        resources.Samplers.Register(handle, in info, "CanonicalImmutableSampler");
    }
}
