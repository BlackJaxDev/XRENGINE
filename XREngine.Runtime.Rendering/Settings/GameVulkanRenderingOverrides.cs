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
}
