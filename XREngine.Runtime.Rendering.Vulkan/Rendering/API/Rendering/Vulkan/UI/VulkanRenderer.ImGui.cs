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
    /// <summary>
    /// Compatibility facade for command-buffer orchestration while ownership
    /// remains centralized in <see cref="VulkanImGuiResources"/>.
    /// </summary>
    private CommandBuffer[]? _imguiOverlayCommandBuffers
    {
        get => _outputRuntime._imguiResources.OverlayCommandBuffers;
        set => _outputRuntime._imguiResources.OverlayCommandBuffers = value;
    }

    private const uint ImGuiDescriptorPoolMaxSets = 256;

    protected override bool SupportsImGui => true;

    protected override IImGuiRendererBackend? GetImGuiBackend(XRViewport? viewport)
    {
        if (!SupportsImGui)
            return null;

        if (_outputRuntime.ConsumeImGuiFrameMarkerResetRequest())
            ResetImGuiFrameMarker();
        return _outputRuntime.GetOrCreateImGuiBackend(new VulkanImGuiServices(
            XRWindow,
            _outputRuntime,
            _deviceContext,
            _commandRuntime,
            ResourceRuntime,
            _frameTelemetry));
    }

}
