using MemoryPack;
using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Rendering.Vulkan;

namespace XREngine;

/// <summary>
/// Defines project overrides for the Vulkan backend.
/// </summary>
[Serializable]
[MemoryPackable]
public partial class GameVulkanRenderingOverrides : OverrideableSettingsOwnerBase
{
    private OverrideableSetting<EVulkanGpuDrivenProfile> _gpuDrivenProfileOverride = new();
    private OverrideableSetting<EVulkanRenderTargetMode> _renderTargetModeOverride = new();
    private OverrideableSetting<EVulkanPresentationProfile> _presentationProfileOverride = new();
    private OverrideableSetting<float> _presentationTargetRefreshHzOverride = new();

    [Category("Vulkan Overrides")]
    [Description("Project override for the Vulkan GPU-driven runtime profile.")]
    public OverrideableSetting<EVulkanGpuDrivenProfile> GpuDrivenProfileOverride
    {
        get => _gpuDrivenProfileOverride;
        set => SetField(ref _gpuDrivenProfileOverride, value ?? new());
    }

    [Category("Vulkan Overrides")]
    [Description("Project override for the Vulkan dynamic-rendering target mode.")]
    public OverrideableSetting<EVulkanRenderTargetMode> RenderTargetModeOverride
    {
        get => _renderTargetModeOverride;
        set => SetField(ref _renderTargetModeOverride, value ?? new());
    }

    [Category("Vulkan Overrides")]
    [Description("Project override for the deliberate Vulkan desktop presentation profile.")]
    public OverrideableSetting<EVulkanPresentationProfile> PresentationProfileOverride
    {
        get => _presentationProfileOverride;
        set => SetField(ref _presentationProfileOverride, value ?? new());
    }

    [Category("Vulkan Overrides")]
    [Description("Project override for Vulkan presentation target cadence in hertz; zero selects automatic display cadence.")]
    public OverrideableSetting<float> PresentationTargetRefreshHzOverride
    {
        get => _presentationTargetRefreshHzOverride;
        set => SetField(ref _presentationTargetRefreshHzOverride, value ?? new());
    }
}
