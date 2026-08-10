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

/// <summary>
/// Owns ImGui texture descriptor registration and refresh without retaining the renderer facade.
/// </summary>
internal sealed unsafe class VulkanImGuiTextureRegistryService
{
    private readonly VulkanImGuiResources _resourcesState;
    private readonly VulkanImGuiTextureRegistry _registry;
    private readonly VulkanResourceRuntime _resources;
    private readonly VulkanCommandRuntime _commands;
    private readonly VulkanDeviceContext _device;
    private readonly VulkanImGuiTextureOutputResources _textureOutput;
    private readonly VulkanImGuiFontAtlasResources _fontAtlas;

    internal VulkanImGuiTextureRegistryService(
        VulkanImGuiResources resourcesState,
        VulkanImGuiTextureRegistry registry,
        VulkanResourceRuntime resources,
        VulkanCommandRuntime commands,
        VulkanDeviceContext device,
        VulkanImGuiTextureOutputResources textureOutput,
        VulkanImGuiFontAtlasResources fontAtlas)
    {
        _resourcesState = resourcesState;
        _registry = registry;
        _resources = resources;
        _commands = commands;
        _device = device;
        _textureOutput = textureOutput;
        _fontAtlas = fontAtlas;
    }

    private VulkanBackendObjectContext BackendContext
        => _resources.BackendObjectContext
            ?? throw new InvalidOperationException("The Vulkan backend object context is not initialized.");

    private VulkanImGuiTextureOutputResources ImGuiTextureOutputResources
        => _textureOutput;

    private DescriptorSet ResolveImGuiDescriptorSet(nint textureId)
    {
        if (textureId == 0)
            return _resourcesState.FontDescriptorSet;

        if (!RefreshImGuiRegisteredTexture(textureId))
            return _resourcesState.FontDescriptorSet;

        if (_registry.DescriptorSets.TryGetValue(textureId, out DescriptorSet set) && set.Handle != 0)
            return set;

        return _resourcesState.FontDescriptorSet;
    }

    private bool RefreshImGuiRegisteredTexture(nint textureId)
    {
        if (!_registry.TexturesById.TryGetValue(textureId, out XRTexture? texture))
            return false;

        if (!_registry.Registrations.TryGetValue(texture, out VulkanImGuiTextureRegistration registration) ||
            registration.Id != textureId)
            return false;

        if (!_registry.DescriptorSets.TryGetValue(textureId, out DescriptorSet descriptorSet) ||
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

        _registry.DescriptorSets[textureId] = replacementSet;
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
        _registry.Registrations[texture] = registration;
        ImGuiTextureOutputResources.Retire(_resourcesState, descriptorSet);
        return true;
    }

    public IntPtr RegisterImGuiTexture(XRTexture texture)
    {
        if (texture is null)
            return IntPtr.Zero;

        _fontAtlas.EnsureCreated();

        if (!TryResolveImGuiDescriptorBinding(
                texture,
                out ImageView descriptorView,
                out Sampler descriptorSampler,
                out ImageLayout descriptorLayout,
                out ulong descriptorGeneration))
            return IntPtr.Zero;

        if (_registry.Registrations.TryGetValue(texture, out VulkanImGuiTextureRegistration registration))
        {
            if (!_registry.DescriptorSets.TryGetValue(registration.Id, out DescriptorSet liveDescriptorSet)
                || liveDescriptorSet.Handle == 0)
            {
                _registry.Registrations.Remove(texture);
                _registry.DescriptorSets.Remove(registration.Id);
                _registry.TexturesById.Remove(registration.Id);
            }
            else
            {
                registration.DescriptorSet = liveDescriptorSet;
                _registry.TexturesById[registration.Id] = texture;
                if (registration.ImageViewHandle != descriptorView.Handle
                    || registration.SamplerHandle != descriptorSampler.Handle
                    || registration.ImageLayout != descriptorLayout
                    || registration.DescriptorGeneration != descriptorGeneration)
                {
                    DescriptorSet replacementSet = AllocateImGuiDescriptorSet(descriptorView, descriptorSampler, descriptorLayout);
                    if (replacementSet.Handle == 0)
                        return (IntPtr)registration.Id;

                    _registry.DescriptorSets[registration.Id] = replacementSet;
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
                    _registry.Registrations[texture] = registration;
                    ImGuiTextureOutputResources.Retire(
                        _resourcesState,
                        liveDescriptorSet);
                }

                return (IntPtr)registration.Id;
            }
        }

        DescriptorSet descriptorSet = AllocateImGuiDescriptorSet(descriptorView, descriptorSampler, descriptorLayout);
        if (descriptorSet.Handle == 0)
            return IntPtr.Zero;

        nint id = _registry.NextTextureId++;
        _registry.DescriptorSets[id] = descriptorSet;
        UpdateImGuiDescriptorHeapPayload(id, new DescriptorImageInfo
        {
            Sampler = descriptorSampler,
            ImageView = descriptorView,
            ImageLayout = descriptorLayout,
        });
        _registry.TexturesById[id] = texture;
        _registry.Registrations[texture] = new VulkanImGuiTextureRegistration
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

        if (!_registry.DescriptorSets.TryGetValue(id, out DescriptorSet descriptorSet))
            return false;

        _registry.DescriptorSets.Remove(id);
        _registry.DescriptorHeapPushData.Remove(id);
        _registry.TexturesById.Remove(id);

        XRTexture? keyToRemove = null;
        foreach (var entry in _registry.Registrations)
        {
            if (entry.Value.Id == id)
            {
                keyToRemove = entry.Key;
                break;
            }
        }

        if (keyToRemove is not null)
            _registry.Registrations.Remove(keyToRemove);

        if (descriptorSet.Handle != 0)
            ImGuiTextureOutputResources.Retire(
                _resourcesState,
                descriptorSet);

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

        bool allowSynchronousTextureUpload = BackendContext.Resources.AllowSynchronousResourceUploads;
        if (BackendContext.GetOrCreateAPIRenderObject(texture, generateNow: allowSynchronousTextureUpload) is not IVkImageDescriptorSource source)
            return false;

        if (allowSynchronousTextureUpload)
            TryUploadImGuiTextureIfUninitialized(texture, ref source);
        else if (!source.IsDescriptorReady)
            return false;

        descriptorView = ResolveImGuiDescriptorView(source);
        descriptorSampler = source.DescriptorSampler;
        if (descriptorSampler.Handle != 0 && !_resources.Descriptors.IsLiveSampler(descriptorSampler))
            descriptorSampler = default;

        if (descriptorSampler.Handle == 0)
            descriptorSampler = _resources.FallbackTexture.GetSampler();

        descriptorLayout = VulkanProgramUtilities.ResolveDescriptorImageLayout(source, DescriptorType.CombinedImageSampler);
        descriptorGeneration = source.DescriptorGeneration;
        return descriptorView.Handle != 0 &&
            _resources.Images.IsLiveBackedByLiveImage(descriptorView) &&
            _resources.Images.IsAvailableForDescriptor(descriptorView) &&
            descriptorSampler.Handle != 0 &&
            _resources.Descriptors.IsLiveSampler(descriptorSampler);
    }

    private void TryUploadImGuiTextureIfUninitialized(XRTexture texture, ref IVkImageDescriptorSource source)
    {
        if (source.TrackedImageLayout != ImageLayout.Undefined)
            return;

        if (texture is not XRTexture2D { Mipmaps.Length: > 0 })
            return;

        texture.PushData();

        if (BackendContext.GetOrCreateAPIRenderObject(texture, generateNow: true) is IVkImageDescriptorSource refreshed)
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
        DescriptorSet descriptorSet = ImGuiTextureOutputResources.AllocateAndWrite(
            _resourcesState,
            descriptorView,
            descriptorSampler,
            descriptorLayout);
        if (descriptorSet.Handle == 0)
            return default;

        _resources.DescriptorLifetime.SetDebugName(descriptorSet, $"ImGui.Texture.DescriptorSet.0x{descriptorView.Handle:X}");
        _resources.DescriptorLifetime.RecordTableGeneration();
        _resources.DescriptorLifetime.RecordTableGeneration();
        return descriptorSet;
    }

    private bool UpdateImGuiDescriptorHeapPayload(nint textureId, DescriptorImageInfo imageInfo)
    {
        if (_resources.Descriptors.Heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap ||
            !_resources.Descriptors.Heap.StorageReady)
            return true;

        DescriptorHeapPushDataPayload payload = new(new uint[2]);
        if (!_resources.DescriptorLifetime.TryWriteCombinedImageSamplerHeapPayload(imageInfo, payload, out string reason))
        {
            Debug.VulkanWarning("[Vulkan.ImGui] Failed to write descriptor heap payload for textureId={0}: {1}", textureId, reason);
            return false;
        }

        _registry.DescriptorHeapPushData[textureId] = payload;
        return true;
    }

    private DescriptorHeapPushDataPayload? ResolveImGuiDescriptorHeapPayload(nint textureId)
    {
        if (textureId != 0)
            RefreshImGuiRegisteredTexture(textureId);

        if (_registry.DescriptorHeapPushData.TryGetValue(textureId, out DescriptorHeapPushDataPayload? payload))
            return payload;

        if (_registry.DescriptorHeapPushData.TryGetValue((nint)1, out payload))
            return payload;

        return null;
    }

    private static bool IsCombinedDepthStencilFormat(Format format)
        => format is Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint;

}
