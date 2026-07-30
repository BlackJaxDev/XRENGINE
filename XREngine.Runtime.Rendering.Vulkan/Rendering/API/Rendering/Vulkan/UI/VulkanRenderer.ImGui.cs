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
    internal bool SamplerAnisotropyEnabled => DeviceCapabilities.Supports(EVulkanDeviceCapability.Anisotropy);
    internal IInputContext? ImGuiInputContext => XRWindow.Input;
    private VulkanImGuiBackend? _imguiBackend;
    private readonly VulkanImGuiDrawDataCache _imguiDrawData = new();
    private readonly VulkanImGuiResources _imguiResources = new();
    private readonly VulkanImGuiTextureRegistry _imguiTextureRegistry = new();

    /// <summary>
    /// Compatibility facade for command-buffer orchestration while ownership
    /// remains centralized in <see cref="VulkanImGuiResources"/>.
    /// </summary>
    private CommandBuffer[]? _imguiOverlayCommandBuffers
    {
        get => _imguiResources.OverlayCommandBuffers;
        set => _imguiResources.OverlayCommandBuffers = value;
    }

    internal void StoreImGuiDrawData(ImDrawDataPtr drawData)
        => _imguiDrawData.Store(drawData);

    private const uint ImGuiDescriptorPoolMaxSets = 256;

    protected override bool SupportsImGui => true;

    private VulkanImGuiBackend GetOrCreateImGuiBackend()
    {
        if (_imguiBackend is not null && !ImGuiContextTracker.IsAlive(_imguiBackend.ContextHandle))
        {
            _imguiBackend.Dispose();
            _imguiBackend = null;
            _imguiDrawData.Clear();
        }

        return _imguiBackend ??= new VulkanImGuiBackend(this);
    }

    protected override IImGuiRendererBackend? GetImGuiBackend(XRViewport? viewport)
        => SupportsImGui ? GetOrCreateImGuiBackend() : null;

    private void DisposeImGuiResources()
    {
        DestroyImGuiPipelineResources();
        DestroyImGuiFontResources();
        DestroyImGuiDrawBuffers();

        _imguiBackend?.Dispose();
        _imguiBackend = null;
        _imguiDrawData.Clear();
        ResetImGuiFrameMarker();
    }

}
