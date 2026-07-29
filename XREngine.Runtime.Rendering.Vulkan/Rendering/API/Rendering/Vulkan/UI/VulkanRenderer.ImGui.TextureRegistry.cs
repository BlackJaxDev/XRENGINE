using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private DescriptorSet ResolveImGuiDescriptorSet(nint textureId)
    {
        if (textureId == 0)
            return _imguiResources.FontDescriptorSet;

        if (!RefreshImGuiRegisteredTexture(textureId))
            return _imguiResources.FontDescriptorSet;

        if (_imguiTextureRegistry.DescriptorSets.TryGetValue(textureId, out DescriptorSet set) && set.Handle != 0)
            return set;

        return _imguiResources.FontDescriptorSet;
    }

    private bool RefreshImGuiRegisteredTexture(nint textureId)
    {
        if (!_imguiTextureRegistry.TexturesById.TryGetValue(textureId, out XRTexture? texture))
            return false;

        if (!_imguiTextureRegistry.Registrations.TryGetValue(texture, out VulkanImGuiTextureRegistration registration) ||
            registration.Id != textureId)
            return false;

        if (!_imguiTextureRegistry.DescriptorSets.TryGetValue(textureId, out DescriptorSet descriptorSet) ||
            descriptorSet.Handle == 0)
            return false;

        if (!TryResolveImGuiDescriptorBinding(
                texture,
                out ImageView descriptorView,
                out Sampler descriptorSampler,
                out ImageLayout descriptorLayout,
                out ulong descriptorGeneration))
            return false;

        if (registration.ImageViewHandle == descriptorView.Handle &&
            registration.SamplerHandle == descriptorSampler.Handle &&
            registration.ImageLayout == descriptorLayout &&
            registration.DescriptorGeneration == descriptorGeneration)
        {
            return true;
        }

        DescriptorSet replacementSet = AllocateImGuiDescriptorSet(descriptorView, descriptorSampler, descriptorLayout);
        if (replacementSet.Handle == 0)
            return false;

        _imguiTextureRegistry.DescriptorSets[textureId] = replacementSet;
        UpdateImGuiDescriptorHeapPayload(textureId, new DescriptorImageInfo
        {
            Sampler = descriptorSampler,
            ImageView = descriptorView,
            ImageLayout = descriptorLayout,
        });
        registration.DescriptorSet = replacementSet;
        registration.ImageViewHandle = descriptorView.Handle;
        registration.SamplerHandle = descriptorSampler.Handle;
        registration.ImageLayout = descriptorLayout;
        registration.DescriptorGeneration = descriptorGeneration;
        _imguiTextureRegistry.Registrations[texture] = registration;
        RetireDescriptorSet(_imguiResources.DescriptorPool, descriptorSet);
        return true;
    }

    public IntPtr RegisterImGuiTexture(XRTexture texture)
    {
        if (texture is null)
            return IntPtr.Zero;

        EnsureImGuiFontResources();

        if (!TryResolveImGuiDescriptorBinding(
                texture,
                out ImageView descriptorView,
                out Sampler descriptorSampler,
                out ImageLayout descriptorLayout,
                out ulong descriptorGeneration))
            return IntPtr.Zero;

        if (_imguiTextureRegistry.Registrations.TryGetValue(texture, out VulkanImGuiTextureRegistration registration))
        {
            if (!_imguiTextureRegistry.DescriptorSets.TryGetValue(registration.Id, out DescriptorSet liveDescriptorSet)
                || liveDescriptorSet.Handle == 0)
            {
                _imguiTextureRegistry.Registrations.Remove(texture);
                _imguiTextureRegistry.DescriptorSets.Remove(registration.Id);
                _imguiTextureRegistry.TexturesById.Remove(registration.Id);
            }
            else
            {
                registration.DescriptorSet = liveDescriptorSet;
                _imguiTextureRegistry.TexturesById[registration.Id] = texture;
                if (registration.ImageViewHandle != descriptorView.Handle
                    || registration.SamplerHandle != descriptorSampler.Handle
                    || registration.ImageLayout != descriptorLayout
                    || registration.DescriptorGeneration != descriptorGeneration)
                {
                    DescriptorSet replacementSet = AllocateImGuiDescriptorSet(descriptorView, descriptorSampler, descriptorLayout);
                    if (replacementSet.Handle == 0)
                        return (IntPtr)registration.Id;

                    _imguiTextureRegistry.DescriptorSets[registration.Id] = replacementSet;
                    UpdateImGuiDescriptorHeapPayload(registration.Id, new DescriptorImageInfo
                    {
                        Sampler = descriptorSampler,
                        ImageView = descriptorView,
                        ImageLayout = descriptorLayout,
                    });
                    registration.DescriptorSet = replacementSet;
                    registration.ImageViewHandle = descriptorView.Handle;
                    registration.SamplerHandle = descriptorSampler.Handle;
                    registration.ImageLayout = descriptorLayout;
                    registration.DescriptorGeneration = descriptorGeneration;
                    _imguiTextureRegistry.Registrations[texture] = registration;
                    RetireDescriptorSet(_imguiResources.DescriptorPool, liveDescriptorSet);
                }

                return (IntPtr)registration.Id;
            }
        }

        DescriptorSet descriptorSet = AllocateImGuiDescriptorSet(descriptorView, descriptorSampler, descriptorLayout);
        if (descriptorSet.Handle == 0)
            return IntPtr.Zero;

        nint id = _imguiTextureRegistry.NextTextureId++;
        _imguiTextureRegistry.DescriptorSets[id] = descriptorSet;
        UpdateImGuiDescriptorHeapPayload(id, new DescriptorImageInfo
        {
            Sampler = descriptorSampler,
            ImageView = descriptorView,
            ImageLayout = descriptorLayout,
        });
        _imguiTextureRegistry.TexturesById[id] = texture;
        _imguiTextureRegistry.Registrations[texture] = new VulkanImGuiTextureRegistration
        {
            Id = id,
            DescriptorSet = descriptorSet,
            ImageViewHandle = descriptorView.Handle,
            SamplerHandle = descriptorSampler.Handle,
            ImageLayout = descriptorLayout,
            DescriptorGeneration = descriptorGeneration,
        };
        return (IntPtr)id;
    }

    public bool UnregisterImGuiTexture(IntPtr textureId)
    {
        nint id = textureId;
        if (id <= 1)
            return false;

        if (!_imguiTextureRegistry.DescriptorSets.TryGetValue(id, out DescriptorSet descriptorSet))
            return false;

        _imguiTextureRegistry.DescriptorSets.Remove(id);
        _imguiTextureRegistry.DescriptorHeapPushData.Remove(id);
        _imguiTextureRegistry.TexturesById.Remove(id);

        XRTexture? keyToRemove = null;
        foreach (var entry in _imguiTextureRegistry.Registrations)
        {
            if (entry.Value.Id == id)
            {
                keyToRemove = entry.Key;
                break;
            }
        }

        if (keyToRemove is not null)
            _imguiTextureRegistry.Registrations.Remove(keyToRemove);

        if (descriptorSet.Handle != 0)
            RetireDescriptorSet(_imguiResources.DescriptorPool, descriptorSet);

        return true;
    }

    private bool TryResolveImGuiDescriptorBinding(
        XRTexture texture,
        out ImageView descriptorView,
        out Sampler descriptorSampler,
        out ImageLayout descriptorLayout,
        out ulong descriptorGeneration)
    {
        descriptorView = default;
        descriptorSampler = default;
        descriptorLayout = ImageLayout.ShaderReadOnlyOptimal;
        descriptorGeneration = 0;

        bool allowSynchronousTextureUpload = AllowSynchronousResourceUploads;
        if (GetOrCreateAPIRenderObject(texture, generateNow: allowSynchronousTextureUpload) is not IVkImageDescriptorSource source)
            return false;

        if (allowSynchronousTextureUpload)
            TryUploadImGuiTextureIfUninitialized(texture, ref source);
        else if (!source.IsDescriptorReady)
            return false;

        descriptorView = ResolveImGuiDescriptorView(source);
        descriptorSampler = source.DescriptorSampler;
        if (descriptorSampler.Handle != 0 && !IsLiveSampler(descriptorSampler))
            descriptorSampler = default;

        if (descriptorSampler.Handle == 0)
            descriptorSampler = GetPlaceholderSampler();

        descriptorLayout = ResolveDescriptorImageLayout(source, DescriptorType.CombinedImageSampler);
        descriptorGeneration = source.DescriptorGeneration;
        return descriptorView.Handle != 0 &&
            IsLiveImageViewBackedByLiveImage(descriptorView) &&
            IsImageViewAvailableForDescriptor(descriptorView) &&
            descriptorSampler.Handle != 0 &&
            IsLiveSampler(descriptorSampler);
    }

    private void TryUploadImGuiTextureIfUninitialized(XRTexture texture, ref IVkImageDescriptorSource source)
    {
        if (source.TrackedImageLayout != ImageLayout.Undefined)
            return;

        if (texture is not XRTexture2D { Mipmaps.Length: > 0 })
            return;

        texture.PushData();

        if (GetOrCreateAPIRenderObject(texture, generateNow: true) is IVkImageDescriptorSource refreshed)
            source = refreshed;
    }

    private static ImageView ResolveImGuiDescriptorView(IVkImageDescriptorSource source)
    {
        ImageView descriptorView = source.DescriptorView;
        if (!IsCombinedDepthStencilFormat(source.DescriptorFormat))
            return descriptorView;

        ImageAspectFlags descriptorAspect = source.DescriptorAspect;
        bool hasCombinedAspects = (descriptorAspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit))
            == (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit);
        if (!hasCombinedAspects)
            return descriptorView;

        ImageView depthOnlyView = source.GetDepthOnlyDescriptorView();
        return depthOnlyView.Handle != 0 ? depthOnlyView : descriptorView;
    }

    private DescriptorSet AllocateImGuiDescriptorSet(ImageView descriptorView, Sampler descriptorSampler, ImageLayout descriptorLayout)
    {
        if (_imguiResources.DescriptorPool.Handle == 0 || _imguiResources.DescriptorSetLayout.Handle == 0)
            return default;

        DescriptorSetLayout layout = _imguiResources.DescriptorSetLayout;
        DescriptorSetAllocateInfo allocInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _imguiResources.DescriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };

        if (Api!.AllocateDescriptorSets(device, ref allocInfo, out DescriptorSet descriptorSet) != Result.Success)
            return default;

        RegisterVulkanDescriptorSet(
            _imguiResources.DescriptorPool,
            descriptorSet,
            usesUpdateAfterBind: false,
            "ImGui.Texture.DescriptorSet");
        SetDebugDescriptorSetName(descriptorSet, $"ImGui.Texture.DescriptorSet.0x{descriptorView.Handle:X}");
        RecordVulkanDescriptorTableGeneration("ImGui.TextureDescriptorSet.Allocated");
        UpdateImGuiDescriptorSet(descriptorSet, descriptorView, descriptorSampler, descriptorLayout);
        return descriptorSet;
    }

    private void UpdateImGuiDescriptorSet(DescriptorSet descriptorSet, ImageView descriptorView, Sampler descriptorSampler, ImageLayout descriptorLayout)
    {
        DescriptorImageInfo imageInfo = new()
        {
            Sampler = descriptorSampler,
            ImageView = descriptorView,
            ImageLayout = descriptorLayout,
        };

        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageInfo,
        };

        UpdateDescriptorSetsTracked(1, &write);
        RecordVulkanDescriptorTableGeneration("ImGui.TextureDescriptorSet.Update");
    }

    private bool UpdateImGuiDescriptorHeapPayload(nint textureId, DescriptorImageInfo imageInfo)
    {
        if (!IsDescriptorHeapDrawBindingActive)
            return true;

        DescriptorHeapPushDataPayload payload = new(new uint[2]);
        if (!TryWriteDescriptorHeapCombinedImageSamplerPayload(imageInfo, payload, out string reason))
        {
            Debug.VulkanWarning("[Vulkan.ImGui] Failed to write descriptor heap payload for textureId={0}: {1}", textureId, reason);
            return false;
        }

        _imguiTextureRegistry.DescriptorHeapPushData[textureId] = payload;
        return true;
    }

    private DescriptorHeapPushDataPayload? ResolveImGuiDescriptorHeapPayload(nint textureId)
    {
        if (textureId != 0)
            RefreshImGuiRegisteredTexture(textureId);

        if (_imguiTextureRegistry.DescriptorHeapPushData.TryGetValue(textureId, out DescriptorHeapPushDataPayload? payload))
            return payload;

        if (_imguiTextureRegistry.DescriptorHeapPushData.TryGetValue((nint)1, out payload))
            return payload;

        return null;
    }

    internal void DestroySwapchainImGuiResources()
        => DestroyImGuiPipelineResources();
}
