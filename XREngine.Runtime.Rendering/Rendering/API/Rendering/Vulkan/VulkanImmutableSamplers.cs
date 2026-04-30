using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal enum VulkanCanonicalSampler
    {
        LinearClamp,
        NearestClamp,
        LinearRepeat,
        Anisotropic,
        ShadowComparison,
    }

    private readonly Sampler[] _canonicalImmutableSamplers = new Sampler[5];

    internal bool TryGetCanonicalImmutableSampler(VulkanCanonicalSampler sampler, out Sampler handle)
    {
        int index = (int)sampler;
        handle = index >= 0 && index < _canonicalImmutableSamplers.Length
            ? _canonicalImmutableSamplers[index]
            : default;
        return handle.Handle != 0;
    }

    private void InitializeCanonicalImmutableSamplers()
    {
        CreateCanonicalSampler(VulkanCanonicalSampler.LinearClamp, Filter.Linear, SamplerMipmapMode.Linear, SamplerAddressMode.ClampToEdge, false, false);
        CreateCanonicalSampler(VulkanCanonicalSampler.NearestClamp, Filter.Nearest, SamplerMipmapMode.Nearest, SamplerAddressMode.ClampToEdge, false, false);
        CreateCanonicalSampler(VulkanCanonicalSampler.LinearRepeat, Filter.Linear, SamplerMipmapMode.Linear, SamplerAddressMode.Repeat, false, false);
        CreateCanonicalSampler(VulkanCanonicalSampler.Anisotropic, Filter.Linear, SamplerMipmapMode.Linear, SamplerAddressMode.Repeat, _supportsAnisotropy, false);
        CreateCanonicalSampler(VulkanCanonicalSampler.ShadowComparison, Filter.Linear, SamplerMipmapMode.Linear, SamplerAddressMode.ClampToEdge, false, true);
    }

    private void CreateCanonicalSampler(
        VulkanCanonicalSampler sampler,
        Filter filter,
        SamplerMipmapMode mipmapMode,
        SamplerAddressMode addressMode,
        bool anisotropy,
        bool comparison)
    {
        int index = (int)sampler;
        if ((uint)index >= (uint)_canonicalImmutableSamplers.Length || _canonicalImmutableSamplers[index].Handle != 0)
            return;

        float maxAnisotropy = 1f;
        if (anisotropy)
        {
            Api!.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
            maxAnisotropy = MathF.Max(1f, MathF.Min(16f, properties.Limits.MaxSamplerAnisotropy));
        }

        SamplerCreateInfo info = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = mipmapMode,
            AddressModeU = addressMode,
            AddressModeV = addressMode,
            AddressModeW = addressMode,
            MipLodBias = 0f,
            AnisotropyEnable = anisotropy ? Vk.True : Vk.False,
            MaxAnisotropy = maxAnisotropy,
            CompareEnable = comparison ? Vk.True : Vk.False,
            CompareOp = comparison ? CompareOp.LessOrEqual : CompareOp.Always,
            MinLod = 0f,
            MaxLod = Vk.LodClampNone,
            BorderColor = BorderColor.FloatOpaqueWhite,
            UnnormalizedCoordinates = Vk.False,
        };

        if (Api!.CreateSampler(device, ref info, null, out Sampler handle) == Result.Success)
            _canonicalImmutableSamplers[index] = handle;
        else
            Debug.VulkanWarning($"[Vulkan] Failed to create canonical immutable sampler '{sampler}'.");
    }

    private void DestroyCanonicalImmutableSamplers()
    {
        for (int i = 0; i < _canonicalImmutableSamplers.Length; i++)
        {
            if (_canonicalImmutableSamplers[i].Handle == 0)
                continue;

            Api!.DestroySampler(device, _canonicalImmutableSamplers[i], null);
            _canonicalImmutableSamplers[i] = default;
        }
    }
}
