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
    internal bool SamplerAnisotropyEnabled => _deviceContext.Capabilities.Supports(EVulkanDeviceCapability.Anisotropy);
    internal IInputContext? ImGuiInputContext => XRWindow.Input;
    /// <summary>
    /// Compatibility facade for command-buffer orchestration while ownership
    /// remains centralized in <see cref="VulkanImGuiResources"/>.
    /// </summary>
    private CommandBuffer[]? _imguiOverlayCommandBuffers
    {
        get => _outputRuntime._imguiResources.OverlayCommandBuffers;
        set => _outputRuntime._imguiResources.OverlayCommandBuffers = value;
    }

    internal void StoreImGuiDrawData(ImDrawDataPtr drawData)
        => _outputRuntime._imguiDrawData.Store(drawData);

    private Result PresentImGuiViewport(ref PresentInfoKHR presentInfo)
    {
        using VulkanQueueOperationLease queueOperation =
            VulkanQueueOperationLease.TryEnter(_oneTimeSubmitLock, _deviceContext.StateMachine, _frameTelemetry);
        if (!queueOperation.Acquired)
            return Result.ErrorDeviceLost;

        Result result = OutputRuntime.Desktop.SwapchainExtension!.QueuePresent(_deviceContext.PresentQueue, ref presentInfo);
        RecordVulkanQueueOperation("present-imgui-viewport", _deviceContext.PresentQueue, result, 0, nameof(PresentImGuiViewport));
        if (result == Result.ErrorDeviceLost)
        {
            RecordFirstFailingVulkanApi($"vkQueuePresentKHR:{nameof(PresentImGuiViewport)}:{result}");
            MarkDeviceLost(
                $"QueuePresent returned ErrorDeviceLost in {nameof(PresentImGuiViewport)}",
                $"vkQueuePresentKHR.{nameof(PresentImGuiViewport)}",
                result);
        }

        return result;
    }

    private const uint ImGuiDescriptorPoolMaxSets = 256;

    protected override bool SupportsImGui => true;

    private VulkanImGuiBackend GetOrCreateImGuiBackend()
    {
        if (_outputRuntime.ConsumeImGuiFrameMarkerResetRequest())
            ResetImGuiFrameMarker();

        if (_outputRuntime._imguiBackend is not null && !ImGuiContextTracker.IsAlive(_outputRuntime._imguiBackend.ContextHandle))
        {
            _outputRuntime._imguiBackend.Dispose();
            _outputRuntime._imguiBackend = null;
            _outputRuntime._imguiDrawData.Clear();
        }

        return _outputRuntime._imguiBackend ??= new VulkanImGuiBackend(this);
    }

    protected override IImGuiRendererBackend? GetImGuiBackend(XRViewport? viewport)
        => SupportsImGui ? GetOrCreateImGuiBackend() : null;

    private void DisposeImGuiResources()
    {
        _outputRuntime._imguiBackend?.Dispose();
        _outputRuntime._imguiBackend = null;

        DestroyImGuiPipelineResources();
        DestroyImGuiFontResources();
        DestroyImGuiDrawBuffers();

        _outputRuntime._imguiDrawData.Clear();
        ResetImGuiFrameMarker();
    }

}
