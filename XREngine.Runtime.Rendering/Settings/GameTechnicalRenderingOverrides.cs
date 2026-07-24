using MemoryPack;
using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine;

/// <summary>
/// Defines project overrides for technical rendering behavior.
/// </summary>
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
