using MemoryPack;
using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Rendering.Vulkan;

namespace XREngine;

[Serializable]
[MemoryPackable]
public partial class GameRenderingOverrides : XRBase
{
    private GameCommonRenderingOverrides _common = new();
    private GameOpenGLRenderingOverrides _openGL = new();
    private GameVulkanRenderingOverrides _vulkan = new();
    private GameQualityRenderingOverrides _quality = new();
    private GameTechnicalRenderingOverrides _technical = new();

    public GameRenderingOverrides()
        => AttachSubSettings(_common, _openGL, _vulkan, _quality, _technical);

    public GameCommonRenderingOverrides Common
    {
        get => _common;
        set => SetField(ref _common, value ?? new GameCommonRenderingOverrides());
    }

    public GameOpenGLRenderingOverrides OpenGL
    {
        get => _openGL;
        set => SetField(ref _openGL, value ?? new GameOpenGLRenderingOverrides());
    }

    public GameVulkanRenderingOverrides Vulkan
    {
        get => _vulkan;
        set => SetField(ref _vulkan, value ?? new GameVulkanRenderingOverrides());
    }

    public GameQualityRenderingOverrides Quality
    {
        get => _quality;
        set => SetField(ref _quality, value ?? new GameQualityRenderingOverrides());
    }

    public GameTechnicalRenderingOverrides Technical
    {
        get => _technical;
        set => SetField(ref _technical, value ?? new GameTechnicalRenderingOverrides());
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);

        if (propName == nameof(Common)
            || propName == nameof(OpenGL)
            || propName == nameof(Vulkan)
            || propName == nameof(Quality)
            || propName == nameof(Technical))
        {
            RefreshSubSettings(prev, field, HandleSubSettingsChanged);
        }
    }

    private void AttachSubSettings(params IXRNotifyPropertyChanged?[] settings)
    {
        for (int i = 0; i < settings.Length; i++)
        {
            if (settings[i] is not null)
                settings[i]!.PropertyChanged += HandleSubSettingsChanged;
        }
    }

    private static void RefreshSubSettings<T>(T previous, T current, XRPropertyChangedEventHandler handler)
    {
        if (previous is IXRNotifyPropertyChanged previousNotify)
            previousNotify.PropertyChanged -= handler;

        if (current is IXRNotifyPropertyChanged currentNotify)
            currentNotify.PropertyChanged += handler;
    }

    private void HandleSubSettingsChanged(object? sender, IXRPropertyChangedEventArgs e)
        => OnPropertyChanged(e.PropertyName, e.PreviousValue, e.NewValue);
}

[Serializable]
[MemoryPackable]
public partial class GameCommonRenderingOverrides : OverrideableSettingsOwnerBase
{
    private OverrideableSetting<RenderBackendFallbackPolicy> _renderBackendFallbackPolicyOverride = new();

    [Category("Rendering Overrides")]
    [Description("Project override for render backend fallback behavior during startup.")]
    public OverrideableSetting<RenderBackendFallbackPolicy> RenderBackendFallbackPolicyOverride
    {
        get => _renderBackendFallbackPolicyOverride;
        set => SetField(ref _renderBackendFallbackPolicyOverride, value ?? new());
    }
}

[Serializable]
[MemoryPackable]
public partial class GameOpenGLRenderingOverrides : OverrideableSettingsOwnerBase
{
    private OverrideableSetting<bool> _allowProgramPipelinesOverride = new();
    private OverrideableSetting<bool> _useDetailPreservingComputeMipmapsOverride = new();

    [Category("OpenGL Overrides")]
    [Description("Project override for OpenGL program pipelines.")]
    public OverrideableSetting<bool> AllowProgramPipelinesOverride
    {
        get => _allowProgramPipelinesOverride;
        set => SetField(ref _allowProgramPipelinesOverride, value ?? new());
    }

    [Category("OpenGL Overrides")]
    [Description("Project override for detail-preserving OpenGL compute mipmap generation.")]
    public OverrideableSetting<bool> UseDetailPreservingComputeMipmapsOverride
    {
        get => _useDetailPreservingComputeMipmapsOverride;
        set => SetField(ref _useDetailPreservingComputeMipmapsOverride, value ?? new());
    }
}

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

[Serializable]
[MemoryPackable]
public partial class GameQualityRenderingOverrides : OverrideableSettingsOwnerBase
{
}

[Serializable]
[MemoryPackable]
public partial class GameTechnicalRenderingOverrides : OverrideableSettingsOwnerBase
{
    private OverrideableSetting<bool> _allowSkinningOverride = new();
    private OverrideableSetting<bool> _useIntegerWeightingIdsOverride = new();
    private OverrideableSetting<ELoopType> _recalcChildMatricesLoopTypeOverride = new();
    private OverrideableSetting<ESkinnedBoundsRecomputePolicy> _skinnedBoundsRecomputePolicyOverride = new();
    private OverrideableSetting<bool> _allowInitialSkinnedBoundsBuildWhenNeverOverride = new();
    private OverrideableSetting<bool> _calculateSkinningInComputeShaderOverride = new();
    private OverrideableSetting<bool> _calculateBlendshapesInComputeShaderOverride = new();

    [Category("Technical Overrides")]
    public OverrideableSetting<bool> AllowSkinningOverride
    {
        get => _allowSkinningOverride;
        set => SetField(ref _allowSkinningOverride, value ?? new());
    }

    [Category("Technical Overrides")]
    public OverrideableSetting<bool> UseIntegerWeightingIdsOverride
    {
        get => _useIntegerWeightingIdsOverride;
        set => SetField(ref _useIntegerWeightingIdsOverride, value ?? new());
    }

    [Category("Technical Overrides")]
    public OverrideableSetting<ELoopType> RecalcChildMatricesLoopTypeOverride
    {
        get => _recalcChildMatricesLoopTypeOverride;
        set => SetField(ref _recalcChildMatricesLoopTypeOverride, value ?? new());
    }

    [Category("Technical Overrides")]
    public OverrideableSetting<ESkinnedBoundsRecomputePolicy> SkinnedBoundsRecomputePolicyOverride
    {
        get => _skinnedBoundsRecomputePolicyOverride;
        set => SetField(ref _skinnedBoundsRecomputePolicyOverride, value ?? new());
    }

    [Category("Technical Overrides")]
    public OverrideableSetting<bool> AllowInitialSkinnedBoundsBuildWhenNeverOverride
    {
        get => _allowInitialSkinnedBoundsBuildWhenNeverOverride;
        set => SetField(ref _allowInitialSkinnedBoundsBuildWhenNeverOverride, value ?? new());
    }

    [Category("Technical Overrides")]
    public OverrideableSetting<bool> CalculateSkinningInComputeShaderOverride
    {
        get => _calculateSkinningInComputeShaderOverride;
        set => SetField(ref _calculateSkinningInComputeShaderOverride, value ?? new());
    }

    [Category("Technical Overrides")]
    public OverrideableSetting<bool> CalculateBlendshapesInComputeShaderOverride
    {
        get => _calculateBlendshapesInComputeShaderOverride;
        set => SetField(ref _calculateBlendshapesInComputeShaderOverride, value ?? new());
    }
}
